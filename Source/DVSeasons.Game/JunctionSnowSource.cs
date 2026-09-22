using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DVSeasons.Mod
{
    // Junction blades move a few centimetres, independently of the stationary
    // sleepers and rails. Preserve their first captured pose for snow sampling.
    // They must not consume the 32 height/snow slices reserved for rolling stock.
    internal sealed class JunctionSnowSource : IDisposable
    {
        private sealed class Blade
        {
            public MeshRenderer Renderer;
            public Transform Root, Parent;
            public Vector3 Position, Scale, SavedPosition, SavedScale;
            public Quaternion Rotation, SavedRotation;
            public int Submeshes;
        }
        private readonly Dictionary<int, Blade> blades = new Dictionary<int, Blade>();
        private readonly List<int> expired = new List<int>();
        private readonly List<Blade> captured = new List<Blade>();
        private readonly List<Blade> visible = new List<Blade>();
        private readonly List<Material> materials = new List<Material>();
        private static readonly FieldInfo AnimatorField = typeof(VisualSwitch).GetField("animator", BindingFlags.Instance | BindingFlags.Public);
        private readonly IncrementalSceneScan<VisualSwitch> scan;
        private readonly HashSet<int> visitedAnimators = new HashSet<int>();
        private float nextCleanup;
        private bool subscribed, geometryChanged;
        public int Count => blades.Count;

        public JunctionSnowSource()
        { scan = new IncrementalSceneScan<VisualSwitch>(30f, 192, Visit); }

        public bool ConsumeGeometryChanged()
        { bool changed = geometryChanged; geometryChanged = false; return changed; }

        public void Update()
        {
            if (!subscribed)
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.sceneUnloaded += OnSceneUnloaded;
                subscribed = true;
            }
            using (SnowPerformance.Measure("junction-discovery")) scan.Step();
            if (Time.realtimeSinceStartup >= nextCleanup)
            {
                nextCleanup = Time.realtimeSinceStartup + 5f;
                expired.Clear();
                foreach (var pair in blades)
                    if (pair.Value.Renderer == null || pair.Value.Root == null) expired.Add(pair.Key);
                foreach (var id in expired) blades.Remove(id);
                if (expired.Count != 0) geometryChanged = true;
            }
        }

        private void Visit(VisualSwitch owner)
        {
            if (owner == null) return;
            var animator = AnimatorField?.GetValue(owner) as Component;
            if (owner == null || animator == null || !owner.gameObject.scene.IsValid() ||
                !visitedAnimators.Add(animator.GetInstanceID())) return;
            // This bounded scene visitor only descends into a native junction's
            // small animator hierarchy once, rather than globally enumerating all
            // switches and their meshes every five seconds.
            foreach (var renderer in animator.GetComponentsInChildren<MeshRenderer>(true))
            {
                // The native animation controls rails_moving as a rigid mesh.
                // Require that actual mesh under the native animator; a similarly
                // named scenery object or the fixed rail bed is never registered.
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null || !string.Equals(mesh.name, "rails_moving", StringComparison.OrdinalIgnoreCase) ||
                    renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                Register(renderer, mesh.subMeshCount);
            }
        }

        private void Register(MeshRenderer renderer, int submeshes)
        {
            int id = renderer.GetInstanceID();
            if (blades.ContainsKey(id)) return;
            var root = renderer.transform;
            blades.Add(id, new Blade { Renderer = renderer, Root = root, Parent = root.parent,
                Position = root.localPosition, Rotation = root.localRotation, Scale = root.localScale,
                Submeshes = submeshes });
            geometryChanged = true;
        }

        public bool PrepareVisible(Camera camera, Plane[] frustum, Plane[] secondaryFrustum = null)
        {
            visible.Clear();
            foreach (var blade in blades.Values)
            {
                var r = blade.Renderer;
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff ||
                    blade.Root.parent != blade.Parent || (camera.cullingMask & (1 << r.gameObject.layer)) == 0 ||
                    !SnowCameraFrustum.Intersects(frustum, secondaryFrustum, r.bounds)) continue;
                visible.Add(blade);
            }
            return visible.Count != 0;
        }

        public void Record(CommandBuffer buffer, Material material, Func<Renderer,bool> allowed = null)
        {
            if (visible.Count == 0) return;
            foreach (var blade in visible)
            {
                if (blade.Renderer == null) continue;
                buffer.SetGlobalFloat("_DVPSVehicleIndex", allowed == null || allowed(blade.Renderer) ? -3f : -1f);
                // Subtract in the parent's local frame, then transform the small
                // displacement as a vector. Half-float pixels retain centimetres
                // even at 15 km map coordinates and after an origin shift.
                var reference = Matrix4x4.TRS(blade.Position, blade.Rotation, blade.Scale);
                var current = Matrix4x4.TRS(blade.Root.localPosition, blade.Root.localRotation, blade.Root.localScale);
                var delta = new Matrix4x4();
                for (var row = 0; row < 4; row++) for (var col = 0; col < 4; col++)
                    delta[row, col] = reference[row, col] - current[row, col];
                if (blade.Parent != null) delta = blade.Parent.localToWorldMatrix * delta;
                buffer.SetGlobalMatrix("_DVPSJunctionDelta", delta);
                blade.Renderer.GetSharedMaterials(materials);
                for (var slot = 0; slot < Math.Min(blade.Submeshes, materials.Count); slot++)
                {
                    var source = materials[slot];
                    if (source == null || source.renderQueue > 2500) continue;
                    float cutoff = source.GetTag("RenderType", false) == "TransparentCutout" ?
                        (source.HasProperty("_Cutoff") ? source.GetFloat("_Cutoff") : .5f) : 0f;
                    buffer.SetGlobalFloat("_DVPSVehicleCutoff", cutoff);
                    if (cutoff > 0)
                    {
                        var texture = source.HasProperty("_MainTex") ? source.GetTexture("_MainTex") : null;
                        var scale = source.HasProperty("_MainTex") ? source.GetTextureScale("_MainTex") : Vector2.one;
                        var offset = source.HasProperty("_MainTex") ? source.GetTextureOffset("_MainTex") : Vector2.zero;
                        buffer.SetGlobalTexture("_DVPSVehicleAlbedo", texture != null ? texture : Texture2D.whiteTexture);
                        buffer.SetGlobalVector("_DVPSVehicleST", new Vector4(scale.x, scale.y, offset.x, offset.y));
                    }
                    buffer.DrawRenderer(blade.Renderer, material, slot, 0);
                }
            }
        }

        public void PrepareForStaticCapture(Vector4 area)
        {
            captured.Clear();
            foreach (var blade in blades.Values)
            {
                if (blade.Root == null || blade.Root.parent != blade.Parent || !blade.Root.gameObject.activeInHierarchy ||
                    Mathf.Abs(blade.Root.position.x - area.x) > area.z + 40 ||
                    Mathf.Abs(blade.Root.position.z - area.y) > area.z + 40) continue;
                blade.SavedPosition = blade.Root.localPosition;
                blade.SavedRotation = blade.Root.localRotation;
                blade.SavedScale = blade.Root.localScale;
                captured.Add(blade);
                // RenderWithShader is synchronous. Restore in the caller's finally
                // block before another camera, Animator or physics step can run.
                blade.Root.localPosition = blade.Position;
                blade.Root.localRotation = blade.Rotation;
                blade.Root.localScale = blade.Scale;
            }
        }

        public void RestoreAfterStaticCapture()
        {
            foreach (var blade in captured)
            {
                if (blade.Root == null) continue;
                blade.Root.localPosition = blade.SavedPosition;
                blade.Root.localRotation = blade.SavedRotation;
                blade.Root.localScale = blade.SavedScale;
            }
            captured.Clear();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) { scan.Dispose(); visitedAnimators.Clear(); }
        private void OnSceneUnloaded(Scene scene) { scan.Dispose(); visitedAnimators.Clear(); nextCleanup = 0; }

        public void Dispose()
        {
            RestoreAfterStaticCapture();
            if (subscribed)
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                SceneManager.sceneUnloaded -= OnSceneUnloaded;
                subscribed = false;
            }
            scan.Dispose(); visitedAnimators.Clear();
            blades.Clear(); visible.Clear(); expired.Clear(); materials.Clear();
            geometryChanged = false; nextCleanup = 0;
        }
    }
}
