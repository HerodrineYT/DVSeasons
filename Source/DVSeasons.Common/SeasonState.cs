using System;

namespace DVSeasons.Core
{
    public sealed class SeasonState
    {
        public SeasonState(double phase, SeasonKind current, SeasonKind next, float transition,
            float snowAmount, float temperatureCelsius, float winterWetnessEquivalent)
        {
            Phase = phase;
            Current = current;
            Next = next;
            Transition = Clamp01(transition);
            SnowAmount = Clamp01(snowAmount);
            TemperatureCelsius = temperatureCelsius;
            WinterWetnessEquivalent = Math.Max(0f, Math.Min(0.5f, winterWetnessEquivalent));
        }

        public double Phase { get; private set; }
        public SeasonKind Current { get; private set; }
        public SeasonKind Next { get; private set; }
        public float Transition { get; private set; }
        public float SnowAmount { get; private set; }
        public float TemperatureCelsius { get; private set; }
        public float WinterWetnessEquivalent { get; private set; }

        public static SeasonState FromNetwork(SeasonNetworkState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return new SeasonState(state.Phase, state.Current, state.Next, state.Transition,
                state.SnowAmount, state.TemperatureCelsius, state.WinterWetnessEquivalent);
        }

        private static float Clamp01(float value) { return Math.Max(0f, Math.Min(1f, value)); }
    }
}
