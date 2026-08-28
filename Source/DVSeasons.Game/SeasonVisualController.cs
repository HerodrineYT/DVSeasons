using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class SeasonVisualController : IDisposable
    {
        private sealed class TerrainRecord
        {
            public Terrain Terrain;
            public Material OriginalMaterial;
            public Material SeasonalMaterial;
            public Color OriginalGrassTint;
            public float OriginalDetailObjectDensity;
            public float OriginalTreeBillboardDistance;
            public int OriginalTreeMaximumFullLodCount;
            public TreeInstance[] OriginalTreeInstances;
            public int LastTreeColorStep;
            public bool TreeColorsApplied;
        }

        private sealed class RainRecord
        {
            public ParticleSystem System;
            public float RateOverTimeMultiplier;
            public float RateOverDistanceMultiplier;
        }

        private readonly Dictionary<int, TerrainRecord> terrains = new Dictionary<int, TerrainRecord>();
        private readonly Dictionary<int, RainRecord> rainSystems = new Dictionary<int, RainRecord>();
        private readonly SeasonalTextureController seasonalTextures;
        private readonly MicroSplatSeasonalTerrainController microSplatTerrain;
        private GameObject snowObject;
        private ParticleSystem snowSystem;
        private Material snowMaterial;
        private Texture2D snowTexture;
        private float nextTerrainScan;
        private float nextRainScan;

        public SeasonVisualController(string modPath)
        {
            var texturePack = new SeasonAssetBundleRepository(modPath);
            seasonalTextures = new SeasonalTextureController(texturePack);
            microSplatTerrain = new MicroSplatSeasonalTerrainController(texturePack);
        }

        public void Apply(SeasonState state, float rainIntensity, SeasonModSettings settings)
        {
            if (state == null || settings == null) return;
            ScanTerrains();
            ScanRainSystems();
            seasonalTextures.Apply(state, settings);
            microSplatTerrain.Apply(state, settings);
            ApplyTerrains(state, settings);
            ApplyRainCrossfade(settings.ReplaceRainWithSnow ? state.SnowAmount : 0f);
            ApplySnowfall(state, rainIntensity, settings);
        }

        public void Dispose()
        {
            seasonalTextures.Dispose();
            microSplatTerrain.Dispose();
            foreach (var record in terrains.Values)
            {
                if (record.Terrain != null)
                {
                    record.Terrain.materialTemplate = record.OriginalMaterial;
                    record.Terrain.detailObjectDensity = record.OriginalDetailObjectDensity;
                    record.Terrain.treeBillboardDistance = record.OriginalTreeBillboardDistance;
                    record.Terrain.treeMaximumFullLODCount = record.OriginalTreeMaximumFullLodCount;
                    if (record.TreeColorsApplied && record.Terrain.terrainData != null &&
                        record.OriginalTreeInstances != null)
                        record.Terrain.terrainData.treeInstances = record.OriginalTreeInstances;
                    if (record.Terrain.terrainData != null) record.Terrain.terrainData.wavingGrassTint = record.OriginalGrassTint;
                }
                if (record.SeasonalMaterial != null) UnityEngine.Object.Destroy(record.SeasonalMaterial);
            }
            terrains.Clear();
            foreach (var record in rainSystems.Values) RestoreRain(record);
            rainSystems.Clear();
            if (snowObject != null) UnityEngine.Object.Destroy(snowObject);
            if (snowMaterial != null) UnityEngine.Object.Destroy(snowMaterial);
            if (snowTexture != null) UnityEngine.Object.Destroy(snowTexture);
            snowObject = null;
            snowSystem = null;
        }

        private void ScanTerrains()
        {
            if (Time.realtimeSinceStartup < nextTerrainScan) return;
            nextTerrainScan = Time.realtimeSinceStartup + 5f;
            var active = Terrain.activeTerrains;
            for (var i = 0; i < active.Length; i++)
            {
                var terrain = active[i];
                if (terrain == null || terrains.ContainsKey(terrain.GetInstanceID())) continue;
                var original = terrain.materialTemplate;
                var seasonal = original == null ? null : new Material(original) { name = original.name + " [DVSeasons]" };
                if (seasonal != null) terrain.materialTemplate = seasonal;
                terrains.Add(terrain.GetInstanceID(), new TerrainRecord
                {
                    Terrain = terrain,
                    OriginalMaterial = original,
                    SeasonalMaterial = seasonal,
                    OriginalGrassTint = terrain.terrainData == null ? Color.white : terrain.terrainData.wavingGrassTint,
                    OriginalDetailObjectDensity = terrain.detailObjectDensity,
                    OriginalTreeBillboardDistance = terrain.treeBillboardDistance,
                    OriginalTreeMaximumFullLodCount = terrain.treeMaximumFullLODCount,
                    OriginalTreeInstances = terrain.terrainData == null ? null : terrain.terrainData.treeInstances,
                    LastTreeColorStep = -1
                });
            }
        }

        private void ScanRainSystems()
        {
            if (Time.realtimeSinceStartup < nextRainScan) return;
            nextRainScan = Time.realtimeSinceStartup + 3f;
            var systems = UnityEngine.Object.FindObjectsOfType<ParticleSystem>();
            for (var i = 0; i < systems.Length; i++)
            {
                var system = systems[i];
                if (system == null || system == snowSystem || system.name.IndexOf("rain", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var id = system.GetInstanceID();
                if (rainSystems.ContainsKey(id)) continue;
                var emission = system.emission;
                rainSystems.Add(id, new RainRecord
                {
                    System = system,
                    RateOverTimeMultiplier = emission.rateOverTimeMultiplier,
                    RateOverDistanceMultiplier = emission.rateOverDistanceMultiplier
                });
            }
        }

        private void ApplyTerrains(SeasonState state, SeasonModSettings settings)
        {
            var seasonalTint = GetSeasonTint(state);
            var tintStrength = Mathf.Clamp01(settings.FoliageTintStrength);
            var snow = settings.GroundSnowEnabled ? Mathf.Clamp01(state.SnowAmount * settings.GroundSnowStrength) : 0f;
            foreach (var record in terrains.Values)
            {
                if (record.Terrain == null) continue;
                if (record.Terrain.terrainData != null)
                    record.Terrain.terrainData.wavingGrassTint = Color.Lerp(record.OriginalGrassTint,
                        Multiply(record.OriginalGrassTint, seasonalTint), tintStrength);
                var winterClearing = settings.SeasonalTexturesEnabled && settings.VegetationTextureChanges
                    ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(state.SnowAmount * settings.TextureChangeStrength))
                    : 0f;
                record.Terrain.detailObjectDensity = Mathf.Lerp(record.OriginalDetailObjectDensity,
                    record.OriginalDetailObjectDensity * 0.08f, winterClearing);
                var leaflessDistanceWeight = settings.LeaflessDistantTrees ? winterClearing : 0f;
                record.Terrain.treeBillboardDistance = Mathf.Lerp(record.OriginalTreeBillboardDistance,
                    Mathf.Max(record.OriginalTreeBillboardDistance, settings.WinterFullTreeDistance),
                    leaflessDistanceWeight);
                record.Terrain.treeMaximumFullLODCount = Mathf.RoundToInt(Mathf.Lerp(
                    record.OriginalTreeMaximumFullLodCount,
                    Mathf.Max(record.OriginalTreeMaximumFullLodCount, settings.WinterFullTreeCount),
                    leaflessDistanceWeight));
                ApplyTreeColors(record, winterClearing);
                ApplySnowProperties(record.SeasonalMaterial, record.OriginalMaterial, snow);
            }
        }

        private static void ApplyTreeColors(TerrainRecord record, float winterWeight)
        {
            var data = record.Terrain == null ? null : record.Terrain.terrainData;
            var originals = record.OriginalTreeInstances;
            if (data == null || originals == null || originals.Length == 0) return;
            var step = Mathf.RoundToInt(Mathf.Clamp01(winterWeight) * 16f);
            if (record.LastTreeColorStep == step) return;
            record.LastTreeColorStep = step;
            var blend = step / 16f;
            var prototypes = data.treePrototypes;
            var output = new TreeInstance[originals.Length];
            for (var i = 0; i < originals.Length; i++)
            {
                var instance = originals[i];
                var prototypeName = string.Empty;
                if (instance.prototypeIndex >= 0 && instance.prototypeIndex < prototypes.Length &&
                    prototypes[instance.prototypeIndex] != null &&
                    prototypes[instance.prototypeIndex].prefab != null)
                    prototypeName = prototypes[instance.prototypeIndex].prefab.name;
                var evergreen = ContainsAny(prototypeName, "fir", "pine", "spruce", "conifer", "cedar");
                var baseColor = (Color)instance.color;
                var baseLightmap = (Color)instance.lightmapColor;
                var luminance = baseColor.r * 0.30f + baseColor.g * 0.59f + baseColor.b * 0.11f;
                var target = evergreen
                    ? new Color(baseColor.r * 0.68f + 0.08f, baseColor.g * 0.76f + 0.10f,
                        baseColor.b * 0.82f + 0.16f, baseColor.a)
                    : new Color(luminance * 0.58f + 0.24f, luminance * 0.62f + 0.27f,
                        luminance * 0.68f + 0.32f, baseColor.a);
                var lightTarget = new Color(target.r, target.g, target.b, target.a);
                instance.color = Color.Lerp(baseColor, target, blend);
                instance.lightmapColor = Color.Lerp(baseLightmap, lightTarget, blend);
                output[i] = instance;
            }
            data.treeInstances = output;
            record.TreeColorsApplied = step > 0;
        }

        private static bool ContainsAny(string value, params string[] fragments)
        {
            if (string.IsNullOrEmpty(value)) return false;
            for (var i = 0; i < fragments.Length; i++)
                if (value.IndexOf(fragments[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static void ApplySnowProperties(Material material, Material original, float snow)
        {
            if (material == null) return;
            // The bundled MicroSplat diffuse array already contains the seasonal ground.
            // Enabling MicroSplat's procedural snow at the same time creates isolated
            // slope/noise patches on top of that array, which looks like square chunks.
            if (material.HasProperty("_Diffuse") && material.GetTexture("_Diffuse") is Texture2DArray) return;
            SetFloat(material, original, "_Snow_Amount", snow);
            SetFloat(material, original, "_SnowAmount", snow);
            SetFloat(material, original, "_SnowBlendFactor", snow);
            SetFloat(material, original, "_UseSnow", snow > 0.001f ? 1f : 0f);
            if (material.HasProperty("_SnowColor")) material.SetColor("_SnowColor", Color.Lerp(new Color(0.82f, 0.88f, 0.94f), Color.white, snow));
            if (material.HasProperty("_SnowAlbedoColor")) material.SetColor("_SnowAlbedoColor", Color.Lerp(new Color(0.82f, 0.88f, 0.94f), Color.white, snow));
            if (snow > 0.001f) material.EnableKeyword("_USESNOW_ON");
            else material.DisableKeyword("_USESNOW_ON");
        }

        private static void SetFloat(Material material, Material original, string property, float snow)
        {
            if (!material.HasProperty(property)) return;
            var baseline = original != null && original.HasProperty(property) ? original.GetFloat(property) : 0f;
            material.SetFloat(property, Mathf.Max(baseline, snow));
        }

        private void ApplyRainCrossfade(float snowAmount)
        {
            var rainFactor = 1f - Mathf.Clamp01(snowAmount);
            foreach (var record in rainSystems.Values)
            {
                if (record.System == null) continue;
                var emission = record.System.emission;
                emission.rateOverTimeMultiplier = record.RateOverTimeMultiplier * rainFactor;
                emission.rateOverDistanceMultiplier = record.RateOverDistanceMultiplier * rainFactor;
            }
        }

        private static void RestoreRain(RainRecord record)
        {
            if (record.System == null) return;
            var emission = record.System.emission;
            emission.rateOverTimeMultiplier = record.RateOverTimeMultiplier;
            emission.rateOverDistanceMultiplier = record.RateOverDistanceMultiplier;
        }

        private void ApplySnowfall(SeasonState state, float rainIntensity, SeasonModSettings settings)
        {
            var precipitation = Mathf.Max(rainIntensity, settings.AmbientWinterSnowfall * state.SnowAmount);
            var intensity = settings.SnowParticlesEnabled ? state.SnowAmount * precipitation * settings.SnowfallDensity : 0f;
            if (intensity <= 0.0001f)
            {
                if (snowSystem != null)
                {
                    var off = snowSystem.emission;
                    off.rateOverTimeMultiplier = 0f;
                }
                return;
            }
            EnsureSnowSystem();
            var camera = Camera.main;
            if (camera != null) snowObject.transform.position = camera.transform.position + (Vector3.up * 12f);
            var emission = snowSystem.emission;
            emission.rateOverTimeMultiplier = 1800f * intensity;
            if (!snowSystem.isPlaying) snowSystem.Play();
        }

        private void EnsureSnowSystem()
        {
            if (snowSystem != null) return;
            snowObject = new GameObject("DVSeasons Snowfall") { hideFlags = HideFlags.HideAndDontSave };
            snowSystem = snowObject.AddComponent<ParticleSystem>();
            var main = snowSystem.main;
            main.loop = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.startLifetime = new ParticleSystem.MinMaxCurve(7f, 11f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2.5f, 5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.13f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.92f, 0.96f, 1f, 0.9f), Color.white);
            main.gravityModifier = 0.04f;
            main.maxParticles = 7000;
            var shape = snowSystem.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(55f, 5f, 55f);
            var velocity = snowSystem.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(-0.35f, 0.35f);
            velocity.y = new ParticleSystem.MinMaxCurve(-0.5f, -0.1f);
            velocity.z = new ParticleSystem.MinMaxCurve(-0.35f, 0.35f);
            var noise = snowSystem.noise;
            noise.enabled = true;
            noise.strength = 0.55f;
            noise.frequency = 0.22f;
            noise.scrollSpeed = 0.18f;
            var renderer = snowObject.GetComponent<ParticleSystemRenderer>();
            var shader = Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (shader != null)
            {
                snowTexture = CreateSnowTexture();
                snowMaterial = new Material(shader) { name = "DVSeasons Snow Material", mainTexture = snowTexture };
                renderer.material = snowMaterial;
            }
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
        }

        private static Texture2D CreateSnowTexture()
        {
            const int size = 32;
            var texture = new Texture2D(size, size, TextureFormat.ARGB32, false) { name = "DVSeasons Snowflake" };
            var pixels = new Color[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = ((x + 0.5f) / size * 2f) - 1f;
                var dy = ((y + 0.5f) / size * 2f) - 1f;
                var alpha = Mathf.Clamp01(1f - Mathf.Sqrt((dx * dx) + (dy * dy)));
                alpha *= alpha;
                pixels[(y * size) + x] = new Color(1f, 1f, 1f, alpha);
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Color GetSeasonTint(SeasonState state)
        {
            return Color.Lerp(ProfileTint(state.Current), ProfileTint(state.Next), state.Transition);
        }

        private static Color ProfileTint(SeasonKind season)
        {
            switch (season)
            {
                case SeasonKind.Spring: return new Color(0.78f, 1.08f, 0.76f);
                case SeasonKind.Autumn: return new Color(1.18f, 0.58f, 0.24f);
                case SeasonKind.Winter: return new Color(1.2f, 1.25f, 1.32f);
                default: return Color.white;
            }
        }

        private static Color Multiply(Color a, Color b)
        {
            return new Color(a.r * b.r, a.g * b.g, a.b * b.b, a.a);
        }
    }
}
