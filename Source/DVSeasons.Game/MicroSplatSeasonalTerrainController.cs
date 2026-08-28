using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class MicroSplatSeasonalTerrainController : IDisposable
    {
        private sealed class Binding
        {
            public Material Material;
            public string Property;
            public Texture Original;
            public Texture Applied;
            public bool IsDistantTerrain;
        }

        private static readonly string[] DiffuseProperties =
        {
            "_Diffuse",
            "_ClusterDiffuse2",
            "_ClusterDiffuse3",
            "_DistanceResampleHackDiff",
            "_Splats"
        };
        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>();
        private readonly SeasonAssetBundleRepository texturePack;
        private readonly Dictionary<string, Texture> canonicalSummerArrays =
            new Dictionary<string, Texture>(StringComparer.Ordinal);
        private Texture2DArray uniformWinterArray;
        private bool uniformWinterAttempted;
        private float nextScanTime;
        private int lastSeasonKey = int.MinValue;
        private bool discoveryLogged;

        public MicroSplatSeasonalTerrainController(SeasonAssetBundleRepository texturePack)
        {
            this.texturePack = texturePack;
        }

        public void Apply(SeasonState state, SeasonModSettings settings)
        {
            if (state == null || settings == null) return;
            if (!settings.SeasonalTexturesEnabled || !settings.TerrainTextureChanges ||
                settings.TextureChangeStrength <= 0.001f)
            {
                Restore();
                return;
            }

            if (Time.realtimeSinceStartup >= nextScanTime)
            {
                nextScanTime = Time.realtimeSinceStartup + 2f;
                Scan(settings.DistantTerrainSeasonal);
            }

            var selected = state.Transition < 0.5f ? state.Current : state.Next;
            var seasonKey = (int)selected | (settings.DistantTerrainSeasonal ? 0x100 : 0);
            if (seasonKey != lastSeasonKey)
            {
                lastSeasonKey = seasonKey;
                ApplySeason(selected, settings.DistantTerrainSeasonal);
            }
            else
            {
                ReapplyToNewBindings(selected, settings.DistantTerrainSeasonal);
            }
        }

        public void Dispose()
        {
            Restore();
            bindings.Clear();
            canonicalSummerArrays.Clear();
            if (uniformWinterArray != null) UnityEngine.Object.Destroy(uniformWinterArray);
            uniformWinterArray = null;
            uniformWinterAttempted = false;
        }

        private void Scan(bool includeDistantTerrain)
        {
            var terrains = Terrain.activeTerrains;
            var added = 0;
            var distantAdded = 0;
            for (var i = 0; i < terrains.Length; i++)
            {
                var material = terrains[i] == null ? null : terrains[i].materialTemplate;
                if (material == null) continue;
                added += ScanMaterial(material, false);
            }

            // Derail Valley renders the remote mountain ring as mesh geometry with a
            // separate material named DistantTerrain. It does not belong to
            // Terrain.activeTerrains and stores its diffuse Texture2DArray in _Splats.
            if (includeDistantTerrain)
            {
                var materials = Resources.FindObjectsOfTypeAll<Material>();
                for (var i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (!IsDistantTerrainMaterial(material)) continue;
                    distantAdded += ScanMaterial(material, true);
                }
                added += distantAdded;
            }

            if (!discoveryLogged && (added > 0 || terrains.Length > 0))
            {
                discoveryLogged = true;
                Debug.Log("[DVSeasons] MicroSplat terrain scan: " + bindings.Count +
                    " seasonal albedo binding(s), including the DistantTerrain _Splats array.");
            }
            if (distantAdded > 0)
                Debug.Log("[DVSeasons] Bound " + distantAdded +
                    " DistantTerrain _Splats material array(s) for seasonal ground.");
        }

        private int ScanMaterial(Material material, bool isDistantTerrain)
        {
            var added = 0;
            for (var propertyIndex = 0; propertyIndex < DiffuseProperties.Length; propertyIndex++)
            {
                var property = DiffuseProperties[propertyIndex];
                if (!material.HasProperty(property)) continue;
                var observed = material.GetTexture(property);
                if (!(observed is Texture2DArray)) continue;
                var key = material.GetInstanceID() + "|" + property;
                if (bindings.ContainsKey(key)) continue;
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
                bindings.Add(key, new Binding
                {
                    Material = material,
                    Property = property,
                    Original = original,
                    IsDistantTerrain = isDistantTerrain || property == "_Splats"
                });
                added++;
            }
            return added;
        }

        private static bool IsDistantTerrainMaterial(Material material)
        {
            return material != null && material.HasProperty("_Splats") && material.HasProperty("_Control") &&
                   material.GetTexture("_Splats") is Texture2DArray;
        }

        private void ApplySeason(SeasonKind season, bool distantTerrainEnabled)
        {
            Texture target = null;
            // The generated spring/autumn arrays recolor rock, ballast and soil as if
            // they were foliage. Keep the game's physically correct ground arrays for
            // all snow-free seasons; foliage has its own seasonal texture path.
            if (season == SeasonKind.Winter)
            {
                Texture2DArray seasonal;
                if (!TryGetTerrainArray(season, out seasonal))
                {
                    Debug.LogWarning("[DVSeasons] MicroSplat terrain array is missing for " + season + ".");
                    return;
                }
                target = seasonal;
            }

            foreach (var binding in bindings.Values)
                Apply(binding, binding.IsDistantTerrain && !distantTerrainEnabled
                    ? binding.Original
                    : target ?? binding.Original);
            Debug.Log("[DVSeasons] MicroSplat ground switched to " + season + " terrain textures (" +
                bindings.Count + " albedo binding(s)).");
        }

        private void ReapplyToNewBindings(SeasonKind season, bool distantTerrainEnabled)
        {
            Texture seasonal = null;
            if (season == SeasonKind.Winter)
            {
                Texture2DArray array;
                if (!TryGetTerrainArray(season, out array)) return;
                seasonal = array;
            }
            foreach (var binding in bindings.Values)
                if (binding.Applied == null) Apply(binding,
                    binding.IsDistantTerrain && !distantTerrainEnabled
                        ? binding.Original
                        : seasonal ?? binding.Original);
        }

        private static void Apply(Binding binding, Texture target)
        {
            if (binding.Material == null || target == null) return;
            binding.Material.SetTexture(binding.Property, target);
            binding.Applied = target;
        }

        private static bool IsSeasonalTerrainTexture(Texture texture)
        {
            var name = texture == null ? string.Empty : texture.name;
            return !string.IsNullOrEmpty(name) &&
                name.IndexOf("DVSeasons", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool TryGetTerrainArray(SeasonKind season, out Texture2DArray array)
        {
            array = null;
            Texture2DArray packed;
            if (!texturePack.TryGetTerrainArray(season, out packed)) return false;
            if (season != SeasonKind.Winter)
            {
                array = packed;
                return true;
            }

            if (!uniformWinterAttempted)
            {
                uniformWinterAttempted = true;
                try
                {
                    uniformWinterArray = new Texture2DArray(packed.width, packed.height, packed.depth,
                        packed.format, packed.mipmapCount > 1, false)
                    {
                        name = "DVSeasons Uniform Winter Terrain",
                        wrapMode = packed.wrapMode,
                        filterMode = packed.filterMode,
                        anisoLevel = packed.anisoLevel,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    for (var slice = 0; slice < packed.depth; slice++)
                    for (var mip = 0; mip < packed.mipmapCount; mip++)
                        uniformWinterArray.SetPixels(packed.GetPixels(0, mip), slice, mip);
                    uniformWinterArray.Apply(false, false);
                    Debug.Log("[DVSeasons] Built uniform winter terrain array from the snow albedo slice (" +
                        packed.depth + " slices).");
                }
                catch (Exception exception)
                {
                    if (uniformWinterArray != null) UnityEngine.Object.Destroy(uniformWinterArray);
                    uniformWinterArray = null;
                    Debug.LogWarning("[DVSeasons] Uniform winter terrain array could not be built: " +
                        exception.Message);
                }
            }

            array = uniformWinterArray ?? packed;
            return true;
        }

        private void Restore()
        {
            foreach (var binding in bindings.Values)
            {
                if (binding.Material == null) continue;
                var current = binding.Material.GetTexture(binding.Property);
                if (binding.Applied == null || current == binding.Applied)
                    binding.Material.SetTexture(binding.Property, binding.Original);
                binding.Applied = null;
            }
            lastSeasonKey = int.MinValue;
        }
    }
}
