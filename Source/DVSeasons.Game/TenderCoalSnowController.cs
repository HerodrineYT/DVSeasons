using System;
using System.Collections.Generic;
using DV.ThingTypes;
using DVSeasons.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    internal sealed class TenderCoalSnowController : IDisposable
    {
        private const int BlendSize = 1024;
        private static readonly int MainTextureId = Shader.PropertyToID("_MainTex");
        private static readonly int SnowTextureId = Shader.PropertyToID("_DVPSCoalSnow");
        private static readonly int SnowAmountId = Shader.PropertyToID("_DVPSCoalAmount");

        private sealed class SourceMaterial
        {
            public Material Material;
            public int CheckedEpoch = -1, Crc, Version, Users;
        }

        private sealed class CoalBlend
        {
            public Texture Source;
            public RenderTexture Texture;
            public int Step, Users;
        }

        private sealed class Binding
        {
            public Renderer Renderer;
            public int Slot;
            public long Key;
            public Material Original, Winter;
            public SourceMaterial SourceMaterial;
            public TrainCar Car;
            public CoalBlend Blend;
            public Texture SourceTexture, AppliedTexture;
            public int Step, SourceVersion = -1;
        }

        private readonly List<Binding> bindings = new List<Binding>();
        private readonly HashSet<long> known = new HashSet<long>();
        private readonly Dictionary<int, SourceMaterial> sources = new Dictionary<int, SourceMaterial>();
        private readonly Dictionary<long, CoalBlend> blends = new Dictionary<long, CoalBlend>();
        private readonly List<long> unusedBlends = new List<long>();
        private readonly Stack<RenderTexture> reusableTextures = new Stack<RenderTexture>();
        private readonly List<MeshRenderer> rendererScratch = new List<MeshRenderer>();
        private readonly SeasonAssetBundleRepository repository;
        private Texture2D snow;
        private Material blendMaterial;
        private float nextScan, nextLoad;
        private int materialEpoch;
        internal int BlendRenderCount { get; private set; }
        internal int MaterialCopyCount { get; private set; }
        public Func<Component,bool> SnowObjectAllowed;

        public TenderCoalSnowController(SeasonAssetBundleRepository repository) { this.repository = repository; }
        private static long Key(Renderer renderer, int slot) { return ((long)renderer.GetInstanceID() << 32) | (uint)slot; }
        public bool HasSnowTexture(Renderer renderer, int slot) { return renderer != null && known.Contains(Key(renderer, slot)); }

        public void Apply(float coverage, Func<Component, float> remaining)
        {
            if (coverage <= 0.001f) { Restore(); return; }
            if ((snow == null || blendMaterial == null) && Time.realtimeSinceStartup < nextLoad) return;
            if (snow == null || blendMaterial == null) nextLoad = Time.realtimeSinceStartup + 5f;
            if (snow == null)
            {
                if (!repository.TryLoadTexture("Coal_01d", SeasonKind.Winter, out snow)) return;
                if (blendMaterial != null) blendMaterial.SetTexture(SnowTextureId, snow);
            }
            if (blendMaterial == null)
            {
                var shader = repository.LoadShader("SnowVehicle");
                if (shader == null) return;
                blendMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                blendMaterial.SetTexture(SnowTextureId, snow);
            }
            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup + 3f;
                foreach (var car in RailSnowGameSource.GetCars())
                {
                    if (car == null || (car.carType != TrainCarType.Tender && car.carType != TrainCarType.LocoS060 && car.carType != TrainCarType.LocoSteamHeavy)) continue;
                    Register(car, car.transform);
                    if (car.interior != null) Register(car, car.interior.transform);
                }
            }

            // Release all changed references before reusing their render targets.
            // Only currently needed snow stages are retained, rather than caching
            // every stage a tender has passed through during the session.
            for (var i = bindings.Count - 1; i >= 0; i--)
            {
                var binding = bindings[i];
                if (binding.Renderer == null || binding.Car == null || binding.Original == null)
                {
                    known.Remove(binding.Key);
                    Release(binding);
                    bindings.RemoveAt(i);
                    continue;
                }
                var amount = coverage * (remaining != null ? remaining(binding.Car) : 1f);
                if(SnowObjectAllowed != null && !SnowObjectAllowed(binding.Car)) amount = 0f;
                binding.Step = Mathf.RoundToInt(Mathf.Clamp01(amount) * 32);
                binding.SourceTexture = binding.Original.GetTexture(MainTextureId);
                if (binding.Blend != null && (binding.Blend.Source != binding.SourceTexture || binding.Blend.Step != binding.Step))
                    ReleaseBlend(binding);
            }
            RecycleUnusedBlends();

            materialEpoch++;
            foreach (var binding in bindings)
            {
                if (binding.Blend == null) binding.Blend = AcquireBlend(binding.SourceTexture, binding.Step);
                SynchronizeMaterial(binding);
            }
            // Retain one spare for the next stage change without allocating a new
            // 1024-square GPU image. Memory cannot grow with historical stages.
            while (reusableTextures.Count > 1) Destroy(reusableTextures.Pop());
        }

        private CoalBlend AcquireBlend(Texture source, int step)
        {
            var key = ((long)(source != null ? source.GetInstanceID() : 0) << 6) | (uint)step;
            CoalBlend result;
            if (!blends.TryGetValue(key, out result))
            {
                var texture = reusableTextures.Count > 0 ? reusableTextures.Pop() : new RenderTexture(
                    BlendSize, BlendSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                {
                    name = "DVSeasons shared coal blend", useMipMap = true, autoGenerateMips = true,
                    filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Repeat, anisoLevel = 4,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (!texture.IsCreated()) texture.Create();
                blendMaterial.SetFloat(SnowAmountId, step / 32f);
                var previous = RenderTexture.active;
                try { Graphics.Blit(source, texture, blendMaterial, 4); }
                finally { RenderTexture.active = previous; }
                result = new CoalBlend { Source = source, Step = step, Texture = texture };
                blends.Add(key, result);
                BlendRenderCount++;
            }
            result.Users++;
            return result;
        }

        private void SynchronizeMaterial(Binding binding)
        {
            var source = binding.SourceMaterial;
            if (source.CheckedEpoch != materialEpoch)
            {
                // Unity's native content CRC includes material properties and
                // keywords. This preserves changes made by controllers holding
                // the original material, without dirtying every winter material
                // each frame. Shared originals are checked once per Apply.
                var crc = source.Material.ComputeCRC();
                if (source.CheckedEpoch < 0 || source.Crc != crc)
                {
                    source.Crc = crc;
                    source.Version++;
                }
                source.CheckedEpoch = materialEpoch;
            }
            var changed = binding.SourceVersion != source.Version;
            if (changed)
            {
                if (binding.Winter.shader != binding.Original.shader) binding.Winter.shader = binding.Original.shader;
                binding.Winter.CopyPropertiesFromMaterial(binding.Original);
                binding.SourceVersion = source.Version;
                MaterialCopyCount++;
            }
            if (changed || binding.AppliedTexture != binding.Blend.Texture)
            {
                binding.Winter.SetTexture(MainTextureId, binding.Blend.Texture);
                binding.AppliedTexture = binding.Blend.Texture;
            }
        }

        private static void ReleaseBlend(Binding binding)
        {
            if (binding.Blend != null) binding.Blend.Users--;
            binding.Blend = null;
        }

        private void RecycleUnusedBlends()
        {
            unusedBlends.Clear();
            foreach (var pair in blends)
            {
                if (pair.Value.Users > 0) continue;
                unusedBlends.Add(pair.Key);
                reusableTextures.Push(pair.Value.Texture);
            }
            foreach (var key in unusedBlends) blends.Remove(key);
        }

        private void Register(TrainCar car, Transform root)
        {
            rendererScratch.Clear();
            root.GetComponentsInChildren(true, rendererScratch);
            foreach (var renderer in rendererScratch)
            {
                var materials = renderer.sharedMaterials;
                var changed = false;
                for (var slot = 0; slot < materials.Length; slot++)
                {
                    var key = Key(renderer, slot);
                    if (known.Contains(key)) continue;
                    var original = materials[slot];
                    if (!HasTextureProperty(original, "_MainTex")) continue;
                    var mainTexture = original.GetTexture(MainTextureId);
                    if (mainTexture == null || !string.Equals(mainTexture.name, "Coal_01d", StringComparison.OrdinalIgnoreCase)) continue;
                    SourceMaterial source;
                    var originalId = original.GetInstanceID();
                    if (!sources.TryGetValue(originalId, out source))
                    {
                        source = new SourceMaterial { Material = original };
                        sources.Add(originalId, source);
                    }
                    var winter = new Material(original) { name = original.name + " [DVSeasons tender snow]", hideFlags = HideFlags.HideAndDontSave };
                    source.Users++;
                    bindings.Add(new Binding { Renderer = renderer, Slot = slot, Key = key, Original = original,
                        Winter = winter, Car = car, SourceMaterial = source });
                    known.Add(key);
                    materials[slot] = winter;
                    changed = true;
                }
                if (changed) renderer.sharedMaterials = materials;
            }
            rendererScratch.Clear();
        }

        private static bool HasTextureProperty(Material material, string property)
        {
            if (material == null || material.shader == null) return false;
            var index = material.shader.FindPropertyIndex(property);
            return index >= 0 && material.shader.GetPropertyType(index) == ShaderPropertyType.Texture;
        }
        private static void Destroy(UnityEngine.Object value)
        { if (value == null) return; if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); }
        private void Release(Binding binding)
        {
            if (binding.Renderer != null)
            {
                var materials = binding.Renderer.sharedMaterials;
                if (binding.Slot < materials.Length && materials[binding.Slot] == binding.Winter)
                { materials[binding.Slot] = binding.Original; binding.Renderer.sharedMaterials = materials; }
            }
            ReleaseBlend(binding);
            if (--binding.SourceMaterial.Users == 0) sources.Remove(binding.Original.GetInstanceID());
            Destroy(binding.Winter);
        }
        private void Restore()
        {
            foreach (var binding in bindings) Release(binding);
            foreach (var entry in blends.Values) Destroy(entry.Texture);
            while (reusableTextures.Count > 0) Destroy(reusableTextures.Pop());
            bindings.Clear(); known.Clear(); sources.Clear(); blends.Clear(); unusedBlends.Clear(); nextScan = 0;
        }
        public void Dispose()
        {
            Restore();
            // Repository owns the bundled/override texture and can serve it again.
            Destroy(blendMaterial); snow = null; blendMaterial = null; nextLoad = 0;
        }
    }
}
