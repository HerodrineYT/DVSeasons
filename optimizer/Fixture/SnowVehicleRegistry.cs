using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
            public string Name;
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
        private int observedFirst;
        private int observedSecond;
        private bool observedPartScheduler;
        private bool originalReferenceRestored;

        public SnowVehicleRegistry()
        {
            var lod = new LodSet { Current = 1, FilterInterior = false };
            var vehicle = new Vehicle();
            vehicle.Parts.Add(new Part { Name = "always" });
            vehicle.Parts.Add(new Part { Name = "lod0", Lod = lod, LodMask = 1 << 0 });
            vehicle.Parts.Add(new Part { Name = "lod1", Lod = lod, LodMask = 1 << 1 });
            vehicle.Parts.Add(new Part { Name = "interior", Lod = lod, LodMask = 1 << 0, Interior = true });
            vehicles.Add(vehicle);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool SelectPartBatching()
        {
            return PartVehicleBatchesEnabled;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void UpdateLods(Vehicle vehicle)
        {
            if (vehicle == null) throw new ArgumentNullException(nameof(vehicle));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RecordCore()
        {
            var original = vehicles[0].Parts;

            UpdateLods(vehicles[0]);
            observedFirst = vehicles[0].Parts.Count;
            observedPartScheduler = SelectPartBatching();

            // Same LOD on a second call should reuse the cached active bucket.
            originalReferenceRestored = false;
        }

        public string Verify()
        {
            var vehicle = vehicles[0];
            var original = vehicle.Parts;

            RecordCore();
            originalReferenceRestored = ReferenceEquals(original, vehicle.Parts) && vehicle.Parts.Count == 4;

            RecordCore();
            observedSecond = vehicle.Parts.Count;

            return observedFirst + ":" + original.Count + ":" + observedPartScheduler + ":" +
                   PartVehicleBatchesEnabled + ":" + originalReferenceRestored + ":" + observedSecond;
        }
    }
}
