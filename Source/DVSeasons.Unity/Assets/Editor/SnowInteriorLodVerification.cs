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
    // Real Unity LOD selection and deferred depth; the reference deliberately
    // retains the former all-interior-LODs exclusion path without changing the
    // native renderer state. No game prefab or production toggle is required.
    public static class SnowInteriorLodVerification
    {
        const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static object Get(object owner,string name) {return owner.GetType().GetField(name,All).GetValue(owner);}
        static void Set(object owner,string name,object value) {owner.GetType().GetField(name,All).SetValue(owner,value);}
        static object Call(object owner,string name,params object[] args) {return owner.GetType().GetMethod(name,All).Invoke(owner,args);}
        static int Count(object owner,string name) {return (int)owner.GetType().GetProperty(name,All).GetValue(owner,null);}
        static void Require(bool ok,string message) {if(!ok)throw new InvalidOperationException(message);}
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD");
            if(string.IsNullOrEmpty(runtime))runtime=Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME")??"F:/Steam/steamapps/common/Derail Valley";
            ResolveEventHandler resolver=(sender,args)=>
            {
                foreach(var directory in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {
                    string file=Path.Combine(directory,new AssemblyName(args.Name).Name+".dll");
                    if(File.Exists(file))return Assembly.LoadFrom(file);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;int result=0;
            try {Verify(Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll")),runtime);}
            catch(Exception exception) {Debug.LogException(exception);result=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(result);
        }
        static void Verify(Assembly mod,string runtime)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            float originalBias=QualitySettings.lodBias;QualitySettings.lodBias=1;QualitySettings.antiAliasing=0;RenderSettings.fog=false;
            var resources=new List<UnityEngine.Object>();
            var fleet=new GameObject("Interior LOD verification fleet");resources.Add(fleet);
            var camera=new GameObject("Interior LOD verification camera").AddComponent<Camera>();resources.Add(camera.gameObject);
            camera.transform.rotation=Quaternion.Euler(90,0,0);camera.fieldOfView=60;camera.nearClipPlane=.1f;camera.farClipPlane=400;
            camera.renderingPath=RenderingPath.DeferredShading;camera.depthTextureMode=DepthTextureMode.Depth;
            camera.allowMSAA=false;camera.allowHDR=true;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
            var target=new RenderTexture(512,384,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);target.Create();resources.Add(target);camera.targetTexture=target;
            var surface=new RenderTexture(512,384,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);surface.Create();resources.Add(surface);
            var opaque=new Material(Shader.Find("Standard"));opaque.color=Color.gray;resources.Add(opaque);
            var group=CreateBogie(fleet.transform,opaque,Vector3.zero,true);
            var repositoryType=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
            var repository=Activator.CreateInstance(repositoryType,new object[]{runtime});
            var bundle=AssetBundle.LoadFromFile(Path.Combine(runtime,"AssetBundles/dvseasons_dv99"));
            repositoryType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{bundle});
            var registry=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SnowVehicleRegistry",true),true);
            var rails=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.RailSnowTracks",true),true);
            var commands=new CommandBuffer {name="Interior LOD exclusion parity"};camera.AddCommandBuffer(CameraEvent.BeforeReflections,commands);
            try
            {
                Require((bool)Call(registry,"Initialize",repository),"Vehicle snow initialization failed");
                ((Material)Get(registry,"material")).shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/SnowVehicle.shader");
                Call(registry,"Register",fleet.transform,null,null);var vehicle=((IList)Get(registry,"vehicles"))[0];Call(registry,"RefreshParts",vehicle);
                foreach(float distance in new[]{6f,12f,30f,350f})
                {
                    camera.transform.position=new Vector3(0,distance,0);
                    var reference=Render(registry,rails,vehicle,camera,commands,surface,true);
                    int before=Count(registry,"FrameDrawCount");
                    var actual=Render(registry,rails,vehicle,camera,commands,surface,false);
                    int expected=distance<20?3:distance<100?1:0;
                    Require(Count(registry,"FrameDrawCount")==expected,"Wrong native LOD draw count at "+distance+"m: "+Count(registry,"FrameDrawCount")+"; expected "+expected);
                    Compare(reference,actual,"distance="+distance,distance<100);
                    Debug.Log("SNOW_INTERIOR_LOD_POSE: distance="+distance+"; logical draws="+before+"->"+Count(registry,"FrameDrawCount"));
                }
                // Native/shared labels belong to both near levels. A last-LOD
                // dictionary assignment would omit the visible near label.
                camera.transform.position=new Vector3(0,6,0);
                var shared=group.transform.Find("[axle]/Shared label").GetComponent<Renderer>();
                object sharedPart=null;foreach(var part in (IList)Get(vehicle,"Parts"))if((Renderer)Get(part,"Renderer")==shared)sharedPart=part;
                Require(sharedPart!=null && (bool)Call(sharedPart,"UsesLod",0) && (bool)Call(sharedPart,"UsesLod",1),"Shared renderer lost one native LOD membership");
                foreach(int mode in new[]{0,1})
                {
                    group.enabled=mode!=0;group.fadeMode=mode==1?LODFadeMode.CrossFade:LODFadeMode.None;
                    var reference=Render(registry,rails,vehicle,camera,commands,surface,true);int before=Count(registry,"FrameDrawCount");
                    var actual=Render(registry,rails,vehicle,camera,commands,surface,false);
                    Require(Count(registry,"FrameDrawCount")==before,"Disabled/fading group lost conservative exclusions");
                    Compare(reference,actual,mode==0?"disabled group":"fading group",true);
                }
                group.enabled=true;group.fadeMode=LODFadeMode.None;
                var offset=new Vector3(5200,0,-6700);fleet.transform.position+=offset;camera.transform.position+=offset;
                Compare(Render(registry,rails,vehicle,camera,commands,surface,true),Render(registry,rails,vehicle,camera,commands,surface,false),"floating origin",true);
                fleet.transform.position=Vector3.zero;
                UnityEngine.Object.DestroyImmediate(group.gameObject);
                for(int i=0;i<40;i++)CreateBogie(fleet.transform,opaque,new Vector3((i%10-4.5f)*3.2f,0,(i/10-1.5f)*3.2f),false);
                Call(registry,"RefreshParts",vehicle);Set(vehicle,"SnowReady",true);camera.transform.position=new Vector3(0,80,0);
                var fleetReference=Render(registry,rails,vehicle,camera,commands,surface,true);int oldCheap=Count(registry,"FrameExclusionDrawCount"),oldFull=Count(registry,"FrameSnowDrawCount");
                var fleetActual=Render(registry,rails,vehicle,camera,commands,surface,false);Compare(fleetReference,fleetActual,"20-wagon axle fleet",true);
                Require(oldCheap==160 && Count(registry,"FrameExclusionDrawCount")==0,"Distant fleet retained unused axle LODs: "+oldCheap+"->"+Count(registry,"FrameExclusionDrawCount"));
                Require(oldFull==40 && Count(registry,"FrameSnowDrawCount")==40,"Combined native bogie geometry was removed");
                Debug.Log("SNOW_INTERIOR_LOD_FLEET: 20 wagons / 40 bogies; unused axle exclusions 160->0; combined native bogie surfaces 40->40; identical surface IDs.");
                // Time complete deferred renders, not the reflection-based
                // reference setup or per-pixel image readback used above.
                // More detailed wheel-set geometry represents the vertex work
                // of replayed axle parts while native LOD2 remains unchanged.
                var axleMesh=CreateWheelSetMesh();resources.Add(axleMesh);
                InstallAxleMeshes(fleet.transform,axleMesh);
                Call(registry,"RefreshParts",vehicle);Set(vehicle,"SnowReady",true);
                Compare(Render(registry,rails,vehicle,camera,commands,surface,true),Render(registry,rails,vehicle,camera,commands,surface,false),"20-wagon wheel-set mesh parity",true);
                Benchmark(registry,rails,vehicle,camera,commands,"20 wagons / 40 bogies",axleMesh);
                for(int i=fleet.transform.childCount-1;i>=0;i--)UnityEngine.Object.DestroyImmediate(fleet.transform.GetChild(i).gameObject);
                for(int i=0;i<200;i++)CreateBogie(fleet.transform,opaque,new Vector3((i%20-9.5f)*3.2f,0,(i/20-4.5f)*3.2f),false);
                InstallAxleMeshes(fleet.transform,axleMesh);
                Call(registry,"RefreshParts",vehicle);Set(vehicle,"SnowReady",true);camera.transform.position=new Vector3(0,100,0);
                var largeReference=Render(registry,rails,vehicle,camera,commands,surface,true);
                int largeCheap=Count(registry,"FrameExclusionDrawCount"),largeFull=Count(registry,"FrameSnowDrawCount");
                var largeActual=Render(registry,rails,vehicle,camera,commands,surface,false);Compare(largeReference,largeActual,"100-wagon axle fleet",true);
                Require(largeCheap==800 && Count(registry,"FrameExclusionDrawCount")==0,"Large fleet did not omit only unused axle levels");
                Require(largeFull==200 && Count(registry,"FrameSnowDrawCount")==200,"Large fleet native combined bogies changed");
                Benchmark(registry,rails,vehicle,camera,commands,"100 wagons / 200 bogies",axleMesh);
                Debug.Log("SNOW_INTERIOR_LOD_OK: native near/middle/combined/culled selection; shared membership; disabled/fading fallback; origin shift; exact deferred surface-ID parity.");
            }
            finally
            {
                QualitySettings.lodBias=originalBias;camera.RemoveCommandBuffer(CameraEvent.BeforeReflections,commands);commands.Dispose();camera.targetTexture=null;
                ((IDisposable)registry).Dispose();((IDisposable)rails).Dispose();((IDisposable)repository).Dispose();
                foreach(var resource in resources)if(resource!=null)UnityEngine.Object.DestroyImmediate(resource);
            }
        }
        static LODGroup CreateBogie(Transform parent,Material material,Vector3 position,bool sharedLabel)
        {
            var root=new GameObject("Native-style bogie");root.transform.SetParent(parent,false);root.transform.localPosition=position;
            var axle=new GameObject("[axle]");axle.transform.SetParent(root.transform,false);
            var near=new List<Renderer>();var middle=new List<Renderer>();
            for(int i=0;i<2;i++)
            {
                near.Add(Cube(axle.transform,"Axle LOD0 "+i,new Vector3(i==0?-.65f:.65f,0,0),new Vector3(.6f,.35f,1.2f),material));
                middle.Add(Cube(axle.transform,"Axle LOD1 "+i,new Vector3(i==0?-.65f:.65f,0,0),new Vector3(.58f,.32f,1.18f),material));
            }
            if(sharedLabel)
            {
                var shared=Cube(axle.transform,"Shared label",new Vector3(0,.35f,0),new Vector3(.3f,.1f,.7f),material);
                near.Add(shared);middle.Add(shared);
            }
            var combined=Cube(root.transform,"Combined bogie LOD2",Vector3.zero,new Vector3(2.1f,.3f,1.2f),material);
            var group=root.AddComponent<LODGroup>();group.SetLODs(new[]{new LOD(.3048425f,near.ToArray()),new LOD(.1404414f,middle.ToArray()),new LOD(.0097196f,new[]{combined})});
            group.localReferencePoint=Vector3.zero;group.size=2.9963493f;group.fadeMode=LODFadeMode.None;return group;
        }
        static Renderer Cube(Transform parent,string name,Vector3 position,Vector3 scale,Material material)
        {
            var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);cube.name=name;cube.transform.SetParent(parent,false);cube.transform.localPosition=position;cube.transform.localScale=scale;
            var renderer=cube.GetComponent<MeshRenderer>();renderer.sharedMaterial=material;return renderer;
        }
        static Mesh CreateWheelSetMesh()
        {
            // Two wheels plus their axle, using actual tessellated cylinder
            // geometry instead of an arbitrary repeated triangle multiplier.
            var cylinder=GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            var mesh=cylinder.GetComponent<MeshFilter>().sharedMesh;
            var wheelRotation=Quaternion.Euler(90,0,0);
            var combine=new[]{
                new CombineInstance {mesh=mesh,transform=Matrix4x4.TRS(new Vector3(0,0,-.4f),wheelRotation,new Vector3(1,.06f,1))},
                new CombineInstance {mesh=mesh,transform=Matrix4x4.TRS(new Vector3(0,0,.4f),wheelRotation,new Vector3(1,.06f,1))},
                new CombineInstance {mesh=mesh,transform=Matrix4x4.TRS(Vector3.zero,wheelRotation,new Vector3(.18f,.45f,.18f))}
            };
            var result=new Mesh {name="Regression wheel pair and axle"};result.CombineMeshes(combine,true,true);
            UnityEngine.Object.DestroyImmediate(cylinder);return result;
        }
        static void InstallAxleMeshes(Transform fleet,Mesh mesh)
        {
            foreach(var filter in fleet.GetComponentsInChildren<MeshFilter>(true))
                if(filter.name.StartsWith("Axle LOD",StringComparison.Ordinal))filter.sharedMesh=mesh;
        }
        static void Benchmark(object registry,object rails,object vehicle,Camera camera,CommandBuffer commands,string phase,Mesh axleMesh)
        {
            var lods=new Dictionary<object,object>();
            foreach(var part in (IList)Get(vehicle,"Parts"))if((bool)Get(part,"Interior"))lods.Add(part,Get(part,"Lod"));
            var readback=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);
            var renderArgs=new object[]{commands,camera,rails,false};var releaseArgs=new object[]{commands};
            var record=registry.GetType().GetMethod("Record",All);var release=registry.GetType().GetMethod("ReleaseFrame",All);
            var previous=RenderTexture.active;
            Action<bool> select=all=> {foreach(var item in lods)Set(item.Key,"Lod",all?null:item.Value);};
            Action drain=()=> {RenderTexture.active=camera.targetTexture;readback.ReadPixels(new Rect(0,0,1,1),0,0,false);};
            Action frame=()=> {commands.Clear();record.Invoke(registry,renderArgs);release.Invoke(registry,releaseArgs);camera.Render();};
            Func<bool,int,double> measure=(all,frames)=> {
                select(all);drain();var watch=System.Diagnostics.Stopwatch.StartNew();
                for(int i=0;i<frames;i++)frame();
                drain();watch.Stop();return watch.Elapsed.TotalMilliseconds/frames;
            };
            try
            {
                measure(true,20);measure(false,20);
                var baseline=new double[7];var filtered=new double[7];
                for(int trial=0;trial<7;trial++)
                    if((trial&1)==0) {baseline[trial]=measure(true,100);filtered[trial]=measure(false,100);}
                    else {filtered[trial]=measure(false,100);baseline[trial]=measure(true,100);}
                Array.Sort(baseline);Array.Sort(filtered);
                select(true);frame();int oldFull=Count(registry,"FrameSnowDrawCount"),oldCheap=Count(registry,"FrameExclusionDrawCount");
                int oldCommands=Count(registry,"FrameDrawCount")-Count(registry,"FrameInstancedExclusionCount")+Count(registry,"FrameExclusionBatchCount");
                select(false);frame();int newFull=Count(registry,"FrameSnowDrawCount"),newCheap=Count(registry,"FrameExclusionDrawCount");
                int newCommands=Count(registry,"FrameDrawCount")-Count(registry,"FrameInstancedExclusionCount")+Count(registry,"FrameExclusionBatchCount");
                Debug.Log("SNOW_INTERIOR_LOD_RENDER_BENCH: "+phase+"; native_full="+oldFull+"->"+newFull+"; axle_exclusions="+oldCheap+"->"+newCheap+
                    "; snow_draw_commands="+oldCommands+"->"+newCommands+"; axle_mesh_vertices="+axleMesh.vertexCount+"; axle_mesh_triangles="+axleMesh.triangles.Length/3+
                    "; baseline_median_ms="+baseline[3].ToString("F4",System.Globalization.CultureInfo.InvariantCulture)+
                    "; filtered_median_ms="+filtered[3].ToString("F4",System.Globalization.CultureInfo.InvariantCulture)+
                    "; baseline_range_ms="+baseline[0].ToString("F4")+".."+baseline[6].ToString("F4")+
                    "; filtered_range_ms="+filtered[0].ToString("F4")+".."+filtered[6].ToString("F4")+
                    "; resolution="+camera.pixelWidth+"x"+camera.pixelHeight+
                    "; 7 alternating trials x100 frames after20 warmup; includes registry Record, native deferred camera and GPU completion drain; reference setup and image parity readbacks outside timing; synthetic not_game_FPS=true");
            }
            finally {select(false);RenderTexture.active=previous;UnityEngine.Object.DestroyImmediate(readback);}
        }
        static Color[] Render(object registry,object rails,object vehicle,Camera camera,CommandBuffer commands,RenderTexture surface,bool allInteriorLods)
        {
            var restore=new Dictionary<object,object>();
            if(allInteriorLods)foreach(var part in (IList)Get(vehicle,"Parts"))if((bool)Get(part,"Interior"))
            {restore.Add(part,Get(part,"Lod"));Set(part,"Lod",null);}
            try
            {
                commands.Clear();Call(registry,"Record",commands,camera,rails,false);commands.Blit(Shader.PropertyToID("_DVPSVehicleData"),surface);Call(registry,"ReleaseFrame",commands);camera.Render();
                var previous=RenderTexture.active;RenderTexture.active=surface;
                var image=new Texture2D(surface.width,surface.height,TextureFormat.RGBAFloat,false,true);image.ReadPixels(new Rect(0,0,image.width,image.height),0,0);image.Apply(false,false);RenderTexture.active=previous;
                var pixels=image.GetPixels();UnityEngine.Object.DestroyImmediate(image);return pixels;
            }
            finally {foreach(var item in restore)Set(item.Key,"Lod",item.Value);}
        }
        static void Compare(Color[] reference,Color[] actual,string phase,bool requireSurface)
        {
            int marked=0,mismatch=0;
            for(int i=0;i<reference.Length;i++)
            {
                if(Mathf.Abs(reference[i].a)>.5f)marked++;
                if(Mathf.Abs(reference[i].r-actual[i].r)>.001f || Mathf.Abs(reference[i].g-actual[i].g)>.001f || Mathf.Abs(reference[i].b-actual[i].b)>.001f || Mathf.Abs(reference[i].a-actual[i].a)>.001f)mismatch++;
            }
            Require(!requireSurface || marked>4,"Blank native surface-ID reference: "+phase);
            Require(mismatch==0,"Interior LOD filtering changed "+mismatch+" pixels: "+phase);
        }
    }
}
