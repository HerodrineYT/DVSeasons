using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Xml.Serialization;
using DV.CabControls;
using DV.Interaction;
using DV.ThingTypes;
using DV.Simulation.Cars;
using DV.Openables;
using LocoSim.Definitions;
using LocoSim.Implementations;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

public sealed class MemoryFixtureControl : ControlImplBase
{
    protected override InteractionHandPoses GenericHandPoses {get{return default(InteractionHandPoses);}}
    protected override void AcceptSetValue(float value) { }
    public override bool IsGrabbed(){return false;}
    public override void ForceEndInteraction(){ }
}

public static class VerifyCabShortcut
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object Get(object o,string f){return o.GetType().GetField(f,All).GetValue(o);}
    static object Call(object o,string m,params object[] a){return o.GetType().GetMethod(m,All).Invoke(o,a);}
    static void Require(bool ok,string m){if(!ok)throw new Exception(m);}
    static void FinishRestore(object memory)
    {var routine=(IEnumerator)Call(memory,"RestoreAfterInitialization");int n=0;while(routine.MoveNext())if(++n>5)throw new Exception("Fixture never initialized");}
    public static void Run(string modPath)
    {
        var mod=Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll"));
        VerifyCclEnumHeating(mod);
        VerifyEngineHeating(mod);
        VerifyCabOpeningScope(mod);
        var settings=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SeasonModSettings",true));
        Require((bool)Get(settings,"EngineHeatingWithoutSwitch"),"Engine heating default must be enabled");
        settings.GetType().GetField("EngineHeatingWithoutSwitch").SetValue(settings,false);
        var key=(KeyBinding)Get(settings,"CabHeaterHotkey");Require(key.keyCode==KeyCode.None,"Default hotkey must be unassigned");
        key.Change(KeyCode.H,3);
        var serializer=new XmlSerializer(settings.GetType());string xml;
        using(var writer=new StringWriter()){serializer.Serialize(writer,settings);xml=writer.ToString();}
        using(var reader=new StringReader(xml))settings=serializer.Deserialize(reader);
        Require(!(bool)Get(settings,"EngineHeatingWithoutSwitch"),"Disabled engine heating lost on settings reload");
        using(var reader=new StringReader(xml.Replace("<EngineHeatingWithoutSwitch>false</EngineHeatingWithoutSwitch>","")))
            Require((bool)Get(serializer.Deserialize(reader),"EngineHeatingWithoutSwitch"),"Old settings did not retain enabled default");
        key=(KeyBinding)Get(settings,"CabHeaterHotkey");Require(key.keyCode==KeyCode.H && key.modifiers==3,"Hotkey/modifiers lost on reload");

        var network=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.OfflineSeasonBridge",true),true);
        var service=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.CabHeaterService",true),new[]{network});
        Call(service,"StartSession",new SaveGameData());
        var dm1u=Get(service,"dm1u");
        var root=new GameObject("DM1U native controls fixture");root.SetActive(false);
        try
        {
            var heater=root.AddComponent<MemoryFixtureControl>();
            var memory=root.AddComponent(mod.GetType("DVSeasons.Mod.Dm1uControlMemory",true));
            int changes=0;
            Action<float> onChanged=value=>{changes++;Call(service,"OnToggle","dm1u-fixture",value);};
            Call(memory,"Bind",root.transform,heater,0f,onChanged);
            root.SetActive(true);FinishRestore(memory);heater.SetValue(1);
            Require(changes==1 && (float)Call(service,"GetLevelById","dm1u-fixture")==1,"Manual heater change not recorded");
            root.SetActive(false);Call(memory,"OnDisable");heater.SetValue(0);
            Require(changes==1,"Native disable reset overwrote memory");
            root.SetActive(true);FinishRestore(memory);
            Require(heater.Value==1 && changes==1,"Re-enabled control lost its position or emitted a user toggle");
            Call(memory,"SetDesired",0f);Require(heater.Value==0 && changes==1,"Confirmed host value was echoed as user input");
            root.SetActive(false);Call(memory,"Detach");UnityEngine.Object.DestroyImmediate(memory);
            memory=root.AddComponent(mod.GetType("DVSeasons.Mod.Dm1uControlMemory",true));
            Call(memory,"Bind",root.transform,heater,1f,onChanged);root.SetActive(true);FinishRestore(memory);
            Require(heater.Value==1,"Recreated interior did not recover heater");
            var fanStates=(IDictionary)Get(dm1u,"fans");fanStates["dm1u-fixture"]=1f;fanStates["another-dm1u"]=0f;
            var saved=new SaveGameData();Call(service,"Save",saved);
            Debug.Log("CAB_SAVE_FIXTURE: "+saved.GetString("DVSeasons.CabHeaters"));
            Call(service,"StartSession",saved);
            Require((float)Call(service,"GetLevelById","dm1u-fixture")==1,"Saved heater lost on session reload");
            fanStates=(IDictionary)Get(dm1u,"fans");Require((float)fanStates["dm1u-fixture"]==1 && (float)fanStates["another-dm1u"]==0,"Independent fan states lost on save/reload");
            Debug.Log("CAB_SHORTCUT_OK: actual UMM key serialization; native control disable/re-enable/recreation; suppressed restore callbacks; per-car heater/fan save reload.");
        }
        finally{UnityEngine.Object.DestroyImmediate(root);((IDisposable)service).Dispose();}
    }

    private static void VerifyEngineHeating(Assembly mod)
    {
        var root = new GameObject("Custom heating fixture"); root.SetActive(false);
        var prefab = new GameObject("Custom interior prefab"); prefab.SetActive(false);
        var interior = new GameObject("Custom streamed interior"); interior.SetActive(false);
        var stockType = ScriptableObject.CreateInstance<TrainCarType_v2>();
        var customType = ScriptableObject.CreateInstance<TrainCarType_v2>();
        var stock = ScriptableObject.CreateInstance<TrainCarLivery>(); stock.parentType = stockType;
        var livery = ScriptableObject.CreateInstance<TrainCarLivery>(); livery.parentType = customType; livery.interiorPrefab = prefab;
        try
        {
            var type = mod.GetType("DVSeasons.Mod.CabEngineHeating", true);
            var classify = type.GetMethod("IsCustomType", All);
            Require((bool)classify.Invoke(null, new object[]{TrainCarType.LocoShunter,livery,stock}), "Custom loco borrowing DE2 enum misclassified as stock");
            livery.parentType = stockType;
            Require(!(bool)classify.Invoke(null, new object[]{TrainCarType.LocoShunter,livery,stock}), "Stock reskin misclassified as custom");
            livery.parentType = customType;
            var car = root.AddComponent<TrainCar>(); car.carType = (TrainCarType)12345; car.carLivery = livery;
            typeof(TrainCar).GetField("_isLoco", All).SetValue(car, (bool?)true);
            var adapter = Activator.CreateInstance(type, true);
            Func<bool, object[]> sample = enabled => {
                object[] args = {car, enabled, 0f, false};
                Require((bool)type.GetMethod("TryGetLevel", All).Invoke(adapter, args), "Custom heating adapter not active");
                return args;
            };
            var result = sample(true);
            Require((bool)result[3] && (float)result[2] == 0, "Cold engine must be eligible but supply zero heat");
            var binding = ((IDictionary)Get(adapter,"bindings"))[car]; var heat = Get(binding,"Heat");
            for(int i=0;i<120;i++)Call(heat,"Advance",5f,true,float.NaN);
            Require((float)sample(true)[2] > .9f, "Warmed engine supplies no heat");
            object climate = null;
            for(int i=0;i<120;i++)
            {
                binding.GetType().GetField("LastClimate",All).SetValue(binding,Time.time-5);
                climate = Call(adapter,"GetClimate",car,true,-20f,1f);
            }
            Require((float)climate.GetType().GetProperty("CabinTemperature").GetValue(climate,null)>15,"Cab without windows did not warm");
            for(int i=0;i<120;i++)
            {
                binding.GetType().GetField("LastClimate",All).SetValue(binding,Time.time-5);
                climate = Call(adapter,"GetClimate",car,false,-20f,1f);
            }
            Require((float)climate.GetType().GetProperty("CabinTemperature").GetValue(climate,null)<-19,"No-window cab ignored disabled fallback");
            var offline = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.OfflineSeasonBridge",true),true);
            var service = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.CabHeaterService",true),new[]{offline});
            Call(service,"StartSession",new SaveGameData());
            service.GetType().GetField("OutsideTemperature",All).SetValue(service,-20f);
            Require((float)Call(service,"GetCabinTemperature",car)==-20,"Shared cabin temperature requires window visual controller");
            ((IDisposable)service).Dispose();
            result = sample(false); Require(!(bool)result[3] && (float)result[2] == 0, "Disabled fallback still supplies heat");

            var radiator = new GameObject("CabHeater"); radiator.transform.SetParent(prefab.transform);
            Call(adapter,"Clear"); result = sample(true);
            Require((bool)result[3], "Plain radiator mesh incorrectly counted as a switch");
            var switchRoot = new GameObject("C_CabHeater"); switchRoot.transform.SetParent(prefab.transform);
            Call(adapter,"Clear"); result = sample(true);
            Require(!(bool)result[3], "Unloaded heater control placeholder activated engine fallback");
            var liveSwitch = new GameObject("C_CabHeater"); liveSwitch.transform.SetParent(interior.transform);
            var control = liveSwitch.AddComponent<MemoryFixtureControl>(); control.SetValue(.5f);
            typeof(TrainCar).GetProperty("loadedInterior",All).SetValue(car,interior,null);
            result = sample(true); Require(!(bool)result[3] && (float)result[2] == .5f,"Physical mod heater switch must take priority");
            typeof(TrainCar).GetProperty("loadedInterior",All).SetValue(car,null,null); UnityEngine.Object.DestroyImmediate(interior); interior = null;
            result = sample(true); Require(!(bool)result[3] && (float)result[2] == .5f,"Streaming unload changed physical switch into engine fallback");
            Debug.Log("ENGINE_CAB_HEAT_OK: custom type with borrowed stock enum; stock reskin; cold/warm engine; no-window cab warmth and setting off; shared temperature without window controller; radiator without switch; unloaded switch; physical switch priority and streaming.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(prefab);
            if(interior!=null)UnityEngine.Object.DestroyImmediate(interior);
            foreach(var value in new UnityEngine.Object[]{stock, livery, stockType, customType})UnityEngine.Object.DestroyImmediate(value);
        }
    }

    // Match CCL's installed EnumPatches.IsDefinedPrefix contract exactly for
    // its registered class750 value, without loading every CCL dependency.
    private static bool CclIsDefinedPrefix(Type enumType, object value, ref bool __result)
    {
        if (enumType == typeof(TrainCarType) &&
            ((value is int && (int)value == -1002) || (value is TrainCarType && (TrainCarType)value == (TrainCarType)(-1002))))
        { __result = true; return false; }
        return true;
    }

    private static void VerifyCclEnumHeating(Assembly mod)
    {
        const string patchId = "DVSeasons.Verify.CclEnumHeating";
        var patcher = new Harmony(patchId);
        var root = new GameObject("CCL class750 heating regression"); root.SetActive(false);
        var prefab = new GameObject("CCL custom interior without heater"); prefab.SetActive(false);
        var parentType = ScriptableObject.CreateInstance<TrainCarType_v2>();
        var livery = ScriptableObject.CreateInstance<TrainCarLivery>(); livery.parentType = parentType; livery.interiorPrefab = prefab;
        try
        {
            var original = typeof(Enum).GetMethod("IsDefined",new[]{typeof(Type),typeof(object)});
            patcher.Patch(original,prefix:new HarmonyMethod(typeof(VerifyCabShortcut).GetMethod("CclIsDefinedPrefix",All)));
            var custom = (TrainCarType)(-1002);
            Require(Enum.IsDefined(typeof(TrainCarType),custom),"CCL-style Enum.IsDefined patch did not activate");
            // CCL also maps its new enum value to the very same custom livery.
            var legacyMapping = new Dictionary<TrainCarType,TrainCarLivery>{{custom,livery}};
            bool oldClassifier = custom == TrainCarType.NotSet || !Enum.IsDefined(typeof(TrainCarType),custom) ||
                legacyMapping[custom].parentType != livery.parentType;
            Require(!oldClassifier,"Former custom detection must reproduce CCL false-negative");
            var adapterType = mod.GetType("DVSeasons.Mod.CabEngineHeating",true);
            Require((bool)adapterType.GetMethod("IsCustomType",All).Invoke(null,new object[]{custom,livery,livery}),
                "CCL registered locomotive was incorrectly classified as stock");
            var car = root.AddComponent<TrainCar>(); car.carType = custom; car.carLivery = livery;
            typeof(TrainCar).GetField("_isLoco",All).SetValue(car,(bool?)true);
            Require((bool)adapterType.GetMethod("IsCustomLocomotive",All).Invoke(null,new object[]{car}),
                "Runtime custom-car eligibility failed with CCL Enum patch active");
            var adapter = Activator.CreateInstance(adapterType,true);
            object[] args = {car,true,0f,false};
            Require((bool)adapterType.GetMethod("TryGetLevel",All).Invoke(adapter,args) && (bool)args[3],
                "CCL locomotive never reached direct engine-heating branch");
            var binding = ((IDictionary)Get(adapter,"bindings"))[car]; var heat = Get(binding,"Heat");
            for(int i=0;i<120;i++) Call(heat,"Advance",.5f,true,float.NaN);
            adapterType.GetMethod("TryGetLevel",All).Invoke(adapter,args);
            Require((float)args[2]>.9f,"CCL registered loco has zero heating despite warm engine");
            VerifyRpmHeating(mod);
            Debug.Log("CCL_ENUM_HEATING_OK: actual Harmony Enum.IsDefined=true for class750 -1002; old classifier reproduced false; declared-type classification and live direct-heating branch pass.");
        }
        finally
        {
            patcher.UnpatchAll(patchId);
            UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(prefab);
            UnityEngine.Object.DestroyImmediate(livery); UnityEngine.Object.DestroyImmediate(parentType);
        }
    }

    private static void VerifyRpmHeating(Assembly mod)
    {
        var root = new GameObject("Class750 engine heating fixture"); root.SetActive(false);
        var prefab = new GameObject("Class750 interior without heater"); prefab.SetActive(false);
        var carType = ScriptableObject.CreateInstance<TrainCarType_v2>();
        var livery = ScriptableObject.CreateInstance<TrainCarLivery>(); livery.parentType = carType; livery.interiorPrefab = prefab;
        try
        {
            var definition = root.AddComponent<DieselEngineDirectDefinition>();
            definition.ID = "de"; definition.engineRpmIdle = 480; definition.engineRpmMax = 1100;
            definition.rpmToPowerCurve = AnimationCurve.Linear(0, 0, 1100, 1000000);
            definition.engineStarterFuseId = "fusebox.ENGINE_STARTER";
            var engine = new DieselEngineDirect(definition);
            // The installed class750 uses these real native ports and explicitly
            // leaves de.TEMPERATURE unconnected. No full vehicle physics is needed.
            var flow = (SimulationFlow)FormatterServices.GetUninitializedObject(typeof(SimulationFlow));
            typeof(SimulationFlow).GetField("OrderedSimComps").SetValue(flow,new SimComponent[]{engine});
            typeof(SimulationFlow).GetField("AllPorts").SetValue(flow,new List<Port>(engine.GetAllPorts()));
            root.AddComponent<SimController>().simFlow = flow;
            var car = root.AddComponent<TrainCar>(); car.carType = (TrainCarType)(-1002); car.carLivery = livery;
            typeof(TrainCar).GetField("_isLoco",All).SetValue(car,(bool?)true);
            var adapterType = mod.GetType("DVSeasons.Mod.CabEngineHeating",true);
            engine.engineOnReadOut.Value = 1;
            float[] levels = new float[2], temperatures = new float[2];
            float maxAirAt60 = 0, maxAirAt90 = 0, maxGlassAt90 = 0;
            for(int pass=0;pass<2;pass++)
            {
                engine.engineRpmNormalizedReadOut.Value = pass==0 ? 480f/1100f : 1f;
                var adapter = Activator.CreateInstance(adapterType,true);
                Call(adapter,"GetClimate",car,true,-20f,1f);
                var binding = ((IDictionary)Get(adapter,"bindings"))[car];
                var savedClimate = Activator.CreateInstance(Get(binding,"Climate").GetType().Assembly.GetType("DVSeasons.Core.WindowClimateState",true));
                savedClimate.GetType().GetField("Initialized").SetValue(savedClimate,true);
                savedClimate.GetType().GetField("EngineWarmth").SetValue(savedClimate,.9987277f);
                savedClimate.GetType().GetField("Heater").SetValue(savedClimate,-16.24913f);
                savedClimate.GetType().GetField("Cabin").SetValue(savedClimate,-14.25736f);
                savedClimate.GetType().GetField("Glass").SetValue(savedClimate,-15.11552f);
                savedClimate.GetType().GetField("Frost").SetValue(savedClimate,1f);
                Call(Get(binding,"Climate"),"Restore",savedClimate);
                Require(ReferenceEquals(Get(binding,"EngineRpm"),engine.engineRpmNormalizedReadOut),"Live native RPM port not bound");
                Require(ReferenceEquals(Get(binding,"IdleRpm"),engine.engineIdleRpmNormalizedReadOut),"Native idle RPM port not bound");
                Require(ReferenceEquals(Get(binding,"MaximumRpm"),engine.engineRpmMaxReadOut),"Native maximum RPM port not bound");
                Require(!engine.temperature.IsConnected,"Fixture must represent class750's missing coolant measurement");
                object climate = null;
                for(int i=0;i<240;i++)
                {
                    binding.GetType().GetField("LastUpdate",All).SetValue(binding,Time.time-.5f);
                    binding.GetType().GetField("LastClimate",All).SetValue(binding,Time.time-.5f);
                    climate = Call(adapter,"GetClimate",car,true,-20f,1f);
                    if(i==119)
                    {
                        object[] args = {car,true,0f,false}; adapterType.GetMethod("TryGetLevel",All).Invoke(adapter,args);
                        levels[pass] = (float)args[2];
                        if(pass==1) maxAirAt60 = (float)climate.GetType().GetProperty("CabinTemperature").GetValue(climate,null);
                    }
                    if(pass==1 && i==179)
                    {
                        maxAirAt90 = (float)climate.GetType().GetProperty("CabinTemperature").GetValue(climate,null);
                        maxGlassAt90 = (float)climate.GetType().GetProperty("GlassTemperature").GetValue(climate,null);
                    }
                }
                temperatures[pass] = (float)climate.GetType().GetProperty("CabinTemperature").GetValue(climate,null);
            }
            Require(levels[1]>levels[0]*2,"Raising native RPM did not increase early heating");
            Require(temperatures[0]>9 && temperatures[1]>temperatures[0]+10,"Cab heating is too weak or independent of RPM");
            Require(maxAirAt60>12 && maxAirAt90>20 && maxGlassAt90>5,"Maximum RPM did not warm cab and thaw glass in90seconds");
            Debug.Log("ENGINE_CAB_RPM_OK: actual DieselEngineDirect ports; unconnected coolant; idle480/max1100; after60s power="+levels[0]+"/"+levels[1]+"; after120s cabin="+temperatures[0]+"/"+temperatures[1]+" C at -20 C outside; maxRPMair60/90="+maxAirAt60+"/"+maxAirAt90+"; glass90="+maxGlassAt90+" C.");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(prefab);
            UnityEngine.Object.DestroyImmediate(livery); UnityEngine.Object.DestroyImmediate(carType);
        }
    }

    private static void VerifyCabOpeningScope(Assembly mod)
    {
        var type = mod.GetType("DVSeasons.Mod.CabOpeningScope",true);
        var split = type.GetMethod("FindBoundary",All); var select = type.GetMethod("SelectSide",All); var includes = type.GetMethod("Includes",All);
        var positions = new[]{-5.867f,-5.867f,-5.721f,5.867f,5.867f,5.724f};
        float boundary = (float)split.Invoke(null,new object[]{positions});
        int front = (int)select.Invoke(null,new object[]{boundary,positions,true,6.5f});
        int rear = (int)select.Invoke(null,new object[]{boundary,positions,true,-6.5f});
        Require(front==1 && rear==-1,"Occupied class750 cab group not selected");
        for(int i=0;i<positions.Length;i++)
        {
            Require((bool)includes.Invoke(null,new object[]{positions[i],boundary,front})==(i>=3),"Opposite cab door affects front cab ventilation");
            Require((bool)includes.Invoke(null,new object[]{positions[i],boundary,rear})==(i<3),"Same cab side/internal door was excluded");
        }
        Require((int)select.Invoke(null,new object[]{boundary,positions,false,6.5f})==0,"Unoccupied car must keep conservative ventilation");
        Require((int)select.Invoke(null,new object[]{boundary,positions,true,0f})==0,"Engine-room position must not select an unrelated cab");
        Require(float.IsNaN((float)split.Invoke(null,new object[]{new[]{-1f,0f,1f}})),"Single cab doors split into two cabins");
        VerifyScopedRuntimeDoors(mod,boundary);
        Debug.Log("CAB_OPENING_SCOPE_OK: six actual class750 door positions; front/rear occupied cab; all local side and internal doors retained; opposite cab ignored; no-player and ambiguous/single-cab fallback.");
    }

    private static void VerifyScopedRuntimeDoors(Assembly mod,float boundary)
    {
        var root = new GameObject("Actual class750 cab door controls fixture"); root.SetActive(false);
        try
        {
            var frontGo = new GameObject("C_door1"); frontGo.transform.SetParent(root.transform);
            var rearGo = new GameObject("C_door3"); rearGo.transform.SetParent(root.transform);
            var frontControl = frontGo.AddComponent<MemoryFixtureControl>();
            var rearControl = rearGo.AddComponent<MemoryFixtureControl>();
            var front = frontGo.AddComponent<OpenableControl>(); front.closedAtZero=true; front.Init();
            var rear = rearGo.AddComponent<OpenableControl>(); rear.closedAtZero=true; rear.Init();
            var controller = root.AddComponent<DoorsAndWindowsController>();
            controller.entries = new[]{front,rear}; Call(controller,"Start");
            var type = mod.GetType("DVSeasons.Mod.CabEngineHeating",true);
            var binding = Activator.CreateInstance(type.GetNestedType("Binding",BindingFlags.NonPublic),true);
            binding.GetType().GetField("DoorsAndWindows",All).SetValue(binding,new[]{controller});
            binding.GetType().GetField("Openables",All).SetValue(binding,new[]{front,rear});
            binding.GetType().GetField("OpeningControls",All).SetValue(binding,new ControlImplBase[]{frontControl,rearControl});
            binding.GetType().GetField("OpeningPositions",All).SetValue(binding,new[]{-5.867f,5.867f});
            binding.GetType().GetField("CabBoundary",All).SetValue(binding,boundary);
            var read = type.GetMethod("AnythingOpen",All);
            rearControl.SetValue(1);
            Require(controller.AnythingOpen(),"Native aggregate did not see opposite open door");
            Require(!(bool)read.Invoke(null,new object[]{binding,-1}),"Opposite open cab door still ventilates occupied cab");
            Require((bool)read.Invoke(null,new object[]{binding,0}),"Unoccupied car must preserve aggregate open state");
            frontControl.SetValue(.1f);
            Require((bool)read.Invoke(null,new object[]{binding,-1}),"Own cab door did not ventilate at native threshold");
            frontControl.SetValue(.09f);
            Require(!(bool)read.Invoke(null,new object[]{binding,-1}),"Native closed threshold changed");
            binding.GetType().GetField("OpeningControls",All).SetValue(binding,new ControlImplBase[]{frontControl,null});
            Require((bool)read.Invoke(null,new object[]{binding,-1}),"Missing direct binding silently discarded native open state");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }
}
