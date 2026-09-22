using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;
using UnityModManagerNet;

namespace DVSeasons.Mod
{
    // Optional public boundary for DVSurvival. Seasons owns controls/save/MP;
    // consumers only sample the confirmed heat setting of the exact car.
    public static class CabHeating
    {
        internal static CabHeaterService Service;
        public static bool IsActive { get { return Service != null && Service.Active; } }
        public static bool Ensure(TrainCar car) { return IsActive && Service.Ensure(car); }
        public static float GetLevel(TrainCar car) { return IsActive ? Service.GetLevel(car) : 0; }
        public static bool UsesEngineHeating(TrainCar car) { return IsActive && Service.UsesEngineHeating(car); }
        public static float GetLevelById(string id) { return IsActive ? Service.GetLevelById(id) : 0; }
        public static float GetCabinTemperature(TrainCar car)
        { return IsActive ? Service.GetCabinTemperature(car) : float.NaN; }
        internal static WindowWinterClimate CustomClimate(TrainCar car)
        { return IsActive ? Service.CustomClimate(car) : null; }
        internal static bool UsesNetworkClimate { get { return IsActive && Service.UsesNetworkClimate; } }
        internal static WindowWinterClimate NetworkClimate(TrainCar car)
        { return IsActive ? Service.NetworkClimate(car) : null; }
        internal static void RestoreCustomClimates(List<CabFrostState> records)
        { if (IsActive) Service.RestoreCustomClimates(records); }
        internal static void SaveCustomClimates(IDictionary<string, WindowClimateState> destination)
        { if (IsActive) Service.SaveCustomClimates(destination); }
    }

    internal sealed class CabHeaterService : IDisposable
    {
        [Serializable] private sealed class Record { public string CarId; public float Level; public bool IsOn; }
        [Serializable] private sealed class Document { public int Version = 2; public List<Record> CabHeaters = new List<Record>(); public List<Dm1uFanState> Dm1uFans = new List<Dm1uFanState>(); }
        private const string SaveKey = "DVSeasons.CabHeaters";
        private readonly ISeasonNetworkBridge network;
        private readonly ICabHeaterNetworkBridge heaterNetwork;
        private readonly CabHeaterSwitchSystem switches;
        private readonly Dm1uCabControls dm1u;
        private readonly CabEngineHeating engineHeating = new CabEngineHeating();
        private readonly Dictionary<string,WindowWinterClimate> networkClimates = new Dictionary<string,WindowWinterClimate>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> climateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> expiredClimates = new List<string>();
        internal bool UsesNetworkClimate => network.IsSessionActive && !network.IsAuthority;
        public bool EngineHeatingWithoutSwitch = true;
        public float OutsideTemperature, SnowCoverage;
        private readonly Dictionary<string,float> states = new Dictionary<string,float>(StringComparer.OrdinalIgnoreCase);
        private float nextScan;
        private bool receivedSnapshot;
        public bool Active { get; private set; }
        public CabHeaterService(ISeasonNetworkBridge network)
        {
            this.network = network; heaterNetwork = network as ICabHeaterNetworkBridge;
            switches = new CabHeaterSwitchSystem(OnToggle);
            dm1u = new Dm1uCabControls(OnToggle);
            if (heaterNetwork != null)
            { heaterNetwork.HeaterChanged += OnConfirmed; heaterNetwork.HeatersReceived += OnSnapshot; }
        }
        public void StartSession(SaveGameData data)
        {
            EndSession(); Active = true; CabHeating.Service = this;
            if (!network.IsSessionActive || network.IsAuthority)
            {
                string json = data.GetString(SaveKey);
                if (string.IsNullOrEmpty(json)) json = data.GetString("DVSurvival.State");
                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        var document = Newtonsoft.Json.JsonConvert.DeserializeObject<Document>(json);
                        if (document != null) dm1u.RestoreFans(document.Dm1uFans);
                        if (document != null && document.CabHeaters != null)
                            foreach(var record in document.CabHeaters)
                            {
                                if (record == null || string.IsNullOrEmpty(record.CarId) || record.CarId.Length > 80) continue;
                                if (states.Count >= 1024) break;
                                states[record.CarId] = CabHeaterSetting.NormalizeSwitch(record.Level > 0 ? record.Level : record.IsOn ? 1 : 0);
                            }
                    }
                    catch (Exception e) { Debug.LogWarning("[DVSeasons] Heater save could not be restored: " + e.Message); }
                }
                foreach(var pair in states) switches.ApplyConfirmed(pair.Key,pair.Value);
                if (heaterNetwork != null) heaterNetwork.SetHeaters(states,true);
            }
            else network.RequestState();
        }
        public void Tick(KeyBinding shortcut)
        {
            if (!Active) return;
            if (shortcut != null && Time.timeScale > 0 && Cursor.lockState == CursorLockMode.Locked &&
                (UnityModManager.UI.Instance == null || !UnityModManager.UI.Instance.Opened) && shortcut.Down()) ToggleCurrent();
            if (Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + .5f;
            engineHeating.PruneDestroyed();
            if (network.IsSessionActive && !network.IsAuthority && !receivedSnapshot) network.RequestState();
            foreach(var car in RailSnowGameSource.GetCars())
            {
                if (car != null && car.loadedInterior != null && CabHeaterSwitchSystem.IsSupported(car)) Ensure(car);
                else if (CabEngineHeating.IsCustomLocomotive(car)) CustomClimate(car);
            }
        }
        public bool Ensure(TrainCar car)
        {
            if (!Active || car == null) return false;
            float customLevel; bool engineSource;
            if (engineHeating.TryGetLevel(car, EngineHeatingWithoutSwitch, out customLevel, out engineSource)) return true;
            return car.carType == DV.ThingTypes.TrainCarType.LocoDM1U
                ? dm1u.Ensure(car, GetLevelById(car.CarGUID)) : switches.Ensure(car);
        }
        public float GetLevel(TrainCar car)
        {
            if (!Active || car == null) return 0;
            float level; bool engineSource;
            return engineHeating.TryGetLevel(car, EngineHeatingWithoutSwitch, out level, out engineSource)
                ? level : GetLevelById(car.CarGUID);
        }
        public bool UsesEngineHeating(TrainCar car)
        {
            float level; bool engineSource;
            return Active && engineHeating.TryGetLevel(car, EngineHeatingWithoutSwitch, out level, out engineSource) && engineSource;
        }
        internal WindowWinterClimate CustomClimate(TrainCar car)
        { return !Active ? null : UsesNetworkClimate ? NetworkClimate(car)
            : engineHeating.GetClimate(car, EngineHeatingWithoutSwitch, OutsideTemperature, SnowCoverage); }
        internal WindowWinterClimate NetworkClimate(TrainCar car)
        {
            WindowWinterClimate climate;
            return car != null && !string.IsNullOrEmpty(car.CarGUID) && networkClimates.TryGetValue(car.CarGUID, out climate) ? climate : null;
        }
        internal void ApplyThermalNetworkState(VehicleThermalNetworkState[] snapshot)
        {
            if (!UsesNetworkClimate || !VehicleThermalNetworkState.IsValid(snapshot)) return;
            climateIds.Clear();
            foreach (var state in snapshot)
            {
                if (!state.HasClimate) continue;
                climateIds.Add(state.CarId);
                WindowWinterClimate climate;
                if (!networkClimates.TryGetValue(state.CarId, out climate))
                    networkClimates.Add(state.CarId, climate = new WindowWinterClimate());
                climate.Restore(state.ClimateState());
            }
            expiredClimates.Clear();
            foreach (var id in networkClimates.Keys) if (!climateIds.Contains(id)) expiredClimates.Add(id);
            foreach (var id in expiredClimates) networkClimates.Remove(id);
        }
        internal void RestoreCustomClimates(List<CabFrostState> records) { engineHeating.RestoreClimates(records); }
        internal void SaveCustomClimates(IDictionary<string, WindowClimateState> destination) { engineHeating.SaveClimates(destination); }
        public float GetCabinTemperature(TrainCar car)
        {
            var climate = CustomClimate(car);
            if (UsesNetworkClimate) return climate != null && climate.IsInitialized ? climate.CabinTemperature : float.NaN;
            return climate != null ? climate.CabinTemperature : WinterWindowController.CabinTemperature(car);
        }
        public float GetLevelById(string id) { return Active ? switches.GetLevel(id) : 0; }
        public void ToggleCurrent()
        {
            var car = PlayerManager.Car;
            if (!Active || !CabHeaterSwitchSystem.IsSupported(car) || !Ensure(car) || string.IsNullOrEmpty(car.CarGUID)) return;
            OnToggle(car.CarGUID, GetLevel(car) > 0 ? 0 : 1);
        }
        private void OnToggle(string id, float level)
        {
            if (!Active) return;
            if (network.IsSessionActive && !network.IsAuthority)
            {
                float previous; states.TryGetValue(id,out previous); switches.ApplyConfirmed(id,previous); dm1u.ApplyHeater(id,previous);
                if (heaterNetwork != null) heaterNetwork.RequestHeaterChange(id,level);
            }
            else OnConfirmed(id,level);
        }
        private void OnConfirmed(string id,float level)
        {
            if (!Active || string.IsNullOrEmpty(id) || id.Length>80 || !CabHeaterSetting.IsAllowed(level)) return;
            if (!states.ContainsKey(id) && states.Count >= 1024) return;
            float previous;
            if (states.TryGetValue(id,out previous) && previous == level) return;
            states[id]=level; switches.ApplyConfirmed(id,level); dm1u.ApplyHeater(id,level);
            if (network.IsAuthority && heaterNetwork != null) heaterNetwork.SetHeaters(states,true);
        }
        private void OnSnapshot(Dictionary<string,float> snapshot)
        {
            if (!Active || network.IsAuthority || snapshot == null) return;
            receivedSnapshot = true;
            foreach(var pair in states) if(!snapshot.ContainsKey(pair.Key))
            { switches.ApplyConfirmed(pair.Key,0); dm1u.ApplyHeater(pair.Key,0); }
            states.Clear();
            foreach(var pair in snapshot) {states[pair.Key]=pair.Value; switches.ApplyConfirmed(pair.Key,pair.Value);dm1u.ApplyHeater(pair.Key,pair.Value);}
        }
        public void Save(SaveGameData data)
        {
            if (!Active || data == null || (network.IsSessionActive && !network.IsAuthority)) return;
            var document = new Document();
            document.Dm1uFans = dm1u.CaptureFans();
            foreach(var pair in states) document.CabHeaters.Add(new Record {CarId=pair.Key,Level=pair.Value,IsOn=pair.Value>0});
            data.SetString(SaveKey,Newtonsoft.Json.JsonConvert.SerializeObject(document));
        }
        public void EndSession()
        {
            Active=false; if(CabHeating.Service==this) CabHeating.Service=null;
            switches.Reset();dm1u.Reset();engineHeating.Clear();states.Clear();nextScan=0;
            networkClimates.Clear(); climateIds.Clear(); expiredClimates.Clear();
            receivedSnapshot=false;
            if(heaterNetwork!=null) heaterNetwork.SetHeaters(states,false);
        }
        public void Dispose()
        {
            EndSession();switches.Dispose();
            if(heaterNetwork!=null) {heaterNetwork.HeaterChanged-=OnConfirmed;heaterNetwork.HeatersReceived-=OnSnapshot;}
        }
    }
}
