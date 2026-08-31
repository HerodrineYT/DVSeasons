using System;
using System.IO;

namespace DVSeasons.Core
{
    public sealed class SeasonNetworkState
    {
        public const int CurrentProtocol = 6;
        public int Protocol { get; set; }
        public uint Sequence { get; set; }
        public double Phase { get; set; }
        public SeasonKind Current { get; set; }
        public SeasonKind Next { get; set; }
        public float Transition { get; set; }
        public float SnowAmount { get; set; }
        public float TemperatureCelsius { get; set; }
        public float WinterWetnessEquivalent { get; set; }
        public float DaysPerSeason { get; set; }
        public bool RandomTransitionDuration { get; set; }
        public float TransitionDays { get; set; }
        public int TransitionSeason { get; set; }
        public float RainIntensity { get; set; }
        public float WindVelocityX { get; set; }
        public float WindVelocityZ { get; set; }
        public float SnowLightFactor { get; set; }

        public static SeasonNetworkState FromState(SeasonState state, float daysPerSeason = 1f,
            float transitionDays = 1f, float rainIntensity = 0f, float windVelocityX = 0f,
            float windVelocityZ = 0f, float snowLightFactor = 1f,
            int transitionSeason = -1, bool randomTransitionDuration = true)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return new SeasonNetworkState
            {
                Protocol = CurrentProtocol,
                Phase = state.Phase,
                Current = state.Current,
                Next = state.Next,
                Transition = state.Transition,
                SnowAmount = state.SnowAmount,
                TemperatureCelsius = state.TemperatureCelsius,
                WinterWetnessEquivalent = state.WinterWetnessEquivalent,
                DaysPerSeason = Math.Max(1f, Math.Min(365f, daysPerSeason)),
                RandomTransitionDuration = randomTransitionDuration,
                TransitionDays = Math.Max(1f, Math.Min(5f, transitionDays)),
                TransitionSeason = transitionSeason >= 0 && transitionSeason <= 3
                    ? transitionSeason
                    : (int)state.Current,
                RainIntensity = Clamp(rainIntensity, 0f, 1f),
                WindVelocityX = Clamp(windVelocityX, -20f, 20f),
                WindVelocityZ = Clamp(windVelocityZ, -20f, 20f),
                SnowLightFactor = Clamp(snowLightFactor, 0f, 1f)
            };
        }

        public bool IsValid()
        {
            return Protocol == CurrentProtocol && !double.IsNaN(Phase) && !double.IsInfinity(Phase) &&
                Phase >= 0d && Phase < 4d && Enum.IsDefined(typeof(SeasonKind), Current) &&
                Enum.IsDefined(typeof(SeasonKind), Next) && IsFiniteInRange(Transition, 0f, 1f) &&
                IsFiniteInRange(SnowAmount, 0f, 1f) && IsFiniteInRange(TemperatureCelsius, -100f, 100f) &&
                IsFiniteInRange(WinterWetnessEquivalent, 0f, 0.5f) &&
                IsFiniteInRange(DaysPerSeason, 1f, 365f) &&
                IsFiniteInRange(TransitionDays, 1f, Math.Min(5f, DaysPerSeason)) &&
                TransitionSeason >= 0 && TransitionSeason <= 3 &&
                IsFiniteInRange(RainIntensity, 0f, 1f) &&
                IsFiniteInRange(WindVelocityX, -20f, 20f) &&
                IsFiniteInRange(WindVelocityZ, -20f, 20f) &&
                IsFiniteInRange(SnowLightFactor, 0f, 1f);
        }

        public void WriteTo(BinaryWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.Write(Protocol);
            writer.Write(Sequence);
            writer.Write(Phase);
            writer.Write((byte)Current);
            writer.Write((byte)Next);
            writer.Write(Transition);
            writer.Write(SnowAmount);
            writer.Write(TemperatureCelsius);
            writer.Write(WinterWetnessEquivalent);
            writer.Write(DaysPerSeason);
            writer.Write(RandomTransitionDuration);
            writer.Write(TransitionDays);
            writer.Write(TransitionSeason);
            writer.Write(RainIntensity);
            writer.Write(WindVelocityX);
            writer.Write(WindVelocityZ);
            writer.Write(SnowLightFactor);
        }

        public static SeasonNetworkState ReadFrom(BinaryReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            return new SeasonNetworkState
            {
                Protocol = reader.ReadInt32(),
                Sequence = reader.ReadUInt32(),
                Phase = reader.ReadDouble(),
                Current = (SeasonKind)reader.ReadByte(),
                Next = (SeasonKind)reader.ReadByte(),
                Transition = reader.ReadSingle(),
                SnowAmount = reader.ReadSingle(),
                TemperatureCelsius = reader.ReadSingle(),
                WinterWetnessEquivalent = reader.ReadSingle(),
                DaysPerSeason = reader.ReadSingle(),
                RandomTransitionDuration = reader.ReadBoolean(),
                TransitionDays = reader.ReadSingle(),
                TransitionSeason = reader.ReadInt32(),
                RainIntensity = reader.ReadSingle(),
                WindVelocityX = reader.ReadSingle(),
                WindVelocityZ = reader.ReadSingle(),
                SnowLightFactor = reader.ReadSingle()
            };
        }

        private static float Clamp(float value, float min, float max)
        {
            if (float.IsNaN(value)) return min;
            return Math.Max(min, Math.Min(max, value));
        }

        private static bool IsFiniteInRange(float value, float min, float max)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= min && value <= max;
        }
    }

    public interface ISeasonNetworkBridge : IDisposable
    {
        bool IsAvailable { get; }
        bool IsSessionActive { get; }
        bool IsAuthority { get; }
        string Status { get; }
        event Action<SeasonNetworkState> StateReceived;
        void Initialize(string modId);
        void SetEnabled(bool enabled);
        void Publish(SeasonNetworkState state, bool force);
        void RequestState();
    }
}
