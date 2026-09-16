using System;
using System.IO;

namespace DVSeasons.Core
{
    // Values contain the host's manual overrides before seasonal modifiers.
    // Slots: rain, thunder, wetness, wind speed/direction, weather X/Y, hour, day length.
    public sealed class WeatherNetworkState
    {
        public const int ValueCount = 9;
        public const ushort AllOverridesMask = (1 << ValueCount) - 1;

        public bool Available { get; set; }
        public ushort Overrides { get; set; }
        public float[] Values { get; set; } = new float[ValueCount];
        public long RealDateTimeTicks { get; set; }
        public uint TimeRevision { get; set; }
        public bool SeasonalDaylight { get; set; }
        public bool SeasonalPrecipitation { get; set; }
        public bool WinterAdhesion { get; set; }
        public bool DisableWinterThunder { get; set; }
        public bool RespectExternalWetnessOverride { get; set; }
        public float BaseDayLengthInMinutes { get; set; } = 120f;

        public WeatherNetworkState Clone()
        {
            var result = (WeatherNetworkState)MemberwiseClone();
            result.Values = Values == null ? null : (float[])Values.Clone();
            return result;
        }

        public bool IsValid()
        {
            if (!Available) return true;
            if ((Overrides & ~AllOverridesMask) != 0 || Values == null || Values.Length != ValueCount ||
                RealDateTimeTicks < DateTime.MinValue.Ticks || RealDateTimeTicks > DateTime.MaxValue.Ticks ||
                !InRange(BaseDayLengthInMinutes, .01f, 100000f)) return false;
            for (int i = 0; i < ValueCount; i++)
            {
                float value = Values[i];
                if (float.IsNaN(value) || float.IsInfinity(value)) return false;
                if ((Overrides & (1 << i)) == 0) continue;
                float min = i == 8 ? .01f : 0f;
                float max = i == 3 ? 10f : i == 4 ? 360f : i == 7 ? 24f : i == 8 ? 100000f : 1f;
                if (!InRange(value, min, max)) return false;
            }
            return true;
        }

        public void WriteTo(BinaryWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.Write(Available);
            if (!Available) return;
            if (Values == null || Values.Length != ValueCount)
                throw new InvalidDataException("Weather snapshot must contain nine override values.");
            writer.Write(Overrides);
            for (int i = 0; i < ValueCount; i++) writer.Write(Values[i]);
            writer.Write(RealDateTimeTicks);
            writer.Write(TimeRevision);
            writer.Write(SeasonalDaylight);
            writer.Write(SeasonalPrecipitation);
            writer.Write(WinterAdhesion);
            writer.Write(DisableWinterThunder);
            writer.Write(RespectExternalWetnessOverride);
            writer.Write(BaseDayLengthInMinutes);
        }

        public static WeatherNetworkState ReadFrom(BinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            var result = new WeatherNetworkState { Available = reader.ReadBoolean() };
            if (!result.Available) return result;
            result.Overrides = reader.ReadUInt16();
            for (int i = 0; i < ValueCount; i++) result.Values[i] = reader.ReadSingle();
            result.RealDateTimeTicks = reader.ReadInt64();
            result.TimeRevision = reader.ReadUInt32();
            result.SeasonalDaylight = reader.ReadBoolean();
            result.SeasonalPrecipitation = reader.ReadBoolean();
            result.WinterAdhesion = reader.ReadBoolean();
            result.DisableWinterThunder = reader.ReadBoolean();
            result.RespectExternalWetnessOverride = reader.ReadBoolean();
            result.BaseDayLengthInMinutes = reader.ReadSingle();
            return result;
        }

        private static bool InRange(float value, float min, float max)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;
        }
    }
}
