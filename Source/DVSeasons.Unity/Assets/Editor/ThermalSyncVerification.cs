using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace DVSeasons.AssetBundleBuild
{
    public static class ThermalSyncVerification
    {
        public static void Run()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var mod=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD");
            var managed=Path.Combine(Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME"),"DerailValley_Data/Managed");
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=> {
                foreach(var dir in new[]{mod,managed,Path.Combine(managed,"UnityModManager")})
                {var path=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll");if(File.Exists(path))return Assembly.LoadFrom(path);}
                return null;
            };
            int code=0;
            try {Assembly.LoadFrom(Path.Combine(root,"artifacts/verification/VerifyThermalSync.dll"))
                .GetType("VerifyThermalSync").GetMethod("Run").Invoke(null,new object[]{mod});}
            catch(Exception e){Debug.LogException(e);code=1;}
            EditorApplication.Exit(code);
        }
    }
}
