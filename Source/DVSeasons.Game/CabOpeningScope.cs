using System;

namespace DVSeasons.Mod
{
    // Two distant groups of cab doors must not ventilate each other's cabin.
    // Only use this distinction with a known occupied car and a nearby group;
    // ambiguous layouts retain the conservative whole-car behavior.
    internal static class CabOpeningScope
    {
        internal static float FindBoundary(float[] positions)
        {
            if (positions == null || positions.Length < 2) return float.NaN;
            var sorted = (float[])positions.Clone(); Array.Sort(sorted);
            float gap = 6, boundary = float.NaN;
            for (int i = 1; i < sorted.Length; i++)
                if (sorted[i] - sorted[i - 1] >= gap)
                { gap = sorted[i] - sorted[i - 1]; boundary = (sorted[i] + sorted[i - 1]) * .5f; }
            return boundary;
        }

        internal static int SelectSide(float boundary, float[] positions, bool occupied, float playerZ)
        {
            if (!occupied || float.IsNaN(boundary) || float.IsNaN(playerZ) || float.IsInfinity(playerZ)) return 0;
            float nearest = float.PositiveInfinity;
            foreach (float position in positions) nearest = Math.Min(nearest, Math.Abs(position - playerZ));
            if (nearest > 3) return 0;
            return playerZ >= boundary ? 1 : -1;
        }

        internal static bool Includes(float position, float boundary, int side)
        { return side == 0 || (position >= boundary ? 1 : -1) == side; }
    }
}
