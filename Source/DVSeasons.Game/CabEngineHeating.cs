using System;
using System.Collections.Generic;
using DV;
using DV.CabControls;
using DV.Openables;
using DV.ThingTypes;
using DVSeasons.Core;
using LocoSim.Implementations;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Custom cars can reuse a stock legacy enum. Compare the actual car type,
    // rather than treating every car labelled DE2/DE6 as its stock interior.
    internal sealed class CabEngineHeating
    {
        private sealed class Binding
        {
            public string CarId;
            public GameObject Interior;
            public ControlImplBase Control;
            public bool Known, HasSwitch;
            public float LastLevel;
            public float NextScan;
            public int Scans;
            public SimulationFlow Flow;
            public Port EngineOn, EngineRpm, EngineTemperature;
            public Port IdleRpm, MaximumRpm;
            public bool RpmIsNormalized = true;
            public PortReference TemperatureReference;
            public readonly EngineCabHeat Heat = new EngineCabHeat();
            public readonly CatenaryCabPower Electric = new CatenaryCabPower();
            public float LastUpdate = Time.time;
            public readonly WindowWinterClimate Climate = new WindowWinterClimate();
            public float LastClimate = Time.time;
            public GameObject OpeningsInterior, OpeningsExternal;
            public bool OpeningsReady;
            public OpenableControl[] Openables;
            public ControlImplBase[] OpeningControls;
            public DoorsAndWindowsController[] DoorsAndWindows;
            public float[] OpeningPositions;
            public float CabBoundary = float.NaN;
            public float NextDiagnostic;
        }
        private readonly Dictionary<TrainCar, Binding> bindings = new Dictionary<TrainCar, Binding>();
        private readonly List<TrainCar> expiredBindings = new List<TrainCar>();
        private readonly Dictionary<string, WindowClimateState> savedClimates = new Dictionary<string, WindowClimateState>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> retiredClimateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<TrainCarType> stockTypes = ReadStockTypes();

        private static HashSet<TrainCarType> ReadStockTypes()
        {
            var result = new HashSet<TrainCarType>();
            // CCL patches Enum.IsDefined to include its dynamically registered
            // car types, and adds them to the game's v2 lookup as well. Only CLR
            // enum literals identify the actual stock types in this boundary.
            foreach (var field in typeof(TrainCarType).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                if (field.IsLiteral) result.Add((TrainCarType)Convert.ToInt32(field.GetRawConstantValue()));
            return result;
        }

        public static bool IsCustomLocomotive(TrainCar car)
        {
            if (car == null || car.carLivery == null || car.carLivery.parentType == null || !car.IsLoco) return false;
            if (car.carType == TrainCarType.NotSet || !stockTypes.Contains(car.carType)) return true;
            TrainCarLivery stock = null;
            var globals = Globals.G;
            if (globals != null && globals.Types != null) globals.Types.TrainCarType_to_v2.TryGetValue(car.carType, out stock);
            return IsCustomType(car.carType, car.carLivery, stock);
        }
        internal static bool IsStockType(TrainCarType type) => type!=TrainCarType.NotSet && stockTypes.Contains(type);

        internal static bool IsCustomType(TrainCarType legacyType, TrainCarLivery livery, TrainCarLivery stock)
        { return legacyType == TrainCarType.NotSet || !stockTypes.Contains(legacyType) ||
            (stock != null && stock.parentType != livery.parentType); }

        public bool TryGetLevel(TrainCar car, bool enabled, out float level, out bool engineSource)
        {
            level = 0; engineSource = false;
            if (!IsCustomLocomotive(car)) return false;
            Binding binding;
            if (!bindings.TryGetValue(car, out binding))
            {
                // A replacement can be queried before the half-second cleanup.
                // Capture its destroyed predecessor before restoring this GUID.
                PruneDestroyed();
                binding = new Binding(); bindings.Add(car, binding);
                RefreshBindingIdentity(car, binding);
                binding.Electric.Bind(car, null);
                WindowClimateState saved;
                if (savedClimates.Count > 0 && !string.IsNullOrEmpty(binding.CarId) && savedClimates.TryGetValue(binding.CarId, out saved))
                    binding.Climate.Restore(saved);
                // The prefab establishes switch presence even when the interior
                // has never streamed in. An unloaded switch never enables fallback.
                var prefab = car.carLivery.interiorPrefab;
                if (prefab != null)
                { binding.Known = true; FindHeater(prefab, out binding.HasSwitch); }
            }
            else RefreshBindingIdentity(car, binding);
            var interior = car.loadedInterior;
            if (interior != null && interior != binding.Interior)
            {
                binding.Interior = interior;
                binding.Control = null;
                binding.Scans = 0; binding.NextScan = 0;
            }
            if (interior != null && binding.Control == null && binding.Scans < 3 && Time.time >= binding.NextScan)
            {
                bool foundSwitch;
                binding.Control = FindHeater(interior, out foundSwitch);
                binding.Known = true;
                binding.HasSwitch |= foundSwitch;
                binding.Scans++; binding.NextScan = Time.time + 2;
            }
            float engineHeat = EngineHeat(car, binding);
            if (binding.Control != null) binding.LastLevel = Mathf.Clamp01(binding.Control.Value);
            if (binding.Electric.Supported)
            {
                // A real switch still takes priority, but cannot energize a
                // heater while the main breaker is open or the supply is dead.
                level = binding.HasSwitch ? binding.LastLevel * binding.Electric.Power
                    : enabled && binding.Known ? binding.Electric.Power : 0;
                return true;
            }
            if (binding.HasSwitch) level = binding.LastLevel;
            else if (binding.Known)
            {
                if (enabled) { level = engineHeat; engineSource = true; }
            }
            return true;
        }

        public WindowWinterClimate GetClimate(TrainCar car, bool enabled, float outside, float snow)
        {
            float level; bool engineSource;
            if (!TryGetLevel(car, enabled, out level, out engineSource)) return null;
            var binding = bindings[car];
            var interior = car.loadedInterior; var external = car.loadedExternalInteractables;
            if (!binding.OpeningsReady || interior != binding.OpeningsInterior || external != binding.OpeningsExternal)
            {
                binding.OpeningsReady = true; binding.OpeningsInterior = interior; binding.OpeningsExternal = external;
                var openings = new HashSet<OpenableControl>();
                var controllers = new HashSet<DoorsAndWindowsController>();
                foreach (var root in new[] { car.gameObject, interior, external })
                    if (root != null)
                    {
                        foreach (var opening in root.GetComponentsInChildren<OpenableControl>(true)) openings.Add(opening);
                        foreach (var controller in root.GetComponentsInChildren<DoorsAndWindowsController>(true)) controllers.Add(controller);
                    }
                binding.DoorsAndWindows = new DoorsAndWindowsController[controllers.Count]; controllers.CopyTo(binding.DoorsAndWindows);
                binding.Openables = new OpenableControl[openings.Count]; openings.CopyTo(binding.Openables);
                binding.OpeningControls = new ControlImplBase[binding.Openables.Length];
                binding.OpeningPositions = new float[binding.Openables.Length];
                for (int i = 0; i < binding.Openables.Length; i++)
                {
                    binding.OpeningControls[i] = binding.Openables[i].GetComponent<ControlImplBase>();
                    binding.OpeningPositions[i] = car.transform.InverseTransformPoint(binding.Openables[i].transform.position).z;
                }
                binding.CabBoundary = CabOpeningScope.FindBoundary(binding.OpeningPositions);
            }
            var camera = PlayerManager.ActiveCamera;
            bool occupied = camera != null && PlayerManager.Car == car;
            int cabSide = CabOpeningScope.SelectSide(binding.CabBoundary, binding.OpeningPositions, occupied,
                occupied ? car.transform.InverseTransformPoint(camera.transform.position).z : float.NaN);
            bool open = AnythingOpen(binding, cabSide);
            bool running = binding.EngineOn != null ? binding.EngineOn.Value > .5f
                : binding.EngineRpm != null && binding.EngineRpm.Value > .05f;
            var sim = car.SimController;
            if (sim != null && sim.firebox != null && sim.firebox.IsFireOn) { running = true; level = Math.Max(level, .85f); }
            float elapsed = Mathf.Max(0, Time.time - binding.LastClimate); binding.LastClimate = Time.time;
            if (binding.Electric.Supported)
                binding.Climate.AdvanceElectricHeated(elapsed, outside, level, open, snow);
            else if (engineSource)
                binding.Climate.AdvanceEngineHeated(elapsed, outside, running,
                    EngineTemperature(binding, outside), level, open, snow);
            else
                binding.Climate.Advance(elapsed, outside, running,
                    EngineTemperature(binding, outside), level, open, snow);
            if (occupied && Time.realtimeSinceStartup >= binding.NextDiagnostic)
            {
                binding.NextDiagnostic = Time.realtimeSinceStartup + 60f;
                var doors = new System.Text.StringBuilder();
                for (int i = 0; i < binding.Openables.Length && i < 16; i++)
                {
                    var opening = binding.Openables[i]; var control = binding.OpeningControls[i];
                    if (opening == null) continue;
                    if (doors.Length > 0) doors.Append(", ");
                    doors.Append(opening.name).Append('=');
                    doors.Append(control != null ? control.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : "unbound");
                    doors.Append(CabOpeningScope.Includes(binding.OpeningPositions[i], binding.CabBoundary, cabSide) ? " local" : " other-cab");
                }
                Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[DVSeasons] Cab heat {0}: running={1}, rpm={2:F3}, source={3}, power={4:F3}, outside={5:F1}, air={6:F1}, glass={7:F1}, open={8}, cabSide={9}, switch={10}; doors=[{11}], supply={12:F1} V",
                    car.name, running, binding.EngineRpm != null ? binding.EngineRpm.Value : float.NaN,
                    binding.Electric.Supported ? "catenary" : engineSource ? "engine" : "switch/disabled", level, outside, binding.Climate.CabinTemperature,
                    binding.Climate.GlassTemperature, open, cabSide, binding.Control != null ? binding.Control.name : binding.HasSwitch ? "unloaded" : "none", doors, binding.Electric.Voltage));
            }
            return binding.Climate;
        }

        private static bool AnythingOpen(Binding binding, int cabSide)
        {
            bool open = false;
            foreach (var controller in binding.DoorsAndWindows)
            {
                if (controller == null || open) continue;
                // The aggregate reports the opposite cab as well. Its entries
                // are evaluated individually below when their scope is known.
                if (cabSide != 0 && ControllerCovered(controller, binding)) continue;
                try { open = controller.AnythingOpen(); }
                catch { /* Native entries can still be initializing; direct controls remain available below. */ }
            }
            for (int i = 0; i < binding.Openables.Length && !open; i++)
            {
                var opening = binding.Openables[i]; var control = binding.OpeningControls[i];
                if (opening == null || control == null) continue;
                if (!CabOpeningScope.Includes(binding.OpeningPositions[i], binding.CabBoundary, cabSide)) continue;
                open = opening.closedAtZero ? control.Value >= .1f : control.Value <= .9f;
            }
            return open;
        }

        private static bool ControllerCovered(DoorsAndWindowsController controller, Binding binding)
        {
            if (controller.entries == null) return false;
            foreach (var entry in controller.entries)
            {
                if (entry == null) continue;
                int index = Array.IndexOf(binding.Openables, entry);
                if (index < 0 || binding.OpeningControls[index] == null) return false;
            }
            return true;
        }

        private static float EngineHeat(TrainCar car, Binding binding)
        {
            var sim = car.SimController;
            var flow = sim != null ? sim.simFlow : null;
            if (flow != binding.Flow)
            {
                binding.Flow = flow; binding.EngineOn = binding.EngineRpm = binding.EngineTemperature = null;
                binding.IdleRpm = binding.MaximumRpm = null; binding.RpmIsNormalized = true; binding.TemperatureReference = null;
                binding.Electric.Bind(car, flow);
                if (binding.Electric.Supported) return 0;
                if (flow != null) foreach (var component in flow.OrderedSimComps)
                {
                    var field = component.GetType().GetField("engineOnReadOut");
                    if (field != null) binding.EngineOn = field.GetValue(component) as Port;
                    if (binding.EngineOn != null)
                    {
                        var temperature = component.GetType().GetField("temperature");
                        if (temperature != null) binding.TemperatureReference = temperature.GetValue(component) as PortReference;
                        var rpm = component.GetType().GetField("engineRpmNormalizedReadOut");
                        if (rpm != null) binding.EngineRpm = rpm.GetValue(component) as Port;
                        var idle = component.GetType().GetField("engineIdleRpmNormalizedReadOut");
                        if (idle != null) binding.IdleRpm = idle.GetValue(component) as Port;
                        var max = component.GetType().GetField("engineRpmMaxReadOut") ?? component.GetType().GetField("maxRpmReadOut");
                        if (max != null) binding.MaximumRpm = max.GetValue(component) as Port;
                        if (binding.EngineRpm == null)
                        {
                            rpm = component.GetType().GetField("engineRpmReadOut") ?? component.GetType().GetField("engineRpm");
                            if (rpm != null) { binding.EngineRpm = rpm.GetValue(component) as Port; binding.RpmIsNormalized = false; }
                        }
                        break;
                    }
                }
                if (flow != null) foreach (var port in flow.AllPorts)
                {
                    if (port == null || port.id == null) continue;
                    var name = port.id.Replace("_", "").Replace(".", "").Replace("-", "").ToLowerInvariant();
                    if (binding.EngineOn == null && name.Contains("engineon")) binding.EngineOn = port;
                    // Only a normalized live RPM value is a safe generic fallback.
                    // MAX_RPM and IDLE_RPM are constants, not running-engine signals.
                    if (binding.EngineRpm == null && name.Contains("enginerpm") && name.Contains("normalized") &&
                        !name.Contains("idle") && !name.Contains("max")) binding.EngineRpm = port;
                    if ((name.Contains("engine") || name.Contains("diesel")) && name.Contains("temperature")) binding.EngineTemperature = port;
                }
            }
            if (binding.Electric.Supported) return 0;
            bool running = binding.EngineOn != null ? binding.EngineOn.Value > .5f
                : binding.EngineRpm != null && binding.EngineRpm.Value > .05f;
            float elapsed = Mathf.Max(0, Time.time - binding.LastUpdate); binding.LastUpdate = Time.time;
            float rpmValue = binding.EngineRpm != null ? binding.EngineRpm.Value : float.NaN;
            if (!binding.RpmIsNormalized)
                rpmValue = binding.MaximumRpm != null && binding.MaximumRpm.Value > 1 ? rpmValue / binding.MaximumRpm.Value : float.NaN;
            return binding.Heat.AdvanceAtRpm(elapsed, running,
                EngineTemperature(binding, float.NaN), rpmValue, binding.IdleRpm != null ? binding.IdleRpm.Value : .25f);
        }

        private static float EngineTemperature(Binding binding, float fallback)
        { return binding.TemperatureReference != null && binding.TemperatureReference.IsConnected ? binding.TemperatureReference.Value
            : binding.EngineTemperature != null ? binding.EngineTemperature.Value : fallback; }

        private static ControlImplBase FindHeater(GameObject root, out bool hasSwitch)
        {
            hasSwitch = false;
            foreach (var part in root.GetComponentsInChildren<Transform>(true))
            {
                var name = part.name.Replace("_", "").Replace(" ", "").Replace("-", "").ToLowerInvariant();
                if (name.Contains("cabheater") || name.Contains("cabinheater") || name.Contains("heating") ||
                    name == "heater" || name == "cheater")
                {
                    var control = part.GetComponentInChildren<ControlImplBase>(true);
                    if (control != null) { hasSwitch = true; return control; }
                    // A named control placeholder can be populated during Start.
                    // A heater/radiator mesh alone does not count as a switch.
                    if (part.name.StartsWith("C_", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("switch") || name.Contains("toggle") || name.Contains("rotary"))
                        hasSwitch = true;
                }
            }
            return null;
        }

        public void RestoreClimates(List<CabFrostState> records)
        {
            savedClimates.Clear(); retiredClimateIds.Clear();
            if (records == null) return;
            foreach (var record in records)
                if (record != null && !string.IsNullOrEmpty(record.Id) && record.Id.Length <= 80 && record.Climate != null && record.Climate.IsValid())
                    savedClimates[record.Id] = record.Climate;
        }
        public void SaveClimates(IDictionary<string, WindowClimateState> destination)
        {
            // RestoreClimates receives native cabs too. Publish only retired
            // custom bindings here; copying all restored records would replace
            // newer native-cab temperatures already written by winter windows.
            foreach (var id in retiredClimateIds) destination[id] = savedClimates[id];
            foreach (var pair in bindings)
                if (RefreshBindingIdentity(pair.Key, pair.Value))
                    destination[pair.Value.CarId] = pair.Value.Climate.Capture();
        }
        private bool RefreshBindingIdentity(TrainCar car, Binding binding)
        {
            // Streaming can bind controls before the logic car gets its GUID.
            // Avoid the native getter's missing-logic warning, and retain the
            // last valid identity when a pooled/destroyed car loses its logic.
            if (car == null || car.logicCar == null) return false;
            string id = car.CarGUID;
            if (string.IsNullOrEmpty(id) || id.Length > 80) return false;
            binding.CarId = id;
            retiredClimateIds.Remove(id);
            return true;
        }
        public void PruneDestroyed()
        {
            expiredBindings.Clear();
            foreach (var pair in bindings)
            {
                // Unity keeps destroyed objects' managed wrappers alive. Do not
                // retain their interiors, controls and simulation ports for the
                // entire session; only the small climate state needs to survive.
                if (pair.Key != null) continue;
                var binding = pair.Value;
                if (!string.IsNullOrEmpty(binding.CarId) && binding.CarId.Length <= 80)
                {
                    savedClimates[binding.CarId] = binding.Climate.Capture();
                    retiredClimateIds.Add(binding.CarId);
                }
                expiredBindings.Add(pair.Key);
            }
            foreach (var car in expiredBindings) bindings.Remove(car);
            expiredBindings.Clear();
        }
        public void Clear() { bindings.Clear(); expiredBindings.Clear(); savedClimates.Clear(); retiredClimateIds.Clear(); }
    }
}
