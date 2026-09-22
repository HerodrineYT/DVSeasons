using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    // Beyond the user's detailed-snow budget, only an exclusion marker is
    // required. Reconstruct the visible depth inside one box instead of drawing
    // every bolt, axle, hidden interior and LOD of that car a second time.
    internal sealed class SnowVehicleExclusionVolumes : IDisposable
    {
        private readonly Matrix4x4[] matrices = new Matrix4x4[1023];
        private Mesh box;
        public int Count { get; private set; }
        public void Begin() { Count = 0; }

        public bool TryAdd(SnowVehicleRegistry.Vehicle vehicle, Camera camera)
        {
            if (Count == matrices.Length || !vehicle.RollingStock || !vehicle.Topology.Initialized ||
                vehicle.PartsPending || vehicle.InteriorPending || vehicle.PartsExploded ||
                !vehicle.Root.gameObject.activeInHierarchy) return false;
            var bounds = vehicle.Topology.Local;
            var root = vehicle.Root.localToWorldMatrix;
            if (root.determinant <= 0f || bounds.size.sqrMagnitude < .01f) return false;
            // Keep exact cutouts, cab interiors and neighbouring ground close to
            // the player. This approximation affects only distant capped cars.
            var position = camera.transform.position;
            var nearest = root.MultiplyPoint3x4(bounds.ClosestPoint(vehicle.Root.InverseTransformPoint(position)));
            if ((nearest-position).sqrMagnitude < 20f*20f) return false;
            var scale = vehicle.Root.lossyScale;
            var radius = bounds.extents.magnitude * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            if (Vector3.Distance(root.MultiplyPoint3x4(bounds.center), position)+radius >= camera.farClipPlane) return false;
            // No motion hysteresis here: use the live pose, and keep the lower
            // face tight so rails/ground below the wheels retain their snow.
            var size = bounds.size + new Vector3(.12f, .02f, .12f);
            matrices[Count++] = root * Matrix4x4.TRS(bounds.center, Quaternion.identity, size);
            return true;
        }

        public void Record(CommandBuffer buffer, Material material, Camera camera)
        {
            if (Count == 0) return;
            if (box == null)
            {
                box = new Mesh { name = "DVSeasons capped-car exclusion volume", hideFlags = HideFlags.HideAndDontSave };
                var vertices = new Vector3[8];
                for (int i=0;i<8;i++) vertices[i]=new Vector3((i&1)==0?-.5f:.5f,(i&2)==0?-.5f:.5f,(i&4)==0?-.5f:.5f);
                box.vertices = vertices;
                box.triangles = new[]{0,2,1,1,2,3,4,5,6,5,7,6,0,4,2,2,4,6,1,3,5,3,7,5,0,1,4,1,5,4,2,6,3,3,6,7};
                box.UploadMeshData(true);
            }
            buffer.SetGlobalMatrix("_DVPSExclusionInverseVP", StereoRenderSupport.InverseViewProjection(camera));
            buffer.DrawMeshInstanced(box, 0, material, 7, matrices, Count);
        }

        public void Dispose()
        {
            if (box != null) { if (Application.isPlaying) UnityEngine.Object.Destroy(box); else UnityEngine.Object.DestroyImmediate(box); }
            box = null; Count = 0;
        }
    }
}
