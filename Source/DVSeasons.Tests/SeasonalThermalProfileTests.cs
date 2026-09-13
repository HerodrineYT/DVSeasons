using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public class SeasonalThermalProfileTests
    {
        [Theory]
        [InlineData(-30f, 250f, 1f, -13750f)]
        [InlineData(25f, 250f, 1f, 0f)]
        [InlineData(30f, 250f, 1f, 1250f)]
        [InlineData(-30f, 250f, 0.4f, -5500f)]
        public void CoolerCorrectionMovesNativeTargetToSeasonalAmbient(float ambient,
            float conductance, float effect, float expected)
        {
            Assert.Equal(expected,
                SeasonalThermalProfile.CoolerHeatCorrection(ambient, conductance, effect), 3);
        }

        [Fact]
        public void WinterCoolsHotBrakeFasterThanSummer()
        {
            var winter = SeasonalThermalProfile.StepBrakeTemperature(300f, 0f, 0f, 10f, -30f);
            var summer = SeasonalThermalProfile.StepBrakeTemperature(300f, 0f, 0f, 10f, 30f);
            Assert.True(winter < summer);
            Assert.Equal(286.76f, winter, 2);
            Assert.Equal(289.16f, summer, 2);
        }

        [Fact]
        public void BrakingHeatStillUsesNativeMechanicalInput()
        {
            var stopped = SeasonalThermalProfile.StepBrakeTemperature(25f, 0f, 1f, 1f, 25f);
            var moving = SeasonalThermalProfile.StepBrakeTemperature(25f, 20f, 0.5f, 1f, 25f);
            Assert.Equal(25f, stopped);
            Assert.Equal(40f, moving);
        }

        [Fact]
        public void ColdBrakeMayCoolBelowVanillaTwentyFiveDegreeFloor()
        {
            var temperature = 25f;
            for (var second = 0; second < 300; second++)
                temperature = SeasonalThermalProfile.StepBrakeTemperature(
                    temperature, 0f, 0f, 1f, -30f);
            Assert.InRange(temperature, -30f, 0f);
        }

        [Theory]
        [InlineData(500f, -30f, 500f)]
        [InlineData(1000f, 25f, 1000f)]
        [InlineData(1400f, -30f, 1380.75f)]
        [InlineData(1400f, 30f, 1401.75f)]
        public void FireboxRetainsCombustionFloorAndRespondsToIntakeAir(float nativeTarget,
            float ambient, float expected)
        {
            Assert.Equal(expected,
                SeasonalThermalProfile.AdjustFireboxCombustionTarget(nativeTarget, ambient), 3);
        }

        [Theory]
        [InlineData(15f, -30f, 18.3f)]
        [InlineData(15f, 25f, 15f)]
        [InlineData(15f, 30f, 14.7f)]
        public void FireboxWarmupRateRespondsEvenAtMinimumCombustion(float nativeSeconds,
            float ambient, float expected)
        {
            Assert.Equal(expected,
                SeasonalThermalProfile.AdjustFireboxResponseTime(nativeSeconds, ambient), 3);
        }

        [Theory]
        [InlineData(599f, 0f, 1f)]
        [InlineData(800f, 0.5f, 0.56f)]
        [InlineData(1000f, 1f, 0.12f)]
        public void NativeBrakeOverheatThresholdsRemainUnchanged(float temperature,
            float expectedOverheat, float expectedForce)
        {
            Assert.Equal(expectedOverheat,
                SeasonalThermalProfile.BrakeOverheatFraction(temperature), 4);
            Assert.Equal(expectedForce,
                SeasonalThermalProfile.BrakeForceFactor(temperature), 4);
        }

        [Fact]
        public void InvalidNetworkTemperatureFallsBackToNeutralVanillaClimate()
        {
            Assert.Equal(25f, SeasonalThermalProfile.NormalizeAmbient(float.NaN));
            Assert.Equal(25f, SeasonalThermalProfile.NormalizeAmbient(float.PositiveInfinity));
        }
    }
}
