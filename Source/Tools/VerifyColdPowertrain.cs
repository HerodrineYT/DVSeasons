using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using LocoSim.Definitions;
using LocoSim.Implementations;
using UnityEngine;
using SimBattery = LocoSim.Implementations.Battery;

// Runs in Unity against installed DV simulation assemblies and production patches.
public static class VerifyColdPowertrain
{
    const BindingFlags All = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static readonly List<GameObject> objects = new List<GameObject>();
    static Type controllerType;
    static object controller;
    static SimComponent lastEngine;
    static void Require(bool ok, string message) { if(!ok) throw new Exception(message); }
    static object Get(object o, string name) { return o.GetType().GetField(name,All).GetValue(o); }
    static void Set(object o, string name, object value) { o.GetType().GetField(name,All).SetValue(o,value); }
    static T Def<T>(string id) where T:SimComponentDefinition
    {
        var go=new GameObject("cold verification"); objects.Add(go);
        var definition=go.AddComponent<T>(); definition.ID=id; return definition;
    }
    static Port Connect(PortReference reference, PortValueType type, float value)
    {
        var port=new Port("fixture",new PortDefinition(PortType.OUT,type,"VALUE"),value);
        reference.SetPortReference(port);return port;
    }
    static Port ConnectExternal(PortReference reference, PortValueType type, float value)
    {
        var port=new Port("fixture",new PortDefinition(PortType.EXTERNAL_IN,type,"VALUE"),value);
        reference.SetPortReference(port);return port;
    }
    static void SetReferenceValue(PortReference reference, float value)
    {
        var field=typeof(PortReference).GetField("port",BindingFlags.Instance|BindingFlags.NonPublic);
        ((Port)field.GetValue(reference)).Value=value;
    }
    static void SetFuse(FuseReference reference)
    { reference.SetFuse(new Fuse("fixture",new FuseDefinition("POWER",true,0))); }
    static void Prepare(SimComponent engine, float temperature)
    {
        engine.SetGameParams(new SimGameParams(false,false,false,1,1));
        Connect((PortReference)Get(engine,"temperature"),PortValueType.TEMPERATURE,temperature);
        Connect((PortReference)Get(engine,"fuel"),PortValueType.FUEL,1000);
        Connect((PortReference)Get(engine,"oil"),PortValueType.OIL,1000);
        SetFuse((FuseReference)Get(engine,"engineStarterFuseRef"));
        ((Port)Get(engine,"ignitionExtIn")).Value=1;
    }
    static float StartSeconds(int kind,float temperature, float ambient = 25, bool de6 = false, bool interrupt = false, bool bypass=false, bool vanilla=true, float throttle=0)
    {
        UnityEngine.Random.InitState(54321);
        SimComponent engine;
        if(kind==0) engine=new DieselEnginePowerSource(Def<DieselEnginePowerSourceDefinition>("diesel"));
        else if(kind==1)
        {
            var d=Def<DieselEngineDirectDriveDefinition>("diesel");
            d.rpmToPowerCurve=AnimationCurve.Linear(0,1000,6000,100000);
            engine=new DieselEngineDirectDrive(d);
        }
        else
        {
            var d=Def<DieselEngineDirectDefinition>("diesel");
            d.rotationalInertia=1f; d.viscousDampingFactor=1f; d.engineRpmMax=6000f; d.engineRpmIdle=1000f;
            d.fuelInjection=5f; d.oilConsumptionRate=.1f; d.retarderBrakingTorque=5000f;
            d.rpmToPowerCurve=AnimationCurve.Linear(0,1000,6000,100000);
            engine=new DieselEngineDirect(d);
        }
        Prepare(engine,temperature);
        lastEngine=engine;
        Set(controller,"ambient",ambient);
        Set(controller,"IgnoreVanillaColdStarts",bypass);
        var starts=Get(controller,"starts");
        var state=starts.GetType().GetMethod("GetOrCreateValue").Invoke(starts,new object[]{engine});
        Set(state,"Vanilla",vanilla);
        if(de6)
        {
            Set(state,"De6",true);
            Set(state,"Primer",new Port("primerThrottle",new PortDefinition(PortType.EXTERNAL_IN,PortValueType.GENERIC,"EXT_IN"),0));
            Set(state,"Throttle",new Port("throttle",new PortDefinition(PortType.EXTERNAL_IN,PortValueType.CONTROL,"EXT_IN"),throttle));
        }
        for(int i=1;i<=15000;i++)
        {
            if(interrupt && i==100) ((Port)Get(engine,"ignitionExtIn")).Value=0;
            if(interrupt && i==101) ((Port)Get(engine,"ignitionExtIn")).Value=1;
            engine.Tick(.02f);
            if(i==50 && ambient<0 && temperature<40 && !(bypass && vanilla))
            {
                Require((bool)Get(state,"Cranking"),"Cold starter countdown did not activate");
                float remaining=(float)Get(state,"Remaining");
                Require(remaining>0 && remaining<12,"Countdown did not use live starter progress: "+remaining);
            }
            if(i==101 && interrupt && ambient<0)
                Require((float)Get(state,"Remaining")>4.5f,"Cancelled start did not reset the hint countdown");
            if(((Port)Get(engine,"engineOnReadOut")).Value>.5f) return i*.02f;
        }
        throw new Exception("Diesel failed to start: "+kind+", "+temperature);
    }
    static SimBattery Battery(string id)
    {
        var def=Def<BatteryDefinition>(id); var battery=new SimBattery(def);
        battery.SetGameParams(new SimGameParams(false,false,false,1,1));
        SetFuse((FuseReference)Get(battery,"powerFuseRef"));
        Connect((PortReference)Get(battery,"chargeNormalized"),PortValueType.ELECTRIC_CHARGE,.8f);
        ConnectExternal((PortReference)Get(battery,"chargeConsumption"),PortValueType.ELECTRIC_CHARGE,0);
        Connect((PortReference)Get(battery,"powerReader"),PortValueType.POWER,10000);
        return battery;
    }
    static void Register(SimBattery battery,float temperature)
    {
        var packType=controllerType.GetNestedType("Pack",BindingFlags.NonPublic);
        var pack=Activator.CreateInstance(packType,true);Set(pack,"Temperature",temperature);
        var packs=Get(controller,"packs");packs.GetType().GetMethod("Add").Invoke(packs,new object[]{battery,pack});
    }
    public static void Run(string modPath)
    {
        var mod=Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll"));
        controllerType=mod.GetType("DVSeasons.Mod.ColdPowertrainController",true);
        controller=Activator.CreateInstance(controllerType,true);
        controllerType.GetField("active",All).SetValue(null,controller);Set(controller,"ambient",-30f);
        var harmony=new Harmony("DVSeasons.ColdVerification");
        try
        {
            foreach(var t in new[]{typeof(DieselEnginePowerSource),typeof(DieselEngineDirectDrive)})
                harmony.Patch(t.GetMethod("Tick"),prefix:new HarmonyMethod(controllerType.GetMethod("TimedStarter",All)),
                    postfix:new HarmonyMethod(controllerType.GetMethod("TimedStarterState",All)));
            harmony.Patch(typeof(DieselEngineDirect).GetMethod("SimulateTorque",All),prefix:new HarmonyMethod(controllerType.GetMethod("DirectStarter",All)));
            harmony.Patch(typeof(DieselEngineDirect).GetMethod("Tick"),postfix:new HarmonyMethod(controllerType.GetMethod("StabilizeDe6",All)));
            harmony.Patch(typeof(SimBattery).GetMethod("Tick"),transpiler:new HarmonyMethod(controllerType.GetMethod("BatteryLoads",All)),
                prefix:new HarmonyMethod(controllerType.GetMethod("BatteryTemperature",All)));
            for(int kind=0;kind<3;kind++)
            {
                float warm=StartSeconds(kind,25),cold=StartSeconds(kind,-30,-30),hot=StartSeconds(kind,70,-30);
                Require(cold>warm*1.5f,"Cold starter not slower: "+kind);
                Require(Math.Abs(hot-warm)<.025f,"Warm restart changed in winter: "+kind);
                Require(Math.Abs(cold-12)<.08f,"Deep frost hold was not 12 seconds: "+kind+" / "+cold);
                float mild=StartSeconds(kind,-10,-10);
                Require(Math.Abs(mild-5)<.08f,"-10C hold was not 5 seconds: "+kind+" / "+mild);
                float interrupted=StartSeconds(kind,-10,-10,false,true);
                Require(interrupted>6.95f,"Releasing starter retained progress: "+kind+" / "+interrupted);
                Debug.Log("DVSeasons starter verified kind="+kind+": warm="+warm+"s, cold="+cold+"s");
                float bypassed=StartSeconds(kind,-30,-30,kind==2,false,true);
                Require(Math.Abs(bypassed-warm)<.025f,"Vanilla bypass did not restore native start timing: "+kind);
                if(kind==2)
                {
                    var direct=(DieselEngineDirect)lastEngine;direct.ignitionExtIn.Value=0;
                    for(int i=0;i<250;i++)direct.Tick(.02f);
                    Require(direct.engineOnReadOut.Value==1,"Bypassed DE6 still required primer");
                }
                float custom=StartSeconds(kind,-30,-30,false,false,true,false);
                Require(Math.Abs(custom-cold)<.025f,"Vanilla bypass changed a custom engine");
            }
            VerifyDe6();
            VerifyWarmRestarts();
            VerifyEarlyOilLamp(mod);
            var normal=Battery("other");var coldPack=Battery("be2");Register(coldPack,-30);
            normal.Tick(1);coldPack.Tick(1);
            Require(((Port)Get(coldPack,"voltageReadOut")).Value<((Port)Get(normal,"voltageReadOut")).Value,"Cold battery voltage sag missing");
            Require(((PortReference)Get(coldPack,"chargeConsumption")).Value>((PortReference)Get(normal,"chargeConsumption")).Value*1.5f,"Cold range cost missing");
            Require(((PortReference)Get(coldPack,"chargeNormalized")).Value==.8f,"Cold pack rewrote stored charge");
            SetReferenceValue((PortReference)Get(coldPack,"powerReader"),0);coldPack.Tick(1);
            Require(((PortReference)Get(coldPack,"chargeConsumption")).Value==0,"Idle pack loses charge without a load");
            ((FuseReference)Get(coldPack,"powerFuseRef")).ChangeState(false);coldPack.Tick(1);
            Require(((Port)Get(coldPack,"voltageReadOut")).Value==0,"Disabled fuse bypassed");
            harmony.UnpatchAll("DVSeasons.ColdVerification");
            SetReferenceValue((PortReference)Get(coldPack,"powerReader"),10000);SetFuse((FuseReference)Get(coldPack,"powerFuseRef"));coldPack.Tick(1);
            Require(Math.Abs(((Port)Get(coldPack,"voltageReadOut")).Value-((Port)Get(normal,"voltageReadOut")).Value)<.001f,"Unload did not restore native battery");
            VerifySurfacesAndLamp(mod);
            Debug.Log("DVSeasons cold features verified: three native starters, BE2-only battery load, native fuse/charge, unpatch, WALKABLE and isolated heater lamp.");
        }
        finally
        {
            harmony.UnpatchAll("DVSeasons.ColdVerification");controllerType.GetField("active",All).SetValue(null,null);
            foreach(var go in objects) UnityEngine.Object.DestroyImmediate(go);objects.Clear();
        }
    }
    static void VerifyWarmRestarts()
    {
        StartSeconds(2,-30,-30);
        var engine=(DieselEngineDirect)lastEngine;
        // DE6 has no native temperature connection. Exercise that real layout.
        Set(engine.temperature,"port",null);
        var table=Get(controller,"starts");
        var state=table.GetType().GetMethod("GetOrCreateValue").Invoke(table,new object[]{engine});
        for(int i=0;i<9000;i++)engine.Tick(.02f);
        float block=(float)Get(state,"BlockTemperature");
        Require(block>35,"Idling engine block did not warm in three minutes: "+block);
        Set(engine,"engineOn",false);engine.engineOnReadOut.Value=0;engine.engineRpm.Value=0;engine.ignitionExtIn.Value=0;
        for(int i=0;i<3000;i++)engine.Tick(.02f);
        engine.ignitionExtIn.Value=1;
        float restart=0;
        while(engine.engineOnReadOut.Value<.5f && restart<15){engine.Tick(.02f);restart+=.02f;}
        Require(restart<4.5f,"Warm engine still needs cold starter hold: "+restart);
        Set(engine,"engineOn",false);engine.engineOnReadOut.Value=0;engine.engineRpm.Value=0;engine.ignitionExtIn.Value=0;
        for(int i=0;i<90000;i++)engine.Tick(.02f);
        engine.ignitionExtIn.Value=1;restart=0;
        while(engine.engineOnReadOut.Value<.5f && restart<15){engine.Tick(.02f);restart+=.02f;}
        Require(restart>11.7f && restart<12.1f,"Cooled engine retains warm-start advantage: "+restart);
        Debug.Log("DVSeasons warm restarts verified: per-engine heat, three-minute idle, short stop and thirty-minute cool-down.");
    }
    static void VerifyEarlyOilLamp(Assembly mod)
    {
        var thermalType=mod.GetType("DVSeasons.Mod.SeasonalThermalController",true);
        var thermal=Activator.CreateInstance(thermalType,true);
        Require(thermalType.GetField("active",All).GetValue(null)==null,"Fixture must precede world thermal activation");
        thermalType.GetMethod("EnableLampProtection").Invoke(thermal,null);
        int errors=0;
        Application.LogCallback callback=(message,stack,kind)=>{if(message.Contains("Bad range setup for lamp"))errors++;};
        Application.logMessageReceived+=callback;
        try
        {
            var definition=Def<LampLogicDefinition>("oilTempLamp");
            definition.offRangeMin=0;definition.offRangeMax=90;definition.onRangeUsed=true;
            definition.onRangeMin=90;definition.onRangeMax=105;definition.blinkRangeUsed=true;
            definition.blinkRangeMin=105;definition.blinkRangeMax=float.PositiveInfinity;
            definition.powerFuseId=null;
            var lamp=new LampLogic(definition);
            var port=Connect((PortReference)Get(lamp,"inputReader"),PortValueType.TEMPERATURE,-30);
            lamp.InitializationAfterConnecting();
            var output=(Port)Get(lamp,"lampStateReadOut");
            Require(output.Value==0 && port.Value==-30,"Cold loading lamp protection changed simulation temperature");
            port.Value=100;Require(output.Value>=1,"Warm oil alarm broken");
            port.Value=120;Require(output.Value>=3,"Overheated oil blinking broken");
            Require(errors==0,"Oil lamp logs errors before world is ready");
            Debug.Log("DVSeasons oil lamp verified before thermal activation: cold save loads without errors; hot alarms remain functional.");
        }
        finally {Application.logMessageReceived-=callback;((IDisposable)thermal).Dispose();}
    }
    static void VerifyDe6()
    {
        StartSeconds(2,-10,-10,true);
        var engine=(DieselEngineDirect)lastEngine;
        engine.ignitionExtIn.Value=0;
        for(int i=0;i<245;i++)engine.Tick(.02f);
        Require(engine.engineOnReadOut.Value==1,"DE6 stalled before five-second response window elapsed");
        for(int i=0;i<10;i++)engine.Tick(.02f);
        Require(engine.engineOnReadOut.Value==0,"DE6 without either control did not stall after five seconds");
        StartSeconds(2,-10,-10,true);
        engine=(DieselEngineDirect)lastEngine;engine.ignitionExtIn.Value=0;
        var starts=Get(controller,"starts");
        var state=starts.GetType().GetMethod("GetOrCreateValue").Invoke(starts,new object[]{engine});
        var primer=(Port)Get(state,"Primer");
        var idle=Get(state,"Idle");
        Require((float)idle.GetType().GetProperty("RemainingSeconds").GetValue(idle,null)==3,"Primer hint counted down before lever opened");
        for(int i=0;i<225;i++)engine.Tick(.02f);
        primer.Value=1;
        for(int i=0;i<50;i++)engine.Tick(.02f);
        float left=(float)idle.GetType().GetProperty("RemainingSeconds").GetValue(idle,null);
        Require(Math.Abs(left-2)<.025f,"Primer hint did not track actual continuous hold");
        for(int i=0;i<101;i++)engine.Tick(.02f);
        Require(!(bool)Get(state,"Idle").GetType().GetProperty("Active").GetValue(Get(state,"Idle"),null),"DE6 primer hold did not finish");
        primer.Value=0;
        for(int i=0;i<100;i++)engine.Tick(.02f);
        Require(engine.engineOnReadOut.Value==1,"DE6 stalled after completing primer sequence");

        foreach(bool preset in new[]{false,true})
        {
            StartSeconds(2,-10,-10,true,throttle:preset?1:0);
            engine=(DieselEngineDirect)lastEngine;engine.ignitionExtIn.Value=0;
            state=starts.GetType().GetMethod("GetOrCreateValue").Invoke(starts,new object[]{engine});
            var throttle=(Port)Get(state,"Throttle");
            if(!preset)for(int i=0;i<225;i++)engine.Tick(.02f);
            throttle.Value=1;
            for(int i=0;i<50;i++)engine.Tick(.02f);
            idle=Get(state,"Idle");
            left=(float)idle.GetType().GetProperty("RemainingSeconds").GetValue(idle,null);
            Require(Math.Abs(left-2)<.025f,"Maximum cab throttle did not advance the hold countdown");
            for(int i=0;i<101;i++)engine.Tick(.02f);
            Require(!(bool)idle.GetType().GetProperty("Active").GetValue(idle,null),"Cab throttle did not complete stabilization");
            throttle.Value=0;
            for(int i=0;i<100;i++)engine.Tick(.02f);
            Require(engine.engineOnReadOut.Value==1,"DE6 with cab throttle alone did not remain running");
        }
        StartSeconds(2,-10,-10,true,throttle:.99f);
        engine=(DieselEngineDirect)lastEngine;engine.ignitionExtIn.Value=0;
        for(int i=0;i<255;i++)engine.Tick(.02f);
        Require(engine.engineOnReadOut.Value==0,"Partial cab throttle incorrectly replaced the primer");

        StartSeconds(2,-10,-10,true,throttle:1);
        engine=(DieselEngineDirect)lastEngine;engine.ignitionExtIn.Value=0;
        state=starts.GetType().GetMethod("GetOrCreateValue").Invoke(starts,new object[]{engine});
        for(int i=0;i<50;i++)engine.Tick(.02f);
        ((Port)Get(state,"Throttle")).Value=0;
        engine.Tick(.02f);
        Require(engine.engineOnReadOut.Value==0,"Interrupted full-throttle hold did not stall");

        StartSeconds(2,-10,-10,true);
        engine=(DieselEngineDirect)lastEngine;engine.ignitionExtIn.Value=0;
        state=starts.GetType().GetMethod("GetOrCreateValue").Invoke(starts,new object[]{engine});
        ((Port)Get(state,"Primer")).Value=1;
        for(int i=0;i<50;i++)engine.Tick(.02f);
        ((Port)Get(state,"Throttle")).Value=1;
        ((Port)Get(state,"Primer")).Value=0;
        for(int i=0;i<101;i++)engine.Tick(.02f);
        Require(engine.engineOnReadOut.Value==1 && !(bool)Get(state,"Idle").GetType().GetProperty("Active").GetValue(Get(state,"Idle"),null),"Continuous handover between primer and full throttle reset stabilization");
        Debug.Log("DVSeasons DE6 native engine verified: 5s response window; 3s primer or maximum cab throttle hold; preset throttle, partial throttle rejection, interruption and continuous handover.");
    }
    static void VerifySurfacesAndLamp(Assembly mod)
    {
        int mask=(int)mod.GetType("DVSeasons.Mod.SeasonSurfaceLayers",true).GetField("Mask").GetRawConstantValue();
        var big=new GameObject("coarse hull");objects.Add(big);big.layer=10;big.transform.position=new Vector3(0,3,0);big.AddComponent<BoxCollider>();
        var deck=new GameObject("walkable deck");objects.Add(deck);deck.layer=11;deck.transform.position=new Vector3(0,1,0);deck.AddComponent<BoxCollider>();
        Physics.SyncTransforms();RaycastHit hit;
        Require(Physics.Raycast(new Vector3(0,5,0),Vector3.down,out hit,10,mask,QueryTriggerInteraction.Ignore)&&hit.collider.gameObject==deck,"Coarse collision hull captured instead of WALKABLE");
        var mat=new Material(Shader.Find("Standard"));mat.EnableKeyword("_EMISSION");mat.SetColor("_EmissionColor",Color.black);
        var renderer=deck.AddComponent<MeshRenderer>();renderer.sharedMaterial=mat;
        var block=new MaterialPropertyBlock();block.SetFloat("_Glossiness",.37f);renderer.SetPropertyBlock(block);
        var lampType=mod.GetType("DVSeasons.Mod.CabHeaterLamp",true);var lamp=Activator.CreateInstance(lampType,new object[]{renderer});
        try
        {
            lampType.GetMethod("Set").Invoke(lamp,new object[]{1f});renderer.GetPropertyBlock(block);
            Require(block.GetColor("_EmissionColor").r>.9f,"Heater lamp did not light");
            Require(mat.GetColor("_EmissionColor").r==0,"Heater lamp changed other lamps' shared material");
            Require(Math.Abs(block.GetFloat("_Glossiness")-.37f)<.001f,"Lamp overwrote another property");
            lampType.GetMethod("Set").Invoke(lamp,new object[]{0f});renderer.GetPropertyBlock(block);
            Require(block.GetColor("_EmissionColor").r==0,"Heater lamp did not turn off");
        }
        finally {lampType.GetMethod("Dispose").Invoke(lamp,null);UnityEngine.Object.DestroyImmediate(mat);}
    }
}
