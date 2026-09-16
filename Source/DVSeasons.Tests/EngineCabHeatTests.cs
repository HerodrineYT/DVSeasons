using DVSeasons.Core;
using Xunit;

public sealed class EngineCabHeatTests
{
    [Fact]
    public void ColdStoppedEngineSuppliesNoHeat()
    {
        var heat = new EngineCabHeat();
        for (int i = 0; i < 100; i++) Assert.Equal(0, heat.Advance(5, false, -20));
    }

    [Fact]
    public void StartingColdEngineWarmsCabGradually()
    {
        var heat = new EngineCabHeat();
        Assert.InRange(heat.Advance(.1f, true, -20), 0, .006f);
        var cabin = new WindowWinterClimate();
        for (int i = 0; i < 600; i++)
            cabin.Advance(.5f, -20, true, -20, heat.Advance(.5f, true, -20), false, 1);
        Assert.InRange(cabin.CabinTemperature, 20, 40);
    }

    [Fact]
    public void HotEngineCanSupplyHeatImmediatelyAfterReload()
    {
        Assert.Equal(1, new EngineCabHeat().Advance(0, false, 80));
        Assert.Equal(.5f, new EngineCabHeat().Advance(0, false, 50));
    }

    [Fact]
    public void ShutdownRetainsHeatThenCoolsAndRestartRecovers()
    {
        var heat = new EngineCabHeat();
        float value = 0;
        for (int i = 0; i < 120; i++) value = heat.Advance(5, true, float.NaN);
        float before = value;
        value = heat.Advance(.5f, false, float.NaN);
        Assert.InRange(value, before - .005f, before);
        for (int i = 0; i < 24; i++) value = heat.Advance(5, false, float.NaN);
        Assert.InRange(value, .5f, .65f);
        float restarting = heat.Advance(5, true, float.NaN);
        Assert.True(restarting > value);
        for (int i = 0; i < 400; i++) value = heat.Advance(5, false, float.NaN);
        Assert.InRange(value, 0, .001f);
    }

    [Fact]
    public void DisablingFallbackStopsPoweredCabHeating()
    {
        var cabin = new WindowWinterClimate();
        for (int i = 0; i < 600; i++) cabin.Advance(1, -20, true, 80, 1, false, 1);
        float heated = cabin.CabinTemperature;
        for (int i = 0; i < 600; i++) cabin.Advance(1, -20, true, 80, 0, false, 1);
        Assert.True(cabin.CabinTemperature < heated - 30);
        Assert.InRange(cabin.CabinTemperature, -20, -17);
    }

    [Fact]
    public void Class750UnconnectedTemperatureWarmsFasterAboveItsActualIdle()
    {
        var idle = new EngineCabHeat(); var full = new EngineCabHeat();
        float idleLevel = 0, fullLevel = 0;
        const float idleRpm = 480f / 1100f;
        for (int i = 0; i < 120; i++)
        {
            idleLevel = idle.AdvanceAtRpm(.5f, true, float.NaN, idleRpm, idleRpm);
            fullLevel = full.AdvanceAtRpm(.5f, true, float.NaN, 1, idleRpm);
        }
        Assert.InRange(idleLevel, .43f, .45f);
        Assert.InRange(fullLevel, .95f, .97f);
        Assert.True(fullLevel > idleLevel * 2);
    }

    [Fact]
    public void RpmChangesHeaterDeliveryEvenWhenCoolantIsAlreadyHot()
    {
        var heat = new EngineCabHeat();
        float value = heat.AdvanceAtRpm(0, true, 80, .4f, .4f);
        Assert.Equal(.8f, value);
        float first = heat.AdvanceAtRpm(.1f, true, 80, 1, .4f);
        Assert.InRange(first - value, 0, .003f);
        for (int i = 0; i < 60; i++) value = heat.AdvanceAtRpm(1, true, 80, 1, .4f);
        Assert.InRange(value, .995f, 1);
        float beforeShutdown = value;
        value = heat.AdvanceAtRpm(.1f, false, 80, 0, .4f);
        Assert.InRange(value, beforeShutdown - .001f, beforeShutdown);
    }

    [Fact]
    public void DirectEngineHeatingHasUsefulIdleWarmthAndClearlyStrongerHighRpm()
    {
        var idleHeat = new EngineCabHeat(); var fastHeat = new EngineCabHeat();
        var idleCab = new WindowWinterClimate(); var fastCab = new WindowWinterClimate();
        const float rpm = 480f / 1100f;
        for (int i = 0; i < 240; i++)
        {
            float idle = idleHeat.AdvanceAtRpm(.5f, true, float.NaN, rpm, rpm);
            float fast = fastHeat.AdvanceAtRpm(.5f, true, float.NaN, 1, rpm);
            idleCab.AdvanceEngineHeated(.5f, -20, true, -20, idle, false, 1);
            fastCab.AdvanceEngineHeated(.5f, -20, true, -20, fast, false, 1);
        }
        Assert.InRange(idleCab.CabinTemperature, 8, 16);
        Assert.InRange(fastCab.CabinTemperature, 23, 30);
        Assert.True(fastCab.CabinTemperature > idleCab.CabinTemperature + 10);
    }

    [Fact]
    public void MissingRpmUsesIdleWhileStoppedEngineStillCannotProduceHeat()
    {
        var idle = new EngineCabHeat(); var missing = new EngineCabHeat();
        for (int i = 0; i < 120; i++)
            Assert.Equal(idle.AdvanceAtRpm(.5f, true, float.NaN, .25f, .25f),
                missing.AdvanceAtRpm(.5f, true, float.NaN, float.NaN, float.NaN));
        Assert.Equal(0, new EngineCabHeat().AdvanceAtRpm(5, false, float.NaN, 1, .25f));
    }

    [Fact]
    public void ColdCabWarmsWithinAMinuteAndGlassClearsSmoothlyAtHighRpm()
    {
        var heat = new EngineCabHeat(); var cab = new WindowWinterClimate();
        float previousGlass = -20;
        for (int i = 0; i < 180; i++)
        {
            float power = heat.AdvanceAtRpm(.5f, true, float.NaN, 1, 480f / 1100f);
            cab.AdvanceEngineHeated(.5f, -20, true, -20, power, false, 1);
            Assert.InRange(cab.GlassTemperature - previousGlass, 0, .4f);
            previousGlass = cab.GlassTemperature;
            if (i == 0) Assert.InRange(cab.CabinTemperature, -20, -19.9f);
            if (i == 119) Assert.InRange(cab.CabinTemperature, 10, 18);
        }
        Assert.InRange(cab.CabinTemperature, 20, 28);
        Assert.True(cab.GlassTemperature > WindowWinterClimate.ClearGlassTemperature);
        Assert.Equal(0, cab.IceVisibility);
    }

    [Fact]
    public void FasterEngineHeatingStillLosesHeatThroughOpenDoorAndRespectsSettingOff()
    {
        var closed = new WindowWinterClimate(); var open = new WindowWinterClimate();
        for (int i = 0; i < 180; i++)
        {
            closed.AdvanceEngineHeated(.5f, -20, true, 80, 1, false, 1);
            open.AdvanceEngineHeated(.5f, -20, true, 80, 1, true, 1);
        }
        Assert.True(closed.CabinTemperature > open.CabinTemperature + 30);
        float before = closed.CabinTemperature;
        closed.Advance(.1f, -20, true, 80, 0, false, 1);
        Assert.InRange(closed.CabinTemperature, before - .2f, before);
        for (int i = 0; i < 120; i++) closed.Advance(5, -20, true, 80, 0, false, 1);
        Assert.InRange(closed.CabinTemperature, -20, -17);
    }
}
