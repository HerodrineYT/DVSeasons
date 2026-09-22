using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
namespace DVSeasons.AssetBundleBuild
{
    public static class SnowNativeIntegrationVerification
    {
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Path.Combine(root,"artifacts/build/DVSeasons"),game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,args)=>{
                foreach(var dir in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var file=Path.Combine(dir,new AssemblyName(args.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            int result=0;
            try{ProceduralSnowVerification.VerifyDe6Roof();Debug.Log("SNOW_NATIVE_INTEGRATION_OK: full controller and actual DE6 model/normal textures, five LODs, both roof slopes, origin rebasing.");}
            catch(Exception e){Debug.LogException(e);result=1;}
            EditorApplication.Exit(result);
        }
    }
}