using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class SeasonalTextureController : IDisposable
    {
        private enum TextureCategory : byte { Terrain, Foliage, Bark, Ballast, Billboard }

        private sealed class SeasonalTextureSet
        {
            private readonly Texture2D source;
            private readonly TextureCategory category;
            private readonly int maximumResolution;
            private readonly SeasonAssetBundleRepository texturePack;
            private readonly bool evergreen;
            private Color32[] basePixels;
            private Color32[][] profiles;
            private Color32[] outputPixels;
            private int lastStyleKey = int.MinValue;
            private bool failed;

            public SeasonalTextureSet(Texture2D source, TextureCategory category, int maximumResolution,
                SeasonAssetBundleRepository texturePack)
            {
                this.source = source;
                this.category = category;
                this.maximumResolution = maximumResolution;
                this.texturePack = texturePack;
                var normalizedName = (source == null ? string.Empty : source.name).ToLowerInvariant();
                evergreen = normalizedName.Contains("fir") || normalizedName.Contains("pine") ||
                    normalizedName.Contains("spruce");
            }

            public Texture2D Output { get; private set; }
            public bool IsReady { get { return Output != null; } }
            public bool IsFailed { get { return failed; } }

            public void Update(SeasonState state, float strength, int styleKey)
            {
                if (failed) return;
                try
                {
                    if (!IsReady) Initialize();
                    if (!IsReady || styleKey == lastStyleKey) return;
                    lastStyleKey = styleKey;
                    var current = profiles[(int)state.Current];
                    var next = profiles[(int)state.Next];
                    var transition = Mathf.Clamp01(state.Transition);
                    var seasonalStrength = Mathf.Clamp01(strength);
                    for (var i = 0; i < outputPixels.Length; i++)
                    {
                        var seasonal = Lerp(current[i], next[i], transition);
                        outputPixels[i] = Lerp(basePixels[i], seasonal, seasonalStrength);
                        if (category == TextureCategory.Foliage)
                        {
                            var winterWeight = 0f;
                            if (state.Current == SeasonKind.Winter) winterWeight += 1f - transition;
                            if (state.Next == SeasonKind.Winter) winterWeight += transition;
                            var alphaStrength = Mathf.Max(seasonalStrength, Mathf.Clamp01(winterWeight));
                            var seasonalAlpha = seasonal.a;
                            if (!evergreen)
                                seasonalAlpha = (byte)Mathf.RoundToInt(seasonalAlpha *
                                    Mathf.Lerp(1f, 0.08f, Mathf.Clamp01(winterWeight)));
                            outputPixels[i].a = (byte)Mathf.RoundToInt(Mathf.Lerp(basePixels[i].a,
                                seasonalAlpha, alphaStrength));
                        }
                    }
                    Output.SetPixels32(outputPixels);
                    Output.Apply(false, false);
                }
                catch (Exception exception)
                {
                    failed = true;
                    Debug.LogWarning("[DVSeasons] Texture '" + (source == null ? "<destroyed>" : source.name) +
                        "' could not be converted: " + exception.Message);
                    DisposeOutput();
                }
            }

            public void Dispose()
            {
                DisposeOutput();
                basePixels = null;
                profiles = null;
                outputPixels = null;
            }

            private void Initialize()
            {
                if (source == null) return;
                var scale = Mathf.Min(1f, maximumResolution / (float)Mathf.Max(source.width, source.height));
                var width = Mathf.Max(16, Mathf.RoundToInt(source.width * scale));
                var height = Mathf.Max(16, Mathf.RoundToInt(source.height * scale));
                basePixels = ReadScaledPixels(source, width, height);

                profiles = new Color32[4][];
                var keepSurfaceNeutral = category == TextureCategory.Terrain ||
                    category == TextureCategory.Ballast || category == TextureCategory.Bark;
                profiles[(int)SeasonKind.Spring] = keepSurfaceNeutral
                    ? basePixels
                    : LoadOrCreateProfile(SeasonKind.Spring, width, height);
                profiles[(int)SeasonKind.Summer] = basePixels;
                profiles[(int)SeasonKind.Autumn] = keepSurfaceNeutral
                    ? basePixels
                    : LoadOrCreateProfile(SeasonKind.Autumn, width, height);
                profiles[(int)SeasonKind.Winter] = LoadOrCreateProfile(SeasonKind.Winter, width, height);
                outputPixels = new Color32[basePixels.Length];

                Output = new Texture2D(width, height, TextureFormat.RGBA32, false)
                {
                    name = source.name + " [DVSeasons " + category + "]",
                    wrapMode = source.wrapMode,
                    filterMode = source.filterMode,
                    anisoLevel = source.anisoLevel,
                    hideFlags = HideFlags.HideAndDontSave
                };
                Output.SetPixels32(basePixels);
                Output.Apply(false, false);
            }

            private Color32[] LoadOrCreateProfile(SeasonKind season, int width, int height)
            {
                Color32[] packed;
                if (season == SeasonKind.Winter && category == TextureCategory.Ballast &&
                    texturePack.TryLoadGenericWinterSnowPixels(width, height, out packed))
                    return packed;
                if (texturePack.TryLoadPixels(source.name, season, width, height, out packed)) return packed;
                return CreateFallbackProfile(basePixels, width, height, season, source.name, category);
            }

            private static Color32[] CreateFallbackProfile(Color32[] sourcePixels, int width, int height,
                SeasonKind season, string textureName, TextureCategory category)
            {
                var result = new Color32[sourcePixels.Length];
                var name = (textureName ?? string.Empty).ToLowerInvariant();
                var evergreen = name.Contains("fir") || name.Contains("pine") || name.Contains("spruce");
                for (var i = 0; i < sourcePixels.Length; i++)
                {
                    var pixel = sourcePixels[i];
                    if (season == SeasonKind.Spring)
                    {
                        result[i] = new Color32((byte)Mathf.Clamp(pixel.r * 0.92f + 8f, 0f, 255f),
                            (byte)Mathf.Clamp(pixel.g * 1.08f + 5f, 0f, 255f),
                            (byte)Mathf.Clamp(pixel.b * 0.90f + 4f, 0f, 255f), pixel.a);
                        continue;
                    }
                    if (season == SeasonKind.Autumn)
                    {
                        var luminance = (pixel.r * 0.30f) + (pixel.g * 0.59f) + (pixel.b * 0.11f);
                        result[i] = new Color32((byte)Mathf.Clamp(luminance * 1.25f + 24f, 0f, 255f),
                            (byte)Mathf.Clamp(luminance * 0.62f + 15f, 0f, 255f),
                            (byte)Mathf.Clamp(luminance * 0.25f + 8f, 0f, 255f), pixel.a);
                        continue;
                    }

                    var light = (pixel.r * 0.30f) + (pixel.g * 0.59f) + (pixel.b * 0.11f);
                    var greenDominant = pixel.g > pixel.r * 1.08f && pixel.g > pixel.b * 1.08f;
                    var y = i / width;
                    var upperSnow = Mathf.Clamp01((y / (float)Mathf.Max(1, height - 1) - 0.35f) * 1.2f);
                    if (category == TextureCategory.Billboard)
                    {
                        var frost = greenDominant ? 0.78f : 0.28f;
                        result[i] = new Color32((byte)Mathf.Lerp(pixel.r, light * 0.68f + 70f, frost),
                            (byte)Mathf.Lerp(pixel.g, light * 0.72f + 76f, frost),
                            (byte)Mathf.Lerp(pixel.b, light * 0.78f + 88f, frost), pixel.a);
                    }
                    else if (category == TextureCategory.Foliage && evergreen)
                    {
                        var snow = Mathf.Clamp01(upperSnow * (light / 255f) * 1.4f);
                        result[i] = new Color32((byte)Mathf.Lerp(light * 0.58f, 225f, snow),
                            (byte)Mathf.Lerp(light * 0.66f, 234f, snow),
                            (byte)Mathf.Lerp(light * 0.70f, 244f, snow), pixel.a);
                    }
                    else
                    {
                        var alpha = pixel.a;
                        if (category == TextureCategory.Foliage && greenDominant)
                            alpha = (byte)Mathf.RoundToInt(pixel.a * 0.08f);
                        result[i] = new Color32((byte)Mathf.Clamp(light * 0.70f + 38f, 0f, 255f),
                            (byte)Mathf.Clamp(light * 0.68f + 40f, 0f, 255f),
                            (byte)Mathf.Clamp(light * 0.72f + 48f, 0f, 255f), alpha);
                    }
                }
                return result;
            }

            private void DisposeOutput()
            {
                if (Output != null) UnityEngine.Object.Destroy(Output);
                Output = null;
            }

            private static Color32[] ReadScaledPixels(Texture sourceTexture, int width, int height)
            {
                var temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default);
                var previous = RenderTexture.active;
                Texture2D readable = null;
                try
                {
                    Graphics.Blit(sourceTexture, temporary);
                    RenderTexture.active = temporary;
                    readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                    readable.Apply(false, false);
                    return readable.GetPixels32();
                }
                finally
                {
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(temporary);
                    if (readable != null) UnityEngine.Object.Destroy(readable);
                }
            }

            private static Color32 Lerp(Color32 a, Color32 b, float t)
            {
                return new Color32((byte)Mathf.RoundToInt(Mathf.Lerp(a.r, b.r, t)),
                    (byte)Mathf.RoundToInt(Mathf.Lerp(a.g, b.g, t)),
                    (byte)Mathf.RoundToInt(Mathf.Lerp(a.b, b.b, t)),
                    (byte)Mathf.RoundToInt(Mathf.Lerp(a.a, b.a, t)));
            }
        }

        private sealed class MaterialBinding
        {
            public Material Material;
            public string Property;
            public Texture2D Original;
            public SeasonalTextureSet Set;
        }

        private sealed class TerrainLayerBinding
        {
            public TerrainLayer Layer;
            public Texture2D Original;
            public SeasonalTextureSet Set;
        }

        private readonly Dictionary<string, SeasonalTextureSet> sets = new Dictionary<string, SeasonalTextureSet>();
        private readonly Dictionary<string, MaterialBinding> materialBindings = new Dictionary<string, MaterialBinding>();
        private readonly Dictionary<int, TerrainLayerBinding> terrainBindings = new Dictionary<int, TerrainLayerBinding>();
        private readonly HashSet<int> scannedMaterials = new HashSet<int>();
        private readonly Queue<SeasonalTextureSet> updateQueue = new Queue<SeasonalTextureSet>();
        private readonly HashSet<SeasonalTextureSet> queued = new HashSet<SeasonalTextureSet>();
        private readonly SeasonAssetBundleRepository texturePack;
        private float nextScanTime;
        private int lastStyleKey = int.MinValue;
        private int configurationKey = int.MinValue;
        private bool active;

        public SeasonalTextureController(SeasonAssetBundleRepository texturePack)
        {
            this.texturePack = texturePack;
        }

        public void Apply(SeasonState state, SeasonModSettings settings)
        {
            if (!settings.SeasonalTexturesEnabled)
            {
                if (active) Reset();
                return;
            }
            active = true;
            var newConfigurationKey = settings.SeasonalTextureResolution * 397 ^ settings.MaximumSeasonalTextures ^
                (settings.TerrainTextureChanges ? 0x10000 : 0) ^
                (settings.VegetationTextureChanges ? 0x20000 : 0);
            if (configurationKey != int.MinValue && configurationKey != newConfigurationKey) Reset();
            configurationKey = newConfigurationKey;
            if (Time.realtimeSinceStartup >= nextScanTime)
            {
                nextScanTime = Time.realtimeSinceStartup + 12f;
                if (settings.TerrainTextureChanges) ScanTerrainLayers(settings);
                if (settings.TerrainTextureChanges || settings.VegetationTextureChanges)
                    ScanVegetationMaterials(settings);
            }

            var styleKey = BuildStyleKey(state, settings.TextureChangeStrength);
            if (styleKey != lastStyleKey)
            {
                lastStyleKey = styleKey;
                foreach (var set in sets.Values) Enqueue(set);
            }
            var budget = Mathf.Clamp(settings.TextureUpdatesPerFrame, 1, 4);
            for (var i = 0; i < budget && updateQueue.Count > 0; i++)
            {
                var set = updateQueue.Dequeue();
                queued.Remove(set);
                set.Update(state, settings.TextureChangeStrength, styleKey);
            }
            ApplyBindings();
        }

        public void Dispose() { Reset(); }

        private void ScanTerrainLayers(SeasonModSettings settings)
        {
            var activeTerrains = Terrain.activeTerrains;
            for (var i = 0; i < activeTerrains.Length; i++)
            {
                var terrain = activeTerrains[i];
                var data = terrain == null ? null : terrain.terrainData;
                if (data == null) continue;
                var material = terrain.materialTemplate;
                if (material != null && material.HasProperty("_Diffuse") &&
                    material.GetTexture("_Diffuse") is Texture2DArray)
                    continue;
                var layers = data.terrainLayers;
                for (var j = 0; j < layers.Length; j++)
                {
                    var layer = layers[j];
                    if (layer == null || layer.diffuseTexture == null || terrainBindings.ContainsKey(layer.GetInstanceID())) continue;
                    var set = GetOrCreate(layer.diffuseTexture, TextureCategory.Terrain, settings);
                    if (set == null) continue;
                    terrainBindings.Add(layer.GetInstanceID(), new TerrainLayerBinding
                    {
                        Layer = layer,
                        Original = layer.diffuseTexture,
                        Set = set
                    });
                }
            }
        }

        private void ScanVegetationMaterials(SeasonModSettings settings)
        {
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            for (var i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null || material.name.IndexOf("DVSeasons", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var materialId = material.GetInstanceID();
                if (!scannedMaterials.Add(materialId)) continue;
                var shaderName = material.shader == null ? string.Empty : material.shader.name;
                var propertyNames = material.GetTexturePropertyNames();
                for (var j = 0; j < propertyNames.Length; j++)
                {
                    var property = propertyNames[j];
                    if (!IsAlbedoProperty(property)) continue;
                    var texture = material.GetTexture(property) as Texture2D;
                    if (texture == null) continue;
                    TextureCategory category;
                    if (!TryClassifyVegetation(material.name + " " + texture.name + " " + shaderName, out category)) continue;
                    if (category == TextureCategory.Ballast && !settings.TerrainTextureChanges) continue;
                    if (category != TextureCategory.Ballast && !settings.VegetationTextureChanges) continue;
                    var bindingKey = materialId + "|" + property;
                    if (materialBindings.ContainsKey(bindingKey)) continue;
                    var set = GetOrCreate(texture, category, settings);
                    if (set == null) continue;
                    materialBindings.Add(bindingKey, new MaterialBinding
                    {
                        Material = material,
                        Property = property,
                        Original = texture,
                        Set = set
                    });
                }
            }
        }

        private SeasonalTextureSet GetOrCreate(Texture2D texture, TextureCategory category, SeasonModSettings settings)
        {
            var key = texture.GetInstanceID() + "|" + (int)category;
            SeasonalTextureSet set;
            if (sets.TryGetValue(key, out set)) return set;
            if (sets.Count >= settings.MaximumSeasonalTextures) return null;
            set = new SeasonalTextureSet(texture, category, settings.SeasonalTextureResolution, texturePack);
            sets.Add(key, set);
            Enqueue(set);
            return set;
        }

        private void ApplyBindings()
        {
            var terrainChanged = false;
            foreach (var binding in terrainBindings.Values)
            {
                if (binding.Layer != null && binding.Set.IsReady && binding.Layer.diffuseTexture != binding.Set.Output)
                {
                    binding.Layer.diffuseTexture = binding.Set.Output;
                    terrainChanged = true;
                }
            }
            foreach (var binding in materialBindings.Values)
            {
                if (binding.Material != null && binding.Set.IsReady && binding.Material.GetTexture(binding.Property) != binding.Set.Output)
                    binding.Material.SetTexture(binding.Property, binding.Set.Output);
            }
            if (terrainChanged) FlushTerrains();
        }

        private void Reset()
        {
            var terrainChanged = false;
            foreach (var binding in terrainBindings.Values)
            {
                if (binding.Layer != null && (binding.Set.Output == null || binding.Layer.diffuseTexture == binding.Set.Output))
                {
                    binding.Layer.diffuseTexture = binding.Original;
                    terrainChanged = true;
                }
            }
            foreach (var binding in materialBindings.Values)
            {
                if (binding.Material == null) continue;
                var current = binding.Material.GetTexture(binding.Property);
                if (binding.Set.Output == null || current == binding.Set.Output) binding.Material.SetTexture(binding.Property, binding.Original);
            }
            foreach (var set in sets.Values) set.Dispose();
            sets.Clear();
            terrainBindings.Clear();
            materialBindings.Clear();
            scannedMaterials.Clear();
            updateQueue.Clear();
            queued.Clear();
            nextScanTime = 0f;
            lastStyleKey = int.MinValue;
            configurationKey = int.MinValue;
            active = false;
            if (terrainChanged) FlushTerrains();
        }

        private void Enqueue(SeasonalTextureSet set)
        {
            if (!set.IsFailed && queued.Add(set)) updateQueue.Enqueue(set);
        }

        private static void FlushTerrains()
        {
            var terrains = Terrain.activeTerrains;
            for (var i = 0; i < terrains.Length; i++) if (terrains[i] != null) terrains[i].Flush();
        }

        private static int BuildStyleKey(SeasonState state, float strength)
        {
            var transitionStep = Mathf.RoundToInt(Mathf.Clamp01(state.Transition) * 32f);
            var strengthStep = Mathf.RoundToInt(Mathf.Clamp01(strength) * 20f);
            return ((int)state.Current << 16) | ((int)state.Next << 12) | (transitionStep << 5) | strengthStep;
        }

        private static bool IsAlbedoProperty(string property)
        {
            if (string.IsNullOrEmpty(property)) return false;
            var value = property.ToLowerInvariant();
            if (value.Contains("normal") || value.Contains("mask") || value.Contains("metal") ||
                value.Contains("spec") || value.Contains("rough") || value.Contains("height") || value.Contains("bump")) return false;
            return value.StartsWith("_maintex", StringComparison.Ordinal) || value.Contains("albedo") || value.Contains("diffuse") ||
                value.Contains("basemap") || value.Contains("basecolor");
        }

        private static bool TryClassifyVegetation(string description, out TextureCategory category)
        {
            var value = (description ?? string.Empty).ToLowerInvariant();
            if (ContainsAny(value, "ballast", "railbed", "rail bed", "embankment", "gravel"))
            {
                category = TextureCategory.Ballast;
                return true;
            }
            if (value.Contains("billboard_") || value.Contains("tree billboard") ||
                value.Contains("speedtree billboard"))
            {
                category = TextureCategory.Billboard;
                return true;
            }
            if (ContainsAny(value, "bark", "trunk", "branch", "wood"))
            {
                category = TextureCategory.Bark;
                return true;
            }
            if (ContainsAny(value, "leaf", "leaves", "foliage", "tree", "pine", "spruce", "fir", "bush",
                "grass", "vegetation", "forest", "shrub", "plant"))
            {
                category = TextureCategory.Foliage;
                return true;
            }
            category = TextureCategory.Foliage;
            return false;
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            for (var i = 0; i < terms.Length; i++) if (value.Contains(terms[i])) return true;
            return false;
        }
    }
}
