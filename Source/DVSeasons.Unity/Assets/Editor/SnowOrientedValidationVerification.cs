using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    // Runs the real registry validation without a graphics device or a game
    // assembly. Each accepted box is checked against its actual source corners.
    public static class SnowOrientedValidationVerification
    {
        const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static int checks,submits,fast,detailed,meshReads;
        static object Field(object target,string name) {return target.GetType().GetField(name,All).GetValue(target);}
        static void Set(object target,string name,object value) {target.GetType().GetField(name,All).SetValue(target,value);}
        static object Call(object target,string name,params object[] args) {return target.GetType().GetMethod(name,All).Invoke(target,args);}
        static object Property(object target,string name) {return target.GetType().GetProperty(name,All).GetValue(target,null);}
        static void Require(bool condition,string message) {checks++;if(!condition)throw new InvalidOperationException(message);}
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME")??"F:/steam/steamapps/common/Derail Valley";
            ResolveEventHandler resolver=(sender,args)=>{
                foreach(var directory in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {string path=Path.Combine(directory,new AssemblyName(args.Name).Name+".dll");if(File.Exists(path))return Assembly.LoadFrom(path);}return null;};
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;int result=0;
            try {Verify(Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll")));}
            catch(Exception error) {Debug.LogException(error);result=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(result);
        }
        static MeshRenderer Cube(Transform parent,string name,Material material,Vector3 position,Vector3 scale)
        {
            var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(parent,false);
            go.transform.localPosition=position;go.transform.localScale=scale;
            var renderer=go.GetComponent<MeshRenderer>();renderer.sharedMaterial=material;return renderer;
        }
        static object Part(object vehicle,Renderer renderer)
        {foreach(var part in (IList)Field(vehicle,"Parts"))if((Renderer)Field(part,"Renderer")==renderer)return part;throw new Exception("Missing part");}
        static void Verify(Assembly mod)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var scene=new GameObject("OBB validation fixture");var material=new Material(Shader.Find("Standard"));
            object registry=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SnowVehicleRegistry",true),true);
            var ownedMeshes=new List<Mesh>();
            try
            {
                var root=new GameObject("Vehicle");root.transform.SetParent(scene.transform,false);root.transform.localRotation=Quaternion.Euler(0,43,0);
                var body=Cube(root.transform,"Body",material,Vector3.zero,new Vector3(3,3,18));
                var bogie=new GameObject("Bogie");bogie.transform.SetParent(root.transform,false);
                var moving=Cube(bogie.transform,"Moving part",material,new Vector3(0,-.5f,3),Vector3.one*.3f);
                for(int i=0;i<16;i++)Cube(root.transform,"Rigid detail "+i,material,new Vector3(0,(i%3-1)*.2f,(i/3-2)*.4f),Vector3.one*.2f);
                var body2=Cube(root.transform,"Second body using same mesh",material,new Vector3(0,0,.1f),new Vector3(2.9f,2.9f,17.8f));
                var skinGo=new GameObject("Skin");skinGo.transform.SetParent(root.transform,false);
                var skin=skinGo.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=body.GetComponent<MeshFilter>().sharedMesh;
                skin.sharedMaterial=material;skin.localBounds=new Bounds(Vector3.zero,Vector3.one*.4f);skin.updateWhenOffscreen=true;
                Call(registry,"Register",root.transform,null,null);
                var vehicle=((IList)Field(registry,"vehicles"))[0];Call(registry,"RefreshParts",vehicle);
                Submit(registry,vehicle,.125f,"first rigid frame");
                Require((int)Property(registry,"FrameOrientedFastCount")>=16,"Rigid details did not take zero-native-call guard");
                Require((int)Property(registry,"FrameOrientedDetailedCount")>=2,"Large diagonal bodies did not retain exact fallback");
                Require((int)Property(registry,"FrameOrientedMeshBoundsReadCount")==1,"Shared body mesh bounds read more than once per submit");
                Require((bool)Field(Part(vehicle,body),"OrientedGuardRejected"),"Large body did not remember its failed inexpensive guard");
                for(int frame=0;frame<120;frame++)
                {
                    root.transform.localPosition=new Vector3(Mathf.Sin(frame)*.004f,0,Mathf.Cos(frame)*.005f);
                    root.transform.localRotation=Quaternion.Euler(0,43+Mathf.Sin(frame)*.003f,0);
                    moving.transform.hasChanged=true;
                    Submit(registry,vehicle,.125f,"rigid root jitter "+frame);
                    Require((int)Property(registry,"FrameOrientedFastCount")>=16,"Root jitter disabled rigid fast guard");
                    Require(moving.transform.hasChanged,"Validation reset another system's transform flag");
                }
                // No load event or RefreshParts accompanies any of these changes.
                moving.transform.localScale=new Vector3(8,2,5);bogie.transform.localRotation=Quaternion.Euler(18,65,12);
                Submit(registry,vehicle,.125f,"bogie rotation and local scale");
                moving.transform.SetParent(scene.transform,true);moving.transform.position+=new Vector3(8,2,-9);
                Submit(registry,vehicle,.125f,"detached animated part");
                var mesh=UnityEngine.Object.Instantiate(body.GetComponent<MeshFilter>().sharedMesh);ownedMeshes.Add(mesh);
                body.GetComponent<MeshFilter>().sharedMesh=mesh;
                Submit(registry,vehicle,.125f,"mesh replacement without refresh");
                Require((int)Call(registry,"GeometryReason",Part(vehicle,body))==5,"Mesh replacement used stale instanced mesh");
                Call(registry,"RefreshParts",vehicle);
                mesh.bounds=new Bounds(new Vector3(4,1,0),new Vector3(10,2,4));
                Submit(registry,vehicle,.125f,"same mesh bounds mutated");
                var stream=UnityEngine.Object.Instantiate(body2.GetComponent<MeshFilter>().sharedMesh);ownedMeshes.Add(stream);
                body2.additionalVertexStreams=stream;
                Submit(registry,vehicle,.125f,"additional vertex streams added");
                Require((int)Call(registry,"GeometryReason",Part(vehicle,body2))==3,"Runtime streams reused rigid eligibility");
                body2.additionalVertexStreams=null;
                skin.localBounds=new Bounds(new Vector3(20,3,0),new Vector3(4,3,5));
                Submit(registry,vehicle,.125f,"skinned bounds animate");
                root.transform.localScale=new Vector3(-2,1.5f,.7f);
                Submit(registry,vehicle,1f,"mirrored scaled root and padding grows");
                root.transform.position=new Vector3(28000,1500,-34000);
                Submit(registry,vehicle,1f,"distant position/origin shift");
                root.transform.localScale=Vector3.one;root.transform.position=Vector3.zero;
                var staticRoot=new GameObject("Static batched pair");staticRoot.transform.SetParent(root.transform,false);
                body.transform.SetParent(staticRoot.transform,true);body2.transform.SetParent(staticRoot.transform,true);
                StaticBatchingUtility.Combine(staticRoot);
                Require(body.isPartOfStaticBatch && body2.isPartOfStaticBatch,"Static batching fixture did not create a batch");
                Submit(registry,vehicle,.125f,"runtime static batching");
                Require((int)Call(registry,"GeometryReason",Part(vehicle,body2))==2,"Static batch reused ordinary geometry");
                Require(((IDictionary)Field(registry,"orientedMeshBounds")).Count==0,"Temporary mesh bounds retained after submit");
                VerifyGuard(mod,registry);
                ((IDisposable)registry).Dispose();
                Require((int)Property(registry,"FrameOrientedFastCount")==0 && (int)Property(registry,"FrameOrientedDetailedCount")==0,
                    "Dispose retained per-submit counters");
                Debug.Log("SNOW_ORIENTED_VALIDATION_OK: checks="+checks+", submissions="+submits+", fast="+fast+
                    ", detailed="+detailed+", native shared mesh bounds reads="+meshReads+
                    "; root jitter, bogie motion, detach, mutable/replaced meshes, streams, skin, static batch, mirror, padding and origin shift; exact corner guards.");
            }
            finally {((IDisposable)registry).Dispose();UnityEngine.Object.DestroyImmediate(scene);foreach(var mesh in ownedMeshes)UnityEngine.Object.DestroyImmediate(mesh);UnityEngine.Object.DestroyImmediate(material);}
        }
        static void Submit(object registry,object vehicle,float padding,string context)
        {
            Set(registry,"exclusionFrame",(int)Field(registry,"exclusionFrame")+1);
            foreach(string property in new[]{"FrameOrientedFastCount","FrameOrientedDetailedCount","FrameOrientedMeshBoundsReadCount"})
                registry.GetType().GetProperty(property,All).SetValue(registry,0,null);
            var frames=(IList)Field(registry,"frameVehicles");frames.Clear();frames.Add(vehicle);
            var parts=(IList)Field(vehicle,"FrameParts");parts.Clear();
            var root=(Transform)Field(vehicle,"Root");var topology=Field(vehicle,"Topology");
            Call(topology,"Update",root.localToWorldMatrix);var bounds=new Bounds();bool known=false;
            foreach(var part in (IList)Field(vehicle,"Parts"))
            {
                var renderer=(Renderer)Field(part,"Renderer");if(renderer==null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)continue;
                Bounds visible=renderer.bounds;Set(part,"VisibleBounds",visible);parts.Add(part);
                if(known)bounds.Encapsulate(visible);else{bounds=visible;known=true;}
            }
            if(!(bool)Call(topology,"ContainsGeometry",bounds))
            {
                foreach(var part in parts)registry.GetType().GetMethod("IncludeTopologyPart",All).Invoke(null,new[]{vehicle,part,(object)root.worldToLocalMatrix});
                Call(topology,"Update",root.localToWorldMatrix);
            }
            Call(topology,"EnsureContains",bounds,padding);Call(registry,"ValidateOrientedVehicles");
            var box=Property(Field(vehicle,"Oriented"),"World");
            Require((bool)Field(box,"Valid"),context+": oriented envelope unexpectedly disabled");
            foreach(var part in parts)
            {
                var renderer=(Renderer)Field(part,"Renderer");var skin=renderer as SkinnedMeshRenderer;
                var current=renderer.GetComponent<MeshFilter>();var mr=renderer as MeshRenderer;Bounds source;Matrix4x4 matrix;
                if(skin!=null){source=skin.localBounds;matrix=renderer.localToWorldMatrix;}
                else if(!renderer.isPartOfStaticBatch && mr.additionalVertexStreams==null && current.sharedMesh==(Mesh)Field(part,"Mesh"))
                {source=current.sharedMesh.bounds;matrix=renderer.localToWorldMatrix;}
                else {source=renderer.bounds;matrix=Matrix4x4.identity;}
                Require(CornersInside(box,source,matrix,(float)Property(topology,"Padding"),.003d),context+": escaped source corners "+renderer.name);
            }
            fast+=(int)Property(registry,"FrameOrientedFastCount");detailed+=(int)Property(registry,"FrameOrientedDetailedCount");
            meshReads+=(int)Property(registry,"FrameOrientedMeshBoundsReadCount");submits++;
        }
        static bool CornersInside(object box,Bounds bounds,Matrix4x4 matrix,float padding,double tolerance)
        {
            if(!(bool)Field(box,"Valid"))return false;
            var center=(Vector3)Field(box,"Center");var extents=(Vector3)Field(box,"Extents");
            var axes=new[]{(Vector3)Field(box,"AxisX"),(Vector3)Field(box,"AxisY"),(Vector3)Field(box,"AxisZ")};
            var lo=bounds.min;var hi=bounds.max;
            for(int corner=0;corner<8;corner++)
            {
                double x=(corner&1)==0?lo.x:hi.x,y=(corner&2)==0?lo.y:hi.y,z=(corner&4)==0?lo.z:hi.z;
                double wx=matrix.m00*x+matrix.m01*y+matrix.m02*z+matrix.m03-center.x;
                double wy=matrix.m10*x+matrix.m11*y+matrix.m12*z+matrix.m13-center.y;
                double wz=matrix.m20*x+matrix.m21*y+matrix.m22*z+matrix.m23-center.z;
                for(int axis=0;axis<3;axis++)if(!(Math.Abs(wx*axes[axis].x+wy*axes[axis].y+wz*axes[axis].z)+padding<=extents[axis]+tolerance))return false;
            }
            return true;
        }
        static void VerifyGuard(Assembly mod,object registry)
        {
            var type=mod.GetType("DVSeasons.Mod.SnowOrientedBounds",true);var random=new System.Random(712843);
            for(int i=0;i<2000;i++)
            {
                var pose=Matrix4x4.TRS(new Vector3(i%31-15,i%17-8,i%43-21),Quaternion.Euler(i%83,i%167,i%91),Vector3.one);
                var box=type.GetMethod("Create",All).Invoke(null,new object[]{new Bounds(Vector3.zero,new Vector3(3,5,18)),pose,.4f});
                var source=new Bounds(pose.MultiplyPoint3x4(new Vector3((float)random.NextDouble()*8-4,(float)random.NextDouble()*10-5,(float)random.NextDouble()*22-11)),Vector3.one*(float)random.NextDouble());
                bool accepted=(bool)registry.GetType().GetMethod("ContainsOrientedWorldBounds",All).Invoke(null,new[]{box,(object)source,.125f});
                if(accepted)Require(CornersInside(box,source,Matrix4x4.identity,.125f,1e-6),"Fast guard accepted an escaping corner");
            }
            var invalid=Activator.CreateInstance(type);
            Require(!(bool)registry.GetType().GetMethod("ContainsOrientedWorldBounds",All).Invoke(null,new[]{invalid,(object)new Bounds(),0f}),"Invalid OBB accepted geometry");
        }
    }
}
