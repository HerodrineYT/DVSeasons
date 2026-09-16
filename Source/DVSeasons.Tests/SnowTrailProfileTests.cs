using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public class SnowTrailProfileTests
    {
        [Theory]
        [InlineData(0,1,0)]
        [InlineData(25,1,0)]
        [InlineData(40,1,.104)]
        [InlineData(62.5,.5,.25)]
        [InlineData(100,1,1)]
        [InlineData(160,.4,.4)]
        public void TrailStartsAboveTwentyFiveAndScalesWithSnow(float speed,float coverage,float expected)
        {Assert.Equal(expected,SnowTrailProfile.Intensity(speed,coverage),5);}

        [Fact]
        public void EmissionAppearsGraduallyAboveThreshold()
        {
            Assert.Equal(0f, SnowTrailProfile.EmissionRate(25f, 1f, -15f, 0f));
            var justStarted = SnowTrailProfile.EmissionRate(25.01f, 1f, -15f, 0f);
            Assert.InRange(justStarted, 0f, .001f);
            Assert.True(justStarted > 0f);
            Assert.True(SnowTrailProfile.EmissionRate(26f, 1f, -15f, 0f) < .1f);
            Assert.Equal(SnowTrailProfile.MaximumEmissionRate,
                SnowTrailProfile.EmissionRate(100f, 1f, -15f, 0f), 5);
        }

        [Fact]
        public void EmissionIsBoundedAndIncreasesWithSpeedAndSnow()
        {
            float previous = 0f;
            for (int speed = -20; speed <= 400; speed++)
            {
                var rate = SnowTrailProfile.EmissionRate(speed, 1f, -15f, 0f);
                Assert.InRange(rate, previous, SnowTrailProfile.MaximumEmissionRate);
                previous = rate;
            }
            previous = 0f;
            for (int hundredths = -20; hundredths <= 120; hundredths++)
            {
                var rate = SnowTrailProfile.EmissionRate(80f, hundredths / 100f, -15f, 0f);
                Assert.InRange(rate, previous, SnowTrailProfile.MaximumEmissionRate);
                previous = rate;
            }
            Assert.Equal(0f, SnowTrailProfile.EmissionRate(100f, 0f, -15f, 0f));
            Assert.Equal(0f, SnowTrailProfile.EmissionRate(100f, -1f, -15f, 0f));
        }

        [Fact]
        public void WarmingAndRainSuppressPowderContinuously()
        {
            var cold = SnowTrailProfile.EmissionRate(80f, 1f, -15f, 0f);
            Assert.True(SnowTrailProfile.EmissionRate(80f, 1f, 0f, 0f) < cold * .2f);
            Assert.True(SnowTrailProfile.EmissionRate(80f, 1f, -15f, 1f) < cold * .11f);

            float previous = 1f;
            for (int tenths = -400; tenths <= 150; tenths++)
            {
                var factor = SnowTrailProfile.PowderFactor(tenths / 10f, 0f);
                Assert.InRange(factor, 0f, previous);
                Assert.InRange(previous - factor, 0f, .013f);
                previous = factor;
            }
            previous = 1f;
            for (int hundredths = -20; hundredths <= 120; hundredths++)
            {
                var factor = SnowTrailProfile.PowderFactor(-15f, hundredths / 100f);
                Assert.InRange(factor, 0f, previous);
                Assert.InRange(previous - factor, 0f, .014f);
                previous = factor;
            }
            Assert.Equal(1f, SnowTrailProfile.PowderFactor(-15f, -1f));
            Assert.Equal(SnowTrailProfile.PowderFactor(2f, 1f),
                SnowTrailProfile.PowderFactor(float.MaxValue, float.MaxValue));
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void InvalidInputCannotProduceParticles(float invalid)
        {
            Assert.Equal(0f, SnowTrailProfile.Intensity(invalid, 1f));
            Assert.Equal(0f, SnowTrailProfile.Intensity(80f, invalid));
            Assert.Equal(0f, SnowTrailProfile.PowderFactor(invalid, 0f));
            Assert.Equal(0f, SnowTrailProfile.PowderFactor(-15f, invalid));
            Assert.Equal(0f, SnowTrailProfile.EmissionRate(invalid, 1f, -15f, 0f));
            Assert.Equal(0f, SnowTrailProfile.EmissionRate(80f, invalid, -15f, 0f));
            Assert.Equal(0f, SnowTrailProfile.EmissionRate(80f, 1f, invalid, 0f));
            Assert.Equal(0f, SnowTrailProfile.EmissionRate(80f, 1f, -15f, invalid));
        }

        [Theory]
        [InlineData(float.MinValue, .65f, .7f)]
        [InlineData(-100f, .65f, .7f)]
        [InlineData(0f, .65f, .7f)]
        [InlineData(25f, .65f, .871875f)]
        [InlineData(62.5f, 1.425f, 1.4519531f)]
        [InlineData(100f, 2.2f, 1.8f)]
        [InlineData(float.MaxValue, 2.2f, 1.8f)]
        public void ParticleSizesHaveBoundedSpeedEndpoints(float speed, float main, float wheel)
        {
            Assert.Equal(main, SnowTrailProfile.MainSizeMultiplier(speed), 5);
            Assert.Equal(wheel, SnowTrailProfile.WheelSizeMultiplier(speed), 5);
        }

        [Fact]
        public void ParticleSizesIncreaseSmoothlyWithSpeed()
        {
            float previousMain = .65f, previousWheel = .7f;
            for (int tenths = -100; tenths <= 1200; tenths++)
            {
                var speed = tenths / 10f;
                var main = SnowTrailProfile.MainSizeMultiplier(speed);
                var wheel = SnowTrailProfile.WheelSizeMultiplier(speed);
                Assert.InRange(main, previousMain, 2.2f);
                Assert.InRange(wheel, previousWheel, 1.8f);
                Assert.InRange(main - previousMain, 0f, .0032f);
                Assert.InRange(wheel - previousWheel, 0f, .0017f);
                previousMain = main;
                previousWheel = wheel;
            }
            Assert.InRange(SnowTrailProfile.MainSizeMultiplier(25.01f) - .65f, 0f, .00001f);
            Assert.InRange(2.2f - SnowTrailProfile.MainSizeMultiplier(99.99f), 0f, .00001f);
            Assert.InRange(SnowTrailProfile.WheelSizeMultiplier(.01f) - .7f, 0f, .00001f);
            Assert.InRange(1.8f - SnowTrailProfile.WheelSizeMultiplier(99.99f), 0f, .00001f);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void InvalidSpeedUsesSmallSafeParticleSizes(float invalid)
        {
            Assert.Equal(.65f, SnowTrailProfile.MainSizeMultiplier(invalid));
            Assert.Equal(.7f, SnowTrailProfile.WheelSizeMultiplier(invalid));
        }
    }
}
