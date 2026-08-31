using System;

namespace DVSeasons.Core
{
    // The calendar belongs to the game save. Visual preferences remain in UMM.
    public sealed class SeasonSaveState
    {
        public const int CurrentVersion = 2;

        public int Version = CurrentVersion;
        public double Phase;
        public bool AutomaticCycle;
        public float DaysPerSeason;
        public bool RandomTransitionDuration = true;
        public float TransitionDays;
        public int TransitionSeason;

        public bool IsValid()
        {
            return Version == CurrentVersion &&
                !double.IsNaN(Phase) && !double.IsInfinity(Phase) && Phase >= 0d && Phase < 4d &&
                IsFinite(DaysPerSeason) && DaysPerSeason >= 1f && DaysPerSeason <= 365f &&
                IsFinite(TransitionDays) && TransitionDays >= 1f &&
                TransitionDays <= Math.Min(5f, DaysPerSeason) &&
                TransitionSeason == (int)Math.Floor(Phase);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
