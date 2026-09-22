using System;
using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public sealed class SnowRenderBenchmarkSessionTests
    {
        [Fact]
        public void RequiresExplicitStartAndHasFiveSecondDelay()
        {
            var session = new SnowRenderBenchmarkSession();
            session.Tick(100d, true);
            Assert.False(session.Active);
            Assert.Equal(0, session.CompletedCount);
            AssertNormalRendering(session);
            session.Start();
            Assert.True(session.Active);
            Assert.True(session.Waiting);
            Assert.Equal(55d, session.SecondsRemaining);
            session.Tick(4d, true);
            Assert.True(session.Waiting);
            Assert.Equal(51d, session.SecondsRemaining);
            AssertNormalRendering(session);
            session.Tick(1d, true);
            Assert.False(session.Waiting);
            Assert.True(session.WarmingUp);
            Assert.Equal(0, session.PhaseIndex);
            Assert.Equal(50d, session.SecondsRemaining);
        }

        [Fact]
        public void MeasuresAllFivePhasesAndRestoresRenderingAfterFiftyFiveSeconds()
        {
            var session = new SnowRenderBenchmarkSession();
            session.Start();
            session.Tick(5d, true);
            for (int phase = 0; phase < SnowRenderBenchmarkSession.PhaseCount; phase++)
            {
                Assert.Equal((SnowRenderBenchmarkPhase)phase, session.Phase);
                Assert.Equal(phase == 1 || phase == 3, session.SkipVehicleSurfaces);
                Assert.Equal(phase == 2 || phase == 3, session.SkipShading);
                Assert.Equal(phase == 3, session.SkipAll);
                session.Tick(2d, true);
                Assert.False(session.WarmingUp);
                for (int frame = 0; frame < 512; frame++) session.Tick(1d / 64d, true);
                Assert.Equal(phase + 1, session.CompletedCount);
                var result = session.GetResult(phase);
                Assert.Equal((SnowRenderBenchmarkPhase)phase, result.Phase);
                Assert.Equal(512, result.FrameCount);
                Assert.Equal(8d, result.MeasuredSeconds);
                Assert.Equal(64d, result.Fps);
                Assert.Equal(15.625d, result.MinimumFrameMilliseconds);
                Assert.Equal(15.625d, result.MaximumFrameMilliseconds);
                Assert.False(result.Contaminated);
            }
            Assert.False(session.Active);
            Assert.Equal(0d, session.SecondsRemaining);
            AssertNormalRendering(session);
            for (int phase = 0; phase < SnowRenderBenchmarkSession.PhaseCount; phase++)
            {
                Assert.True(session.TryTakeCompletedResult(out var result));
                Assert.Equal((SnowRenderBenchmarkPhase)phase, result.Phase);
            }
            Assert.False(session.TryTakeCompletedResult(out _));
        }

        [Fact]
        public void BoundaryFrameBelongsOnlyToThePhaseThatActuallyRenderedIt()
        {
            var session = new SnowRenderBenchmarkSession();
            session.Start();
            session.Tick(5d, true);
            session.Tick(2d, true);
            session.Tick(7.5d, true);
            Assert.Equal(0, session.CompletedCount);
            session.Tick(.75d, true);
            Assert.Equal(SnowRenderBenchmarkPhase.WithoutVehicleSurfaces, session.Phase);
            Assert.Equal(10d, session.PhaseSecondsRemaining);
            var baseline = session.GetResult(0);
            Assert.Equal(2, baseline.FrameCount);
            Assert.Equal(8.25d, baseline.MeasuredSeconds);
            Assert.Equal(750d, baseline.MinimumFrameMilliseconds);
            Assert.Equal(7500d, baseline.MaximumFrameMilliseconds);
            session.Tick(2d, true);
            for (int frame = 0; frame < 512; frame++) session.Tick(1d / 64d, true);
            Assert.Equal(8d, session.GetResult(1).MeasuredSeconds);
            Assert.Equal(64d, session.GetResult(1).Fps);
        }

        [Fact]
        public void WarmupCrossingAndLargeStartDelayCannotLeakIntoMeasurements()
        {
            var session = new SnowRenderBenchmarkSession();
            session.Start();
            session.Tick(100d, false);
            Assert.Equal(10d, session.PhaseSecondsRemaining);
            session.Tick(1.5d, true);
            session.Tick(1d, true);
            Assert.False(session.WarmingUp);
            for (int frame = 0; frame < 480; frame++) session.Tick(1d / 64d, true);
            var result = session.GetResult(0);
            Assert.Equal(480, result.FrameCount);
            Assert.Equal(7.5d, result.MeasuredSeconds);
            Assert.Equal(64d, result.Fps);
            Assert.False(result.Contaminated);
        }

        [Fact]
        public void MissingRendersAdvanceTimeButDoNotFabricateFpsSamples()
        {
            var session = new SnowRenderBenchmarkSession();
            session.Start();
            session.Tick(5d, false);
            session.Tick(2d, false);
            for (int frame = 0; frame < 256; frame++) session.Tick(1d / 64d, true);
            session.Tick(2d, false);
            session.Tick(2d, false);
            var result = session.GetResult(0);
            Assert.Equal(256, result.FrameCount);
            Assert.Equal(4d, result.MeasuredSeconds);
            Assert.Equal(64d, result.Fps);
            Assert.Equal(2, result.MissingRenderFrames);
            Assert.Equal(4d, result.MissingRenderSeconds);
            Assert.True(result.Contaminated);
            session.Tick(2d, false);
            session.Tick(8d, false);
            var empty = session.GetResult(1);
            Assert.Equal(0, empty.FrameCount);
            Assert.Equal(0d, empty.Fps);
            Assert.Equal(0d, empty.MinimumFrameMilliseconds);
            Assert.True(empty.Contaminated);
        }

        [Fact]
        public void CancellationAndRestartCannotRetainDisableFlagsOrOldResults()
        {
            var session = new SnowRenderBenchmarkSession();
            session.Start();
            session.Tick(5d, true);
            for (int phase = 0; phase < 3; phase++)
            {
                session.Tick(2d, true);
                session.Tick(8d, true);
            }
            Assert.True(session.SkipAll);
            session.Cancel();
            Assert.False(session.Active);
            AssertNormalRendering(session);
            Assert.False(session.TryTakeCompletedResult(out _));
            Assert.Throws<ArgumentOutOfRangeException>(() => session.GetResult(0));
            session.Tick(30d, true);
            Assert.False(session.Active);
            session.Start();
            Assert.True(session.Waiting);
            Assert.Equal(55d, session.SecondsRemaining);
            AssertNormalRendering(session);
        }

        [Fact]
        public void InvalidDeltasDoNotAdvanceOrPolluteSession()
        {
            var session = new SnowRenderBenchmarkSession();
            session.Start();
            foreach (double delta in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, 0d })
                session.Tick(delta, true);
            Assert.Equal(55d, session.SecondsRemaining);
            session.Tick(5d, true);
            session.Tick(2d, true);
            session.Tick(double.NaN, true);
            session.Tick(-1d, false);
            session.Tick(8d, true);
            Assert.Equal(1, session.GetResult(0).FrameCount);
            Assert.Equal(8d, session.GetResult(0).MeasuredSeconds);
            Assert.False(session.GetResult(0).Contaminated);
        }

        [Fact]
        public void WarmFrameAndResultPathDoNotAllocate()
        {
            var session = new SnowRenderBenchmarkSession();
            session.Start();
            session.Tick(5d, true);
            session.Tick(2d, true);
            session.Tick(.125d, true);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 63; frame++) session.Tick(.125d, true);
            bool available = session.TryTakeCompletedResult(out var result);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(available);
            Assert.Equal(64, result.FrameCount);
            Assert.Equal(0, allocated);
        }

        private static void AssertNormalRendering(SnowRenderBenchmarkSession session)
        {
            Assert.False(session.SkipAll);
            Assert.False(session.SkipShading);
            Assert.False(session.SkipVehicleSurfaces);
        }
    }
}
