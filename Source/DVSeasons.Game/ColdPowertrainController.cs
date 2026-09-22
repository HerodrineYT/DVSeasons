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
            public Port Primer, Throttle;
            public TrainCar Car;
            public bool Vanilla, Cranking;
            public float Remaining;
            public readonly ColdIdleStabilization Idle = new ColdIdleStabilization();
        }
        private ConditionalWeakTable<SimComponent, StartState> starts = new ConditionalWeakTable<SimComponent, StartState>();
        private float ambient = 25, nextScan;
        private bool installed, failed;
        public bool IgnoreVanillaColdStarts;
        private readonly Dictionary<TrainCar,List<SimComponent>> engines=new Dictionary<TrainCar,List<SimComponent>>();
        private readonly Dictionary<TrainCar,SimulationFlow> engineFlows=new Dictionary<TrainCar,SimulationFlow>();
        private readonly List<TrainCar> removedCars=new List<TrainCar>();
        private readonly List<ColdStartHintState> hintSnapshot=new List<ColdStartHintState>();

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
                            postfix: new HarmonyMethod(typeof(ColdPowertrainController), nameof(TimedStarterState)));
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
            removedCars.Clear();foreach(var car in engines.Keys)if(car==null)removedCars.Add(car);
            foreach(var car in removedCars){engines.Remove(car);engineFlows.Remove(car);}
            foreach (var car in RailSnowGameSource.GetCars())
            {
                if (car == null || !car.IsLoco) continue;
                bool batteryCar = car.carType == TrainCarType.LocoMicroshunter;
                if (car.SimController == null) continue;
                var flow = car.SimController.simFlow;
                if (flow == null) continue;
                RegisterEngines(car);
                if(!batteryCar)continue;
                foreach (var component in flow.OrderedSimComps)
                {
                    var SimBattery = component as SimBattery;
                    Pack pack;
                    if (SimBattery != null && batteryCar && !packs.TryGetValue(SimBattery, out pack))
                        packs.Add(SimBattery, new Pack { Temperature = ambient });
                }
            }
        }

        private void RegisterEngines(TrainCar car)
        {
            if(car==null || !car.IsLoco)return;
            var flow=car!=null && car.SimController!=null?car.SimController.simFlow:null;
            if(flow==null)return;
            SimulationFlow known;if(engineFlows.TryGetValue(car,out known) && ReferenceEquals(known,flow))return;
            engineFlows[car]=flow;
            List<SimComponent> list;
            if(!engines.TryGetValue(car,out list)){list=new List<SimComponent>();engines.Add(car,list);}else list.Clear();
            bool vanilla=CabEngineHeating.IsStockType(car.carType) && !CabEngineHeating.IsCustomLocomotive(car);
            foreach(var engine in flow.OrderedSimComps)
            {
                if(!(engine is DieselEnginePowerSource) && !(engine is DieselEngineDirectDrive) && !(engine is DieselEngineDirect))continue;
                var state=starts.GetOrCreateValue(engine);state.Car=car;state.Vanilla=vanilla;list.Add(engine);
                if(vanilla && car.carType==TrainCarType.LocoDiesel && engine is DieselEngineDirect)
                {
                    state.De6=true;
                    state.Primer=state.Throttle=null;
                    foreach(var port in flow.AllPorts)
                    {
                        if(port.id=="primerThrottle.EXT_IN")state.Primer=port;
                        else if(port.id=="throttle.EXT_IN")state.Throttle=port;
                    }
                }
            }
        }
        private bool Bypassed(SimComponent engine,StartState state)
        {
            if(!IgnoreVanillaColdStarts)return false;
            // A new engine may start before the next periodic fleet refresh.
            if(state.Car==null)foreach(var car in RailSnowGameSource.GetCars())
            {
                RegisterEngines(car);if(state.Car!=null)break;
            }
            return state.Vanilla;
        }
        private ColdStartHintState Hint(SimComponent engine,StartState state)
        {
            if(state.Car==null || Bypassed(engine,state))return default(ColdStartHintState);
            return new ColdStartHintState{CarId=state.Car.CarGUID,De6=state.De6,
                Stage=state.Idle.Active?ColdStartHintStage.Primer:state.Cranking?ColdStartHintStage.Starter:ColdStartHintStage.None,
                RemainingSeconds=state.Idle.Active?state.Idle.RemainingSeconds:Mathf.Clamp(state.Remaining,0,60)};
        }
        public ColdStartHintState GetHint(TrainCar car)
        {
            List<SimComponent> list;
            if(car!=null && !engines.ContainsKey(car))RegisterEngines(car);
            if(car==null || !engines.TryGetValue(car,out list))return default(ColdStartHintState);
            foreach(var engine in list)
            {var hint=Hint(engine,starts.GetOrCreateValue(engine));if(hint.Stage!=ColdStartHintStage.None)return hint;}
            return default(ColdStartHintState);
        }
        public ColdStartHintState[] CaptureHints()
        {
            hintSnapshot.Clear();
            foreach(var pair in engines)
            {
                var hint=GetHint(pair.Key);
                if(hint.Stage!=ColdStartHintStage.None && hint.IsValid())hintSnapshot.Add(hint);
                if(hintSnapshot.Count>=VehicleThermalNetworkState.MaxVehicles)break;
            }
            return hintSnapshot.Count==0?ColdStartHintState.Empty:hintSnapshot.ToArray();
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
            if (active == null || ___engineOn) return;
            var power = __instance as DieselEnginePowerSource;
            var drive = __instance as DieselEngineDirectDrive;
            var ignition = power != null ? power.ignitionExtIn : drive.ignitionExtIn;
            var fuse = power != null ? power.engineStarterFuseRef : drive.engineStarterFuseRef;
            if (!fuse.State || ignition.Value < .75f) return;
            var state = active.starts.GetOrCreateValue(__instance);
            float native = power != null ? power.ignitionTime : drive.ignitionMaxTime;
            if(active.Bypassed(__instance,state))
            {
                if(state.ColdAttempt && ___ignitionRequested)
                {___ignitionDelay=Mathf.Min(___ignitionDelay,native*.4f);___ignitionRpmBuildUpTime=Mathf.Min(___ignitionRpmBuildUpTime,native*.6f);}
                state.ColdAttempt=false;return;
            }
            if(___ignitionRequested)return;
            var temperature = EngineTemperature(__instance, power != null ? power.temperature : drive.temperature);
            state.ColdAttempt = active.ambient < 0 && temperature < 40;
            if (!state.ColdAttempt) return;
            float hold = ColdPowertrainProfile.StarterHoldSeconds(active.ambient, temperature, native);
            // Set only the new attempt's cranking parameters. Native fuel, fuse,
            // health, cancellation and engine-on logic still runs in Tick.
            ___ignitionRequested = true;
            ___engineRpm = 0;
            ___ignitionDelay = hold * .4f;
            ___ignitionRpmBuildUpTime = hold * .6f;
        }

        private static void TimedStarterState(SimComponent __instance,float delta,bool ___engineOn,
            bool ___ignitionRequested,float ___ignitionDelay,float ___ignitionRpmBuildUpTime,float ___engineRpm)
        {
            if(active==null)return;
            var state=active.starts.GetOrCreateValue(__instance);
            var power=__instance as DieselEnginePowerSource;var drive=__instance as DieselEngineDirectDrive;
            float idle=power!=null?power.engineRpmIdle:drive.engineRpmIdle;
            state.Cranking=state.ColdAttempt && ___ignitionRequested && !___engineOn && !active.Bypassed(__instance,state);
            state.Remaining=state.Cranking?Mathf.Max(0,___ignitionDelay)+___ignitionRpmBuildUpTime*Mathf.Clamp01(1-___engineRpm/Mathf.Max(.01f,idle)):0;
            if(!___ignitionRequested || ___engineOn)state.ColdAttempt=false;
            EngineWarmth(__instance,delta,___engineOn);
        }

        private static void DirectStarter(DieselEngineDirect __instance, float delta, bool ___engineOn,
            float ___engineRpmMin)
        {
            if (active == null) return;
            var state = active.starts.GetOrCreateValue(__instance);
            if(active.Bypassed(__instance,state)){state.ColdAttempt=state.Cranking=false;state.Held=0;return;}
            bool cranking = !___engineOn && __instance.engineStarterFuseRef.State && __instance.ignitionExtIn.Value > 0;
            if (!cranking) { state.Held = 0;state.Cranking=false; return; }
            if (state.Held <= 0)
            {
                float temperature = EngineTemperature(__instance, __instance.temperature);
                state.ColdAttempt = active.ambient < 0 && temperature < 40;
                state.Required = ColdPowertrainProfile.StarterHoldSeconds(active.ambient, temperature, 3.5f);
            }
            if (!state.ColdAttempt) return;
            state.Held += Mathf.Max(0, delta);
            state.Cranking=true;state.Remaining=Mathf.Max(0,state.Required-state.Held);
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
            if(active.Bypassed(__instance,state))
            {state.Idle.Reset();state.ColdAttempt=state.Cranking=false;state.WasRunning=___engineOn;EngineWarmth(__instance,delta,___engineOn);return;}
            if (___engineOn && !state.WasRunning && state.ColdAttempt && state.De6 && (state.Primer != null || state.Throttle != null))
            { state.Idle.Begin(); state.ColdAttempt = false; }
            else if (___engineOn && state.Idle.Advance(delta,
                state.Primer != null && state.Primer.Value > .1f || state.Throttle != null && state.Throttle.Value >= .9999f))
            {
                ___engineOn = false; __instance.engineOnReadOut.Value = 0;
                Debug.Log("[DVSeasons] Cold DE6 stalled: within 5 seconds, open the engine-room throttle or set the cab throttle to maximum; hold either for 3 seconds.");
            }
            if (!___engineOn) state.Idle.Reset();
            if(___engineOn)state.Cranking=false;
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
            engines.Clear();engineFlows.Clear();removedCars.Clear();hintSnapshot.Clear();
            if (active == this) active = null;
        }
        public void Dispose() { Reset(); }
    }
}
