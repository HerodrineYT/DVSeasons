using System;
using System.IO;
using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public class BlizzardTests
    {
        private readonly Random random = new Random(781);
        private static readonly long Now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc).Ticks;
        [Fact]
        public void ForecastRangesAndExactlyThreeNotices()
        {
            for (int i = 0; i < 500; i++)
            {
                var s = new BlizzardState(); s.Schedule(random, Now);
                Assert.True(s.IsValid()); Assert.Equal(BlizzardNotice.Early, s.Notice);
                Assert.InRange(s.StartHours - s.Hours, 2, 4);
                Assert.InRange(s.EndHours - s.StartHours, 4, 36);
                Assert.InRange(s.EndHours - s.EndingHours, 1, 2);
                s.Advance(s.StartHours - 1, true, true, random, Now);
                Assert.Equal(BlizzardNotice.OneHour, s.Notice); Assert.Equal(2u, s.NoticeSequence);
                s.Advance(.001, true, true, random, Now); Assert.Equal(2u, s.NoticeSequence);
                s.Advance(s.StartHours - s.Hours + .2, true, true, random, Now);
                Assert.True(s.Active); Assert.Equal(1f, s.Strength);
                s.Advance(s.OutageHours - s.Hours + .01, true, true, random, Now); Assert.True(s.Blackout);
                s.Advance(s.EndingHours - s.Hours + .001, true, true, random, Now);
                Assert.Equal(BlizzardNotice.Ending, s.Notice); Assert.Equal(3u, s.NoticeSequence);
                var end = s.EndHours;
                s.Advance(end - s.Hours + .001, true, true, random, Now);
                Assert.False(s.Active); Assert.False(s.Blackout);
                Assert.Equal(end + 240, s.EarliestStartHours, 6);
            }
        }
        [Fact]
        public void CooldownIsFromEndAndSurvivesCancelSummerAndSerialization()
        {
            var s = new BlizzardState(); s.Schedule(random, Now);
            s.Advance(s.EndHours + 1, true, true, random, Now);
            double earliest = s.EndHours + 240;
            s.Advance(1, false, true, random, Now);
            s.Advance(1, true, false, random, Now);
            using (var stream = new MemoryStream())
            {
                s.WriteTo(new BinaryWriter(stream)); stream.Position = 0;
                s = BlizzardState.ReadFrom(new BinaryReader(stream));
            }
            Assert.True(s.IsValid()); s.Schedule(random, Now);
            Assert.True(s.StartHours >= earliest); Assert.Equal(BlizzardNotice.None, s.Notice);
            s.Cancel(); s.Schedule(random, Now); Assert.True(s.StartHours >= earliest);
            s.Advance(s.EarlyHours - s.Hours, true, true, random, Now);
            Assert.Equal(BlizzardNotice.Early, s.Notice);
        }
        [Fact]
        public void EndingStormEarlyStillRequiresTenDays()
        {
            var s = new BlizzardState(); s.Schedule(random, Now);
            s.Advance(s.StartHours + .5, true, true, random, Now);
            s.Cancel(); double earliest = s.Hours + 240;
            s.Schedule(random, Now); Assert.True(s.StartHours >= earliest);
        }
        [Fact]
        public void SleepingSkipsObsoleteForecastsAndNoWinterMeansNoStorm()
        {
            var s = new BlizzardState(); s.Schedule(random, Now);
            s.Advance(s.EndHours + 1, true, true, random, Now);
            Assert.Equal(BlizzardNotice.None, s.Notice); Assert.Equal(1u, s.NoticeSequence);
            s.Advance(500, false, true, random, Now); Assert.False(s.Scheduled);
            s.Advance(double.NaN, false, true, random, Now); Assert.True(s.IsValid());
        }
        [Fact]
        public void HostPacketRetainsOutageNoticeAndCooldown()
        {
            var state = new SeasonNetworkState { Protocol = SeasonNetworkState.CurrentProtocol };
            state.Blizzard.Schedule(random, Now);
            state.Blizzard.Advance(state.Blizzard.OutageHours + .1, true, true, random, Now);
            state.Blizzard.HostUtcTicks = Now;
            using (var stream = new MemoryStream())
            {
                state.WriteTo(new BinaryWriter(stream)); stream.Position = 0;
                var copy = SeasonNetworkState.ReadFrom(new BinaryReader(stream));
                Assert.True(copy.Blizzard.Blackout); Assert.True(copy.Blizzard.IsValid());
                Assert.Equal(state.Blizzard.NoticeUtcTicks, copy.Blizzard.NoticeUtcTicks);
                Assert.Equal(state.Blizzard.EndHours, copy.Blizzard.EndHours);
            }
        }
        [Fact]
        public void RejectsMalformedNetworkTimeline()
        {
            var s = new BlizzardState(); s.Schedule(random, Now);
            s.EndHours = s.StartHours - 1; Assert.False(s.IsValid());
            s = new BlizzardState { Hours = double.PositiveInfinity }; Assert.False(s.IsValid());
            s = new BlizzardState { EarliestStartHours = double.NaN }; Assert.False(s.IsValid());
        }
    }
}
