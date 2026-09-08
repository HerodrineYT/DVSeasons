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
        private readonly Vector3[] ambientDirections = { Vector3.up };
        private readonly Color[] ambientColors = new Color[1];
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
        public void SetVehicleDiscovery(Func<IEnumerable<Component>> discovery) { vehicles.DiscoverVehicles=discovery; }
        public void SetVehicleSnowRemaining(Func<Component,float> remaining) {vehicles.SnowRemaining=remaining;}
        public void SetNativeVehicleSnow(Func<Renderer,int,bool> hasTexture) {vehicles.HasNativeSnowTexture=hasTexture;}
        public void InvalidateGeometry() { near.GeometryDirty=far.GeometryDirty=true; }
        private void SceneLoaded(Scene scene,LoadSceneMode mode) { InvalidateGeometry(); }
        private void SceneUnloaded(Scene scene) { InvalidateGeometry(); }
        private Vector3 worldOffset;
        public void SetWorldOffset(Vector3 offset)
        {
            var delta=offset-worldOffset;
            if(delta==Vector3.zero) return;
            ShiftExposure(near,delta); ShiftExposure(far,delta);
            worldOffset=offset;
        }
        private static void ShiftExposure(ExposureMap map,Vector3 delta)
        {
            if(!map.Ready) return;
            map.Area.x+=delta.x; map.Area.y+=delta.z;
            map.HeightOffset+=delta.y;
        }
        private float snowfall;
        private bool coverageInitialized;
        private Texture2D noiseTexture;
        public float Coverage => amount;
        public void SetWeather(float intensity) { snowfall=Mathf.Clamp01(intensity); }
        public int ExposureCaptureCount { get; private set; }
        private static readonly int DiffuseId = Shader.PropertyToID("_DVPSDiffuse");
        private static readonly int SpecularId = Shader.PropertyToID("_DVPSSpecular");
        private static readonly int NormalId = Shader.PropertyToID("_DVPSNormal");
        private static readonly int LightingId = Shader.PropertyToID("_DVPSLighting");
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
            if (!coverageInitialized || snowfall>0.001f || coverage<=0.001f)
            { amount=coverage; coverageInitialized=true; }
            RailTracks.Advance(snowfall,Time.deltaTime);
            var camera = Camera.main;
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
                near.Ready = far.Ready = false;
                Debug.Log("[DVSeasons] Procedural snow bound to deferred camera '" + camera.name +
                    "': world-position coverage, 256m/2048m exposure maps, no object winter textures.");
            }
            IsActive = true;
            if (amount <= 0.001f) { commands.Clear(); return; }
            // Auxiliary cameras must finish before the world camera starts culling.
            // In 0.2.14 nested renders in onPreCull could disturb camera globals;
            // the cached inverse matrix also preceded camera shake/TAA updates.
            try
            {
                vehicles.Update(camera);
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
                if(near.Ready && far.Ready)
                    vehicles.Accumulate(near.Texture,near.Area,near.HeightOffset,far.Texture,far.Area,far.HeightOffset,RailTracks.SnowClock);
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
            if(terrainExposureMaterial==null) terrainExposureMaterial=new Material(exposureShader) {hideFlags=HideFlags.HideAndDontSave};
            if(terrainExposureCommands==null) terrainExposureCommands=new CommandBuffer {name="DVSeasons terrain height capture"};
            if (material == null) material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if(noiseTexture==null)
            {
                noiseTexture=new Texture2D(128,128,TextureFormat.RGBA32,false,true)
                { wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Bilinear,hideFlags=HideFlags.HideAndDontSave };
                var pixels=new Color32[128*128]; uint seed=0x71623u;
                for(int i=0;i<pixels.Length;i++) { seed=1664525u*seed+1013904223u; byte v=(byte)(seed>>24); pixels[i]=new Color32(v,v,v,255); }
                noiseTexture.SetPixels32(pixels);noiseTexture.Apply(false,true);
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
            if (!IsActive || amount <= 0.001f || material == null || !near.Ready || !far.Ready) return;
            try
            {
                using(SnowPerformance.Measure("rail-traces")) BeforeSnowRender?.Invoke();
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
            if (commands != null) commands.Clear(); IsActive=false;
            renderRetryAfter=Time.realtimeSinceStartup+5f;
            if (!failureLogged) { failureLogged=true; Debug.LogError("[DVSeasons] Procedural snow render failed: "+exception); }
        }

        private void UpdateExposure(ExposureMap map, float radius, float interval)
        {
            var position = worldCamera.transform.position;
            if (map.Ready && (!map.GeometryDirty || Time.realtimeSinceStartup<nextProxyRefresh) &&
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
            var lightingTarget = camera.allowHDR ? BuiltinRenderTextureType.CameraTarget : BuiltinRenderTextureType.GBuffer3;
            bool hdr=camera.allowHDR;
            if(hdr) material.EnableKeyword("DVPS_FAST_HDR"); else material.DisableKeyword("DVPS_FAST_HDR");
            material.SetInt("_SnowSrcBlend",(int)(hdr?BlendMode.SrcAlpha:BlendMode.One));
            material.SetInt("_SnowDstBlend",(int)(hdr?BlendMode.OneMinusSrcAlpha:BlendMode.Zero));
            material.SetInt("_SnowAlphaSrcBlend",(int)(hdr?BlendMode.Zero:BlendMode.One));
            material.SetInt("_SnowAlphaDstBlend",(int)(hdr?BlendMode.One:BlendMode.Zero));
            material.SetInt("_SnowSpecAlphaDstBlend",(int)(hdr?BlendMode.OneMinusSrcAlpha:BlendMode.Zero));
            // HDR only needs the AO channel; native albedo/alpha are preserved by
            // blending. A half-size AO copy avoids a full-resolution color copy.
            commands.GetTemporaryRT(DiffuseId,hdr?Mathf.Max(1,camera.pixelWidth/2):-1,
                hdr?Mathf.Max(1,camera.pixelHeight/2):-1,0,hdr?FilterMode.Bilinear:FilterMode.Point,
                RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            commands.Blit(BuiltinRenderTextureType.GBuffer0,DiffuseId);
            commands.SetGlobalTexture(DiffuseId,DiffuseId);
            if(!hdr)
            {
                commands.GetTemporaryRT(SpecularId,-1,-1,0,FilterMode.Point,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
                commands.GetTemporaryRT(LightingId,-1,-1,0,FilterMode.Point,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
                commands.Blit(BuiltinRenderTextureType.GBuffer1,SpecularId);
                commands.Blit(lightingTarget,LightingId);
                commands.SetGlobalTexture(SpecularId,SpecularId);
                commands.SetGlobalTexture(LightingId,LightingId);
            }
            commands.SetGlobalTexture(NormalId,BuiltinRenderTextureType.GBuffer2);
            vehicles.Record(commands,camera,RailTracks);
            commands.SetGlobalTexture("_DVPSNoise",noiseTexture);
            commands.SetGlobalTexture("_DVPSNearHeight",near.Texture);
            commands.SetGlobalTexture("_DVPSFarHeight",far.Texture);
            commands.SetGlobalVector("_DVPSNearArea",near.Area);
            commands.SetGlobalVector("_DVPSFarArea",far.Area);
            commands.SetGlobalVector("_DVPSWorldOffset",worldOffset);
            commands.SetGlobalVector("_DVPSHeightOffsets",new Vector4(near.HeightOffset,far.HeightOffset,0,0));
            commands.SetGlobalFloat("_DVPSAmount",amount);
            commands.SetGlobalFloat("_DVPSHDR",camera.allowHDR ? 1f : 0f);
            var probe = RenderSettings.ambientProbe;
            probe.Evaluate(ambientDirections,ambientColors);
            var ambient = RenderSettings.ambientMode == AmbientMode.Flat ? RenderSettings.ambientLight : ambientColors[0];
            commands.SetGlobalColor("_DVPSAmbient",ambient);
            commands.SetGlobalMatrix("_DVPSInverseVP",
                (GL.GetGPUProjectionMatrix(camera.projectionMatrix,true)*camera.worldToCameraMatrix).inverse);
            commands.SetRenderTarget(new[] { new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer0),
                new RenderTargetIdentifier(BuiltinRenderTextureType.GBuffer1),
                new RenderTargetIdentifier(lightingTarget) },
                BuiltinRenderTextureType.CameraTarget);
            commands.DrawMesh(quad,Matrix4x4.identity,material,0,0);
            commands.ReleaseTemporaryRT(DiffuseId);
            if(!hdr) { commands.ReleaseTemporaryRT(SpecularId); commands.ReleaseTemporaryRT(LightingId); }
            vehicles.ReleaseFrame(commands);
        }

        private void Unbind()
        {
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
            exposureExclusions.Dispose();nextProxyRefresh=0;
            near.GeometryDirty=far.GeometryDirty=false;
            worldOffset=Vector3.zero;
            if (exposureCamera != null) DestroyResource(exposureCamera.gameObject);
            DestroyResource(near.Texture); DestroyResource(far.Texture);
            DestroyResource(material); DestroyResource(quad); DestroyResource(noiseTexture); noiseTexture=null;
            DestroyResource(terrainExposureMaterial);terrainExposureMaterial=null;
            if(terrainExposureCommands!=null) terrainExposureCommands.Dispose();terrainExposureCommands=null;
            exposureCamera = null; near.Texture = far.Texture = null; material = null; quad = null;
            near.Ready = far.Ready = false; nextLoadAttempt = renderRetryAfter = 0; failureLogged = false;
        }

        private static void DestroyResource(UnityEngine.Object resource)
        {
            if (resource == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(resource);
            else UnityEngine.Object.DestroyImmediate(resource);
        }
    }
}
