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
    // Historical rejected experiment: select its preserved DLL with
    // DVSEASONS_VERIFY_NATIVE_EXPERIMENT_DLL. Production has no fast-path entry.
    // Record both paths inside the SAME real OnPreRender callback. In particular,
    // the native visibility path never gets a warmup render on spawn/teleport.
    public static class SnowNativeVisibilityVerification
    {
        const int Width=800,Height=600;
        const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static int checks;
        static object Get(object o,string name) {return o.GetType().GetField(name,All).GetValue(o);}
        static void Set(object o,string name,object value) {o.GetType().GetField(name,All).SetValue(o,value);}
        static object Call(object o,string name,params object[] args) {return o.GetType().GetMethod(name,All).Invoke(o,args);}
        static int Count(object o,string name) {return (int)o.GetType().GetProperty(name,All).GetValue(o,null);}
        static void Require(bool ok,string message) {checks++;if(!ok)throw new InvalidOperationException(message);}
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME")??"F:/steam/steamapps/common/Derail Valley";
            ResolveEventHandler resolver=(sender,args)=>{
                foreach(var directory in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var path=Path.Combine(directory,new AssemblyName(args.Name).Name+".dll");if(File.Exists(path))return Assembly.LoadFrom(path);}return null;};
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;int result=0;
            try
            {
                var candidate=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_NATIVE_EXPERIMENT_DLL");
                if(string.IsNullOrEmpty(candidate))candidate=Path.Combine(runtime,"DVSeasons.dll");
                Require(File.Exists(candidate),"Native visibility experiment DLL does not exist: "+candidate);
                var mod=Assembly.LoadFrom(candidate);
                Require(mod.GetType("DVSeasons.Mod.SnowVehicleRegistry",true).GetMethod("RecordAfterCull",All)!=null,
                    "Production no longer contains the rejected native visibility experiment. Set DVSEASONS_VERIFY_NATIVE_EXPERIMENT_DLL to "+
                    Path.Combine(root,"artifacts/verification/full-opt-native-experimental/DVSeasons.dll")+" to reproduce its historical measurements.");
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                using(var fixture=new Fixture(mod,runtime))fixture.Verify();
                Debug.Log("SNOW_NATIVE_VISIBILITY_OK checks="+checks+"; actual OnPreRender ID/coverage/slope parity, first frame/new spawn, teleport/rotation, visibility/layer/reactivation, LOD, skinned/native, secondary/exposure-like camera; timings are synthetic CPU recording, not game FPS.");
            }
            catch(Exception error) {Debug.LogException(error);result=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(result);
        }
        sealed class Fixture:IDisposable
        {
            readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
            readonly List<Renderer> renderers=new List<Renderer>();
            readonly List<object> vehicles=new List<object>();
            readonly object registry,rails,repository;
            readonly Camera camera,auxiliary;
            readonly GameObject fleet;
            readonly Material native;
            readonly RenderTexture target;
            readonly RenderTexture[] snapshots=new RenderTexture[4];
            readonly Texture2D read;
            readonly CommandBuffer commands=new CommandBuffer {name="Native visibility comparison"};
            readonly int dataId=Shader.PropertyToID("_DVPSVehicleData"),slopeId=Shader.PropertyToID("_DVPSVehicleSlope");
            readonly MethodInfo record,afterCull;
            bool compare=true,optimized,renderAuxiliaryInside;
            Exception callbackFailure;
            int callbackCount,expectedDraws,actualDraws,visibleRenderers;
            long originalTicks,optimizedTicks;
            long benchmarkNativeCulled,benchmarkVisible;
            Renderer body,lodNear,lodFar,skin;
            LODGroup lod;
            T Keep<T>(T item) where T:UnityEngine.Object {owned.Add(item);return item;}
            public Fixture(Assembly mod,string runtime)
            {
                QualitySettings.antiAliasing=0;RenderSettings.fog=false;QualitySettings.lodBias=1;
                fleet=Keep(new GameObject("Visibility fixture fleet"));
                camera=Keep(new GameObject("Main deferred fixture camera")).AddComponent<Camera>();camera.enabled=false;
                camera.renderingPath=RenderingPath.DeferredShading;camera.depthTextureMode=DepthTextureMode.Depth;
                camera.allowHDR=true;camera.allowMSAA=false;camera.clearFlags=CameraClearFlags.SolidColor;
                camera.backgroundColor=Color.black;camera.fieldOfView=55;camera.nearClipPlane=.1f;camera.farClipPlane=300;
                camera.transform.position=new Vector3(0,15,-30);camera.transform.LookAt(new Vector3(0,0,8));
                target=Texture(Width,Height,24,RenderTextureFormat.ARGBHalf);camera.targetTexture=target;
                auxiliary=Keep(new GameObject("Secondary exposure-like camera")).AddComponent<Camera>();auxiliary.enabled=false;
                auxiliary.renderingPath=RenderingPath.Forward;auxiliary.targetTexture=Texture(64,64,24,RenderTextureFormat.ARGB32);
                auxiliary.transform.position=new Vector3(400,30,-30);auxiliary.transform.LookAt(new Vector3(400,30,-100));
                auxiliary.cullingMask=1;
                for(int i=0;i<4;i++)snapshots[i]=Texture(Width,Height,0,i%2==0?RenderTextureFormat.ARGBHalf:RenderTextureFormat.R8);
                read=Keep(new Texture2D(Width,Height,TextureFormat.RGBAFloat,false,true));
                native=Keep(new Material(Shader.Find("Standard")) {color=Color.gray});
                repository=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true),new object[]{runtime});
                var bundle=AssetBundle.LoadFromFile(Path.Combine(runtime,"AssetBundles/dvseasons_dv99"));Require(bundle!=null,"Fixture bundle missing");
                repository.GetType().GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{bundle});
                registry=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SnowVehicleRegistry",true),true);
                rails=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.RailSnowTracks",true),true);
                Require((bool)Call(registry,"Initialize",repository),"Registry shader unavailable");
                record=registry.GetType().GetMethod("Record",All);afterCull=registry.GetType().GetMethod("RecordAfterCull",All);
                Require(afterCull!=null,"Candidate RecordAfterCull entry is missing");
                var central=Car("Central car",Vector3.zero);
                body=Cube(central,"Body",Vector3.zero,new Vector3(3,2,5));
                Cube(central,"Interior exclusion",new Vector3(0,2,0),new Vector3(1,.2f,1));
                var lodRoot=new GameObject("LOD assembly");lodRoot.transform.SetParent(central,false);lodRoot.transform.localPosition=new Vector3(4,0,0);
                lodNear=Cube(lodRoot.transform,"LOD near",Vector3.zero,new Vector3(2,2,3));
                lodFar=Cube(lodRoot.transform,"LOD far",Vector3.zero,new Vector3(1.7f,1.7f,2.7f));
                lod=lodRoot.AddComponent<LODGroup>();lod.SetLODs(new[]{new LOD(.075f,new[]{lodNear}),new LOD(0,new[]{lodFar})});lod.RecalculateBounds();
                var skinRoot=new GameObject("Skinned part");skinRoot.transform.SetParent(central,false);skinRoot.transform.localPosition=new Vector3(-4,0,0);
                var bone=new GameObject("Skinned bone").transform;bone.SetParent(skinRoot.transform,false);
                var skinMesh=Keep(UnityEngine.Object.Instantiate(body.GetComponent<MeshFilter>().sharedMesh));
                var weights=new BoneWeight[skinMesh.vertexCount];for(int i=0;i<weights.Length;i++)weights[i]=new BoneWeight {boneIndex0=0,weight0=1};
                skinMesh.boneWeights=weights;skinMesh.bindposes=new[]{Matrix4x4.identity};
                var sr=skinRoot.AddComponent<SkinnedMeshRenderer>();sr.sharedMesh=skinMesh;sr.bones=new[]{bone};sr.rootBone=bone;
                sr.sharedMaterial=native;sr.localBounds=sr.sharedMesh.bounds;sr.updateWhenOffscreen=false;skin=sr;renderers.Add(sr);
                // Hundreds of distant parts share wide per-car bounds so normal
                // per-part native checks are meaningful in the record benchmark.
                for(int car=0;car<24;car++)
                {
                    var rootCar=Car("Scattered car "+car,new Vector3((car%6-2.5f)*7,0,(car/6)*9+12));
                    for(int part=0;part<24;part++)
                        Cube(rootCar,"Part "+part,new Vector3((part%4-1.5f)*4,part%3*.4f,(part/4)*12),new Vector3(1.2f,.5f,1.2f));
                }
                foreach(var v in (IList)Get(registry,"vehicles"))Prepare(v);
                camera.AddCommandBuffer(CameraEvent.BeforeReflections,commands);Camera.onPreRender+=BeforeRender;
            }
            RenderTexture Texture(int width,int height,int depth,RenderTextureFormat format)
            {var texture=Keep(new RenderTexture(width,height,depth,format,RenderTextureReadWrite.Linear));texture.Create();return texture;}
            Transform Car(string name,Vector3 position)
            {
                var root=new GameObject(name).transform;root.SetParent(fleet.transform,false);root.localPosition=position;
                Call(registry,"Register",root,null,null);return root;
            }
            Renderer Cube(Transform parent,string name,Vector3 position,Vector3 scale)
            {
                var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(parent,false);
                go.transform.localPosition=position;go.transform.localScale=scale;
                var renderer=go.GetComponent<MeshRenderer>();renderer.sharedMaterial=native;renderer.shadowCastingMode=ShadowCastingMode.Off;
                renderers.Add(renderer);return renderer;
            }
            void Prepare(object vehicle)
            {
                Call(registry,"RefreshParts",vehicle);Set(vehicle,"SnowReady",true);Set(vehicle,"Ready",true);
                // Deliberately conservative: keep scattered car roots visible.
                Set(vehicle,"LocalBounds",new Bounds(new Vector3(0,0,30),new Vector3(25,8,90)));
                foreach(var part in (IList)Get(vehicle,"Parts"))
                    if(((Renderer)Get(part,"Renderer")).name=="Interior exclusion")Set(part,"Interior",true);
                if(!vehicles.Contains(vehicle))vehicles.Add(vehicle);
            }
            void BeforeRender(Camera current)
            {
                if(current!=camera)return;
                try
                {
                    Require(Camera.current==camera,"OnPreRender did not expose the actual camera");
                    if(renderAuxiliaryInside)auxiliary.Render();
                    commands.Clear();callbackCount++;visibleRenderers=0;
                    foreach(var renderer in renderers)if(renderer!=null && renderer.isVisible)visibleRenderers++;
                    if(compare)
                    {
                        Record(record,0);expectedDraws=Count(registry,"FrameDrawCount");
                        Record(afterCull,2);actualDraws=Count(registry,"FrameDrawCount");
                    }
                    else
                    {
                        long start=System.Diagnostics.Stopwatch.GetTimestamp();
                        Record(optimized?afterCull:record,-1);
                        long elapsed=System.Diagnostics.Stopwatch.GetTimestamp()-start;
                        if(optimized)
                        {
                            optimizedTicks+=elapsed;
                            benchmarkNativeCulled+=Count(registry,"FrameNativeCulledCount");
                            benchmarkVisible+=visibleRenderers;
                        }
                        else originalTicks+=elapsed;
                    }
                }
                catch(Exception error) {callbackFailure=error;}
            }
            void Record(MethodInfo method,int capture)
            {
                method.Invoke(registry,new object[]{commands,camera,rails,true});
                if(capture>=0) {commands.Blit(dataId,snapshots[capture]);commands.Blit(slopeId,snapshots[capture+1]);}
                Call(registry,"ReleaseFrame",commands);
            }
            void Render()
            {callbackFailure=null;int before=callbackCount;camera.Render();if(callbackFailure!=null)throw callbackFailure;Require(callbackCount==before+1,"Main OnPreRender callback absent");}
            Color[] Read(RenderTexture texture)
            {
                var prior=RenderTexture.active;try {RenderTexture.active=texture;read.ReadPixels(new Rect(0,0,Width,Height),0,0);read.Apply(false,false);return read.GetPixels();}
                finally {RenderTexture.active=prior;}
            }
            void Compare(string scenario,bool expectVisible=true)
            {
                compare=true;Render();var expected=Read(snapshots[0]);var actual=Read(snapshots[2]);
                var expectedSlope=Read(snapshots[1]);var actualSlope=Read(snapshots[3]);
                int covered=0,ids=0,slopes=0;float local=0;
                for(int i=0;i<expected.Length;i++)
                {
                    if(expected[i].a>0)covered++;
                    if(expected[i].a!=actual[i].a)ids++;
                    if(expectedSlope[i].r!=actualSlope[i].r)slopes++;
                    local=Mathf.Max(local,Mathf.Max(Mathf.Abs(expected[i].r-actual[i].r),Mathf.Max(Mathf.Abs(expected[i].g-actual[i].g),Mathf.Abs(expected[i].b-actual[i].b))));
                }
                Require(!expectVisible || covered>100,"Blank baseline for "+scenario);
                Require(ids==0 && slopes==0 && local<=.002f,scenario+" native visibility drops snow: IDs="+ids+" slopes="+slopes+" local="+local+" native-visible="+visibleRenderers+" draws="+expectedDraws+"/"+actualDraws);
                Debug.Log("NATIVE_VISIBILITY_PARITY scenario="+scenario+" pixels="+covered+" ids="+ids+" slopes="+slopes+" max_local="+local+" draws="+expectedDraws+"/"+actualDraws+" native-visible="+visibleRenderers);
            }
            public void Verify()
            {
                Compare("first camera frame with newly created renderers");
                var newCar=Car("Spawned on next frame",new Vector3(0,1,-6));Cube(newCar,"New spawn body",Vector3.zero,new Vector3(3,2,3));
                Prepare(((IList)Get(registry,"vehicles"))[((IList)Get(registry,"vehicles")).Count-1]);Compare("first frame of new car");
                camera.transform.position=new Vector3(35,12,55);camera.transform.LookAt(new Vector3(0,0,35));Compare("first teleport/rotation frame");
                camera.transform.Rotate(0,160,0);Compare("first turn away",false);
                camera.transform.position=new Vector3(0,10,-20);camera.transform.LookAt(new Vector3(0,0,5));Compare("first frame returning to fleet");
                body.enabled=false;Compare("disabled renderer");body.enabled=true;Compare("reactivated renderer");
                body.gameObject.SetActive(false);Compare("inactive object");body.gameObject.SetActive(true);Compare("reactivated object");
                body.gameObject.layer=9;camera.cullingMask=~(1<<9);Compare("camera layer excluded");camera.cullingMask=-1;Compare("camera layer restored");body.gameObject.layer=0;
                body.forceRenderingOff=true;Compare("force rendering off");body.forceRenderingOff=false;Compare("force rendering restored");
                camera.transform.position=new Vector3(0,4,-7);camera.transform.LookAt(new Vector3(0,0,1));Compare("near LOD first frame");
                camera.transform.position=new Vector3(0,35,-130);camera.transform.LookAt(new Vector3(0,0,25));Compare("far LOD first frame");
                skin.transform.position=new Vector3(0,5,-2);Compare("skinned renderer moved into view");
                auxiliary.transform.position=new Vector3(300,10,0);auxiliary.transform.LookAt(new Vector3(300,10,100));auxiliary.Render();Compare("auxiliary camera looked away before main");
                auxiliary.transform.position=new Vector3(0,20,35);auxiliary.transform.LookAt(new Vector3(0,0,35));auxiliary.Render();Compare("auxiliary camera saw main-hidden objects");
                renderAuxiliaryInside=true;Compare("nested exposure-like camera before registry recording");renderAuxiliaryInside=false;
                var light=Keep(new GameObject("Shadow light")).AddComponent<Light>();light.type=LightType.Directional;light.shadows=LightShadows.Hard;light.transform.rotation=Quaternion.Euler(45,20,0);
                foreach(var renderer in renderers)renderer.shadowCastingMode=ShadowCastingMode.On;
                Compare("shadow visibility remains conservative");
                Benchmark();
                Debug.Log("NATIVE_VISIBILITY_LIMITATION: fixture has no baked Umbra occlusion data; it validates current-frame native visibility at real camera culling, not game-yard occlusion performance.");
            }
            void Benchmark()
            {
                if(Environment.GetEnvironmentVariable("DVSEASONS_SKIP_BENCHMARKS")=="1")return;
                compare=false;
                foreach(var renderer in renderers)renderer.shadowCastingMode=ShadowCastingMode.Off;
                camera.transform.position=new Vector3(0,15,-30);camera.transform.LookAt(new Vector3(0,0,30));camera.fieldOfView=60;
                BenchmarkView("front-full");
                camera.transform.Rotate(0,180,0);BenchmarkView("looking-away");
                camera.transform.position=new Vector3(0,3,-6);camera.transform.LookAt(new Vector3(0,0,20));camera.fieldOfView=15;
                BenchmarkView("narrow-partial");
            }
            void BenchmarkView(string scenario)
            {
                // Give all previously used cameras repeated culls which exclude
                // the fleet, so an earlier auxiliary view cannot keep it visible.
                auxiliary.cullingMask=0;
                for(int i=0;i<32;i++){auxiliary.Render();optimized=i%2!=0;Render();}
                const int samples=100;originalTicks=optimizedTicks=benchmarkNativeCulled=benchmarkVisible=0;
                for(int i=0;i<samples*2;i++){optimized=i%2!=0;Render();}
                Debug.Log("NATIVE_VISIBILITY_CPU_BENCH scenario="+scenario+
                    " original_ms="+(originalTicks*1000d/System.Diagnostics.Stopwatch.Frequency/samples).ToString("F4")+
                    " candidate_ms="+(optimizedTicks*1000d/System.Diagnostics.Stopwatch.Frequency/samples).ToString("F4")+
                    " renderers="+renderers.Count+" native-visible-avg="+(benchmarkVisible/(double)samples).ToString("F1")+
                    " native-culled-avg="+(benchmarkNativeCulled/(double)samples).ToString("F1")+
                    " samples="+samples+" warmup-camera-renders=32 scope=CPU_record_inside_OnPreRender synthetic=true game_FPS=false");
            }
            public void Dispose()
            {
                Camera.onPreRender-=BeforeRender;camera.RemoveCommandBuffer(CameraEvent.BeforeReflections,commands);commands.Dispose();
                camera.targetTexture=null;auxiliary.targetTexture=null;
                ((IDisposable)registry).Dispose();((IDisposable)rails).Dispose();((IDisposable)repository).Dispose();
                for(int i=owned.Count-1;i>=0;i--)if(owned[i]!=null)UnityEngine.Object.DestroyImmediate(owned[i]);
            }
        }
    }
}
