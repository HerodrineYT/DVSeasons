using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowFleetCacheVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static object Get(object o,string n)=>o.GetType().GetField(n,All).GetValue(o);
        static void Set(object o,string n,object v)=>o.GetType().GetField(n,All).SetValue(o,v);
        static object Call(object o,string n,params object[] a)=>o.GetType().GetMethod(n,All).Invoke(o,a);
        static int Count(object o,string n)=>(int)o.GetType().GetProperty(n,All).GetValue(o,null);
        static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
        static bool source;
        public static void RunSource(){source=true;Run();}
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Path.Combine(root,"artifacts/build/DVSeasons"),game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,args)=>{
                foreach(var dir in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var file=Path.Combine(dir,new AssemblyName(args.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            int result=0;
            try
            {
                var mod=Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll"));
                var type=typeof(SnowYardBatchVerification).GetNestedType("Fixture",All);
                using(var fixture=(IDisposable)Activator.CreateInstance(type,All,null,new object[]{mod,runtime,128},null))Verify(fixture);
            }
            catch(Exception e){Debug.LogException(e);result=1;}
            EditorApplication.Exit(result);
        }
        static void Verify(object fixture)
        {
            var registry=Get(fixture,"registry");var camera=(Camera)Get(fixture,"camera");
            var vehicles=(IList)Get(fixture,"vehicles");var first=(Transform)Get(vehicles[2],"Root");
            if(source)((Material)Get(registry,"material")).shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/SnowVehicle.shader");
            var sample=first.GetComponentInChildren<MeshFilter>();var mesh=sample.sharedMesh;var material=sample.GetComponent<Renderer>().sharedMaterial;
            var modelParts=first.GetComponentsInChildren<MeshFilter>();
            MeshRenderer moving=null,hidden=null;
            foreach(var v in vehicles)
            {
                var car=(Transform)Get(v,"Root");
                var group=new GameObject("Cached exterior");group.transform.SetParent(car,false);
                var levels=new LOD[3];
                for(int level=0;level<3;level++)
                {
                    var list=new Renderer[6];
                    for(int p=0;p<6;p++)
                    {
                        var go=new GameObject("Rigid exterior "+level+"-"+p);go.transform.SetParent(group.transform,false);
                        go.transform.localPosition=new Vector3((p%3-1)*.6f,.15f+level*.005f,(p/3-.5f)*.7f);
                        go.AddComponent<MeshFilter>().sharedMesh=modelParts[p].sharedMesh;var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;
                        list[p]=renderer;if(v==vehicles[2] && level==1 && p==0)moving=renderer;
                        if(v==vehicles[3] && level==1 && p==1)hidden=renderer;
                    }
                    levels[level]=new LOD(level==0?.06f:level==1?.006f:0,list);
                }
                var lod=group.AddComponent<LODGroup>();lod.SetLODs(levels);lod.RecalculateBounds();
                var cab=new GameObject("Dormant cab");cab.transform.SetParent(car,false);
                for(int p=0;p<16;p++){var go=new GameObject("Detail");go.transform.SetParent(cab.transform,false);go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterial=material;}
                cab.SetActive(false);Call(fixture,"Prepare",v);Set(v,"PartsPending",false);Set(v,"RollingStock",true);
            }
            Set(registry,"PartCacheEnabled",false);var baseline=(Color[][])Call(fixture,"Draw",true);
            Set(registry,"PartCacheEnabled",true);Equal(baseline,(Color[][])Call(fixture,"Draw",true),"cached active LOD membership");
            foreach(var v in vehicles)Call(Get(v,"MeshCache"),"Build",v);
            Set(registry,"CombinedMeshesEnabled",true);Equal(baseline,(Color[][])Call(fixture,"Draw",true),"combined opaque meshes");
            Require(Count(registry,"FrameCombinedCommands")>=100,"Combined meshes were not used");
            Benchmark(fixture,registry);
            moving.transform.localPosition+=new Vector3(0,.4f,0);hidden.enabled=false;
            Set(registry,"CombinedMeshesEnabled",false);baseline=(Color[][])Call(fixture,"Draw",true);
            Set(registry,"CombinedMeshesEnabled",true);Equal(baseline,(Color[][])Call(fixture,"Draw",true),"articulation and disabled member fallback");
            moving.transform.parent.gameObject.SetActive(false);
            Set(registry,"PartCacheEnabled",false);baseline=(Color[][])Call(fixture,"Draw",true);
            Set(registry,"PartCacheEnabled",true);Equal(baseline,(Color[][])Call(fixture,"Draw",true),"inactive LOD parent");
            moving.transform.parent.gameObject.SetActive(true);hidden.enabled=true;
            Set(registry,"CombinedMeshesEnabled",false);
            camera.transform.position=new Vector3(0,110,-8);camera.transform.LookAt(Vector3.zero);camera.farClipPlane=500;
            foreach(var v in vehicles)
            {
                foreach(var part in (IList)Get(v,"Parts"))Set(part,"Interior",false);
                Call(Get(v,"PartCache"),"Build",Get(v,"Parts"));
            }
            baseline=(Color[][])Call(fixture,"Draw",true);int oldDraws=Count(registry,"FrameDrawCount");
            Set(registry,"DistantSurfacesEnabled",true);var far=(Color[][])Call(fixture,"Draw",true);
            int snow=0,missing=0,wrong=0,background=0;
            for(int i=0;i<baseline[0].Length;i++)
            {
                if(baseline[0][i].a>0){snow++;if(far[0][i].a<=0)missing++;else if(far[0][i].a!=baseline[0][i].a)wrong++;}
                else if(far[0][i].a>0)background++;
            }
            Debug.Log("FLEET_FAR_PROXY cars="+Count(registry,"FrameDistantCars")+" commands="+Count(registry,"FrameDistantCommands")+" native_draws="+oldDraws+"->"+Count(registry,"FrameDrawCount")+" snow="+snow+" missing="+missing+" wrong_id="+wrong+" extra="+background);
            Require(snow>3000 && missing==0 && wrong==0 && background==0,"Far proxy lost silhouettes or covered the background");
            Require(Count(registry,"FrameDistantCars")==128 && Count(registry,"FrameDistantCommands")==1 && Count(registry,"FrameDrawCount")==0,"Far proxy did not remove per-part geometry");
            var farSystem=Get(registry,"distantSurfaces");var firstVehicle=vehicles[0];
            Set(firstVehicle,"PartsExploded",true);Require(!(bool)Call(farSystem,"Eligible",firstVehicle,camera),"Exploded car used proxy");Set(firstVehicle,"PartsExploded",false);
            Set(firstVehicle,"PartsPending",true);Require(!(bool)Call(farSystem,"Eligible",firstVehicle,camera),"Pending car used proxy");Set(firstVehicle,"PartsPending",false);
            camera.transform.position=((Transform)Get(firstVehicle,"Root")).position+Vector3.up*5;
            Require(!(bool)Call(farSystem,"Eligible",firstVehicle,camera),"Near car used far proxy");
            Debug.Log("SNOW_FLEET_CACHE_OK: active/inactive LODs, combined native order, articulated and hidden parts, 128 far proxies, exact visible silhouette/vehicle IDs and near/pending/exploded fallbacks.");
        }
        static void Equal(Color[][] expected,Color[][] actual,string label)
        {
            int ids=0,slopes=0;float error=0;
            for(int i=0;i<expected[0].Length;i++)
            {
                var a=expected[0][i];var b=actual[0][i];if(a.a!=b.a)ids++;if(expected[1][i].r!=actual[1][i].r)slopes++;
                error=Mathf.Max(error,Mathf.Max(Mathf.Abs(a.r-b.r),Mathf.Max(Mathf.Abs(a.g-b.g),Mathf.Abs(a.b-b.b))));
            }
            Debug.Log("FLEET_CACHE_DIFF "+label+": ids="+ids+" slopes="+slopes+" local="+error);
            Require(ids==0 && slopes==0 && error<=.002f,"Cache parity failed: "+label);
        }
        static void Benchmark(object fixture,object registry)
        {
            Set(registry,"CombinedMeshesEnabled",false);Call(fixture,"Time",true,12,true);
            double before=(double)Call(fixture,"Time",true,100,true);
            Set(registry,"CombinedMeshesEnabled",true);Call(fixture,"Time",true,12,true);
            double after=(double)Call(fixture,"Time",true,100,true);
            Debug.Log("FLEET_COMBINED_BENCH render_ms="+before.ToString("F3")+"->"+after.ToString("F3")+" combined_commands="+Count(registry,"FrameCombinedCommands")+"; not_game_FPS=true");
        }
    }
}
