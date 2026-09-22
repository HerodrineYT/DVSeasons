using System;
using DVSeasons.Core;
using DVSeasons.Mod;
using UnityModManagerNet;
using Xunit;

public sealed class SeasonSettingsCacheTests
{
    [Fact]
    public void StableSettingsReuseImmutableSnapshotWithoutAllocating()
    {
        var settings = new SeasonModSettings();
        var first = settings.ToSnapshot();
        for (int i = 0; i < 1000; i++) settings.ToSnapshot();
        long before = GC.GetAllocatedBytesForCurrentThread();
        SeasonSettingsSnapshot last = null;
        for (int i = 0; i < 10000; i++) last = settings.ToSnapshot();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Same(first, last);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void EachCalendarInputInvalidatesImmediatelyAndPreviousSnapshotStaysImmutable()
    {
        var settings = new SeasonModSettings();
        Action[] changes = {
            () => settings.AutomaticCycle = false,
            () => settings.DaysPerSeason = 20,
            () => settings.TransitionDays = 4,
            () => settings.StartingSeason = 3,
            () => settings.WinterAdhesionEnabled = false,
            () => settings.WinterWetnessEquivalent = .25f
        };
        var original = settings.ToSnapshot();
        var previous = original;
        foreach (var change in changes)
        {
            change();
            var actual = settings.ToSnapshot();
            Assert.NotSame(previous, actual);
            Assert.Same(actual, settings.ToSnapshot());
            Assert.Equal(settings.AutomaticCycle, actual.AutomaticCycle);
            Assert.Equal(settings.DaysPerSeason, actual.DaysPerSeason);
            Assert.Equal(settings.TransitionDays, actual.TransitionDays);
            Assert.Equal((SeasonKind)settings.StartingSeason, actual.StartingSeason);
            Assert.Equal(settings.WinterAdhesionEnabled, actual.WinterAdhesionEnabled);
            Assert.Equal(settings.WinterWetnessEquivalent, actual.WinterWetnessEquivalent);
            previous = actual;
        }
        Assert.True(original.AutomaticCycle);
        Assert.Equal(14f, original.DaysPerSeason);
        Assert.Equal(.45f, original.WinterWetnessEquivalent);
    }

    [Fact]
    public void VisualChangesAndPersistenceDoNotInvalidateOrSerializeRuntimeCache()
    {
        var settings = new SeasonModSettings();
        var snapshot = settings.ToSnapshot();
        var entry = new UnityModManager.ModEntry();
        settings.SnowObjectLimit = 64;
        settings.SnowGlareReduction = .75f;
        settings.Save(entry);
        Assert.Same(snapshot, settings.ToSnapshot());
        Assert.DoesNotContain("cachedSnapshot", entry.SavedSettingsXml);
        Assert.DoesNotContain("snapshotDays", entry.SavedSettingsXml);
    }

    [Fact]
    public void InvalidRawInputsStillUseOriginalNormalizationAndCanBeCorrected()
    {
        var settings = new SeasonModSettings {
            StartingSeason = 999, DaysPerSeason = -1, TransitionDays = 40,
            WinterWetnessEquivalent = float.PositiveInfinity
        };
        var first = settings.ToSnapshot();
        Assert.Equal(SeasonKind.Spring, first.StartingSeason);
        Assert.Equal(1, first.DaysPerSeason);
        Assert.Equal(1, first.TransitionDays);
        Assert.Equal(.5f, first.WinterWetnessEquivalent);
        Assert.Same(first, settings.ToSnapshot());
        settings.DaysPerSeason = float.NaN;
        var nan = settings.ToSnapshot();
        Assert.True(float.IsNaN(nan.DaysPerSeason));
        Assert.Same(nan, settings.ToSnapshot());
        settings.DaysPerSeason = 10;
        Assert.Equal(10, settings.ToSnapshot().DaysPerSeason);
    }
}
