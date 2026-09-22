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
    // Production CPU work with real Unity meshes/hierarchies. No timing claim or
    // visual quality approximation: check exact data and eliminated native work.
    public static class SnowWorldWorkVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static int checks;
        static object Get(object target,string name) {return target.GetType().GetField(name,All).GetValue(target);}
        static void Set(object target,string name,object value) {target.GetType().GetField(name,All).SetValue(target,value);}
        static object Call(object target,string name,params object[] args) {return target.GetType().GetMethod(name,All).Invoke(target,args);}
        static int Property(object target,string name) {return (int)target.GetType().GetProperty(name,All).GetValue(target,null);}
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
            try
            {
                var mod=Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll"));
                VerifyRailIndex(mod);VerifyRails(mod);VerifyDiscovery(mod);VerifyMidScanMutation(mod);
                Debug.Log("SNOW_WORLD_WORK_OK checks="+checks+"; deferred offscreen uploads, live CPU bounds, view return, boundary/stereo visibility, origin shifts, save/restore/refill; bounded DFS, inactive/late/destroyed scene nodes.");
            }
            catch(Exception error) {Debug.LogException(error);result=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(result);
        }

        static Plane[] Box(Vector3 center,float halfSize)
        {
            return new[]{new Plane(Vector3.right,center-Vector3.right*halfSize),new Plane(Vector3.left,center+Vector3.right*halfSize),
                new Plane(Vector3.up,center-Vector3.up*halfSize),new Plane(Vector3.down,center+Vector3.up*halfSize),
                new Plane(Vector3.forward,center-Vector3.forward*halfSize),new Plane(Vector3.back,center+Vector3.forward*halfSize)};
        }
        sealed class ReferenceMark
        {
            internal object Runtime;
            internal Vector3 A,B,Width;
            internal float Stamp;
            internal bool Active=true;
            internal readonly HashSet<Vector3Int> Cells=new HashSet<Vector3Int>();
        }
        static Vector3Int Cell(Vector3 p)
        {return new Vector3Int(Mathf.FloorToInt(p.x/8),Mathf.FloorToInt(p.y/8),Mathf.FloorToInt(p.z/8));}
        static void Publish(object index,ReferenceMark mark)
        {
            Set(mark.Runtime,"A",mark.A);Set(mark.Runtime,"B",mark.B);Set(mark.Runtime,"Width",mark.Width);Set(mark.Runtime,"Stamp",mark.Stamp);
            Call(index,"Update",mark.Runtime);
            // Independent original cell-enumeration path, including retained
            // cells after an extending endpoint changes direction.
            var extent=new Vector3(mark.Width.magnitude,.12f,mark.Width.magnitude);
            var min=Cell(Vector3.Min(mark.A,mark.B)-extent);var max=Cell(Vector3.Max(mark.A,mark.B)+extent);
            for(int x=min.x;x<=max.x;x++)for(int y=min.y;y<=max.y;y++)for(int z=min.z;z<=max.z;z++)mark.Cells.Add(new Vector3Int(x,y,z));
        }
        static float OriginalRemaining(List<ReferenceMark> marks,Vector3 point,float clock)
        {
            float remaining=1;var cell=Cell(point);
            foreach(var mark in marks)
            {
                if(!mark.Active || !mark.Cells.Contains(cell))continue;
                float age=Mathf.Clamp01(clock-mark.Stamp);if(age>=remaining)continue;
                var axis=mark.B-mark.A;float length=axis.sqrMagnitude;if(length<.000001f)continue;
                float t=Vector3.Dot(point-mark.A,axis)/length;if(t<0 || t>1)continue;
                var delta=point-Vector3.Lerp(mark.A,mark.B,t);float width=mark.Width.magnitude;if(width<.00001f)continue;
                float across=Vector3.Dot(delta,mark.Width.normalized);
                if((delta-mark.Width.normalized*across).sqrMagnitude>.12f*.12f)continue;
                float edge=Mathf.InverseLerp(.65f,1,Mathf.Abs(across)/width);
                remaining=Mathf.Min(remaining,1-(1-Mathf.SmoothStep(0,1,edge))*(1-age));
                if(remaining<=.001f)return 0;
            }
            return remaining;
        }
        static void VerifyRailIndex(Assembly mod)
        {
            var type=mod.GetType("DVSeasons.Mod.RailSnowContactIndex",true);
            var markType=type.GetNestedType("Mark",All);var index=Activator.CreateInstance(type,true);
            var marks=new List<ReferenceMark>();var random=new System.Random(61039);
            for(int i=0;i<160;i++)
            {
                var a=new Vector3((float)random.NextDouble()*64-32,(float)random.NextDouble()*3,(float)random.NextDouble()*64-32);
                var rotation=Quaternion.Euler(0,(float)random.NextDouble()*360,0);
                var mark=new ReferenceMark {Runtime=Activator.CreateInstance(markType,true),A=a,
                    B=a+rotation*Vector3.forward*(1+(float)random.NextDouble()*7),Width=rotation*Vector3.right*.085f,Stamp=(float)random.NextDouble()};
                marks.Add(mark);Publish(index,mark);
            }
            int samples=0;
            for(int frame=0;frame<8;frame++)
            {
                foreach(var mark in marks)
                {
                    mark.B+=new Vector3((float)random.NextDouble()*.5f,0,(float)random.NextDouble()*.5f);
                    if(frame==3)mark.Width*=1.25f;
                    Publish(index,mark);Publish(index,mark); // same-cell cache hit
                }
                if(frame==4)for(int i=0;i<marks.Count;i+=7)
                {Call(index,"Remove",marks[i].Runtime);marks[i].Active=false;marks[i].Cells.Clear();}
                if(frame==5)for(int i=0;i<marks.Count;i+=7)
                {marks[i].Active=true;Publish(index,marks[i]);}
                for(int sample=0;sample<900;sample++)
                {
                    var mark=marks[random.Next(marks.Count)];
                    var point=Vector3.LerpUnclamped(mark.A,mark.B,(float)random.NextDouble()*1.1f-.05f)+
                        mark.Width*((float)random.NextDouble()*3-1.5f)+Vector3.up*((float)random.NextDouble()*.4f-.2f);
                    float clock=frame*.15f;
                    float expected=OriginalRemaining(marks,point,clock),actual=(float)Call(index,"Remaining",point,clock);
                    Require(Mathf.Abs(expected-actual)<.000001f,"Cached rail geometry differs from original live calculation at sample "+samples);
                    samples++;
                }
            }
            Debug.Log("WORLD_RAIL_INDEX_PARITY_OK original-equation-samples="+samples+"; extension/width changes, identical-cell hits, remove/reinsert and snowfall aging.");
        }
        static void VerifyRails(Assembly mod)
        {
            var tracks=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.RailSnowTracks",true),true);
            var commands=new CommandBuffer();
            var material=new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/SnowVehicle.shader"));
            var near=Box(Vector3.zero,10f);var distant=Box(new Vector3(1000,0,0),100f);
            try
            {
                // Separate chunks are all outside the active view. Newly written
                // ribbons must not force an exclusion pass or a GPU mesh upload.
                for(int i=0;i<1800;i++)
                    Call(tracks,"WheelAt",i,new Vector3(1000+i%40,0,i/40),Vector3.right,Vector3.forward);
                Require(!Visible(tracks,near,null),"Dirty offscreen ribbons force a visible-track pass");
                Call(tracks,"Record",commands,material,near,null);
                Require(Property(tracks,"MeshUploadCount")==0,"Offscreen dirty ribbons uploaded geometry");
                AssertEnvelopes(tracks,false);
                Require(Visible(tracks,near,distant),"Right-eye-only ribbons were culled");
                Call(tracks,"Record",commands,material,near,distant);
                int chunks=((IList)Get(tracks,"chunks")).Count;
                Require(Property(tracks,"MeshUploadCount")==chunks,"First visible frame did not upload every deferred chunk");
                AssertEnvelopes(tracks,true);
                int uploaded=Property(tracks,"MeshUploadCount");
                commands.Clear();Call(tracks,"Record",commands,material,near,distant);
                Require(Property(tracks,"MeshUploadCount")==uploaded,"Unchanged visible geometry was uploaded twice");
                // Extension beyond the old mesh bound remains visible before the
                // upload. Move all cameras away while changing existing tracks.
                for(int step=1;step<=12;step++)for(int i=0;i<60;i++)
                    Call(tracks,"WheelAt",i,new Vector3(1000+i%40,0,i/40+step*.3f),Vector3.right,Vector3.forward);
                commands.Clear();Call(tracks,"Record",commands,material,near,null);
                Require(Property(tracks,"MeshUploadCount")==uploaded,"Offscreen extensions uploaded geometry");
                AssertEnvelopes(tracks,false);
                var last=((IList)Get(tracks,"chunks"))[((IList)Get(tracks,"chunks")).Count-1];
                var edge=((Bounds)Get(last,"Bounds")).max.x;
                Require(Visible(tracks,new[]{new Plane(Vector3.right,new Vector3(edge,0,0))},null),"Touching CPU envelope was culled");
                var shift=new Vector3(-1000,15,8);Set(tracks,"WorldOffset",shift);
                var rebased=Box(new Vector3(20,15,28),100f);
                Require(Visible(tracks,rebased,null),"Origin shift hid deferred ribbons");
                commands.Clear();Call(tracks,"Record",commands,material,rebased,null);
                AssertEnvelopes(tracks,true);
                var records=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(mod.GetType("DVSeasons.Mod.RailSnowStamp",true)));
                Call(tracks,"Save",records);int count=records.Count;
                Call(tracks,"Restore",records);
                Require(Property(tracks,"SegmentCount")==count,"Deferred geometry changed saved track count");
                Require(Visible(tracks,rebased,null),"Restored CPU envelope was missing");
                AssertEnvelopes(tracks,false);
                Call(tracks,"Advance",1f,181f);
                Require(!Visible(tracks,rebased,null),"Refilled snow retained an active ribbon");
                int before=Property(tracks,"MeshUploadCount");
                commands.Clear();Call(tracks,"Record",commands,material,rebased,null);
                Require(Property(tracks,"MeshUploadCount")==before,"Expired ribbons uploaded geometry");
                Debug.Log("WORLD_RAIL_UPLOADS visible-chunks="+chunks+"; 1800 offscreen wheel stamps and 720 extensions produce zero offscreen uploads; all saved marks preserved="+count);
            }
            finally {((IDisposable)tracks).Dispose();commands.Release();UnityEngine.Object.DestroyImmediate(material);}
        }
        static bool Visible(object tracks,Plane[] primary,Plane[] secondary)
        {return (bool)Call(tracks,"HasVisibleTracks",primary,secondary);}
        static void AssertEnvelopes(object tracks,bool uploaded)
        {
            foreach(var chunk in (IList)Get(tracks,"chunks"))
            {
                var bounds=(Bounds)Get(chunk,"Bounds");bounds.Expand(.002f);
                var mesh=(Mesh)Get(chunk,"Mesh");
                var vertices=(IList)Get(chunk,"Vertices");
                foreach(Vector3 point in vertices)Require(bounds.Contains(point),"CPU culling envelope excludes a ribbon vertex");
                if(!uploaded)continue;
                var gpu=mesh.vertices;
                Require(gpu.Length==vertices.Count,"Visible mesh retained a deferred vertex count");
                for(int i=0;i<gpu.Length;i++)Require(gpu[i]==(Vector3)vertices[i],"Visible mesh data differs from CPU track history");
            }
        }
        static void VerifyDiscovery(Assembly mod)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var root=new GameObject("Large-fanout discovery root");
            var expected=new List<int>();var visited=new List<int>();
            expected.Add(root.AddComponent<MeshRenderer>().GetInstanceID());
            for(int i=0;i<4096;i++)
            {
                var child=new GameObject("Child "+i);child.transform.SetParent(root.transform,false);
                expected.Add(child.AddComponent<MeshRenderer>().GetInstanceID());
                if(i%16==0)child.SetActive(false);
                if(i%64==0)
                {
                    var grandchild=new GameObject("Nested child");grandchild.transform.SetParent(child.transform,false);
                    expected.Add(grandchild.AddComponent<MeshRenderer>().GetInstanceID());
                }
            }
            var type=mod.GetType("DVSeasons.Mod.IncrementalSceneScan`1",true).MakeGenericType(typeof(MeshRenderer));
            Action<MeshRenderer> visit=renderer=>visited.Add(renderer.GetInstanceID());
            var scan=Activator.CreateInstance(type,new object[]{0f,1,visit,false});
            try
            {
                Call(scan,"Step");
                Require(visited.Count==1 && visited[0]==expected[0],"First one-node step did more than visit the root");
                var stack=Get(scan,"depthCursors");
                Require(Property(stack,"Count")==1,"Root fan-out was expanded atomically instead of retaining one cursor");
                for(int i=0;i<expected.Count*2 && visited.Count<expected.Count;i++)Call(scan,"Step");
                Require(visited.Count==expected.Count,"Incremental DFS omitted inactive or nested renderers");
                for(int i=0;i<expected.Count;i++)Require(visited[i]==expected[i],"Incremental DFS changed native hierarchy order");
                // Scene mutation after a completed scan must be seen on restart.
                var late=new GameObject("Late-loaded child");late.transform.SetParent(root.transform,false);
                int lateId=late.AddComponent<MeshRenderer>().GetInstanceID();
                var removed=root.transform.GetChild(10).gameObject;int removedId=removed.GetComponent<MeshRenderer>().GetInstanceID();
                UnityEngine.Object.DestroyImmediate(removed);
                visited.Clear();
                for(int i=0;i<expected.Count*2 && !visited.Contains(lateId);i++)Call(scan,"Step");
                Require(visited.Contains(lateId),"Newly loaded node was not found by the next scan");
                Require(!visited.Contains(removedId),"Destroyed renderer leaked from a cached hierarchy");
                ((IDisposable)scan).Dispose();
                Require(Property(Get(scan,"depthCursors"),"Count")==0,"Reset retained cursor references");
                // Roots-first mode still visits inactive descendants as well.
                var rootsFirst=Activator.CreateInstance(type,new object[]{0f,4,visit,true});
                try
                {
                    visited.Clear();for(int i=0;i<expected.Count*2 && visited.Count<expected.Count;i++)Call(rootsFirst,"Step");
                    Require(visited.Contains(lateId) && visited.Count==expected.Count,"Roots-first pooled traversal missed live scene nodes");
                }
                finally {((IDisposable)rootsFirst).Dispose();}
                Debug.Log("WORLD_DISCOVERY_BUDGET_OK hierarchy-nodes="+expected.Count+"; root-with4096children keeps one pending cursor; native DFS order/inactive/new/destruction/reset preserved.");
            }
            finally {((IDisposable)scan).Dispose();UnityEngine.Object.DestroyImmediate(root);}
        }
        static void VerifyMidScanMutation(Assembly mod)
        {
            var type=mod.GetType("DVSeasons.Mod.IncrementalSceneScan`1",true).MakeGenericType(typeof(MeshRenderer));
            foreach(bool rootsFirst in new[]{false,true})foreach(bool reparent in new[]{false,true})
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                var root=new GameObject("Mutable root");root.AddComponent<MeshRenderer>();
                var first=new GameObject("Visited A");first.transform.SetParent(root.transform,false);var a=first.AddComponent<MeshRenderer>();
                var second=new GameObject("Pending B");second.transform.SetParent(root.transform,false);var b=second.AddComponent<MeshRenderer>();
                var third=new GameObject("Pending C");third.transform.SetParent(root.transform,false);var c=third.AddComponent<MeshRenderer>();
                var visited=new List<int>();Action<MeshRenderer> visit=r=>visited.Add(r.GetInstanceID());
                var scan=Activator.CreateInstance(type,new object[]{60f,1,visit,rootsFirst});
                try
                {
                    for(int i=0;i<6 && !visited.Contains(a.GetInstanceID());i++)Call(scan,"Step");
                    Require(visited.Contains(a.GetInstanceID()) && !visited.Contains(b.GetInstanceID()),"Mutation fixture did not pause between siblings");
                    if(reparent)first.transform.SetParent(null,true);else UnityEngine.Object.DestroyImmediate(first);
                    for(int i=0;i<8;i++)Call(scan,"Step");
                    Require(visited.Contains(b.GetInstanceID()) && visited.Contains(c.GetInstanceID()),
                        "Destroying/reparenting a visited sibling skipped the next child until another scan");
                }
                finally
                {
                    ((IDisposable)scan).Dispose();UnityEngine.Object.DestroyImmediate(root);
                    if(first!=null)UnityEngine.Object.DestroyImmediate(first);
                }
            }
            Debug.Log("WORLD_DISCOVERY_MUTATION_OK: deletion/reparenting between one-node steps preserves both pending siblings in DFS and roots-first traversal without waiting for rescan.");
        }
    }
}
