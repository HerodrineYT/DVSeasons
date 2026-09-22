using System.IO;
using System.Xml.Serialization;
using DVSeasons.Mod;
using UnityModManagerNet;
using Xunit;

public sealed class SnowModeSettingsTests
{
    [Fact]
    public void SettingsWithoutSnowModeEnableNewSystem()
    {
        var settings = Read("<SeasonModSettings />");
        settings.Clamp();
        Assert.True(settings.ProceduralSnowEnabled);
        Assert.Equal(0, settings.SnowObjectLimit);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(50, 50)]
    [InlineData(10000, 10000)]
    [InlineData(-9, 0)]
    [InlineData(20000, 10000)]
    public void ObjectSnowLimitSurvivesSaveAndIsBounded(int input, int expected)
    {
        var settings = Read("<SeasonModSettings><SnowObjectLimit>" + input +
            "</SnowObjectLimit></SeasonModSettings>");
        var entry = new UnityModManager.ModEntry();
        settings.Save(entry);
        var restored = Read(entry.SavedSettingsXml);
        restored.Clamp();
        Assert.Equal(expected, restored.SnowObjectLimit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingSnowModeSurvivesLoadClampAndSave(bool enabled)
    {
        var settings = Read("<SeasonModSettings><ProceduralSnowEnabled>" +
            (enabled ? "true" : "false") + "</ProceduralSnowEnabled></SeasonModSettings>");
        var entry = new UnityModManager.ModEntry();
        settings.Save(entry);
        var restored = Read(entry.SavedSettingsXml);
        restored.Clamp();
        Assert.Equal(enabled, restored.ProceduralSnowEnabled);
        Assert.True(restored.SeasonalTexturesEnabled);
        Assert.True(restored.TerrainTextureChanges);
    }

    private static SeasonModSettings Read(string xml)
    {
        using (var reader = new StringReader(xml))
            return (SeasonModSettings)new XmlSerializer(typeof(SeasonModSettings)).Deserialize(reader);
    }
}
