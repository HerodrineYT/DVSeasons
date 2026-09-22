using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using DV.CabControls;
using DV.Interaction;
using DV.Simulation.Cars;
using DV.ThingTypes;
using LocoSim.Definitions;
using LocoSim.Implementations;
using UnityEngine;

public sealed class ElectricHeatingFixtureControl : ControlImplBase
{
    protected override InteractionHandPoses GenericHandPoses { get { return default(InteractionHandPoses); } }
    protected override void AcceptSetValue(float value) { }
    public override bool IsGrabbed() { return false; }
    public override void ForceEndInteraction() { }
}

// Run inside Unity against the game's actual Port/Fuse/TrainCar classes. The
// explicit IDs and voltage units are taken from the supplied Catenary-DC 2.5.3
// and 2WE3 2.3.5 files; their DLL is not required or executed by this fixture.
public static class VerifyElectricCabHeating
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Type adapterType, powerType;
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static object Get(object target, string field) { return target.GetType().GetField(field, All).GetValue(target); }
    private static void Set(object target, string field, object value) { target.GetType().GetField(field, All).SetValue(target, value); }
    private static object Call(object target, string method, params object[] args) { return target.GetType().GetMethod(method, All).Invoke(target, args); }
    private static float Number(object target, string property) { return (float)target.GetType().GetProperty(property, All).GetValue(target, null); }
    private static bool Flag(object target, string property) { return (bool)target.GetType().GetProperty(property, All).GetValue(target, null); }
    private static void Near(float actual, float expected, string message)
    { Require(!float.IsNaN(actual) && Math.Abs(actual - expected) < .0001f, message + ": " + actual + " expected " + expected); }

    private sealed class Supply
    {
        public readonly Fuse Breaker = new Fuse("[MainBreakerContacts]", new FuseDefinition("CLOSED", false, 0));
        public readonly Port Voltage = Port("[CustomGauges]", "SUPPLY", PortValueType.VOLTS, 0);
        public readonly Port Relative = Port("[CustomSimulation]", "RELATIVE_SUPPLY_VOLTAGE", PortValueType.GENERIC, 0);
        public readonly Port MotorRpm = Port("[CustomSimulation]", "MOTOR_RPM", PortValueType.RPM, 0);
        public readonly Port TractionLoad = Port("[CustomSimulation]", "MOTOR_LOAD", PortValueType.AMPS, 0);
        public readonly Port Throttle = Port("[Throttle]", "EXT_IN", PortValueType.GENERIC, 0);
        public readonly SimulationFlow Flow;

        public Supply(bool gauge, bool relative, bool breaker)
        {
            // A shell flow avoids unrelated vehicle physics and native Start(),
            // but its ports/fuses and their state transitions are the real ones.
            Flow = (SimulationFlow)FormatterServices.GetUninitializedObject(typeof(SimulationFlow));
            var ports = new List<Port> { MotorRpm, TractionLoad, Throttle };
            if (gauge) ports.Add(Voltage);
            if (relative) ports.Add(Relative);
            typeof(SimulationFlow).GetField("AllPorts", All).SetValue(Flow, ports);
            typeof(SimulationFlow).GetField("AllFuses", All).SetValue(Flow, breaker ? new List<Fuse> { Breaker } : new List<Fuse>());
            typeof(SimulationFlow).GetField("OrderedSimComps", All).SetValue(Flow, new SimComponent[0]);
        }

        public void Energize(float volts, bool breaker)
        { Voltage.Value = volts; Relative.Value = volts / 1500f; Breaker.ChangeState(breaker); }
    }

    private sealed class Cab : IDisposable
    {
        public readonly GameObject Root, Prefab;
        public readonly TrainCar Car;
        public readonly TrainCarType_v2 CarType;
        public readonly TrainCarLivery Livery;
        public readonly SimController Sim;
        public readonly object Adapter;
        public GameObject Interior;

        public Cab(string id, Supply supply)
        {
            Root = new GameObject("Electric heating " + id); Root.SetActive(false);
            Prefab = new GameObject("Electric cab without heater control"); Prefab.SetActive(false);
            CarType = ScriptableObject.CreateInstance<TrainCarType_v2>(); CarType.id = id;
            Livery = ScriptableObject.CreateInstance<TrainCarLivery>(); Livery.id = id;
            Livery.parentType = CarType; Livery.interiorPrefab = Prefab;
            Sim = Root.AddComponent<SimController>(); Sim.simFlow = supply.Flow;
            Car = Root.AddComponent<TrainCar>(); Car.carLivery = Livery; Car.carType = (TrainCarType)(-3003);
            typeof(TrainCar).GetField("_isLoco", All).SetValue(Car, (bool?)true);
            Adapter = Activator.CreateInstance(adapterType, true);
        }

        public float Level(bool enabled)
        {
            object[] args = { Car, enabled, 0f, false };
            Require((bool)adapterType.GetMethod("TryGetLevel", All).Invoke(Adapter, args), "Custom cab rejected: " + Livery.id);
            if (Livery.id.StartsWith("WE6981", StringComparison.Ordinal))
                Require(!(bool)args[3], "Electric heating incorrectly entered diesel engine branch");
            return (float)args[2];
        }

        public object Advance(int seconds, bool enabled, float outside = -20f)
        {
            object climate = Call(Adapter, "GetClimate", Car, enabled, outside, 1f);
            var binding = ((IDictionary)Get(Adapter, "bindings"))[Car];
            for (int i = 0; i < seconds * 2; i++)
            {
                Set(binding, "LastClimate", Time.time - .5f);
                Set(binding, "LastUpdate", Time.time - .5f);
                climate = Call(Adapter, "GetClimate", Car, enabled, outside, 1f);
            }
            return climate;
        }

        public ElectricHeatingFixtureControl AddSwitch()
        {
            var placeholder = new GameObject("C_CabHeater"); placeholder.transform.SetParent(Prefab.transform);
            Interior = new GameObject("Electric cab streamed interior"); Interior.SetActive(false);
            var switchRoot = new GameObject("C_CabHeater"); switchRoot.transform.SetParent(Interior.transform);
            var control = switchRoot.AddComponent<ElectricHeatingFixtureControl>();
            typeof(TrainCar).GetProperty("loadedInterior", All).SetValue(Car, Interior, null);
            Call(Adapter, "Clear");
            return control;
        }

        public void Dispose()
        {
            Call(Adapter, "Clear");
            UnityEngine.Object.DestroyImmediate(Root); UnityEngine.Object.DestroyImmediate(Prefab);
            if (Interior != null) UnityEngine.Object.DestroyImmediate(Interior);
            UnityEngine.Object.DestroyImmediate(Livery); UnityEngine.Object.DestroyImmediate(CarType);
        }
    }

    private static Port Port(string component, string id, PortValueType kind, float value)
    { return new Port(component, new PortDefinition(PortType.OUT, kind, id), value); }

    public static void Run(string modPath)
    {
        var mod = Assembly.LoadFrom(Path.Combine(modPath, "DVSeasons.dll"));
        adapterType = mod.GetType("DVSeasons.Mod.CabEngineHeating", true);
        powerType = mod.GetType("DVSeasons.Mod.CatenaryCabPower", true);
        VerifyPowerContract();
        VerifyRuntimeCab("WE6981A", false);
        VerifyRuntimeCab("WE6981B", true);
        VerifyPhysicalSwitch();
        VerifyFlowReplacement();
        VerifyWarmAmbientWithoutPower();
        Debug.Log("ELECTRIC_CAB_HEATING_OK: native 2WE3 A/B fuse and voltage contracts; zero RPM and traction; frozen cold start; warm air and glass; trips and loss of voltage; settings and physical controls; unknown/missing contracts; replacement simulation flow.");
    }

    private static void VerifyPowerContract()
    {
        var supply = new Supply(true, true, true);
        using (var cab = new Cab("WE6981A_custom_livery", supply))
        {
            var power = Activator.CreateInstance(powerType, true);
            Call(power, "Bind", cab.Car, supply.Flow);
            Require(Flag(power, "Supported"), "Author-compatible livery suffix not recognized");
            supply.Energize(1500, false); Near(Number(power, "Power"), 0, "Open breaker powered heater");
            supply.Breaker.ChangeState(true); Near(Number(power, "Power"), 1, "Closed breaker with 1500 V supplies no heat");
            Near(Number(power, "Voltage"), 1500, "Gauge voltage units wrong");
            supply.Voltage.Value = 750; supply.Relative.Value = 1;
            Near(Number(power, "Power"), .25f, "Supply voltmeter must take precedence and resistive power follow voltage squared");
            foreach (float invalid in new[] { 0f, -1500f, 5f, float.NaN, float.PositiveInfinity })
            {
                supply.Voltage.Value = invalid;
                Near(Number(power, "Power"), 0, "Invalid/dead gauge voltage powered heater: " + invalid);
            }
            supply.Energize(3000, true); Near(Number(power, "Power"), 1, "Overvoltage bypassed maximum heater power");
            var relative = new Supply(false, true, true); relative.Energize(1500, true);
            Call(power, "Bind", cab.Car, relative.Flow);
            Near(Number(power, "Power"), 1, "Relative-voltage-only fallback failed");
            Near(Number(power, "Voltage"), 1500, "Relative voltage was not converted to volts");
            relative.Energize(750, true); Near(Number(power, "Power"), .25f, "Relative voltage power curve inconsistent");
            foreach (var missing in new[] { new Supply(true, true, false), new Supply(false, false, true) })
            {
                missing.Energize(1500, true); Call(power, "Bind", cab.Car, missing.Flow);
                Require(Flag(power, "Supported"), "Known electric with missing ports lost explicit support classification");
                Near(Number(power, "Power"), 0, "Missing fuse or voltage source did not fail closed");
                cab.Sim.simFlow = missing.Flow; Near(cab.Level(true), 0, "Missing contract fell back to unrelated engine heating");
            }
            Call(power, "Bind", cab.Car, null); Near(Number(power, "Power"), 0, "Missing simulation retained old power");
        }
        using (var other = new Cab("UnrelatedElectricWithIdenticalPortNames", supply))
        {
            var power = Activator.CreateInstance(powerType, true); Call(power, "Bind", other.Car, supply.Flow);
            Require(!Flag(power, "Supported"), "Unsupported locomotive inferred solely from coincidental port names");
            Near(Number(power, "Power"), 0, "Unsupported locomotive received overhead heating");
        }
        Debug.Log("ELECTRIC_POWER_CONTRACT_OK: actual native fuse/state and port updates; supply units and priority; relative fallback; safe absent/invalid contracts; explicit car eligibility.");
    }

    private static void VerifyRuntimeCab(string id, bool relativeOnly)
    {
        var supply = new Supply(!relativeOnly, true, true);
        using (var cab = new Cab(id, supply))
        {
            supply.Energize(1500, false);
            Near(cab.Level(true), 0, "Breaker-open cab heats");
            object climate = cab.Advance(120, true);
            Near(Number(climate, "CabinTemperature"), -20, "Unpowered cab warmed in frost");
            Require(Number(climate, "Frost") > .99f, "Cold unpowered glass thawed");
            supply.Breaker.ChangeState(true);
            Near(supply.MotorRpm.Value, 0, "Fixture must stand with stopped traction motors");
            Near(supply.TractionLoad.Value, 0, "Fixture must have no traction load");
            Near(supply.Throttle.Value, 0, "Fixture must have throttle at zero");
            Near(cab.Level(true), 1, "Idle stationary electric failed to supply heating");
            climate = cab.Advance(120, true);
            float air = Number(climate, "CabinTemperature"), glass = Number(climate, "GlassTemperature");
            Require(air > 15 && glass > 5, id + " did not warm air and thaw glass within two minutes: " + air + "/" + glass);
            Near(Number(climate, "Frost"), 0, "Warm electric glass retained ice");
            Near(Number(climate, "EngineWarmth"), 0, "Electric heating accumulated fictitious diesel warmth");
            Near(cab.Level(false), 0, "Disabled automatic heating supplied power");
            supply.Energize(0, true); Near(cab.Level(true), 0, "Dead catenary with closed breaker supplied power");
            climate = cab.Advance(1, true);
            Require(Number(climate, "CabinTemperature") > 10, "Power loss discarded cabin thermal inertia");
            climate = cab.Advance(180, true);
            Require(Number(climate, "CabinTemperature") < -10 && Number(climate, "GlassTemperature") < 5,
                "Cab/glass did not cool after loss of catenary supply");
            supply.Energize(1500, true); climate = cab.Advance(120, false);
            Require(Number(climate, "CabinTemperature") < -18, "Disabled automatic heating still warmed shared climate");
            Debug.Log("ELECTRIC_CAB_RUNTIME_OK: " + id + " relativeOnly=" + relativeOnly + "; at -20 C after120s air=" + air + " glass=" + glass + "; no motion, traction, throttle or diesel warmth.");
        }
    }

    private static void VerifyPhysicalSwitch()
    {
        var supply = new Supply(true, true, true);
        using (var cab = new Cab("WE6981A", supply))
        {
            supply.Energize(1500, true); var control = cab.AddSwitch();
            cab.Sim.simFlow = null; control.SetValue(1);
            Near(cab.Level(true), 0, "First binding without a simulation bypassed electric power checks");
            var cold = cab.Advance(120, true);
            Near(Number(cold, "CabinTemperature"), -20, "Initially missing flow supplied heat through a physical switch");
            cab.Sim.simFlow = supply.Flow;
            Near(cab.Level(true), 1, "Delayed simulation initialization failed to energize the selected heater");
            control.SetValue(0); Near(cab.Level(true), 0, "Automatic fallback bypassed physical heater switch");
            control.SetValue(.5f); Near(cab.Level(true), .5f, "Physical heater level lost");
            Near(cab.Level(false), .5f, "Fallback-only setting unexpectedly disabled physical heater");
            supply.Breaker.ChangeState(false); Near(cab.Level(true), 0, "Physical switch powered heater through open breaker");
            supply.Energize(0, true); Near(cab.Level(true), 0, "Physical switch powered heater on dead wire");
            supply.Energize(1500, true); Near(cab.Level(true), .5f, "Restored overhead supply did not restore selected heater level");
            typeof(TrainCar).GetProperty("loadedInterior", All).SetValue(cab.Car, null, null);
            UnityEngine.Object.DestroyImmediate(cab.Interior); cab.Interior = null;
            Near(cab.Level(true), .5f, "Unloading interior lost physical heater setting");
            supply.Breaker.ChangeState(false); Near(cab.Level(true), 0, "Unloaded physical heater ignored breaker trip");
        }
        Debug.Log("ELECTRIC_PHYSICAL_SWITCH_OK: switch precedence, power availability, fallback setting scope, interior unload.");
    }

    private static void VerifyFlowReplacement()
    {
        var original = new Supply(true, true, true); original.Energize(1500, true);
        using (var cab = new Cab("WE6981B", original))
        {
            Near(cab.Level(true), 1, "Initial flow not energized");
            var replacement = new Supply(true, true, true); replacement.Energize(1500, false);
            cab.Sim.simFlow = replacement.Flow; Near(cab.Level(true), 0, "Replacement flow retained old closed breaker");
            replacement.Breaker.ChangeState(true); Near(cab.Level(true), 1, "Replacement flow not rebound");
            original.Energize(0, false); Near(cab.Level(true), 1, "Old flow still controls heating after replacement");
            cab.Sim.simFlow = null; Near(cab.Level(true), 0, "Removed simulation retained electric power");
            cab.Sim.simFlow = replacement.Flow; Near(cab.Level(true), 1, "Reloaded simulation not rebound");
        }
        Debug.Log("ELECTRIC_FLOW_REBIND_OK: new flow, old source isolation, unload and reload.");
    }

    private static void VerifyWarmAmbientWithoutPower()
    {
        var supply = new Supply(true, true, true); supply.Energize(0, false);
        using (var cab = new Cab("WE6981B", supply))
        {
            var climate = cab.Advance(180, true, 35f);
            Near(Number(climate, "CabinTemperature"), 35, "Warm ambient was incorrectly counted as residual diesel heat");
            Near(Number(climate, "GlassTemperature"), 35, "Unpowered warm-season glass gained fictitious engine heat");
            Near(Number(climate, "EngineWarmth"), 0, "Electric cab accumulated engine warmth in summer");
        }
        Debug.Log("ELECTRIC_WARM_AMBIENT_OK: unpowered 35 C cab and glass remain at ambient; no phantom diesel source.");
    }
}
