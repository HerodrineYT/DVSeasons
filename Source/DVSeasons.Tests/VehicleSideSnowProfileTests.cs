using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public class VehicleSideSnowProfileTests
    {
        [Fact] public void ParkedTrainNeedsWindAndWindwardSideAccumulatesMore()
        {
            var calm = VehicleSideSnowProfile.ImpactFactor(0f, 0f, 0f, 1f, 0f);
            Assert.Equal(0f, VehicleSideSnowProfile.Advance(0f, 0f, 1f, -10f, 0f, true, 120f, calm));
            var windward = VehicleSideSnowProfile.ImpactFactor(0f, -5f, 0f, 1f, 0f);
            var leeward = VehicleSideSnowProfile.ImpactFactor(0f, -5f, 0f, -1f, 0f);
            var right = VehicleSideSnowProfile.Advance(0f, 0f, .11f, -10f, 0f, true, 90f, windward);
            var left = VehicleSideSnowProfile.Advance(0f, 0f, .11f, -10f, 0f, true, 90f, leeward);
            Assert.True(right > .04f);
            Assert.True(right > left * 10f);
        }

        [Fact] public void AccumulationFadesInSmoothlyAndDependsOnSnowfall()
        {
            Assert.InRange(VehicleSideSnowProfile.SpeedFactor(30.1f), 0f, .0001f);
            Assert.Equal(.5f, VehicleSideSnowProfile.SpeedFactor(50f), 5);
            Assert.Equal(1f, VehicleSideSnowProfile.Advance(0f, 70f, 1f, -10f, 0f, true, 90f), 5);
            Assert.Equal(.5f, VehicleSideSnowProfile.Advance(0f, 70f, .5f, -10f, 0f, true, 90f), 5);
            Assert.Equal(0f, VehicleSideSnowProfile.Advance(0f, 120f, 0f, -10f, 0f, true, 600f));
        }

        [Fact] public void ShelterBlocksNewSnowButDoesNotEraseExistingLayer()
        {
            Assert.Equal(0f, VehicleSideSnowProfile.Advance(0f, 70f, 1f, -10f, 0f, false, 120f));
            Assert.Equal(.7f, VehicleSideSnowProfile.Advance(.7f, 70f, 1f, -10f, 0f, false, 120f));
            Assert.Equal(.7f, VehicleSideSnowProfile.Advance(.7f, 0f, 0f, -10f, 0f, true, 3600f));
        }

        [Fact] public void HeatLimitsStickingAndMeltsAfterTheTrainStops()
        {
            var diesel = VehicleSideSnowProfile.Advance(0f, 70f, 1f, -10f, .5f, true, 120f);
            var steam = VehicleSideSnowProfile.Advance(0f, 70f, 1f, -10f, 1f, true, 120f);
            Assert.InRange(diesel, .2f, .5f);
            Assert.InRange(steam, 0f, diesel);
            Assert.InRange(VehicleSideSnowProfile.Advance(diesel, 0f, 0f, -10f, .5f, true, 120f), 0f, diesel * .3f);
        }

        [Fact] public void PositiveAirTemperatureThawsWithoutAnEngine()
        {
            Assert.InRange(VehicleSideSnowProfile.Advance(1f, 70f, 1f, 10f, 0f, true, 120f), 0f, .01f);
            Assert.Equal(0f, VehicleSideSnowProfile.Advance(0f, 70f, 1f, 10f, 0f, true, 120f));
        }

        [Fact] public void UpdateCadenceDoesNotChangeAmount()
        {
            var whole = VehicleSideSnowProfile.Advance(.1f, 60f, .8f, -10f, .5f, true, 90f);
            float split = .1f;
            for (int i = 0; i < 360; i++) split = VehicleSideSnowProfile.Advance(split, 60f, .8f, -10f, .5f, true, .25f);
            Assert.Equal(whole, split, 5);
        }

        [Fact] public void LightSnowAtThirtyFiveKmhAccumulatesInsteadOfRoundingToZero()
        {
            var impact = VehicleSideSnowProfile.ImpactFactor(35f, 0f, -35f / 3.6f, 1f, 0f);
            float amount = 0f;
            for(int i = 0; i < 2400; i++)
                amount = VehicleSideSnowProfile.Advance(amount, 35f, .11f, -15.7f, 0f, true, .25f, impact);
            Assert.InRange(amount, .05f, .08f);
        }

        [Fact] public void VerySmallPositiveGrowthSurvivesDifferentUpdateCadencesAndHeating()
        {
            var impact = VehicleSideSnowProfile.ImpactFactor(0f, -.01f, 0f, 1f, 0f);
            var whole = VehicleSideSnowProfile.Advance(0f, 0f, .0011f, -10f, .5f, true, 60f, impact);
            float split = 0f;
            for(int i = 0; i < 240; i++)
                split = VehicleSideSnowProfile.Advance(split, 0f, .0011f, -10f, .5f, true, .25f, impact);
            Assert.True(split > 0f);
            Assert.InRange(split / whole, .9999f, 1.0001f);
            var cold = VehicleSideSnowProfile.Advance(0f, 0f, .0011f, -10f, 0f, true, 60f, impact);
            Assert.InRange(split, 0f, cold * .6f);
        }

        [Fact] public void InvalidOrPausedFramesCannotCreateCorruptCoverage()
        {
            Assert.Equal(.5f, VehicleSideSnowProfile.Advance(.5f, 70f, 1f, -10f, 1f, true, 0f));
            Assert.Equal(.5f, VehicleSideSnowProfile.Advance(.5f, 70f, 1f, -10f, 1f, true, float.NaN));
            Assert.Equal(.5f, VehicleSideSnowProfile.Advance(.5f, 70f, 1f, float.NaN, 1f, true, 1f));
            Assert.Equal(0f, VehicleSideSnowProfile.Advance(float.NaN, float.NaN, 1f, -10f, 0f, true, 120f));
        }

        [Fact] public void RelativeWindSelectsLeadingEndAndCrosswindSide()
        {
            var front = VehicleSideSnowProfile.ImpactFactor(70f, 0f, -20f, 0f, 1f);
            var rear = VehicleSideSnowProfile.ImpactFactor(70f, 0f, -20f, 0f, -1f);
            Assert.True(front > rear * 4f);
            Assert.Equal(front, VehicleSideSnowProfile.ImpactFactor(70f, 0f, 20f, 0f, -1f));
            var right = VehicleSideSnowProfile.ImpactFactor(70f, -15f, -20f, 1f, 0f);
            var left = VehicleSideSnowProfile.ImpactFactor(70f, -15f, -20f, -1f, 0f);
            Assert.True(right > left * 4f);
            Assert.True(right > VehicleSideSnowProfile.ImpactFactor(70f, -5f, -20f, 1f, 0f));
        }

        [Fact] public void ChangingWindDoesNotMoveDepositedSnowToAnotherFace()
        {
            var right = VehicleSideSnowProfile.Advance(0f, 70f, 1f, -10f, 0f, true, 60f,
                VehicleSideSnowProfile.ImpactFactor(70f, -15f, -20f, 1f, 0f));
            var left = VehicleSideSnowProfile.Advance(0f, 70f, 1f, -10f, 0f, true, 60f,
                VehicleSideSnowProfile.ImpactFactor(70f, -15f, -20f, -1f, 0f));
            Assert.True(right > left);
            Assert.Equal(right, VehicleSideSnowProfile.Advance(right, 70f, 0f, -10f, 0f, true, 60f,
                VehicleSideSnowProfile.ImpactFactor(70f, 15f, -20f, 1f, 0f)));
            Assert.Equal(left, VehicleSideSnowProfile.Advance(left, 0f, 0f, -10f, 0f, true, 60f,
                VehicleSideSnowProfile.ImpactFactor(0f, 15f, 0f, -1f, 0f)));
        }
    }
}
