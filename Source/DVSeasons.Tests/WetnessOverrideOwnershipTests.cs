using DVSeasons.Core;
using Xunit;

public sealed class WetnessOverrideOwnershipTests
{
    [Fact] public void DefaultWinterAdhesionHasPriorityOverManualDryWeather()
    {
        var settings=new DVSeasons.Mod.SeasonModSettings();
        Assert.True(settings.WinterAdhesionEnabled);
        Assert.False(settings.RespectExternalWetnessOverride);
    }
    [Fact] public void WinterResumesAfterExternalWeatherOverrideEnds()
    {
        var state=new WetnessOverrideOwnership();
        Assert.False(state.Acquire(true,0,true));
        Assert.True(state.Acquire(false,0,true));state.Applied(0.5f);
        Assert.True(state.Acquire(true,0.5f,true));
    }
    [Fact] public void ClearingOwnedOverrideCanBeRecovered()
    {
        var state=new WetnessOverrideOwnership();
        Assert.True(state.Acquire(false,0,true));state.Applied(0.5f);
        Assert.True(state.Acquire(false,0,true));state.Applied(0.5f);
        bool overridden;float value;
        Assert.True(state.Release(true,0.5f,out overridden,out value));Assert.False(overridden);
    }
    [Fact] public void ChangedExternalValueSurvivesRelease()
    {
        var state=new WetnessOverrideOwnership();state.Acquire(false,0,true);state.Applied(0.5f);
        bool overridden;float value;
        Assert.False(state.Release(true,0.1f,out overridden,out value));
    }
    [Fact] public void ExplicitOverrideRestoresPreviousValue()
    {
        var state=new WetnessOverrideOwnership();Assert.True(state.Acquire(true,0.2f,false));state.Applied(0.5f);
        bool overridden;float value;
        Assert.True(state.Release(true,0.5f,out overridden,out value));Assert.True(overridden);Assert.Equal(0.2f,value);
    }
}
