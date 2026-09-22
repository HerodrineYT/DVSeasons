using UnityEngine;

namespace DVSeasons.Mod
{
    internal static class SnowCameraFrustum
    {
        // A centre-eye projection can reject a surface which is visible to one
        // headset eye. Keep both actual frusta rather than approximating their
        // union with the desktop mirror's FOV. Callers own the reusable arrays.
        internal static Plane[] Prepare(Camera camera, Plane[] primary, Plane[] secondary)
        {
            if (!camera.stereoEnabled)
            {
                GeometryUtility.CalculateFrustumPlanes(camera, primary);
                return null;
            }
            GeometryUtility.CalculateFrustumPlanes(
                camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left) *
                camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left), primary);
            GeometryUtility.CalculateFrustumPlanes(
                camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) *
                camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right), secondary);
            return secondary;
        }

        internal static bool Intersects(Plane[] primary, Plane[] secondary, Bounds bounds)
        {
            return GeometryUtility.TestPlanesAABB(primary, bounds) ||
                (secondary != null && GeometryUtility.TestPlanesAABB(secondary, bounds));
        }

        internal static float MaximumRayLength(Camera camera)
        {
            return Mathf.Max(MaximumRayLength(camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left)),
                MaximumRayLength(camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right)));
        }

        internal static float MaximumRayLength(Matrix4x4 projection)
        {
            // Depth tolerance is measured along view Z. Convert it to a
            // conservative ray distance even for wide, off-axis headset views.
            var inverse = projection.inverse;
            float maximum = 1f;
            for (int corner = 0; corner < 4; corner++)
            {
                var point = inverse.MultiplyPoint(new Vector3((corner & 1) == 0 ? -1f : 1f,
                    (corner & 2) == 0 ? -1f : 1f, 0));
                maximum = Mathf.Max(maximum, point.magnitude / Mathf.Max(.000001f, Mathf.Abs(point.z)));
            }
            return maximum;
        }
    }
}
