using System;
using System.Collections.Generic;
using DVSeasons.Core;
using DVSeasons.Mod;
using Xunit;

public sealed class WindowWinterClimateTests
{
    [Fact]
    public void OpenDoorSlowsWarmupWithHeaterStillOn()
    {
        var closed = new WindowWinterClimate(); var open = new WindowWinterClimate();
        for (int i = 0; i < 1200; i++)
        {
            closed.Advance(.1f, -20, true, 80, 1, false, 1);
            open.Advance(.1f, -20, true, 80, 1, true, 1);
            Assert.True(open.CabinTemperature <= closed.CabinTemperature + .00001f);
        }
        Assert.True(closed.CabinTemperature > open.CabinTemperature + 15);
    }

    [Fact]
    public void OpeningWarmCabCoolsItDespiteRunningHeaterAndClosingReheats()
    {
        var cab = new WindowWinterClimate();
        for (int i = 0; i < 600; i++) cab.Advance(.5f, -20, true, 80, 1, false, 1);
        float warm = cab.CabinTemperature;
        cab.Advance(.1f, -20, true, 80, 1, true, 1);
        Assert.True(cab.CabinTemperature < warm);
        for (int i = 0; i < 60; i++) cab.Advance(.5f, -20, true, 80, 1, true, 1);
        float cooled = cab.CabinTemperature;
        Assert.True(cooled < warm - 15);
        for (int i = 0; i < 120; i++) cab.Advance(.5f, -20, true, 80, 1, false, 1);
        Assert.True(cab.CabinTemperature > cooled + 15);
    }

    [Fact]
    public void HeatingCrossesClearingThresholdsWithoutOpacityJumps()
    {
        var climate = new WindowWinterClimate();
        climate.Advance(0, -20, false, -20, 0, false, 1);
        float frost = climate.Frost*climate.IceVisibility, fog = climate.Fog;
        bool crossedIce = false, crossedFog = false;
        for (int i=0; i<9000; i++)
        {
            float glass = climate.GlassTemperature;
            climate.Advance(.1f, -20, true, 80, 1, false, 1);
            Assert.InRange(Math.Abs(climate.Frost*climate.IceVisibility-frost), 0, .015f);
            Assert.InRange(Math.Abs(climate.Fog-fog), 0, .015f);
            if (glass<5 && climate.GlassTemperature>=5)
            { crossedIce=true; Assert.Equal(0,climate.Frost); }
            if (glass<12 && climate.GlassTemperature>=12)
            { crossedFog=true; Assert.Equal(0,climate.Fog); }
            frost=climate.Frost*climate.IceVisibility; fog=climate.Fog;
        }
        Assert.True(crossedIce && crossedFog);
    }

    [Fact]
    public void PartialThawPreservesCrystalCoverageWhileOpacityFades()
    {
        var climate = new WindowWinterClimate();
        climate.Restore(new WindowClimateState { Initialized=true, Glass=3, Cabin=10, Heater=20, Frost=.65f });
        climate.Advance(0,-15,false,-15,0,false,1);
        Assert.Equal(.65f,climate.Frost);
        Assert.InRange(climate.IceVisibility,.73f,.75f);
        climate.Restore(new WindowClimateState { Initialized=true, Glass=2, Cabin=10, Heater=20, Frost=.65f });
        climate.Advance(0,-15,false,-15,0,false,1);
        Assert.Equal(1,climate.IceVisibility);
        Assert.Equal(.65f,climate.Frost);
    }

    [Fact]
    public void FrostDependsOnColdAndWarmGlassClearsExactly()
    {
        var mild=new WindowWinterClimate(); var freezing=new WindowWinterClimate();
        mild.Advance(.1f,-1,false,-1,0,false,1);
        freezing.Advance(.1f,-15,false,-15,0,false,1);
        Assert.InRange(mild.Frost,.01f,.15f);
        Assert.Equal(1,freezing.Frost);
        for(int i=0;i<1800;i++) freezing.Advance(.5f,-15,true,80,1,false,1);
        Assert.True(freezing.GlassTemperature>=WindowWinterClimate.ClearGlassTemperature);
        Assert.Equal(0,freezing.Frost);
        Assert.Equal(0,freezing.Fog);
    }

    [Fact]
    public void PositiveSpringAirClearsRestoredCrystalCoverage()
    {
        var climate = new WindowWinterClimate();
        climate.Restore(new WindowClimateState { Initialized=true, Glass=-12, Cabin=-8,
            Heater=-5, Frost=1, Fog=0 });
        climate.Advance(.1f, 11, false, 11, 0, false, 1);
        Assert.Equal(0, climate.Frost);
    }

    [Fact]
    public void CoolingRefreezesGraduallyAndSaveResumesSameThermalState()
    {
        var original=new WindowWinterClimate();
        for(int i=0;i<1800;i++) original.Advance(.5f,-20,true,80,1,false,1);
        bool gradual=false;
        for(int i=0;i<600;i++)
        {
            original.Advance(.5f,-20,false,-20,0,true,1);
            if(original.Frost>.05f && original.Frost<.8f) gradual=true;
        }
        Assert.True(gradual);
        var restored=new WindowWinterClimate(); restored.Restore(original.Capture());
        for(int i=0;i<120;i++)
        {
            original.Advance(.5f,-20,true,80,1,false,1);
            restored.Advance(.5f,-20,true,80,1,false,1);
        }
        Assert.Equal(original.GlassTemperature,restored.GlassTemperature);
        Assert.Equal(original.Frost,restored.Frost);
        Assert.Equal(original.Stage,restored.Stage);
        restored.Restore(new WindowClimateState {Glass=float.NaN});
        Assert.Equal(original.GlassTemperature,restored.GlassTemperature);
    }
    [Fact]
    public void ColdEngineAndCabStayFrozenWithoutHeater()
    {
        var climate = new WindowWinterClimate();
        for (int i = 0; i < 1200; i++) climate.Advance(.5f, -20, false, -20, 0, false, 1);
        Assert.Equal(WinterGlassStage.Frozen, climate.Stage);
        Assert.Equal(-20, climate.GlassTemperature);
        Assert.Equal(1, climate.Frost);
    }

    [Fact]
    public void HeaterWarmsBeforeColdEngineAndCabThenGlassThawsAndFogs()
    {
        var climate = new WindowWinterClimate();
        var stages = new HashSet<WinterGlassStage>();
        for (int i = 0; i < 1800; i++)
        {
            climate.Advance(.5f, -20, false, -20, 1, false, 1);
            stages.Add(climate.Stage);
            if (i == 39)
            {
                Assert.True(climate.HeaterTemperature > climate.CabinTemperature + 10);
                Assert.True(climate.CabinTemperature < 0);
                Assert.Equal(WinterGlassStage.Frozen, climate.Stage);
            }
        }
        Assert.Contains(WinterGlassStage.Thawing, stages);
        Assert.Contains(WinterGlassStage.Fogged, stages);
        Assert.Contains(WinterGlassStage.Wet, stages);
        Assert.Equal(0, climate.EngineWarmth);
    }

    [Fact]
    public void HeatingOneCarNeverWarmsAnotherAndOpeningCabRefreezesGlass()
    {
        var hot = new WindowWinterClimate(); var cold = new WindowWinterClimate();
        for (int i = 0; i < 1800; i++)
        {
            hot.Advance(.5f, -30, true, 85, 1, false, 1);
            cold.Advance(.5f, -30, false, -30, 0, false, 1);
        }
        Assert.Equal(WinterGlassStage.Wet, hot.Stage);
        Assert.Equal(WinterGlassStage.Frozen, cold.Stage);
        for (int i = 0; i < 1200; i++) hot.Advance(.5f, -30, false, -30, 0, true, 1);
        Assert.Equal(WinterGlassStage.Frozen, hot.Stage);
        Assert.True(hot.Frost > .95f);
    }

    [Fact]
    public void ClimateIsStableAcrossUpdateRatesAndInvalidInputs()
    {
        var fast = new WindowWinterClimate(); var slow = new WindowWinterClimate();
        for (int i = 0; i < 3000; i++) fast.Advance(.1f, -15, true, 80, 1, false, 1);
        for (int i = 0; i < 600; i++) slow.Advance(.5f, -15, true, 80, 1, false, 1);
        Assert.InRange(Math.Abs(fast.GlassTemperature - slow.GlassTemperature), 0, .2f);
        fast.Advance(float.NaN, float.NaN, false, float.NaN, float.NaN, false, float.NaN);
        Assert.False(float.IsNaN(fast.GlassTemperature));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2200, 2200)]
    [InlineData(20000, 20000)]
    [InlineData(75, 100)]
    public void LeafLimitPreservesUnlimitedAndFiniteValues(int input, int expected)
    {
        var settings = new SeasonModSettings { AutumnLeafLimit = input };
        settings.Clamp();
        Assert.Equal(expected, settings.AutumnLeafLimit);
        Assert.True(settings.WinterWindowsEnabled);
    }
}
