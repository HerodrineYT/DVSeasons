using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public sealed class WinterTrackTextureProfileTests
    {
        [Theory]
        [InlineData(0f, WinterTrackTextureStage.SnowFree, WinterTrackTextureStage.Early, 0f)]
        [InlineData(WinterTrackTextureProfile.EarlyAnchor,
            WinterTrackTextureStage.SnowFree, WinterTrackTextureStage.Early, 1f)]
        [InlineData(WinterTrackTextureProfile.MiddleAnchor,
            WinterTrackTextureStage.Early, WinterTrackTextureStage.Middle, 1f)]
        [InlineData(1f, WinterTrackTextureStage.Middle, WinterTrackTextureStage.Late, 1f)]
        public void SnowAnchorsSelectExpectedStages(float snowAmount,
            WinterTrackTextureStage expectedLower,
            WinterTrackTextureStage expectedUpper,
            float expectedBlend)
        {
            WinterTrackTextureProfile.GetBlend(snowAmount,
                out var lower, out var upper, out var blend);

            Assert.Equal(expectedLower, lower);
            Assert.Equal(expectedUpper, upper);
            Assert.Equal(expectedBlend, blend, 4);
        }

        [Fact]
        public void DecreasingSnowTraversesStagesInReverseOrder()
        {
            var samples = new[] { 1f, 0.80f, 0.62f, 0.45f, 0.28f, 0.12f, 0f };
            var previousPosition = float.MaxValue;

            foreach (var sample in samples)
            {
                WinterTrackTextureProfile.GetBlend(sample,
                    out var lower, out var upper, out var blend);
                var position = (int)lower + (((int)upper - (int)lower) * blend);
                Assert.True(position <= previousPosition + 0.0001f);
                previousPosition = position;
            }
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.NegativeInfinity)]
        public void InvalidOrNegativeSnowUsesSnowFreeAnchor(float snowAmount)
        {
            WinterTrackTextureProfile.GetBlend(snowAmount,
                out var lower, out var upper, out var blend);

            Assert.Equal(WinterTrackTextureStage.SnowFree, lower);
            Assert.Equal(WinterTrackTextureStage.Early, upper);
            Assert.Equal(0f, blend);
        }
    }
}
