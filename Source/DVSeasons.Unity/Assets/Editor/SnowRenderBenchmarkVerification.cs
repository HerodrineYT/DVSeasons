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
    // Exercises actual deferred camera execution and the production callback.
    // Advances only the diagnostic session; does not run the game's simulation.
    public static class SnowRenderBenchmarkVerification
    {
        const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static int checks;
        static object Get(object o,string n){return o.GetType().GetField(n,All).GetValue(o);}
        static void Set(object o,string n,object v){o.GetType().GetField(n,All).SetValue(o,v);}
        static object Call(object o,string n,params object[] a){return o.GetType().GetMethod(n,All).Invoke(o,a);}
        static object Static(Type t,string n,params object[] a){return t.GetMethod(n,All).Invoke(null,a);}
        static void Require(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME")??"F:/steam/steamapps/common/Derail Valley";
            ResolveEventHandler resolve=(s,a)=>{foreach(var dir in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var file=Path.Combine(dir,new AssemblyName(a.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            AppDomain.CurrentDomain.AssemblyResolve+=resolve;int result=0;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                using(var f=new Fixture(Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll")),runtime))f.Verify();
                Debug.Log("SNOW_RENDER_BENCHMARK_OK checks="+checks+"; real HDR/LDR camera normal/omitted shading/omitted surfaces/empty command buffer, exact restoration and untouched history; no game FPS claim.");
            }
            catch(Exception e){Debug.LogException(e);result=1;}
            finally{AppDomain.CurrentDomain.AssemblyResolve-=resolve;}
            EditorApplication.Exit(result);
        }
        sealed class Fixture:IDisposable
        {
            readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
            readonly object controller,repository,registry,session;
            readonly Type benchmark;
            readonly Camera camera;
            RenderTexture target;
            readonly Texture2D reader;
            readonly RenderTexture previousTarget;
            readonly CommandBuffer commands;
            readonly Camera.CameraCallback callback;
            bool record=true;
            Exception failure;
            int renderErrors;
            readonly Application.LogCallback logCallback;
            T Keep<T>(T o)where T:UnityEngine.Object{owned.Add(o);return o;}
            public Fixture(Assembly mod,string runtime)
            {
                previousTarget=RenderTexture.active;
                QualitySettings.antiAliasing=0;RenderSettings.fog=false;
                RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.gray*.3f;
                camera=Keep(new GameObject("Render probe camera")).AddComponent<Camera>();camera.enabled=false;
                camera.renderingPath=RenderingPath.DeferredShading;camera.depthTextureMode=DepthTextureMode.Depth;
                camera.allowMSAA=false;camera.allowHDR=true;camera.nearClipPlane=.1f;camera.farClipPlane=100;
                camera.transform.position=new Vector3(0,7,-8);camera.transform.LookAt(Vector3.zero);
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                target=Keep(new RenderTexture(256,192,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                reader=Keep(new Texture2D(256,192,TextureFormat.RGBAFloat,false,true));
                var material=Keep(new Material(Shader.Find("Standard")));material.color=new Color(.12f,.1f,.08f);material.SetFloat("_Glossiness",0);
                var floor=Keep(GameObject.CreatePrimitive(PrimitiveType.Cube));floor.transform.localScale=new Vector3(30,.2f,30);floor.transform.position=Vector3.down*.1f;floor.GetComponent<Renderer>().sharedMaterial=material;
                var car=Keep(GameObject.CreatePrimitive(PrimitiveType.Cube));car.name="Excluded vehicle";car.transform.position=new Vector3(0,.6f,0);car.transform.localScale=new Vector3(2,1.2f,3);car.GetComponent<Renderer>().sharedMaterial=material;
                var sun=Keep(new GameObject("Probe light")).AddComponent<Light>();sun.type=LightType.Directional;sun.transform.rotation=Quaternion.Euler(50,20,0);sun.shadows=LightShadows.None;
                var rt=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);repository=Activator.CreateInstance(rt,new object[]{runtime});
                rt.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{AssetBundle.LoadFromFile(Path.Combine(runtime,"AssetBundles/dvseasons_dv99"))});
                controller=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.ProceduralSnowController",true),new[]{repository});
                Require((bool)Call(controller,"LoadResources"),"Snow resources unavailable");
                registry=Get(controller,"vehicles");Call(registry,"Register",car.transform,null,null);
                var vehicle=((IList)Get(registry,"vehicles"))[0];Call(registry,"RefreshParts",vehicle);Set(vehicle,"Ready",true);Set(vehicle,"LocalBounds",new Bounds(Vector3.zero,Vector3.one));
                // It deliberately stays SnowReady=false, exercising the excluded
                // silhouette without manufacturing saved vehicle snow masks.
                Set(controller,"worldCamera",camera);Set(controller,"amount",.8f);Set(controller,"coverageInitialized",true);
                controller.GetType().GetProperty("IsActive").GetSetMethod(true).Invoke(controller,new object[]{true});
                commands=new CommandBuffer{name="Production diagnostic phase verification"};Set(controller,"commands",commands);camera.AddCommandBuffer(CameraEvent.BeforeReflections,commands);
                var heightSource=Keep(new Texture2D(64,64,TextureFormat.RGBAFloat,false,true));var heightPixels=new Color[64*64];
                for(int y=0;y<64;y++)for(int x=0;x<64;x++)
                {
                    float wx=(x+.5f)*.5f-16f,wz=(y+.5f)*.5f-16f;
                    heightPixels[y*64+x]=new Color(Mathf.Abs(wx)<1f&&Mathf.Abs(wz)<1.5f?1.2f:0,0,0,0);
                }
                heightSource.SetPixels(heightPixels);heightSource.Apply(false,false);
                foreach(var name in new[]{"near","far","distant"})
                {
                    var height=new RenderTexture(64,64,0,RenderTextureFormat.RFloat,RenderTextureReadWrite.Linear){filterMode=FilterMode.Point};height.Create();Graphics.Blit(heightSource,height);
                    var map=Get(controller,name);Set(map,"Texture",height);Set(map,"Ready",true);Set(map,"Area",new Vector4(0,0,16,.03125f));Set(map,"HeightOffset",0f);
                }
                RenderTexture.active=previousTarget;
                benchmark=mod.GetType("DVSeasons.Mod.SnowRenderBenchmark",true);session=benchmark.GetField("session",All).GetValue(null);
                callback=current=>{if(current!=camera)return;try{if(record)Call(controller,"BeforeCamera",current);else commands.Clear();}catch(Exception e){failure=e;}};
                Camera.onPreRender+=callback;
                logCallback=(condition,trace,type)=>{if(type==LogType.Error||type==LogType.Exception)renderErrors++;};Application.logMessageReceived+=logCallback;
            }
            Color[] Render()
            {
                camera.Render();if(failure!=null)throw failure;
                var old=RenderTexture.active;RenderTexture.active=target;reader.ReadPixels(new Rect(0,0,256,192),0,0);reader.Apply(false,false);RenderTexture.active=old;
                var result=reader.GetPixels();foreach(var c in result)Require(!float.IsNaN(c.r)&&!float.IsInfinity(c.r)&&!float.IsNaN(c.g)&&!float.IsInfinity(c.g),"Non-finite rendered color");return result;
            }
            void Phase(int index)
            {
                Static(benchmark,"Cancel","fixture phase reset");Static(benchmark,"Ready",camera,.8f,0);Static(benchmark,"Start");
                Call(session,"Tick",5d,true);for(int i=0;i<index;i++)Call(session,"Tick",10d,true);
                Require((int)session.GetType().GetProperty("PhaseIndex").GetValue(session,null)==index,"Wrong diagnostic phase");
                // Synchronous Editor renders share a frame and do not provide
                // gameplay frame timing. The session itself is unit-tested;
                // retain this explicitly selected phase during camera execution.
                benchmark.GetField("lastFrame",All).SetValue(null,Time.frameCount);
            }
            static float Difference(Color[] a,Color[] b)
            {
                float maximum=0;for(int i=0;i<a.Length;i++)maximum=Mathf.Max(maximum,Mathf.Max(Mathf.Abs(a[i].r-b[i].r),Mathf.Max(Mathf.Abs(a[i].g-b[i].g),Mathf.Abs(a[i].b-b[i].b))));return maximum;
            }
            public void Verify()
            {
                object rails=Get(controller,"RailTracks");float snowClock=(float)rails.GetType().GetProperty("SnowClock").GetValue(rails,null);
                object near=Get(controller,"near");var nearTexture=Get(near,"Texture");
                foreach(bool hdr in new[]{true,false})
                {
                    if(!hdr)
                    {
                        target=Keep(new RenderTexture(256,192,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                    }
                    camera.allowHDR=hdr;Static(benchmark,"Cancel","new HDR mode");record=false;Render();var bare=Render();record=true;
                    Phase(0);var normal=Render();Require(Difference(normal,bare)>.02f,"Fixture failed to render visible snow");
                    Phase(1);var noVehicles=Render();Require(Difference(normal,noVehicles)>.001f,"Vehicle surface omission did not change excluded body");
                    Require(Shader.GetGlobalTexture("_DVPSVehicleData")==((Texture)Get(registry,"emptySurfaceData")),"Omitted vehicle frame did not bind explicit empty data");
                    Phase(2);Require(Difference(Render(),bare)<.00001f,"WithoutShading changed native deferred image");
                    Phase(3);Require(Difference(Render(),bare)<.00001f,"WithoutProceduralSnow changed native image");Require(commands.sizeInBytes==0,"SkipAll retained prior render commands");
                    Phase(4);Require(Difference(Render(),normal)<.00001f,"RestoredBaseline changed snow image");
                    Static(benchmark,"Cancel","fixture cancellation");Require(Difference(Render(),normal)<.00001f,"Cancel did not restore exact normal image");
                    Require(camera.GetCommandBuffers(CameraEvent.BeforeReflections).Length==1,"Phase transition changed camera buffer lifetime");
                    Require(ReferenceEquals(nearTexture,Get(near,"Texture")),"Phase transition replaced shelter history");
                    Require((float)Get(controller,"amount")==.8f,"Phase transition changed snow coverage");
                    Require((float)rails.GetType().GetProperty("SnowClock").GetValue(rails,null)==snowClock,"Phase transition advanced snow clock");
                    Debug.Log("SNOW_RENDER_BENCHMARK_PHASES hdr="+hdr+" restored_max_error=0 normal_bare_delta="+Difference(normal,bare));
                }
                Phase(0);Static(benchmark,"Ready",camera,.806f,0);
                Require((bool)benchmark.GetProperty("Active",All).GetValue(null,null),"Sub-threshold coverage drift canceled probe");
                Static(benchmark,"Ready",camera,.812f,0);
                Require(!(bool)benchmark.GetProperty("Active",All).GetValue(null,null),"Cumulative coverage drift did not cancel probe");
                var seasonType=benchmark.GetMethod("SetSeason",All).GetParameters()[0].ParameterType;
                var seasons=Enum.GetValues(seasonType);Static(benchmark,"SetSeason",seasons.GetValue(0));Phase(0);
                Static(benchmark,"SetSeason",seasons.GetValue(1));
                Require(!(bool)benchmark.GetProperty("Active",All).GetValue(null,null),"Season change did not cancel probe");
                if(Time.frameCount>=3)
                {
                    Phase(0);benchmark.GetField("lastFrame",All).SetValue(null,Time.frameCount-3);Static(benchmark,"BeforeRender",camera);
                    Require(!(bool)benchmark.GetProperty("Active",All).GetValue(null,null),"Missing world-camera frames did not cancel probe");
                }
                Require(renderErrors==0,"Unity reported errors during diagnostic phases: "+renderErrors);
            }
            public void Dispose()
            {
                Camera.onPreRender-=callback;Application.logMessageReceived-=logCallback;Static(benchmark,"Cancel","fixture disposal");
                RenderTexture.active=previousTarget;
                ((IDisposable)controller).Dispose();((IDisposable)repository).Dispose();
                Require(camera.GetCommandBuffers(CameraEvent.BeforeReflections).Length==0,"Dispose retained probe command buffer");
                for(int i=owned.Count-1;i>=0;i--)if(owned[i]!=null)UnityEngine.Object.DestroyImmediate(owned[i]);
            }
        }
    }
}
