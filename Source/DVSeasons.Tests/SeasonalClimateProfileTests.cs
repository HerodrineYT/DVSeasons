using System;
using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public class SeasonalClimateProfileTests
    {
        [Theory]
        [InlineData(0, 12)]
        [InlineData(1, 18)]
        [InlineData(2, 12)]
        [InlineData(3, 6)]
        [InlineData(4, 12)]
        [InlineData(-1, 6)]
        public void DaylightAnchors(double phase, double hours)
        { Assert.Equal(hours, SeasonalClimateProfile.DaylightHours(phase), 8); }

        [Fact]
        public void DaylightIsContinuousAndBoundedAcrossEveryBoundary()
        {
            for (double phase = 0; phase < 4.01; phase += .001)
            {
                var hours = SeasonalClimateProfile.DaylightHours(phase);
                Assert.InRange(hours, 6, 18);
                Assert.InRange(Math.Abs(hours - SeasonalClimateProfile.DaylightHours(phase - .001)), 0, .0095);
            }
        }

        [Theory]
        [InlineData(9, 5)]
        [InlineData(13, 13)]
        [InlineData(17, 21)]
        [InlineData(1, 1)]
        public void WinterRemapsSunNotClock(double hour, double mapped)
        { Assert.Equal(mapped, SeasonalClimateProfile.MapSolarHour(hour, 5, 21, 8), 8); }

        [Fact]
        public void NativeDayIsIdentityAndMidnightContinuous()
        {
            for (double hour = 0; hour < 24; hour += .125)
                Assert.Equal(hour, SeasonalClimateProfile.MapSolarHour(hour, 5, 21, 16), 8);
            var before = SeasonalClimateProfile.MapSolarHour(23.999, 5, 21, 8);
            var after = SeasonalClimateProfile.MapSolarHour(24.001, 5, 21, 8);
            Assert.InRange(Math.Abs(after - before), 0, .01);
        }

        [Theory]
        [InlineData(1,4,22)]
        [InlineData(3,10,16)]
        public void StrongSeasonalDawnAndDuskRetainSolarNoon(double phase,double dawn,double dusk)
        {
            var hours=SeasonalClimateProfile.DaylightHours(phase);
            Assert.Equal(5,SeasonalClimateProfile.MapSolarHour(dawn,5,21,hours),8);
            Assert.Equal(21,SeasonalClimateProfile.MapSolarHour(dusk,5,21,hours),8);
            Assert.Equal(13,SeasonalClimateProfile.MapSolarHour(13,5,21,hours),8);
        }

        [Fact]
        public void WeatherIsDrierInSummerAndBlendsContinuously()
        {
            var summer = new SeasonState(1, SeasonKind.Summer, SeasonKind.Autumn, 0, 0, 24, 0);
            var autumn = new SeasonState(2, SeasonKind.Autumn, SeasonKind.Winter, 0, 0, 8, 0);
            var blend = new SeasonState(1.9, SeasonKind.Summer, SeasonKind.Autumn, .5f, 0, 16, 0);
            foreach (var cloud in new[] { false, true })
            {
                var a = SeasonalClimateProfile.NoiseOffset(summer, cloud);
                var b = SeasonalClimateProfile.NoiseOffset(autumn, cloud);
                Assert.Equal(0f, a);
                Assert.True(b > 0);
                Assert.Equal((a + b) / 2, SeasonalClimateProfile.NoiseOffset(blend, cloud), 6);
            }
        }
    }
}
