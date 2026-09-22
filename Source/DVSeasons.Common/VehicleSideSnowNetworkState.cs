using System;
using System.IO;

namespace DVSeasons.Core
{
    // Snapshots are owned by their creator and never modified after publication.
    public struct VehicleSideSnowNetworkState
    {
        public const int MaxVehicleCount = 4096;
        public const int MaxCarIdLength = 64;
        public static readonly VehicleSideSnowNetworkState[] Empty = new VehicleSideSnowNetworkState[0];
        public string CarId;
        public float PositiveX;
        public float NegativeX;
        public float PositiveZ;
        public float NegativeZ;

        public bool IsValid()
        {
            if (string.IsNullOrEmpty(CarId) || CarId.Length > MaxCarIdLength ||
                !ValidAmount(PositiveX) || !ValidAmount(NegativeX) ||
                !ValidAmount(PositiveZ) || !ValidAmount(NegativeZ)) return false;
            // Car GUIDs and supported mod identifiers use ASCII. An explicit
            // bound keeps malformed packet strings from allocating arbitrary memory.
            for (int i = 0; i < CarId.Length; i++)
                if (CarId[i] < 33 || CarId[i] > 126) return false;
            return true;
        }

        private static bool ValidAmount(float amount)
        {
            return !float.IsNaN(amount) && !float.IsInfinity(amount) && amount >= 0f && amount <= 1f;
        }

        public static bool IsValid(VehicleSideSnowNetworkState[] states)
        {
            if (states == null || states.Length > MaxVehicleCount) return false;
            for (int i = 0; i < states.Length; i++) if (!states[i].IsValid()) return false;
            return true;
        }

        internal static void WriteTo(BinaryWriter writer, VehicleSideSnowNetworkState[] states)
        {
            if (!IsValid(states)) throw new InvalidDataException("Invalid vehicle side-snow snapshot.");
            writer.Write((ushort)states.Length);
            for (int i = 0; i < states.Length; i++)
            {
                writer.Write((byte)states[i].CarId.Length);
                for (int j = 0; j < states[i].CarId.Length; j++) writer.Write((byte)states[i].CarId[j]);
                writer.Write((ushort)Math.Round(states[i].PositiveX * ushort.MaxValue));
                writer.Write((ushort)Math.Round(states[i].NegativeX * ushort.MaxValue));
                writer.Write((ushort)Math.Round(states[i].PositiveZ * ushort.MaxValue));
                writer.Write((ushort)Math.Round(states[i].NegativeZ * ushort.MaxValue));
            }
        }

        internal static VehicleSideSnowNetworkState[] ReadFrom(BinaryReader reader)
        {
            int count = reader.ReadUInt16();
            if (count > MaxVehicleCount) throw new InvalidDataException("Too many vehicle side-snow entries.");
            if (count == 0) return Empty;
            var result = new VehicleSideSnowNetworkState[count];
            var characters = new char[MaxCarIdLength];
            for (int i = 0; i < count; i++)
            {
                int length = reader.ReadByte();
                if (length == 0 || length > MaxCarIdLength) throw new InvalidDataException("Invalid vehicle identifier length.");
                for (int j = 0; j < length; j++)
                {
                    byte value = reader.ReadByte();
                    if (value < 33 || value > 126) throw new InvalidDataException("Invalid vehicle identifier.");
                    characters[j] = (char)value;
                }
                result[i].CarId = new string(characters, 0, length);
                result[i].PositiveX = reader.ReadUInt16() / (float)ushort.MaxValue;
                result[i].NegativeX = reader.ReadUInt16() / (float)ushort.MaxValue;
                result[i].PositiveZ = reader.ReadUInt16() / (float)ushort.MaxValue;
                result[i].NegativeZ = reader.ReadUInt16() / (float)ushort.MaxValue;
            }
            return result;
        }
    }
}
