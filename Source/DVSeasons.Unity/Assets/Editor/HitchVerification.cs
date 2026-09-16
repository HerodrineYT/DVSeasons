using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace DVSeasons.AssetBundleBuild
{
    public static class HitchVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        static object Get(object o,string f){return o.GetType().GetField(f,All).GetValue(o);}
        static void Set(object o,string f,object v){o.GetType().GetField(f,All).SetValue(o,v);}
        static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
        public static void Run()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var modPath=Path.Combine(root,"artifacts/build/DVSeasons");
            var managed=Path.Combine(Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME"),"DerailValley_Data/Managed");
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{
                foreach(var dir in new[]{modPath,managed,Path.Combine(managed,"UnityModManager")})
                {var file=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            int code=0;
            try
            {
                var mod=Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll"));
                var advance=mod.GetType("DVSeasons.Mod.FrameDiscovery",true).GetMethod("Advance",All);
                var steps=new Steps();var args=new object[]{steps};int frames=0;
                do {int before=steps.Index;advance.Invoke(null,args);Require(steps.Index-before<=64,"Discovery exceeded item budget");frames++;}while(args[0]!=null);
                Require(steps.Index==1000 && steps.Disposed && frames>=16,"Discovery lost work or leaked enumerator");
                steps=new Steps{Throw=true};args=new object[]{steps};
                try{advance.Invoke(null,args);throw new Exception("Expected iterator error");}
                catch(TargetInvocationException){Require(steps.Disposed,"Faulted discovery was not disposed");}
                VerifyMaterials(mod,advance);
                VerifyWipers(mod);
                Debug.Log("HITCH_CHECK_OK: bounded discovery, full material coverage, wiper sweep geometry and clean-mask no-op.");
            }
            catch(Exception e){Debug.LogException(e);code=1;}
            EditorApplication.Exit(code);
        }
        sealed class Steps:IEnumerator<int>
        {
            public int Index;public bool Disposed,Throw;
            public int Current{get{return 0;}}object IEnumerator.Current{get{return Current;}}
            public bool MoveNext(){if(Throw)throw new InvalidOperationException();if(Index==1000)return false;Index++;return true;}
            public void Dispose(){Disposed=true;}public void Reset(){throw new NotSupportedException();}
        }
        static void VerifyMaterials(Assembly mod,MethodInfo advance)
        {
            var type=mod.GetType("DVSeasons.Mod.SeasonalTextureController",true);
            var controller=Activator.CreateInstance(type,new object[]{null});
            var settings=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SeasonModSettings",true));
            var texture=new Texture2D(4,4){name="hitch_fixture_leaves"};var materials=new List<Material>();
            try
            {
                for(int i=0;i<300;i++)materials.Add(new Material(Shader.Find("Standard")){name="hitch_fixture_tree_"+i,mainTexture=texture});
                var work=(IEnumerable<int>)type.GetMethod("ScanVegetationMaterials",All).Invoke(controller,new[]{settings});
                var args=new object[]{work.GetEnumerator()};advance.Invoke(null,args);
                Require(((IDictionary)Get(controller,"materialBindings")).Count<300,"All materials processed in one frame");
                int frames=1;while(args[0]!=null && frames++<2000)advance.Invoke(null,args);
                Require(args[0]==null,"Material discovery never finished");
                var bindings=(IDictionary)Get(controller,"materialBindings");
                foreach(var mat in materials)Require(bindings.Contains(mat.GetInstanceID()+"|_MainTex"),"Lost a material across frames");
            }
            finally{((IDisposable)controller).Dispose();foreach(var m in materials)UnityEngine.Object.DestroyImmediate(m);UnityEngine.Object.DestroyImmediate(texture);}
        }
        static void VerifyWipers(Assembly mod)
        {
            var type=mod.GetType("DVSeasons.Mod.WinterWindowController",true);
            var pane=Activator.CreateInstance(type.GetNestedType("Pane",All),true);
            var cab=Activator.CreateInstance(type.GetNestedType("Cab",All),true);Set(pane,"Cab",cab);
            var climate=Get(cab,"Climate");var temperature=climate.GetType().GetProperty("GlassTemperature").GetSetMethod(true);
            var wipe=type.GetMethod("WipeTriangle",All);
            var inside=(Func<Vector2,Vector2,Vector2,Vector2,bool>)Delegate.CreateDelegate(typeof(Func<Vector2,Vector2,Vector2,Vector2,bool>),type.GetMethod("Inside",All));
            var pixels=(Color32[])Get(pane,"Pixels");var random=new System.Random(9917);
            for(int trial=0;trial<180;trial++)
            {
                bool warm=trial%2==0;temperature.Invoke(climate,new object[]{warm?10f:-10f});
                var vertices=new Vector2[3];for(int i=0;i<3;i++)vertices[i]=new Vector2((float)random.NextDouble()*1.8f-.4f,(float)random.NextDouble()*1.8f-.4f);
                for(int i=0;i<pixels.Length;i++)pixels[i]=new Color32(255,0,0,0);
                wipe.Invoke(null,new[]{pane,(object)vertices[0],vertices[1],vertices[2]});int differences=0;
                for(int y=0;y<128;y++)for(int x=0;x<128;x++)
                {
                    bool expected=inside(new Vector2((x+.5f)/128,(y+.5f)/128),vertices[0],vertices[1],vertices[2]);
                    var p=pixels[y*128+x];if((p.r==0)!=expected)differences++;
                    Require(p.g==(warm && p.r==0?255:0),"Wiper cleared frozen crystal film");
                }
                Require(differences<=1,"Scanline raster changed sweep coverage: "+differences);
                Set(pane,"MaskChanged",false);wipe.Invoke(null,new[]{pane,(object)vertices[0],vertices[1],vertices[2]});
                Require(!(bool)Get(pane,"MaskChanged"),"Clean swept region triggered another texture upload");
            }
        }
    }
}
