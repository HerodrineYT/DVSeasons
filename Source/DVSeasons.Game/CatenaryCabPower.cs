using System;
using LocoSim.Implementations;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Explicit 2WE3-981 / Catenary-DC contract. Electric locomotives do not have
    // universal simulation ports; never infer overhead power from diesel RPM,
    // battery voltage, traction current, or the main-breaker sound trigger.
    internal sealed class CatenaryCabPower
    {
        private Fuse breaker;
        private Port supplyVoltage;
        private Port relativeVoltage;
        public bool Supported { get; private set; }
        public float Voltage { get { return supplyVoltage != null ? supplyVoltage.Value
            : relativeVoltage != null ? relativeVoltage.Value * 1500f : 0; } }
        public float Power
        {
            get
            {
                if (!Supported || breaker == null || !breaker.State) return 0;
                float voltage = Voltage;
                if (float.IsNaN(voltage) || float.IsInfinity(voltage) || voltage <= 10f) return 0;
                // Resistive heating scales with voltage squared. The tiny
                // residual voltage of a disconnected supply cannot keep it on.
                voltage = Mathf.Clamp01(voltage / 1500f);
                return voltage * voltage;
            }
        }

        public void Bind(TrainCar car, SimulationFlow flow)
        {
            breaker = null; supplyVoltage = null; relativeVoltage = null;
            var livery = car != null ? car.carLivery : null;
            Supported = livery != null && (IsUnit(livery.id) ||
                (livery.parentType != null && IsUnit(livery.parentType.id)));
            if (!Supported || flow == null) return;
            // TryGetPort/TryGetFuse log errors when a port is missing. Scan once
            // per flow instead so absent/changed optional mods remain quiet.
            if (flow.AllFuses != null) foreach (var fuse in flow.AllFuses)
                if (fuse != null && fuse.id == "[MainBreakerContacts].CLOSED") { breaker = fuse; break; }
            if (flow.AllPorts != null) foreach (var port in flow.AllPorts)
            {
                if (port == null) continue;
                if (port.id == "[CustomGauges].SUPPLY") supplyVoltage = port;
                else if (port.id == "[CustomSimulation].RELATIVE_SUPPLY_VOLTAGE") relativeVoltage = port;
            }
        }

        private static bool IsUnit(string id)
        { return id != null && (id.StartsWith("WE6981A", StringComparison.Ordinal) ||
            id.StartsWith("WE6981B", StringComparison.Ordinal)); }
    }
}
