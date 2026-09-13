using System;

namespace DVSeasons.Core
{
    public static class SpringLifeProfile
    {
        public static float Activity(SeasonState state, float rain, float daylight, float wind)
        {
            if (state == null) return 0;
            float spring = state.Current == SeasonKind.Spring ? 1 - state.Transition
                : state.Next == SeasonKind.Spring ? state.Transition : 0;
            return Clamp(spring) * Clamp(1-state.SnowAmount*2) * Clamp((state.TemperatureCelsius - 5) / 7) *
                Clamp((daylight - .08f) / .35f) * Clamp(1 - rain * 4) * Clamp(1 - wind / 12);
        }
        private static float Clamp(float value) { return Math.Max(0, Math.Min(1, value)); }
    }
}
