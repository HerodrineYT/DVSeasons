using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace DVSeasons.AssetBundleBuild
{
    public static class TerrainCloneVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static object Call(object o,string name,params object[] args) {return o.GetType().GetMethod(name,All).Invoke(o,args);}
        static void Set(object o,string name,object value) {o.GetType().GetField(name,All).SetValue(o,value);}
        static void Require(bool ok,string message) {if(!ok)throw new Exception(message);}
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string modPath=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD") ?? Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=> {
                foreach(var folder in new[]{modPath,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {string file=Path.Combine(folder,new AssemblyName(e.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            int code=0;
            try
            {
                var mod=Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll"));
                var repoType=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
                var repo=Activator.CreateInstance(repoType,new object[]{modPath});
                var bundle=AssetBundle.LoadFromFile(Path.Combine(modPath,"AssetBundles/dvseasons_dv99"));
                repoType.GetProperty("Bundle").GetSetMethod(true).Invoke(repo,new object[]{bundle});
                var controller=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.MicroSplatSeasonalTerrainController",true),new[]{repo});
                var a=new Texture2DArray(16,16,16,TextureFormat.RGBA32,true,false) {name="MicroSplatConfig_diff_tarray"};
                for(int slice=0;slice<16;slice++){var pixels=new Color[256];for(int i=0;i<256;i++)pixels[i]=new Color(.2f,.4f,.1f,1);a.SetPixels(pixels,slice);}a.Apply();
                var mat=new Material(Shader.Find("Hidden/DVSeasons/Tests/MicroSplatClone")){name="MicroSplat fixture"};mat.SetTexture("_Diffuse",a);
                Material clone=null,lateClone=null;
                try
                {
                    Call(controller,"ScanMaterial",mat,false);Set(controller,"springStep",32);Set(controller,"restored",false);
                    Call(controller,"ApplyCoverage",0,true);
                    var spring=mat.GetTexture("_Diffuse") as RenderTexture;
                    Require(spring!=null && spring.IsCreated(),"Spring output missing");
                    clone=new Material(mat){name="MicroSplat streamed clone"};
                    Require((int)Call(controller,"ScanMaterial",clone,false)==1,"Streamed RenderTexture clone was ignored");
                    lateClone=new Material(mat){name="MicroSplat unscanned clone"};
                    Set(controller,"springStep",0);Call(controller,"ApplyCoverage",0,true);
                    Require(clone.GetTexture("_Diffuse")==a,"Clone did not restore its own summer texture");
                    Call(controller,"Restore");
                    Require(lateClone.GetTexture("_Diffuse")==a,"Unscanned clone retained a destroyed spring texture");
                    Require(mat.GetTexture("_Diffuse")==a,"Original landscape lost texture");
                    Debug.Log("TERRAIN_CLONE_OK: streamed spring arrays discovered; own summer source restored; unscanned clones restored before release.");
                }
                finally {((IDisposable)controller).Dispose();((IDisposable)repo).Dispose();foreach(var o in new UnityEngine.Object[]{a,mat,clone,lateClone})if(o!=null)UnityEngine.Object.DestroyImmediate(o);}
            }
            catch(Exception e){Debug.LogException(e);code=1;}
            EditorApplication.Exit(code);
        }
    }
}
