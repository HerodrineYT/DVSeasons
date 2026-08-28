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
        }
    }
}
