using System;

namespace DVSeasons.Core
{
    public sealed class SeasonNetworkState
    {
        public const int CurrentProtocol = 1;
        public int Protocol { get; set; }
        public uint Sequence { get; set; }
        public double Phase { get; set; }
        public SeasonKind Current { get; set; }
        public SeasonKind Next { get; set; }
        public float Transition { get; set; }
        public float SnowAmount { get; set; }
        public float TemperatureCelsius { get; set; }
        public float WinterWetnessEquivalent { get; set; }

        public static SeasonNetworkState FromState(SeasonState state)
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
                WinterWetnessEquivalent = state.WinterWetnessEquivalent
            };
        }

        public bool IsValid()
        {
            return Protocol == CurrentProtocol && !double.IsNaN(Phase) && !double.IsInfinity(Phase) &&
                Phase >= 0d && Phase < 4d && Enum.IsDefined(typeof(SeasonKind), Current) &&
                Enum.IsDefined(typeof(SeasonKind), Next) && IsFiniteInRange(Transition, 0f, 1f) &&
                IsFiniteInRange(SnowAmount, 0f, 1f) && IsFiniteInRange(TemperatureCelsius, -100f, 100f) &&
                IsFiniteInRange(WinterWetnessEquivalent, 0f, 0.5f);
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
