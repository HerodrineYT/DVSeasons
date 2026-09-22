using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    // Mixed native/fallback fleet: verify surface parity and measure a rebuild
    // triggered by streaming one car, without counting GPU work as CPU work.
    public static class SnowFallbackIndexVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static object Get(object o,string n)=>o.GetType().GetField(n,All).GetValue(o);
        static void Set(object o,string n,object v)=>o.GetType().GetField(n,All).SetValue(o,v);
        static object Call(object o,string n,params object[] a)=>o.GetType().GetMethod(n,All).Invoke(o,a);
        static void Require(bool value,string message){if(!value)throw new Exception(message);}
        public static void Run()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            var game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{
                foreach(var directory in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var file=Path.Combine(directory,new AssemblyName(e.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            int result=0;
            try
            {
                var mod=Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll"));
                var type=typeof(SnowYardBatchVerification).GetNestedType("Fixture",All);
                using(var fixture=(IDisposable)Activator.CreateInstance(type,All,null,new object[]{mod,runtime,128},null))Verify(fixture);
            }
            catch(Exception e){UnityEngine.Debug.LogException(e);result=1;}
            EditorApplication.Exit(result);
        }
        static void Verify(object fixture)
        {
            var registry=Get(fixture,"registry");var native=Get(registry,"nativeMaterials");
            var vehicles=(IList)Get(fixture,"vehicles");
            Set(native,"Enabled",true);Require((bool)Call(native,"Initialize",Get(fixture,"repository")),"Native snow shader unavailable");
            var custom=new Material(Shader.Find("Standard"));custom.EnableKeyword("_EMISSION");
            var meshes=new HashSet<Mesh>();int all=0,fallback=0;
            try
            {
                foreach(var vehicle in vehicles)
                {
                    var parts=(IList)Get(vehicle,"Parts");
                    // Emissive Standard uses the real deferred depth, but stays
                    // on fallback because animated lamps must retain ownership.
                    foreach(var part in parts)
                    {
                        var renderer=(Renderer)Get(part,"Renderer");
                        if(renderer.name=="Yard part 2" || renderer.name=="Yard part 6")renderer.sharedMaterial=custom;
                    }
                    Call(native,"Bind",vehicle);
                    var cache=Get(vehicle,"PartCache");Call(cache,"Build",parts);
                    foreach(var part in parts)
                    {
                        all++;
                        if(!(bool)Get(part,"HasOpaque") || (bool)Get(part,"NativeComplete"))continue;
                        fallback++;meshes.Add((Mesh)Get(part,"Mesh"));
                    }
                }
                var rebuild=(Action)Delegate.CreateDelegate(typeof(Action),registry,registry.GetType().GetMethod("RefreshExclusionMeshUses",All));
                for(int i=0;i<20;i++)rebuild();
                var watch=Stopwatch.StartNew();for(int i=0;i<300;i++)rebuild();watch.Stop();
                int indexed=((IDictionary)Get(registry,"exclusionMeshUses")).Count;
                if(Get(vehicles[0],"PartCache").GetType().GetField("FallbackParts",All)!=null)
                    Require(indexed==meshes.Count,"Native-only mesh still participates in fallback batching");
                UnityEngine.Debug.Log("FALLBACK_INDEX_BENCH all_parts="+all+" fallback_parts="+fallback+" indexed_meshes="+indexed+" rebuild_ms="+(watch.Elapsed.TotalMilliseconds/300).ToString("F4"));
                Call(fixture,"Compare","mixed native and custom snow surfaces",false,false,true);
                // Cargo/material replacement can return a previously native part
                // to fallback. The new generation must be indexed immediately.
                var changed=vehicles[0];Call(native,"Release",changed);
                Call(Get(changed,"PartCache"),"Build",Get(changed,"Parts"));rebuild();
                foreach(var part in (IList)Get(changed,"Parts"))
                    if((bool)Get(part,"HasOpaque"))Require(((IDictionary)Get(registry,"exclusionMeshUses")).Contains(Get(part,"Mesh")),"Replaced material lost its fallback mesh");
                Call(fixture,"Compare","native material returned to fallback",false,false,true);
                UnityEngine.Debug.Log("SNOW_FALLBACK_INDEX_OK: mixed native/custom surfaces, exact ordered surface parity and material replacement.");
            }
            finally{UnityEngine.Object.DestroyImmediate(custom);}
        }
    }
}
