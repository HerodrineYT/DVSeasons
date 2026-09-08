using System;
using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public class SurfaceSnowAccumulationTests
    {
        [Theory]
        [InlineData(false, 0.95f)]
        [InlineData(true, 0.98f)]
        public void StagesGrowInAreaAndReverseExactly(bool roof, float maximum)
        {
            Assert.Equal(0, SurfaceSnowAccumulation.Coverage(0, roof));
            Assert.Equal(0.22f, SurfaceSnowAccumulation.Coverage(0.28f, roof), 4);
            Assert.Equal(0.65f, SurfaceSnowAccumulation.Coverage(0.62f, roof), 4);
            Assert.Equal(maximum, SurfaceSnowAccumulation.Coverage(1, roof), 4);
            for (var rank = 0; rank < 256; rank++)
            {
                var previous = 0f;
                for (var step = 0; step <= 1000; step++)
                {
                    var coverage = SurfaceSnowAccumulation.Coverage(step / 1000f, roof);
                    var weight = SurfaceSnowAccumulation.Weight((byte)rank, coverage);
                    Assert.InRange(weight, previous, 1f);
                    previous = weight;
                }
                Assert.Equal(0, SurfaceSnowAccumulation.Weight((byte)rank, 0));
            }
        }

        [Fact]
        public void MaskIsDeterministicAndActualAreaMatchesStages()
        {
            var ranks = SurfaceSnowAccumulation.CreateRanks(128);
            Assert.Equal(ranks, SurfaceSnowAccumulation.CreateRanks(128));
            foreach (var snow in new[] { 0f, 0.28f, 0.62f, 1f })
            {
                var coverage = SurfaceSnowAccumulation.Coverage(snow, false);
                double sum = 0;
                foreach (var rank in ranks) sum += SurfaceSnowAccumulation.Weight(rank, coverage);
                Assert.InRange(sum / ranks.Length, Math.Max(0, coverage - 0.015), coverage + 0.015);
            }
        }

        [Fact]
        public void StageBoundariesAreContinuousAndInputsAreClamped()
        {
            foreach (var edge in new[] { 0.28f, 0.62f, 1f })
                Assert.InRange(Math.Abs(SurfaceSnowAccumulation.Coverage(edge - 0.00001f, true) -
                    SurfaceSnowAccumulation.Coverage(edge + 0.00001f, true)), 0, 0.0001f);
            Assert.Equal(0, SurfaceSnowAccumulation.Coverage(float.NaN, true));
            Assert.Equal(0, SurfaceSnowAccumulation.Coverage(float.NegativeInfinity, false));
            Assert.Equal(0.98f, SurfaceSnowAccumulation.Coverage(float.PositiveInfinity, true));
        }
    }
}
