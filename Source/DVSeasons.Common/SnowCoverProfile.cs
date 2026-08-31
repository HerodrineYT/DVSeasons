using System;

namespace DVSeasons.Core
{
    public static class SnowCoverProfile
    {
        public const int GroundTextureSteps = 32;
        public const float SnowfallLeadGameMinutes = 5f;
        public const float VegetationThawSnowThreshold = 0.06f;
        public const float PatchyCoverStart = 0.015f;
        public const float PatchyArrayEnter = 0.02f;
        public const float PatchyArrayExit = 0.01f;
        public const float FullCoverEnter = 0.90f;
        public const float FullCoverExit = 0.82f;

        public static bool ShouldUseFullCover(float coverage, bool currentlyFull)
        {
            coverage = Clamp01(coverage);
            return currentlyFull ? coverage >= FullCoverExit : coverage >= FullCoverEnter;
        }

        public static bool ShouldUsePatchyArray(float coverage, bool currentlyPatchy, bool fullCover)
        {
            if (fullCover) return false;
            coverage = Clamp01(coverage);
            return currentlyPatchy ? coverage >= PatchyArrayExit : coverage >= PatchyArrayEnter;
        }

        public static float GetProceduralAmount(float coverage)
        {
            coverage = Clamp01(coverage);
            var linear = Clamp01((coverage - PatchyCoverStart) /
                (FullCoverEnter - PatchyCoverStart));
            return linear * linear * (3f - (2f * linear));
        }

        public static float GetSnowfallVisibility(float snowAmount)
        {
            return (float)Math.Sqrt(Clamp01(snowAmount));
        }

        public static float GetPrecipitationSnowAmount(SeasonState state, float daysPerSeason,
            float transitionDays, float groundSnowStrength)
        {
            if (state == null) return 0f;
            if (state.Current != SeasonKind.Autumn || state.Next != SeasonKind.Winter)
                return state.SnowAmount;

            daysPerSeason = Math.Max(1f, daysPerSeason);
            transitionDays = Math.Max(0f, Math.Min(daysPerSeason, transitionDays));
            groundSnowStrength = Math.Max(0f, groundSnowStrength);
            if (transitionDays <= 0f || groundSnowStrength <= 0f)
                return state.SnowAmount;

            // The first whole terrain layer is selected when rounded 32-step
            // coverage reaches one. Convert that snow amount back through the
            // cycle's SmoothStep curve so precipitation can lead it by five
            // in-game minutes without changing the ground itself early.
            var firstLayerSnow = Math.Min(1f, 0.5f / GroundTextureSteps / groundSnowStrength);
            var firstLayerLinear = InverseSmoothStep(firstLayerSnow);
            var transitionStart = daysPerSeason - transitionDays;
            var firstLayerDay = transitionStart + (transitionDays * firstLayerLinear);
            var leadDays = SnowfallLeadGameMinutes / (24f * 60f);
            var leadStartDay = firstLayerDay - leadDays;
            var seasonProgressDays = (float)(state.Phase - Math.Floor(state.Phase)) * daysPerSeason;

            if (seasonProgressDays <= leadStartDay) return 0f;
            if (seasonProgressDays >= firstLayerDay) return state.SnowAmount;
            return firstLayerSnow * Clamp01((seasonProgressDays - leadStartDay) / leadDays);
        }

        public static int GetGroundTextureStep(float coverage)
        {
            return (int)Math.Round(Clamp01(coverage) * GroundTextureSteps,
                MidpointRounding.AwayFromZero);
        }

        public static int GetWinterTerrainLayerCount(int coverageStep, int layerCount)
        {
            if (layerCount <= 0) return 0;
            coverageStep = Math.Max(0, Math.Min(GroundTextureSteps, coverageStep));
            return Math.Max(0, Math.Min(layerCount, (int)Math.Ceiling(
                coverageStep / (double)GroundTextureSteps * layerCount)));
        }

        public static float GetVegetationWinterWeight(float snowAmount, bool thawing)
        {
            snowAmount = Clamp01(snowAmount);
            // Leaf fall leads visible ground accumulation. Two square roots produce
            // a deliberately front-loaded curve: even the first thin snow cover makes
            // deciduous vegetation visibly sparse, while thaw still restores it late.
            if (!thawing) return SmoothStep((float)Math.Sqrt(Math.Sqrt(snowAmount)));
            return SmoothStep(Clamp01(snowAmount / VegetationThawSnowThreshold));
        }

        private static float SmoothStep(float value)
        {
            value = Clamp01(value);
            return value * value * (3f - (2f * value));
        }

        private static float InverseSmoothStep(float value)
        {
            value = Clamp01(value);
            var low = 0f;
            var high = 1f;
            for (var i = 0; i < 16; i++)
            {
                var middle = (low + high) * 0.5f;
                if (SmoothStep(middle) < value) low = middle;
                else high = middle;
            }
            return (low + high) * 0.5f;
        }

        private static float Clamp01(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return Math.Max(0f, Math.Min(1f, value));
        }
    }
}
