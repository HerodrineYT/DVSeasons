using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class ColdFeaturesVerification
    {
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            if(string.IsNullOrEmpty(game)) throw new Exception("DVSEASONS_VERIFY_GAME is required");
            string managed=Path.Combine(game,"DerailValley_Data/Managed");
            string mod=Path.Combine(root,"artifacts/build/DVSeasons");
            ResolveEventHandler resolve=(s,e)=>
            {
                foreach(var dir in new[]{mod,managed,Path.Combine(managed,"UnityModManager")})
                {var path=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll");if(File.Exists(path)) return Assembly.LoadFrom(path);}
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve+=resolve;
            try
            {
                var verifier=Assembly.LoadFrom(Path.Combine(root,"artifacts/verification/VerifyColdPowertrain.dll"));
                verifier.GetType("VerifyColdPowertrain",true).GetMethod("Run").Invoke(null,new object[]{mod});
            }
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolve;}
        }
    }
}
