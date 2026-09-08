using System;

namespace DVSeasons.Core
{
    public static class SnowTrailProfile
    {
        public const float StartSpeedKmh = 50f;
        public const float FullSpeedKmh = 100f;

        public static float Intensity(float speedKmh, float snowCoverage)
        {
            if(float.IsNaN(speedKmh) || float.IsInfinity(speedKmh) ||
                float.IsNaN(snowCoverage) || float.IsInfinity(snowCoverage)) return 0f;
            var coverage=Math.Max(0,Math.Min(1,snowCoverage));
            if(speedKmh<=StartSpeedKmh || coverage<=0) return 0f;
            var speed=(speedKmh-StartSpeedKmh)/(FullSpeedKmh-StartSpeedKmh);
            return (float)(coverage*Math.Max(0,Math.Min(1,speed)));
        }
    }
}
