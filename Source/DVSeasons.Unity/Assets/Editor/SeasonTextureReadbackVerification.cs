using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class SeasonTextureReadbackVerification
    {
        const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        static void Require(bool value,string message) {if(!value)throw new Exception(message);}
        public static void Run()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var mod=Assembly.LoadFrom(Path.Combine(root,"artifacts/build/DVSeasons/DVSeasons.dll"));
            var type=mod.GetType("DVSeasons.Mod.SeasonTextureReadback",true);
            var source=new Texture2D(128,64,TextureFormat.RGBA32,true,false) {filterMode=FilterMode.Bilinear};
            var pixels=new Color32[128*64];
            for(int y=0;y<64;y++) for(int x=0;x<128;x++) pixels[y*128+x]=new Color32((byte)(x*2),(byte)(y*4),(byte)((x+y)%256),(byte)((x*11+y*7)%256));
            source.SetPixels32(pixels);source.Apply();
            var previous=RenderTexture.active;
            var target=RenderTexture.GetTemporary(64,32,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Default);
            var read=new Texture2D(64,32,TextureFormat.RGBA32,false);
            int code=0;
            try
            {
                Require(SystemInfo.supportsAsyncGPUReadback,"This GPU cannot verify asynchronous readback");
                Graphics.Blit(source,target);RenderTexture.active=target;
                read.ReadPixels(new Rect(0,0,64,32),0,0,false);read.Apply(false,false);
                var expected=read.GetPixels32();RenderTexture.active=previous;
                var helper=Activator.CreateInstance(type);
                try
                {
                    var args=new object[]{source,64,32,null};
                    Require(!(bool)type.GetMethod("TryRead").Invoke(helper,args),"First read synchronously waited for GPU");
                    // Only the fixture waits. Runtime polls completed copies.
                    AsyncGPUReadback.WaitAllRequests();
                    Require((bool)type.GetMethod("TryRead").Invoke(helper,args),"Finished GPU request never produced pixels");
                    var actual=(Color32[])args[3];Require(actual.Length==expected.Length,"Readback dimensions changed");
                    for(int i=0;i<actual.Length;i++)
                    {
                        var a=actual[i];var b=expected[i];
                        Require(Math.Abs(a.r-b.r)<=1 && Math.Abs(a.g-b.g)<=1 && Math.Abs(a.b-b.b)<=1 && a.a==b.a,"Readback changed colourspace/orientation/alpha at "+i+": "+a+" vs "+b);
                    }
                }
                finally {((IDisposable)helper).Dispose();}
                var first=Activator.CreateInstance(type);var second=Activator.CreateInstance(type);var third=Activator.CreateInstance(type);
                try
                {
                    foreach(var owner in new[]{first,second})
                        type.GetMethod("TryRead").Invoke(owner,new object[]{source,64,32,null});
                    int before=(int)type.GetProperty("AsyncRequestCount",All).GetValue(null,null);
                    type.GetMethod("TryRead").Invoke(third,new object[]{source,64,32,null});
                    Require((int)type.GetProperty("AsyncRequestCount",All).GetValue(null,null)==before,"More than two readbacks allocated simultaneously");
                    ((IDisposable)first).Dispose();((IDisposable)second).Dispose();
                    AsyncGPUReadback.WaitAllRequests();
                    Require((int)type.GetProperty("ActiveRequests",All).GetValue(null,null)==0,"Cancelled readback retained GPU resources");
                    var args=new object[]{source,64,32,null};type.GetMethod("TryRead").Invoke(third,args);
                    AsyncGPUReadback.WaitAllRequests();
                    Require((bool)type.GetMethod("TryRead").Invoke(third,args),"Reset/cancellation blocked subsequent textures");
                    Require((int)type.GetProperty("SynchronousReadCount",All).GetValue(null,null)==0,"Supported GPU used a blocking ReadPixels fallback");
                }
                finally {((IDisposable)first).Dispose();((IDisposable)second).Dispose();((IDisposable)third).Dispose();}
                Debug.Log("SEASON_READBACK_OK: exact scaled sRGB/alpha pixels; no synchronous readback on supported GPU; two-request bound; cancellation and reuse release GPU resources.");
            }
            catch(Exception e) {Debug.LogException(e);code=1;}
            finally
            {
                AsyncGPUReadback.WaitAllRequests();RenderTexture.active=previous;
                RenderTexture.ReleaseTemporary(target);UnityEngine.Object.DestroyImmediate(source);UnityEngine.Object.DestroyImmediate(read);
            }
            EditorApplication.Exit(code);
        }
    }
}
