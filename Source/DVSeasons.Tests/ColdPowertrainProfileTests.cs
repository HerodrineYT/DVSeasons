using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public class ColdPowertrainProfileTests
    {
        [Theory]
        [InlineData(-10, 5)]
        [InlineData(-17.5f, 8.5f)]
        [InlineData(-25, 12)]
        [InlineData(-30, 12)]
        public void FullyColdStarterUsesSpecifiedHoldAndHotRestartIsNative(float ambient, float seconds)
        {
            Assert.Equal(seconds, ColdPowertrainProfile.StarterHoldSeconds(ambient, ambient, 2), 3);
            Assert.Equal(2, ColdPowertrainProfile.StarterHoldSeconds(ambient, 70, 2));
        }

        [Fact]
        public void FewMinutesRunningEaseRestartAndCoolingRestoresColdHold()
        {
            float warm = ColdPowertrainProfile.StepEngineBlockTemperature(-30,-30,true,180);
            float warmHold = ColdPowertrainProfile.StarterHoldSeconds(-30,warm,3.5f);
            Assert.InRange(warm,35,40);
            Assert.InRange(warmHold,3.5f,4);
            float shortStop = ColdPowertrainProfile.StepEngineBlockTemperature(warm,-30,false,60);
            Assert.InRange(ColdPowertrainProfile.StarterHoldSeconds(-30,shortStop,3.5f),3.5f,4.5f);
            float cold = ColdPowertrainProfile.StepEngineBlockTemperature(warm,-30,false,1800);
            Assert.InRange(ColdPowertrainProfile.StarterHoldSeconds(-30,cold,3.5f),11.8f,12);
            Assert.Equal(-30,ColdPowertrainProfile.StepEngineBlockTemperature(-30,-30,false,180));
        }

        [Fact]
        public void De6MissedPrimerAndInterruptedHoldStallButCompleteHoldPasses()
        {
            var idle = new ColdIdleStabilization(); idle.Begin();
            Assert.False(idle.Advance(4.99f, false));
            Assert.True(idle.Advance(.02f, false));
            idle.Begin();
            Assert.False(idle.Advance(1, false));
            Assert.False(idle.Advance(1, true));
            Assert.True(idle.Advance(.1f, false));
            idle.Begin();
            Assert.False(idle.Advance(4.5f, false));
            for(int i=0;i<150;i++) Assert.False(idle.Advance(.02f,true));
            Assert.False(idle.Active);
            Assert.False(idle.Advance(10,false));
        }
        [Fact]
        public void De6AllowsOpeningAtFiveSecondsButRejectsLateOpening()
        {
            var idle = new ColdIdleStabilization(); idle.Begin();
            Assert.False(idle.Advance(4.98f,false));
            Assert.False(idle.Advance(.02f,true));
            for(int i=0;i<150;i++)Assert.False(idle.Advance(.02f,true));
            Assert.False(idle.Active);
            idle.Begin();
            Assert.False(idle.Advance(4.99f,false));
            Assert.True(idle.Advance(.03f,true));
            Assert.False(idle.Active);
        }
        [Fact]
        public void ColdStartSlowsProgressivelyButWarmRestartStaysNative()
        {
            Assert.Equal(1, ColdPowertrainProfile.DieselStartMultiplier(80));
            Assert.Equal(1, ColdPowertrainProfile.DieselStartMultiplier(25));
            Assert.True(ColdPowertrainProfile.DieselStartMultiplier(-30) >
                ColdPowertrainProfile.DieselStartMultiplier(-10));
            Assert.InRange(ColdPowertrainProfile.DieselStartMultiplier(-30), 3, 4);
        }

        [Fact]
        public void ColdBatteryHasMoreSagAndLessUsableEnergy()
        {
            Assert.Equal(1, ColdPowertrainProfile.BatteryResistanceMultiplier(25));
            Assert.Equal(1, ColdPowertrainProfile.BatteryConsumptionMultiplier(25));
            Assert.True(ColdPowertrainProfile.BatteryResistanceMultiplier(-30) > 2);
            Assert.InRange(ColdPowertrainProfile.BatteryConsumptionMultiplier(-30), 1.5f, 1.8f);
        }

        [Fact]
        public void WeatherChangeDoesNotInstantlyWarmPackAndLoadWarmsItGradually()
        {
            var idle = ColdPowertrainProfile.StepBatteryTemperature(-30, -30, 0, 600);
            var loaded = ColdPowertrainProfile.StepBatteryTemperature(-30, -30, 150000, 600);
            Assert.Equal(-30, idle);
            Assert.InRange(loaded, -30, -18);
            Assert.True(loaded > idle);
            Assert.InRange(ColdPowertrainProfile.StepBatteryTemperature(-30, 30, 0, .1f), -30, -29.9f);
        }

        [Fact]
        public void ThermalStepIsIndependentOfTickSize()
        {
            float many = -30;
            for(int i=0;i<600;i++) many = ColdPowertrainProfile.StepBatteryTemperature(many, -20, 75000, 1);
            Assert.Equal(ColdPowertrainProfile.StepBatteryTemperature(-30, -20, 75000, 600), many, 3);
        }

        [Fact]
        public void InvalidInputsDoNotPoisonSimulation()
        {
            Assert.Equal(1, ColdPowertrainProfile.DieselStartMultiplier(float.NaN));
            Assert.Equal(1, ColdPowertrainProfile.BatteryConsumptionMultiplier(float.PositiveInfinity));
            Assert.Equal(-20, ColdPowertrainProfile.StepBatteryTemperature(-20, -30, 1, float.NaN));
            Assert.False(float.IsNaN(ColdPowertrainProfile.StepBatteryTemperature(float.NaN, -30, float.NaN, 1)));
        }
    }
}
