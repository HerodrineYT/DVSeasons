using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace DVSeasons.AssetBundleBuild
{
    [InitializeOnLoad]
    public static class ColdFeaturesVerification
    {
        private const string Pending="DVSeasons.VerifyColdFeatures";
        static ColdFeaturesVerification(){EditorApplication.playModeStateChanged+=OnPlayMode;}
        public static void Run()
        {
            SessionState.SetBool(Pending,true);
            EditorApplication.isPlaying=true;
        }
        private static void OnPlayMode(PlayModeStateChange state)
        {
            if(state!=PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending,false))return;
            SessionState.SetBool(Pending,false);
            int code=0;
            try{Verify();}catch(Exception e){Debug.LogException(e);code=1;}
            EditorApplication.Exit(code);
        }
        private static void Verify()
        {
            string root=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_ROOT");
            if(string.IsNullOrEmpty(root)) root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            if(string.IsNullOrEmpty(game)) throw new Exception("DVSEASONS_VERIFY_GAME is required");
            string managed=Path.Combine(game,"DerailValley_Data/Managed");
            string mod=Path.Combine(root,"artifacts/build/DVSeasons");
            ResolveEventHandler resolve=(s,e)=>
            {
                foreach(var dir in new[]{mod,managed,Path.Combine(managed,"UnityModManager"),Path.Combine(game,"Mods/DVLangHelper")})
                {var path=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll");if(File.Exists(path)) return Assembly.LoadFrom(path);}
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve+=resolve;
            try
            {
                var verifier=Assembly.LoadFrom(Path.Combine(root,"artifacts/verification/VerifyColdPowertrain.dll"));
                verifier.GetType("VerifyColdPowertrain",true).GetMethod("Run").Invoke(null,new object[]{mod});
                verifier.GetType("VerifyColdStartHints",true).GetMethod("Run").Invoke(null,new object[]{mod});
            }
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolve;}
        }
    }
}
