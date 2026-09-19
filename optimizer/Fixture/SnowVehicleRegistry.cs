using System.Collections.Generic;

namespace DVSeasons.Mod
{
    public sealed class SnowVehicleRegistry
    {
        internal sealed class LodSet
        {
            public int Current;
            public bool FilterInterior;
        }

        internal sealed class Part
        {
            public LodSet Lod;
            public int LodMask;
            public bool Interior;
        }

        internal sealed class Vehicle
        {
            public readonly List<Part> Parts = new List<Part>();
            public int Signature = 1;
        }

        private readonly List<Vehicle> vehicles = new List<Vehicle>();
        public bool PartVehicleBatchesEnabled = true;
        private int observedPartCount;
        private bool observedPartScheduler;

        public SnowVehicleRegistry()
        {
            var lod = new LodSet { Current = 1, FilterInterior = false };
            var vehicle = new Vehicle();
            vehicle.Parts.Add(new Part());
            vehicle.Parts.Add(new Part { Lod = lod, LodMask = 1 << 0 });
            vehicle.Parts.Add(new Part { Lod = lod, LodMask = 1 << 1 });
            vehicle.Parts.Add(new Part { Lod = lod, LodMask = 1 << 0, Interior = true });
            vehicles.Add(vehicle);
        }

        private bool SelectPartBatching()
        {
            return PartVehicleBatchesEnabled;
        }

        private static void UpdateLods(Vehicle vehicle)
        {
            // Current is already 1. The optimizer postfix must run after this
            // method and before RecordCore observes the Parts list.
        }

        private void RecordCore()
        {
            UpdateLods(vehicles[0]);
            observedPartCount = vehicles[0].Parts.Count;
            observedPartScheduler = SelectPartBatching();
        }

        public string Verify()
        {
            RecordCore();
            var restored = vehicles[0].Parts.Count;
            return observedPartCount + ":" + restored + ":" + observedPartScheduler + ":" + PartVehicleBatchesEnabled;
        }
    }
}
