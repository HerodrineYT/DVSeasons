using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowCaptureInvalidationVerification
    {
        public static void Run()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var mod=Path.Combine(root,"artifacts/build/DVSeasons");
            var managed=Path.Combine(Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME"),"DerailValley_Data/Managed");
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,args)=>{
                foreach(var dir in new[]{mod,managed,Path.Combine(managed,"UnityModManager")})
                {
                    var path=Path.Combine(dir,new AssemblyName(args.Name).Name+".dll");
                    if(File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            int code=0;
            try
            {
                Assembly.LoadFrom(Path.Combine(root,"artifacts/verification/VerifySnowCaptureInvalidation.dll"))
                    .GetType("VerifySnowCaptureInvalidation").GetMethod("Run").Invoke(null,new object[]{mod});
            }
            catch(Exception error) {Debug.LogException(error);code=1;}
            EditorApplication.Exit(code);
        }
    }
}
