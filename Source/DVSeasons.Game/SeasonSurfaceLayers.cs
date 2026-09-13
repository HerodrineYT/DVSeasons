using DV;

namespace DVSeasons.Mod
{
    internal static class SeasonSurfaceLayers
    {
        // The large train collision hull is deliberately absent: it does not
        // follow roofs, steps or cab openings. Keep scenery and detailed floors.
        public const int Mask = (int)(Layers.DVLayerMask.Default |
            Layers.DVLayerMask.Terrain | Layers.DVLayerMask.Train_Walkable |
            Layers.DVLayerMask.Train_Interior);
    }
}
