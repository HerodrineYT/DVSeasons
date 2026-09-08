using System;

namespace DVSeasons.Core
{
    public enum WinterTrackTextureStage : byte
    {
        SnowFree = 0,
        Early = 1,
        Middle = 2,
        Late = 3
    }

    public static class WinterTrackTextureProfile
    {
        public const float EarlyAnchor = 0.28f;
        public const float MiddleAnchor = 0.62f;
        public const float LateAnchor = 1f;

        public static void GetBlend(float snowAmount,
            out WinterTrackTextureStage lower,
            out WinterTrackTextureStage upper,
            out float blend)
        {
            snowAmount = Clamp01(snowAmount);
            if (snowAmount <= EarlyAnchor)
            {
                lower = WinterTrackTextureStage.SnowFree;
                upper = WinterTrackTextureStage.Early;
                blend = SmoothStep(snowAmount / EarlyAnchor);
                return;
            }
            if (snowAmount <= MiddleAnchor)
            {
                lower = WinterTrackTextureStage.Early;
                upper = WinterTrackTextureStage.Middle;
                blend = SmoothStep((snowAmount - EarlyAnchor) /
                    (MiddleAnchor - EarlyAnchor));
                return;
            }

            lower = WinterTrackTextureStage.Middle;
            upper = WinterTrackTextureStage.Late;
            blend = SmoothStep((snowAmount - MiddleAnchor) /
                (LateAnchor - MiddleAnchor));
        }

        private static float SmoothStep(float value)
        {
            value = Clamp01(value);
            return value * value * (3f - (2f * value));
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return Math.Max(0f, Math.Min(1f, value));
        }
    }
}
