using DVSeasons.Core;
using Xunit;

namespace DVSeasons.Tests
{
    public class SnowSurfaceProfileTests
    {
        [Theory]
        [InlineData("AsphaltTiling_01d_White", SnowSurfaceKind.Road)]
        [InlineData("Roads_LOD_01d [DVSeasons RoadSurface]", SnowSurfaceKind.Road)]
        [InlineData("MB_concrete_rough_01d", SnowSurfaceKind.Pavement)]
        [InlineData("MB_cobblestone_pavement_01d", SnowSurfaceKind.Pavement)]
        [InlineData("MB_rooftile_red_01d", SnowSurfaceKind.Roof)]
        [InlineData("MB_rooftile_brown_01d", SnowSurfaceKind.Roof)]
        [InlineData("MB_roofsheets_01d_blue", SnowSurfaceKind.Roof)]
        [InlineData("MB_roofsheets_01d_gray", SnowSurfaceKind.Roof)]
        [InlineData("MB_roofsheets_rusty_01d", SnowSurfaceKind.Roof)]
        [InlineData("MB_rooftop_cinder_01d", SnowSurfaceKind.Roof)]
        [InlineData("MB_rooftile_red_01n", SnowSurfaceKind.None)]
        [InlineData("WaterLake", SnowSurfaceKind.None)]
        [InlineData("LocomotiveMetal", SnowSurfaceKind.None)]
        [InlineData("Wood", SnowSurfaceKind.None)]
        [InlineData(null, SnowSurfaceKind.None)]
        public void ExactAlbedoAllowlist(string name, SnowSurfaceKind expected)
        { Assert.Equal(expected, SnowSurfaceProfile.Classify(name)); }

        [Theory]
        [InlineData(1, 1, true)]
        [InlineData(0.9, 1, true)]
        [InlineData(0.5, 1, false)]
        [InlineData(0, 1, false)]
        [InlineData(0, 0, false)]
        public void SharedWallMaterialsAreNotGloballyReplaced(double top, double total, bool expected)
        { Assert.Equal(expected, SnowSurfaceProfile.IsMostlyUpward(top,total)); }
    }
}
