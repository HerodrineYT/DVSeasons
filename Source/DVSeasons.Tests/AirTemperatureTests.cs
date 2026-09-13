using System;
using DVSeasons.Core;
using Xunit;

public sealed class AirTemperatureTests
{
    private static SeasonState Season(SeasonKind season, float t)
    { return new SeasonState((int)season, season, (SeasonKind)(((int)season+1)%4), 0, season==SeasonKind.Winter?1:0, t, 0); }
    [Fact] public void ClearAfternoonWarmerThanNightAndCloudsReduceSwing()
    {
        var s=Season(SeasonKind.Autumn,8);
        float clear=AirTemperatureProfile.Evaluate(s,0,15,0,0,0,0)-AirTemperatureProfile.Evaluate(s,0,3,0,0,0,0);
        float cloud=AirTemperatureProfile.Evaluate(s,0,15,1,0,0,0)-AirTemperatureProfile.Evaluate(s,0,3,1,0,0,0);
        Assert.True(clear>cloud && cloud>0);
    }
    [Fact] public void WetWindyFrontCoolsAirWithoutWindChillExtremes()
    {
        var s=Season(SeasonKind.Autumn,8);
        float calm=AirTemperatureProfile.Evaluate(s,0,12,.8f,1,0,0);
        float windy=AirTemperatureProfile.Evaluate(s,0,12,.8f,1,1,0);
        Assert.InRange(calm-windy, .5f, 3);
    }
    [Fact] public void SeasonalExtremesRemainBoundedAndWeatherCanReachThem()
    {
        float min=100,max=-100;
        for(int day=0;day<40;day++) for(int h=0;h<24;h++)
        {
            float cold=AirTemperatureProfile.Evaluate(Season(SeasonKind.Winter,-30),day,h,.4f,1,1,1);
            float hot=AirTemperatureProfile.Evaluate(Season(SeasonKind.Summer,30),day,h,0,0,0,0);
            Assert.InRange(cold,-30,30);Assert.InRange(hot,-30,30);
            min=Math.Min(min,cold);max=Math.Max(max,hot);
        }
        Assert.Equal(-30,min);Assert.Equal(30,max);
    }
    [Fact] public void NegativeGlassNeverThawsEvenWithHeaterRunning()
    {
        var c=new WindowWinterClimate();
        for(int i=0;i<1200;i++)
        {
            c.Advance(.1f,-15,true,80,1,false,1);
            if(c.GlassTemperature<=0) Assert.Equal(WinterGlassStage.Frozen,c.Stage);
        }
    }
    [Fact] public void BrakeWarmsInWinterAndAfterSeasonSwitchAndRecoversInvalidState()
    {
        float t=-30;
        for(int i=0;i<30;i++) t=SeasonalThermalProfile.StepBrakeTemperature(t,15,.5f,1,-30);
        Assert.True(t>50);
        float before=t;
        for(int i=0;i<30;i++) t=SeasonalThermalProfile.StepBrakeTemperature(t,15,.5f,1,30);
        Assert.True(t>before);
        Assert.False(float.IsNaN(SeasonalThermalProfile.StepBrakeTemperature(float.NaN,15,.5f,1,30)));
    }
    [Fact] public void SeasonalSnowReductionCannotMeltSubzeroGlass()
    {
        var c=new WindowWinterClimate();c.Advance(1,-20,false,-20,0,false,1);
        for(int i=0;i<300;i++)c.Advance(1,-15,false,-15,0,false,.3f);
        Assert.Equal(1,c.Frost);Assert.Equal(WinterGlassStage.Frozen,c.Stage);
    }
}
