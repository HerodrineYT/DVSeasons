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
    public static class SnowPartsRefreshVerification
    {
        const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static int checks, refreshes, referenceQueries, optimizedQueries;
        static object Field(object target,string name) {return target.GetType().GetField(name,All).GetValue(target);}
        static void Set(object target,string name,object value) {target.GetType().GetField(name,All).SetValue(target,value);}
        static object Call(object target,string name,params object[] values) {return target.GetType().GetMethod(name,All).Invoke(target,values);}
        static void Require(bool condition,string message) {checks++;if(!condition)throw new InvalidOperationException(message);}
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME")??"F:/steam/steamapps/common/Derail Valley";
            ResolveEventHandler resolver=(sender,args)=>{
                foreach(var directory in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {string file=Path.Combine(directory,new AssemblyName(args.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;int result=0;
            try {Verify(Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll")));}
            catch(Exception error) {Debug.LogException(error);result=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(result);
        }
        static GameObject Branch(Transform parent,string name)
        {var value=new GameObject(name);value.transform.SetParent(parent,false);return value;}
        static MeshRenderer Cube(Transform parent,string name,Material material)
        {
            var value=GameObject.CreatePrimitive(PrimitiveType.Cube);value.name=name;value.transform.SetParent(parent,false);
            var renderer=value.GetComponent<MeshRenderer>();renderer.sharedMaterial=material;return renderer;
        }
        static void Verify(Assembly mod)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var scene=new GameObject("Parts refresh fixture");
            var material=new Material(Shader.Find("Standard"));
            var cutout=new Material(Shader.Find("Standard"));cutout.SetOverrideTag("RenderType","TransparentCutout");cutout.SetFloat("_Cutoff",.37f);
            cutout.mainTexture=Texture2D.whiteTexture;cutout.mainTextureScale=new Vector2(2,3);cutout.mainTextureOffset=new Vector2(.2f,.3f);
            object registry=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SnowVehicleRegistry",true),true);
            try
            {
                var root=Branch(scene.transform,"Vehicle");Cube(root.transform,"Body",material);
                var interior=Branch(root.transform,"interior container");var loaded=Branch(interior.transform,"loaded cab");
                Cube(loaded.transform,"Cab panels",cutout);
                var lodRoot=Branch(interior.transform,"interior LOD");var near=Cube(lodRoot.transform,"Near",material);var far=Cube(lodRoot.transform,"Far",material);
                var shared=Cube(lodRoot.transform,"Shared",material);var lod=lodRoot.AddComponent<LODGroup>();
                lod.SetLODs(new[]{new LOD(.4f,new Renderer[]{near,shared}),new LOD(.1f,new Renderer[]{far,shared})});
                var cargo=Branch(loaded.transform,"Cargo");Cube(cargo.transform,"Exposed freight",material);
                var external=Branch(interior.transform,"External");Cube(external.transform,"Exposed controls",material);
                var inactive=Cube(loaded.transform,"Inactive cab detail",material);inactive.gameObject.SetActive(false);
                var shadows=Cube(root.transform,"Shadows only",material);shadows.shadowCastingMode=ShadowCastingMode.ShadowsOnly;
                Branch(root.transform,"Other renderer").AddComponent<LineRenderer>();
                Call(registry,"Register",root.transform,interior.transform,lodRoot.transform);
                var vehicle=((IList)Field(registry,"vehicles"))[0];Set(vehicle,"Cargo",cargo.transform);Set(vehicle,"External",external.transform);Set(vehicle,"DummyExternal",external.transform);
                Check(registry,vehicle,"nested roots and inactive/shared LOD");
                Require(Queries(registry)==2,"Nested six-root hierarchy was traversed more than once per component type");
                Check(registry,vehicle,"repeat with pooled scratch");

                interior.transform.SetParent(scene.transform,true);Check(registry,vehicle,"detached interior");
                Require(Queries(registry)==4,"Detached interior was omitted or repeatedly scanned");
                // Descendant first, ancestor later: retaining the original root
                // order matters more than eliminating this unusual duplicate.
                Set(vehicle,"Interior",loaded.transform);Set(vehicle,"InteriorLod",interior.transform);
                Check(registry,vehicle,"earlier child and later ancestor order");
                Set(vehicle,"Interior",interior.transform);Set(vehicle,"InteriorLod",lodRoot.transform);

                var ambiguous=Branch(interior.transform,"Second group").AddComponent<LODGroup>();
                ambiguous.SetLODs(new[]{new LOD(.2f,new Renderer[]{shared})});
                Check(registry,vehicle,"ambiguous LOD membership");
                UnityEngine.Object.DestroyImmediate(ambiguous.gameObject);
                inactive.gameObject.SetActive(true);Check(registry,vehicle,"inactive detail enabled");
                UnityEngine.Object.DestroyImmediate(loaded);Set(vehicle,"Cargo",null);
                Check(registry,vehicle,"cab and cargo unloaded under same parent");
                loaded=Branch(interior.transform,"replacement cab");Cube(loaded.transform,"New cab panels",material);
                cargo=Branch(loaded.transform,"new cargo");Cube(cargo.transform,"Replacement freight",cutout);Set(vehicle,"Cargo",cargo.transform);
                Check(registry,vehicle,"cab and cargo replaced under same parent");
                Set(vehicle,"External",null);Set(vehicle,"DummyExternal",external.transform);
                Check(registry,vehicle,"real external converted to same dummy object");
                UnityEngine.Object.DestroyImmediate(external);Set(vehicle,"DummyExternal",null);
                Check(registry,vehicle,"dummy destroyed without load event");
                external=Branch(interior.transform,"replacement external");Cube(external.transform,"New external",material);Set(vehicle,"External",external.transform);
                material.renderQueue=3000;cutout.SetFloat("_Cutoff",.62f);cutout.mainTextureScale=new Vector2(.5f,2f);
                Check(registry,vehicle,"same material and branch properties changed");
                material.renderQueue=2000;
                for(int iteration=0;iteration<20;iteration++)Check(registry,vehicle,"pooled repeat "+iteration);
                Debug.Log("SNOW_PARTS_REFRESH_OK: "+checks+" checks, "+refreshes+" refreshes; exact original renderer/part/LOD order and masks, classification and material data; nested/detached/inactive/ancestor-last roots; dynamic cab/cargo/external/dummy replacement; original hierarchy queries="+referenceQueries+", optimized="+optimizedQueries+"; scratch references cleared after every refresh.");
            }
            finally
            {
                ((IDisposable)registry).Dispose();UnityEngine.Object.DestroyImmediate(scene);
                UnityEngine.Object.DestroyImmediate(material);UnityEngine.Object.DestroyImmediate(cutout);
            }
        }
        static int Queries(object registry) {return (int)registry.GetType().GetProperty("LastPartsHierarchyQueries",All).GetValue(registry,null);}
        static void Check(object registry,object vehicle,string context)
        {
            var roots=new Transform[6];string[] names={"Root","Interior","InteriorLod","Cargo","External","DummyExternal"};
            for(int i=0;i<roots.Length;i++)roots[i]=(Transform)Field(vehicle,names[i]);
            var renderers=new List<Renderer>();var groups=new List<LODGroup>();var seenRenderers=new HashSet<int>();var seenGroups=new HashSet<int>();
            var membership=new Dictionary<Renderer,Tuple<LODGroup,int>>();
            foreach(var root in roots)
            {
                if(root==null)continue;referenceQueries+=2;
                foreach(var group in root.GetComponentsInChildren<LODGroup>(true))
                {
                    if(!seenGroups.Add(group.GetInstanceID()))continue;groups.Add(group);var levels=group.GetLODs();
                    for(int i=0;i<levels.Length;i++)foreach(var renderer in levels[i].renderers)
                    {
                        if(renderer==null)continue;Tuple<LODGroup,int> old;
                        membership[renderer]=membership.TryGetValue(renderer,out old)?
                            old.Item1==group&&i<32?Tuple.Create(group,old.Item2|(1<<i)):Tuple.Create<LODGroup,int>(null,0):
                            i<32?Tuple.Create(group,1<<i):Tuple.Create<LODGroup,int>(null,0);
                    }
                }
                foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
                    if(renderer!=null&&seenRenderers.Add(renderer.GetInstanceID()))renderers.Add(renderer);
            }
            Call(registry,"RefreshParts",vehicle);refreshes++;optimizedQueries+=Queries(registry);
            var actualRenderers=(IList)Field(vehicle,"CaptureRenderers");Require(actualRenderers.Count==renderers.Count,context+": capture renderer count");
            for(int i=0;i<renderers.Count;i++)Require(ReferenceEquals(actualRenderers[i],renderers[i]),context+": capture traversal order at "+i);
            var actualGroups=(IList)Field(vehicle,"Lods");Require(actualGroups.Count==groups.Count,context+": LOD group count");
            for(int i=0;i<groups.Count;i++)Require(ReferenceEquals(Field(actualGroups[i],"Group"),groups[i]),context+": LOD traversal order");
            var expectedParts=new List<Renderer>();foreach(var renderer in renderers)
                if((renderer is MeshRenderer||renderer is SkinnedMeshRenderer)&&renderer.shadowCastingMode!=ShadowCastingMode.ShadowsOnly&&
                    (renderer.GetComponent<MeshFilter>()!=null?renderer.GetComponent<MeshFilter>().sharedMesh!=null:((SkinnedMeshRenderer)renderer).sharedMesh!=null))expectedParts.Add(renderer);
            var actualParts=(IList)Field(vehicle,"Parts");Require(actualParts.Count==expectedParts.Count,context+": part count");
            for(int i=0;i<expectedParts.Count;i++)
            {
                var renderer=expectedParts[i];var part=actualParts[i];Require(ReferenceEquals(Field(part,"Renderer"),renderer),context+": part order");
                bool interior=roots[1]!=null&&renderer.transform.IsChildOf(roots[1])||roots[2]!=null&&renderer.transform.IsChildOf(roots[2]);
                for(var node=renderer.transform;node!=null&&node!=roots[0];node=node.parent)
                    if(node.name.IndexOf("interior",StringComparison.OrdinalIgnoreCase)>=0||node.name.StartsWith("[axle]",StringComparison.OrdinalIgnoreCase))interior=true;
                for(int r=3;r<6;r++)if(roots[r]!=null&&renderer.transform.IsChildOf(roots[r]))interior=false;
                Require((bool)Field(part,"Interior")==interior,context+": exposed cargo/external classification");
                Tuple<LODGroup,int> expected;membership.TryGetValue(renderer,out expected);var actual=Field(part,"Lod");
                Require(ReferenceEquals(actual==null?null:Field(actual,"Group"),expected==null?null:expected.Item1),context+": LOD association");
                Require((int)Field(part,"LodMask")== (expected==null?0:expected.Item2),context+": shared/ambiguous LOD mask");
                var materials=renderer.sharedMaterials;var opaque=(bool[])Field(part,"Opaque");var cutoff=(float[])Field(part,"Cutoff");
                var uv=(Vector4[])Field(part,"ST");var albedo=(Texture[])Field(part,"Albedo");
                for(int slot=0;slot<opaque.Length;slot++)
                {
                    var material=materials[slot];bool isOpaque=material!=null&&material.renderQueue<=2500;
                    Require(opaque[slot]==isOpaque,context+": material opacity");if(!isOpaque)continue;
                    float expectedCutoff=material.GetTag("RenderType",false)=="TransparentCutout"?(material.HasProperty("_Cutoff")?material.GetFloat("_Cutoff"):.5f):0;
                    Require(cutoff[slot]==expectedCutoff,context+": material cutoff");
                    var scale=material.HasProperty("_MainTex")?material.GetTextureScale("_MainTex"):Vector2.one;
                    var offset=material.HasProperty("_MainTex")?material.GetTextureOffset("_MainTex"):Vector2.zero;
                    Require(uv[slot]==new Vector4(scale.x,scale.y,offset.x,offset.y),context+": material UV");
                    Require(albedo[slot]==(material.HasProperty("_MainTex")?material.GetTexture("_MainTex"):null),context+": material texture");
                }
            }
            foreach(string field in new[]{"partRootsScratch","partsScratch","partRenderersScratch","partGroupsScratch","partSeenScratch","partGroupsSeenScratch","partLodsScratch"})
            {var scratch=Field(registry,field);Require((int)scratch.GetType().GetProperty("Count").GetValue(scratch,null)==0,context+": retained scratch references in "+field);}
        }
    }
}
