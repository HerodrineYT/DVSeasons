using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using LocoSim.Definitions;
using LocoSim.Implementations;

// Executes native DV99 cooler/reservoir ticks with production Harmony patches.
// Definition components are data-only fixtures: no Unity scenes are started.
internal static class VerifyThermalRuntime
{
    private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object Def(Type t,string id) {var d=FormatterServices.GetUninitializedObject(t);t.GetField("ID").SetValue(d,id);return d;}
    static void Set(object o,string field,object v) {o.GetType().GetField(field,All).SetValue(o,v);}
    static Port P(string id,PortValueType kind,float value) {return new Port(id,new PortDefinition(PortType.READONLY_OUT,kind,"OUT"),value);}
    static HeatReservoir Reservoir(string id,float value,params Port[] inputs)
    {
        var d=(HeatReservoirDefinition)Def(typeof(HeatReservoirDefinition),id);
        d.heatCapacity=50000;d.maxTemperature=1000;d.overheatingTemperatureThreshold=900;
        Set(d,"temperature",new PortDefinition(PortType.READONLY_OUT,PortValueType.TEMPERATURE,"TEMPERATURE"));
        d.inputs=new PortReferenceDefinition[inputs.Length];
        for(int i=0;i<inputs.Length;i++)d.inputs[i]=new PortReferenceDefinition(PortValueType.HEAT_RATE,"IN"+i);
        var r=new HeatReservoir(d);r.temperature.Value=value;r.SetGameParams(new SimGameParams(true,true,true,1,1));
        for(int i=0;i<inputs.Length;i++)r.inputs[i].SetPortReference(inputs[i]);return r;
    }
    static PassiveCooler Cooler(string id,float rate,Port source,Port target)
    {
        var d=(PassiveCoolerDefinition)Def(typeof(PassiveCoolerDefinition),id);d.coolingRate=rate;
        Set(d,"temperature",new PortReferenceDefinition(PortValueType.TEMPERATURE,"TEMPERATURE"));
        Set(d,"targetTemperature",new PortReferenceDefinition(PortValueType.TEMPERATURE,"TARGET_TEMPERATURE"));
        Set(d,"heatOut",new PortDefinition(PortType.READONLY_OUT,PortValueType.HEAT_RATE,"HEAT_OUT"));
        var c=new PassiveCooler(d);c.temperature.SetPortReference(source);
        if(target!=null)c.targetTemperature.SetPortReference(target);return c;
    }
    static void Require(bool ok,string message) {if(!ok)throw new Exception(message);}
    static int Main(string[] paths)
    {
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{
            foreach(var p in paths){var f=Path.Combine(p,new AssemblyName(e.Name).Name+".dll");if(File.Exists(f))return Assembly.LoadFrom(f);}return null;};
        try {return Run(paths);}catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    static int Run(string[] paths)
    {
        var assembly=Assembly.LoadFrom(Path.Combine(paths[0],"DVSeasons.dll"));
        var type=assembly.GetType("DVSeasons.Mod.SeasonalThermalController",true);
        var controller=Activator.CreateInstance(type,true);type.GetField("active",All).SetValue(null,controller);
        type.GetField("ambientCelsius",All).SetValue(controller,-30f);
        var harmony=new Harmony("DVSeasons.ThermalVerification");
        foreach(var t in new[]{typeof(PassiveCooler),typeof(ActiveCooler),typeof(AutomaticCooler),typeof(DirectionalMovementCooler)})
            harmony.Patch(t.GetMethod("Tick"),transpiler:new HarmonyMethod(type.GetMethod("CoolerTarget",All)));
        harmony.Patch(typeof(LampLogic).GetMethod("UpdateLampState",All),transpiler:new HarmonyMethod(type.GetMethod("LampInput",All)));
        harmony.Patch(typeof(HeatReservoir).GetMethod("Tick"),prefix:new HarmonyMethod(type.GetMethod("RepairInvalidReservoir",All)));
        var power=P("power",PortValueType.HEAT_RATE,15000);
        var engine=Reservoir("engine",-30,power);var oil=Reservoir("oil",-30);
        var air=Cooler("air",250,engine.temperature,null);
        var toOil=Cooler("toOil",1000,oil.temperature,engine.temperature);
        var toEngine=Cooler("toEngine",1000,engine.temperature,oil.temperature);
        // Rebuild inputs once; all cooler references follow these same output ports.
        Set(engine,"inputs",new[]{Ref(power),Ref(air.heatOut),Ref(toEngine.heatOut)});
        Set(oil,"inputs",new[]{Ref(toOil.heatOut)});
        int updates=0;air.heatOut.ValueUpdatedInternally+=v=>updates++;
        for(int i=0;i<2400;i++){air.Tick(.25f);toOil.Tick(.25f);toEngine.Tick(.25f);engine.Tick(.25f);oil.Tick(.25f);}
        Require(engine.temperature.Value>0 && oil.temperature.Value>-5,"Winter engine/oil failed to warm.");
        Require(updates<=2400,"Cooler publishes twice per tick.");
        float before=engine.temperature.Value;type.GetField("ambientCelsius",All).SetValue(controller,30f);
        for(int i=0;i<2400;i++){air.Tick(.25f);toOil.Tick(.25f);toEngine.Tick(.25f);engine.Tick(.25f);oil.Tick(.25f);}
        Require(engine.temperature.Value>before+20 && oil.temperature.Value>20,"Summer switch did not restore warming.");
        Require(Math.Abs(toOil.heatOut.Value+toEngine.heatOut.Value)<.01,"Internal exchanger loses energy to ambient.");
        var cold=Ref(P("oiltemp",PortValueType.TEMPERATURE,-30));
        Require((float)type.GetMethod("ReadLampInput",All).Invoke(null,new object[]{cold})==0,"Cold oil lamp range not repaired.");
        Require(cold.Value==-30,"Lamp patch changed real temperature.");
        var lampDef=(LampLogicDefinition)Def(typeof(LampLogicDefinition),"oilTempLamp");
        lampDef.offRangeMin=0;lampDef.offRangeMax=110;lampDef.onRangeUsed=true;
        lampDef.onRangeMin=110;lampDef.onRangeMax=1000;
        lampDef.inputReader=new PortReferenceDefinition(PortValueType.GENERIC,"INPUT");
        Set(lampDef,"lampStateReadOut",new PortDefinition(PortType.READONLY_OUT,PortValueType.STATE,"STATE"));
        var lamp=new LampLogic(lampDef);var temp=P("lampOil",PortValueType.TEMPERATURE,-30);
        ((PortReference)typeof(LampLogic).GetField("inputReader",All).GetValue(lamp)).SetPortReference(temp);
        lamp.InitializationAfterConnecting();
        var lampState=(Port)typeof(LampLogic).GetField("lampStateReadOut",All).GetValue(lamp);
        Require(lampState.Value==0,"Native lamp rejects negative oil temperature.");
        temp.Value=150;Require(lampState.Value==1,"Native hot-oil alarm no longer works.");
        engine.temperature.Value=-10000;engine.Tick(.25f);
        Require(engine.temperature.Value>-40,"Old overcooled save did not recover.");
        harmony.UnpatchAll("DVSeasons.ThermalVerification");
        Console.WriteLine("PASS: native cooler patches, winter/summer engine and oil warmup, conserved internal heat, single publication, negative lamp input and old-save recovery.");return 0;
    }
    static PortReference Ref(Port port) {var p=new PortReference("test",new PortReferenceDefinition(port.valueType,"IN"));p.SetPortReference(port);return p;}
}
