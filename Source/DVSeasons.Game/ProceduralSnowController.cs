using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DVSeasons.Mod
{
    /// <summary>Material-independent snow for the active deferred world camera.</summary>
    internal sealed class ProceduralSnowController : IDisposable
    {
        private sealed class ExposureMap
        {
            public RenderTexture Texture;
            public Vector4 Area;
            public float NextUpdate;
            public float HeightOffset;
            public bool Ready;
            public bool GeometryDirty;
        }

        private readonly SeasonAssetBundleRepository repository;
        private readonly ExposureMap near = new ExposureMap();
        private readonly ExposureMap far = new ExposureMap();
        private readonly ExposureMap distant = new ExposureMap();
        private readonly Vector3[] ambientDirections = { Vector3.up };
        private readonly Color[] ambientColors = new Color[1];
        private readonly RenderTargetIdentifier[] snowTargets = {
            new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer0),
            new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer1),
            new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget)
        };
        private bool? materialHdr;
        private readonly Matrix4x4[] inverseStereoVP = new Matrix4x4[2];
        private readonly RenderTextureFormat ambientCopyFormat = SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8)
            ? RenderTextureFormat.R8 : RenderTextureFormat.ARGB32;
        private struct TerrainCaptureState
        {
            public Terrain Terrain;
            public bool DrawHeightmap;
        }
        private readonly List<TerrainCaptureState> captureTerrains = new List<TerrainCaptureState>();
        private readonly SnowVehicleRegistry vehicles = new SnowVehicleRegistry();
        private readonly SnowExposureExclusions exposureExclusions = new SnowExposureExclusions();
        private float nextProxyRefresh;
        public readonly RailSnowTracks RailTracks = new RailSnowTracks();
        public Action BeforeSnowRender;
        public float GlareReduction;
        public void SetObjectLimit(int limit) { vehicles.ObjectLimiter.Limit = limit; }
        public void SetVehicleSnowEnabled(bool enabled)
        {
            if(vehicles.VehicleSnowEnabled==enabled)return;
            SnowRenderBenchmark.Cancel("vehicle snow setting changed");
            vehicles.VehicleSnowEnabled=enabled;
        }
        public bool IsObjectSnowAllowed(Component source)
        { return !IsActive || !vehicles.ObjectLimiter.Active || (source != null && vehicles.ObjectLimiter.IsSelected(source.transform)); }
        public void SetVehicleDiscovery(Func<IEnumerable<Component>> discovery) { vehicles.DiscoverVehicles=discovery; }
        public void SetMovingSurfaceDiscovery(Func<IEnumerable<Transform>> discovery) { vehicles.DiscoverMovingRoots=discovery; }
        public void SetVehicleSnowRemaining(Func<Component,float> remaining) {vehicles.SnowRemaining=remaining;}
        public void SetVehicleSideSnow(Func<Component,Vector4> amount) {vehicles.SideSnowAmount=amount;}
        public void SetVehicleSaveIdentity(Func<Component,string> identity) {vehicles.StableVehicleId=identity;}
        public void SetNativeVehicleSnow(Func<Renderer,int,bool> hasTexture) {vehicles.HasNativeSnowTexture=hasTexture;}
        public void InvalidateGeometry() { near.GeometryDirty=far.GeometryDirty=distant.GeometryDirty=true; }
        private void SceneLoaded(Scene scene,LoadSceneMode mode) { InvalidateGeometry(); }
        private void SceneUnloaded(Scene scene) { InvalidateGeometry(); }
        private Vector3 worldOffset;
        public void SetWorldOffset(Vector3 offset)
        {
            var delta=offset-worldOffset;
            if(delta==Vector3.zero) return;
            ShiftExposure(near,delta); ShiftExposure(far,delta); ShiftExposure(distant,delta);
            vehicles.ObjectLimiter.ShiftWorld(delta);
            worldOffset=offset;
        }
        private static void ShiftExposure(ExposureMap map,Vector3 delta)
        {
            if(!map.Ready) return;
            map.Area.x+=delta.x; map.Area.y+=delta.z;
            map.HeightOffset+=delta.y;
        }
        private float snowfall;
        private float? hostCoverage;
        public void SetNetworkCoverage(float? coverage) { hostCoverage = coverage; }
        private bool coverageInitialized;
        private Texture2D noiseTexture;
        public float Coverage => amount;
        public bool HasCoverage => coverageInitialized;
        public void ReseedSeasonCoverage() { coverageInitialized = false; }
        public void SaveSnow(SnowWorldSave state)
        { state.HasCoverage=coverageInitialized; state.Coverage=amount; RailTracks.Save(state.Rails); vehicles.SaveMasks(state.VehicleMasks); }
        public void RestoreSnow(SnowWorldSave state)
        { amount=state.Coverage; coverageInitialized=state.HasCoverage; RailTracks.Restore(state.Rails); vehicles.RestoreMasks(state.VehicleMasks); }
        public void SetWeather(float intensity) { snowfall=Mathf.Clamp01(intensity); }
        public int ExposureCaptureCount { get; private set; }
        private static readonly int DiffuseId = Shader.PropertyToID("_DVPSDiffuse");
        private static readonly int SpecularId = Shader.PropertyToID("_DVPSSpecular");
        private static readonly int NormalId = Shader.PropertyToID("_DVPSNormal");
        private static readonly int LightingId = Shader.PropertyToID("_DVPSLighting");
        private static readonly int NoiseId = Shader.PropertyToID("_DVPSNoise");
        private static readonly int NearHeightId = Shader.PropertyToID("_DVPSNearHeight");
        private static readonly int FarHeightId = Shader.PropertyToID("_DVPSFarHeight");
        private static readonly int DistantHeightId = Shader.PropertyToID("_DVPSDistantHeight");
        private static readonly int NearAreaId = Shader.PropertyToID("_DVPSNearArea");
        private static readonly int FarAreaId = Shader.PropertyToID("_DVPSFarArea");
        private static readonly int DistantAreaId = Shader.PropertyToID("_DVPSDistantArea");
        private static readonly int WorldOffsetId = Shader.PropertyToID("_DVPSWorldOffset");
        private static readonly int HeightOffsetsId = Shader.PropertyToID("_DVPSHeightOffsets");
        private static readonly int AmountId = Shader.PropertyToID("_DVPSAmount");
        private static readonly int GlareReductionId = Shader.PropertyToID("_DVPSGlareReduction");
        private static readonly int HdrId = Shader.PropertyToID("_DVPSHDR");
        private static readonly int AmbientId = Shader.PropertyToID("_DVPSAmbient");
        private static readonly int InverseVpId = Shader.PropertyToID("_DVPSInverseVP");
        private static readonly int SrcBlendId = Shader.PropertyToID("_SnowSrcBlend");
        private static readonly int DstBlendId = Shader.PropertyToID("_SnowDstBlend");
        private static readonly int AlphaSrcBlendId = Shader.PropertyToID("_SnowAlphaSrcBlend");
        private static readonly int AlphaDstBlendId = Shader.PropertyToID("_SnowAlphaDstBlend");
        private static readonly int SpecAlphaDstBlendId = Shader.PropertyToID("_SnowSpecAlphaDstBlend");
        private Camera worldCamera;
        private Camera exposureCamera;
        private Shader exposureShader;
        private Material terrainExposureMaterial;
        private CommandBuffer terrainExposureCommands;
        private Material material;
        private Mesh quad;
        private CommandBuffer commands;
        private float amount;
        private float nextLoadAttempt;
        private bool renderingExposure;
        private bool failureLogged;
        private bool subscribed;
        private float renderRetryAfter;
        public bool IsActive { get; private set; }

        public ProceduralSnowController(SeasonAssetBundleRepository repository)
        {
            this.repository = repository;
        }

        public void Apply(float coverage, bool enabled)
        {
            // Texture-only mode must not advance the snow clock or retain the
            // exposure maps, vehicle masks and camera callbacks between frames.
            if (!enabled)
            {
                if (coverageInitialized) Dispose();
                return;
            }
            coverage=Mathf.Clamp01(coverage);
            if(coverage<=0.001f && amount>0.001f) vehicles.ResetSnow();
            if (hostCoverage.HasValue && coverage > 0.001f)
            { amount=Mathf.Clamp01(hostCoverage.Value); coverageInitialized=true; }
            else if (!coverageInitialized || snowfall>0.001f || coverage<=0.001f)
            { amount=coverage; coverageInitialized=true; }
            RailTracks.Advance(snowfall,Time.deltaTime);
            if (amount <= .001f) { Unbind(); return; }
            var camera = PlayerManager.ActiveCamera != null ? PlayerManager.ActiveCamera : Camera.main;
            if (camera == null || camera.actualRenderingPath != RenderingPath.DeferredShading ||
                SystemInfo.supportedRenderTargetCount < 4 || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RFloat))
            {
                Unbind();
                return;
            }
            if (Time.realtimeSinceStartup < renderRetryAfter || !LoadResources()) { Unbind(); return; }
            if (worldCamera != camera)
            {
                Unbind();
                worldCamera = camera;
                worldCamera.depthTextureMode |= DepthTextureMode.Depth;
                commands = new CommandBuffer { name = "DVSeasons procedural snow" };
                worldCamera.AddCommandBuffer(CameraEvent.BeforeReflections, commands);
                Camera.onPreRender += BeforeCamera;
                SceneManager.sceneLoaded += SceneLoaded;
                SceneManager.sceneUnloaded += SceneUnloaded;
                subscribed = true;
                // Exposure maps describe the world, not a specific camera.
                // UpdateExposure already refreshes them after actual relocation.
            }
            IsActive = true;
            SnowRenderBenchmark.Ready(camera,amount,vehicles.ObjectLimiter.Limit);
            if (amount <= 0.001f) { commands.Clear(); return; }
            // Auxiliary cameras must finish before the world camera starts culling.
            // In 0.2.14 nested renders in onPreCull could disturb camera globals;
            // the cached inverse matrix also preceded camera shake/TAA updates.
            try
            {
                vehicles.StaticGeometryChanged=InvalidateGeometry;
                using(SnowPerformance.Measure("vehicle-discovery")) vehicles.Update(camera);
                if(exposureExclusions.Update()) InvalidateGeometry();
                // Geometry is static between weather changes. Camera relocation still
                // maintains visibility; it does not accumulate snow.
                var nearWasReady=near.Ready;
                int capturesBefore=ExposureCaptureCount;
                UpdateExposure(near,128f,30f);
                // The initial near and far maps are deliberately built on separate
                // frames so entering a world cannot issue both auxiliary renders at once.
                if(nearWasReady && (!far.Ready || ExposureCaptureCount==capturesBefore))
                    UpdateExposure(far,1024f,120f);
                if(far.Ready && ExposureCaptureCount==capturesBefore)
                    UpdateExposure(distant,Mathf.Max(2048f,Mathf.Ceil(camera.farClipPlane*2.5f/256f)*256f),240f);
                if(near.Ready && far.Ready)
                    vehicles.Accumulate(near.Texture,near.Area,near.HeightOffset,far.Texture,far.Area,far.HeightOffset,RailTracks.SnowClock,
                        distant.Ready?distant.Texture:null,distant.Area,distant.HeightOffset);
            }
            catch (Exception exception) { FailRender(exception); }
        }

        private bool LoadResources()
        {
            if (material != null && exposureShader != null && exposureCamera != null) return vehicles.Initialize(repository);
            if (Time.realtimeSinceStartup < nextLoadAttempt) return false;
            nextLoadAttempt = Time.realtimeSinceStartup + 5f;
            var shader = repository.LoadShader("ProceduralSnow");
            exposureShader = repository.LoadShader("SnowExposure");
            if (shader == null || exposureShader == null) return false;
            exposureExclusions.SetExclusionShader(repository.LoadShader("SnowVehicle"));
            if(terrainExposureMaterial==null) terrainExposureMaterial=new Material(exposureShader) {hideFlags=HideFlags.HideAndDontSave};
            if(terrainExposureCommands==null) terrainExposureCommands=new CommandBuffer {name="DVSeasons terrain height capture"};
            if (material == null)
            { material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave }; materialHdr=null; }
            if(noiseTexture==null)
            {
                noiseTexture=SnowCoveragePattern.CreateTexture();
            }
            if (quad == null)
            {
                quad = new Mesh
                {
                    name = "DVSeasons procedural snow screen quad", hideFlags = HideFlags.HideAndDontSave,
                    vertices = new[] { new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0) },
                    uv = new[] { Vector2.zero,Vector2.right,Vector2.one,Vector2.up },
                    triangles = new[] { 0,1,2,0,2,3 }
                };
            }
            if (exposureCamera == null)
            {
                var owner = new GameObject("DVSeasons snow exposure") { hideFlags = HideFlags.HideAndDontSave };
                exposureCamera = owner.AddComponent<Camera>();
                exposureCamera.enabled = false;
                exposureCamera.stereoTargetEye = StereoTargetEyeMask.None;
                exposureCamera.orthographic = true;
                exposureCamera.renderingPath = RenderingPath.Forward;
                exposureCamera.clearFlags = CameraClearFlags.SolidColor;
                exposureCamera.backgroundColor = new Color(-100000f,0,0,0);
                exposureCamera.allowHDR = false;
                exposureCamera.allowMSAA = false;
                exposureCamera.useOcclusionCulling = false;
                exposureCamera.depthTextureMode = DepthTextureMode.None;
                exposureCamera.nearClipPlane = 0.1f;
                exposureCamera.farClipPlane = 6000f;
                exposureCamera.transform.rotation = Quaternion.Euler(90f,0,0);
            }
            return vehicles.Initialize(repository);
        }

        private void BeforeCamera(Camera camera)
        {
            if (renderingExposure || camera != worldCamera || commands == null) return;
            commands.Clear();
            if (!IsActive || amount <= 0.001f || material == null || !near.Ready || !far.Ready)
            {SnowRenderBenchmark.Cancel("snow renderer not ready");return;}
            SnowRenderBenchmark.BeforeRender(camera);
            try
            {
                using(SnowPerformance.Measure("rail-traces")) BeforeSnowRender?.Invoke();
                Color nativeAmbient;
                if(RenderSettings.ambientMode==AmbientMode.Flat)nativeAmbient=RenderSettings.ambientLight;
                else {var probe=RenderSettings.ambientProbe;probe.Evaluate(ambientDirections,ambientColors);nativeAmbient=ambientColors[0];}
                vehicles.PrepareNativeSnow(SnowRenderBenchmark.SkipAll || SnowRenderBenchmark.SkipVehicles || SnowRenderBenchmark.SkipShading?0:amount,
                    GlareReduction,noiseTexture,nativeAmbient);
                if(SnowRenderBenchmark.SkipAll)return;
                using(SnowPerformance.Measure("render-commands")) RecordCommands();
            }
            catch (Exception exception)
            {
                FailRender(exception);
            }
            finally { renderingExposure = false; }
        }

        private void FailRender(Exception exception)
        {
            SnowRenderBenchmark.Cancel("render error");
            if (commands != null) commands.Clear(); IsActive=false;
            renderRetryAfter=Time.realtimeSinceStartup+5f;
            if (!failureLogged) { failureLogged=true; Debug.LogError("[DVSeasons] Procedural snow render failed: "+exception); }
        }

        private void UpdateExposure(ExposureMap map, float radius, float interval)
        {
            var position = worldCamera.transform.position;
            if (map.Ready && Mathf.Abs(map.Area.z-radius)<1 && (!map.GeometryDirty || Time.realtimeSinceStartup<nextProxyRefresh) &&
                (snowfall<=0.001f || Time.realtimeSinceStartup < map.NextUpdate) &&
                Mathf.Abs(position.x-map.Area.x) < radius*0.5f && Mathf.Abs(position.z-map.Area.y) < radius*0.5f) return;
            const int size = 1024;
            if (map.Texture == null)
            {
                map.Texture = new RenderTexture(size,size,24,RenderTextureFormat.RFloat,RenderTextureReadWrite.Linear)
                {
                    name = "DVSeasons snow exposure " + radius, filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave
                };
                map.Texture.Create();
            }
            float texel = radius*2/size;
            map.Area = new Vector4(Mathf.Floor(position.x/texel)*texel,
                Mathf.Floor(position.z/texel)*texel,radius,texel);
            exposureCamera.transform.position = new Vector3(map.Area.x,position.y+3000f,map.Area.y);
            exposureCamera.orthographicSize = radius;
            exposureCamera.aspect = 1f;
            exposureCamera.cullingMask = worldCamera.cullingMask;
            exposureCamera.targetTexture = map.Texture;
            var oldArea = Shader.GetGlobalVector("_DVPSCaptureArea");
            var oldTarget = RenderTexture.active;
            try
            {
                renderingExposure=true;
                vehicles.HideForStaticCapture(map.Area);
                exposureExclusions.HideForCapture();
                // Replacement rendering uses a coarse terrain mesh, independently
                // of the visible terrain LOD. Its planar triangles produce metre-
                // sized height errors on hills. Capture native height data directly.
                captureTerrains.Clear();
                foreach (var terrain in Terrain.activeTerrains)
                {
                    if (terrain == null || !terrain.drawHeightmap || terrain.terrainData==null) continue;
                    var terrainPosition=terrain.transform.position;var terrainSize=terrain.terrainData.size;
                    if(terrainPosition.x>map.Area.x+radius || terrainPosition.x+terrainSize.x<map.Area.x-radius ||
                        terrainPosition.z>map.Area.y+radius || terrainPosition.z+terrainSize.z<map.Area.y-radius) continue;
                    captureTerrains.Add(new TerrainCaptureState {Terrain=terrain,DrawHeightmap=terrain.drawHeightmap});
                    terrain.drawHeightmap=false;
                }
                Shader.SetGlobalVector("_DVPSCaptureArea",map.Area);
                using(SnowPerformance.Measure("exposure-render")) exposureCamera.RenderWithShader(exposureShader,"RenderType");
                terrainExposureCommands.Clear();
                terrainExposureCommands.SetRenderTarget(map.Texture);
                terrainExposureCommands.SetGlobalMatrix("_DVPSTerrainVP",GL.GetGPUProjectionMatrix(exposureCamera.projectionMatrix,true)*exposureCamera.worldToCameraMatrix);
                foreach(var state in captureTerrains)
                {
                    var terrain=state.Terrain;var data=terrain.terrainData;
                    var p=terrain.transform.position;var s=data.size;
                    terrainExposureCommands.SetGlobalTexture("_DVPSTerrainHeightmap",data.heightmapTexture);
                    terrainExposureCommands.SetGlobalTexture("_DVPSTerrainHoles",data.holesTexture);
                    terrainExposureCommands.SetGlobalVector("_DVPSTerrainArea",new Vector4(p.x,p.z,s.x,s.z));
                    // Unity encodes normalized terrain heights in 0..32766 of R16.
                    terrainExposureCommands.SetGlobalVector("_DVPSTerrainHeightScale",new Vector4(p.y,s.y*(65535f/32766f),data.heightmapResolution,0));
                    terrainExposureCommands.DrawMesh(quad,Matrix4x4.identity,terrainExposureMaterial,0,1);
                }
                Graphics.ExecuteCommandBuffer(terrainExposureCommands);
            }
            finally
            {
                vehicles.RestoreAfterStaticCapture();
                exposureExclusions.Restore();
                renderingExposure=false;
                foreach (var state in captureTerrains)
                    if (state.Terrain != null)
                        state.Terrain.drawHeightmap=state.DrawHeightmap;
                captureTerrains.Clear();
                Shader.SetGlobalVector("_DVPSCaptureArea",oldArea);
                RenderTexture.active = oldTarget;
                exposureCamera.targetTexture = null;
            }
            map.Ready = true;
            if(map.GeometryDirty) nextProxyRefresh=Time.realtimeSinceStartup+1f;
            map.GeometryDirty=false;
            map.HeightOffset = 0;
            ExposureCaptureCount++;
            map.NextUpdate = Time.realtimeSinceStartup+interval;
        }

        private void RecordCommands()
        {
            var camera = worldCamera;
            bool hdr;
            // These four non-overlapping CPU scopes cover the entire command
            // submission. The existing command-buffer samples measure GPU work.
            using(SnowPerformance.Measure("snow-buffer-prep-record"))
            {
            hdr=camera.allowHDR;
            commands.BeginSample("DVSeasons snow buffer preparation");
            var lightingTarget = hdr ? BuiltinRenderTextureType.CameraTarget : BuiltinRenderTextureType.GBuffer3;
            if(materialHdr!=hdr)
            {
                if(hdr) material.EnableKeyword("DVPS_FAST_HDR"); else material.DisableKeyword("DVPS_FAST_HDR");
                material.SetInt(SrcBlendId,(int)(hdr?BlendMode.SrcAlpha:BlendMode.One));
                material.SetInt(DstBlendId,(int)(hdr?BlendMode.OneMinusSrcAlpha:BlendMode.Zero));
                material.SetInt(AlphaSrcBlendId,(int)(hdr?BlendMode.Zero:BlendMode.One));
                material.SetInt(AlphaDstBlendId,(int)(hdr?BlendMode.One:BlendMode.Zero));
                material.SetInt(SpecAlphaDstBlendId,(int)(hdr?BlendMode.OneMinusSrcAlpha:BlendMode.Zero));
                snowTargets[2]=new RenderTargetIdentifier(lightingTarget);
                materialHdr=hdr;
            }
            // HDR only needs AO. Copy alpha into a single 8-bit channel instead
            // of carrying unused RGB through the half-size intermediate. The
            // source GBuffer AO is already 8-bit; filtering stays unchanged.
            StereoRenderSupport.GetTemporaryRT(commands,DiffuseId,camera,
                hdr?ambientCopyFormat:RenderTextureFormat.ARGB32,hdr?FilterMode.Bilinear:FilterMode.Point,hdr?2:1);
            if(hdr) commands.Blit(BuiltinRenderTextureType.GBuffer0,DiffuseId,material,1);
            else commands.Blit(BuiltinRenderTextureType.GBuffer0,DiffuseId);
            commands.SetGlobalTexture(DiffuseId,DiffuseId);
            if(!hdr)
            {
                StereoRenderSupport.GetTemporaryRT(commands,SpecularId,camera,RenderTextureFormat.ARGB32,FilterMode.Point);
                StereoRenderSupport.GetTemporaryRT(commands,LightingId,camera,RenderTextureFormat.ARGBHalf,FilterMode.Point);
                commands.Blit(BuiltinRenderTextureType.GBuffer1,SpecularId);
                commands.Blit(lightingTarget,LightingId);
                commands.SetGlobalTexture(SpecularId,SpecularId);
                commands.SetGlobalTexture(LightingId,LightingId);
            }
            commands.SetGlobalTexture(NormalId,BuiltinRenderTextureType.GBuffer2);
            commands.EndSample("DVSeasons snow buffer preparation");
            }
            using(SnowPerformance.Measure("snow-vehicle-record"))
            {
            commands.BeginSample("DVSeasons snow vehicle surfaces");
            // Animal visibility and exclusions belong to this outer surface
            // scope too; their cost must not disappear between vehicle phases.
            bool recordVehicles=!SnowRenderBenchmark.SkipVehicles;
            if(recordVehicles)
            {
            vehicles.Record(commands,camera,RailTracks,exposureExclusions.PrepareVisibleAnimals(camera));
            SnowPerformance.SnowRender(vehicles.FrameSnowDrawCount,vehicles.FrameExclusionDrawCount,
                vehicles.ObjectLimiter.Limit,camera.pixelWidth,camera.pixelHeight,
                vehicles.FrameInstancedExclusionCount,vehicles.FrameExclusionBatchCount,
                vehicles.FrameInstancedFullCount,vehicles.FrameFullBatchCount,vehicles.FrameExclusionVolumeCount,
                vehicles.NativeMaterialSlots,vehicles.FrameCombinedCommands,vehicles.FrameDistantCars,vehicles.FrameDistantCommands);
            exposureExclusions.RecordAnimalExclusions(commands);
            }
            else
            {
                // This phase deliberately omits all vehicle/rail/animal surface
                // commands. Clear globals instead of reusing a previous target.
                vehicles.BindEmptyFrame(commands);
                SnowPerformance.SnowRender(0,0,vehicles.ObjectLimiter.Limit,camera.pixelWidth,camera.pixelHeight);
            }
            commands.EndSample("DVSeasons snow vehicle surfaces");
            }
            using(SnowPerformance.Measure("snow-snow-shading-record"))
            {
            commands.SetGlobalTexture(NoiseId,noiseTexture);
            commands.SetGlobalTexture(NearHeightId,near.Texture);
            commands.SetGlobalTexture(FarHeightId,far.Texture);
            commands.SetGlobalVector(NearAreaId,near.Area);
            commands.SetGlobalVector(FarAreaId,far.Area);
            commands.SetGlobalTexture(DistantHeightId,distant.Ready?distant.Texture:far.Texture);
            commands.SetGlobalVector(DistantAreaId,distant.Ready?distant.Area:far.Area);
            commands.SetGlobalVector(WorldOffsetId,worldOffset);
            commands.SetGlobalVector(HeightOffsetsId,new Vector4(near.HeightOffset,far.HeightOffset,distant.Ready?distant.HeightOffset:far.HeightOffset,0));
            commands.SetGlobalFloat(AmountId,amount);
            commands.SetGlobalVector("_DVPSVehicleDistanceFade",new Vector4(260,300,1,0));
            // Existing bundles retain the former world-object filter. Only
            // rolling stock is limited now, via its per-pixel exclusion marker.
            commands.SetGlobalFloat("_DVPSObjectLimitEnabled",0f);
            commands.SetGlobalFloat(GlareReductionId,GlareReduction);
            commands.SetGlobalFloat(HdrId,hdr ? 1f : 0f);
            Color ambient;
            if(RenderSettings.ambientMode==AmbientMode.Flat) ambient=RenderSettings.ambientLight;
            else
            {
                var probe=RenderSettings.ambientProbe;
                probe.Evaluate(ambientDirections,ambientColors);
                ambient=ambientColors[0];
            }
            commands.SetGlobalColor(AmbientId,ambient);
            commands.SetGlobalMatrix(InverseVpId,StereoRenderSupport.InverseViewProjection(camera));
            if(camera.stereoEnabled)
            {
                inverseStereoVP[0]=StereoRenderSupport.InverseViewProjection(camera,Camera.StereoscopicEye.Left);
                inverseStereoVP[1]=StereoRenderSupport.InverseViewProjection(camera,Camera.StereoscopicEye.Right);
                commands.SetGlobalMatrixArray("_DVPSInverseVPStereo",inverseStereoVP);
            }
            commands.SetRenderTarget(snowTargets,BuiltinRenderTextureType.CameraTarget);
            commands.BeginSample("DVSeasons snow shading");
            if(!SnowRenderBenchmark.SkipShading)commands.DrawMesh(quad,Matrix4x4.identity,material,0,0);
            commands.EndSample("DVSeasons snow shading");
            }
            using(SnowPerformance.Measure("snow-frame-cleanup-record"))
            {
            commands.ReleaseTemporaryRT(DiffuseId);
            if(!hdr) { commands.ReleaseTemporaryRT(SpecularId); commands.ReleaseTemporaryRT(LightingId); }
            if(!SnowRenderBenchmark.SkipVehicles)vehicles.ReleaseFrame(commands);
            }
        }

        private void Unbind()
        {
            Shader.SetGlobalFloat("_DVPSNativeAmount",0);
            SnowRenderBenchmark.Unavailable();
            IsActive = false;
            if (subscribed) Camera.onPreRender -= BeforeCamera;
            if (subscribed) { SceneManager.sceneLoaded-=SceneLoaded;SceneManager.sceneUnloaded-=SceneUnloaded; }
            subscribed = false;
            if (commands != null)
            {
                if (worldCamera != null) worldCamera.RemoveCommandBuffer(CameraEvent.BeforeReflections,commands);
                commands.Dispose(); commands = null;
            }
            worldCamera = null;
        }

        public void Dispose()
        {
            Unbind();
            vehicles.Dispose(); RailTracks.Dispose(); coverageInitialized=false;
            hostCoverage = null;
            exposureExclusions.Dispose();nextProxyRefresh=0;
            near.GeometryDirty=far.GeometryDirty=distant.GeometryDirty=false;
            worldOffset=Vector3.zero;
            if (exposureCamera != null) DestroyResource(exposureCamera.gameObject);
            DestroyResource(near.Texture); DestroyResource(far.Texture); DestroyResource(distant.Texture);
            DestroyResource(material); DestroyResource(quad); DestroyResource(noiseTexture); noiseTexture=null;
            materialHdr=null;
            DestroyResource(terrainExposureMaterial);terrainExposureMaterial=null;
            if(terrainExposureCommands!=null) terrainExposureCommands.Dispose();terrainExposureCommands=null;
            exposureCamera = null; near.Texture = far.Texture = distant.Texture = null; material = null; quad = null;
            near.Ready = far.Ready = distant.Ready = false; nextLoadAttempt = renderRetryAfter = 0; failureLogged = false;
        }

        private static void DestroyResource(UnityEngine.Object resource)
        {
            if (resource == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(resource);
            else UnityEngine.Object.DestroyImmediate(resource);
        }
    }
}
