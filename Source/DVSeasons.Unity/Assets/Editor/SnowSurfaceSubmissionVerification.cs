using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Stopwatch=System.Diagnostics.Stopwatch;

namespace DVSeasons.AssetBundleBuild
{
    // Two independently loaded production DLLs operate on the same scene. This
    // preserves the previous implementation as an oracle, without duplicating
    // either visibility algorithm in the fixture or timing reflection calls.
    public static class SnowSurfaceSubmissionVerification
    {
        const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        const int Cars=224,Width=800,Height=600,Trials=7,CpuSamples=24,RenderSamples=8;
        static int checks;
        static bool heavyOnly;
        static object Get(object o,string n){return o.GetType().GetField(n,All).GetValue(o);}
        static void Set(object o,string n,object v){o.GetType().GetField(n,All).SetValue(o,v);}
        static object Call(object o,string n,params object[] args){return o.GetType().GetMethod(n,All).Invoke(o,args);}
        static int Count(object o,string n){return (int)o.GetType().GetProperty(n,All).GetValue(o,null);}
        static void Require(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
        static string Hash(string path){using(var s=File.OpenRead(path))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","");}
        static string Number(double value){return value.ToString("F4",CultureInfo.InvariantCulture);}
        static string Values(double[] values){var result=new string[values.Length];for(int i=0;i<values.Length;i++)result[i]=Number(values[i]);return string.Join(",",result);}
        static double Median(double[] values){var copy=(double[])values.Clone();Array.Sort(copy);return copy[copy.Length/2];}
        static Assembly LoadBaselineClone(string path)
        {
            // Unity's Mono loader can unify Assembly.Load(byte[]) calls with the
            // same assembly identity. Rename only the test copy's manifest using
            // Unity's bundled Cecil; do not modify the archived binary or its IL.
            string cecilPath=Path.Combine(Path.GetDirectoryName(EditorApplication.applicationPath),"Data/Managed/Unity.Cecil.dll");
            var cecil=Assembly.LoadFrom(cecilPath);var definition=cecil.GetType("Mono.Cecil.AssemblyDefinition",true);
            object assembly=definition.GetMethod("ReadAssembly",new[]{typeof(string)}).Invoke(null,new object[]{path});
            try
            {
                object name=definition.GetProperty("Name").GetValue(assembly,null);
                string cloneName="DVSeasons.SurfaceBaseline."+Guid.NewGuid().ToString("N");
                name.GetType().GetProperty("Name").SetValue(name,cloneName,null);
                using(var bytes=new MemoryStream())
                {
                    definition.GetMethod("Write",new[]{typeof(Stream)}).Invoke(assembly,new object[]{bytes});
                    var result=Assembly.Load(bytes.ToArray());
                    Require(result.GetName().Name==cloneName,"Mono did not preserve the renamed baseline test identity.");return result;
                }
            }
            finally{((IDisposable)assembly).Dispose();}
        }

        public static void RunHeavy(){heavyOnly=true;Run();}
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            string baseline=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_BASELINE_DLL")??Path.Combine(root,"artifacts/backups/surface-opt20260919/baseline/DVSeasons.dll");
            string candidate=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_CANDIDATE_DLL")??Path.Combine(runtime,"DVSeasons.dll");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME")??"F:/steam/steamapps/common/Derail Valley";
            ResolveEventHandler resolver=(s,a)=>{foreach(var dir in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {string file=Path.Combine(dir,new AssemblyName(a.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;
            int exit=0,oldVsync=QualitySettings.vSyncCount,oldRate=Application.targetFrameRate;float oldBias=QualitySettings.lodBias;
            try
            {
                Require(File.Exists(baseline)&&File.Exists(candidate),"Both production DLLs must exist.");
                var oldAssembly=LoadBaselineClone(baseline);var newAssembly=Assembly.LoadFrom(candidate);
                Require(!ReferenceEquals(oldAssembly,newAssembly),"Assembly loader unified both runtime versions.");
                Require(oldAssembly.ManifestModule.ModuleVersionId!=newAssembly.ManifestModule.ModuleVersionId,"Baseline and candidate have the same build identity.");
                Debug.Log("SNOW_SURFACE_AB_ID baseline="+Hash(baseline)+" candidate="+Hash(candidate)+" bundle="+Hash(Path.Combine(runtime,"AssetBundles/dvseasons_dv99"))+
                    " unity="+Application.unityVersion+" GPU="+SystemInfo.graphicsDeviceName+" API="+SystemInfo.graphicsDeviceType);
                QualitySettings.vSyncCount=0;Application.targetFrameRate=-1;QualitySettings.lodBias=1;
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                using(var fixture=new Fixture(oldAssembly,newAssembly,runtime)){if(heavyOnly)fixture.RunHeavy();else fixture.Run();}
                Debug.Log("SNOW_SURFACE_SUBMISSION_OK checks="+checks+"; two production DLLs, shared scene, ordered renderer/bounds/draw parity and surface pixel parity; timings are synthetic, not live yard FPS.");
            }
            catch(Exception error){Debug.LogException(error);exit=1;}
            finally{QualitySettings.vSyncCount=oldVsync;Application.targetFrameRate=oldRate;QualitySettings.lodBias=oldBias;AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(exit);
        }

        sealed class Runtime:IDisposable
        {
            public readonly object Registry,Rails,Repository;
            public readonly IList Vehicles;
            public readonly CommandBuffer Commands=new CommandBuffer();
            public readonly Action Record,Release;
            public readonly Func<int> Draws;
            readonly Type performance;
            public Runtime(Assembly assembly,string runtime,AssetBundle bundle,Camera camera,List<Transform> roots,List<Transform> interiors)
            {
                var repo=assembly.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
                Repository=Activator.CreateInstance(repo,new object[]{runtime});repo.GetProperty("Bundle").GetSetMethod(true).Invoke(Repository,new object[]{bundle});
                // No terrain/track textures are needed. Two repositories must
                // not independently async-load the same auxiliary bundles.
                Set(Repository,"winterBundleLoadFinished",true);Set(Repository,"tracksBundleLoadFinished",true);
                Registry=Activator.CreateInstance(assembly.GetType("DVSeasons.Mod.SnowVehicleRegistry",true),true);
                Rails=Activator.CreateInstance(assembly.GetType("DVSeasons.Mod.RailSnowTracks",true),true);
                for(int i=0;i<roots.Count;i++)Call(Registry,"Register",roots[i],interiors[i],null);
                Require((bool)Call(Registry,"Initialize",Repository),"Surface registry initialize");
                Vehicles=(IList)Get(Registry,"vehicles");
                foreach(var vehicle in Vehicles)Prepare(vehicle);
                Record=Expression.Lambda<Action>(Expression.Call(Expression.Constant(Registry),Registry.GetType().GetMethod("Record",All),
                    Expression.Constant(Commands),Expression.Constant(camera),Expression.Constant(Rails),Expression.Constant(false))).Compile();
                Release=Expression.Lambda<Action>(Expression.Call(Expression.Constant(Registry),Registry.GetType().GetMethod("ReleaseFrame",All),Expression.Constant(Commands))).Compile();
                Draws=Expression.Lambda<Func<int>>(Expression.Property(Expression.Constant(Registry),"FrameDrawCount")).Compile();
                performance=assembly.GetType("DVSeasons.Mod.SnowPerformance",true);
            }
            public void Prepare(object vehicle)
            {
                Call(Registry,"RefreshParts",vehicle);Set(vehicle,"Ready",true);Set(vehicle,"SnowReady",true);Set(vehicle,"PartsPending",false);
                Set(vehicle,"RollingStock",true);Set(vehicle,"LocalBounds",new Bounds(new Vector3(0,.5f,0),new Vector3(4,3,4)));
            }
            public void Limit(int limit,Camera camera)
            {
                var limiter=Get(Registry,"ObjectLimiter");limiter.GetType().GetProperty("Limit",All).SetValue(limiter,limit,null);
                Call(limiter,"InvalidateMembership");Call(limiter,"Update",camera,Vehicles);
            }
            public long Ticks(string name)
            {
                var counters=(IDictionary)performance.GetField("counters",All).GetValue(null);
                return counters.Contains(name)?(long)Get(counters[name],"Ticks"):0;
            }
            public int DrawCommands()
            {return Draws()-Count(Registry,"FrameInstancedExclusionCount")-Count(Registry,"FrameInstancedFullCount")+Count(Registry,"FrameExclusionBatchCount")+Count(Registry,"FrameFullBatchCount");}
            public void Dispose()
            {
                Commands.Dispose();((IDisposable)Registry).Dispose();((IDisposable)Rails).Dispose();
                // Both repositories share one bundle; the fixture owns its lifetime.
                Repository.GetType().GetProperty("Bundle").GetSetMethod(true).Invoke(Repository,new object[]{null});((IDisposable)Repository).Dispose();
            }
        }

        sealed class Fixture:IDisposable
        {
            readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
            readonly List<Transform> cars=new List<Transform>(),interiors=new List<Transform>();
            readonly List<Vector3> positions=new List<Vector3>();
            readonly List<Renderer> renderers=new List<Renderer>();
            readonly List<LODGroup> groups=new List<LODGroup>();
            readonly Runtime baseline,candidate;
            readonly Camera camera;
            readonly RenderTexture target,surface,slope,previous;
            readonly Texture2D reader,fence;
            readonly AssetBundle bundle;
            readonly Mesh[] meshes=new Mesh[8];
            readonly Material opaque,cutout;
            readonly GameObject fleet;
            bool heavyLayout;
            readonly int dataId=Shader.PropertyToID("_DVPSVehicleData"),slopeId=Shader.PropertyToID("_DVPSVehicleSlope");
            T Keep<T>(T value)where T:UnityEngine.Object{owned.Add(value);return value;}
            public Fixture(Assembly oldAssembly,Assembly newAssembly,string runtime)
            {
                previous=RenderTexture.active;fleet=Keep(new GameObject("Surface AB fleet"));
                camera=Keep(new GameObject("Surface AB camera")).AddComponent<Camera>();camera.enabled=false;camera.orthographic=false;camera.fieldOfView=46;camera.orthographicSize=43;
                camera.transform.position=new Vector3(0,100,0);camera.transform.rotation=Quaternion.Euler(90,0,0);camera.nearClipPlane=.1f;camera.farClipPlane=250;
                camera.renderingPath=RenderingPath.DeferredShading;camera.depthTextureMode=DepthTextureMode.Depth;camera.allowHDR=true;camera.allowMSAA=false;
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                target=Keep(new RenderTexture(Width,Height,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                surface=Keep(new RenderTexture(Width,Height,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));surface.Create();
                slope=Keep(new RenderTexture(Width,Height,0,RenderTextureFormat.R8,RenderTextureReadWrite.Linear));slope.Create();
                reader=Keep(new Texture2D(Width,Height,TextureFormat.RGBAFloat,false,true));fence=Keep(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                opaque=Keep(new Material(Shader.Find("Standard")) {color=Color.gray});
                var alpha=Keep(new Texture2D(8,8,TextureFormat.RGBA32,false,true));var colors=new Color[64];for(int i=0;i<64;i++)colors[i]=new Color(1,1,1,(i+i/8)%2);alpha.SetPixels(colors);alpha.Apply();
                cutout=Keep(new Material(opaque));cutout.mainTexture=alpha;cutout.SetFloat("_Mode",1);cutout.SetFloat("_Cutoff",.5f);cutout.SetOverrideTag("RenderType","TransparentCutout");cutout.EnableKeyword("_ALPHATEST_ON");cutout.renderQueue=2450;
                for(int m=0;m<meshes.Length;m++)
                {
                    var mesh=Keep(new Mesh{name="AB shared part "+m,vertices=new[]{new Vector3(-.2f,0,-.2f),new Vector3(-.2f,0,.2f),new Vector3(.2f,0,.2f),new Vector3(.2f,0,-.2f)},
                        normals=new[]{Vector3.up,Vector3.up,Vector3.up,Vector3.up},uv=new[]{Vector2.zero,Vector2.up,Vector2.one,Vector2.right},triangles=new[]{0,1,2,0,2,3}});mesh.RecalculateBounds();meshes[m]=mesh;
                }
                for(int car=0;car<Cars;car++)
                {
                    var root=new GameObject("Car "+car).transform;root.SetParent(fleet.transform,false);root.localPosition=new Vector3((car%16-7.5f)*4.5f,0,(car/16-6.5f)*4.5f);
                    cars.Add(root);positions.Add(root.localPosition);var interior=new GameObject("interior").transform;interior.SetParent(root,false);interiors.Add(interior);
                    for(int group=0;group<4;group++)
                    {
                        var node=new GameObject("LOD "+group).transform;node.SetParent(group==3?interior:root,false);node.localPosition=new Vector3((group%2-.5f)*1.7f,.3f,(group/2-.5f)*1.7f);
                        var levels=new LOD[3];
                        for(int level=0;level<3;level++)
                        {
                            var pair=new Renderer[2];for(int p=0;p<2;p++)pair[p]=Part(node,"Level "+level+" part "+p,new Vector3((p-.5f)*.5f,level*.025f,0),(group*2+p)%8);
                            levels[level]=new LOD(level==0?.03f:level==1?.015f:.001f,pair);
                        }
                        var lod=node.gameObject.AddComponent<LODGroup>();lod.SetLODs(levels);lod.size=car%3==0?3f:car%3==1?2f:1f;lod.localReferencePoint=Vector3.zero;groups.Add(lod);
                    }
                    for(int p=0;p<8;p++)
                    {
                        var r=Part(p>5?interior:root,"Ungrouped "+p,new Vector3((p%4-1.5f)*.5f,.7f,(p/4-.5f)*2.6f),p);
                        if(car<25&&p==0){var lod=r.gameObject.AddComponent<LODGroup>();lod.SetLODs(new[]{new LOD(0,new[]{r})});lod.size=.5f;groups.Add(lod);}
                    }
                    if(car%7!=0)interior.gameObject.SetActive(false);
                }
                Require(renderers.Count==7168&&groups.Count==921,"The fixture must model 7168 parts and 921 LOD groups.");
                bundle=AssetBundle.LoadFromFile(Path.Combine(runtime,"AssetBundles/dvseasons_dv99"));Require(bundle!=null,"Packed snow shader bundle");
                baseline=new Runtime(oldAssembly,runtime,bundle,camera,cars,interiors);candidate=new Runtime(newAssembly,runtime,bundle,camera,cars,interiors);
                Reset();
                Debug.Log("SNOW_SURFACE_AB_SCENE cars="+Cars+" renderers="+renderers.Count+" LOD_groups="+groups.Count+" resolution="+Width+"x"+Height);
            }
            Renderer Part(Transform parent,string name,Vector3 position,int mesh)
            {
                var node=new GameObject(name);node.transform.SetParent(parent,false);node.transform.localPosition=position;node.AddComponent<MeshFilter>().sharedMesh=meshes[mesh];
                var renderer=node.AddComponent<MeshRenderer>();renderer.sharedMaterial=mesh%3==0?cutout:opaque;renderers.Add(renderer);return renderer;
            }
            void Build(Runtime version,bool copy)
            {
                version.Commands.Clear();version.Record();
                if(copy){version.Commands.Blit(dataId,surface);version.Commands.Blit(slopeId,slope);}version.Release();
            }
            void Render(Runtime version)
            {
                camera.AddCommandBuffer(CameraEvent.BeforeReflections,version.Commands);
                try{camera.Render();}finally{camera.RemoveCommandBuffer(CameraEvent.BeforeReflections,version.Commands);}
            }
            Color[][] Pixels(Runtime version)
            {
                Build(version,true);Render(version);var pixels=new Color[2][];
                RenderTexture.active=surface;reader.ReadPixels(new Rect(0,0,Width,Height),0,0);reader.Apply(false,false);pixels[0]=reader.GetPixels();
                RenderTexture.active=slope;reader.ReadPixels(new Rect(0,0,Width,Height),0,0);reader.Apply(false,false);pixels[1]=reader.GetPixels();return pixels;
            }
            void Compare(string label,bool pixels=true)
            {
                Build(baseline,false);Build(candidate,false);int visibleParts=0;
                Require(baseline.Vehicles.Count==candidate.Vehicles.Count,label+" registered cars");
                for(int car=0;car<baseline.Vehicles.Count;car++)
                {
                    var a=baseline.Vehicles[car];var b=candidate.Vehicles[car];Require((bool)Get(a,"FrameVisible")== (bool)Get(b,"FrameVisible"),label+" car visibility "+car);
                    var ap=(IList)Get(a,"FrameParts");var bp=(IList)Get(b,"FrameParts");Require(ap.Count==bp.Count,label+" part count "+car);
                    for(int p=0;p<ap.Count;p++)
                    {
                        Require((Renderer)Get(ap[p],"Renderer")== (Renderer)Get(bp[p],"Renderer"),label+" native part order "+car+":"+p);
                        Require(((Bounds)Get(ap[p],"VisibleBounds")).Equals((Bounds)Get(bp[p],"VisibleBounds")),label+" live bounds "+car+":"+p);visibleParts++;
                    }
                }
                Require(baseline.Draws()==candidate.Draws(),label+" detailed/exclusion draw count");
                if(pixels)
                {
                    var a=Pixels(baseline);var b=Pixels(candidate);int covered=0,ids=0,slopes=0;float local=0;
                    for(int i=0;i<a[0].Length;i++)
                    {
                        if(a[0][i].a!=0)covered++;if(a[0][i].a!=b[0][i].a)ids++;if(a[1][i].r!=b[1][i].r)slopes++;
                        local=Mathf.Max(local,Mathf.Abs(a[0][i].r-b[0][i].r),Mathf.Abs(a[0][i].g-b[0][i].g),Mathf.Abs(a[0][i].b-b[0][i].b));
                    }
                    Require(covered>50,label+" nonempty surface image: covered="+covered+" draws="+baseline.Draws()+" actualPath="+camera.actualRenderingPath);Require(ids==0&&slopes==0&&local<=.002f,label+" pixels ids="+ids+" slopes="+slopes+" local="+local);
                }
                Debug.Log("SNOW_SURFACE_AB_PARITY "+label+" ordered_parts="+visibleParts+" draws="+baseline.Draws()+" commands="+baseline.DrawCommands()+"->"+candidate.DrawCommands()+" pixels="+pixels);
            }
            void Reset()
            {
                camera.orthographic=false;camera.fieldOfView=46;camera.orthographicSize=43;camera.transform.position=new Vector3(0,heavyLayout?400:100,0);
                camera.farClipPlane=heavyLayout?900:250;camera.transform.rotation=Quaternion.Euler(90,0,0);camera.cullingMask=-1;QualitySettings.lodBias=1;
                for(int i=0;i<cars.Count;i++){cars[i].localPosition=positions[i];cars[i].localRotation=Quaternion.identity;cars[i].localScale=Vector3.one;interiors[i].gameObject.SetActive(i%7==0);}
                foreach(var r in renderers)if(r!=null)
                {
                    // A live yard rejects many prepared parts for renderer state.
                    // Retain that cost while keeping ~1900 submitted parts below
                    // the current 2048-stream bound; the dense case tests fallback.
                    r.enabled=r.name!="Ungrouped 0"&&r.name!="Ungrouped 1"&&r.name!="Ungrouped 2"&&r.name!="Ungrouped 3";
                    r.forceRenderingOff=false;r.gameObject.SetActive(true);r.gameObject.layer=0;
                }
            }
            void Mutate(int mode,int sample)
            {
                if(mode==1)
                {
                    float t=sample*.071f;camera.transform.position=new Vector3(Mathf.Sin(t)*18f,100,Mathf.Cos(t)*9f);camera.fieldOfView=42+Mathf.Sin(t*.7f)*12f;
                    QualitySettings.lodBias=.9f+Mathf.Sin(t*.6f)*.5f;
                }
                else if(mode==2)
                {
                    for(int i=0;i<cars.Count;i++){float d=Mathf.Sin(sample*.3f+i)*.002f;cars[i].localPosition=positions[i]+new Vector3(d,d*.2f,-d*.7f);cars[i].localRotation=Quaternion.Euler(0,d*2,0);}
                }
                else if(mode==3)
                {
                    for(int i=0;i<renderers.Count;i+=37)renderers[i].enabled=(sample%4)<2;
                    for(int i=5;i<renderers.Count;i+=43)renderers[i].forceRenderingOff=(sample%6)<3;
                    for(int i=0;i<interiors.Count;i+=7)interiors[i].gameObject.SetActive((sample%8)<4);
                }
                else if(mode==4)
                {
                    camera.transform.position=new Vector3((sample&1)==0?-12:12,100,0);
                    camera.fieldOfView=(sample&1)==0?44:51;
                    renderers[28].enabled=(sample&1)!=0;
                    interiors[0].gameObject.SetActive((sample&1)!=0);
                }
            }
            void Drain(){RenderTexture.active=target;fence.ReadPixels(new Rect(0,0,1,1),0,0,false);}
            void WarmStationary(int frames=20)
            {for(int frame=0;frame<frames;frame++){Build(baseline,false);Build(candidate,false);}}
            void RequireFineMode(bool expected,string label)
            {
                var mode=candidate.Registry.GetType().GetProperty("FrameUsesPartBatches",All);
                if(mode!=null)Require((bool)mode.GetValue(candidate.Registry,null)==expected,label+" expected fine mode="+expected);
            }
            void MeasureTransition(string label,bool alternate)
            {
                var field=candidate.Registry.GetType().GetField("partDraws",All);
                if(field==null)return;
                var scheduler=field.GetValue(candidate.Registry);
                int initialBuilds=Count(scheduler,"ComponentBuilds"),firstFine=-1;
                var oldTimes=new double[20];var newTimes=new double[20];var modes=new string[20];var builds=new string[20];var oldCommands=new string[20];var newCommands=new string[20];
                for(int frame=0;frame<20;frame++)
                {
                    if(alternate)Mutate(4,frame);
                    int before=Count(scheduler,"ComponentBuilds");
                    for(int order=0;order<2;order++)
                    {
                        bool next=((frame+order)&1)!=0;var version=next?candidate:baseline;
                        version.Commands.Clear();long begin=Stopwatch.GetTimestamp();version.Record();version.Release();
                        (next?newTimes:oldTimes)[frame]=(Stopwatch.GetTimestamp()-begin)*1000d/Stopwatch.Frequency;
                    }
                    bool fine=(bool)candidate.Registry.GetType().GetProperty("FrameUsesPartBatches",All).GetValue(candidate.Registry,null);
                    modes[frame]=fine?"fine":"vehicle";builds[frame]=(Count(scheduler,"ComponentBuilds")-before).ToString();
                    oldCommands[frame]=baseline.DrawCommands().ToString();newCommands[frame]=candidate.DrawCommands().ToString();
                    if(fine&&firstFine<0)firstFine=frame;
                    if(alternate)Require(!fine,label+" moving/visibility alternation selected fine path at frame "+frame);
                }
                int componentBuilds=Count(scheduler,"ComponentBuilds")-initialBuilds;
                if(alternate)Require(componentBuilds==0,label+" repeatedly rebuilt fine topology despite unstable layouts");
                else Require(firstFine>=0,label+" stable connected layout never reached the fine path");
                double oldMaximum=0,newMaximum=0;for(int i=0;i<20;i++){oldMaximum=Math.Max(oldMaximum,oldTimes[i]);newMaximum=Math.Max(newMaximum,newTimes[i]);}
                Debug.Log("SNOW_SURFACE_AB_TRANSITION "+label+" old_CPU_frames_ms="+Values(oldTimes)+" new_CPU_frames_ms="+Values(newTimes)+
                    " modes="+string.Join(",",modes)+" component_deltas="+string.Join(",",builds)+" first_fine_frame="+firstFine+
                    " old_commands="+string.Join(",",oldCommands)+" new_commands="+string.Join(",",newCommands)+
                    " component_builds="+componentBuilds+" old_max_ms="+Number(oldMaximum)+" new_max_ms="+Number(newMaximum)+
                    "; 20 consecutive frames, no excluded cold frames or warmup, scene mutation outside timing, CPU Record+Release only");
            }
            double Time(Runtime version,int mode,int samples,bool render)
            {
                Reset();for(int i=0;i<24;i++){Mutate(mode,i);Build(version,false);if(render)Render(version);}Drain();long ticks=0;
                for(int sample=0;sample<samples;sample++)
                {
                    // Scene changes are identical and outside CPU timing. Native
                    // transform work induced inside Record remains in its timing.
                    Mutate(mode,sample);version.Commands.Clear();long begin=Stopwatch.GetTimestamp();version.Record();version.Release();
                    if(render)Render(version);ticks+=Stopwatch.GetTimestamp()-begin;
                }
                if(render){long begin=Stopwatch.GetTimestamp();Drain();ticks+=Stopwatch.GetTimestamp()-begin;}
                return ticks*1000d/Stopwatch.Frequency/samples;
            }
            void Benchmark(string label,int mode)
            {
                var oldCpu=new double[Trials];var newCpu=new double[Trials];var oldRender=new double[Trials];var newRender=new double[Trials];
                for(int trial=0;trial<Trials;trial++)for(int order=0;order<2;order++)
                {
                    bool next=((trial+order)&1)!=0;var version=next?candidate:baseline;
                    (next?newCpu:oldCpu)[trial]=Time(version,mode,CpuSamples,false);
                    (next?newRender:oldRender)[trial]=Time(version,mode,RenderSamples,true);
                }
                Debug.Log("SNOW_SURFACE_AB_BENCH "+label+" old_CPU_ms="+Number(Median(oldCpu))+" new_CPU_ms="+Number(Median(newCpu))+
                    " old_render_ms="+Number(Median(oldRender))+" new_render_ms="+Number(Median(newRender))+" old_CPU_trials="+Values(oldCpu)+" new_CPU_trials="+Values(newCpu)+
                    " old_render_trials="+Values(oldRender)+" new_render_trials="+Values(newRender)+" commands="+baseline.DrawCommands()+"->"+candidate.DrawCommands()+
                    "; 7 alternating trials, CPU=Record+Release; render=Record+Release+native camera+GPU completion, no pixel readback except terminal 1px fence, mutation outside timing; not game FPS");
            }
            public void Run()
            {
                WarmStationary();Compare("224-car 7168-part 921-LOD baseline");
                RequireFineMode(false,"Already efficient 18-command yard must retain vehicle scheduler");
                Benchmark("stationary all snow",0);
                for(int mode=1;mode<=3;mode++)
                {
                    Reset();for(int frame=0;frame<18;frame++){Mutate(mode,frame);Compare("mutation="+mode+" frame="+frame,frame%6==0);}
                    Benchmark(mode==1?"moving camera and LOD":mode==2?"PhysX pose jitter":"renderer state and interiors",mode);
                }
                Reset();baseline.Limit(79,camera);candidate.Limit(79,camera);Compare("rolling-stock limit 79");Benchmark("stationary 79-car limit",0);
                baseline.Limit(0,camera);candidate.Limit(0,camera);Reset();
                // Long envelopes connect whole consists, while repeated body/
                // wheel-sized meshes remain spatially independent along most
                // of each car. This distinguishes part-level ordering from a
                // fixture where vehicle-level streams already batch perfectly.
                for(int i=0;i<positions.Count;i++)positions[i]=new Vector3((i%14-6.5f)*4.5f,0,(i/14-7.5f)*2.8f);
                Reset();MeasureTransition("cold connected-layout eligibility and first fine graph",false);
                WarmStationary();Compare("14 connected consists with repeated disjoint parts");
                RequireFineMode(true,"Connected stationary yard must reach fine scheduler after warmup");
                Benchmark("connected car envelopes, independent detailed parts",0);
                Reset();WarmStationary();MeasureTransition("connected layout alternating camera and renderer/interior state",true);
                Compare("connected alternating layout final frame");
                Benchmark("connected consists with moving camera and LOD",1);
                Benchmark("connected consists with renderer state and interiors",3);
                Benchmark("connected consists with alternating camera and visibility",4);
                Reset();
                for(int i=0;i<cars.Count;i++)cars[i].localRotation=Quaternion.Euler(0,17,0);
                Compare("rotated connected consists");
                // Fine mode must step aside as soon as a previously warmed
                // layout changes, then become reusable once it settles again.
                Reset();WarmStationary();renderers[28].enabled=false;Compare("first frame of visible-detail removal");
                RequireFineMode(false,"Immediate visibility-change fallback");
                renderers[28].enabled=true;WarmStationary();Compare("restored stable detailed layout");
                RequireFineMode(true,"Stable layout recovers fine scheduler");
                var original=cars[1].position;cars[1].position=cars[0].position;
                Compare("coincident car geometry across different snow IDs");WarmStationary();Compare("warmed coincident car geometry");cars[1].position=original;
                var detail=renderers[28].transform;var oldDetailPosition=detail.position;detail.position=renderers[60].transform.position+new Vector3(0,.005f,0);
                Compare("near-coplanar inter-car depth-tolerance boundary");WarmStationary();Compare("warmed near-coplanar boundary");detail.position=oldDetailPosition;
                Reset();
                camera.orthographic=false;camera.fieldOfView=55;camera.transform.position=new Vector3(3,32,-42);camera.transform.LookAt(Vector3.zero);Compare("perspective partial frustum");
                camera.cullingMask=~(1<<8);for(int i=9;i<renderers.Count;i+=43)renderers[i].gameObject.layer=8;Compare("live camera and renderer layers");
                Reset();foreach(var cabin in interiors)cabin.gameObject.SetActive(true);
                foreach(var renderer in renderers)if(renderer!=null)renderer.enabled=true;
                foreach(var lod in groups){lod.enabled=false;lod.fadeMode=LODFadeMode.CrossFade;}Compare("disabled fading LOD groups with all interiors, stream-cap fallback");
                Require(baseline.Draws()>4096,"The stream-cap fixture must emit more than 4096 detailed/exclusion part requests.");
                foreach(var lod in groups){lod.enabled=true;lod.fadeMode=LODFadeMode.None;lod.size*=1.15f;lod.localReferencePoint=new Vector3(.2f,.1f,0);}Compare("live LOD size and reference point");
                var moved=renderers[7].transform;var parent=moved.parent;var local=moved.localPosition;moved.SetParent(fleet.transform,true);moved.position=cars[10].position+new Vector3(0,1,0);Compare("detached part escapes vehicle envelope");moved.SetParent(parent,false);moved.localPosition=local;
                renderers[8].GetComponent<MeshFilter>().sharedMesh=meshes[6];baseline.Prepare(baseline.Vehicles[0]);candidate.Prepare(candidate.Vehicles[0]);Compare("refreshed mesh replacement");
                var added=Part(cars[0],"Streamed new detail",new Vector3(0,1,0),2);baseline.Prepare(baseline.Vehicles[0]);candidate.Prepare(candidate.Vehicles[0]);Compare("new streamed detail after discovery");
                UnityEngine.Object.DestroyImmediate(added.gameObject);Compare("destroyed prepared renderer");
                UnityEngine.Object.DestroyImmediate(groups[2]);Compare("destroyed prepared LOD group");
                var shift=new Vector3(5000,200,-7000);fleet.transform.position+=shift;camera.transform.position+=shift;Compare("floating origin shift");
            }
            public void RunHeavy()
            {
                // Deliberately extreme ordering workload: two 112-car consists
                // rather than a representative yard layout. Retain all 224 cars,
                // the same 7168 prepared parts and ~1920 active surface requests.
                // A higher perspective camera sees the full train length; scale
                // LOD group sizes (not meshes) to keep their relative thresholds.
                heavyLayout=true;
                for(int car=0;car<positions.Count;car++)positions[car]=new Vector3((car%2-.5f)*4.5f,0,(car/2-55.5f)*2.8f);
                foreach(var group in groups)group.size*=4;
                Reset();MeasureTransition("extreme two 112-car consists cold graph",false);
                WarmStationary();Compare("extreme two 112-car consists full-view surface parity");
                Require(baseline.DrawCommands()>=600,"Heavy ordering fixture must submit at least 600 baseline commands, got "+baseline.DrawCommands());
                RequireFineMode(false,"Heavy distant geometry must reject an unprofitable fine plan");
                Require((bool)Get(candidate.Registry,"rejectedPartLayout"),"Heavy fallback must be caused by the measured command-saving check");
                Require(Count(Get(candidate.Registry,"partDraws"),"CommandCount")*4>=baseline.DrawCommands()*3,
                    "The retained trial plan must actually fail the 25% saving threshold");
                Debug.Log("SNOW_SURFACE_AB_HEAVY synthetic_extreme_ordering=true cars=224 consists=2 cars_per_consist=112 active_parts="+baseline.Draws()+
                    " baseline_commands="+baseline.DrawCommands()+" trial_fine_commands="+Count(Get(candidate.Registry,"partDraws"),"CommandCount")+
                    " candidate_commands="+candidate.DrawCommands()+" steady_mode=profitability_fallback camera_height=400; LOD thresholds retained by group-size scaling, geometry unscaled, not a live yard reproduction");
                Benchmark("extreme two 112-car consists",0);
                renderers[28].enabled=false;Compare("heavy chain immediate state fallback");RequireFineMode(false,"Heavy chain renderer-change fallback");
                renderers[28].enabled=true;WarmStationary();Compare("heavy chain profitability fallback restored");RequireFineMode(false,"Heavy chain unprofitable fine plan must remain rejected");
            }
            public void Dispose()
            {
                RenderTexture.active=previous;camera.targetTexture=null;baseline.Dispose();candidate.Dispose();bundle.Unload(true);
                for(int i=owned.Count-1;i>=0;i--)if(owned[i]!=null)UnityEngine.Object.DestroyImmediate(owned[i]);
            }
        }
    }
}
