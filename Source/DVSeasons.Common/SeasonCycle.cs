using System;

namespace DVSeasons.Core
{
    public sealed class SeasonCycle
    {
        private static readonly float[] SnowProfile = { 0f, 0f, 0f, 1f };
        private static readonly float[] TemperatureProfile = { 10f, 24f, 8f, -8f };
        private SeasonSettingsSnapshot settings;
        private double phase;

        public SeasonCycle(SeasonSettingsSnapshot settings, double initialPhase)
        {
            Configure(settings);
            SetPhase(initialPhase);
        }

        public double Phase { get { return phase; } }

        public void Configure(SeasonSettingsSnapshot newSettings)
        {
            settings = newSettings ?? throw new ArgumentNullException(nameof(newSettings));
        }

        public void AdvanceGameDays(double gameDays)
        {
            if (!settings.AutomaticCycle || double.IsNaN(gameDays) || double.IsInfinity(gameDays) || gameDays <= 0d)
                return;
            SetPhase(phase + (gameDays / settings.DaysPerSeason));
        }

        public void SetPhase(double newPhase)
        {
            if (double.IsNaN(newPhase) || double.IsInfinity(newPhase)) newPhase = 0d;
            phase = newPhase % 4d;
            if (phase < 0d) phase += 4d;
        }

        public void SetSeason(SeasonKind season) { SetPhase((int)season); }

        public void AdvanceToNextSeason()
        {
            SetPhase(Math.Floor(phase) + 1d);
        }

        public SeasonState GetState()
        {
            var currentIndex = (int)Math.Floor(phase);
            var nextIndex = (currentIndex + 1) % 4;
            var progressDays = (float)((phase - currentIndex) * settings.DaysPerSeason);
            var transition = 0f;
            if (settings.TransitionDays > 0f)
            {
                var start = settings.DaysPerSeason - settings.TransitionDays;
                var linear = Clamp01((progressDays - start) / settings.TransitionDays);
                transition = linear * linear * (3f - (2f * linear));
            }

            var snow = Lerp(SnowProfile[currentIndex], SnowProfile[nextIndex], transition);
            var temperature = Lerp(TemperatureProfile[currentIndex], TemperatureProfile[nextIndex], transition);
            var wetness = settings.WinterAdhesionEnabled ? snow * settings.WinterWetnessEquivalent : 0f;
            return new SeasonState(phase, (SeasonKind)currentIndex, (SeasonKind)nextIndex, transition,
                snow, temperature, wetness);
        }

        private static float Lerp(float a, float b, float t) { return a + ((b - a) * t); }
        private static float Clamp01(float value) { return Math.Max(0f, Math.Min(1f, value)); }
    }
}
