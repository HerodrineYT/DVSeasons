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
    // Compare the actual deferred surface-ID target before and after batching.
    // Shader source mode is useful while iterating; Run also verifies the packed
    // shader kept its instancing variant in the distribution AssetBundle.
    public static class SnowExclusionInstancingVerification
    {
        const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static bool sourceShaders;
        static object Get(object owner,string name) {return owner.GetType().GetField(name,All).GetValue(owner);}
        static void Set(object owner,string name,object value) {owner.GetType().GetField(name,All).SetValue(owner,value);}
        static object Call(object owner,string name,params object[] args) {return owner.GetType().GetMethod(name,All).Invoke(owner,args);}
        static int Count(object owner,string name) {return (int)owner.GetType().GetProperty(name,All).GetValue(owner,null);}
        static void Require(bool ok,string message) {if(!ok)throw new InvalidOperationException(message);}
        public static void RunSourceShaders() {sourceShaders=true;Run();}
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            ResolveEventHandler resolver=(sender,args)=>
            {
                foreach(var directory in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {
                    string file=Path.Combine(directory,new AssemblyName(args.Name).Name+".dll");
                    if(File.Exists(file))return Assembly.LoadFrom(file);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;
            int result=0;
            try {Verify(Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll")),runtime);}
            catch(Exception exception) {Debug.LogException(exception);result=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(result);
        }
        static void Verify(Assembly mod,string runtime)
        {
            Require(SystemInfo.supportsInstancing,"GPU instancing not supported by verification backend");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            QualitySettings.antiAliasing=0;RenderSettings.fog=false;
            var resources=new List<UnityEngine.Object>();
            var fleet=new GameObject("Exclusion instancing fleet");resources.Add(fleet);
            var camera=new GameObject("Exclusion instancing camera").AddComponent<Camera>();resources.Add(camera.gameObject);
            camera.transform.position=new Vector3(0,40,0);camera.transform.rotation=Quaternion.Euler(90,0,0);
            // Unity's built-in deferred path falls back to forward for
            // orthographic cameras, where BeforeReflections never executes.
            camera.orthographic=false;camera.fieldOfView=40;camera.nearClipPlane=.1f;camera.farClipPlane=100;
            camera.renderingPath=RenderingPath.DeferredShading;camera.depthTextureMode=DepthTextureMode.Depth;
            camera.allowMSAA=false;camera.allowHDR=true;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
            var target=new RenderTexture(600,400,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);target.Create();resources.Add(target);camera.targetTexture=target;
            var surface=new RenderTexture(600,400,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);surface.Create();resources.Add(surface);
            var opaque=new Material(Shader.Find("Standard"));opaque.color=Color.gray;resources.Add(opaque);
            var cutout=new Material(opaque);resources.Add(cutout);
            var alpha=new Texture2D(8,8,TextureFormat.RGBA32,false,true);resources.Add(alpha);alpha.filterMode=FilterMode.Point;
            var pixels=new Color[64];for(int y=0;y<8;y++)for(int x=0;x<8;x++)pixels[y*8+x]=new Color(1,1,1,(x+y)%2);
            alpha.SetPixels(pixels);alpha.Apply(false,false);
            cutout.mainTexture=alpha;cutout.SetFloat("_Mode",1);cutout.SetFloat("_Cutoff",.5f);
            cutout.SetOverrideTag("RenderType","TransparentCutout");cutout.EnableKeyword("_ALPHATEST_ON");cutout.renderQueue=2450;
            cutout.mainTextureScale=new Vector2(2,1);cutout.mainTextureOffset=new Vector2(.125f,0);
            var renderers=new List<Renderer>();
            for(int i=0;i<200;i++)
            {
                var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);cube.name="Repeated exclusion "+i;
                cube.transform.SetParent(fleet.transform,false);
                cube.transform.localPosition=new Vector3((i%20-9.5f)*2,0,(i/20-4.5f)*2);
                cube.transform.localScale=new Vector3(1.3f,.8f,1.3f);
                cube.transform.localRotation=Quaternion.Euler(0,(i%3)*15,0);
                var renderer=cube.GetComponent<MeshRenderer>();renderer.sharedMaterial=i%4==0?cutout:opaque;renderers.Add(renderer);
            }
            // Native-only paths must remain native, including mirrored culling,
            // deformed skinned meshes, custom vertex streams and static batches.
            renderers[0].transform.localScale=new Vector3(-1.3f,.8f,1.3f);
            var streamMesh=UnityEngine.Object.Instantiate(renderers[1].GetComponent<MeshFilter>().sharedMesh);resources.Add(streamMesh);
            ((MeshRenderer)renderers[1]).additionalVertexStreams=streamMesh;
            var skinObject=new GameObject("Skinned exclusion");skinObject.transform.SetParent(fleet.transform,false);
            skinObject.transform.localPosition=new Vector3(-18,1,11);
            var skin=skinObject.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=renderers[2].GetComponent<MeshFilter>().sharedMesh;
            skin.sharedMaterial=opaque;skin.localBounds=skin.sharedMesh.bounds;renderers.Add(skin);
            var staticRoot=new GameObject("Static batched exclusions");staticRoot.transform.SetParent(fleet.transform,false);
            renderers[3].transform.SetParent(staticRoot.transform,true);renderers[4].transform.SetParent(staticRoot.transform,true);
            StaticBatchingUtility.Combine(staticRoot);

            var repositoryType=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
            var repository=Activator.CreateInstance(repositoryType,new object[]{runtime});
            var bundle=AssetBundle.LoadFromFile(Path.Combine(runtime,"AssetBundles/dvseasons_dv99"));
            repositoryType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{bundle});
            var registry=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SnowVehicleRegistry",true),true);
            var rails=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.RailSnowTracks",true),true);
            var commands=new CommandBuffer {name="Exclusion instancing parity"};
            camera.AddCommandBuffer(CameraEvent.BeforeReflections,commands);
            try
            {
                Require((bool)Call(registry,"Initialize",repository),"Vehicle snow shader initialization failed");
                if(sourceShaders)((Material)Get(registry,"material")).shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/SnowVehicle.shader");
                Call(registry,"Register",fleet.transform,null,null);
                var vehicle=((IList)Get(registry,"vehicles"))[0];Call(registry,"RefreshParts",vehicle);
                var baseline=Render(registry,rails,camera,commands,surface,false);
                int logical=Count(registry,"FrameExclusionDrawCount");
                Require(logical>=195,"Fixture did not cover at least 195 visible exclusion submeshes: "+logical);
                var instanced=Render(registry,rails,camera,commands,surface,true);
                Compare(baseline,instanced,"all exclusions");
                int instances=Count(registry,"FrameInstancedExclusionCount"),batches=Count(registry,"FrameExclusionBatchCount");
                Require(instances>=190 && batches<=3,"Repeated exclusions did not batch: instances="+instances+", batches="+batches);
                Require(instances<logical,"Unsafe renderer fallbacks were unexpectedly instanced");
                int commandsAfter=logical-instances+batches;
                Require(commandsAfter<logical/10,"Exclusion draw commands were not reduced by at least 90%");
                Debug.Log("SNOW_EXCLUSION_BATCH_WORK: "+logical+" exclusion submeshes -> "+commandsAfter+" draw commands; "+instances+" instances in "+batches+" batches.");
                double baselineCpu=Benchmark(registry,rails,camera,commands,false);
                double instancedCpu=Benchmark(registry,rails,camera,commands,true);
                Debug.Log("SNOW_EXCLUSION_RECORD_CPU_MS: DrawRenderer="+baselineCpu.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+
                    "; instanced="+instancedCpu.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+
                    "; per Record after warmup, 100 iterations, command submission only; excludes GPU execution and is not an FPS measurement.");
                if(Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_SNOW_GPU_BENCH")=="1")
                    BenchmarkRenderedFrames(registry,rails,camera,commands,surface);

                int cacheBuilds=Count(registry,"ExclusionCacheBuildCount");
                for(int frame=0;frame<240;frame++)
                {commands.Clear();Call(registry,"Record",commands,camera,rails,false);Call(registry,"ReleaseFrame",commands);}
                Require(Count(registry,"ExclusionCacheBuildCount")==cacheBuilds,"Stable geometry rebuilt the exclusion grouping cache");
                // The cache describes grouping only. Visibility, pose, culling
                // handedness and native-only streams remain live each frame.
                var moved=renderers[7];var oldPose=moved.transform.localPosition;
                moved.transform.localPosition+=new Vector3(.7f,2,.4f);
                moved.transform.localRotation=Quaternion.Euler(12,70,25);
                Compare(Render(registry,rails,camera,commands,surface,false),Render(registry,rails,camera,commands,surface,true),"cached group moving part");
                moved.transform.localScale=new Vector3(-1.3f,.8f,1.3f);
                Compare(Render(registry,rails,camera,commands,surface,false),Render(registry,rails,camera,commands,surface,true),"cached group becomes mirrored");
                moved.transform.localScale=new Vector3(1.3f,.8f,1.3f);
                ((MeshRenderer)moved).additionalVertexStreams=streamMesh;
                Compare(Render(registry,rails,camera,commands,surface,false),Render(registry,rails,camera,commands,surface,true),"cached group gains vertex stream");
                ((MeshRenderer)moved).additionalVertexStreams=null;
                moved.enabled=false;renderers[8].forceRenderingOff=true;renderers[9].gameObject.SetActive(false);
                Compare(Render(registry,rails,camera,commands,surface,false),Render(registry,rails,camera,commands,surface,true),"cached group hidden renderers");
                moved.enabled=true;renderers[8].forceRenderingOff=false;renderers[9].gameObject.SetActive(true);
                moved.transform.localPosition=oldPose;
                Require(Count(registry,"ExclusionCacheBuildCount")==cacheBuilds,"Movement/visibility discarded reusable groups");
                Debug.Log("SNOW_EXCLUSION_CACHE_OK: 240 stable records without rebuilding; live movement, mirrored scale, additional vertex streams, enabled/forceRenderingOff/active state; exact surface ID parity.");

                // Alternating detailed and exclusion surfaces share the same
                // target. Batching must never move an exclusion past a detailed
                // draw, even when the geometry is exactly coincident.
                Set(vehicle,"SnowReady",true);
                var parts=(IList)Get(vehicle,"Parts");
                for(int i=0;i<parts.Count;i++)Set(parts[i],"Interior",i%37!=0);
                renderers[10].transform.position=renderers[0].transform.position;
                renderers[74].transform.position=renderers[73].transform.position;
                Compare(Render(registry,rails,camera,commands,surface,false),Render(registry,rails,camera,commands,surface,true),"interleaved full/exclusion writes");
                Require(Count(registry,"FrameSnowDrawCount")>0 && Count(registry,"FrameExclusionBatchCount")>0,
                    "Mixed fixture did not exercise both detailed and batched passes");
                LogBenchmark(registry,rails,camera,commands,"interleaved full/exclusions");
                var shift=new Vector3(5000,0,-7000);fleet.transform.position+=shift;camera.transform.position+=shift;
                Compare(Render(registry,rails,camera,commands,surface,false),Render(registry,rails,camera,commands,surface,true),"large world coordinates");

                // Unique meshes cannot profit from instancing. Keep their draw
                // primitive native, and report the bounded grouping overhead.
                foreach(var renderer in renderers)
                {
                    var meshRenderer=renderer as MeshRenderer;
                    if(meshRenderer==null || meshRenderer.isPartOfStaticBatch)continue;
                    var filter=meshRenderer.GetComponent<MeshFilter>();
                    var unique=UnityEngine.Object.Instantiate(filter.sharedMesh);resources.Add(unique);filter.sharedMesh=unique;
                }
                Call(registry,"RefreshParts",vehicle);Set(vehicle,"SnowReady",false);
                Compare(Render(registry,rails,camera,commands,surface,false),Render(registry,rails,camera,commands,surface,true),"distinct singleton meshes");
                Require(Count(registry,"ExclusionCacheBuildCount")==cacheBuilds+1,"Mesh replacements did not invalidate grouping exactly once");
                Require(Count(registry,"FrameInstancedExclusionCount")==0 && Count(registry,"FrameExclusionBatchCount")==0,
                    "A singleton exclusion mesh issued an instanced draw");
                LogBenchmark(registry,rails,camera,commands,"distinct singleton meshes");
                Debug.Log("SNOW_EXCLUSION_INSTANCING_OK: 200 repeated meshes; cutout alpha and UV transform; native mirrored/skinned/vertex-stream/static-batch fallbacks; identical surface IDs; ordered detailed/exclusion writes; world relocation. Source shaders="+sourceShaders);
            }
            finally
            {
                camera.RemoveCommandBuffer(CameraEvent.BeforeReflections,commands);commands.Dispose();camera.targetTexture=null;
                ((IDisposable)registry).Dispose();((IDisposable)rails).Dispose();((IDisposable)repository).Dispose();
                foreach(var resource in resources)if(resource!=null)UnityEngine.Object.DestroyImmediate(resource);
            }
        }
        static Color[] Render(object registry,object rails,Camera camera,CommandBuffer commands,RenderTexture surface,bool instancing)
        {
            Set(registry,"ExclusionInstancingEnabled",instancing);commands.Clear();
            Call(registry,"Record",commands,camera,rails,false);
            commands.Blit(Shader.PropertyToID("_DVPSVehicleData"),surface);
            Call(registry,"ReleaseFrame",commands);camera.Render();
            var previous=RenderTexture.active;RenderTexture.active=surface;
            var image=new Texture2D(surface.width,surface.height,TextureFormat.RGBAFloat,false,true);
            image.ReadPixels(new Rect(0,0,image.width,image.height),0,0);image.Apply(false,false);RenderTexture.active=previous;
            var result=image.GetPixels();UnityEngine.Object.DestroyImmediate(image);return result;
        }
        static double Benchmark(object registry,object rails,Camera camera,CommandBuffer commands,bool instancing)
        {
            Set(registry,"ExclusionInstancingEnabled",instancing);
            for(int i=0;i<8;i++) {commands.Clear();Call(registry,"Record",commands,camera,rails,false);Call(registry,"ReleaseFrame",commands);}
            var watch=System.Diagnostics.Stopwatch.StartNew();
            for(int i=0;i<100;i++) {commands.Clear();Call(registry,"Record",commands,camera,rails,false);Call(registry,"ReleaseFrame",commands);}
            watch.Stop();return watch.Elapsed.TotalMilliseconds/100;
        }
        static void LogBenchmark(object registry,object rails,Camera camera,CommandBuffer commands,string phase)
        {
            double baseline=Benchmark(registry,rails,camera,commands,false);
            double instanced=Benchmark(registry,rails,camera,commands,true);
            Debug.Log("SNOW_EXCLUSION_RECORD_CPU_MS: "+phase+"; DrawRenderer="+baseline.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+
                "; instanced="+instanced.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+
                "; per Record after warmup, 100 iterations, CPU command submission only.");
        }
        static void BenchmarkRenderedFrames(object registry,object rails,Camera camera,CommandBuffer commands,RenderTexture surface)
        {
            var readback=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);
            try
            {
                var times=new double[4];
                // Both shader paths and all pooling are warm before timing.
                for(int variant=0;variant<2;variant++)
                {
                    Set(registry,"ExclusionInstancingEnabled",variant!=0);
                    for(int frame=0;frame<8;frame++)RenderFrame(registry,rails,camera,commands,surface);
                }
                // A/B then B/A reduces simple warmup and ordering bias. The
                // synchronous 1-pixel read waits for completed camera work only
                // at each interval boundary, never between measured frames.
                for(int run=0;run<4;run++)
                {
                    bool instancing=run==1 || run==2;
                    Set(registry,"ExclusionInstancingEnabled",instancing);
                    Drain(camera.targetTexture,readback);
                    var watch=System.Diagnostics.Stopwatch.StartNew();
                    for(int frame=0;frame<120;frame++)RenderFrame(registry,rails,camera,commands,surface);
                    Drain(camera.targetTexture,readback);watch.Stop();
                    times[run]=watch.Elapsed.TotalMilliseconds/120;
                }
                var culture=System.Globalization.CultureInfo.InvariantCulture;
                Debug.Log("SNOW_EXCLUSION_RENDER_CPU_GPU_MS: DrawRenderer="+times[0].ToString("F3",culture)+","+times[3].ToString("F3",culture)+
                    "; instanced="+times[1].ToString("F3",culture)+","+times[2].ToString("F3",culture)+
                    "; 120 complete camera renders per run, warm A/B then B/A, synchronous GPU drain at boundaries, includes Record and native scene rendering; synthetic editor scene, not game FPS.");
            }
            finally {UnityEngine.Object.DestroyImmediate(readback);}
        }
        static void RenderFrame(object registry,object rails,Camera camera,CommandBuffer commands,RenderTexture surface)
        {
            commands.Clear();Call(registry,"Record",commands,camera,rails,false);
            commands.Blit(Shader.PropertyToID("_DVPSVehicleData"),surface);
            Call(registry,"ReleaseFrame",commands);camera.Render();
        }
        static void Drain(RenderTexture target,Texture2D readback)
        {
            var previous=RenderTexture.active;
            try {RenderTexture.active=target;readback.ReadPixels(new Rect(0,0,1,1),0,0,false);}
            finally {RenderTexture.active=previous;}
        }
        static void Compare(Color[] expected,Color[] actual,string phase)
        {
            int mismatches=0,marked=0;
            for(int i=0;i<expected.Length;i++)
            {
                if(expected[i].a<-.5f)marked++;
                if(expected[i]!=actual[i])mismatches++;
            }
            Require(marked>5000,phase+": blank surface-ID fixture, marked pixels="+marked);
            Require(mismatches==0,phase+": instancing changed "+mismatches+" surface-ID pixels");
        }
    }
}
