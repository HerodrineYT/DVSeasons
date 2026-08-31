using DVSeasons.Core;
using UnityModManagerNet;

namespace DVSeasons.Mod
{
    public sealed class SeasonModSettings : UnityModManager.ModSettings
    {
        public bool AutomaticCycle = true;
        public float DaysPerSeason = 14f;
        public bool RandomTransitionDuration = true;
        public float TransitionDays = 3f;
        public int TransitionSeason = -1;
        public int StartingSeason;
        public bool HasSavedPhase;
        public float SavedPhase;
        public bool SnowParticlesEnabled = true;
        public float SnowfallDensity = 1f;
        // Retained only for settings-file compatibility. Snowfall now follows the
        // native WeatherDriver.RainValue exclusively.
        public float AmbientWinterSnowfall;
        public bool GroundSnowEnabled = true;
        public float GroundSnowStrength = 1f;
        public float FoliageTintStrength = 0.75f;
        public bool SeasonalTexturesEnabled = true;
        public bool TerrainTextureChanges = true;
        public bool DistantTerrainSeasonal = true;
        // Kept only so settings written by 0.6.6 remain forward-compatible.
        public float DistantTerrainDetailDistance = 8000f;
        public bool VegetationTextureChanges = true;
        public bool LeaflessDistantTrees = true;
        public float WinterFullTreeDistance = 750f;
        public int WinterFullTreeCount = 1200;
        // Retained for settings-file compatibility with 0.1.41. Winter keeps the
        // exact summer treeDistance so silhouettes occupy identical positions.
        public float WinterTreeDistanceMultiplier = 1f;
        public float WinterDetailDistanceMultiplier = 1.2f;
        // Retained for settings-file compatibility. The middle-distance winter LOD
        // is controlled by WinterFullTreeDistance above.
        public float WinterBillboardStartDistance = 180f;
        public float TextureChangeStrength = 1f;
        public int SeasonalTextureResolution = 256;
        public int TextureUpdatesPerFrame = 1;
        public int MaximumSeasonalTextures = 64;
        public bool ReplaceRainWithSnow = true;
        public bool MuteRainAudioDuringSnow = true;
        public bool SeasonalPrecipitationEnabled = true;
        public bool DisableWinterThunder = true;
        public bool WinterAdhesionEnabled = true;
        public float WinterWetnessEquivalent = 0.45f;
        public bool RespectExternalWetnessOverride = true;
        public float FallbackMinutesPerGameDay = 60f;

        public SeasonSettingsSnapshot ToSnapshot()
        {
            return new SeasonSettingsSnapshot(AutomaticCycle, DaysPerSeason, TransitionDays,
                (SeasonKind)StartingSeason, WinterAdhesionEnabled, WinterWetnessEquivalent);
        }

        public void Clamp()
        {
            // These features are part of the default visual/physics profile. They are
            // intentionally not exposed in the compact settings UI anymore.
            SnowParticlesEnabled = true;
            GroundSnowEnabled = true;
            SeasonalTexturesEnabled = true;
            TerrainTextureChanges = true;
            DistantTerrainSeasonal = true;
            VegetationTextureChanges = true;
            LeaflessDistantTrees = true;
            ReplaceRainWithSnow = true;
            WinterAdhesionEnabled = true;
            if (DaysPerSeason < 1f) DaysPerSeason = 1f;
            if (DaysPerSeason > 365f) DaysPerSeason = 365f;
            if (TransitionDays < 1f) TransitionDays = 1f;
            var maximumTransitionDays = DaysPerSeason < 5f ? DaysPerSeason : 5f;
            if (TransitionDays > maximumTransitionDays) TransitionDays = maximumTransitionDays;
            if (TransitionSeason < -1 || TransitionSeason > 3) TransitionSeason = -1;
            if (StartingSeason < 0 || StartingSeason > 3) StartingSeason = 0;
            if (SnowfallDensity < 0f) SnowfallDensity = 0f;
            if (SnowfallDensity > 2f) SnowfallDensity = 2f;
            if (AmbientWinterSnowfall < 0f) AmbientWinterSnowfall = 0f;
            if (AmbientWinterSnowfall > 0.5f) AmbientWinterSnowfall = 0.5f;
            if (GroundSnowStrength < 0f) GroundSnowStrength = 0f;
            if (GroundSnowStrength > 1.5f) GroundSnowStrength = 1.5f;
            if (FoliageTintStrength < 0f) FoliageTintStrength = 0f;
            if (FoliageTintStrength > 1f) FoliageTintStrength = 1f;
            if (TextureChangeStrength < 0f) TextureChangeStrength = 0f;
            if (TextureChangeStrength > 1f) TextureChangeStrength = 1f;
            if (DistantTerrainDetailDistance < 500f) DistantTerrainDetailDistance = 500f;
            if (DistantTerrainDetailDistance > 20000f) DistantTerrainDetailDistance = 20000f;
            if (WinterFullTreeDistance < 150f) WinterFullTreeDistance = 150f;
            // Older experimental builds could persist 2500 m here. Applying that
            // value to real 3D trees causes a severe winter FPS regression.
            if (WinterFullTreeDistance > 750f) WinterFullTreeDistance = 750f;
            if (WinterFullTreeCount < 100) WinterFullTreeCount = 100;
            if (WinterFullTreeCount > 1200) WinterFullTreeCount = 1200;
            WinterTreeDistanceMultiplier = 1f;
            if (WinterDetailDistanceMultiplier <= 0f) WinterDetailDistanceMultiplier = 1.2f;
            if (WinterDetailDistanceMultiplier < 1f) WinterDetailDistanceMultiplier = 1f;
            if (WinterDetailDistanceMultiplier > 1.5f) WinterDetailDistanceMultiplier = 1.5f;
            if (WinterBillboardStartDistance <= 0f) WinterBillboardStartDistance = 180f;
            if (WinterBillboardStartDistance < 50f) WinterBillboardStartDistance = 50f;
            if (WinterBillboardStartDistance > 400f) WinterBillboardStartDistance = 400f;
            SeasonalTextureResolution = NormalizeTextureResolution(SeasonalTextureResolution);
            if (TextureUpdatesPerFrame < 1) TextureUpdatesPerFrame = 1;
            if (TextureUpdatesPerFrame > 4) TextureUpdatesPerFrame = 4;
            if (MaximumSeasonalTextures < 8) MaximumSeasonalTextures = 8;
            var maximumAllowedTextures = SeasonalTextureResolution >= 512 ? 32 : 128;
            if (MaximumSeasonalTextures > maximumAllowedTextures) MaximumSeasonalTextures = maximumAllowedTextures;
            if (WinterWetnessEquivalent < 0f) WinterWetnessEquivalent = 0f;
            if (WinterWetnessEquivalent > 0.5f) WinterWetnessEquivalent = 0.5f;
            if (FallbackMinutesPerGameDay < 1f) FallbackMinutesPerGameDay = 1f;
        }

        public override void Save(UnityModManager.ModEntry entry) { Clamp(); Save(this, entry); }

        private static int NormalizeTextureResolution(int value)
        {
            if (value <= 128) return 128;
            if (value <= 256) return 256;
            return 512;
        }
    }
}
