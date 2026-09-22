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
    // Exercises production registry invalidation hooks and limiter selection
    // without a shader, height capture, synthetic provider or rendered frame.
    public static class SnowLimiterMembershipVerification
    {
        const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static object Get(object owner,string name) {return owner.GetType().GetField(name,All).GetValue(owner);}
        static void Set(object owner,string name,object value) {owner.GetType().GetField(name,All).SetValue(owner,value);}
        static object Call(object owner,string name,params object[] args) {return owner.GetType().GetMethod(name,All).Invoke(owner,args);}
        static int Rebuilds(object limiter) {return (int)limiter.GetType().GetProperty("MembershipRebuildCount",All).GetValue(limiter,null);}
        static bool Selected(object limiter,Transform root)
        {return (bool)limiter.GetType().GetMethod("IsSelected",All,null,new[]{typeof(Transform)},null).Invoke(limiter,new object[]{root});}
        static void Require(bool ok,string message) {if(!ok)throw new InvalidOperationException(message);}
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            ResolveEventHandler resolver=(sender,args)=>
            {
                foreach(var directory in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {string path=Path.Combine(directory,new AssemblyName(args.Name).Name+".dll");if(File.Exists(path))return Assembly.LoadFrom(path);}
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;int result=0;
            try {Verify(Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll")));}
            catch(Exception exception) {Debug.LogException(exception);result=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(result);
        }
        static void Verify(Assembly mod)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var objects=new List<GameObject>();
            var camera=Create(objects,"Membership camera").AddComponent<Camera>();
            camera.fieldOfView=110;camera.farClipPlane=1000;camera.transform.rotation=Quaternion.Euler(90,0,0);
            camera.transform.position=new Vector3(-50,100,0);
            var registry=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SnowVehicleRegistry",true),true);
            var limiter=Get(registry,"ObjectLimiter");var vehicles=(IList)Get(registry,"vehicles");
            var left=Create(objects,"Left train").transform;left.position=new Vector3(-50,0,0);
            var right=Create(objects,"Right train").transform;right.position=new Vector3(50,0,0);
            try
            {
                Call(registry,"Register",left,null,null);Call(registry,"Register",right,null,null);
                foreach(var vehicle in vehicles)
                {Set(vehicle,"RollingStock",true);Set(vehicle,"Ready",true);Set(vehicle,"LocalBounds",new Bounds(Vector3.zero,new Vector3(6,5,14)));}
                limiter.GetType().GetProperty("Limit").SetValue(limiter,1,null);
                Call(limiter,"Update",camera,vehicles);
                Require(Selected(limiter,left) && !Selected(limiter,right),"Initial nearest train selection failed");
                int rebuilds=Rebuilds(limiter);
                var watch=System.Diagnostics.Stopwatch.StartNew();
                for(int frame=0;frame<1200;frame++)Call(limiter,"Update",camera,vehicles);
                watch.Stop();
                Require(Rebuilds(limiter)==rebuilds,"Stable fleet rebuilt membership on ordinary frames");
                Debug.Log("SNOW_LIMIT_MEMBERSHIP_CACHE: 1200 stable updates, zero branch rebuilds, CPU total="+
                    watch.Elapsed.TotalMilliseconds.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+" ms (reflection fixture overhead included).");

                var oldCargo=Create(objects,"Detached old cargo").transform;
                Call(registry,"SetCargo",right,oldCargo);Call(limiter,"Update",camera,vehicles);
                Require(!Selected(limiter,oldCargo) && Rebuilds(limiter)==++rebuilds,"Cargo streamed without membership invalidation");
                Call(registry,"SetCargo",right,oldCargo);Call(limiter,"Update",camera,vehicles);
                Require(Rebuilds(limiter)==rebuilds,"Unchanged cargo rebuilt membership");
                var cargo=Create(objects,"Detached new cargo").transform;
                Call(registry,"SetCargo",right,cargo);Call(limiter,"Update",camera,vehicles);
                Require(!Selected(limiter,cargo) && Selected(limiter,oldCargo) && Rebuilds(limiter)==++rebuilds,"Cargo replacement retained old branch ownership");
                var external=Create(objects,"Detached external").transform;var dummy=Create(objects,"Detached dummy external").transform;
                Call(registry,"SetExternalParts",right,external,dummy);Call(limiter,"Update",camera,vehicles);
                Require(!Selected(limiter,external) && !Selected(limiter,dummy) && Rebuilds(limiter)==++rebuilds,"External/dummy streaming was not invalidated");
                var interior=Create(objects,"Detached interior").transform;var lod=Create(objects,"Detached interior LOD").transform;
                Call(registry,"Register",right,interior,lod);Call(limiter,"Update",camera,vehicles);
                Require(!Selected(limiter,interior) && !Selected(limiter,lod) && Rebuilds(limiter)==++rebuilds,"Interior/LOD replacement was not invalidated");
                Call(registry,"Register",right,interior,lod);Call(limiter,"Update",camera,vehicles);
                Require(Rebuilds(limiter)==rebuilds,"Repeated Register of unchanged branches rebuilt membership");

                camera.transform.position=new Vector3(50,100,0);Call(limiter,"Update",camera,vehicles);
                Require(Selected(limiter,right) && Selected(limiter,cargo) && !Selected(limiter,left) && Rebuilds(limiter)==rebuilds,
                    "Camera teleport failed selection or rebuilt unchanged branch map");
                var shift=new Vector3(5000,0,-7000);
                foreach(var item in objects)if(item!=null)item.transform.position+=shift;
                Call(limiter,"ShiftWorld",shift);Call(limiter,"Update",camera,vehicles);
                Require(Selected(limiter,right) && Rebuilds(limiter)==rebuilds,"Floating origin changed membership or selection");

                // Motion is still reconsidered on the existing selection timer.
                left.position=right.position+new Vector3(0,30,0);Set(limiter,"nextSelection",0f);
                Call(limiter,"Update",camera,vehicles);
                Require(Selected(limiter,left) && !Selected(limiter,right) && Rebuilds(limiter)==rebuilds,
                    "Timed nearest selection ignored moving trains or rebuilt branches");
                UnityEngine.Object.DestroyImmediate(left.gameObject);Call(limiter,"Update",camera,vehicles);
                Require(Selected(limiter,right) && Rebuilds(limiter)==++rebuilds,"Destroyed selected root was not replaced immediately");

                // A detached child may survive destruction of its owning train;
                // cached ownership must not affect that now-unowned geometry.
                UnityEngine.Object.DestroyImmediate(right.gameObject);
                Require(Selected(limiter,cargo),"Destroyed owner still limited its surviving detached branch");
                Call(limiter,"Update",camera,vehicles);rebuilds=Rebuilds(limiter);
                var sceneObject=Create(objects,"Ordinary scenery").transform;
                Require(Selected(limiter,sceneObject),"Non-train scenery consumed the car limit");
                limiter.GetType().GetProperty("Limit").SetValue(limiter,0,null);Call(limiter,"Update",camera,vehicles);
                Require(Selected(limiter,cargo) && Rebuilds(limiter)==rebuilds,"Unlimited mode rebuilt branches or retained a limit");
                limiter.GetType().GetProperty("Limit").SetValue(limiter,2,null);Call(limiter,"Update",camera,vehicles);
                Require(Rebuilds(limiter)==rebuilds+1,"Re-enabling a limit did not rebuild cleared ownership");
                Debug.Log("SNOW_LIMIT_MEMBERSHIP_OK: cached stable fleet; cargo/external/dummy/interior streaming and replacement; no-op registry calls; camera teleport; timed moving-train selection; floating origin; destroyed selected root and owner; scenery; unlimited/re-enabled modes.");
            }
            finally
            {
                ((IDisposable)registry).Dispose();
                foreach(var item in objects)if(item!=null)UnityEngine.Object.DestroyImmediate(item);
            }
        }
        static GameObject Create(List<GameObject> objects,string name)
        {var item=new GameObject(name);objects.Add(item);return item;}
    }
}
