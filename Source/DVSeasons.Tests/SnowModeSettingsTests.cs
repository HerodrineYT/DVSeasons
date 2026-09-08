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
