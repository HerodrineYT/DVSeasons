using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class SeasonVisualController : IDisposable
    {
        internal float BlizzardIntensity = 1f;
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
            public TreePrototype[] TreePrototypes;
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
        private readonly TurntableSnowSource turntableSnowSource = new TurntableSnowSource();
        private readonly LocomotiveSnowHeatController locomotiveHeat = new LocomotiveSnowHeatController();
        private readonly VehicleSideSnowController vehicleSideSnow = new VehicleSideSnowController();
        private readonly Func<Component,float> sideSnowHeating;
        private readonly TenderCoalSnowController tenderCoal;
        private readonly TrainSnowTrailController trainSnowTrail;
        private readonly AutumnLeafGroundController autumnLeaves;
        private readonly SpringLifeController springLife;
        private readonly SnowFootstepAudioController snowFootsteps;
        private readonly WinterWindowController winterWindows;
        private readonly SnowGlareController snowGlare;
        private float lastWindowParticleCheck;
        private DV.TerrainSystem.TerrainGrid snowTerrainGrid;
        private readonly SeasonAssetBundleRepository texturePack;
        private readonly string modPath;
        private GameObject snowObject;
        private ParticleSystem snowSystem;
        private ParticleSystem.Particle[] snowParticleBuffer;
        private readonly SnowfallWorldCollision snowCollisions = new SnowfallWorldCollision();
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
        private bool dynamicSnowEnabled;

        public SeasonVisualController(string modPath)
        {
            this.modPath = modPath ?? string.Empty;
            sideSnowHeating = locomotiveHeat.Heating;
            snowFootsteps = new SnowFootstepAudioController(this.modPath);
            texturePack = new SeasonAssetBundleRepository(modPath);
            trainSnowTrail = new TrainSnowTrailController(texturePack);
            autumnLeaves = new AutumnLeafGroundController(texturePack, this.modPath);
            springLife = new SpringLifeController(this.modPath);
            winterWindows = new WinterWindowController(texturePack);
            snowGlare = new SnowGlareController(texturePack);
            tenderCoal=new TenderCoalSnowController(texturePack);
            seasonalTextures = new SeasonalTextureController(texturePack);
            microSplatTerrain = new MicroSplatSeasonalTerrainController(texturePack);
            waterIce = new WaterIceController(texturePack);
            winterPuddles = new WinterPuddleController(texturePack);
            proceduralSurfaceSnow = new ProceduralSnowController(texturePack);
            proceduralSurfaceSnow.SetVehicleDiscovery(() => RailSnowGameSource.GetCars());
            proceduralSurfaceSnow.SetMovingSurfaceDiscovery(turntableSnowSource.GetRoots);
            proceduralSurfaceSnow.SetVehicleSnowRemaining(locomotiveHeat.Remaining);
            proceduralSurfaceSnow.SetVehicleSideSnow(vehicleSideSnow.Amount);
            proceduralSurfaceSnow.SetVehicleSaveIdentity(source => (source as TrainCar)?.CarGUID);
            snowFootsteps.SetVehicleSnowRemaining(locomotiveHeat.RemainingAt);
            proceduralSurfaceSnow.SetNativeVehicleSnow(tenderCoal.HasSnowTexture);
            tenderCoal.SnowObjectAllowed=proceduralSurfaceSnow.IsObjectSnowAllowed;
            proceduralSurfaceSnow.BeforeSnowRender = () =>
            {
                proceduralSurfaceSnow.SetWorldOffset(DV.OriginShift.OriginShift.currentMove);
                railSnowSource.Update(proceduralSurfaceSnow.RailTracks, trainSnowTrail.WheelAt, trainSnowTrail.ReportTrackContact);
            };
        }

        public void BeginSession()
        {
            startupConfigured = true;
            startupStage = 0;
            Debug.Log("[DVSeasons] Seasonal visuals staged after world initialization; " +
                "streamed textures are checked individually before every readback.");
        }

        // Keep the old call shape for integrations compiled against 0.3.1. The
        // session type no longer affects readiness; each texture reports its own
        // streaming state through StreamingTextureReadiness.
        public void BeginSession(bool multiplayerSession)
        {
            BeginSession();
        }

        public void RestoreSnow(SaveGameData data)
        {
            string json=data.GetString("DVSeasons.SnowState");
            if(string.IsNullOrEmpty(json)) return;
            try
            {
                var state=SnowWorldSave.Decode(json);
                if(state==null || state.Version<1 || state.Version>2 || !SnowWorldSave.Unit(state.Coverage)) return;
                proceduralSurfaceSnow.RestoreSnow(state);
                locomotiveHeat.Restore(state.Cars);
                vehicleSideSnow.Restore(state.VehicleSides);
                winterWindows.Restore(state.Cabs);
                winterWindows.RestoreMasks(state.WindowMasks);
            }
            catch(Exception e) { Debug.LogWarning("[DVSeasons] Snow save could not be restored: "+e.Message); }
        }
        public void SaveSnow(SaveGameData data)
        {
            var state=new SnowWorldSave();
            proceduralSurfaceSnow.SaveSnow(state);
            locomotiveHeat.Save(state.Cars);
            vehicleSideSnow.Save(state.VehicleSides);
            winterWindows.Save(state.Cabs);
            winterWindows.SaveMasks(state.WindowMasks);
            data.SetString("DVSeasons.SnowState",state.Encode());
        }

        public void OnSeasonSelected() { proceduralSurfaceSnow.ReseedSeasonCoverage(); }
        public float? SurfaceSnowCoverage => proceduralSurfaceSnow.HasCoverage
            ? (float?)proceduralSurfaceSnow.Coverage : null;
        public void SetNetworkSnowCoverage(float? coverage) { proceduralSurfaceSnow.SetNetworkCoverage(coverage); }
        public VehicleSideSnowNetworkState[] CaptureSideSnowNetworkState() { return vehicleSideSnow.CaptureNetworkState(); }
        public void ApplySideSnowNetworkState(VehicleSideSnowNetworkState[] states) { vehicleSideSnow.ApplyNetworkState(states); }
        public void SetSideSnowNetworkAuthority(bool enabled) { vehicleSideSnow.SetNetworkAuthority(!enabled); }
        private bool networkSimulation;
        private readonly List<VehicleThermalNetworkState> thermalSnapshot = new List<VehicleThermalNetworkState>();
        private readonly HashSet<string> thermalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public void SetThermalNetworkAuthority(bool useHost) { locomotiveHeat.SetNetworkAuthority(!useHost); }
        public void ApplyThermalNetworkState(VehicleThermalNetworkState[] states) { locomotiveHeat.ApplyNetworkState(states); }
        public VehicleThermalNetworkState[] CaptureThermalNetworkState()
        {
            thermalSnapshot.Clear(); thermalIds.Clear();
            foreach (var car in RailSnowGameSource.GetCars())
            {
                if (car == null || !car.IsLoco || string.IsNullOrEmpty(car.CarGUID) || !thermalIds.Add(car.CarGUID)) continue;
                var state = VehicleThermalNetworkState.Capture(car.CarGUID, winterWindows.ClimateFor(car), locomotiveHeat.Melted(car));
                if (state.IsValid()) thermalSnapshot.Add(state);
                if (thermalSnapshot.Count >= VehicleThermalNetworkState.MaxVehicles) break;
            }
            return thermalSnapshot.Count == 0 ? VehicleThermalNetworkState.Empty : thermalSnapshot.ToArray();
        }
        public void UpdateSimulation(SeasonState state, float snowfall, Vector3 wind, float seconds,
            bool multiplayer, SeasonModSettings settings)
        {
            networkSimulation = multiplayer;
            using (SnowPerformance.Measure("cab-climate")) winterWindows.UpdateClimate(state, multiplayer);
            if (!multiplayer && !settings.ProceduralSnowEnabled) return;
            // Local rendering switches must not stop the host simulating snow
            // for other players or clear a client's confirmed melt history.
            float coverage = multiplayer ? state.SnowAmount : settings.GroundSnowEnabled ? state.SnowAmount : 0;
            float falling = multiplayer || settings.ReplaceRainWithSnow ? snowfall : 0;
            locomotiveHeat.Update(falling, coverage, seconds);
            using (SnowPerformance.Measure("vehicle-side-snow"))
                vehicleSideSnow.Update(falling, coverage, state.TemperatureCelsius, seconds, sideSnowHeating, wind);
        }

        public void Apply(SeasonState state, float precipitationSnowAmount, float rainIntensity, Vector3 windVelocity,
            float snowLightFactor, SeasonModSettings settings)
        {
            if (state == null || settings == null) return;
            if (!settings.InsectsEnabled) springLife.Dispose();
            if (!settings.AutumnLeavesEnabled) autumnLeaves.Dispose();
            SnowPerformance.Frame(state);
            SnowRenderBenchmark.SetSeason(state.Current);
            SetSnowMode(settings.ProceduralSnowEnabled);
            proceduralSurfaceSnow.SetVehicleSnowEnabled(settings.VehicleSnowEnabled);
            proceduralSurfaceSnow.SetObjectLimit(settings.ProceduralSnowEnabled ? settings.SnowObjectLimit : 0);
            snowFootsteps.SetCoverage(settings.GroundSnowEnabled
                ? state.SnowAmount*settings.GroundSnowStrength*settings.TextureChangeStrength : 0f,
                settings.GroundSnowEnabled);
            if (!PrepareStartup(state, settings)) return;
            snowGlare.Apply(settings.SnowGlareReduction, snowLightFactor,
                settings.GroundSnowEnabled ? (proceduralSurfaceSnow.IsActive ? proceduralSurfaceSnow.Coverage : state.SnowAmount) : 0f);
            proceduralSurfaceSnow.GlareReduction = snowGlare.Strength;
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
                using(SnowPerformance.Measure("snow-prepare")) proceduralSurfaceSnow.Apply(settings.GroundSnowEnabled
                    ? state.SnowAmount * settings.GroundSnowStrength * settings.TextureChangeStrength : 0f, true);
            }
            ApplySeasonalTextures(state, settings);
            using(SnowPerformance.Measure("terrain-materials")) microSplatTerrain.Apply(state, settings, proceduralSurfaceSnow.IsActive,
                proceduralSurfaceSnow.IsActive ? (float?)proceduralSurfaceSnow.Coverage : null);
            using(SnowPerformance.Measure("terrain-properties")) ApplyTerrains(state, settings);
            using(SnowPerformance.Measure("tender-coal")) tenderCoal.Apply(settings.VehicleSnowEnabled && settings.GroundSnowEnabled && settings.SeasonalTexturesEnabled && settings.TerrainTextureChanges
                ? (proceduralSurfaceSnow.IsActive?proceduralSurfaceSnow.Coverage:state.SnowAmount*settings.GroundSnowStrength*settings.TextureChangeStrength):0f,
                dynamicSnowEnabled ? (Func<Component,float>)locomotiveHeat.Remaining : null);
            ApplyRainCrossfade(settings.ReplaceRainWithSnow ? precipitationSnowAmount : 0f);
            using(SnowPerformance.Measure("winter-windows"))
                winterWindows.Apply(state, precipitationSnowAmount * rainIntensity, snowLightFactor, settings.WinterWindowsEnabled);
            using(SnowPerformance.Measure("particles")) ApplySnowfall(precipitationSnowAmount, rainIntensity, windVelocity, snowLightFactor, settings);
            using(SnowPerformance.Measure("autumn-leaf-cover"))
                if (settings.AutumnLeavesEnabled)
                    autumnLeaves.Apply(state, windVelocity, settings.AutumnLeafLimit);
            using(SnowPerformance.Measure("spring-pollinators"))
                if (settings.InsectsEnabled)
                    springLife.Apply(state, rainIntensity, snowLightFactor, windVelocity);

            // Reuse the saved/networked SnowAmount for water and puddles. This keeps
            // both ice systems in the exact same phase as terrain on reconnect and
            // after loading a save midway through a thaw.
            var winterCoverage = settings.GroundSnowEnabled
                ? Mathf.Clamp01(state.SnowAmount * settings.GroundSnowStrength *
                    settings.TextureChangeStrength)
                : 0f;
            if (dynamicSnowEnabled) using(SnowPerformance.Measure("train-snow-trail"))
                trainSnowTrail.Apply(proceduralSurfaceSnow.HasCoverage ? proceduralSurfaceSnow.Coverage : winterCoverage,
                    settings.GroundSnowEnabled && settings.SnowParticlesEnabled,
                    snowLightFactor,windVelocity,state.TemperatureCelsius,
                    rainIntensity*(settings.ReplaceRainWithSnow ? 1-precipitationSnowAmount : 1));
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
            if (!networkSimulation) locomotiveHeat.Reset(); // Local mode restores seasonal footsteps.
            trainSnowTrail.Dispose();
            if (snowTerrainGrid != null)
                snowTerrainGrid.TerrainsMoved -= proceduralSurfaceSnow.InvalidateGeometry;
            snowTerrainGrid = null;
        }

        private bool PrepareStartup(SeasonState state, SeasonModSettings settings)
        {
            if (!startupConfigured) BeginSession();
            if (startupStage >= 4) return true;
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
                    ApplySeasonalTextures(state, settings);
                startupStage = 3;
                return false;
            }
            using (SnowPerformance.Measure("startup-terrain-materials"))
                microSplatTerrain.Apply(state, settings, false, null);
            startupStage = 4;
            Debug.Log("[DVSeasons] Staged seasonal visual initialization complete.");
            return false;
        }

        private void ApplySeasonalTextures(SeasonState state, SeasonModSettings settings)
        {
            // A dry season, async shader warmup or temporary camera change does
            // not switch the user's snow system. Using IsActive here previously
            // prepared legacy road/roof pixels, then destroyed every texture set
            // and built it again as soon as procedural snow became active.
            using(SnowPerformance.Measure("seasonal-textures"))
                seasonalTextures.Apply(state, settings, settings.ProceduralSnowEnabled,
                    settings.ProceduralSnowEnabled && proceduralSurfaceSnow.HasCoverage
                        ? (float?)proceduralSurfaceSnow.Coverage : null);
        }

        public void Dispose()
        {
            ResetForSession();
            turntableSnowSource.Dispose();
            waterIce.Dispose();
            winterPuddles.Dispose();
            snowFootsteps.Dispose();
            texturePack.Dispose();
        }

        public void ResetForSession()
        {
            SnowPerformance.Reset();
            snowGlare.Dispose();
            StreamingTextureReadiness.Reset();
            seasonalTextures.Dispose();
            microSplatTerrain.Dispose();
            waterIce.ResetForSession();
            winterPuddles.ResetForSession();
            proceduralSurfaceSnow.Dispose(); railSnowSource.Reset();
            dynamicSnowEnabled = false;
            tenderCoal.Dispose();locomotiveHeat.Reset();
            vehicleSideSnow.Reset();
            networkSimulation = false; thermalSnapshot.Clear(); thermalIds.Clear();
            trainSnowTrail.Dispose();
            autumnLeaves.Dispose();
            springLife.Dispose();
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
            snowCollisions.Clear();
            snowMaterial = null;
            snowTexture = null;
            nextTerrainScan = 0f;
            nextRainScan = 0f;
            nextSnowShelterCheck = 0f;
            winterWindows.Dispose();
            lastWindowParticleCheck = 0f;
            nextCabSnowClear = 0f;
            snowSheltered = false;
            cameraInsideCab = false;
            startupConfigured = false;
            startupStage = 0;
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
                    OriginalTreeMaximumFullLodCount = terrain.treeMaximumFullLODCount,
                    TreePrototypes = terrain.terrainData == null
                        ? null
                        : terrain.terrainData.treePrototypes
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
                ? visibility * precipitation * settings.SnowfallDensity * BlizzardIntensity
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
            var stormMain = snowSystem.main;
            int desiredCapacity = BlizzardIntensity > 1f ? 22500 : 7500;
            if (stormMain.maxParticles != desiredCapacity) stormMain.maxParticles = desiredCapacity;
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
            InterceptSnow(camera, settings.WinterWindowsEnabled);
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
                // Keep approaching flakes outside the cab so they can hit glass.
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

        private void InterceptSnow(Camera camera, bool windowsEnabled)
        {
            float now = Time.time;
            if (snowSystem == null || now-lastWindowParticleCheck < .05f) return;
            float dt = Mathf.Clamp(now-lastWindowParticleCheck, .01f, .5f);
            lastWindowParticleCheck = now;
            int count = snowSystem.particleCount;
            if (count == 0) return;
            if (snowParticleBuffer == null || snowParticleBuffer.Length < count)
                snowParticleBuffer = new ParticleSystem.Particle[Mathf.NextPowerOfTwo(count)];
            count = snowSystem.GetParticles(snowParticleBuffer);
            bool changed = false;
            var cameraPosition = camera.transform.position;
            snowCollisions.Prepare(cameraPosition);
            for (int i=0;i<count;i++)
            {
                var p=snowParticleBuffer[i];
                float distanceSquared = (p.position-cameraPosition).sqrMagnitude;
                if (distanceSquared > SnowfallWorldCollision.ParticleRadius * SnowfallWorldCollision.ParticleRadius) continue;
                var velocity=p.totalVelocity;
                var previous = p.position - velocity * dt;
                bool windowHit = windowsEnabled && distanceSquared <= 225 && winterWindows.Collide(previous,p.position,dt);
                // The emitter is a volume: a newly born flake can start inside a
                // room or on the sheltered side of a wall. Check its incoming
                // weather path as well, rather than waiting for a wall crossing.
                bool newborn = p.startLifetime - p.remainingLifetime <= dt + .01f;
                if (newborn && velocity.sqrMagnitude > .001f) previous = p.position - velocity.normalized * 32f;
                if (!windowHit && !snowCollisions.Blocked(previous,p.position)) continue;
                p.remainingLifetime=0;snowParticleBuffer[i]=p;changed=true;
            }
            if(changed) snowSystem.SetParticles(snowParticleBuffer,count);
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
                if (winterWindows.IsOutsideCab(cameraPosition, snowParticleBuffer[i].position)) continue;
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
                SeasonSurfaceLayers.Mask, QueryTriggerInteraction.Ignore);
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
            main.startSize = new ParticleSystem.MinMaxCurve(SnowflakeParticleTexture.MinimumSize, SnowflakeParticleTexture.MaximumSize);
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
            // Use the cached nearby-collider interceptor instead of running a
            // global PhysX collision query for every distant particle every frame.
            var collision = snowSystem.collision;
            collision.enabled = false;
            var renderer = snowObject.GetComponent<ParticleSystemRenderer>();
            var shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended") ??
                Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default");
            if (shader != null)
            {
                var usesAtlas = false;
                snowTexture = SnowflakeParticleTexture.LoadOrCreate(modPath, out usesAtlas);
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
                case SeasonKind.Spring: return new Color(1.04f, 1.16f, 0.90f);
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
