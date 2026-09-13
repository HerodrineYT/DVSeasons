using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

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
        public static float GetLevelById(string id) { return IsActive ? Service.GetLevelById(id) : 0; }
        public static float GetCabinTemperature(TrainCar car)
        { return IsActive ? WinterWindowController.CabinTemperature(car) : float.NaN; }
    }

    internal sealed class CabHeaterService : IDisposable
    {
        [Serializable] private sealed class Record { public string CarId; public float Level; public bool IsOn; }
        [Serializable] private sealed class Document { public int Version = 1; public List<Record> CabHeaters = new List<Record>(); }
        private const string SaveKey = "DVSeasons.CabHeaters";
        private readonly ISeasonNetworkBridge network;
        private readonly ICabHeaterNetworkBridge heaterNetwork;
        private readonly CabHeaterSwitchSystem switches;
        private readonly Dictionary<string,float> states = new Dictionary<string,float>(StringComparer.OrdinalIgnoreCase);
        private float nextScan;
        private bool receivedSnapshot;
        public bool Active { get; private set; }
        public CabHeaterService(ISeasonNetworkBridge network)
        {
            this.network = network; heaterNetwork = network as ICabHeaterNetworkBridge;
            switches = new CabHeaterSwitchSystem(OnToggle);
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
                        var document = JsonUtility.FromJson<Document>(json);
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
        public void Tick()
        {
            if (!Active || Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + .5f;
            if (network.IsSessionActive && !network.IsAuthority && !receivedSnapshot) network.RequestState();
            foreach(var car in RailSnowGameSource.GetCars())
                if (car != null && car.loadedInterior != null && CabHeaterSwitchSystem.IsSupported(car)) Ensure(car);
        }
        public bool Ensure(TrainCar car) { return Active && switches.Ensure(car); }
        public float GetLevel(TrainCar car) { return Active ? switches.GetLevel(car) : 0; }
        public float GetLevelById(string id) { return Active ? switches.GetLevel(id) : 0; }
        private void OnToggle(string id, float level)
        {
            if (!Active) return;
            if (network.IsSessionActive && !network.IsAuthority)
            {
                float previous; states.TryGetValue(id,out previous); switches.ApplyConfirmed(id,previous);
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
            states[id]=level; switches.ApplyConfirmed(id,level);
            if (network.IsAuthority && heaterNetwork != null) heaterNetwork.SetHeaters(states,true);
        }
        private void OnSnapshot(Dictionary<string,float> snapshot)
        {
            if (!Active || network.IsAuthority || snapshot == null) return;
            receivedSnapshot = true;
            foreach(var pair in states) if(!snapshot.ContainsKey(pair.Key)) switches.ApplyConfirmed(pair.Key,0);
            states.Clear();
            foreach(var pair in snapshot) {states[pair.Key]=pair.Value; switches.ApplyConfirmed(pair.Key,pair.Value);}
        }
        public void Save(SaveGameData data)
        {
            if (!Active || data == null || (network.IsSessionActive && !network.IsAuthority)) return;
            var document = new Document();
            foreach(var pair in states) document.CabHeaters.Add(new Record {CarId=pair.Key,Level=pair.Value,IsOn=pair.Value>0});
            data.SetString(SaveKey,JsonUtility.ToJson(document));
        }
        public void EndSession()
        {
            Active=false; if(CabHeating.Service==this) CabHeating.Service=null;
            switches.Reset();states.Clear();nextScan=0;
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
