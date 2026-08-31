using System;
using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public sealed class SeasonGameClockTests
    {
        [Fact]
        public void ResetDoesNotCountGapBetweenWorlds()
        {
            var clock = new SeasonGameClock();
            var day = new DateTime(2026, 1, 1);
            Assert.Equal(0d, clock.GetElapsedDays(day, 1f, 60f));
            Assert.Equal(0.5d, clock.GetElapsedDays(day.AddHours(12), 1f, 60f));
            clock.Reset();
            Assert.Equal(0d, clock.GetElapsedDays(day.AddDays(20), 1f, 60f));
            Assert.Equal(1d, clock.GetElapsedDays(day.AddDays(21), 1f, 60f));
        }

        [Fact]
        public void NativeClockRecoveryDoesNotDoubleCountFallbackTime()
        {
            var clock = new SeasonGameClock();
            var day = new DateTime(2026, 1, 1);
            clock.GetElapsedDays(day, 1f, 60f);
            Assert.Equal(0.5d, clock.GetElapsedDays(null, 1800f, 60f));
            Assert.Equal(0d, clock.GetElapsedDays(day.AddHours(12), 1f, 60f));
        }

        [Fact]
        public void RewindAndInvalidFallbackDoNotMoveTheCalendar()
        {
            var clock = new SeasonGameClock();
            var day = new DateTime(2026, 1, 1);
            clock.GetElapsedDays(day, 1f, 60f);
            Assert.Equal(0d, clock.GetElapsedDays(day.AddDays(-2), 1f, 60f));
            Assert.Equal(0d, clock.GetElapsedDays(day.AddDays(100), 1f, 60f));
            Assert.Equal(0d, clock.GetElapsedDays(null, float.NaN, 60f));
            Assert.Equal(0d, clock.GetElapsedDays(null, -1f, 60f));
        }
    }
}
