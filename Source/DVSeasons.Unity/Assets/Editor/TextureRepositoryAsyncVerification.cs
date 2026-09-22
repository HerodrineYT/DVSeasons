using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class TextureRepositoryAsyncVerification
    {
        static object repository;
        static Type type,season;
        static double deadline;
        static int phase,updates;
        static object Call(string name,params object[] args) {return type.GetMethod(name).Invoke(repository,args);}
        public static void Run()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var runtime=Path.Combine(root,"artifacts/build/DVSeasons");
            var mod=Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll"));
            type=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
            season=Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.Core.dll")).GetType("DVSeasons.Core.SeasonKind",true);
            repository=Activator.CreateInstance(type,new object[]{runtime});
            phase=updates=0;deadline=EditorApplication.timeSinceStartup+90;
            Call("BeginLoad");EditorApplication.update+=Poll;
        }
        static void Poll()
        {
            try
            {
                updates++;
                if(EditorApplication.timeSinceStartup>deadline) throw new Exception("Asynchronous texture bundle loading timed out");
                if(!(bool)type.GetProperty("IsLoadFinished").GetValue(repository,null)) return;
                var winter=Enum.Parse(season,"Winter");
                foreach(var name in new[]{"Coal_01d","WaterIceAlbedo","WaterIceNormal","AsphaltRoad_01d"})
                {
                    var args=new object[]{name,winter,null};
                    if(!(bool)Call("TryLoadTexture",args) || args[2]==null) throw new Exception("Prepared asset unavailable after async readiness: "+name);
                }
                if(!(bool)Call("HasCompleteWinterTrackSet","RailMed_d")) throw new Exception("Track stages not indexed after async readiness");
                if(Call("LoadShader","ProceduralSnow")==null) throw new Exception("Shader missing after async readiness");
                if(phase++==0)
                {
                    // DV can unload bundles between worlds; the same repository
                    // must rebuild both bundle indexes and direct-texture caches.
                    Call("ResetForSession");AssetBundle.UnloadAllAssetBundles(true);
                    deadline=EditorApplication.timeSinceStartup+90;Call("BeginLoad");return;
                }
                if((int)type.GetProperty("TextureDecodeCount").GetValue(repository,null)!=0 ||
                    (int)type.GetProperty("PixelReadbackCount").GetValue(repository,null)!=0)
                    throw new Exception("Async prepared loading used PNG decode or GPU readback");
                Debug.Log("TEXTURE_REPOSITORY_ASYNC_OK: independent prepared bundles ready across "+updates+" editor updates; first world, unload/reload, all track stages and shader lookup; no PNG decode/readback.");
                Finish(0);
            }
            catch(Exception error){Debug.LogException(error);Finish(1);}
        }
        static void Finish(int code)
        {EditorApplication.update-=Poll;((IDisposable)repository).Dispose();EditorApplication.Exit(code);}
    }
}
