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
using Debug=UnityEngine.Debug;

namespace DVSeasons.AssetBundleBuild
{
    // Deliberately LOD-heavy: the small eight-part instancing fixture does not
    // model the many alternate representations and interiors in a native yard.
    public static class SnowLodTraversalVerification
    {
        const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static int checks;
        static object Get(object o,string n){return o.GetType().GetField(n,All).GetValue(o);}
        static void Set(object o,string n,object v){o.GetType().GetField(n,All).SetValue(o,v);}
        static object Call(object o,string n,params object[] args){return o.GetType().GetMethod(n,All).Invoke(o,args);}
        static void Require(bool ok,string message){checks++;if(!ok)throw new InvalidOperationException(message);}
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME")??"F:/steam/steamapps/common/Derail Valley";
            ResolveEventHandler resolver=(s,a)=>{foreach(var dir in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var file=Path.Combine(dir,new AssemblyName(a.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;int result=0;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                var candidate=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_LOD_EXPERIMENT_DLL")??
                    Path.Combine(root,"artifacts/verification/winter-summer-lod-experimental/DVSeasons.dll");
                Require(File.Exists(candidate),"Rejected LOD experiment DLL unavailable: "+candidate);
                var mod=Assembly.LoadFrom(candidate);
                Require(mod.GetType("DVSeasons.Mod.SnowVehicleRegistry",true).GetField("IndexedVehiclePartsEnabled",All)!=null,
                    "The shipping registry excludes the rejected LOD traversal experiment. Select its archived DLL.");
                using(var f=new Fixture(mod,runtime))f.Verify();
                Debug.Log("SNOW_LOD_TRAVERSAL_OK checks="+checks+"; identical ordered visible part references/bounds and draw counts; timing is CPU recording, not live game FPS.");
            }
            catch(Exception e){Debug.LogException(e);result=1;}
            finally{AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(result);
        }
        sealed class Fixture:IDisposable
        {
            readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
            readonly List<Renderer> renderers=new List<Renderer>();
            readonly List<LODGroup> groups=new List<LODGroup>();
            readonly List<GameObject> interiors=new List<GameObject>();
            readonly List<GameObject> unusedGroups=new List<GameObject>();
            readonly object registry,rails,repository;
            readonly Camera camera;
            readonly CommandBuffer commands=new CommandBuffer();
            readonly IList vehicles;
            readonly List<object> expected=new List<object>();
            readonly List<Bounds> expectedBounds=new List<Bounds>();
            readonly List<bool> expectedVisibility=new List<bool>();
            readonly MethodInfo record;
            readonly object[] args;
            readonly Type performance;
            readonly Mesh mesh;
            readonly Material opaque,transparent;
            T Keep<T>(T o)where T:UnityEngine.Object{owned.Add(o);return o;}
            public Fixture(Assembly mod,string runtime)
            {
                QualitySettings.lodBias=1f;
                var fleet=Keep(new GameObject("LOD heavy yard"));
                camera=Keep(new GameObject("LOD heavy camera")).AddComponent<Camera>();camera.enabled=false;
                camera.transform.position=new Vector3(0,100,0);camera.transform.rotation=Quaternion.Euler(90,0,0);
                camera.orthographic=true;camera.orthographicSize=48;camera.aspect=1.6f;camera.nearClipPlane=.1f;camera.farClipPlane=250;
                camera.renderingPath=RenderingPath.DeferredShading;
                var target=Keep(new RenderTexture(800,500,24,RenderTextureFormat.ARGBHalf));target.Create();camera.targetTexture=target;
                opaque=Keep(new Material(Shader.Find("Standard")));transparent=Keep(new Material(opaque));transparent.renderQueue=3000;
                mesh=Keep(new Mesh{vertices=new[]{new Vector3(-.15f,0,-.15f),new Vector3(-.15f,0,.15f),new Vector3(.15f,0,.15f),new Vector3(.15f,0,-.15f)},
                    normals=new[]{Vector3.up,Vector3.up,Vector3.up,Vector3.up},triangles=new[]{0,1,2,0,2,3}});mesh.RecalculateBounds();
                var rt=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);repository=Activator.CreateInstance(rt,new object[]{runtime});
                rt.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{AssetBundle.LoadFromFile(Path.Combine(runtime,"AssetBundles/dvseasons_dv99"))});
                registry=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SnowVehicleRegistry",true),true);
                rails=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.RailSnowTracks",true),true);
                Require((bool)Call(registry,"Initialize",repository),"Registry initialize");
                for(int car=0;car<224;car++)
                {
                    var root=new GameObject("Car "+car);root.transform.SetParent(fleet.transform,false);
                    root.transform.localPosition=new Vector3((car%16-7.5f)*4.5f,0,(car/16-6.5f)*4.5f);
                    var interior=new GameObject("interior");interior.transform.SetParent(root.transform,false);interiors.Add(interior);
                    for(int g=0;g<4;g++)
                    {
                        var go=new GameObject("LOD "+g);go.transform.SetParent(g%2==0?root.transform:interior.transform,false);
                        go.transform.localPosition=new Vector3((g%2-.5f)*1.4f,0,(g/2-.5f)*1.4f);
                        var levels=new LOD[3];Renderer shared=null;
                        for(int level=0;level<3;level++)
                        {
                            var list=new List<Renderer>();
                            for(int p=0;p<6;p++)
                            {
                                var r=Part(go.transform,"L"+level+" part "+p,opaque,new Vector3((p%3-1)*.35f,level*.01f,(p/3-.5f)*.35f));list.Add(r);
                                if(level==0&&p==0)shared=r;
                            }
                            if(level>0)list.Add(shared);
                            levels[level]=new LOD(level==0?.03f:level==1?.014f:.001f,list.ToArray());
                        }
                        var lod=go.AddComponent<LODGroup>();lod.SetLODs(levels);lod.size=2f;lod.localReferencePoint=Vector3.zero;groups.Add(lod);
                    }
                    for(int p=0;p<8;p++)Part(root.transform,"Ungrouped "+p,opaque,new Vector3((p%4-1.5f)*.4f,.04f,(p/4-.5f)*.4f));
                    for(int g=0;g<4;g++)
                    {
                        var go=new GameObject("Transparent only LOD "+g);go.transform.SetParent(root.transform,false);
                        unusedGroups.Add(go);
                        var r=Part(go.transform,"Transparent",transparent,Vector3.zero);var lod=go.AddComponent<LODGroup>();lod.SetLODs(new[]{new LOD(0,new[]{r})});lod.size=1;
                    }
                    if(car%3==0)interior.SetActive(false);
                    Call(registry,"Register",root.transform,null,null);
                }
                vehicles=(IList)Get(registry,"vehicles");
                foreach(var v in vehicles){Call(registry,"RefreshParts",v);Set(v,"Ready",true);Set(v,"SnowReady",true);Set(v,"LocalBounds",new Bounds(Vector3.zero,new Vector3(3.5f,1,3.5f)));}
                record=registry.GetType().GetMethod("Record",All);args=new object[]{commands,camera,rails,false};
                performance=mod.GetType("DVSeasons.Mod.SnowPerformance",true);
            }
            Renderer Part(Transform parent,string name,Material material,Vector3 position)
            {
                var go=new GameObject(name);go.transform.SetParent(parent,false);go.transform.localPosition=position;
                go.AddComponent<MeshFilter>().sharedMesh=mesh;var r=go.AddComponent<MeshRenderer>();r.sharedMaterial=material;renderers.Add(r);return r;
            }
            void Record(bool indexed){Set(registry,"IndexedVehiclePartsEnabled",indexed);commands.Clear();record.Invoke(registry,args);}
            void Compare(string label)
            {
                Record(false);expected.Clear();expectedBounds.Clear();expectedVisibility.Clear();
                int draws=(int)registry.GetType().GetProperty("FrameDrawCount").GetValue(registry,null);
                foreach(var v in vehicles)
                {
                    expectedVisibility.Add((bool)Get(v,"FrameVisible"));
                    foreach(var part in (IList)Get(v,"FrameParts")){expected.Add(part);expectedBounds.Add((Bounds)Get(part,"VisibleBounds"));}
                }
                Record(true);int cursor=0,car=0;
                foreach(var v in vehicles)
                {
                    Require((bool)Get(v,"FrameVisible")==expectedVisibility[car++],label+" vehicle visibility");
                    foreach(var p in (IList)Get(v,"FrameParts"))
                    {
                        Require(cursor<expected.Count&&ReferenceEquals(p,expected[cursor]),label+" ordered visible renderer "+cursor);
                        Require(((Bounds)Get(p,"VisibleBounds")).Equals(expectedBounds[cursor]),label+" renderer bounds "+cursor);cursor++;
                    }
                }
                Require(cursor==expected.Count,label+" part count");
                Require(draws==(int)registry.GetType().GetProperty("FrameDrawCount").GetValue(registry,null),label+" draw count");
                Debug.Log("SNOW_LOD_TRAVERSAL_PARITY "+label+" parts="+cursor+" draws="+draws);
            }
            public void Verify()
            {
                Compare("LOD-heavy opaque, interior, shared membership and transparent-only groups");
                Benchmark("224 cars, inactive cabins and unused groups");
                foreach(var unused in unusedGroups)UnityEngine.Object.DestroyImmediate(unused);
                unusedGroups.Clear();renderers.RemoveAll(r=>r==null);
                foreach(var vehicle in vehicles)Call(registry,"RefreshParts",vehicle);
                Compare("ordinary opaque groups without unused groups");
                Benchmark("ordinary opaque groups, no unused groups");
                foreach(float size in new[]{24f,90f,500f}){camera.orthographicSize=size;Compare("orthographic size "+size);}camera.orthographicSize=48;
                foreach(var interior in interiors)interior.SetActive(true);
                foreach(var lod in groups){lod.enabled=false;lod.fadeMode=LODFadeMode.CrossFade;}
                Compare("disabled/fading interior conservative behavior");Benchmark("all interiors and conservative LODs");
                foreach(var lod in groups){lod.enabled=true;lod.fadeMode=LODFadeMode.None;lod.size*=1.1f;lod.localReferencePoint=new Vector3(.2f,.1f,0);}
                for(int i=0;i<renderers.Count;i+=37)renderers[i].enabled=false;
                for(int i=3;i<renderers.Count;i+=41)renderers[i].forceRenderingOff=true;
                for(int i=9;i<renderers.Count;i+=43)renderers[i].gameObject.layer=8;
                camera.cullingMask=~(1<<8);Compare("live group size/reference and renderer state/layer changes");
                var shared=(Renderer)((LODGroup)groups[0]).GetLODs()[0].renderers[0];
                var ambiguousObject=new GameObject("Ambiguous membership group");ambiguousObject.transform.SetParent(groups[0].transform.parent,false);
                var ambiguous=ambiguousObject.AddComponent<LODGroup>();ambiguous.SetLODs(new[]{new LOD(0,new[]{shared})});
                Call(registry,"RefreshParts",vehicles[0]);Compare("ambiguous group membership");
                UnityEngine.Object.DestroyImmediate(groups[2]);Compare("destroyed LOD group fallback");
                UnityEngine.Object.DestroyImmediate(renderers[7]);Compare("destroyed renderer fallback");
                camera.orthographic=false;camera.fieldOfView=55;camera.transform.position=new Vector3(3,25,-35);camera.transform.LookAt(Vector3.zero);Compare("perspective camera and partial frustum");
                Set(registry,"OrderedVehicleBatchesEnabled",false);Compare("native scheduling fallback");
            }
            long Counter(string name)
            {
                var values=(IDictionary)performance.GetField("counters",All).GetValue(null);
                return values.Contains(name)?(long)Get(values[name],"Ticks"):0;
            }
            void Benchmark(string label)
            {
                for(int i=0;i<8;i++){Record(false);Record(true);}
                var original=new List<double>();var indexed=new List<double>();
                long[] detail=new long[4];const int trials=7,samples=12;
                for(int trial=0;trial<trials;trial++)for(int mode=0;mode<2;mode++)
                {
                    bool candidate=((mode+trial)&1)!=0;long lod=Counter("snow-vehicle-lod"),parts=Counter("snow-vehicle-parts");
                    var watch=Stopwatch.StartNew();for(int sample=0;sample<samples;sample++)Record(candidate);watch.Stop();
                    (candidate?indexed:original).Add(watch.Elapsed.TotalMilliseconds/samples);
                    detail[candidate?2:0]+=Counter("snow-vehicle-lod")-lod;detail[candidate?3:1]+=Counter("snow-vehicle-parts")-parts;
                }
                original.Sort();indexed.Sort();double scale=1000d/Stopwatch.Frequency/(trials*samples);
                Debug.Log("SNOW_LOD_TRAVERSAL_BENCH "+label+" original_median_ms="+original[trials/2].ToString("F4")+" indexed_median_ms="+indexed[trials/2].ToString("F4")+
                    " original_lod_parts_ms="+(detail[0]*scale).ToString("F4")+"/"+(detail[1]*scale).ToString("F4")+
                    " indexed_lod_parts_ms="+(detail[2]*scale).ToString("F4")+"/"+(detail[3]*scale).ToString("F4")+" cars=224 renderers="+renderers.Count+" groups="+groups.Count+" unusedGroups="+unusedGroups.Count);
            }
            public void Dispose()
            {
                commands.Dispose();((IDisposable)registry).Dispose();((IDisposable)rails).Dispose();((IDisposable)repository).Dispose();
                for(int i=owned.Count-1;i>=0;i--)if(owned[i]!=null)UnityEngine.Object.DestroyImmediate(owned[i]);
            }
        }
    }
}
