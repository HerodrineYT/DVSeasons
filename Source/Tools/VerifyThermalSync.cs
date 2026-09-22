using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using DV.ThingTypes;
using DVSeasons.Core;
using HarmonyLib;
using UnityEngine;

// Real game TrainCar and production services, with only car enumeration and
// network transport substituted. No host camera or glass renderer is created.
public static class VerifyThermalSync
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly mod;
    static List<TrainCar> cars = new List<TrainCar>();
    static readonly List<GameObject> roots = new List<GameObject>();
    static object Make(string name, params object[] args) => Activator.CreateInstance(mod.GetType("DVSeasons.Mod."+name,true), All, null, args, null);
    static object Get(object o,string f) => o.GetType().GetField(f,All).GetValue(o);
    static void Set(object o,string f,object v) => o.GetType().GetField(f,All).SetValue(o,v);
    static object Call(object o,string m,params object[] args) => o.GetType().GetMethod(m,All).Invoke(o,args);
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    static void Near(float a,float b,string message) { Check(!float.IsNaN(a) && Math.Abs(a-b)<.00001f,message+": "+a+" != "+b); }
    static bool Cars(ref List<TrainCar> __result) { __result=cars; return false; }
    sealed class Bridge : ISeasonNetworkBridge
    {
        public bool Host;
        public bool IsAvailable => true;
        public bool IsSessionActive => true;
        public bool IsAuthority => Host;
        public string Status => "fixture";
        public event Action<SeasonNetworkState> StateReceived { add {} remove {} }
        public void Initialize(string id) {} public void SetEnabled(bool enabled) {}
        public void Publish(SeasonNetworkState state,bool force) {} public void RequestState() {} public void Dispose() {}
    }
    static object Service(bool host)
    {
        var service=Make("CabHeaterService",new Bridge {Host=host});
        Set(service,"<Active>k__BackingField",true);
        mod.GetType("DVSeasons.Mod.CabHeating").GetField("Service",All).SetValue(null,service);
        return service;
    }
    static void Use(object service) => mod.GetType("DVSeasons.Mod.CabHeating").GetField("Service",All).SetValue(null,service);
    static TrainCar Car(string id)
    {
        var go=new GameObject(id);go.SetActive(false);roots.Add(go);
        var car=go.AddComponent<TrainCar>();car.carType=TrainCarType.LocoDiesel;
        Set(car,"_isLoco",(bool?)true);
        var logical=typeof(TrainCar).GetField("logicCar");
        var value=FormatterServices.GetUninitializedObject(logical.FieldType);
        Set(value,"carGuid",id);Set(value,"ID",id);logical.SetValue(car,value);
        return car;
    }
    static void Advance(object windows,SeasonState state,bool fleet)
    { Set(windows,"nextClimate",0f);Set(windows,"lastClimate",0f);Call(windows,"UpdateClimate",state,fleet); }
    public static void Run(string path)
    {
        mod=Assembly.LoadFrom(Path.Combine(path,"DVSeasons.dll"));
        var harmony=new Harmony("DVSeasons.VerifyThermalSync");
        harmony.Patch(mod.GetType("DVSeasons.Mod.RailSnowGameSource").GetMethod("GetCars",All),
            prefix:new HarmonyMethod(typeof(VerifyThermalSync).GetMethod("Cars",All)));
        object host=null,client=null,hostWindows=null,clientWindows=null,heat=null;
        try
        {
            var cold=new SeasonState(3,SeasonKind.Winter,SeasonKind.Spring,0,1,-20,.4f);
            var car=Car("remote-de6");cars.Add(car);
            host=Service(true);Call(host,"OnConfirmed",car.CarGUID,1f);
            hostWindows=Make("WinterWindowController",new object[]{null});
            for(int i=0;i<2500;i++) Advance(hostWindows,cold,true);
            var climate=(WindowWinterClimate)Call(hostWindows,"ClimateFor",car);
            Check(climate!=null && climate.IsInitialized,"Host ignored off-camera cab");
            Check(climate.CabinTemperature>-5 && climate.GlassTemperature>-10,"Remote heater never warmed the cab");
            var snap=new[]{VehicleThermalNetworkState.Capture(car.CarGUID,climate,.27f)};
            // Visual disable must retain the authoritative simulation and save history.
            Call(hostWindows,"Apply",cold,1f,1f,false);
            Check(ReferenceEquals(climate,Call(hostWindows,"ClimateFor",car)),"Visual disable discarded climate");
            Debug.Log("THERMAL_SYNC_HOST off-camera cab / windows disabled / heater inertia: passed");

            client=Service(false);Set(client,"OutsideTemperature",50f);Set(client,"SnowCoverage",0f);
            Call(client,"ApplyThermalNetworkState",(object)snap);
            clientWindows=Make("WinterWindowController",new object[]{null});
            Call(clientWindows,"EnsureCab",car);
            heat=Make("LocomotiveSnowHeatController");Call(heat,"SetNetworkAuthority",false);
            Call(heat,"ApplyNetworkState",(object)snap);
            for(int i=0;i<300;i++)
            {
                Advance(clientWindows,cold,true);
                Call(heat,"Update",1f,1f,1f);
                Near((float)Call(client,"GetCabinTemperature",car),snap[0].Cabin,"Client independently advanced climate");
                Near((float)Call(heat,"Melted",car),.27f,"Client independently advanced thaw");
            }
            var remote=(WindowWinterClimate)Call(clientWindows,"ClimateFor",car);
            Near(remote.GlassTemperature,snap[0].Glass,"Client glass differs");Near(remote.Frost,snap[0].Frost,"Client frost differs");
            Near(remote.Fog,snap[0].Fog,"Client fog differs");
            // Host applies a cooling/thaw update; references already held by panes
            // must change, not remain attached to a replaced climate object.
            snap[0].Cabin=-3;snap[0].Glass=1;snap[0].Frost=.55f;snap[0].Fog=.1f;snap[0].MeltedSnow=.7f;
            Call(client,"ApplyThermalNetworkState",(object)snap);Call(heat,"ApplyNetworkState",(object)snap);
            Near(remote.GlassTemperature,1,"Existing pane reference missed snapshot");
            Check(remote.Stage==WinterGlassStage.Thawing,"Client thaw stage stale");
            var replacement=Car(car.CarGUID);cars.Clear();cars.Add(replacement);
            Call(clientWindows,"EnsureCab",replacement);Advance(clientWindows,cold,true);
            Near((float)Call(client,"GetCabinTemperature",replacement),-3,"Streamed replacement lost temperature");
            Near((float)Call(heat,"Remaining",replacement),LocomotiveSnowHeat.VisibleRemaining(.7f),"Replacement lost melted snow");
            Debug.Log("THERMAL_SYNC_CLIENT climate / thaw / existing panes / streamed replacement / no drift: passed");

            Call(client,"ApplyThermalNetworkState",(object)VehicleThermalNetworkState.Empty);
            Call(heat,"ApplyNetworkState",(object)VehicleThermalNetworkState.Empty);Advance(clientWindows,cold,true);
            Check(float.IsNaN((float)Call(client,"GetCabinTemperature",replacement)),"Empty snapshot retained old cab");
            Check(!((WindowWinterClimate)Call(clientWindows,"ClimateFor",replacement)).IsInitialized,"Pane retained removed host state");
            Near((float)Call(heat,"Melted",replacement),0,"Empty snapshot retained thaw");
            Call(client,"EndSession");Check(((IDictionary)Get(client,"networkClimates")).Count==0,"Session leaked climate");
            Debug.Log("THERMAL_SYNC_RESET explicit empty / session cleanup: passed");
        }
        finally
        {
            if(hostWindows!=null)Call(hostWindows,"Dispose");if(clientWindows!=null)Call(clientWindows,"Dispose");
            if(client!=null)Call(client,"Dispose");if(host!=null){Use(host);Call(host,"Dispose");}
            foreach(var root in roots) UnityEngine.Object.DestroyImmediate(root);
            roots.Clear();cars.Clear();harmony.UnpatchAll("DVSeasons.VerifyThermalSync");
        }
    }
}
