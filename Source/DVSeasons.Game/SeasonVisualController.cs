using System;
using System.Collections.Generic;
using System.IO;
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
            public float OriginalDetailObjectDistance;
            public float OriginalTreeDistance;
            public float OriginalTreeBillboardDistance;
            public int OriginalTreeMaximumFullLodCount;
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
        private readonly WaterIceController waterIce;
        private readonly WinterPuddleController winterPuddles;
        private readonly ProceduralSnowController proceduralSurfaceSnow;
        private readonly RailSnowGameSource railSnowSource = new RailSnowGameSource();
        private readonly LocomotiveSnowHeatController locomotiveHeat = new LocomotiveSnowHeatController();
        private readonly TenderCoalSnowController tenderCoal;
        private readonly TrainSnowTrailController trainSnowTrail = new TrainSnowTrailController();
        private readonly SnowFootstepAudioController snowFootsteps;
        private DV.TerrainSystem.TerrainGrid snowTerrainGrid;
        private readonly SeasonAssetBundleRepository texturePack;
        private readonly string modPath;
        private GameObject snowObject;
        private ParticleSystem snowSystem;
        private ParticleSystem.Particle[] snowParticleBuffer;
        private Material snowMaterial;
        private Texture2D snowTexture;
        private float nextTerrainScan;
        private float nextRainScan;
        private float nextSnowShelterCheck;
        private float nextCabSnowClear;
        private bool snowSheltered;
        private bool cameraInsideCab;
        private bool startupConfigured;
        private int startupStage;
        private float startupNotBefore;
        private bool dynamicSnowEnabled;

        public SeasonVisualController(string modPath)
        {
            this.modPath = modPath ?? string.Empty;
            snowFootsteps = new SnowFootstepAudioController(this.modPath);
            texturePack = new SeasonAssetBundleRepository(modPath);
            tenderCoal=new TenderCoalSnowController(texturePack);
            seasonalTextures = new SeasonalTextureController(texturePack);
            microSplatTerrain = new MicroSplatSeasonalTerrainController(texturePack);
            waterIce = new WaterIceController(texturePack);
            winterPuddles = new WinterPuddleController(texturePack);
            proceduralSurfaceSnow = new ProceduralSnowController(texturePack);
            proceduralSurfaceSnow.SetVehicleDiscovery(() => RailSnowGameSource.GetCars());
            proceduralSurfaceSnow.SetVehicleSnowRemaining(locomotiveHeat.Remaining);
            snowFootsteps.SetVehicleSnowRemaining(locomotiveHeat.RemainingAt);
            proceduralSurfaceSnow.SetNativeVehicleSnow(tenderCoal.HasSnowTexture);
            proceduralSurfaceSnow.BeforeSnowRender = () =>
            {
                proceduralSurfaceSnow.SetWorldOffset(DV.OriginShift.OriginShift.currentMove);
                railSnowSource.Update(proceduralSurfaceSnow.RailTracks);
            };
        }

        public void BeginSession(bool multiplayerSession)
        {
            startupConfigured = true;
            startupStage = 0;
            startupNotBefore = Time.realtimeSinceStartup + (multiplayerSession ? 4f : 0.75f);
            Debug.Log("[DVSeasons] Seasonal visuals deferred for " +
                (multiplayerSession ? "4.00" : "0.75") +
                " s while the world finishes initialization.");
        }

        public void Apply(SeasonState state, float precipitationSnowAmount, float rainIntensity, Vector3 windVelocity,
            float snowLightFactor, SeasonModSettings settings)
        {
            if (state == null || settings == null) return;
            SnowPerformance.Frame();
            SetSnowMode(settings.ProceduralSnowEnabled);
            snowFootsteps.SetCoverage(settings.GroundSnowEnabled
                ? state.SnowAmount*settings.GroundSnowStrength*settings.TextureChangeStrength : 0f,
                settings.GroundSnowEnabled);
            if (!PrepareStartup(state, settings)) return;
            var grid=dynamicSnowEnabled ? DV.TerrainSystem.TerrainGrid.Instance : null;
            if(grid!=snowTerrainGrid)
            {
                if(snowTerrainGrid!=null) snowTerrainGrid.TerrainsMoved-=proceduralSurfaceSnow.InvalidateGeometry;
                snowTerrainGrid=grid;
                if(snowTerrainGrid!=null) snowTerrainGrid.TerrainsMoved+=proceduralSurfaceSnow.InvalidateGeometry;
                proceduralSurfaceSnow.InvalidateGeometry();
            }
            using(SnowPerformance.Measure("world-discovery")) {ScanTerrains();ScanRainSystems();}
            if (dynamicSnowEnabled)
            {
                proceduralSurfaceSnow.SetWorldOffset(DV.OriginShift.OriginShift.currentMove);
                proceduralSurfaceSnow.SetWeather(settings.ReplaceRainWithSnow ? precipitationSnowAmount * rainIntensity : 0f);
                locomotiveHeat.Update(settings.ReplaceRainWithSnow?precipitationSnowAmount*rainIntensity:0f,
                    settings.GroundSnowEnabled?state.SnowAmount:0f,Time.deltaTime);
                using(SnowPerformance.Measure("snow-prepare")) proceduralSurfaceSnow.Apply(settings.GroundSnowEnabled
                    ? state.SnowAmount * settings.GroundSnowStrength * settings.TextureChangeStrength : 0f, true);
            }
            using(SnowPerformance.Measure("seasonal-textures")) seasonalTextures.Apply(state, settings, proceduralSurfaceSnow.IsActive,
                proceduralSurfaceSnow.IsActive ? (float?)proceduralSurfaceSnow.Coverage : null);
            using(SnowPerformance.Measure("terrain-materials")) microSplatTerrain.Apply(state, settings, proceduralSurfaceSnow.IsActive,
                proceduralSurfaceSnow.IsActive ? (float?)proceduralSurfaceSnow.Coverage : null);
            using(SnowPerformance.Measure("terrain-properties")) ApplyTerrains(state, settings);
            using(SnowPerformance.Measure("tender-coal")) tenderCoal.Apply(settings.GroundSnowEnabled && settings.SeasonalTexturesEnabled && settings.TerrainTextureChanges
                ? (proceduralSurfaceSnow.IsActive?proceduralSurfaceSnow.Coverage:state.SnowAmount*settings.GroundSnowStrength*settings.TextureChangeStrength):0f,
                dynamicSnowEnabled ? (Func<Component,float>)locomotiveHeat.Remaining : null);
            ApplyRainCrossfade(settings.ReplaceRainWithSnow ? precipitationSnowAmount : 0f);
            using(SnowPerformance.Measure("particles")) ApplySnowfall(precipitationSnowAmount, rainIntensity, windVelocity, snowLightFactor, settings);

            // Reuse the saved/networked SnowAmount for water and puddles. This keeps
            // both ice systems in the exact same phase as terrain on reconnect and
            // after loading a save midway through a thaw.
            var winterCoverage = settings.GroundSnowEnabled
                ? Mathf.Clamp01(state.SnowAmount * settings.GroundSnowStrength *
                    settings.TextureChangeStrength)
                : 0f;
            if (dynamicSnowEnabled) using(SnowPerformance.Measure("train-snow-trail"))
                trainSnowTrail.Apply(winterCoverage,
                    settings.GroundSnowEnabled && settings.SnowParticlesEnabled,
                    snowLightFactor,windVelocity);
            using(SnowPerformance.Measure("water")) waterIce.Apply(settings.WinterWaterIceEnabled ? winterCoverage : 0f, snowLightFactor);
            using(SnowPerformance.Measure("puddles")) winterPuddles.Apply(winterCoverage, settings.FreezeWinterPuddles);
        }

        private void SetSnowMode(bool enabled)
        {
            if (dynamicSnowEnabled == enabled) return;
            dynamicSnowEnabled = enabled;
            if (enabled) return;
            proceduralSurfaceSnow.Apply(0f, false);
            railSnowSource.Reset();
            locomotiveHeat.Reset(); // Footsteps must see the seasonal snow again.
            trainSnowTrail.Dispose();
            if (snowTerrainGrid != null)
                snowTerrainGrid.TerrainsMoved -= proceduralSurfaceSnow.InvalidateGeometry;
            snowTerrainGrid = null;
        }

        private bool PrepareStartup(SeasonState state, SeasonModSettings settings)
        {
            if (!startupConfigured) BeginSession(false);
            if (startupStage >= 4) return true;
            if (Time.realtimeSinceStartup < startupNotBefore) return false;
            if (startupStage == 0)
            {
                texturePack.BeginLoad();
                startupStage = 1;
                return false;
            }
            if (startupStage == 1)
            {
                if (!texturePack.IsLoadFinished) return false;
                startupStage = 2;
                Debug.Log("[DVSeasons] Seasonal AssetBundle ready; material discovery is being staged across frames.");
                return false;
            }
            if (startupStage == 2)
            {
                using (SnowPerformance.Measure("startup-seasonal-textures"))
                    seasonalTextures.Apply(state, settings, false, null);
                startupStage = 3;
                return false;
            }
            using (SnowPerformance.Measure("startup-terrain-materials"))
                microSplatTerrain.Apply(state, settings, false, null);
            startupStage = 4;
            Debug.Log("[DVSeasons] Staged seasonal visual initialization complete.");
            return false;
        }

        public void Dispose()
        {
            ResetForSession();
            waterIce.Dispose();
            winterPuddles.Dispose();
            snowFootsteps.Dispose();
            texturePack.Dispose();
        }

        public void ResetForSession()
        {
            SnowPerformance.Reset();
            seasonalTextures.Dispose();
            microSplatTerrain.Dispose();
            waterIce.ResetForSession();
            winterPuddles.ResetForSession();
            proceduralSurfaceSnow.Dispose(); railSnowSource.Reset();
            dynamicSnowEnabled = false;
            tenderCoal.Dispose();locomotiveHeat.Reset();
            trainSnowTrail.Dispose();
            snowFootsteps.SetCoverage(0f,false);
            if(snowTerrainGrid!=null) snowTerrainGrid.TerrainsMoved-=proceduralSurfaceSnow.InvalidateGeometry;
            snowTerrainGrid=null;
            foreach (var record in terrains.Values)
            {
                if (record.Terrain != null)
                {
                    record.Terrain.materialTemplate = record.OriginalMaterial;
                    record.Terrain.detailObjectDensity = record.OriginalDetailObjectDensity;
                    record.Terrain.detailObjectDistance = record.OriginalDetailObjectDistance;
                    record.Terrain.treeDistance = record.OriginalTreeDistance;
                    record.Terrain.treeBillboardDistance = record.OriginalTreeBillboardDistance;
                    record.Terrain.treeMaximumFullLODCount = record.OriginalTreeMaximumFullLodCount;
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
            snowParticleBuffer = null;
            snowMaterial = null;
            snowTexture = null;
            nextTerrainScan = 0f;
            nextRainScan = 0f;
            nextSnowShelterCheck = 0f;
            nextCabSnowClear = 0f;
            snowSheltered = false;
            cameraInsideCab = false;
            startupConfigured = false;
            startupStage = 0;
            startupNotBefore = 0f;
            texturePack.ResetForSession();
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
                    OriginalDetailObjectDistance = terrain.detailObjectDistance,
                    OriginalTreeDistance = terrain.treeDistance,
                    OriginalTreeBillboardDistance = terrain.treeBillboardDistance,
                    OriginalTreeMaximumFullLodCount = terrain.treeMaximumFullLODCount
                });
            }
        }

        private void ScanRainSystems()
        {
            if (Time.realtimeSinceStartup < nextRainScan) return;
            nextRainScan = Time.realtimeSinceStartup + 15f;
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
            // Terrain and vegetation setters can make Unity rebuild large native
            // buffers. Quantizing the visual state prevents those rebuilds on every
            // frame while keeping 32 visually smooth steps over several game days.
            var seasonalTint = GetSeasonTint(state, 32);
            var tintStrength = Mathf.Clamp01(settings.FoliageTintStrength);
            var snowCoverage = settings.GroundSnowEnabled && !proceduralSurfaceSnow.IsActive
                ? Mathf.Clamp01(state.SnowAmount * settings.GroundSnowStrength * settings.TextureChangeStrength)
                : 0f;
            var proceduralSnow = Quantize01(SnowCoverProfile.GetProceduralAmount(snowCoverage), 32);
            foreach (var record in terrains.Values)
            {
                if (record.Terrain == null) continue;
                if (record.Terrain.terrainData != null)
                {
                    var grassTint = Color.Lerp(record.OriginalGrassTint,
                        Multiply(record.OriginalGrassTint, seasonalTint), tintStrength);
                    if (!Approximately(record.Terrain.terrainData.wavingGrassTint, grassTint))
                        record.Terrain.terrainData.wavingGrassTint = grassTint;
                }
                var thawing = state.Current == SeasonKind.Winter && state.Next == SeasonKind.Spring;
                var vegetationSnow = Mathf.Clamp01(state.SnowAmount * settings.TextureChangeStrength);
                var winterClearing = settings.SeasonalTexturesEnabled && settings.VegetationTextureChanges
                    ? SnowCoverProfile.GetVegetationWinterWeight(vegetationSnow, thawing)
                    : 0f;
                winterClearing = Quantize01(winterClearing, 16);
                var detailDensity = Mathf.Lerp(record.OriginalDetailObjectDensity,
                    record.OriginalDetailObjectDensity * 0.08f, winterClearing);
                if (Mathf.Abs(record.Terrain.detailObjectDensity - detailDensity) > 0.001f)
                    record.Terrain.detailObjectDensity = detailDensity;
                var leaflessDistanceWeight = settings.LeaflessDistantTrees && !settings.NativeWinterVegetationLod ? winterClearing : 0f;
                var detailDistance = Mathf.Lerp(record.OriginalDetailObjectDistance,
                    Mathf.Max(record.OriginalDetailObjectDistance,
                        Mathf.Min(500f, record.OriginalDetailObjectDistance *
                            settings.WinterDetailDistanceMultiplier)), leaflessDistanceWeight);
                if (Mathf.Abs(record.Terrain.detailObjectDistance - detailDistance) > 0.01f)
                    record.Terrain.detailObjectDistance = detailDistance;
                // Keep exactly the game's summer tree range and instance placement.
                // Winter changes only the 3D-to-billboard switch, so every distant
                // summer tree has a leafless silhouette at the same coordinates.
                var treeDistance = record.OriginalTreeDistance;
                if (Mathf.Abs(record.Terrain.treeDistance - treeDistance) > 0.01f)
                    record.Terrain.treeDistance = treeDistance;
                // Keep the real leafless tree mesh through the middle distance. This
                // gives the player the original trunk and branch geometry instead of
                // a low-resolution crown-shaped billboard around lakes and valleys.
                var billboardDistance = Mathf.Lerp(record.OriginalTreeBillboardDistance,
                    Mathf.Max(record.OriginalTreeBillboardDistance,
                        settings.WinterFullTreeDistance),
                    leaflessDistanceWeight);
                if (Mathf.Abs(record.Terrain.treeBillboardDistance - billboardDistance) > 0.01f)
                    record.Terrain.treeBillboardDistance = billboardDistance;
                var fullTreeCount = Mathf.RoundToInt(Mathf.Lerp(
                    record.OriginalTreeMaximumFullLodCount,
                    Mathf.Max(record.OriginalTreeMaximumFullLodCount,
                        settings.WinterFullTreeCount),
                    leaflessDistanceWeight));
                if (record.Terrain.treeMaximumFullLODCount != fullTreeCount)
                    record.Terrain.treeMaximumFullLODCount = fullTreeCount;
                ApplySnowProperties(record.SeasonalMaterial, record.OriginalMaterial, proceduralSnow);
            }
        }

        private static void ApplySnowProperties(Material material, Material original, float snow)
        {
            if (material == null) return;
            // The smooth DVSeasons array already contains its own irregular snow mask.
            // Reset optional shader snow to avoid stacking a second effect over it.
            if (UsesBakedWinterArray(material)) snow = 0f;
            SetFloat(material, original, "_Snow_Amount", snow);
            SetFloat(material, original, "_SnowAmount", snow);
            SetFloat(material, original, "_SnowBlendFactor", snow);
            SetFloat(material, original, "_UseSnow", snow > 0.001f ? 1f : 0f);
            if (material.HasProperty("_SnowColor")) material.SetColor("_SnowColor", Color.Lerp(new Color(0.82f, 0.88f, 0.94f), Color.white, snow));
            if (material.HasProperty("_SnowAlbedoColor")) material.SetColor("_SnowAlbedoColor", Color.Lerp(new Color(0.82f, 0.88f, 0.94f), Color.white, snow));
            if (snow > 0.001f) material.EnableKeyword("_USESNOW_ON");
            else material.DisableKeyword("_USESNOW_ON");
        }

        private static bool UsesBakedWinterArray(Material material)
        {
            if (!material.HasProperty("_Diffuse")) return false;
            var texture = material.GetTexture("_Diffuse") as Texture2DArray;
            return texture != null && !string.IsNullOrEmpty(texture.name) &&
                texture.name.IndexOf("DVSeasons", StringComparison.OrdinalIgnoreCase) >= 0 &&
                texture.name.IndexOf("Terrain", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void SetFloat(Material material, Material original, string property, float snow)
        {
            if (!material.HasProperty(property)) return;
            var baseline = original != null && original.HasProperty(property) ? original.GetFloat(property) : 0f;
            var value = Mathf.Max(baseline, snow);
            if (Mathf.Abs(material.GetFloat(property) - value) > 0.001f)
                material.SetFloat(property, value);
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

        private void ApplySnowfall(float precipitationSnowAmount, float rainIntensity, Vector3 windVelocity,
            float snowLightFactor, SeasonModSettings settings)
        {
            var visibility = SnowCoverProfile.GetSnowfallVisibility(precipitationSnowAmount);
            // Snow is emitted only when the native game weather reports rain. The
            // old ambient minimum could create snowfall in otherwise dry weather.
            var precipitation = rainIntensity;
            var intensity = settings.SnowParticlesEnabled
                ? visibility * precipitation * settings.SnowfallDensity
                : 0f;
            if (intensity <= 0.0001f)
            {
                if (snowSystem != null)
                {
                    var off = snowSystem.emission;
                    off.rateOverTimeMultiplier = 0f;
                    if (snowSystem.isPlaying)
                        snowSystem.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                return;
            }
            EnsureSnowSystem();
            var camera = Camera.main;
            if (camera == null) return;
            ApplySnowWind(windVelocity);
            ApplySnowLighting(snowLightFactor);
            UpdateSnowShelter(camera);
            if (snowSheltered)
            {
                var shelteredEmission = snowSystem.emission;
                shelteredEmission.rateOverTimeMultiplier = 0f;
                if (snowSystem.isPlaying)
                    snowSystem.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                return;
            }
            PositionSnowEmitter(camera, windVelocity);
            var emission = snowSystem.emission;
            emission.rateOverTimeMultiplier = 1600f * intensity;
            if (!snowSystem.isPlaying) snowSystem.Play();
        }

        private void UpdateSnowShelter(Camera camera)
        {
            if (camera == null || Time.realtimeSinceStartup < nextSnowShelterCheck) return;
            nextSnowShelterCheck = Time.realtimeSinceStartup + 0.20f;
            var forward = HorizontalForward(camera.transform);
            cameraInsideCab = HasRoof(camera.transform.position, 3.25f);
            var firstProbe = camera.transform.position + (forward * 9f) + (Vector3.up * 0.4f);
            var secondProbe = camera.transform.position + (forward * 22f) + (Vector3.up * 0.4f);
            snowSheltered = HasRoof(firstProbe, 12f) && HasRoof(secondProbe, 16f);
        }

        private void PositionSnowEmitter(Camera camera, Vector3 windVelocity)
        {
            var forward = HorizontalForward(camera.transform);
            var forwardOffset = cameraInsideCab ? 24f : 12f;
            // Spawn upwind so strong crosswinds still carry flakes through the
            // visible volume instead of immediately blowing them away from camera.
            var upwindOffset = -Vector3.ClampMagnitude(windVelocity * 2.4f, 26f);
            snowObject.transform.position = camera.transform.position + (forward * forwardOffset) +
                upwindOffset + (Vector3.up * 8f);
            snowObject.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            var shape = snowSystem.shape;
            shape.scale = cameraInsideCab
                ? new Vector3(70f, 18f, 34f)
                : new Vector3(80f, 18f, 60f);
            if (cameraInsideCab && Time.realtimeSinceStartup >= nextCabSnowClear)
            {
                nextCabSnowClear = Time.realtimeSinceStartup + 0.30f;
                ClearSnowNearCamera(camera.transform.position, 4.5f);
            }
        }

        private void ApplySnowWind(Vector3 windVelocity)
        {
            if (snowSystem == null) return;
            var velocity = snowSystem.velocityOverLifetime;
            velocity.x = new ParticleSystem.MinMaxCurve(windVelocity.x - 0.35f,
                windVelocity.x + 0.35f);
            velocity.y = new ParticleSystem.MinMaxCurve(-4.5f, -2.2f);
            velocity.z = new ParticleSystem.MinMaxCurve(windVelocity.z - 0.35f,
                windVelocity.z + 0.35f);

            var windStrength = Mathf.Clamp01(windVelocity.magnitude / 12f);
            var noise = snowSystem.noise;
            noise.strength = Mathf.Lerp(0.45f, 1.1f, windStrength);
            noise.frequency = Mathf.Lerp(0.18f, 0.34f, windStrength);
            noise.scrollSpeed = Mathf.Lerp(0.14f, 0.55f, windStrength);
        }

        private void ApplySnowLighting(float lightFactor)
        {
            if (snowMaterial == null) return;
            lightFactor = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(lightFactor));
            var brightness = Mathf.Lerp(0.20f, 1f, lightFactor);
            var tint = new Color(brightness * 0.86f, brightness * 0.92f,
                brightness, 1f);
            if (snowMaterial.HasProperty("_TintColor")) snowMaterial.SetColor("_TintColor", tint);
            if (snowMaterial.HasProperty("_Color")) snowMaterial.SetColor("_Color", tint);
        }

        private void ClearSnowNearCamera(Vector3 cameraPosition, float radius)
        {
            var particleCount = snowSystem == null ? 0 : snowSystem.particleCount;
            if (particleCount <= 0) return;
            if (snowParticleBuffer == null || snowParticleBuffer.Length < particleCount)
                snowParticleBuffer = new ParticleSystem.Particle[Mathf.NextPowerOfTwo(particleCount)];
            var count = snowSystem.GetParticles(snowParticleBuffer);
            var radiusSquared = radius * radius;
            var changed = false;
            for (var i = 0; i < count; i++)
            {
                if ((snowParticleBuffer[i].position - cameraPosition).sqrMagnitude > radiusSquared) continue;
                snowParticleBuffer[i].remainingLifetime = 0f;
                changed = true;
            }
            if (changed) snowSystem.SetParticles(snowParticleBuffer, count);
        }

        private static Vector3 HorizontalForward(Transform cameraTransform)
        {
            var forward = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up);
            return forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
        }

        private static bool HasRoof(Vector3 origin, float distance)
        {
            RaycastHit hit;
            return Physics.Raycast(origin, Vector3.up, out hit, distance,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        }

        private void EnsureSnowSystem()
        {
            if (snowSystem != null) return;
            snowObject = new GameObject("DVSeasons Snowfall") { hideFlags = HideFlags.HideAndDontSave };
            snowSystem = snowObject.AddComponent<ParticleSystem>();
            var main = snowSystem.main;
            main.loop = true;
            main.prewarm = true;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            main.startLifetime = new ParticleSystem.MinMaxCurve(4f, 6.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.17f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.94f, 0.97f, 1f, 0.95f), Color.white);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.gravityModifier = 0.04f;
            main.maxParticles = 7500;
            var shape = snowSystem.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(80f, 18f, 80f);
            var velocity = snowSystem.velocityOverLifetime;
            velocity.enabled = true;
            velocity.space = ParticleSystemSimulationSpace.World;
            velocity.x = new ParticleSystem.MinMaxCurve(-0.35f, 0.35f);
            velocity.y = new ParticleSystem.MinMaxCurve(-4.5f, -2.2f);
            velocity.z = new ParticleSystem.MinMaxCurve(-0.35f, 0.35f);
            var noise = snowSystem.noise;
            noise.enabled = true;
            noise.quality = ParticleSystemNoiseQuality.Low;
            noise.strength = 0.55f;
            noise.frequency = 0.22f;
            noise.scrollSpeed = 0.18f;
            var rotation = snowSystem.rotationOverLifetime;
            rotation.enabled = true;
            rotation.z = new ParticleSystem.MinMaxCurve(-0.55f, 0.55f);
            // Per-particle world collision was the largest continuous CPU cost. The
            // flakes already live only a few seconds in a camera-relative volume, so
            // collision adds little visually while testing thousands of particles
            // against terrain and trains every frame.
            var collision = snowSystem.collision;
            collision.enabled = false;
            var renderer = snowObject.GetComponent<ParticleSystemRenderer>();
            var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended") ??
                Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                var usesAtlas = false;
                snowTexture = LoadOrCreateSnowTexture(out usesAtlas);
                snowMaterial = new Material(shader) { name = "DVSeasons Snow Material", mainTexture = snowTexture };
                if (snowMaterial.HasProperty("_TintColor")) snowMaterial.SetColor("_TintColor", Color.white);
                if (snowMaterial.HasProperty("_Color")) snowMaterial.SetColor("_Color", Color.white);
                renderer.material = snowMaterial;
                if (usesAtlas)
                {
                    var textureSheet = snowSystem.textureSheetAnimation;
                    textureSheet.enabled = true;
                    textureSheet.mode = ParticleSystemAnimationMode.Grid;
                    textureSheet.numTilesX = 4;
                    textureSheet.numTilesY = 4;
                    textureSheet.animation = ParticleSystemAnimationType.WholeSheet;
                    textureSheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f);
                    textureSheet.startFrame = new ParticleSystem.MinMaxCurve(0f, 0.999f);
                    textureSheet.cycleCount = 1;
                }
            }
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.cameraVelocityScale = 0.12f;
        }

        private Texture2D LoadOrCreateSnowTexture(out bool usesAtlas)
        {
            var atlasPath = Path.Combine(modPath, "Textures", "snowflake_variations.png");
            var path = File.Exists(atlasPath)
                ? atlasPath
                : Path.Combine(modPath, "Textures", "snowflake_realistic.png");
            usesAtlas = string.Equals(path, atlasPath, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                Texture2D loaded = null;
                try
                {
                    loaded = new Texture2D(2, 2, TextureFormat.RGBA32, true)
                    {
                        name = "DVSeasons Irregular Snow Flake",
                        filterMode = FilterMode.Trilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    if (ImageConversion.LoadImage(loaded, File.ReadAllBytes(path), true)) return loaded;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[DVSeasons] Realistic snow particle texture could not be loaded: " +
                        exception.Message);
                }
                if (loaded != null) UnityEngine.Object.Destroy(loaded);
            }
            usesAtlas = false;
            return CreateFallbackSnowTexture();
        }

        private static Texture2D CreateFallbackSnowTexture()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "DVSeasons Six-Arm Snowflake",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            var pixels = new Color[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = ((x + 0.5f) / size * 2f) - 1f;
                var dy = ((y + 0.5f) / size * 2f) - 1f;
                var radius = Mathf.Sqrt((dx * dx) + (dy * dy));
                var alpha = Mathf.Clamp01((0.16f - radius) * 9f);
                for (var arm = 0; arm < 3; arm++)
                {
                    var angle = arm * Mathf.PI / 3f;
                    var along = Mathf.Abs((dx * Mathf.Cos(angle)) + (dy * Mathf.Sin(angle)));
                    var across = Mathf.Abs((-dx * Mathf.Sin(angle)) + (dy * Mathf.Cos(angle)));
                    var line = Mathf.Clamp01((0.052f - across) * 24f) *
                        Mathf.Clamp01((0.94f - along) * 8f);
                    alpha = Mathf.Max(alpha, line);

                    for (var branch = 1; branch <= 2; branch++)
                    {
                        var branchOrigin = branch * 0.28f;
                        var branchLength = 0.24f;
                        var localAlong = along - branchOrigin;
                        if (localAlong < 0f || localAlong > branchLength) continue;
                        var branchAcross = Mathf.Abs(across - (localAlong * 0.58f));
                        var branchLine = Mathf.Clamp01((0.045f - branchAcross) * 25f) *
                            Mathf.Clamp01((branchLength - localAlong) * 10f);
                        alpha = Mathf.Max(alpha, branchLine);
                    }
                }
                alpha *= Mathf.Clamp01((1f - radius) * 5f);
                pixels[(y * size) + x] = new Color(0.92f, 0.97f, 1f, alpha);
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static Color GetSeasonTint(SeasonState state, int steps)
        {
            return Color.Lerp(ProfileTint(state.Current), ProfileTint(state.Next),
                Quantize01(state.Transition, steps));
        }

        private static float Quantize01(float value, int steps)
        {
            if (steps <= 0) return Mathf.Clamp01(value);
            return Mathf.RoundToInt(Mathf.Clamp01(value) * steps) / (float)steps;
        }

        private static Color ProfileTint(SeasonKind season)
        {
            switch (season)
            {
                case SeasonKind.Spring: return new Color(0.78f, 1.08f, 0.76f);
                case SeasonKind.Autumn: return new Color(1.30f, 0.62f, 0.18f);
                case SeasonKind.Winter: return new Color(1.2f, 1.25f, 1.32f);
                default: return Color.white;
            }
        }

        private static Color Multiply(Color a, Color b)
        {
            return new Color(a.r * b.r, a.g * b.g, a.b * b.b, a.a);
        }

        private static bool Approximately(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.001f && Mathf.Abs(a.g - b.g) < 0.001f &&
                Mathf.Abs(a.b - b.b) < 0.001f && Mathf.Abs(a.a - b.a) < 0.001f;
        }
    }
}
