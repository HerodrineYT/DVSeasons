using System;

namespace DVSeasons.Core
{
    public enum SnowSurfaceKind { None, Road, Pavement, Roof }

    public static class SnowSurfaceProfile
    {
        // Exact DV99 albedo names: never match generic 'wood', 'metal' or 'ground'.
        public static SnowSurfaceKind Classify(string textureName)
        {
            var name = (textureName ?? string.Empty).Split(new[] { " [DVSeasons" },
                StringSplitOptions.None)[0].ToLowerInvariant();
            switch (name)
            {
                case "asphaltroad_01d": case "asphalttiling_01d":
                case "asphalttiling_01d_white": case "roads_lod_01d":
                    return SnowSurfaceKind.Road;
                case "sidewalktiles_01d": case "sidewalk_01d":
                case "mb_cobblestone_pavement_01d": case "mb_concrete_01d":
                case "mb_concrete_rough_01d": case "mb_concrete_01d_blue":
                    return SnowSurfaceKind.Pavement;
                case "mb_rooftile_red_01d": case "mb_rooftile_brown_01d":
                case "mb_roofsheets_rusty_01d": case "mb_roofsheets_01d_gray":
                case "mb_roofsheets_01d_blue": case "mb_rooftop_cinder_01d":
                    return SnowSurfaceKind.Roof;
                default: return SnowSurfaceKind.None;
            }
        }

        public static bool IsMostlyUpward(double upwardArea, double totalArea)
        { return totalArea > 0.000001 && upwardArea / totalArea >= 0.85; }
    }
}
