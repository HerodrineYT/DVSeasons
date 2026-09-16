using System;

namespace DVSeasons.Core
{
    public static class SnowTrailProfile
    {
        public const float StartSpeedKmh = 25f;
        public const float FullSpeedKmh = 100f;
        public const float MaximumEmissionRate = 24f;

        public static float Intensity(float speedKmh, float snowCoverage)
        {
            if (!IsFinite(speedKmh) || !IsFinite(snowCoverage)) return 0f;
            var coverage = Clamp01(snowCoverage);
            if(speedKmh<=StartSpeedKmh || coverage<=0) return 0f;
            var speed = (speedKmh - StartSpeedKmh) / (FullSpeedKmh - StartSpeedKmh);
            return coverage * SmoothStep(speed);
        }

        // A visual approximation of how readily loose snow becomes airborne.
        // Damp snow near melting produces much less fine powder; rain further
        // binds it together. The small residual allows existing wet snow to move.
        public static float PowderFactor(float temperatureC, float rainIntensity)
        {
            if (!IsFinite(temperatureC) || !IsFinite(rainIntensity)) return 0f;
            var dampness = SmoothStep((temperatureC + 10f) / 12f);
            var rain = SmoothStep(rainIntensity);
            return (1f - .96f * dampness) * (1f - .9f * rain);
        }

        // Particles per second for a complete car. Keeping a zero baseline
        // avoids the old five-particle jump just above the starting speed.
        public static float EmissionRate(float speedKmh, float snowCoverage,
            float temperatureC, float rainIntensity)
        {
            return MaximumEmissionRate * Intensity(speedKmh, snowCoverage) *
                PowderFactor(temperatureC, rainIntensity);
        }

        public static float MainSizeMultiplier(float speedKmh)
        {
            if (!IsFinite(speedKmh)) return .65f;
            return .65f + 1.55f * SmoothStep(
                (speedKmh - StartSpeedKmh) / (FullSpeedKmh - StartSpeedKmh));
        }

        public static float WheelSizeMultiplier(float speedKmh)
        {
            if (!IsFinite(speedKmh)) return .7f;
            return .7f + 1.1f * SmoothStep(speedKmh / FullSpeedKmh);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static float Clamp01(float value) => Math.Max(0f, Math.Min(1f, value));
        private static float SmoothStep(float value)
        {
            var t = Clamp01(value);
            return t * t * (3f - 2f * t);
        }
    }
}
