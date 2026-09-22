using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace DVSeasons.AssetBundleBuild
{
    // Both APIs run on the same scheduler implementation. Direct delegates keep
    // reflection invocation/boxing out of the timed per-draw request path.
    public static class SnowPreparedRequestsVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        const int Width=640,Height=480,PartsPerCar=10;
        delegate void Legacy(Renderer renderer,Mesh mesh,int slot,int pass,float cutoff,Texture albedo,
            Vector4 st,Matrix4x4 objectToWorld,Matrix4x4 worldToVehicle,float index,bool canInstance);
        delegate void Prepared(object metadata,int pass,ref Matrix4x4 objectToWorld,ref Matrix4x4 worldToVehicle,float index,bool canInstance);
        static void Require(bool condition,string message) {if(!condition)throw new InvalidOperationException(message);}

        public static void Run()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            var game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME")??"F:/Steam/steamapps/common/Derail Valley";
            ResolveEventHandler resolver=(sender,args)=> {
                foreach(var directory in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var file=Path.Combine(directory,new AssemblyName(args.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;int result=0;
            try
            {
                var mod=Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll"));
                var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/SnowVehicle.shader");
                Require(shader!=null&&shader.isSupported&&SystemInfo.supportsInstancing,"Snow shader/instancing unavailable");
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                using(var fixture=new Fixture(mod,shader,32))
                {
                    fixture.Compare("opaque/cutout full/exclusion with native renderer fallbacks");
                    fixture.Compare("warm cached plan");
                    fixture.Move();fixture.Compare("live transforms and captured frames");
                    fixture.RecreateMetadata(false);fixture.Compare("equal replacement metadata");
                    fixture.RecreateMetadata(true);fixture.Compare("changed cutout and UV metadata");
                    fixture.Toggle();fixture.Compare("changed pass, eligibility, ID, and visibility");
                    fixture.Toggle();fixture.Compare("restored previous plan");
                    fixture.Compare("mixed prepared and legacy requests",true);
                }
                using(var fixture=new Fixture(mod,shader,224))
                {
                    fixture.Benchmark("mixed full/exclusion keys and native fallbacks");
                    fixture.AllNative();fixture.Benchmark("all DrawRenderer requests");
                }
                Debug.Log("SNOW_PREPARED_REQUESTS_OK: exact pixels, IDs, slopes and captured coordinates; immutable metadata refresh; dynamic pass/eligibility/pose/ID/visibility; mixed API use; native and instanced submission; direct delegate benchmark.");
            }
            catch(Exception error) {Debug.LogException(error);result=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(result);
        }

        sealed class Scheduler:IDisposable
        {
            public readonly object Value;
            public readonly CommandBuffer Commands=new CommandBuffer {name="Prepared snow request parity"};
            public readonly Action<CommandBuffer,Material> Begin;
            public readonly Action<Bounds> BeginVehicle;
            public readonly Action End;
            public readonly Legacy Add;
            public readonly Prepared AddPrepared;
            public Scheduler(Type type)
            {
                Value=Activator.CreateInstance(type,true);
                Begin=(Action<CommandBuffer,Material>)Delegate.CreateDelegate(typeof(Action<CommandBuffer,Material>),Value,type.GetMethod("Begin",All));
                BeginVehicle=(Action<Bounds>)Delegate.CreateDelegate(typeof(Action<Bounds>),Value,type.GetMethod("BeginVehicle",All));
                End=(Action)Delegate.CreateDelegate(typeof(Action),Value,type.GetMethod("End",All));
                Add=(Legacy)Delegate.CreateDelegate(typeof(Legacy),Value,type.GetMethod("Add",All));
                var method=type.GetMethod("AddPrepared",All);var args=typeof(Prepared).GetMethod("Invoke").GetParameters();
                var parameters=new ParameterExpression[args.Length];var callArgs=new Expression[args.Length];
                for(int i=0;i<args.Length;i++) {parameters[i]=Expression.Parameter(args[i].ParameterType,args[i].Name);callArgs[i]=parameters[i];}
                callArgs[0]=Expression.Convert(parameters[0],method.GetParameters()[0].ParameterType);
                AddPrepared=Expression.Lambda<Prepared>(Expression.Call(Expression.Constant(Value),method,callArgs),parameters).Compile();
            }
            public int Count(string name) {return (int)Value.GetType().GetProperty(name,All).GetValue(Value,null);}
            public void Dispose() {((IDisposable)Value).Dispose();Commands.Release();}
        }
        sealed class Part
        {
            public Renderer Renderer;
            public Mesh Mesh;
            public Texture Albedo;
            public Vector4 ST;
            public float Cutoff,Index;
            public bool CanInstance,Visible=true;
            public int Pass;
            public Matrix4x4 ObjectToWorld,WorldToVehicle;
            public object Metadata;
        }
        sealed class Fixture:IDisposable
        {
            readonly List<UnityEngine.Object> resources=new List<UnityEngine.Object>();
            readonly Scheduler original,prepared;
            readonly ConstructorInfo metadataConstructor;
            readonly Material snowMaterial;
            readonly Part[] parts;
            readonly Bounds[] bounds;
            readonly Camera camera;
            readonly RenderTexture target,surface,slope,previous;
            readonly RenderTargetIdentifier[] mrt;
            readonly Texture2D read;
            bool toggled;
            T Keep<T>(T value) where T:UnityEngine.Object {resources.Add(value);return value;}

            public Fixture(Assembly mod,Shader shader,int cars)
            {
                previous=RenderTexture.active;QualitySettings.antiAliasing=0;RenderSettings.fog=false;
                var type=mod.GetType("DVSeasons.Mod.SnowVehicleDrawScheduler",true);
                original=new Scheduler(type);prepared=new Scheduler(type);
                metadataConstructor=type.GetNestedType("DrawMetadata",All).GetConstructors(All)[0];
                snowMaterial=Keep(new Material(shader) {enableInstancing=true});
                var opaque=Keep(new Material(Shader.Find("Standard")) {color=Color.gray});
                var alpha=Keep(new Texture2D(8,8,TextureFormat.RGBA32,false,true));alpha.filterMode=FilterMode.Point;
                var pixels=new Color[64];for(int i=0;i<64;i++)pixels[i]=new Color(1,1,1,(i+i/8)%2);alpha.SetPixels(pixels);alpha.Apply(false,false);
                var cutout=Keep(new Material(opaque));cutout.mainTexture=alpha;cutout.mainTextureScale=new Vector2(2,1);cutout.mainTextureOffset=new Vector2(.125f,0);
                cutout.SetFloat("_Mode",1);cutout.SetFloat("_Cutoff",.5f);cutout.SetOverrideTag("RenderType","TransparentCutout");cutout.EnableKeyword("_ALPHATEST_ON");cutout.renderQueue=2450;
                var meshes=new Mesh[PartsPerCar];
                for(int kind=0;kind<meshes.Length;kind++)
                {
                    var normal=Quaternion.Euler(kind*6,0,0)*Vector3.up;
                    meshes[kind]=Keep(new Mesh {name="Prepared request model part "+kind,
                        vertices=new[]{new Vector3(-.27f,0,-.27f),new Vector3(-.27f,0,.27f),new Vector3(.27f,0,.27f),new Vector3(.27f,0,-.27f)},
                        normals=new[]{normal,normal,normal,normal},uv=new[]{Vector2.zero,Vector2.up,Vector2.one,Vector2.right},triangles=new[]{0,1,2,0,2,3}});
                    meshes[kind].RecalculateBounds();
                }
                bounds=new Bounds[cars];parts=new Part[cars*PartsPerCar];int columns=cars>32?16:8;
                for(int car=0;car<cars;car++)
                {
                    var carRoot=Keep(new GameObject("Prepared request car "+car)).transform;
                    carRoot.position=new Vector3((car%columns-(columns-1)*.5f)*4,0,(car/columns-(cars/columns-1)*.5f)*3);
                    bounds[car]=new Bounds(carRoot.position,new Vector3(3.8f,3,2.9f));
                    for(int kind=0;kind<PartsPerCar;kind++)
                    {
                        var go=new GameObject("Prepared part "+kind);go.transform.SetParent(carRoot,false);
                        go.transform.localPosition=new Vector3((kind%5-2)*.6f,0,(kind/5-.5f)*.7f);
                        go.transform.localRotation=Quaternion.Euler((car%3)*3,kind*2,kind%2*4);
                        go.AddComponent<MeshFilter>().sharedMesh=meshes[kind];var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=(kind&1)==0?opaque:cutout;
                        var part=new Part {Renderer=renderer,Mesh=meshes[kind],Albedo=(kind&1)==0?null:alpha,Cutoff=(kind&1)==0?0:.5f,
                            ST=new Vector4(2,1,.125f,0),Index=car+1,Pass=kind%4==3?5:0,CanInstance=kind%5!=4,
                            ObjectToWorld=renderer.localToWorldMatrix,WorldToVehicle=carRoot.worldToLocalMatrix};
                        parts[car*PartsPerCar+kind]=part;UpdateMetadata(part);
                    }
                }
                camera=Keep(new GameObject("Prepared snow request camera")).AddComponent<Camera>();camera.enabled=false;
                camera.transform.position=new Vector3(0,cars>32?95:34,-8);camera.transform.LookAt(Vector3.zero);
                camera.fieldOfView=50;camera.nearClipPlane=.1f;camera.farClipPlane=200;camera.renderingPath=RenderingPath.DeferredShading;
                camera.depthTextureMode=DepthTextureMode.Depth;camera.allowHDR=true;camera.allowMSAA=false;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                target=Keep(new RenderTexture(Width,Height,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                surface=Keep(new RenderTexture(Width,Height,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));surface.Create();
                slope=Keep(new RenderTexture(Width,Height,0,RenderTextureFormat.R8,RenderTextureReadWrite.Linear));slope.Create();
                mrt=new[]{new RenderTargetIdentifier(surface),new RenderTargetIdentifier(slope)};
                read=Keep(new Texture2D(Width,Height,TextureFormat.RGBAFloat,false,true));
                camera.Render();
            }
            void UpdateMetadata(Part part) {part.Metadata=metadataConstructor.Invoke(new object[]{part.Renderer,part.Mesh,0,part.Cutoff,part.Albedo,part.ST});}
            long Record(bool cached,bool mixed=false,bool timeIngestion=false)
            {
                var scheduler=cached?prepared:original;var commands=scheduler.Commands;commands.Clear();
                commands.SetRenderTarget(mrt,BuiltinRenderTextureType.CameraTarget);commands.ClearRenderTarget(false,true,Color.clear);
                scheduler.Begin(commands,snowMaterial);long started=timeIngestion?Stopwatch.GetTimestamp():0;
                for(int car=0;car<bounds.Length;car++)
                {
                    scheduler.BeginVehicle(bounds[car]);int end=(car+1)*PartsPerCar;
                    for(int i=car*PartsPerCar;i<end;i++)
                    {
                        var part=parts[i];if(!part.Visible)continue;
                        if(cached&&(!mixed||(i%3)!=0))scheduler.AddPrepared(part.Metadata,part.Pass,ref part.ObjectToWorld,ref part.WorldToVehicle,part.Index,part.CanInstance);
                        else scheduler.Add(part.Renderer,part.Mesh,0,part.Pass,part.Cutoff,part.Albedo,part.ST,part.ObjectToWorld,part.WorldToVehicle,part.Index,part.CanInstance);
                    }
                }
                long ticks=timeIngestion?Stopwatch.GetTimestamp()-started:0;scheduler.End();commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);return ticks;
            }
            Color[][] Draw(bool cached,bool mixed)
            {
                Record(cached,mixed);var commands=(cached?prepared:original).Commands;
                camera.AddCommandBuffer(CameraEvent.BeforeReflections,commands);try {camera.Render();}finally {camera.RemoveCommandBuffer(CameraEvent.BeforeReflections,commands);}
                var result=new Color[2][];
                RenderTexture.active=surface;read.ReadPixels(new Rect(0,0,Width,Height),0,0);read.Apply(false,false);result[0]=read.GetPixels();
                RenderTexture.active=slope;read.ReadPixels(new Rect(0,0,Width,Height),0,0);read.Apply(false,false);result[1]=read.GetPixels();RenderTexture.active=previous;return result;
            }
            public void Compare(string name,bool mixed=false)
            {
                var a=Draw(false,false);var b=Draw(true,mixed);int covered=0,changed=0;
                for(int i=0;i<a[0].Length;i++) {if(a[0][i].a!=0)covered++;if(!a[0][i].Equals(b[0][i])||!a[1][i].Equals(b[1][i]))changed++;}
                Require(covered>5000,name+": insufficient visible snow data "+covered);
                Require(changed==0,name+": changed surface/slope pixels "+changed);
                foreach(var count in new[]{"CommandCount","FullDrawCount","ExclusionDrawCount","FullInstancedObjectCount","FullBatchCount","ExclusionInstancedObjectCount","ExclusionBatchCount"})
                    Require(original.Count(count)==prepared.Count(count),name+": changed "+count);
                Debug.Log("SNOW_PREPARED_REQUEST_DIFF: "+name+" covered="+covered+" changed="+changed+" commands="+prepared.Count("CommandCount"));
            }
            public void Move()
            {
                foreach(var part in parts)
                {
                    part.Renderer.transform.localPosition+=new Vector3(.015f,.025f,-.01f);part.Renderer.transform.localRotation*=Quaternion.Euler(2,3,1);
                    part.ObjectToWorld=part.Renderer.localToWorldMatrix;
                    part.WorldToVehicle=Matrix4x4.TRS(new Vector3(.07f,0,-.05f),Quaternion.Euler(0,5,0),Vector3.one)*part.WorldToVehicle;
                }
            }
            public void RecreateMetadata(bool change)
            {foreach(var part in parts) {if(change&&part.Cutoff>0) {part.ST+=new Vector4(.2f,0,.125f,0);part.Cutoff=.3f;}UpdateMetadata(part);}}
            public void Toggle()
            {
                toggled=!toggled;
                for(int i=0;i<parts.Length;i++)
                {
                    var part=parts[i];int kind=i%PartsPerCar;
                    part.Pass=toggled?(kind%3==0?5:0):(kind%4==3?5:0);
                    part.CanInstance=toggled?kind%4!=2:kind%5!=4;
                    part.Index=i/PartsPerCar+1+(toggled?250:0);part.Visible=!toggled||i%9!=0;
                }
            }
            public void AllNative() {foreach(var part in parts)part.CanInstance=false;}
            double Time(bool cached,int frames,bool ingestion)
            {
                long ticks=0;var watch=Stopwatch.StartNew();
                for(int frame=0;frame<frames;frame++)ticks+=Record(cached,false,ingestion);
                watch.Stop();return ingestion?(double)ticks*1000/Stopwatch.Frequency/frames:watch.Elapsed.TotalMilliseconds/frames;
            }
            public void Benchmark(string name)
            {
                Time(false,80,false);Time(true,80,false);
                var old=new double[7];var current=new double[7];var oldIngest=new double[7];var currentIngest=new double[7];
                int oldBuilds=original.Count("PlanBuilds"),newBuilds=prepared.Count("PlanBuilds");
                for(int trial=0;trial<7;trial++)
                {
                    if((trial&1)==0) {old[trial]=Time(false,150,false);current[trial]=Time(true,150,false);oldIngest[trial]=Time(false,150,true);currentIngest[trial]=Time(true,150,true);}
                    else {current[trial]=Time(true,150,false);old[trial]=Time(false,150,false);currentIngest[trial]=Time(true,150,true);oldIngest[trial]=Time(false,150,true);}
                }
                Array.Sort(old);Array.Sort(current);Array.Sort(oldIngest);Array.Sort(currentIngest);
                Require(original.Count("PlanBuilds")==oldBuilds&&prepared.Count("PlanBuilds")==newBuilds,"Stable fixture unexpectedly rebuilt draw plans");
                Debug.Log("SNOW_PREPARED_REQUEST_BENCH: "+name+" cars="+bounds.Length+" requests="+parts.Length+
                    " record-ms legacy/prepared="+old[3].ToString("F4")+"/"+current[3].ToString("F4")+
                    " ingestion-ms legacy/prepared="+oldIngest[3].ToString("F4")+"/"+currentIngest[3].ToString("F4")+
                    " commands="+prepared.Count("CommandCount")+" builds-during-measurement=0; CPU recording only, no FPS claim");
            }
            public void Dispose()
            {
                RenderTexture.active=previous;camera.targetTexture=null;original.Dispose();prepared.Dispose();
                for(int i=resources.Count-1;i>=0;i--)if(resources[i]!=null)UnityEngine.Object.DestroyImmediate(resources[i]);
            }
        }
    }
}
