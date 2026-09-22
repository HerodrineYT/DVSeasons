using System;
using System.Collections.Generic;
using System.IO;

namespace DVSeasons.Core
{
    // Value-only snapshots: a bridge can cache/copy these without sharing mutable
    // climate objects with the simulation. Sent once a second, never per frame.
    public struct VehicleThermalNetworkState
    {
        public const int MaxVehicles = 1024;
        public const int MaxIdLength = 80;
        public static readonly VehicleThermalNetworkState[] Empty = new VehicleThermalNetworkState[0];
        public string CarId;
        public bool HasClimate, Initialized;
        public float EngineWarmth, Heater, Cabin, Glass, Frost, Fog, MeltedSnow;

        public static VehicleThermalNetworkState Capture(string id, WindowWinterClimate climate, float melted)
        {
            var result = new VehicleThermalNetworkState { CarId = id, MeltedSnow = melted };
            if (climate == null) return result;
            var state = climate.Capture();
            result.HasClimate = true; result.Initialized = state.Initialized;
            result.EngineWarmth = state.EngineWarmth; result.Heater = state.Heater;
            result.Cabin = state.Cabin; result.Glass = state.Glass;
            result.Frost = state.Frost; result.Fog = state.Fog;
            return result;
        }

        public WindowClimateState ClimateState()
        {
            return HasClimate ? new WindowClimateState { Initialized = Initialized,
                EngineWarmth = EngineWarmth, Heater = Heater, Cabin = Cabin,
                Glass = Glass, Frost = Frost, Fog = Fog } : null;
        }

        public bool IsValid()
        {
            if (string.IsNullOrEmpty(CarId) || CarId.Length > MaxIdLength || !Unit(MeltedSnow)) return false;
            for (int i = 0; i < CarId.Length; i++) if (CarId[i] < 33 || CarId[i] > 126) return false;
            return !HasClimate || (Unit(EngineWarmth) && Unit(Frost) && Unit(Fog) &&
                Temperature(Heater) && Temperature(Cabin) && Temperature(Glass));
        }
        private static bool Unit(float v) { return v >= 0 && v <= 1; }
        private static bool Temperature(float v) { return v >= -60 && v <= 150; }

        public static bool IsValid(VehicleThermalNetworkState[] states)
        {
            if (states == null || states.Length > MaxVehicles) return false;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var state in states) if (!state.IsValid() || !ids.Add(state.CarId)) return false;
            return true;
        }

        internal static void WriteTo(BinaryWriter writer, VehicleThermalNetworkState[] states)
        {
            if (!IsValid(states)) throw new InvalidDataException("Invalid vehicle thermal snapshot.");
            writer.Write((ushort)states.Length);
            foreach (var state in states)
            {
                writer.Write((byte)state.CarId.Length);
                foreach (char c in state.CarId) writer.Write((byte)c);
                writer.Write(state.MeltedSnow);
                writer.Write((byte)((state.HasClimate ? 1 : 0) | (state.Initialized ? 2 : 0)));
                if (!state.HasClimate) continue;
                writer.Write(state.EngineWarmth); writer.Write(state.Heater);
                writer.Write(state.Cabin); writer.Write(state.Glass);
                writer.Write(state.Frost); writer.Write(state.Fog);
            }
        }

        internal static VehicleThermalNetworkState[] ReadFrom(BinaryReader reader)
        {
            int count = reader.ReadUInt16();
            if (count > MaxVehicles) throw new InvalidDataException("Too many vehicle thermal entries.");
            if (count == 0) return Empty;
            var result = new VehicleThermalNetworkState[count];
            var chars = new char[MaxIdLength];
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadByte();
                if (length == 0 || length > MaxIdLength) throw new InvalidDataException("Invalid thermal vehicle identifier.");
                for (int j = 0; j < length; j++)
                {
                    byte c = reader.ReadByte();
                    if (c < 33 || c > 126) throw new InvalidDataException("Invalid thermal vehicle identifier.");
                    chars[j] = (char)c;
                }
                result[i].CarId = new string(chars, 0, length);
                result[i].MeltedSnow = reader.ReadSingle();
                byte flags = reader.ReadByte();
                if (flags > 3) throw new InvalidDataException("Invalid thermal snapshot flags.");
                result[i].HasClimate = (flags & 1) != 0; result[i].Initialized = (flags & 2) != 0;
                if (!result[i].HasClimate) continue;
                result[i].EngineWarmth = reader.ReadSingle(); result[i].Heater = reader.ReadSingle();
                result[i].Cabin = reader.ReadSingle(); result[i].Glass = reader.ReadSingle();
                result[i].Frost = reader.ReadSingle(); result[i].Fog = reader.ReadSingle();
            }
            if (!IsValid(result)) throw new InvalidDataException("Invalid vehicle thermal snapshot.");
            return result;
        }
    }
}
