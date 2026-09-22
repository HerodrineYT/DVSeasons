using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowYardBatchVerification
    {
        const int Width=800,Height=600;
        const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static bool source;
        static bool topologyOnly;
        static bool hysteresisOnly;
        static bool orientedOnly;
        static bool dagOnly;
        static bool submissionOnly;
        static string assemblyOverride;
        static object Get(object owner,string name) {return owner.GetType().GetField(name,All).GetValue(owner);}
        static void Set(object owner,string name,object value) {owner.GetType().GetField(name,All).SetValue(owner,value);}
        static object Call(object owner,string name,params object[] args) {return owner.GetType().GetMethod(name,All).Invoke(owner,args);}
        static int Count(object owner,string name) {return (int)owner.GetType().GetProperty(name,All).GetValue(owner,null);}
        static void Require(bool ok,string message) {if(!ok)throw new InvalidOperationException(message);}
        public static void RunSource() {source=true;Run();}
        public static void RunTopology() {topologyOnly=true;Run();}
        public static void RunHysteresis() {hysteresisOnly=true;Run();}
        public static void RunOriented() {orientedOnly=true;Run();}
        public static void RunDag() {dagOnly=true;Run();}
        public static void RunSubmissionBenchmark() {submissionOnly=true;Run();}
        public static void RunSubmissionBaseline()
        {
            assemblyOverride=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_BASELINE_DLL");
            Require(!string.IsNullOrEmpty(assemblyOverride) && File.Exists(assemblyOverride),"Set DVSEASONS_VERIFY_BASELINE_DLL to the previous DVSeasons.dll");
            submissionOnly=true;Run();
        }
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME")??"F:/Steam/steamapps/common/Derail Valley";
            ResolveEventHandler resolver=(sender,args)=> {
                foreach(var directory in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var file=Path.Combine(directory,new AssemblyName(args.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;int result=0;
            try {Verify(Assembly.LoadFrom(assemblyOverride??Path.Combine(runtime,"DVSeasons.dll")),runtime);}
            catch(Exception error) {Debug.LogException(error);result=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(result);
        }
        static void Verify(Assembly mod,string runtime)
        {
            Require(SystemInfo.supportsInstancing,"Instancing unsupported");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            if(submissionOnly)
            {
                using(var submission=new Fixture(mod,runtime,128))submission.VerifySubmissionBenchmark();
                return;
            }
            if(dagOnly)
            {
                using(var dag=new Fixture(mod,runtime,32))dag.VerifyOverlapDag();
                return;
            }
            if(orientedOnly)
            {
                using(var oriented=new Fixture(mod,runtime,128))oriented.VerifyOrientedYard();
                return;
            }
            if(hysteresisOnly)
            {
                using(var topology=new Fixture(mod,runtime,128))topology.VerifyPhysicsJitter();
                return;
            }
            if(topologyOnly)
            {
                using(var topology=new Fixture(mod,runtime,128)) {topology.VerifyCameraTopology();topology.VerifyAlternatingPlans();}
                return;
            }
            using(var fixture=new Fixture(mod,runtime))
            {
                fixture.Compare("repeated cars with interleaved full/exclusion parts",true);
                fixture.Benchmark("separated mixed yard");
                fixture.VerifyPartialOverlapGroups();
                fixture.ChangeMotion();fixture.Compare("live motion and per-part captured snow frame",false);
                fixture.ChangeVisibility();fixture.Compare("enabled, active and forceRenderingOff changes",false);
                fixture.RestoreVisibility();fixture.ChangeFallbacks();fixture.Compare("mirrored, vertex-stream, skinned and static-batch fallbacks",false);
                fixture.Overlap();fixture.Compare("coincident car bodies and full/exclusion order barriers",false);
                fixture.Benchmark("overlapping car order barriers");
                fixture.VerifyOverlapCacheRecovery();
                fixture.Shift();fixture.Compare("large world shift with intersecting cars",false);
                fixture.UniqueMeshes();fixture.Compare("distinct singleton meshes",false);
                fixture.Benchmark("unique meshes and native fallbacks");
                fixture.VerifyDestroyedRenderer();
            }
            using(var fixture=new Fixture(mod,runtime,128))fixture.VerifyObjectLimits();
            Debug.Log("SNOW_YARD_BATCH_OK: production registry old/new scheduling; exact IDs, coverage, R8 slopes and half-float captured coordinates; repeated cars/8 distinct model parts; cutouts; interleaved full/exclusion order; partial overlapping groups and moving group recovery; 128-car limits0/96/64; moving captured frame; hidden renderers; native fallbacks; singleton meshes; origin shift; source="+source);
        }
        sealed class Fixture:IDisposable
        {
            readonly List<UnityEngine.Object> resources=new List<UnityEngine.Object>();
            readonly List<Transform> cars=new List<Transform>();
            readonly List<Vector3> originalPositions=new List<Vector3>();
            readonly List<Renderer> renderers=new List<Renderer>();
            readonly List<object> vehicles=new List<object>();
            readonly object registry,rails,repository;
            readonly Assembly mod;
            readonly GameObject fleet;
            readonly Camera camera;
            readonly RenderTexture target,surface,slope;
            readonly Texture2D read,fence;
            readonly CommandBuffer commands=new CommandBuffer {name="Registry yard batching equivalence"};
            readonly RenderTexture previous;
            readonly Mesh streamMesh;
            readonly int DataId=Shader.PropertyToID("_DVPSVehicleData"),SlopeId=Shader.PropertyToID("_DVPSVehicleSlope");
            T Keep<T>(T value) where T:UnityEngine.Object {resources.Add(value);return value;}
            public Fixture(Assembly mod,string runtime,int forcedCarCount=0)
            {
                this.mod=mod;previous=RenderTexture.active;QualitySettings.antiAliasing=0;RenderSettings.fog=false;
                int requestedCars;
                int carCount=forcedCarCount>0?forcedCarCount:int.TryParse(Environment.GetEnvironmentVariable("DVSEASONS_YARD_CARS"),out requestedCars)?Mathf.Clamp(requestedCars,32,128):32;
                int columns=carCount>32?16:8;
                fleet=Keep(new GameObject("Registry yard fleet"));
                camera=Keep(new GameObject("Registry yard camera")).AddComponent<Camera>();camera.enabled=false;
                camera.transform.position=new Vector3(0,carCount>32?80:34,-8);camera.transform.LookAt(Vector3.zero);
                camera.fieldOfView=50;camera.nearClipPlane=.1f;camera.farClipPlane=150;camera.renderingPath=RenderingPath.DeferredShading;
                camera.depthTextureMode=DepthTextureMode.Depth;camera.allowHDR=true;camera.allowMSAA=false;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                target=Keep(new RenderTexture(Width,Height,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                surface=Keep(new RenderTexture(Width,Height,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));surface.Create();
                slope=Keep(new RenderTexture(Width,Height,0,RenderTextureFormat.R8,RenderTextureReadWrite.Linear));slope.Create();
                read=Keep(new Texture2D(Width,Height,TextureFormat.RGBAFloat,false,true));fence=Keep(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                var opaque=Keep(new Material(Shader.Find("Standard")) {color=Color.gray});
                var alpha=Keep(new Texture2D(8,8,TextureFormat.RGBA32,false,true));alpha.filterMode=FilterMode.Point;alpha.wrapMode=TextureWrapMode.Repeat;
                var pixels=new Color[64];for(int y=0;y<8;y++)for(int x=0;x<8;x++)pixels[y*8+x]=new Color(1,1,1,(x+y)%2);alpha.SetPixels(pixels);alpha.Apply(false,false);
                var cutout=Keep(new Material(opaque));cutout.mainTexture=alpha;cutout.mainTextureScale=new Vector2(2,1);cutout.mainTextureOffset=new Vector2(.125f,0);
                cutout.SetFloat("_Mode",1);cutout.SetFloat("_Cutoff",.5f);cutout.SetOverrideTag("RenderType","TransparentCutout");cutout.EnableKeyword("_ALPHATEST_ON");cutout.renderQueue=2450;
                var meshes=new Mesh[8];
                for(int kind=0;kind<8;kind++)
                {
                    var normal=Quaternion.Euler(kind*8,0,0)*Vector3.up;
                    var mesh=Keep(new Mesh {name="Yard shared model part "+kind,vertices=new[]{new Vector3(-.28f,0,-.29f),new Vector3(-.28f,0,.29f),new Vector3(.28f,0,.29f),new Vector3(.28f,0,-.29f)},
                        normals=new[]{normal,normal,normal,normal},uv=new[]{Vector2.zero,Vector2.up,Vector2.one,Vector2.right},triangles=new[]{0,1,2,0,2,3}});mesh.RecalculateBounds();meshes[kind]=mesh;
                }
                for(int car=0;car<carCount;car++)
                {
                    var carRoot=new GameObject("Yard car "+car).transform;carRoot.SetParent(fleet.transform,false);
                    carRoot.localPosition=new Vector3((car%columns-(columns-1)*.5f)*3.8f,0,(car/columns-(carCount/columns-1)*.5f)*3.8f);cars.Add(carRoot);
                    originalPositions.Add(carRoot.localPosition);
                    for(int kind=0;kind<8;kind++)
                    {
                        var go=new GameObject("Yard part "+kind);go.transform.SetParent(carRoot,false);
                        go.transform.localPosition=new Vector3((kind%4-1.5f)*.7f,0,(kind/4-.5f)*.8f);go.transform.localRotation=Quaternion.Euler((car%3)*3,kind*2,kind%2*4);
                        go.AddComponent<MeshFilter>().sharedMesh=meshes[kind];var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=kind%2==0?opaque:cutout;renderers.Add(renderer);
                    }
                }
                // Native fallbacks are present at discovery time; later changes
                // also test invalidation of an already prepared shared group.
                renderers[0].transform.localScale=new Vector3(-1,1,1);
                streamMesh=Keep(UnityEngine.Object.Instantiate(meshes[1]));((MeshRenderer)renderers[1]).additionalVertexStreams=streamMesh;
                var staticRoot=new GameObject("Static-batched native parts");staticRoot.transform.SetParent(cars[0],false);
                renderers[3].transform.SetParent(staticRoot.transform,true);renderers[4].transform.SetParent(staticRoot.transform,true);StaticBatchingUtility.Combine(staticRoot);
                var skinObject=new GameObject("Skinned native snow part");skinObject.transform.SetParent(cars[0],false);skinObject.transform.localPosition=new Vector3(0,.05f,1.1f);
                var skin=skinObject.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=meshes[0];skin.sharedMaterial=opaque;skin.localBounds=skin.sharedMesh.bounds;renderers.Add(skin);
                var repoType=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);repository=Activator.CreateInstance(repoType,new object[]{runtime});
                var bundle=AssetBundle.LoadFromFile(Path.Combine(runtime,"AssetBundles/dvseasons_dv99"));Require(bundle!=null,"Fixture bundle unavailable");
                repoType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{bundle});
                registry=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SnowVehicleRegistry",true),true);rails=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.RailSnowTracks",true),true);
                var nativeField=registry.GetType().GetField("NativeMaterialsEnabled",All);
                if(nativeField!=null)nativeField.SetValue(registry,false);
                var combinedField=registry.GetType().GetField("CombinedMeshesEnabled",All);
                if(combinedField!=null)combinedField.SetValue(registry,false);
                var distantField=registry.GetType().GetField("DistantSurfacesEnabled",All);
                if(distantField!=null)distantField.SetValue(registry,false);
                // Exact scheduling parity is independent of the new capped-car
                // approximation; its geometry and limits have a separate suite.
                var volumesField=registry.GetType().GetField("ExclusionVolumesEnabled",All);
                if(volumesField!=null)volumesField.SetValue(registry,false);
                Require((bool)Call(registry,"Initialize",repository),"Registry snow material unavailable");
                if(source)((Material)Get(registry,"material")).shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/SnowVehicle.shader");
                Require(((Material)Get(registry,"material")).FindPass("INSTANCED_SNOW")==6,"Loaded shader has no full instancing pass");
                Set(registry,"ExclusionInstancingEnabled",true);
                foreach(var car in cars)Call(registry,"Register",car,null,null);
                foreach(var vehicle in (IList)Get(registry,"vehicles")) {vehicles.Add(vehicle);Prepare(vehicle);}
                camera.AddCommandBuffer(CameraEvent.BeforeReflections,commands);
            }
            void Prepare(object vehicle)
            {
                Call(registry,"RefreshParts",vehicle);Set(vehicle,"SnowReady",true);Set(vehicle,"Ready",true);
                Set(vehicle,"LocalBounds",new Bounds(Vector3.zero,new Vector3(3,2,3)));
                foreach(var part in (IList)Get(vehicle,"Parts"))
                {
                    var renderer=(Renderer)Get(part,"Renderer");
                    Set(part,"Interior",renderer.name=="Yard part 2" || renderer.name=="Yard part 5");
                }
                // Fixture classification changes after production discovery.
                Call(Get(vehicle,"PartCache"),"Build",Get(vehicle,"Parts"));
            }
            void Record(bool ordered)
            {
                Set(registry,"OrderedVehicleBatchesEnabled",ordered);commands.Clear();Call(registry,"Record",commands,camera,rails,false);
                commands.Blit(DataId,surface);commands.Blit(SlopeId,slope);Call(registry,"ReleaseFrame",commands);
            }
            Color[][] Draw(bool ordered)
            {
                Record(ordered);camera.Render();var result=new Color[2][];
                RenderTexture.active=surface;read.ReadPixels(new Rect(0,0,Width,Height),0,0);read.Apply(false,false);result[0]=read.GetPixels();
                RenderTexture.active=slope;read.ReadPixels(new Rect(0,0,Width,Height),0,0);read.Apply(false,false);result[1]=read.GetPixels();return result;
            }
            int Commands()
            {return Count(registry,"FrameDrawCount")-Count(registry,"FrameInstancedExclusionCount")-Count(registry,"FrameInstancedFullCount")+Count(registry,"FrameExclusionBatchCount")+Count(registry,"FrameFullBatchCount");}
            public void Compare(string phase,bool requireBatch,bool requirePartial=false,bool allowSparse=false)
            {
                var expected=Draw(false);int before=Commands();var actual=Draw(true);int after=Commands();
                int covered=0,exclusions=0,idChanges=0,slopeChanges=0;float localError=0;
                for(int i=0;i<expected[0].Length;i++)
                {
                    var a=expected[0][i];var b=actual[0][i];if(a.a>0)covered++;if(a.a<0)exclusions++;
                    if(a.a!=b.a)idChanges++;if(expected[1][i].r!=actual[1][i].r)slopeChanges++;
                    localError=Mathf.Max(localError,Mathf.Max(Mathf.Abs(a.r-b.r),Mathf.Max(Mathf.Abs(a.g-b.g),Mathf.Abs(a.b-b.b))));
                }
                var limiter=Get(registry,"ObjectLimiter");
                int limit=(int)limiter.GetType().GetProperty("Limit",All).GetValue(limiter,null);
                int minimumSnow=limit>0?Mathf.Max(1000,6000*Mathf.Min(limit,cars.Count)/cars.Count):6000;
                Require(covered>(allowSparse?10:minimumSnow) && exclusions>(allowSparse?0:1000),"Blank yard fixture for "+phase+": snow="+covered+" excluded="+exclusions);
                Require(idChanges==0 && slopeChanges==0 && localError<=.002f,phase+": ID/coverage="+idChanges+" slope="+slopeChanges+" local="+localError);
                if(requireBatch)
                {
                    Require(Count(registry,"FrameInstancedFullCount")>100 && Count(registry,"FrameInstancedExclusionCount")>25,"Fixture failed to batch full/excluded surfaces");
                    Require(after<before*.5f,"Separated repeated yard did not halve commands: "+before+" -> "+after);
                    Require(Count(registry,"FrameInstancedFullCount")<Count(registry,"FrameSnowDrawCount"),"Native-only renderers were not retained");
                }
                if(requirePartial)
                {
                    Require(Count(registry,"FrameInstancedFullCount")>0,"An overlap globally disabled full instancing: "+phase);
                    Require(after<before,"Independent overlapping groups did not reduce commands: "+phase+" "+before+" -> "+after);
                }
                Debug.Log("SNOW_YARD_DIFF: "+phase+" commands="+before+"->"+after+" full_instances="+Count(registry,"FrameInstancedFullCount")+" full_batches="+Count(registry,"FrameFullBatchCount")+
                    " exclusion_instances="+Count(registry,"FrameInstancedExclusionCount")+" exclusion_batches="+Count(registry,"FrameExclusionBatchCount")+" ID_changes="+idChanges+" slope_changes="+slopeChanges+" max_local_error="+localError.ToString("G9"));
            }
            public void ChangeMotion()
            {
                var moving=renderers[11];var frameType=mod.GetType("DVSeasons.Mod.SnowMovingVehiclePartFrame",true);
                var frame=Activator.CreateInstance(frameType,All,null,new object[]{cars[1],moving.transform},null);
                foreach(var part in (IList)Get(vehicles[1],"Parts"))if((Renderer)Get(part,"Renderer")==moving)Set(part,"MovingFrame",frame);
                moving.transform.localPosition+=new Vector3(.1f,.15f,-.07f);moving.transform.localRotation*=Quaternion.Euler(7,31,11);
                cars[4].localRotation=Quaternion.Euler(0,15,0);cars[6].localScale=new Vector3(1.02f,1.3f,.91f);
            }
            public void VerifyCameraTopology()
            {
                var lod=cars[3].gameObject.AddComponent<LODGroup>();
                lod.SetLODs(new[]{new LOD(.018f,new[]{renderers[24]}),new LOD(0f,new[]{renderers[25]})});
                lod.RecalculateBounds();Prepare(vehicles[3]);
                var scheduler=Get(registry,"orderedDraws");
                var originalPosition=camera.transform.position;var originalRotation=camera.transform.rotation;
                var originalFov=camera.fieldOfView;
                float originalLodBias=QualitySettings.lodBias;int lowestLod=int.MaxValue,highestLod=-1;
                Record(true);
                int components=Count(scheduler,"ComponentBuilds"),plans=Count(scheduler,"PlanBuilds"),hits=Count(scheduler,"PlanCacheHits");
                long pairs=(long)scheduler.GetType().GetProperty("BoundsPairTests",All).GetValue(scheduler,null);
                int minimumDraws=int.MaxValue,maximumDraws=0;
                for(int frame=0;frame<240;frame++)
                {
                    float phase=frame*.061f;
                    camera.transform.position=new Vector3(Mathf.Sin(phase)*26f,72f+Mathf.Sin(phase*.7f)*8f,Mathf.Cos(phase)*24f);
                    camera.transform.LookAt(new Vector3(Mathf.Cos(phase*.9f)*18f,0,0));
                    camera.fieldOfView=42f+Mathf.Sin(phase*.8f)*8f;
                    QualitySettings.lodBias=originalLodBias*((frame%12)<6?.2f:5f);
                    renderers[33].enabled=(frame%6)<3;
                    renderers[34].forceRenderingOff=(frame%8)<4;
                    renderers[35].gameObject.SetActive((frame%10)<5);
                    Record(true);
                    var lodSet=((IList)Get(vehicles[3],"Lods"))[0];int level=(int)Get(lodSet,"Current");
                    lowestLod=Mathf.Min(lowestLod,level);highestLod=Mathf.Max(highestLod,level);
                    minimumDraws=Mathf.Min(minimumDraws,Count(registry,"FrameDrawCount"));maximumDraws=Mathf.Max(maximumDraws,Count(registry,"FrameDrawCount"));
                    if(frame%40==0)Compare("moving camera, frustum, LOD and visibility frame="+frame,false,false,true);
                    Require(Count(scheduler,"ComponentBuilds")==components,"Stationary fleet rebuilt topology because camera/LOD/visibility changed at frame "+frame);
                }
                Require(maximumDraws-minimumDraws>40,"Camera fixture did not alter visible draw membership");
                Require(highestLod>lowestLod,"LOD fixture never changed its selected representation");
                QualitySettings.lodBias=originalLodBias;
                Require((long)scheduler.GetType().GetProperty("BoundsPairTests",All).GetValue(scheduler,null)==pairs,"Stationary camera tour repeated bounds pair tests");
                Require((int)Get(scheduler,"topologyCount")==cars.Count,"Topology membership discarded offscreen cars");
                Debug.Log("SNOW_STABLE_TOPOLOGY_OK: camera_frames=240 cars=128 component_rebuilds="+(Count(scheduler,"ComponentBuilds")-components)+
                    " pair_tests=0 plans="+(Count(scheduler,"PlanBuilds")-plans)+" hits="+(Count(scheduler,"PlanCacheHits")-hits)+
                    " visible_draws_min="+minimumDraws+" max="+maximumDraws+"; actual registry, frustum/LOD/visibility changes, pixel parity; not_game_FPS=true");
                renderers[33].enabled=true;renderers[34].forceRenderingOff=false;renderers[35].gameObject.SetActive(true);
                camera.transform.position=originalPosition;camera.transform.rotation=originalRotation;camera.fieldOfView=originalFov;
                // An interior/exclusion mesh is detached and animated past its
                // initial envelope. Validate before cross-car batching can occur.
                var detached=renderers[5];var parent=detached.transform.parent;var position=detached.transform.localPosition;
                detached.transform.SetParent(fleet.transform,true);detached.transform.position=renderers[13].transform.position;
                components=Count(scheduler,"ComponentBuilds");Compare("detached exclusion escapes stable envelope",false,false,true);
                Require(Count(scheduler,"ComponentBuilds")>components,"Escaped animated geometry did not invalidate topology");
                detached.transform.SetParent(parent,false);detached.transform.localPosition=position;
                // Deliberately exceed the stored depth-tolerance bucket. It must
                // grow immediately, then stay reusable on a return to the camera.
                camera.nearClipPlane=.005f;camera.farClipPlane=3000f;
                Compare("depth padding bucket promotion",false,false,true);
                components=Count(scheduler,"ComponentBuilds");
                camera.nearClipPlane=.1f;camera.farClipPlane=150f;Record(true);
                Require(Count(scheduler,"ComponentBuilds")==components,"Depth envelope shrank again with the returning camera");
                var oldPose=cars[9].localPosition;cars[9].position=cars[10].position;cars[9].localScale=new Vector3(-1.1f,1.2f,.85f);
                Compare("real car motion and mirrored scale",false,false,true);
                cars[9].localPosition=oldPose;cars[9].localScale=Vector3.one;Record(true);
                var topology=Get(vehicles[9],"Topology");var oldBounds=(Bounds)topology.GetType().GetProperty("World",All).GetValue(topology,null);
                var shift=new Vector3(5000,200,-7000);Shift();Compare("stable topology after world origin shift",false,false,true);
                var movedBounds=(Bounds)topology.GetType().GetProperty("World",All).GetValue(topology,null);
                Require((movedBounds.center-oldBounds.center-shift).sqrMagnitude<.001f,"World-origin shift left cached bounds behind");
                Debug.Log("SNOW_TOPOLOGY_SAFETY_OK: detached exclusion escape; depth bucket growth/reuse; real motion, mirrored scale and origin shifts preserve native snow pixels.");
            }
            public void VerifyAlternatingPlans()
            {
                var scheduler=Get(registry,"orderedDraws");
                for(int frame=0;frame<4;frame++) {renderers[33].enabled=(frame&1)==0;Record(true);}
                int components=Count(scheduler,"ComponentBuilds"),plans=Count(scheduler,"PlanBuilds"),hits=Count(scheduler,"PlanCacheHits");
                int uploads=Count(scheduler,"FullMatrixUploads"),elements=Count(scheduler,"FullMatrixElementsUploaded");
                for(int frame=0;frame<80;frame++)
                {renderers[33].enabled=(frame&1)==0;Record(true);}
                Require(Count(scheduler,"ComponentBuilds")==components,"Visibility toggle invalidated stationary topology");
                Require(Count(scheduler,"PlanBuilds")==plans && Count(scheduler,"PlanCacheHits")-hits==80,"Two warmed visibility plans were rebuilt");
                Require(Count(scheduler,"FullMatrixUploads")==uploads && Count(scheduler,"FullMatrixElementsUploaded")==elements,"Alternating stationary plans uploaded unchanged matrices");
                renderers[33].enabled=true;Compare("cached external visibility plan",false,false,true);
                renderers[33].enabled=false;Compare("cached interior visibility plan",false,false,true);
                // Moving cars and snow IDs are data, not a cached draw plan.
                cars[8].localPosition+=new Vector3(.07f,0,0);
                Set(vehicles[8],"Slot",200);Compare("cached plan with live vehicle matrix and snow ID",false,false,true);
                renderers[33].enabled=true;
                Debug.Log("SNOW_TWO_PLAN_CACHE_OK: alternating_frames=80 warmed_plans=2 plan_builds=0 cache_hits=80 component_builds=0 matrix_uploads=0; native pixel parity and live matrix/ID changes verified.");
            }
            public void VerifyPhysicsJitter()
            {
                // Build the initial envelope while every car is already rotated,
                // as in a real yard. Include static-batch/skinned native fallbacks.
                for(int car=0;car<cars.Count;car++)
                {cars[car].localRotation=Quaternion.Euler(0,37f,0);Prepare(vehicles[car]);}
                var scheduler=Get(registry,"orderedDraws");
                var positions=new Vector3[cars.Count];var rotations=new Quaternion[cars.Count];
                for(int car=0;car<cars.Count;car++){positions[car]=cars[car].localPosition;rotations[car]=cars[car].localRotation;}
                Record(true);int components=Count(scheduler,"ComponentBuilds");
                long pairs=(long)scheduler.GetType().GetProperty("BoundsPairTests",All).GetValue(scheduler,null);
                int uploads=Count(scheduler,"FullMatrixUploads");
                for(int frame=0;frame<240;frame++)
                {
                    for(int car=0;car<cars.Count;car++)
                    {
                        float jitter=Mathf.Sin(frame*.31f+car)*.002f;
                        cars[car].localPosition=positions[car]+new Vector3(jitter,jitter*.15f,-jitter*.7f);
                        cars[car].localRotation=rotations[car]*Quaternion.Euler(jitter*.5f,jitter*2f,jitter*.2f);
                    }
                    Record(true);
                    Require(Count(scheduler,"ComponentBuilds")==components,"Millimetre pose jitter republished the topology at frame "+frame);
                    if(frame%60==0)Compare("rotated parked fleet with physical jitter frame="+frame,false,false,true);
                }
                Require((long)scheduler.GetType().GetProperty("BoundsPairTests",All).GetValue(scheduler,null)==pairs,"Jitter performed new overlap pair tests");
                Require(Count(scheduler,"FullMatrixUploads")>uploads,"Jitter test incorrectly froze actual draw matrices");
                Debug.Log("SNOW_PHYSICS_JITTER_OK: cars=128 yaw=37 frames=240 component_builds=0 pair_tests=0; draw_matrix_uploads="+(Count(scheduler,"FullMatrixUploads")-uploads)+"; exact native pixels at four poses");
                for(int car=0;car<cars.Count;car++){cars[car].localPosition=positions[car];cars[car].localRotation=rotations[car];}
                // A real carriage moves into and through another car. Hysteresis
                // cannot miss the merge just because it reuses a padded box.
                components=Count(scheduler,"ComponentBuilds");
                var start=cars[9].position;var end=cars[10].position;
                for(int step=1;step<=100;step++)
                {
                    cars[9].position=Vector3.Lerp(start,end,step/100f);Record(true);
                    if(step%25==0)Compare("real motion across component boundary step="+step,false,false,true);
                }
                int movedBuilds=Count(scheduler,"ComponentBuilds")-components;
                Require(movedBuilds>0 && movedBuilds<40,"Moving car hysteresis did not preserve bounded publications: "+movedBuilds);
                Require(((Bounds)Get(vehicles[9],"FrameBounds")).Intersects((Bounds)Get(vehicles[10],"FrameBounds")),"Overlapping cars escaped the scheduler graph");
                var detached=renderers[18];detached.transform.SetParent(fleet.transform,true);
                detached.transform.position=renderers[26].transform.position;
                Compare("detached animated exclusion without world AABB roundtrip",false,false,true);
                camera.nearClipPlane=.005f;camera.farClipPlane=3000f;
                Compare("hysteresis includes changed depth tolerance",false,false,true);
                Shift();Compare("hysteresis refreshed on origin shift",false,false,true);
                Debug.Log("SNOW_MOVING_HYSTERESIS_OK: movement_frames=100 component_builds="+movedBuilds+"; real overlap, detached exclusion, depth tolerance and world shift preserve native pixels");
            }
            public void VerifyOrientedYard()
            {
                var rotation=Quaternion.Euler(0,37,0);
                for(int i=0;i<cars.Count;i++)
                {
                    cars[i].localPosition=rotation*new Vector3((i%16-7.5f)*4.2f,0,(i/16-3.5f)*13f);
                    cars[i].localRotation=rotation;cars[i].localScale=new Vector3(1,1,8);
                    Prepare(vehicles[i]);
                }
                camera.transform.position=new Vector3(0,180,-100);camera.transform.LookAt(Vector3.zero);camera.farClipPlane=600;
                var scheduler=Get(registry,"orderedDraws");
                Set(registry,"OrientedVehicleBatchesEnabled",false);Record(true);
                int aabbCommands=Commands(),aabbLargest=Count(scheduler,"LargestComponent"),aabbComponents=Count(scheduler,"ComponentCount");
                Set(registry,"OrientedVehicleBatchesEnabled",true);Record(true);
                int obbCommands=Commands(),obbLargest=Count(scheduler,"LargestComponent"),obbComponents=Count(scheduler,"ComponentCount");
                Require(obbCommands<aabbCommands,"OBB failed to release parallel-car batches: "+aabbCommands+" -> "+obbCommands);
                Require(obbLargest<aabbLargest && obbComponents>aabbComponents,"OBB failed to split inflated AABB components");
                Compare("long rotated parallel yard with OBB dependencies",false,false,true);
                Debug.Log("SNOW_OBB_COMMANDS_OK: cars=128 commands="+aabbCommands+"->"+obbCommands+" largest="+aabbLargest+"->"+obbLargest+" components="+aabbComponents+"->"+obbComponents);
                BenchmarkOriented();
                int builds=Count(scheduler,"ComponentBuilds");
                var positions=new Vector3[cars.Count];for(int i=0;i<cars.Count;i++)positions[i]=cars[i].localPosition;
                for(int frame=0;frame<120;frame++)
                {
                    for(int i=0;i<cars.Count;i++)cars[i].localPosition=positions[i]+Vector3.right*(Mathf.Sin(frame*.31f+i)*.002f);
                    Record(true);
                }
                Require(Count(scheduler,"ComponentBuilds")==builds,"Long rotated yard lost jitter cache");
                // An animated exclusion can leave its local envelope while its
                // world AABB still overlaps the old one. Guard actual geometry.
                var animated=renderers[8*20+2];animated.transform.position=renderers[8*21].transform.position;
                Compare("OBB live animated exclusion crosses neighbouring car",false,false,true);
                // The renderer survives a streamed mesh replacement. Native
                // fallback must cover its new geometry before parts discovery.
                var changed=renderers[8*30];var filter=changed.GetComponent<MeshFilter>();var old=filter.sharedMesh;
                var replacement=Keep(UnityEngine.Object.Instantiate(old));
                var vertices=replacement.vertices;for(int i=0;i<vertices.Length;i++)vertices[i]+=new Vector3(4.2f,0,0);
                replacement.vertices=vertices;replacement.RecalculateBounds();filter.sharedMesh=replacement;
                Compare("OBB replaced mesh before parts refresh",false,false,true);
                filter.sharedMesh=old;
                cars[40].position=cars[41].position;
                Compare("OBB genuine coincident cars preserve native order",false,false,true);
                ChangeFallbacks();Compare("OBB runtime mirror and custom vertex streams",false,false,true);
                Shift();Compare("OBB world origin relocation",false,false,true);
                Debug.Log("SNOW_OBB_REGISTRY_OK: parallel fleet command reduction, 120 jitter frames without topology builds, native pixels, animated/replaced geometry, overlap, native fallbacks and origin shift");
            }

            void DagLayout(params Vector3[] positions)
            {
                fleet.transform.position=Vector3.zero;
                camera.transform.position=new Vector3(0,17,-7);camera.transform.LookAt(Vector3.zero);camera.farClipPlane=150;
                camera.nearClipPlane=.1f;camera.fieldOfView=50;
                for(int car=0;car<cars.Count;car++)
                {
                    cars[car].localPosition=car>0 && car<=positions.Length?positions[car-1]:new Vector3(1000+car*30,0,1000);
                    cars[car].localRotation=Quaternion.identity;cars[car].localScale=Vector3.one;
                    foreach(var renderer in cars[car].GetComponentsInChildren<Renderer>(true))
                    {renderer.enabled=true;renderer.forceRenderingOff=false;renderer.gameObject.SetActive(true);}
                    Prepare(vehicles[car]);
                }
                Set(registry,"OrientedVehicleBatchesEnabled",true);
            }

            public void VerifySubmissionBenchmark()
            {
                // Only the version-7 public/private fixture surface is used here.
                // A separate Unity process can load the previous DLL while using
                // the same bundles, scene, camera, objects and benchmark schedule.
                Require(cars.Count==128,"Submission benchmark requires the same 128-car workload");
                Set(registry,"OrientedVehicleBatchesEnabled",true);
                camera.transform.position=new Vector3(0,100,-32);camera.transform.LookAt(Vector3.zero);
                camera.fieldOfView=50;camera.nearClipPlane=.1f;camera.farClipPlane=600;
                Vector3[] diamond={new Vector3(0,0,-1.6f),new Vector3(-2,0,0),new Vector3(2,0,0),new Vector3(0,0,1.6f)};
                for(int i=0;i<cars.Count;i++)
                {
                    int group=i/4;
                    cars[i].localPosition=new Vector3((group%8-3.5f)*8.5f,0,(group/8-1.5f)*6.3f)+diamond[i%4];
                    cars[i].localRotation=Quaternion.identity;cars[i].localScale=Vector3.one;
                    Prepare(vehicles[i]);
                }
                Compare("submission 128-car / 32 independent diamonds",false,false,true);
                var scheduler=Get(registry,"orderedDraws");
                Require(Count(scheduler,"LargestComponent")==4 && Count(scheduler,"ComponentCount")==32,
                    "Submission benchmark diamond groups overlap each other or are disconnected");
                int diamondCommands=Commands();
                Benchmark("DAG branched fleet");
                Debug.Log("SNOW_SUBMISSION_LAYOUT: layout=32_diamonds cars=128 ordered_commands="+diamondCommands+
                    " baseline="+(!string.IsNullOrEmpty(assemblyOverride))+SubmissionGuardCounters()+" assembly="+mod.Location);

                Quaternion rotation=Quaternion.Euler(0,37,0);
                for(int i=0;i<cars.Count;i++)
                {
                    cars[i].localPosition=rotation*new Vector3((i%16-7.5f)*4.2f,0,(i/16-3.5f)*13f);
                    cars[i].localRotation=rotation;cars[i].localScale=new Vector3(1,1,8);
                    Prepare(vehicles[i]);
                }
                camera.transform.position=new Vector3(0,180,-100);camera.transform.LookAt(Vector3.zero);
                Compare("submission 128-car long parallel yard yaw37",false,false,true);
                int rotatedCommands=Commands();
                Benchmark("DAG rotated parallel fleet");
                Debug.Log("SNOW_SUBMISSION_LAYOUT: layout=long_parallel_yaw37 cars=128 ordered_commands="+rotatedCommands+
                    " baseline="+(!string.IsNullOrEmpty(assemblyOverride))+SubmissionGuardCounters()+" assembly="+mod.Location);
                Debug.Log("SNOW_SUBMISSION_BENCHMARK_OK: identical native/ordered scenes and GPU pixel parity; five alternating 40-frame CPU+GPU trials plus 150-frame CPU submission; previous DLL selected only by explicit environment path; no game FPS claim.");
            }

            string SubmissionGuardCounters()
            {
                // Optional diagnostics preserve compatibility with the prior
                // runtime. Reflection runs after timing, never inside a frame.
                return " oriented_fast="+OptionalRegistryCount("FrameOrientedFastCount")+
                    " oriented_detailed="+OptionalRegistryCount("FrameOrientedDetailedCount")+
                    " mesh_bounds_reads="+OptionalRegistryCount("FrameOrientedMeshBoundsReadCount");
            }

            string OptionalRegistryCount(string name)
            {
                var type=registry.GetType();var property=type.GetProperty(name,All);
                if(property!=null)return Convert.ToString(property.GetValue(registry,null));
                var field=type.GetField(name,All);return field!=null?Convert.ToString(field.GetValue(registry)):"n/a";
            }

            bool DagOverlap(int first,int second)
            {
                if(!((Bounds)Get(vehicles[first],"FrameBounds")).Intersects((Bounds)Get(vehicles[second],"FrameBounds")))return false;
                var a=Get(vehicles[first],"Oriented");var b=Get(vehicles[second],"Oriented");
                var firstBox=a.GetType().GetProperty("World",All).GetValue(a,null);
                var secondBox=b.GetType().GetProperty("World",All).GetValue(b,null);
                return (bool)firstBox.GetType().GetMethod("Intersects",All).Invoke(null,new[]{firstBox,secondBox});
            }

            void VerifyDagPlan(string phase,int independentA=-1,int independentB=-1,int emptyBridge=-1)
            {
                var scheduler=Get(registry,"orderedDraws");var plan=Get(scheduler,"activePlan");
                Require(plan!=null && (bool)Get(plan,"Valid"),"DAG fixture needs a complete cached production plan: "+phase);
                var streams=(IList)Get(scheduler,"streamPool");var counts=(IList)Get(plan,"StreamCounts");
                var batches=(IList)Get(plan,"Batches");int count=(int)Get(plan,"Count");
                Require(counts.Count==cars.Count,"DAG plan omitted empty/offscreen fleet streams: "+phase);
                var ranks=new int[cars.Count][];var minimum=new int[cars.Count];var maximum=new int[cars.Count];
                int expectedRequests=0,actualRequests=0;bool sharedIndependent=false;
                for(int i=0;i<cars.Count;i++)
                {
                    int requests=(int)counts[i];expectedRequests+=requests;ranks[i]=new int[requests];
                    for(int j=0;j<requests;j++)ranks[i][j]=-1;
                    minimum[i]=int.MaxValue;maximum[i]=-1;
                }
                for(int batchIndex=0;batchIndex<count;batchIndex++)
                {
                    var batch=batches[batchIndex];var draws=(Array)Get(batch,"Draws");int used=(int)Get(batch,"Count");
                    int pass=(int)Get(Get(batch,"Key"),"Pass");bool haveA=false,haveB=false;
                    for(int drawIndex=0;drawIndex<used;drawIndex++)
                    {
                        var draw=draws.GetValue(drawIndex);var stream=Get(draw,"Stream");int index=(int)Get(draw,"Index");
                        int owner=-1;for(int i=0;i<cars.Count;i++)if(ReferenceEquals(streams[i],stream)){owner=i;break;}
                        Require(owner>=0 && index>=0 && index<ranks[owner].Length,"DAG plan references unknown stream/request: "+phase);
                        Require(ranks[owner][index]<0,"DAG plan issued one request twice: "+phase);
                        ranks[owner][index]=batchIndex;minimum[owner]=Math.Min(minimum[owner],batchIndex);maximum[owner]=Math.Max(maximum[owner],batchIndex);actualRequests++;
                        if(owner==independentA)haveA=true;if(owner==independentB)haveB=true;
                    }
                    if(pass==0 && haveA && haveB)sharedIndependent=true;
                }
                Require(actualRequests==expectedRequests,"DAG dropped pending requests: "+phase+" "+expectedRequests+" -> "+actualRequests);
                var reachable=new bool[cars.Count,cars.Count];int edges=0,activeEdges=0,activeStreams=0;
                for(int i=0;i<cars.Count;i++)
                {
                    if(ranks[i].Length>0)activeStreams++;
                    var successors=(IList)Get(streams[i],"Successors");int expectedSuccessors=0;
                    for(int j=i+1;j<cars.Count;j++)if(DagOverlap(i,j))
                    {
                        edges++;expectedSuccessors++;
                        Require(successors.Contains(j),"Geometric DAG lost an offscreen edge: "+phase);
                        if(ranks[i].Length>0 && ranks[j].Length>0){reachable[i,j]=true;activeEdges++;}
                    }
                    Require(successors.Count==expectedSuccessors,"Geometric DAG gained a spurious edge: "+phase);
                }
                Require(Count(scheduler,"ActiveStreamCount")==activeStreams && Count(scheduler,"EmptyStreamCount")==cars.Count-activeStreams,
                    "Active stream diagnostic disagrees with cached requests: "+phase);
                Require(Count(scheduler,"ActiveEdgeCount")==activeEdges && Count(scheduler,"EmptyBridgeEdgeCount")==edges-activeEdges,
                    "Active edge diagnostic disagrees with independent induced graph: "+phase);
                for(int k=0;k<cars.Count;k++)for(int i=0;i<k;i++)if(reachable[i,k])
                    for(int j=k+1;j<cars.Count;j++)if(reachable[k,j])reachable[i,j]=true;
                for(int i=0;i<cars.Count;i++)
                {
                    var requests=(IList)Get(streams[i],"Requests");
                    for(int request=0;request<ranks[i].Length;request++)
                    {
                        Require(ranks[i][request]>=0,"DAG skipped a native draw: "+phase);
                        int pass=(int)Get(Get(requests[request],"Key"),"Pass");
                        for(int later=request+1;later<ranks[i].Length;later++)
                        {
                            int nextPass=(int)Get(Get(requests[later],"Key"),"Pass");
                            if(pass==0 || nextPass==0)Require(ranks[i][request]<ranks[i][later],"DAG moved a full draw across a native part/exclusion barrier: "+phase);
                        }
                    }
                    for(int j=i+1;j<cars.Count;j++)if(reachable[i,j] && ranks[i].Length>0 && ranks[j].Length>0)
                        Require(maximum[i]<minimum[j],"DAG violated direct/transitive overlap ordering "+i+" -> "+j+": "+phase);
                }
                if(independentA>=0)
                {
                    Require(!reachable[independentA,independentB],"Independent fork fixture accidentally has a directed path: "+phase);
                    Require(sharedIndependent,"Independent branches never shared a full-snow batch: "+phase);
                }
                if(emptyBridge>=0)Require(ranks[emptyBridge].Length==0,"Invisible bridge unexpectedly has draw requests: "+phase);
                Debug.Log("SNOW_DAG_PLAN_OK: "+phase+" edges="+edges+" active_edges="+activeEdges+" active_streams="+activeStreams+
                    " requests="+actualRequests+" batches="+count+" independent_full_batch="+sharedIndependent+" empty_bridge="+emptyBridge);
            }

            public void VerifyOverlapDag()
            {
                DagLayout(new Vector3(-2,0,0),new Vector3(2,0,0),Vector3.zero);
                Compare("DAG A->C and B->C, independent A/B",false,false,true);
                Require(DagOverlap(1,3) && DagOverlap(2,3) && !DagOverlap(1,2),"Fork layout does not have exactly the intended local overlaps");
                VerifyDagPlan("two parents join",1,2);
                var scheduler=Get(registry,"orderedDraws");int plans=Count(scheduler,"PlanBuilds"),hits=Count(scheduler,"PlanCacheHits");
                for(int i=0;i<20;i++)Record(true);
                Require(Count(scheduler,"PlanBuilds")==plans && Count(scheduler,"PlanCacheHits")-hits==20,"Stable DAG plan was rebuilt");
                ChangeMotion();Set(vehicles[1],"Slot",199);
                Compare("DAG fork live captured frame and snow IDs",false,false,true);VerifyDagPlan("captured frame after cached plan",1,2);

                DagLayout(new Vector3(0,0,-1.6f),new Vector3(-2,0,0),new Vector3(2,0,0),new Vector3(0,0,1.6f));
                Compare("DAG branching diamond with independent middle streams",false,false,true);
                Require(DagOverlap(1,2) && DagOverlap(1,3) && DagOverlap(2,4) && DagOverlap(3,4) && !DagOverlap(2,3),"Diamond fixture is missing intended fork/join edges");
                VerifyDagPlan("diamond",2,3);
                // Alternate visibility while preserving graph topology. Two LRU
                // plans must retain the complete DAG signature, not just counts.
                var hidden=renderers[3*8+1];
                for(int i=0;i<4;i++){hidden.enabled=(i&1)==0;Record(true);}
                plans=Count(scheduler,"PlanBuilds");hits=Count(scheduler,"PlanCacheHits");
                for(int i=0;i<40;i++){hidden.enabled=(i&1)==0;Record(true);}
                Require(Count(scheduler,"PlanBuilds")==plans && Count(scheduler,"PlanCacheHits")-hits==40,"Alternating DAG visibility patterns missed two-entry LRU");
                Compare("DAG diamond hidden cutout cached plan",false,false,true);VerifyDagPlan("diamond hidden cutout");hidden.enabled=true;

                DagLayout(new Vector3(-3.75f,0,0),new Vector3(-1.25f,0,0),new Vector3(1.25f,0,0),new Vector3(3.75f,0,0));
                Compare("DAG chain preserves all full/exclusion barriers",false,false,true);
                Require(DagOverlap(1,2) && DagOverlap(2,3) && DagOverlap(3,4) && !DagOverlap(1,3),"Chain fixture did not isolate consecutive overlaps");
                VerifyDagPlan("four-node chain");

                DagLayout(new Vector3(-2.5f,0,0),Vector3.zero,new Vector3(2.5f,0,0));
                Record(true);
                foreach(var renderer in cars[2].GetComponentsInChildren<Renderer>(true))renderer.enabled=false;
                Compare("DAG invisible empty bridge imposes no dependency",false,false,true);
                Require(DagOverlap(1,2) && DagOverlap(2,3) && !DagOverlap(1,3),"Invisible bridge lost its graph path");
                VerifyDagPlan("empty bridge removed from active graph",1,3,2);
                foreach(var renderer in cars[2].GetComponentsInChildren<Renderer>(true))renderer.enabled=true;
                Compare("DAG invisible bridge restored",false,false,true);VerifyDagPlan("restored bridge");
                var bridgeParts=cars[2].GetComponentsInChildren<Renderer>(true);
                for(int i=0;i<4;i++){foreach(var renderer in bridgeParts)renderer.enabled=(i&1)==0;Record(true);}
                plans=Count(scheduler,"PlanBuilds");hits=Count(scheduler,"PlanCacheHits");int components=Count(scheduler,"ComponentBuilds");
                for(int i=0;i<20;i++)
                {
                    bool visible=(i&1)==0;foreach(var renderer in bridgeParts)renderer.enabled=visible;
                    Compare("DAG bridge visibility LRU "+i,false,false,true);
                    VerifyDagPlan("bridge visibility LRU "+i,visible?-1:1,visible?-1:3,visible?-1:2);
                }
                Require(Count(scheduler,"PlanBuilds")==plans && Count(scheduler,"PlanCacheHits")-hits==20 && Count(scheduler,"ComponentBuilds")==components,
                    "Empty bridge visibility invalidated geometry or two-entry plans");
                foreach(var renderer in bridgeParts)renderer.enabled=true;

                DagLayout(new Vector3(-2,0,0),new Vector3(2,0,0));
                Record(true);
                Bounds first=(Bounds)Get(vehicles[1],"FrameBounds"),second=(Bounds)Get(vehicles[2],"FrameBounds");
                cars[2].position+=Vector3.right*(first.max.x-second.min.x-.0001f);
                Prepare(vehicles[2]);
                Compare("DAG touching padded bounds retain dependency",false,false,true);
                first=(Bounds)Get(vehicles[1],"FrameBounds");second=(Bounds)Get(vehicles[2],"FrameBounds");
                Require(Mathf.Abs(first.max.x-second.min.x)<.002f && DagOverlap(1,2),"Touching-box fixture does not touch published bounds");
                VerifyDagPlan("touching boxes");

                // Move a joining car between independent parents without
                // changing request counts/keys. The two DAGs differ by edges.
                DagLayout(new Vector3(-2,0,0),new Vector3(2,0,0),new Vector3(-2,0,0));
                Compare("DAG changed edge set first parent",false,false,true);VerifyDagPlan("edge change first parent");
                cars[3].position=cars[2].position;
                Compare("DAG changed edge set second parent",false,false,true);VerifyDagPlan("edge change second parent");
                ChangeFallbacks();Compare("DAG mirrored and custom-stream native fallbacks",false,false,true);VerifyDagPlan("native fallbacks");
                Shift();Compare("DAG world-origin shift with native fallbacks",false,false,true);VerifyDagPlan("origin shift");
                Debug.Log("SNOW_OVERLAP_DAG_OK: production registry fork/join/diamond/chain/touching graphs, invisible empty bridge, exact native GPU IDs/coverage/R8 slopes, captured coordinates, all requests accounted once, dependency and intra-stream order, independent full batches, edge-only graph changes, two-entry visibility LRU and native fallbacks.");
            }
            public void ChangeVisibility() {renderers[17].enabled=false;renderers[18].forceRenderingOff=true;renderers[19].gameObject.SetActive(false);}
            public void RestoreVisibility() {renderers[17].enabled=true;renderers[18].forceRenderingOff=false;renderers[19].gameObject.SetActive(true);}
            public void ChangeFallbacks() {renderers[25].transform.localScale=new Vector3(-1,1,1);((MeshRenderer)renderers[26]).additionalVertexStreams=streamMesh;}
            public void Overlap()
            {
                cars[12].position=cars[11].position;cars[12].rotation=cars[11].rotation;
                renderers[13*8+1].transform.position=renderers[13*8+2].transform.position;
                renderers[14*8+5].transform.position=renderers[14*8+6].transform.position;
            }
            void ConnectedConsists()
            {
                int rows=(cars.Count+7)/8;
                for(int i=0;i<cars.Count;i++)
                {
                    // Geometry spans about 2.7 m in X, so 2 m spacing overlaps
                    // actual visible renderer bounds, not only LocalBounds.
                    cars[i].localPosition=new Vector3((i%8-3.5f)*2f,0,(i/8-(rows-1)*.5f)*3.8f);
                    cars[i].localRotation=Quaternion.identity;cars[i].localScale=Vector3.one;
                }
            }
            void RestoreLayout()
            {
                for(int i=0;i<cars.Count;i++)
                {cars[i].localPosition=originalPositions[i];cars[i].localRotation=Quaternion.identity;cars[i].localScale=Vector3.one;}
            }
            public void VerifyPartialOverlapGroups()
            {
                // Coincident parts are intentionally assigned different IDs and
                // car-local coordinates; any illegal cross-car order is visible.
                cars[12].position=cars[11].position;cars[12].rotation=cars[11].rotation;
                Compare("one coincident pair plus independent fleet",false,true);
                Require(((Bounds)Get(vehicles[11],"FrameBounds")).Intersects((Bounds)Get(vehicles[12],"FrameBounds")),"Coincident-pair fixture failed to intersect real frame bounds");
                Benchmark("one overlap pair plus independent fleet");
                RestoreLayout();ConnectedConsists();
                Compare("multiple connected eight-car consists",false,true);
                for(int row=0;row<cars.Count/8;row++)
                    for(int index=1;index<8;index++)
                    {
                        var before=(Bounds)Get(vehicles[row*8+index-1],"FrameBounds");
                        var after=(Bounds)Get(vehicles[row*8+index],"FrameBounds");
                        Require(before.Intersects(after),"Connected consist missed actual padded frame overlap at car "+(row*8+index));
                    }
                Require(!((Bounds)Get(vehicles[0],"FrameBounds")).Intersects((Bounds)Get(vehicles[8],"FrameBounds")),"Connected consists are not independent across rows");
                Benchmark("several connected consists");
                // A bridge car changes the overlap component without rebuilding
                // Parts, registering cars, or waiting for a timer.
                var saved=cars[7].localPosition;cars[7].position=Vector3.Lerp(cars[0].position,cars[8].position,.5f);
                cars[7].localScale=new Vector3(1,1,4);
                Compare("moving bridge merges two overlap components",false,true);
                Require(((Bounds)Get(vehicles[7],"FrameBounds")).Intersects((Bounds)Get(vehicles[0],"FrameBounds")) &&
                    ((Bounds)Get(vehicles[7],"FrameBounds")).Intersects((Bounds)Get(vehicles[8],"FrameBounds")),"Moving bridge did not join two real components");
                cars[7].localPosition=saved;cars[7].localScale=Vector3.one;
                Compare("moving bridge restores separate overlap components",false,true);
                // Alter membership and draw count while overlap components stay.
                var renderer=renderers[9*8];renderer.enabled=false;
                Compare("overlap groups with one newly hidden part",false,true);renderer.enabled=true;
                Compare("overlap groups with restored part",false,true);
                RestoreLayout();Compare("all overlap components separated again",true);
                Debug.Log("SNOW_YARD_PARTIAL_GROUPS_OK: one coincident pair, independent connected consists, moving component bridge, hidden/restored part; pixel IDs/slope/local coordinates preserved and partial full instancing retained.");
            }
            public void VerifyObjectLimits()
            {
                Require(cars.Count==128,"Object-limit benchmark must contain 128 cars");
                foreach(var vehicle in vehicles)Set(vehicle,"RollingStock",true);
                var limiter=Get(registry,"ObjectLimiter");Call(limiter,"InvalidateMembership");
                ConnectedConsists();
                foreach(int limit in new[]{0,96,64})
                {
                    limiter.GetType().GetProperty("Limit",All).SetValue(limiter,limit,null);
                    Call(limiter,"Update",camera,Get(registry,"vehicles"));
                    Require(limit==0 || Count(limiter,"SelectedCount")==limit,"Wrong rolling-stock limit selection "+limit+": "+Count(limiter,"SelectedCount"));
                    Compare("128-car connected yard limit="+limit,false,limit==0 || limit>cars.Count/2);
                    int full=Count(registry,"FrameSnowDrawCount"),cheap=Count(registry,"FrameExclusionDrawCount");
                    Require(full>0 && cheap>0,"Limited yard lost full or exclusion path: "+limit);
                    if(limit>0 && limit<=cars.Count/2)
                        Require(Count(registry,"FrameInstancedFullCount")==0 && Count(registry,"FrameFullBatchCount")==0 && Commands()>=full,
                            "Exclusion-dominated capped yard did not use the native full-draw path: "+limit);
                    if(limit>0)Require(full<=limit*6+1 && cheap>=(cars.Count-limit)*8,"Limit did not convert capped cars to exclusion geometry: limit="+limit+" full="+full+" cheap="+cheap);
                    Benchmark("128-car connected yard limit="+limit);
                    Debug.Log("SNOW_YARD_LIMIT_WORK: limit="+limit+" cars=128 selected="+(limit==0?128:Count(limiter,"SelectedCount"))+" full_submeshes="+full+" exclusion_submeshes="+cheap+" actual_commands="+Commands()+"; synthetic fixture, not game FPS.");
                }
                limiter.GetType().GetProperty("Limit",All).SetValue(limiter,0,null);
                Compare("128-car unlimited mode restored",false,true);
            }
            public void VerifyOverlapCacheRecovery()
            {
                var location=cars[12].position;var rotation=cars[12].rotation;
                cars[12].localPosition=originalPositions[12];
                cars[12].localRotation=Quaternion.identity;
                Compare("separated again after moving out of overlap",true);
                cars[12].position=location;cars[12].rotation=rotation;
                Compare("overlap reappears while independent cars keep batching",false,true);
                var scheduler=Get(registry,"orderedDraws");int builds=Count(scheduler,"PlanBuilds"),hits=Count(scheduler,"PlanCacheHits");
                for(int frame=0;frame<12;frame++)Record(true);
                Require(Count(scheduler,"PlanBuilds")==builds && Count(scheduler,"PlanCacheHits")>hits,"Stable partial-overlap draw plan was not reused");
                Debug.Log("SNOW_YARD_OVERLAP_RECOVERY_OK: separate/rejoin immediately preserves pixels and partial batching; stationary overlap plan reused.");
            }
            public void Shift() {var shift=new Vector3(5000,200,-7000);fleet.transform.position+=shift;camera.transform.position+=shift;}
            public void UniqueMeshes()
            {
                foreach(var renderer in renderers)
                {
                    var meshRenderer=renderer as MeshRenderer;if(meshRenderer==null || meshRenderer.isPartOfStaticBatch)continue;
                    var filter=meshRenderer.GetComponent<MeshFilter>();filter.sharedMesh=Keep(UnityEngine.Object.Instantiate(filter.sharedMesh));
                }
                foreach(var vehicle in vehicles)Prepare(vehicle);
            }
            public void VerifyDestroyedRenderer()
            {
                // Streaming can destroy a native renderer while its managed Part
                // remains until the next budgeted car refresh. `is MeshRenderer`
                // is still true, but native property access must never occur.
                var stale=renderers[0];var peer=(MeshRenderer)renderers[8];
                stale.GetComponent<MeshFilter>().sharedMesh=peer.GetComponent<MeshFilter>().sharedMesh;
                Prepare(vehicles[0]);Prepare(vehicles[1]);
                UnityEngine.Object.DestroyImmediate(stale);
                Require(stale==null && stale is MeshRenderer,"Missing destroyed Unity wrapper fixture");
                Set(registry,"exclusionMeshUsesDirty",true);
                Compare("streamed renderer destroyed before cache rebuild",false);
                Require(Count(registry,"FrameSnowDrawCount")>100,"Destroyed detail removed fleet snow");
                Debug.Log("SNOW_DESTROYED_RENDERER_OK: removed renderer skipped during shared-mesh rebuild; fleet continues rendering");
            }
            void Drain() {RenderTexture.active=target;fence.ReadPixels(new Rect(0,0,1,1),0,0,false);}
            void BenchmarkOriented()
            {
                var aabb=new double[5];var obb=new double[5];
                for(int trial=0;trial<5;trial++)
                    for(int run=0;run<2;run++)
                    {
                        bool oriented=((trial+run)&1)!=0;Set(registry,"OrientedVehicleBatchesEnabled",oriented);
                        Time(true,12,true);(oriented?obb:aabb)[trial]=Time(true,40,true);
                    }
                Array.Sort(aabb);Array.Sort(obb);
                Set(registry,"OrientedVehicleBatchesEnabled",false);Time(true,12,false);double oldCpu=Time(true,150,false);
                Set(registry,"OrientedVehicleBatchesEnabled",true);Time(true,12,false);double newCpu=Time(true,150,false);
                Debug.Log("SNOW_OBB_RENDER_BENCH: AABB_ms="+aabb[2].ToString("F4")+" OBB_ms="+obb[2].ToString("F4")+
                    " AABB_CPU_ms="+oldCpu.ToString("F4")+" OBB_CPU_ms="+newCpu.ToString("F4")+
                    " median_5_alternating_40_frame_trials=Record_camera_GPUcompletion; synthetic_not_game_FPS=true");
            }
            double Time(bool ordered,int count,bool render)
            {
                Drain();var watch=System.Diagnostics.Stopwatch.StartNew();
                for(int i=0;i<count;i++) {Record(ordered);if(render)camera.Render();}
                if(render)Drain();watch.Stop();return watch.Elapsed.TotalMilliseconds/count;
            }
            public void Benchmark(string phase)
            {
                if(Environment.GetEnvironmentVariable("DVSEASONS_SKIP_BENCHMARKS")=="1")return;
                Time(false,12,true);Time(true,12,true);var old=new double[5];var next=new double[5];
                var scheduler=Get(registry,"orderedDraws");
                int cachedBefore=Count(scheduler,"PlanCacheHits"),buildsBefore=Count(scheduler,"PlanBuilds");
                int componentsBefore=Count(scheduler,"ComponentBuilds"),uploadsBefore=Count(scheduler,"FullMatrixUploads"),elementsBefore=Count(scheduler,"FullMatrixElementsUploaded");
                for(int trial=0;trial<5;trial++)
                    if((trial&1)==0) {old[trial]=Time(false,40,true);next[trial]=Time(true,40,true);}
                    else {next[trial]=Time(true,40,true);old[trial]=Time(false,40,true);}
                Array.Sort(old);Array.Sort(next);
                double oldCpu=Time(false,150,false),newCpu=Time(true,150,false);
                if(phase!="unique meshes and native fallbacks" && Count(registry,"FrameInstancedFullCount")>0)
                {
                    Require(Count(scheduler,"PlanCacheHits")>cachedBefore,"Stable fleet did not reuse its ordered draw plan");
                    Require(Count(scheduler,"PlanBuilds")==buildsBefore,"Stable fleet rebuilt its ordered draw plan");
                    Require(Count(scheduler,"ComponentBuilds")==componentsBefore,"Stable fleet rebuilt overlap components: "+phase);
                    Require(Count(scheduler,"FullMatrixUploads")==uploadsBefore && Count(scheduler,"FullMatrixElementsUploaded")==elementsBefore,
                        "Stationary fleet uploaded unchanged instance frames/IDs: "+phase);
                }
                else Require(Count(scheduler,"PlanCacheHits")==cachedBefore && Count(scheduler,"PlanBuilds")==buildsBefore &&
                    Count(scheduler,"ComponentBuilds")==componentsBefore && Count(scheduler,"FullMatrixUploads")==uploadsBefore,
                    "Native fallback unexpectedly processed ordered batches: "+phase);
                Debug.Log("SNOW_YARD_RENDER_BENCH: "+phase+" cars="+cars.Count+" old_ms="+old[2].ToString("F4")+" ordered_ms="+next[2].ToString("F4")+
                    " record_old_ms="+oldCpu.ToString("F4")+" record_ordered_ms="+newCpu.ToString("F4")+
                    " plan_cache_hits="+(Count(scheduler,"PlanCacheHits")-cachedBefore)+" plan_builds="+(Count(scheduler,"PlanBuilds")-buildsBefore)+
                    " component_builds="+(Count(scheduler,"ComponentBuilds")-componentsBefore)+
                    " matrix_uploads="+(Count(scheduler,"FullMatrixUploads")-uploadsBefore)+" matrix_elements="+(Count(scheduler,"FullMatrixElementsUploaded")-elementsBefore)+
                    " render=Record_plus_native_camera_plus_GPU_completion median_of_5_alternating_40_frame_trials record=150_CPU_only not_game_FPS=true");
            }
            public void Dispose()
            {
                RenderTexture.active=previous;camera.RemoveCommandBuffer(CameraEvent.BeforeReflections,commands);commands.Dispose();camera.targetTexture=null;
                ((IDisposable)registry).Dispose();((IDisposable)rails).Dispose();((IDisposable)repository).Dispose();
                foreach(var resource in resources)if(resource!=null)UnityEngine.Object.DestroyImmediate(resource);
            }
        }
    }
}
