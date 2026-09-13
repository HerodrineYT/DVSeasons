using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DV.ThingTypes;
using DVSeasons.Core;
using HarmonyLib;
using LocoSim.Implementations;
using UnityEngine;
using SimBattery = LocoSim.Implementations.Battery;

namespace DVSeasons.Mod
{
    internal sealed class ColdPowertrainController : IDisposable
    {
        private const string HarmonyId = "Herodrine.DVSeasons.ColdPowertrain";
        private readonly Harmony harmony = new Harmony(HarmonyId);
        private static ColdPowertrainController active;
        private sealed class Pack { public float Temperature; }
        private ConditionalWeakTable<SimBattery, Pack> packs = new ConditionalWeakTable<SimBattery, Pack>();
        private sealed class StartState
        {
            public StartState() { }
            public bool ColdAttempt, De6, WasRunning;
            public float Held, Required;
            public float BlockTemperature = float.NaN;
            public Port Primer;
            public readonly ColdIdleStabilization Idle = new ColdIdleStabilization();
        }
        private ConditionalWeakTable<SimComponent, StartState> starts = new ConditionalWeakTable<SimComponent, StartState>();
        private float ambient = 25, nextScan;
        private bool installed, failed;

        public void Apply(float temperature)
        {
            ambient = SeasonalThermalProfile.NormalizeAmbient(temperature);
            if (failed) return;
            active = this;
            if (!installed)
            {
                try
                {
                    foreach (var type in new[] { typeof(DieselEnginePowerSource), typeof(DieselEngineDirectDrive) })
                        harmony.Patch(AccessTools.Method(type, "Tick"),
                            prefix: new HarmonyMethod(typeof(ColdPowertrainController), nameof(TimedStarter)),
                            postfix: new HarmonyMethod(typeof(ColdPowertrainController), nameof(EngineWarmth)));
                    harmony.Patch(AccessTools.Method(typeof(DieselEngineDirect), "Tick"),
                        postfix: new HarmonyMethod(typeof(ColdPowertrainController), nameof(StabilizeDe6)));
                    harmony.Patch(AccessTools.Method(typeof(DieselEngineDirect), "SimulateTorque"),
                        prefix: new HarmonyMethod(typeof(ColdPowertrainController), nameof(DirectStarter)));
                    Patch(typeof(SimBattery), "Tick", nameof(BatteryLoads));
                    harmony.Patch(AccessTools.Method(typeof(SimBattery), "Tick"),
                        prefix: new HarmonyMethod(typeof(ColdPowertrainController), nameof(BatteryTemperature)));
                    installed = true;
                    Debug.Log("[DVSeasons] Cold diesel starting and BE2 SimBattery physics active.");
                }
                catch (Exception e)
                {
                    harmony.UnpatchAll(HarmonyId); active = null; failed = true;
                    Debug.LogWarning("[DVSeasons] Cold powertrain disabled: " + e.Message);
                    return;
                }
            }
            if (Time.realtimeSinceStartup < nextScan) return;
            nextScan = Time.realtimeSinceStartup + 2;
            foreach (var car in RailSnowGameSource.GetCars())
            {
                if (car == null || car.SimController == null) continue;
                var flow = car.SimController.simFlow;
                if (flow == null) continue;
                foreach (var component in flow.OrderedSimComps)
                {
                    var SimBattery = component as SimBattery;
                    Pack pack;
                    if (SimBattery != null && car.carType == TrainCarType.LocoMicroshunter && !packs.TryGetValue(SimBattery, out pack))
                        packs.Add(SimBattery, new Pack { Temperature = ambient });
                    if (car.carType == TrainCarType.LocoDiesel && component is DieselEngineDirect)
                    {
                        var start = starts.GetOrCreateValue(component);
                        start.De6 = true;
                        foreach (var port in flow.AllPorts)
                            if (port.id == "primerThrottle.EXT_IN") { start.Primer = port; break; }
                    }
                }
            }
        }

        private void Patch(Type type, string method, string transpiler)
        {
            var target = AccessTools.Method(type, method);
            if (target == null) throw new MissingMethodException(type.FullName, method);
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(ColdPowertrainController), transpiler));
        }

        private static float EngineTemperature(SimComponent engine, PortReference temperature)
        {
            float native = temperature != null && temperature.IsConnected ? temperature.Value : active.ambient;
            if (float.IsNaN(native) || float.IsInfinity(native)) native = active.ambient;
            var state = active.starts.GetOrCreateValue(engine);
            if (float.IsNaN(state.BlockTemperature)) state.BlockTemperature = native;
            return Mathf.Max(native, state.BlockTemperature);
        }

        private static void EngineWarmth(SimComponent __instance, float delta, bool ___engineOn)
        {
            if (active == null) return;
            var state = active.starts.GetOrCreateValue(__instance);
            state.BlockTemperature = ColdPowertrainProfile.StepEngineBlockTemperature(
                state.BlockTemperature, active.ambient, ___engineOn, delta);
        }

        private static void TimedStarter(SimComponent __instance, bool ___engineOn,
            ref bool ___ignitionRequested, ref float ___ignitionDelay, ref float ___ignitionRpmBuildUpTime,
            ref float ___engineRpm)
        {
            if (active == null || ___engineOn || ___ignitionRequested) return;
            var power = __instance as DieselEnginePowerSource;
            var drive = __instance as DieselEngineDirectDrive;
            var ignition = power != null ? power.ignitionExtIn : drive.ignitionExtIn;
            var fuse = power != null ? power.engineStarterFuseRef : drive.engineStarterFuseRef;
            if (!fuse.State || ignition.Value < .75f) return;
            var temperature = EngineTemperature(__instance, power != null ? power.temperature : drive.temperature);
            var state = active.starts.GetOrCreateValue(__instance);
            state.ColdAttempt = active.ambient < 0 && temperature < 40;
            if (!state.ColdAttempt) return;
            float native = power != null ? power.ignitionTime : drive.ignitionMaxTime;
            float hold = ColdPowertrainProfile.StarterHoldSeconds(active.ambient, temperature, native);
            // Set only the new attempt's cranking parameters. Native fuel, fuse,
            // health, cancellation and engine-on logic still runs in Tick.
            ___ignitionRequested = true;
            ___engineRpm = 0;
            ___ignitionDelay = hold * .4f;
            ___ignitionRpmBuildUpTime = hold * .6f;
        }

        private static void DirectStarter(DieselEngineDirect __instance, float delta, bool ___engineOn,
            float ___engineRpmMin)
        {
            if (active == null) return;
            var state = active.starts.GetOrCreateValue(__instance);
            bool cranking = !___engineOn && __instance.engineStarterFuseRef.State && __instance.ignitionExtIn.Value > 0;
            if (!cranking) { state.Held = 0; return; }
            if (state.Held <= 0)
            {
                float temperature = EngineTemperature(__instance, __instance.temperature);
                state.ColdAttempt = active.ambient < 0 && temperature < 40;
                state.Required = ColdPowertrainProfile.StarterHoldSeconds(active.ambient, temperature, 3.5f);
            }
            if (!state.ColdAttempt) return;
            state.Held += Mathf.Max(0, delta);
            if (state.Held + .0001f < state.Required)
                __instance.engineRpm.Value = Mathf.Min(__instance.engineRpm.Value,
                    ___engineRpmMin * Mathf.Lerp(.2f, .95f, state.Held / state.Required));
            else
                __instance.engineRpm.Value = Mathf.Max(__instance.engineRpm.Value, ___engineRpmMin + .01f);
        }

        private static void StabilizeDe6(DieselEngineDirect __instance, float delta, ref bool ___engineOn)
        {
            if (active == null) return;
            var state = active.starts.GetOrCreateValue(__instance);
            if (___engineOn && !state.WasRunning && state.ColdAttempt && state.De6 && state.Primer != null)
            { state.Idle.Begin(); state.ColdAttempt = false; }
            else if (___engineOn && state.Idle.Advance(delta, state.Primer != null && state.Primer.Value > .1f))
            {
                ___engineOn = false; __instance.engineOnReadOut.Value = 0;
                Debug.Log("[DVSeasons] Cold DE6 stalled: open the engine-room throttle within 2 seconds and hold it for 3 seconds.");
            }
            if (!___engineOn) state.Idle.Reset();
            state.WasRunning = ___engineOn;
            EngineWarmth(__instance, delta, ___engineOn);
        }

        private static IEnumerable<CodeInstruction> BatteryLoads(IEnumerable<CodeInstruction> instructions)
        {
            return AdjustField(AdjustField(instructions, typeof(SimBattery), "internalResistance", nameof(Resistance), 1),
                typeof(SimBattery), "baseConsumptionMultiplier", nameof(Consumption), 1);
        }

        private static IEnumerable<CodeInstruction> AdjustField(IEnumerable<CodeInstruction> instructions,
            Type owner, string name, string method, int expected)
        {
            var result = new List<CodeInstruction>();
            var field = AccessTools.Field(owner, name);
            int count = 0;
            foreach (var instruction in instructions)
            {
                result.Add(instruction);
                if (instruction.opcode != OpCodes.Ldfld || !Equals(instruction.operand, field)) continue;
                result.Add(new CodeInstruction(OpCodes.Ldarg_0));
                result.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ColdPowertrainController), method)));
                count++;
            }
            if (count != expected) throw new InvalidOperationException(owner.Name + "." + name + " layout changed: " + count);
            return result;
        }

        private static void BatteryTemperature(SimBattery __instance, float delta, PortReference ___powerReader,
            FuseReference ___powerFuseRef)
        {
            Pack pack;
            if (active == null || !active.packs.TryGetValue(__instance, out pack)) return;
            pack.Temperature = ColdPowertrainProfile.StepBatteryTemperature(pack.Temperature, active.ambient,
                ___powerFuseRef.State ? ___powerReader.Value : 0, delta);
        }

        private static float Resistance(float original, SimBattery SimBattery)
        {
            Pack pack;
            return active != null && active.packs.TryGetValue(SimBattery, out pack)
                ? original * ColdPowertrainProfile.BatteryResistanceMultiplier(pack.Temperature) : original;
        }

        private static float Consumption(float original, SimBattery SimBattery)
        {
            Pack pack;
            return active != null && active.packs.TryGetValue(SimBattery, out pack)
                ? original * ColdPowertrainProfile.BatteryConsumptionMultiplier(pack.Temperature) : original;
        }

        public void Reset()
        {
            if (installed) harmony.UnpatchAll(HarmonyId);
            installed = false; nextScan = 0;
            packs = new ConditionalWeakTable<SimBattery, Pack>();
            starts = new ConditionalWeakTable<SimComponent, StartState>();
            if (active == this) active = null;
        }
        public void Dispose() { Reset(); }
    }
}
