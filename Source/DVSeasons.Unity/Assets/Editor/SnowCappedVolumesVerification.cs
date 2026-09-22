using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowCappedVolumesVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static object Get(object o,string n)=>o.GetType().GetField(n,All).GetValue(o);
        static void Set(object o,string n,object v)=>o.GetType().GetField(n,All).SetValue(o,v);
        static object Call(object o,string n,params object[] args)=>o.GetType().GetMethod(n,All).Invoke(o,args);
        static int Count(object o,string n)=>(int)o.GetType().GetProperty(n,All).GetValue(o,null);
        static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
        static bool heavy;
        public static void RunHeavy(){heavy=true;Run();}
        public static void Run()
        {
            int result=0;
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,args)=>{
                foreach(var dir in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var path=Path.Combine(dir,new AssemblyName(args.Name).Name+".dll");if(File.Exists(path))return Assembly.LoadFrom(path);}return null;};
            try
            {
                var mod=Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll"));
                var type=typeof(SnowYardBatchVerification).GetNestedType("Fixture",All);
                using(var fixture=(IDisposable)Activator.CreateInstance(type,All,null,new object[]{mod,runtime,heavy?224:128},null))
                {
                    var registry=Get(fixture,"registry");var camera=(Camera)Get(fixture,"camera");
                    var fleet=(GameObject)Get(fixture,"fleet");
                    var vehicles=(IList)Get(fixture,"vehicles");
                    if(heavy)
                    {
                        var sample=((Transform)Get(vehicles[4],"Root")).GetComponentInChildren<MeshFilter>();
                        var mesh=sample.sharedMesh;var material=sample.GetComponent<Renderer>().sharedMaterial;
                        foreach(var vehicle in vehicles)
                        {
                            var carRoot=(Transform)Get(vehicle,"Root");
                            for(int part=0;part<48;part++)
                            {
                                var go=new GameObject("Inactive cab detail "+part);go.transform.SetParent(carRoot,false);
                                go.AddComponent<MeshFilter>().sharedMesh=mesh;go.AddComponent<MeshRenderer>().sharedMaterial=material;go.SetActive(false);
                            }
                            for(int group=0;group<8;group++)
                            {
                                var go=new GameObject("Axle LOD "+group);go.transform.SetParent(carRoot,false);
                                go.transform.localPosition=new Vector3((group%4-1.5f)*.5f,-.2f,(group/4-.5f)*.4f);
                                var lods=new LOD[3];
                                for(int level=0;level<3;level++)
                                {
                                    var child=new GameObject("Axle level "+level);child.transform.SetParent(go.transform,false);
                                    child.AddComponent<MeshFilter>().sharedMesh=mesh;
                                    var renderer=child.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;
                                    lods[level]=new LOD(level==0?.05f:level==1?.01f:0f,new[]{renderer});
                                }
                                var lod=go.AddComponent<LODGroup>();lod.SetLODs(lods);lod.RecalculateBounds();
                            }
                            Call(fixture,"Prepare",vehicle);
                        }
                    }
                    foreach(var vehicle in vehicles){Set(vehicle,"RollingStock",true);Set(vehicle,"PartsPending",false);}
                    var limiter=Get(registry,"ObjectLimiter");Call(limiter,"InvalidateMembership");
                    var ground=GameObject.CreatePrimitive(PrimitiveType.Plane);ground.transform.SetParent(fleet.transform,false);
                    ground.transform.localPosition=new Vector3(0,-3,0);ground.transform.localScale=new Vector3(20,1,20);
                    foreach(int limit in new[]{0,64,35,1})
                    {
                        limiter.GetType().GetProperty("Limit",All).SetValue(limiter,limit,null);
                        Call(limiter,"Update",camera,Get(registry,"vehicles"));
                        Verify(fixture,registry,"limit="+limit,limit>0);
                    }
                    limiter.GetType().GetProperty("Limit",All).SetValue(limiter,35,null);
                    Call(limiter,"Update",camera,Get(registry,"vehicles"));
                    Benchmark(fixture,registry);
                    foreach(var vehicle in vehicles){var t=(Transform)Get(vehicle,"Root");t.localRotation=Quaternion.Euler(0,31,0);t.localPosition+=new Vector3(.3f,.2f,.1f);}
                    Verify(fixture,registry,"moving and rotated cars",true);
                    Call(fixture,"Shift");Verify(fixture,registry,"origin shift",true);
                    camera.orthographic=true;camera.orthographicSize=36;Verify(fixture,registry,"orthographic",true);
                    // Force native geometry even for capped vehicles near camera,
                    // during a pending refresh or after derailing/explosion.
                    var volumes=Get(registry,"exclusionVolumes");var car=vehicles[0];
                    Call(volumes,"Begin");Set(car,"PartsPending",true);
                    Require(!(bool)Call(volumes,"TryAdd",car,camera),"Pending geometry used an obsolete volume");
                    Set(car,"PartsPending",false);Set(car,"PartsExploded",true);
                    Require(!(bool)Call(volumes,"TryAdd",car,camera),"Exploded car used a volume");Set(car,"PartsExploded",false);
                    camera.transform.position=((Transform)Get(car,"Root")).position+Vector3.up*3;
                    Require(!(bool)Call(volumes,"TryAdd",car,camera),"Near car used an approximate volume");
                    Debug.Log("SNOW_CAPPED_VOLUMES_OK: limits 0/64/35/1; selected IDs/coordinates/slopes, excluded silhouettes, background and ground parity; live movement, rotation, origin shift, orthographic, pending/exploded/near fallbacks.");
                }
            }
            catch(Exception e){Debug.LogException(e);result=1;}
            EditorApplication.Exit(result);
        }
        static void Verify(object fixture,object registry,string name,bool expectVolumes)
        {
            Set(registry,"ExclusionVolumesEnabled",false);
            var before=(Color[][])Call(fixture,"Draw",true);int oldDraws=Count(registry,"FrameDrawCount");
            Set(registry,"ExclusionVolumesEnabled",true);
            var after=(Color[][])Call(fixture,"Draw",true);int volumes=Count(registry,"FrameExclusionVolumeCount");
            int missing=0,wrongSnow=0,ground=0,snow=0,excluded=0;
            for(int i=0;i<before[0].Length;i++)
            {
                var a=before[0][i];var b=after[0][i];
                if(a.a>0){snow++;if(a.a!=b.a || before[1][i].r!=after[1][i].r || Mathf.Abs(a.r-b.r)>.002f || Mathf.Abs(a.g-b.g)>.002f || Mathf.Abs(a.b-b.b)>.002f)wrongSnow++;}
                else if(a.a<0){excluded++;if(b.a>=0)missing++;}
                else if(b.a!=0)ground++;
            }
            Debug.Log("CAPPED_VOLUME_DIFF "+name+": native_draws="+oldDraws+" reduced_draws="+Count(registry,"FrameDrawCount")+" volumes="+volumes+" selected_pixels="+snow+" excluded="+excluded+" selected_changes="+wrongSnow+" missing_exclusions="+missing+" background_changes="+ground);
            Require(snow>20 && excluded>100,"Empty fixture: "+name);
            Require(wrongSnow==0 && missing==0 && ground==0,"Surface parity failed: "+name);
            Require(expectVolumes?volumes>0:volumes==0,"Wrong volume eligibility: "+name);
            if(expectVolumes)Require(Count(registry,"FrameDrawCount")<oldDraws,"Capped car parts still submitted: "+name);
        }
        static void Benchmark(object fixture,object registry)
        {
            var original=new double[5];var optimized=new double[5];
            for(int round=0;round<5;round++)for(int half=0;half<2;half++)
            {
                bool volume=((round+half)&1)!=0;Set(registry,"ExclusionVolumesEnabled",volume);
                Call(fixture,"Time",true,12,true);
                var ms=(double)Call(fixture,"Time",true,60,true);
                if(volume)optimized[round]=ms;else original[round]=ms;
            }
            Array.Sort(original);Array.Sort(optimized);
            Set(registry,"ExclusionVolumesEnabled",false);double cpuBefore=(double)Call(fixture,"Time",true,180,false);
            Set(registry,"ExclusionVolumesEnabled",true);double cpuAfter=(double)Call(fixture,"Time",true,180,false);
            Debug.Log("CAPPED_VOLUME_BENCH cars="+(heavy?224:128)+" limit=35 render_ms="+original[2].ToString("F3")+"->"+optimized[2].ToString("F3")+" record_ms="+cpuBefore.ToString("F3")+"->"+cpuAfter.ToString("F3")+"; median of five alternating trials, includes native camera+GPU completion; not_game_FPS=true");
        }
    }
}
