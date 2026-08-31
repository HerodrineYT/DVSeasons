using System.IO;
using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public sealed class SeasonCycleTests
    {
        [Fact]
        public void TransitionIsContinuousAcrossWinterBoundary()
        {
            var settings = new SeasonSettingsSnapshot(true, 10f, 2f, SeasonKind.Autumn, true, 0.45f);
            var cycle = new SeasonCycle(settings, 2.999999d);
            var before = cycle.GetState();
            cycle.SetPhase(3d);
            var after = cycle.GetState();
            Assert.InRange(before.SnowAmount, 0.999f, 1f);
            Assert.Equal(1f, after.SnowAmount);
        }

        [Fact]
        public void WinterWetnessTracksSmoothSnowAmount()
        {
            var settings = new SeasonSettingsSnapshot(true, 10f, 2f, SeasonKind.Autumn, true, 0.4f);
            var cycle = new SeasonCycle(settings, 2.9d);
            var state = cycle.GetState();
            Assert.InRange(state.SnowAmount, 0.49f, 0.51f);
            Assert.InRange(state.WinterWetnessEquivalent, 0.19f, 0.21f);
        }

        [Fact]
        public void AutomaticCycleUsesGameDaysAndWraps()
        {
            var settings = new SeasonSettingsSnapshot(true, 5f, 1f, SeasonKind.Winter, true, 0.5f);
            var cycle = new SeasonCycle(settings, 3d);
            cycle.AdvanceGameDays(5d);
            Assert.Equal(SeasonKind.Spring, cycle.GetState().Current);
        }

        [Fact]
        public void ManualModeDoesNotAdvance()
        {
            var settings = new SeasonSettingsSnapshot(false, 5f, 1f, SeasonKind.Summer, true, 0.5f);
            var cycle = new SeasonCycle(settings, 1d);
            cycle.AdvanceGameDays(100d);
            Assert.Equal(SeasonKind.Summer, cycle.GetState().Current);
        }

        [Fact]
        public void SettingsClampNativeWetnessToSupportedRange()
        {
            var settings = new SeasonSettingsSnapshot(true, -4f, 500f, SeasonKind.Spring, true, 2f);
            Assert.Equal(1f, settings.DaysPerSeason);
            Assert.Equal(1f, settings.TransitionDays);
            Assert.Equal(0.5f, settings.WinterWetnessEquivalent);
        }

        [Fact]
        public void NetworkStateRejectsInvalidOrOutOfRangeData()
        {
            var valid = SeasonNetworkState.FromState(new SeasonState(3.5d, SeasonKind.Winter,
                SeasonKind.Spring, 0.5f, 0.5f, -1f, 0.2f));
            Assert.True(valid.IsValid());
            valid.Phase = double.NaN;
            Assert.False(valid.IsValid());
            valid.Phase = 3.5d;
            valid.SnowAmount = 2f;
            Assert.False(valid.IsValid());
            valid.SnowAmount = 0.5f;
            valid.TransitionDays = 6f;
            Assert.False(valid.IsValid());
            valid.TransitionDays = 1f;
            valid.TransitionSeason = 4;
            Assert.False(valid.IsValid());
        }

        [Fact]
        public void NetworkStateBinaryRoundTripPreservesHostSeasonSettings()
        {
            var original = SeasonNetworkState.FromState(new SeasonState(2.75d, SeasonKind.Autumn,
                SeasonKind.Winter, 0.75f, 0.42f, -3.5f, 0.18f), 21f, 4f,
                0.82f, -7.25f, 3.5f, 0.35f, 3, false);
            original.Sequence = 57;

            SeasonNetworkState restored;
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                    original.WriteTo(writer);
                stream.Position = 0;
                using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true))
                    restored = SeasonNetworkState.ReadFrom(reader);
            }

            Assert.True(restored.IsValid());
            Assert.Equal(original.Protocol, restored.Protocol);
            Assert.Equal(original.Sequence, restored.Sequence);
            Assert.Equal(original.Phase, restored.Phase);
            Assert.Equal(original.Current, restored.Current);
            Assert.Equal(original.Next, restored.Next);
            Assert.Equal(original.Transition, restored.Transition);
            Assert.Equal(original.SnowAmount, restored.SnowAmount);
            Assert.Equal(original.TemperatureCelsius, restored.TemperatureCelsius);
            Assert.Equal(original.WinterWetnessEquivalent, restored.WinterWetnessEquivalent);
            Assert.Equal(21f, restored.DaysPerSeason);
            Assert.False(restored.RandomTransitionDuration);
            Assert.Equal(4f, restored.TransitionDays);
            Assert.Equal(3, restored.TransitionSeason);
            Assert.Equal(0.82f, restored.RainIntensity);
            Assert.Equal(-7.25f, restored.WindVelocityX);
            Assert.Equal(3.5f, restored.WindVelocityZ);
            Assert.Equal(0.35f, restored.SnowLightFactor);
        }

        [Fact]
        public void SnowCoverUsesPatchyStageBeforeFullWinter()
        {
            Assert.Equal(0f, SnowCoverProfile.GetProceduralAmount(0.01f));
            Assert.InRange(SnowCoverProfile.GetProceduralAmount(0.5f), 0.56f, 0.58f);
            Assert.False(SnowCoverProfile.ShouldUseFullCover(0.89f, false));
            Assert.True(SnowCoverProfile.ShouldUseFullCover(0.91f, false));
        }

        [Fact]
        public void SnowCoverHysteresisKeepsThawStable()
        {
            Assert.True(SnowCoverProfile.ShouldUseFullCover(0.85f, true));
            Assert.False(SnowCoverProfile.ShouldUseFullCover(0.80f, true));
            Assert.Equal(0f, SnowCoverProfile.GetProceduralAmount(float.NaN));
        }

        [Fact]
        public void PatchyWinterArrayOccupiesMiddleOfTransition()
        {
            Assert.False(SnowCoverProfile.ShouldUsePatchyArray(0.019f, false, false));
            Assert.True(SnowCoverProfile.ShouldUsePatchyArray(0.021f, false, false));
            Assert.True(SnowCoverProfile.ShouldUsePatchyArray(0.011f, true, false));
            Assert.False(SnowCoverProfile.ShouldUsePatchyArray(0.009f, true, false));
            Assert.False(SnowCoverProfile.ShouldUsePatchyArray(0.75f, true, true));
        }

        [Fact]
        public void SnowfallBecomesVisibleEarlyInTransition()
        {
            Assert.InRange(SnowCoverProfile.GetSnowfallVisibility(0.01f), 0.099f, 0.101f);
            Assert.InRange(SnowCoverProfile.GetSnowfallVisibility(0.25f), 0.499f, 0.501f);
            Assert.Equal(1f, SnowCoverProfile.GetSnowfallVisibility(1f));
        }

        [Fact]
        public void SnowfallLeadsFirstWinterTerrainLayerByFiveGameMinutes()
        {
            const float daysPerSeason = 14f;
            const float transitionDays = 3f;
            var settings = new SeasonSettingsSnapshot(true, daysPerSeason, transitionDays,
                SeasonKind.Autumn, true, 0.45f);
            var cycle = new SeasonCycle(settings, 2d);
            var firstLayerSnow = 0.5f / SnowCoverProfile.GroundTextureSteps;
            var lowDay = daysPerSeason - transitionDays;
            var highDay = daysPerSeason;
            for (var i = 0; i < 32; i++)
            {
                var middleDay = (lowDay + highDay) * 0.5f;
                cycle.SetPhase(2d + (middleDay / daysPerSeason));
                if (cycle.GetState().SnowAmount < firstLayerSnow) lowDay = middleDay;
                else highDay = middleDay;
            }

            cycle.SetPhase(2d + ((highDay - (6f / 1440f)) / daysPerSeason));
            Assert.Equal(0f, SnowCoverProfile.GetPrecipitationSnowAmount(cycle.GetState(),
                daysPerSeason, transitionDays, 1f));

            cycle.SetPhase(2d + ((highDay - (4f / 1440f)) / daysPerSeason));
            var leadState = cycle.GetState();
            Assert.Equal(0, SnowCoverProfile.GetGroundTextureStep(leadState.SnowAmount));
            Assert.True(SnowCoverProfile.GetPrecipitationSnowAmount(leadState,
                daysPerSeason, transitionDays, 1f) > 0f);
        }

        [Fact]
        public void GroundTextureUsesFineGrainedCoverageSteps()
        {
            Assert.Equal(0, SnowCoverProfile.GetGroundTextureStep(0f));
            Assert.Equal(1, SnowCoverProfile.GetGroundTextureStep(0.02f));
            Assert.Equal(16, SnowCoverProfile.GetGroundTextureStep(0.5f));
            Assert.Equal(32, SnowCoverProfile.GetGroundTextureStep(1f));
        }

        [Fact]
        public void GroundCoverageDistributesThirtyTwoStepsAcrossSixteenLayers()
        {
            Assert.Equal(0, SnowCoverProfile.GetWinterTerrainLayerCount(0, 16));
            Assert.Equal(1, SnowCoverProfile.GetWinterTerrainLayerCount(1, 16));
            Assert.Equal(1, SnowCoverProfile.GetWinterTerrainLayerCount(2, 16));
            Assert.Equal(4, SnowCoverProfile.GetWinterTerrainLayerCount(8, 16));
            Assert.Equal(8, SnowCoverProfile.GetWinterTerrainLayerCount(16, 16));
            Assert.Equal(12, SnowCoverProfile.GetWinterTerrainLayerCount(24, 16));
            Assert.Equal(16, SnowCoverProfile.GetWinterTerrainLayerCount(31, 16));
            Assert.Equal(16, SnowCoverProfile.GetWinterTerrainLayerCount(32, 16));
            Assert.Equal(0, SnowCoverProfile.GetWinterTerrainLayerCount(16, 0));
        }

        [Fact]
        public void VegetationReturnsOnlyAtEndOfThaw()
        {
            Assert.Equal(1f, SnowCoverProfile.GetVegetationWinterWeight(0.10f, true));
            Assert.InRange(SnowCoverProfile.GetVegetationWinterWeight(0.03f, true), 0.49f, 0.51f);
            Assert.Equal(0f, SnowCoverProfile.GetVegetationWinterWeight(0f, true));
            Assert.InRange(SnowCoverProfile.GetVegetationWinterWeight(0.03f, false), 0.36f, 0.39f);
            Assert.InRange(SnowCoverProfile.GetVegetationWinterWeight(0.5f, false), 0.92f, 0.95f);
        }

        [Fact]
        public void SeasonalPrecipitationMakesSummerRarerAndAutumnLonger()
        {
            var summer = SeasonalPrecipitationProfile.ForSeason(SeasonKind.Summer);
            var autumn = SeasonalPrecipitationProfile.ForSeason(SeasonKind.Autumn);
            var winter = SeasonalPrecipitationProfile.ForSeason(SeasonKind.Winter);
            Assert.True(summer.StartThresholdOffset > 0f);
            Assert.True(autumn.StartThresholdOffset < winter.StartThresholdOffset);
            Assert.True(winter.StartThresholdOffset < 0f);
        }

        [Fact]
        public void SeasonalPrecipitationBlendsDuringSeasonTransition()
        {
            var state = new SeasonState(1.9d, SeasonKind.Summer, SeasonKind.Autumn,
                0.5f, 0f, 12f, 0f);
            var profile = SeasonalPrecipitationProfile.FromState(state);
            Assert.InRange(profile.StartThresholdOffset, -0.011f, -0.009f);
            Assert.InRange(profile.MaximumThresholdOffset, 0.009f, 0.011f);
        }
    }
}
