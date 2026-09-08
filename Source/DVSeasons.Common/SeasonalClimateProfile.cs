using System;

namespace DVSeasons.Core
{
    public static class SeasonalClimateProfile
    {
        // Continuous through the whole year, not only the texture transition.
        public static double DaylightHours(double phase)
        {
            if (double.IsNaN(phase) || double.IsInfinity(phase)) phase = 0;
            return 12 + 6 * Math.Sin(phase % 4 * Math.PI / 2);
        }

        public static double WrapHour(double hour) { return (hour % 24 + 24) % 24; }

        // Retain native solar noon. Never change the clock, calendar or day speed.
        public static double MapSolarHour(double hour, double sunrise, double sunset, double daylight)
        {
            var nativeDay = WrapHour(sunset - sunrise);
            if (nativeDay < 1 || nativeDay > 23) return WrapHour(hour);
            daylight = Math.Max(1, Math.Min(23, daylight));
            var dawn = sunrise + (nativeDay - daylight) / 2;
            var elapsed = WrapHour(hour - dawn);
            var mapped = elapsed <= daylight
                ? sunrise + elapsed / daylight * nativeDay
                : sunset + (elapsed - daylight) / (24 - daylight) * (24 - nativeDay);
            return WrapHour(mapped);
        }

        public static float NoiseOffset(SeasonState state, bool cloud)
        {
            if (state == null) return 0;
            var a = Offset(state.Current, cloud);
            var b = Offset(state.Next, cloud);
            return a + (b - a) * state.Transition;
        }

        private static float Offset(SeasonKind season, bool cloud)
        {
            switch (season)
            {
                case SeasonKind.Spring: return cloud ? 0.04f : 0.03f;
                case SeasonKind.Summer: return 0f;
                case SeasonKind.Autumn: return cloud ? 0.17f : 0.18f;
                case SeasonKind.Winter: return cloud ? 0.12f : 0.08f;
                default: return 0;
            }
        }
    }
}
