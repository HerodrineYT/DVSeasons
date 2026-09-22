using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    // CPU benchmark plus invariants: sparse world traversal must be one bounded
    // pass, and recording unchanged animals must not keep querying materials.
    public static class ExclusionDiscoveryVerification
    {
        private const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        private static object Call(object owner,string method,params object[] args)
        {return owner.GetType().GetMethod(method,All).Invoke(owner,args);}
        private static object Field(object owner,string name)
        {return owner.GetType().GetField(name,All).GetValue(owner);}
        private static int Count(object owner,string name)
        {return (int)owner.GetType().GetProperty(name,All).GetValue(owner,null);}
        private static void Require(bool ok,string message) {if(!ok)throw new InvalidOperationException(message);}

        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string modPath=Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            ResolveEventHandler resolve=(sender,args)=>{
                foreach(string dir in new[]{modPath,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {string path=Path.Combine(dir,new AssemblyName(args.Name).Name+".dll");if(File.Exists(path))return Assembly.LoadFrom(path);}return null;};
            AppDomain.CurrentDomain.AssemblyResolve+=resolve;
            int code=0;
            try {Verify(Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll")),modPath);}
            catch(Exception error) {UnityEngine.Debug.LogException(error);code=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolve;}
            EditorApplication.Exit(code);
        }

        private static void Verify(Assembly mod,string modPath)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var root=new GameObject("Large streamed environment");
            var material=new Material(Shader.Find("Standard"));
            var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh=cube.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(cube);
            const int emptyNodes=5000,sceneryMeshes=320,animalCount=24;
            for(int i=0;i<emptyNodes;i++)new GameObject("Empty node "+i).transform.SetParent(root.transform,false);
            for(int i=0;i<sceneryMeshes;i++)
            {
                var node=new GameObject("Warehouse panel "+i);node.transform.SetParent(root.transform,false);
                node.AddComponent<MeshFilter>().sharedMesh=mesh;node.AddComponent<MeshRenderer>().sharedMaterial=material;
            }
            var animalRenderers=new List<Renderer>();
            for(int i=0;i<animalCount;i++)
            {
                var animal=new GameObject("Cow_rigged "+i);animal.transform.position=new Vector3((i%6)-3,0,i/6);
                var body=new GameObject("Body");body.transform.SetParent(animal.transform,false);
                body.AddComponent<MeshFilter>().sharedMesh=mesh;
                var renderer=body.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;animalRenderers.Add(renderer);
            }
            var camera=new GameObject("Discovery benchmark camera").AddComponent<Camera>();
            camera.transform.position=new Vector3(0,8,-12);camera.transform.LookAt(new Vector3(0,0,2));camera.farClipPlane=100;
            var source=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SnowExposureExclusions",true),true);
            var shaderBundle=AssetBundle.LoadFromFile(Path.Combine(modPath,"AssetBundles/dvseasons_dv99"));
            var shader=shaderBundle.LoadAsset<Shader>("Assets/DVSeasons/DV99/Shaders/SnowVehicle.shader");
            Require(shader!=null,"Snow vehicle exclusion shader missing from bundle");
            Call(source,"SetExclusionShader",shader);
            var commands=new CommandBuffer {name="Animal record CPU benchmark"};
            try
            {
                long totalTicks=0,maxTicks=0;int frames=0,rendererVisits=0,maxSteps=0;
                do
                {
                    var watch=Stopwatch.StartNew();Call(source,"Update");watch.Stop();
                    totalTicks+=watch.ElapsedTicks;maxTicks=Math.Max(maxTicks,watch.ElapsedTicks);frames++;
                    int steps=Count(source,"LastAnimalDiscoverySteps");maxSteps=Math.Max(maxSteps,steps);
                    Require(steps<=96,"Discovery exceeded shared 96-step frame budget");
                    rendererVisits+=Count(source,"LastRendererDiscoveryCount");
                    Require(frames<3000,"Bounded discovery failed to complete a finite scene");
                }while(((ICollection)Field(source,"pendingAnimalScenes")).Count>0);
                Require(rendererVisits==sceneryMeshes+animalCount,"The same renderers were visited by duplicate scans: "+rendererVisits);
                Require(((IDictionary)Field(source,"animals")).Count==animalCount,"Single discovery pass lost animals");
                var idle=Stopwatch.StartNew();
                for(int i=0;i<120;i++)
                {
                    Call(source,"Update");
                    Require(Count(source,"LastAnimalDiscoverySteps")==0,"Completed scene kept scanning before background interval");
                }
                idle.Stop();
                foreach(var draw in (IList)Field(source,"drawRefresh"))
                    draw.GetType().GetField("NextRefresh",All).SetValue(draw,float.PositiveInfinity);
                int refreshBefore=Count(source,"MaterialRefreshCount");
                var recording=Stopwatch.StartNew();
                for(int i=0;i<180;i++)
                {
                    animalRenderers[0].transform.position=new Vector3((i%5)*.1f,0,0);
                    Require((bool)Call(source,"PrepareVisibleAnimals",camera),"Visible animals disappeared from exclusion pass");
                    commands.Clear();Call(source,"RecordAnimalExclusions",commands);
                }
                recording.Stop();
                Require(Count(source,"MaterialRefreshCount")==refreshBefore,"Unchanged rendering refreshed animal materials every frame");
                var first=((IList)Field(source,"drawRefresh"))[0];
                first.GetType().GetField("NextRefresh",All).SetValue(first,-1f);
                source.GetType().GetField("nextDrawRefresh",All).SetValue(source,0);
                Call(source,"Update");
                Require(Count(source,"MaterialRefreshCount")==refreshBefore+1,"Periodic metadata refresh stopped detecting material changes");
                UnityEngine.Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "EXCLUSION_DISCOVERY_PERF_OK: {0} empty nodes, {1} scenery and {2} animals; single-pass renderer visits={3}; frames={4}; maxSteps={5}; discovery mean/max={6:F3}/{7:F3} ms; completed idle={8:F4} ms/frame; animal record={9:F3} ms/frame; no per-frame material rebuild.",
                    emptyNodes,sceneryMeshes,animalCount,rendererVisits,frames,maxSteps,
                    totalTicks*1000d/Stopwatch.Frequency/frames,maxTicks*1000d/Stopwatch.Frequency,
                    idle.Elapsed.TotalMilliseconds/120,recording.Elapsed.TotalMilliseconds/180));
            }
            finally
            {
                ((IDisposable)source).Dispose();commands.Dispose();
                foreach(var renderer in animalRenderers)if(renderer!=null)UnityEngine.Object.DestroyImmediate(renderer.transform.parent.gameObject);
                UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(material);
                shaderBundle.Unload(true);
            }
        }
    }
}
