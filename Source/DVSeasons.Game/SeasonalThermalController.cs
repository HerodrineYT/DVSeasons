using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using DV.Simulation.Brake;
using DVSeasons.Core;
using HarmonyLib;
using LocoSim.Implementations;
using UnityEngine;

namespace DVSeasons.Mod
{
    /// <summary>
    /// Replaces DV99's fixed 25 C environment in the real simulation components.
    /// No temperatures, damage thresholds or UI readouts are overwritten outside
    /// those components, so the native save state and failure model remain intact.
    /// </summary>
    internal sealed class SeasonalThermalController : IDisposable
    {
        private const string HarmonyId = "Herodrine.DVSeasons.SeasonalThermal";
        private static SeasonalThermalController active;

        private readonly Harmony harmony = new Harmony(HarmonyId);
        private readonly ColdPowertrainController coldPowertrain = new ColdPowertrainController();
        private readonly ColdStartHintController startHints = new ColdStartHintController();
        public void ConfigureStarting(bool ignoreVanillaColdStarts) { coldPowertrain.IgnoreVanillaColdStarts=ignoreVanillaColdStarts; }
        public void UpdateStartHints(bool enabled,bool localAuthority)
        {using(SnowPerformance.Measure("cold-start-hints"))startHints.Update(enabled,localAuthority,coldPowertrain);}
        public void ReceiveStartHints(ColdStartHintState[] hints) { startHints.Receive(hints); }
        public ColdStartHintState[] CaptureStartHints() { return coldPowertrain.CaptureHints(); }
        private float ambientCelsius = SeasonalThermalProfile.VanillaAmbientCelsius;
        private bool installed;
        private bool simulationEnabled = true;
        private bool failed;
        private readonly Harmony lampHarmony = new Harmony(HarmonyId + ".Lamps");
        private bool lampsInstalled;

        public void EnableLampProtection()
        {
            if (lampsInstalled) return;
            lampHarmony.Patch(AccessTools.Method(typeof(LampLogic), "UpdateLampState"),
                transpiler: new HarmonyMethod(typeof(SeasonalThermalController), nameof(LampInput)));
            lampsInstalled = true;
        }
        public void DisableLampProtection()
        {
            if (lampsInstalled) lampHarmony.UnpatchAll(HarmonyId + ".Lamps");
            lampsInstalled = false;
        }

        public void Apply(float temperatureCelsius, bool simulateLocally = true)
        {
            // MP 0.1.16 skips native SimComponent.Tick on clients, but Harmony
            // postfixes still run. Never let our starter/brake/heat hooks write
            // over the host's replicated simulation there. Lamp protection is
            // visual-only and stays installed for sub-zero readouts.
            if (!simulateLocally)
            {
                if (simulationEnabled) Reset();
                simulationEnabled = false;
                return;
            }
            simulationEnabled = true;
            ambientCelsius = SeasonalThermalProfile.NormalizeAmbient(temperatureCelsius);
            coldPowertrain.Apply(ambientCelsius);
            if (failed) return;
            active = this;
            if (installed) return;
            try
            {
                foreach (var cooler in new[] { typeof(PassiveCooler), typeof(ActiveCooler),
                    typeof(AutomaticCooler), typeof(DirectionalMovementCooler) })
                    PatchTranspiler(cooler, "Tick", nameof(CoolerTarget));
                EnableLampProtection();
                PatchPrefix(typeof(HeatReservoir), "Tick", nameof(RepairInvalidReservoir));
                PatchPostfixConstructor(typeof(HeatReservoir), nameof(HeatReservoirConstructed));
                PatchTranspiler(typeof(Boiler), "SimulateSteamConsumption", nameof(BoilerAmbient));
                PatchTranspiler(typeof(Firebox), "ComputeTemperature", nameof(FireboxTemperature));
                PatchPrefix(typeof(BrakeSystem.HeatController), nameof(BrakeSystem.HeatController.UpdateTemperature),
                    nameof(BrakeTemperature));
                PatchTranspiler(typeof(BrakeSystem.HeatController), nameof(BrakeSystem.HeatController.ResetState),
                    nameof(BrakeResetAmbient));
                installed = true;
                Debug.Log("[DVSeasons] Seasonal locomotive thermal physics active.");
            }
            catch (Exception exception)
            {
                harmony.UnpatchAll(HarmonyId);
                installed = false;
                failed = true;
                if (active == this) active = null;
                Debug.LogWarning("[DVSeasons] Seasonal thermal physics disabled because the DV99 " +
                    "simulation layout was not recognized: " + exception.Message);
            }
        }

        private void PatchPostfixConstructor(Type type, string patchName)
        {
            var target = AccessTools.Constructor(type, new[] { typeof(LocoSim.Definitions.HeatReservoirDefinition) });
            var patch = AccessTools.Method(typeof(SeasonalThermalController), patchName);
            if (target == null || patch == null) throw new MissingMethodException(type.FullName, ".ctor");
            harmony.Patch(target, postfix: new HarmonyMethod(patch));
        }

        private void PatchPrefix(Type type, string methodName, string patchName)
        {
            var target = AccessTools.Method(type, methodName);
            var patch = AccessTools.Method(typeof(SeasonalThermalController), patchName);
            if (target == null || patch == null) throw new MissingMethodException(type.FullName, methodName);
            harmony.Patch(target, prefix: new HarmonyMethod(patch));
        }

        private void PatchTranspiler(Type type, string methodName, string patchName)
        {
            var target = AccessTools.Method(type, methodName);
            var patch = AccessTools.Method(typeof(SeasonalThermalController), patchName);
            if (target == null || patch == null) throw new MissingMethodException(type.FullName, methodName);
            harmony.Patch(target, transpiler: new HarmonyMethod(patch));
        }

        private static float CurrentAmbient()
        {
            return active == null
                ? SeasonalThermalProfile.VanillaAmbientCelsius
                : active.ambientCelsius;
        }

        private static IEnumerable<CodeInstruction> CoolerTarget(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceReferenceRead(instructions, "targetTemperature", nameof(ReadCoolerTarget));
        }

        // A connected target is another reservoir (oil/coolant/engine), NOT air.
        // Correct the input before the native calculation and publish heat once.
        private static float ReadCoolerTarget(PortReference target)
        {
            return active != null && !target.IsConnected ? CurrentAmbient() : target.Value;
        }

        private static IEnumerable<CodeInstruction> LampInput(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceReferenceRead(instructions, "inputReader", nameof(ReadLampInput));
        }

        private static readonly AccessTools.FieldRef<PortReference, Port> ConnectedPort =
            AccessTools.FieldRefAccess<PortReference, Port>("port");

        private static float ReadLampInput(PortReference input)
        {
            var value = input.Value;
            var port = ConnectedPort(input);
            // DV's temperature lamp ranges start at zero. Classify sub-zero oil
            // in the cold range without clamping the actual simulation/gauges.
            return port != null &&
                port.valueType == LocoSim.Definitions.PortValueType.TEMPERATURE && value < 0
                ? 0 : value;
        }

        private static IEnumerable<CodeInstruction> ReplaceReferenceRead(
            IEnumerable<CodeInstruction> instructions, string field, string method)
        {
            var result = new List<CodeInstruction>(instructions);
            var getter = AccessTools.PropertyGetter(typeof(PortReference), "Value");
            int count = 0;
            for (int i = 1; i < result.Count; i++)
                if (result[i].Calls(getter) && result[i - 1].opcode == OpCodes.Ldfld &&
                    ((System.Reflection.FieldInfo)result[i - 1].operand).Name == field)
                {
                    result[i].opcode = OpCodes.Call;
                    result[i].operand = AccessTools.Method(typeof(SeasonalThermalController), method);
                    count++;
                }
            if (count != 1) throw new InvalidOperationException("Unsupported reference read: " + field);
            return result;
        }

        private static void RepairInvalidReservoir(HeatReservoir __instance)
        {
            var t = __instance.temperature.Value;
            // Recover saves affected by the old extra cooling on internal heat
            // exchangers. Do not reset healthy temperatures when seasons change.
            if (active != null && (float.IsNaN(t) || float.IsInfinity(t) || t < -40f))
                __instance.temperature.Value = CurrentAmbient();
        }

        private static void HeatReservoirConstructed(HeatReservoir __instance)
        {
            if (active == null || __instance == null) return;
            // Newly streamed vehicles have been standing in the current climate.
            // A saved temperature, when present, is restored later by DV itself.
            __instance.temperature.Value = CurrentAmbient();
        }

        private static IEnumerable<CodeInstruction> BoilerAmbient(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceSingleVanillaAmbient(instructions, "Boiler.SimulateSteamConsumption");
        }

        private static IEnumerable<CodeInstruction> BrakeResetAmbient(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceSingleVanillaAmbient(instructions, "BrakeSystem.HeatController.ResetState");
        }

        private static IEnumerable<CodeInstruction> ReplaceSingleVanillaAmbient(
            IEnumerable<CodeInstruction> instructions, string owner)
        {
            var result = new List<CodeInstruction>();
            var ambient = AccessTools.Method(typeof(SeasonalThermalController), nameof(CurrentAmbient));
            var replacements = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float &&
                    Math.Abs((float)instruction.operand - SeasonalThermalProfile.VanillaAmbientCelsius) < 0.0001f)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = ambient;
                    replacements++;
                }
                result.Add(instruction);
            }
            if (replacements != 1)
                throw new InvalidOperationException("Unsupported " + owner + " ambient constants: " + replacements);
            return result;
        }

        private static IEnumerable<CodeInstruction> FireboxTemperature(
            IEnumerable<CodeInstruction> instructions)
        {
            var result = new List<CodeInstruction>();
            var lerp = AccessTools.Method(typeof(Mathf), nameof(Mathf.Lerp),
                new[] { typeof(float), typeof(float), typeof(float) });
            var adjust = AccessTools.Method(typeof(SeasonalThermalController),
                nameof(AdjustFireboxTarget));
            var responseField = AccessTools.Field(typeof(Firebox), "temperatureSmoothTime");
            var responseAdjust = AccessTools.Method(typeof(SeasonalThermalController),
                nameof(AdjustFireboxResponseTime));
            var targetInsertions = 0;
            var responseInsertions = 0;
            foreach (var instruction in instructions)
            {
                result.Add(instruction);
                if (instruction.Calls(lerp))
                {
                    result.Add(new CodeInstruction(OpCodes.Call, adjust));
                    targetInsertions++;
                }
                if (instruction.opcode == OpCodes.Ldfld && Equals(instruction.operand, responseField))
                {
                    result.Add(new CodeInstruction(OpCodes.Ldarg_0));
                    result.Add(new CodeInstruction(OpCodes.Call, responseAdjust));
                    responseInsertions++;
                }
            }
            if (targetInsertions != 1 || responseInsertions != 1)
                throw new InvalidOperationException("Unsupported Firebox.ComputeTemperature layout: target=" +
                    targetInsertions + ", response=" + responseInsertions);
            return result;
        }

        private static float AdjustFireboxTarget(float vanillaTarget)
        {
            return active == null ? vanillaTarget :
                SeasonalThermalProfile.AdjustFireboxCombustionTarget(vanillaTarget, CurrentAmbient());
        }

        private static float AdjustFireboxResponseTime(float vanillaSeconds, Firebox firebox)
        {
            return active == null || firebox == null || firebox.fireOnReadOut.Value < 0.5f
                ? vanillaSeconds :
                SeasonalThermalProfile.AdjustFireboxResponseTime(vanillaSeconds, CurrentAmbient());
        }

        private static bool BrakeTemperature(BrakeSystem.HeatController __instance,
            float brakingFactor, float dt, Action<bool> ___OverheatingActiveStateChanged)
        {
            if (active == null || __instance == null) return true;
            var oldOverheat = __instance.overheatPercentage;
            __instance.temperature = SeasonalThermalProfile.StepBrakeTemperature(
                __instance.temperature, __instance.currentAbsSpeed, brakingFactor, dt, CurrentAmbient());
            __instance.overheatPercentage = SeasonalThermalProfile.BrakeOverheatFraction(__instance.temperature);
            __instance.overheatReductionFactor = SeasonalThermalProfile.BrakeForceFactor(__instance.temperature);
            if (oldOverheat == 0f && __instance.overheatPercentage > 0f)
                ___OverheatingActiveStateChanged?.Invoke(true);
            else if (oldOverheat > 0f && __instance.overheatPercentage == 0f)
                ___OverheatingActiveStateChanged?.Invoke(false);
            return false;
        }

        public void Reset()
        {
            startHints.Dispose();
            coldPowertrain.Reset();
            if (installed) harmony.UnpatchAll(HarmonyId);
            installed = false;
            ambientCelsius = SeasonalThermalProfile.VanillaAmbientCelsius;
            if (active == this) active = null;
        }

        public void Dispose()
        {
            Reset();
            DisableLampProtection();
        }
    }
}

