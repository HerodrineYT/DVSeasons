using System;

namespace DVSeasons.Core
{
    public struct SeasonalPrecipitationProfile
    {
        public SeasonalPrecipitationProfile(float startThresholdOffset, float maximumThresholdOffset)
        {
            StartThresholdOffset = startThresholdOffset;
            MaximumThresholdOffset = maximumThresholdOffset;
        }

        public float StartThresholdOffset { get; private set; }
        public float MaximumThresholdOffset { get; private set; }

        public static SeasonalPrecipitationProfile FromState(SeasonState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var current = ForSeason(state.Current);
            var next = ForSeason(state.Next);
            return new SeasonalPrecipitationProfile(
                Lerp(current.StartThresholdOffset, next.StartThresholdOffset, state.Transition),
                Lerp(current.MaximumThresholdOffset, next.MaximumThresholdOffset, state.Transition));
        }

        public static SeasonalPrecipitationProfile ForSeason(SeasonKind season)
        {
            // The WeatherDriver starts rain when its fog/cloud noise exceeds the
            // start threshold. A lower start therefore makes precipitation both
            // more frequent and longer without replacing the vanilla weather.
            switch (season)
            {
                case SeasonKind.Spring:
                    return new SeasonalPrecipitationProfile(-0.04f, 0.01f);
                case SeasonKind.Summer:
                    return new SeasonalPrecipitationProfile(0.08f, -0.02f);
                case SeasonKind.Autumn:
                    return new SeasonalPrecipitationProfile(-0.10f, 0.04f);
                case SeasonKind.Winter:
                    return new SeasonalPrecipitationProfile(-0.055f, 0.02f);
                default:
                    return new SeasonalPrecipitationProfile(0f, 0f);
            }
        }

        private static float Lerp(float a, float b, float value)
        {
            value = Math.Max(0f, Math.Min(1f, value));
            return a + ((b - a) * value);
        }
    }
}
