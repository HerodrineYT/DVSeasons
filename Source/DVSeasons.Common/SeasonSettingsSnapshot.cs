using System;

namespace DVSeasons.Core
{
    public sealed class SeasonSettingsSnapshot
    {
        public SeasonSettingsSnapshot(bool automaticCycle, float daysPerSeason, float transitionDays,
            SeasonKind startingSeason, bool winterAdhesionEnabled, float winterWetnessEquivalent)
        {
            AutomaticCycle = automaticCycle;
            DaysPerSeason = Clamp(daysPerSeason, 1f, 365f);
            TransitionDays = Clamp(transitionDays, 0f, DaysPerSeason);
            StartingSeason = Enum.IsDefined(typeof(SeasonKind), startingSeason) ? startingSeason : SeasonKind.Spring;
            WinterAdhesionEnabled = winterAdhesionEnabled;
            WinterWetnessEquivalent = Clamp(winterWetnessEquivalent, 0f, 0.5f);
        }

        public bool AutomaticCycle { get; private set; }
        public float DaysPerSeason { get; private set; }
        public float TransitionDays { get; private set; }
        public SeasonKind StartingSeason { get; private set; }
        public bool WinterAdhesionEnabled { get; private set; }
        public float WinterWetnessEquivalent { get; private set; }

        private static float Clamp(float value, float min, float max)
        {
            return Math.Max(min, Math.Min(max, value));
        }
    }
}
