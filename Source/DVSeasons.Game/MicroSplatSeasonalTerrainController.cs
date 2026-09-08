using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    internal sealed class MicroSplatSeasonalTerrainController : IDisposable
    {
        private sealed class Binding
        {
            public Material Material;
            public Shader Shader;
            public string Property;
            public Texture Original;
            public Texture Applied;
            public bool IsDistantTerrain;
        }

        private sealed class LayeredArrayState
        {
            public Texture2DArray Clear;
            public RenderTexture Output;
            public int CoverageStep = -1;
            public int AppliedLayerCount;
            public bool Failed;
        }

        private sealed class TerrainLayerBinding
        {
            public TerrainLayer Layer;
            public Texture2D Original;
            public Texture2D Winter;
            public Texture2D Applied;
            public int WinterRank;
        }

        private static readonly string[] DiffuseProperties =
        {
            // All four arrays participate at different viewing distances. Each is
            // now given its own layered copy based on that property's original
            // array; sharing one global clear array was what produced the large
            // neon-green mosaic patches around industrial terrain.
            "_Diffuse",
            "_ClusterDiffuse2",
            "_ClusterDiffuse3",
            "_DistanceResampleHackDiff"
        };
        // The order is deliberately spread across the 16 MicroSplat slots. Nearby
        // slot numbers often describe related ground materials, so replacing them
        // sequentially would turn one surface family winter-white all at once.
        private static readonly int[] WinterLayerOrder =
        {
            // Slots 13 and 12 are the two broad grass/yard ground families seen on
            // the screenshots. Both must be covered first: leaving slot 12 until
            // the final step kept a large brown/green surface snow-free through
            // almost the whole winter transition.
            13, 12, 1, 6, 11, 0, 5, 10, 15, 4, 9, 14, 3, 8, 2, 7
        };
        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>();
        private readonly SeasonAssetBundleRepository texturePack;
        private readonly Dictionary<string, Texture> canonicalSummerArrays =
            new Dictionary<string, Texture>(StringComparer.Ordinal);
        private readonly Dictionary<int, LayeredArrayState> layeredArrays =
            new Dictionary<int, LayeredArrayState>();
        private readonly Dictionary<int, TerrainLayerBinding> terrainLayerBindings =
            new Dictionary<int, TerrainLayerBinding>();
        private float nextScanTime;
        private float nextReapplyTime;
        private int lastSeasonKey = int.MinValue;
        private bool discoveryLogged;
        private bool restored=true;
        public int RestorePassCount { get; private set; }
        private bool missingWinterArrayLogged;

        public MicroSplatSeasonalTerrainController(SeasonAssetBundleRepository texturePack)
        {
            this.texturePack = texturePack;
        }

        public void Apply(SeasonState state, SeasonModSettings settings, bool proceduralSnow = false, float? coverageOverride = null)
        {
            if (state == null || settings == null) return;
            if (!settings.SeasonalTexturesEnabled || !settings.TerrainTextureChanges ||
                settings.TextureChangeStrength <= 0.001f)
            {
                Restore();
                return;
            }

            restored=false;
            if (Time.realtimeSinceStartup >= nextScanTime)
            {
                // Resources.FindObjectsOfTypeAll<Material>() is expensive in DV's
                // large streamed world. Ten seconds is still quick enough to catch
                // newly loaded distant terrain without causing a regular FPS hitch.
                nextScanTime = Time.realtimeSinceStartup + 10f;
                Scan(settings.DistantTerrainSeasonal);
            }

            // Native landscape arrays cover every terrain LOD, including native
            // distant terrain beyond the screen-space snow exposure maps.
            var coverage = coverageOverride ?? (settings.GroundSnowEnabled
                ? Mathf.Clamp01(state.SnowAmount * settings.GroundSnowStrength * settings.TextureChangeStrength)
                : 0f);
            var coverageStep = SnowCoverProfile.GetGroundTextureStep(coverage);
            var seasonKey = coverageStep |
                (settings.DistantTerrainSeasonal ? 0x100 : 0);
            if (seasonKey != lastSeasonKey)
            {
                lastSeasonKey = seasonKey;
                ApplyCoverage(coverageStep, settings.DistantTerrainSeasonal);
            }
            else if (Time.realtimeSinceStartup >= nextReapplyTime)
            {
                nextReapplyTime = Time.realtimeSinceStartup + 1f;
                ReapplyToNewBindings(coverageStep, settings.DistantTerrainSeasonal);
            }
        }

        public void Dispose()
        {
            Restore();
            bindings.Clear();
            terrainLayerBindings.Clear();
            canonicalSummerArrays.Clear();
            foreach (var state in layeredArrays.Values)
                if (state.Output != null) UnityEngine.Object.Destroy(state.Output);
            layeredArrays.Clear();
            nextScanTime = 0f;
            nextReapplyTime = 0f;
            discoveryLogged = false;
            missingWinterArrayLogged = false;
        }

        private void Scan(bool includeDistantTerrain)
        {
            var terrains = Terrain.activeTerrains;
            var added = 0;
            var rendererMaterialAdded = 0;
            var distantAdded = 0;
            for (var i = 0; i < terrains.Length; i++)
            {
                var terrain = terrains[i];
                if (terrain == null) continue;
                var material = terrain.materialTemplate;
                if (material != null) added += ScanMaterial(material, false);
                added += ScanTerrainLayers(terrain);
            }

            // DV99 does not expose every streamed landscape tile through
            // Terrain.activeTerrains. Some near tiles are ordinary renderers which
            // share the built-in MicroSplat material from sharedassets5. Looking for
            // that exact material signature catches those green islands without
            // reviving the old broad "terrain-like" scan that also modified custom
            // map impostors and produced giant white/blurred surfaces.
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            for (var i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (IsBuiltInMicroSplatMaterial(material))
                {
                    rendererMaterialAdded += ScanMaterial(material, false);
                    continue;
                }
                if (includeDistantTerrain && IsDistantTerrainMaterial(material))
                    distantAdded += ScanDistantTerrainMaterial(material);
            }
            added += rendererMaterialAdded + distantAdded;

            if (!discoveryLogged && (added > 0 || terrains.Length > 0))
            {
                discoveryLogged = true;
                Debug.Log("[DVSeasons] Terrain scan: " + bindings.Count +
                    " compatible array binding(s), " + terrainLayerBindings.Count +
                    " direct built-in TerrainLayer binding(s); renderer MicroSplat is matched " +
                    "by its built-in 16-layer signature and DistantTerrain only by the exact " +
                    "DV/DistantTerrain shader.");
            }
            if (rendererMaterialAdded > 0)
                Debug.Log("[DVSeasons] Bound " + rendererMaterialAdded +
                    " streamed renderer MicroSplat array(s).");
            if (distantAdded > 0)
                Debug.Log("[DVSeasons] Bound " + distantAdded +
                    " exact DistantTerrain _Splats material array(s).");
        }

        private int ScanMaterial(Material material, bool isDistantTerrain)
        {
            var added = 0;
            for (var propertyIndex = 0; propertyIndex < DiffuseProperties.Length; propertyIndex++)
            {
                var property = DiffuseProperties[propertyIndex];
                if (!HasTextureProperty(material, property)) continue;
                var observed = material.GetTexture(property) as Texture2DArray;
                if (observed == null || observed.depth != WinterLayerOrder.Length) continue;
                var key = material.GetInstanceID() + "|" + property;
                Binding existing;
                if (bindings.TryGetValue(key, out existing) && IsCurrentTextureBinding(existing))
                    continue;
                Texture original;
                if (IsSeasonalTerrainTexture(observed))
                {
                    if (!canonicalSummerArrays.TryGetValue(property, out original)) continue;
                }
                else
                {
                    original = observed;
                    if (!canonicalSummerArrays.ContainsKey(property))
                        canonicalSummerArrays.Add(property, observed);
                }
                // A material can keep its instance ID while its shader is replaced.
                // Replace such a stale binding instead of retaining assumptions
                // about the old shader's property layout.
                bindings[key] = new Binding
                {
                    Material = material,
                    Shader = material.shader,
                    Property = property,
                    Original = original,
                    IsDistantTerrain = isDistantTerrain
                };
                added++;
            }
            return added;
        }

        private int ScanTerrainLayers(Terrain terrain)
        {
            var data = terrain == null ? null : terrain.terrainData;
            var layers = data == null ? null : data.terrainLayers;
            // DV99's material-free near terrain uses the same sixteen logical slots
            // as the MicroSplat arrays. Do not guess on custom terrains: a different
            // layer count has no reliable mapping to the bundled winter set.
            if (layers == null || layers.Length != WinterLayerOrder.Length) return 0;

            var added = 0;
            for (var layerIndex = 0; layerIndex < layers.Length; layerIndex++)
            {
                var layer = layers[layerIndex];
                if (layer == null || layer.diffuseTexture == null ||
                    terrainLayerBindings.ContainsKey(layer.GetInstanceID()))
                    continue;
                Texture2D winter;
                if (!texturePack.TryGetTerrainLayer(SeasonKind.Winter, layerIndex, out winter) ||
                    winter == null)
                    continue;
                terrainLayerBindings.Add(layer.GetInstanceID(), new TerrainLayerBinding
                {
                    Layer = layer,
                    Original = layer.diffuseTexture,
                    Winter = winter,
                    WinterRank = GetWinterRank(layerIndex)
                });
                added++;
            }
            return added;
        }

        private static int GetWinterRank(int layerIndex)
        {
            for (var rank = 0; rank < WinterLayerOrder.Length; rank++)
                if (WinterLayerOrder[rank] == layerIndex) return rank;
            return WinterLayerOrder.Length;
        }

        private int ScanDistantTerrainMaterial(Material material)
        {
            const string property = "_Splats";
            if (!HasTextureProperty(material, property)) return 0;
            var observed = material.GetTexture(property) as Texture2DArray;
            if (observed == null || observed.depth != WinterLayerOrder.Length) return 0;

            var key = material.GetInstanceID() + "|" + property;
            Binding existing;
            if (bindings.TryGetValue(key, out existing) && IsCurrentTextureBinding(existing))
                return 0;
            Texture original;
            if (IsSeasonalTerrainTexture(observed))
            {
                if (!canonicalSummerArrays.TryGetValue(property, out original)) return 0;
            }
            else
            {
                original = observed;
                if (!canonicalSummerArrays.ContainsKey(property))
                    canonicalSummerArrays.Add(property, observed);
            }
            bindings[key] = new Binding
            {
                Material = material,
                Shader = material.shader,
                Property = property,
                Original = original,
                IsDistantTerrain = true
            };
            return 1;
        }

        private static bool IsDistantTerrainMaterial(Material material)
        {
            if (material == null || material.shader == null) return false;
            return string.Equals(material.shader.name, "DV/DistantTerrain",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasTextureProperty(Material material, string property)
        {
            if (material == null || string.IsNullOrEmpty(property)) return false;
            var shader = material.shader;
            if (shader == null) return false;

            // Material.HasProperty is not sufficient here. Unity can retain a saved
            // material value with the same name after a shader swap even when the
            // new shader exposes that name as a Color/Float. Calling GetTexture or
            // SetTexture in that state emits "doesn't have a texture property".
            // Unity 2019 exposes the declared type through Shader's property API.
            var propertyIndex = shader.FindPropertyIndex(property);
            return propertyIndex >= 0 &&
                shader.GetPropertyType(propertyIndex) == ShaderPropertyType.Texture;
        }

        private static bool IsCurrentTextureBinding(Binding binding)
        {
            return binding != null && binding.Material != null &&
                binding.Shader != null && binding.Material.shader == binding.Shader &&
                HasTextureProperty(binding.Material, binding.Property);
        }

        private bool IsBuiltInMicroSplatMaterial(Material material)
        {
            if (!HasTextureProperty(material, "_Diffuse")) return false;
            var diffuse = material.GetTexture("_Diffuse") as Texture2DArray;
            if (diffuse == null || diffuse.depth != WinterLayerOrder.Length) return false;

            var materialName = material.name ?? string.Empty;
            var shaderName = material.shader == null ? string.Empty : material.shader.name;
            var textureName = diffuse.name ?? string.Empty;
            var materialSignature =
                materialName.StartsWith("MicroSplat", StringComparison.OrdinalIgnoreCase) ||
                shaderName.IndexOf("MicroSplat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                HasBuiltInMicroSplatCompanions(material);
            var arraySignature =
                string.Equals(textureName, "MicroSplatConfig_diff_tarray",
                    StringComparison.OrdinalIgnoreCase) ||
                IsSeasonalTerrainTexture(diffuse);
            return materialSignature && arraySignature;
        }

        private static bool HasBuiltInMicroSplatCompanions(Material material)
        {
            return HasTextureProperty(material, "_ClusterDiffuse2") &&
                HasTextureProperty(material, "_ClusterDiffuse3") &&
                HasTextureProperty(material, "_DistanceResampleHackDiff");
        }

        private void ApplyCoverage(int coverageStep, bool distantTerrainEnabled)
        {
            foreach (var binding in bindings.Values)
            {
                if (!IsCurrentTextureBinding(binding)) continue;
                Texture target;
                if (binding.IsDistantTerrain && !distantTerrainEnabled)
                    target = binding.Original;
                else if (!TryGetCoverageTexture(binding, coverageStep, out target))
                    target = binding.Original;
                Apply(binding, target ?? binding.Original);
            }
            ApplyTerrainLayers(coverageStep);
            Debug.Log("[DVSeasons] MicroSplat ground coverage step " + coverageStep + "/" +
                SnowCoverProfile.GroundTextureSteps + " applied (" + bindings.Count +
                " array binding(s), " + terrainLayerBindings.Count +
                " direct TerrainLayer binding(s)).");
        }

        private void ReapplyToNewBindings(int coverageStep, bool distantTerrainEnabled)
        {
            foreach (var binding in bindings.Values)
            {
                if (!IsCurrentTextureBinding(binding)) continue;
                Texture target;
                if (binding.IsDistantTerrain && !distantTerrainEnabled)
                    target = binding.Original;
                else if (!TryGetCoverageTexture(binding, coverageStep, out target))
                    target = binding.Original;
                var current = binding.Material.GetTexture(binding.Property);
                // Reassert our intended state when a streamed terrain material was
                // repopulated with an older DVSeasons array after the cover-mode key
                // had already changed. Preserve unrelated non-seasonal replacements
                // made by other mods.
                if (binding.Applied == null || current == binding.Applied ||
                    IsSeasonalTerrainTexture(current))
                {
                    if (current != target || binding.Applied != target)
                        Apply(binding, target);
                }
            }
            ApplyTerrainLayers(coverageStep);
        }

        private void ApplyTerrainLayers(int coverageStep)
        {
            var desiredLayerCount = Mathf.Clamp(Mathf.CeilToInt(
                coverageStep / (float)SnowCoverProfile.GroundTextureSteps *
                WinterLayerOrder.Length), 0, WinterLayerOrder.Length);
            var changed = false;
            foreach (var binding in terrainLayerBindings.Values)
            {
                if (binding.Layer == null) continue;
                var target = binding.WinterRank < desiredLayerCount
                    ? binding.Winter
                    : binding.Original;
                var current = binding.Layer.diffuseTexture;
                if (current == target)
                {
                    binding.Applied = target;
                    continue;
                }
                // Reassert a streamed/reloaded layer only while it still contains
                // our prior value or its original. Preserve unrelated third-party
                // replacements instead of taking ownership of arbitrary textures.
                if (binding.Applied == null || current == binding.Applied ||
                    current == binding.Original || current == binding.Winter)
                {
                    binding.Layer.diffuseTexture = target;
                    binding.Applied = target;
                    changed = true;
                }
            }
            if (changed) FlushTerrains();
        }

        private static void FlushTerrains()
        {
            var terrains = Terrain.activeTerrains;
            for (var i = 0; i < terrains.Length; i++)
                if (terrains[i] != null) terrains[i].Flush();
        }

        private static void Apply(Binding binding, Texture target)
        {
            if (!IsCurrentTextureBinding(binding) || target == null) return;
            if(binding.Material.GetTexture(binding.Property)!=target) binding.Material.SetTexture(binding.Property, target);
            binding.Applied = target;
        }

        private bool IsSeasonalTerrainTexture(Texture texture)
        {
            if (texture == null) return false;
            if (texturePack.IsTerrainArray(texture)) return true;
            foreach (var state in layeredArrays.Values)
                if (texture == state.Output) return true;
            var name = texture.name;
            return !string.IsNullOrEmpty(name) &&
                name.IndexOf("DVSeasons", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool TryGetCoverageTexture(Binding binding, int coverageStep, out Texture texture)
        {
            texture = null;
            if (coverageStep <= 0)
                return true;

            Texture2DArray winter;
            if (!texturePack.TryGetTerrainArray(SeasonKind.Winter, out winter))
            {
                if (!missingWinterArrayLogged)
                {
                    missingWinterArrayLogged = true;
                    Debug.LogWarning("[DVSeasons] Winter MicroSplat terrain array is unavailable; will retry loading it.");
                }
                return false;
            }
            missingWinterArrayLogged = false;

            if (coverageStep >= SnowCoverProfile.GroundTextureSteps)
            {
                texture = winter;
                return true;
            }

            var clear = binding.Original as Texture2DArray;
            if (!CanBuildLayeredArray(clear, winter))
            {
                Debug.LogWarning("[DVSeasons] MicroSplat array " + binding.Property +
                    " on material '" + binding.Material.name + "' does not expose the " +
                    "16 source layers required for a layered transition.");
                texture = winter;
                return true;
            }

            LayeredArrayState state;
            if (!layeredArrays.TryGetValue(clear.GetInstanceID(), out state))
            {
                state = new LayeredArrayState { Clear = clear };
                layeredArrays.Add(clear.GetInstanceID(), state);
            }
            if (!UpdateLayeredArray(state, coverageStep, winter))
                texture = winter;
            else
                texture = state.Output;
            return true;
        }

        private bool UpdateLayeredArray(
            LayeredArrayState state,
            int coverageStep,
            Texture2DArray winter)
        {
            if (state.Failed) return false;
            if (state.CoverageStep == coverageStep && state.Output != null) return true;
            try
            {
                ValidateLayerSources(state.Clear, winter);

                if (state.Output == null)
                {
                    state.Output = new RenderTexture(
                        winter.width,
                        winter.height,
                        0,
                        RenderTextureFormat.ARGB32,
                        RenderTextureReadWrite.Default)
                    {
                        name = "DVSeasons Layered Winter Terrain " + state.Clear.GetInstanceID(),
                        dimension = TextureDimension.Tex2DArray,
                        volumeDepth = winter.depth,
                        useMipMap = true,
                        autoGenerateMips = false,
                        wrapMode = winter.wrapMode,
                        filterMode = winter.filterMode,
                        anisoLevel = Mathf.Max(state.Clear.anisoLevel, winter.anisoLevel),
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    if (!state.Output.Create())
                        throw new InvalidOperationException(
                            "GPU render array for the terrain transition could not be created");
                    for (var slice = 0; slice < state.Clear.depth; slice++)
                        BlitLayer(state.Clear, slice, state.Output);
                    state.AppliedLayerCount = 0;
                    Debug.Log("[DVSeasons] Created a layered terrain copy from the real '" +
                        state.Clear.name + "' array (" + state.Clear.width + "x" +
                        state.Clear.height + " -> " + winter.width + "x" + winter.height +
                        ", " + winter.depth + " layers).");
                }

                var desiredLayerCount = Mathf.Clamp(Mathf.CeilToInt(
                    coverageStep / (float)SnowCoverProfile.GroundTextureSteps * winter.depth),
                    0,
                    winter.depth);
                if (desiredLayerCount > state.AppliedLayerCount)
                {
                    for (var rank = state.AppliedLayerCount; rank < desiredLayerCount; rank++)
                        BlitLayer(winter, WinterLayerOrder[rank], state.Output);
                }
                else if (desiredLayerCount < state.AppliedLayerCount)
                {
                    for (var rank = state.AppliedLayerCount - 1; rank >= desiredLayerCount; rank--)
                        BlitLayer(state.Clear, WinterLayerOrder[rank], state.Output);
                }

                state.Output.GenerateMips();
                state.AppliedLayerCount = desiredLayerCount;
                state.CoverageStep = coverageStep;
                Debug.Log("[DVSeasons] Layered terrain snow step " + coverageStep + "/" +
                    SnowCoverProfile.GroundTextureSteps + ": " + state.AppliedLayerCount + "/" +
                    winter.depth + " MicroSplat layers use their winter texture.");
                return true;
            }
            catch (Exception exception)
            {
                state.Failed = true;
                if (state.Output != null) UnityEngine.Object.Destroy(state.Output);
                state.Output = null;
                Debug.LogWarning("[DVSeasons] Layer-by-layer terrain snow is unavailable for " +
                    "this source array; leaving it unchanged: " + exception.Message);
                return false;
            }
        }

        private static bool CanBuildLayeredArray(Texture2DArray clear, Texture2DArray winter)
        {
            return clear != null && winter != null &&
                clear.depth == winter.depth &&
                clear.depth == WinterLayerOrder.Length;
        }

        private static void ValidateLayerSources(Texture2DArray clear, Texture2DArray winter)
        {
            if (!CanBuildLayeredArray(clear, winter))
                throw new InvalidOperationException(
                    "clear and winter terrain arrays have incompatible layer counts");
        }

        private static void BlitLayer(
            Texture2DArray source,
            int slice,
            RenderTexture destination)
        {
            // DV's arrays are 1024px while the stable 0.1.34 seasonal bundle is
            // 512px. This Texture2DArray-aware Blit selects the real source slice,
            // scales it on the GPU and writes it into the matching destination
            // slice. It keeps each map's material numbering, unlike the old spring
            // fallback that produced the bright green industrial rectangle.
            Graphics.Blit(source, destination, slice, slice);
        }

        private void Restore()
        {
            if(restored) return;
            restored=true;RestorePassCount++;
            foreach (var binding in bindings.Values)
            {
                if (!IsCurrentTextureBinding(binding))
                {
                    // The material may survive a streamed-scene shader swap. Its old
                    // property layout is no longer safe to query; a later Scan can
                    // replace this binding if the new shader is compatible.
                    binding.Applied = null;
                    continue;
                }
                var current = binding.Material.GetTexture(binding.Property);
                if (binding.Applied == null || current == binding.Applied ||
                    IsSeasonalTerrainTexture(current))
                    if(current!=binding.Original) binding.Material.SetTexture(binding.Property, binding.Original);
                binding.Applied = null;
            }
            var terrainLayersChanged = false;
            foreach (var binding in terrainLayerBindings.Values)
            {
                if (binding.Layer == null) continue;
                var current = binding.Layer.diffuseTexture;
                if (binding.Applied == null || current == binding.Applied ||
                    current == binding.Winter)
                {
                    if (current != binding.Original)
                    {
                        binding.Layer.diffuseTexture = binding.Original;
                        terrainLayersChanged = true;
                    }
                }
                binding.Applied = null;
            }
            if (terrainLayersChanged) FlushTerrains();
            lastSeasonKey = int.MinValue;
            nextReapplyTime = 0f;
        }
    }
}
