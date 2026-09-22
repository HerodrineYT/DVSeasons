using DVSeasons.Core;
using Xunit;

public sealed class ElectricCabClimateTests
{
    [Fact]
    public void WarmWeatherDoesNotCreatePhantomEngineHeatInUnpoweredElectricCab()
    {
        var cab = new WindowWinterClimate();
        for (int i = 0; i < 1200; i++) cab.AdvanceElectricHeated(.5f, 35, 0, false, 0);
        Assert.Equal(35, cab.CabinTemperature);
        Assert.Equal(0, cab.EngineWarmth);
    }

    [Fact]
    public void PoweredCabWarmsAndClearsGlassWithoutRunningEngine()
    {
        var cab = new WindowWinterClimate();
        cab.AdvanceElectricHeated(0, -20, 1, false, 1);
        Assert.Equal(-20, cab.CabinTemperature);
        for (int i = 0; i < 240; i++) cab.AdvanceElectricHeated(.5f, -20, 1, false, 1);
        Assert.InRange(cab.CabinTemperature, 18, 21);
        Assert.True(cab.GlassTemperature >= WindowWinterClimate.ClearGlassTemperature);
        Assert.Equal(0, cab.Frost);
        Assert.Equal(0, cab.EngineWarmth);
    }

    [Fact]
    public void PowerLossStopsHeatInputWithoutInstantlyCoolingCab()
    {
        var cab = new WindowWinterClimate();
        for (int i = 0; i < 240; i++) cab.AdvanceElectricHeated(.5f, -20, 1, false, 1);
        float warm = cab.CabinTemperature;
        cab.AdvanceElectricHeated(.1f, -20, 0, false, 1);
        Assert.InRange(cab.CabinTemperature, warm - .1f, warm);
        for (int i = 0; i < 1200; i++) cab.AdvanceElectricHeated(.5f, -20, 0, false, 1);
        Assert.InRange(cab.CabinTemperature, -20, -19.9f);
        Assert.Equal(0, cab.EngineWarmth);
    }

    [Fact]
    public void OpenDoorCoolsElectricCabAndClosingAllowsWarmup()
    {
        var cab = new WindowWinterClimate();
        for (int i = 0; i < 240; i++) cab.AdvanceElectricHeated(.5f, -20, 1, false, 1);
        float warm = cab.CabinTemperature;
        for (int i = 0; i < 120; i++) cab.AdvanceElectricHeated(.5f, -20, 1, true, 1);
        Assert.True(cab.CabinTemperature < warm - 20);
        for (int i = 0; i < 240; i++) cab.AdvanceElectricHeated(.5f, -20, 1, false, 1);
        Assert.True(cab.CabinTemperature > 18);
    }
}
