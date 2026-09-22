using System;
using UnityEngine;

namespace DVSeasons.Mod
{
    // An acceptance shortcut only. A stale envelope may miss an animated part,
    // so every part keeps its live bounds read and falls back to native culling
    // unless containment is proved. Nothing is rejected by this helper.
    internal static class SnowVehicleFrustumAcceptance
    {
        private const double RoundoffGuard = 16d / 16777216d;

        internal static bool FullyInside(Plane[] primary, Plane[] secondary, Bounds envelope)
        {
            var center = envelope.center;
            var extents = envelope.extents;
            if (!Finite(center.x) || !Finite(center.y) || !Finite(center.z) ||
                !Extent(extents.x) || !Extent(extents.y) || !Extent(extents.z)) return false;
            // The union of two eye frusta is not itself a convex frustum. Prove
            // inclusion in one actual eye; do not combine planes across eyes.
            return InsideEye(primary, center, extents) ||
                (secondary != null && InsideEye(secondary, center, extents));
        }

        private static bool InsideEye(Plane[] planes, Vector3 center, Vector3 extents)
        {
            if (planes == null || planes.Length != 6) return false;
            for (int i = 0; i < 6; i++)
            {
                var normal = planes[i].normal;
                float distance = planes[i].distance;
                if (!Finite(normal.x) || !Finite(normal.y) || !Finite(normal.z) || !Finite(distance)) return false;
                double nx = normal.x, ny = normal.y, nz = normal.z;
                double radius = Math.Abs(nx) * extents.x + Math.Abs(ny) * extents.y + Math.Abs(nz) * extents.z;
                double signed = nx * center.x + ny * center.y + nz * center.z + distance;
                double magnitude = Math.Abs(nx) * Math.Abs((double)center.x) +
                    Math.Abs(ny) * Math.Abs((double)center.y) + Math.Abs(nz) * Math.Abs((double)center.z) +
                    Math.Abs((double)distance) + radius + 1d;
                // Reject the uncertain boundary band rather than enlarging the
                // frustum. This also covers float arithmetic in the native AABB
                // test; double evaluation alone would not give that guarantee.
                if (magnitude >= float.MaxValue / 64d || signed - radius <= magnitude * RoundoffGuard) return false;
            }
            return true;
        }

        internal static bool Contains(Bounds envelope, Bounds part)
        {
            var outer = envelope.center;
            var outerExtents = envelope.extents;
            var inner = part.center;
            var innerExtents = part.extents;
            if (!Extent(outerExtents.x) || !Extent(outerExtents.y) || !Extent(outerExtents.z) ||
                !Extent(innerExtents.x) || !Extent(innerExtents.y) || !Extent(innerExtents.z)) return false;
            // Compute endpoints before rounding to float: Bounds.min/max can
            // round outward and accidentally admit an escaped live renderer.
            // Ordered comparisons also reject any NaN coordinate.
            return (double)inner.x - innerExtents.x >= (double)outer.x - outerExtents.x &&
                (double)inner.x + innerExtents.x <= (double)outer.x + outerExtents.x &&
                (double)inner.y - innerExtents.y >= (double)outer.y - outerExtents.y &&
                (double)inner.y + innerExtents.y <= (double)outer.y + outerExtents.y &&
                (double)inner.z - innerExtents.z >= (double)outer.z - outerExtents.z &&
                (double)inner.z + innerExtents.z <= (double)outer.z + outerExtents.z;
        }

        private static bool Extent(float value) { return value >= 0f && value <= float.MaxValue; }
        private static bool Finite(float value) { return value >= -float.MaxValue && value <= float.MaxValue; }
    }
}
