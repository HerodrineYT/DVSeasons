using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    // The surface fixture predates native junction discovery. Resolve the game
    // assemblies just as the current animal/fleet fixtures do, without copying
    // proprietary binaries into the Unity project or source distribution.
    public static class SnowSurfaceRegressionVerification
    {
        public static void Core() { Run(ProceduralSnowVerification.VerifySurfaceCore); }
        public static void Landscape() { Run(ProceduralSnowVerification.VerifyLandscape); }
        public static void De6Roof() { Run(ProceduralSnowVerification.VerifyDe6Roof); }

        private static void Run(Action verify)
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            var mod=Path.Combine(root,"artifacts/build/DVSeasons");
            var managed=Path.Combine(game,"DerailValley_Data/Managed");
            ResolveEventHandler resolver=(sender,args)=>
            {
                foreach(var dir in new[]{mod,managed,Path.Combine(managed,"UnityModManager")})
                {
                    var file=Path.Combine(dir,new AssemblyName(args.Name).Name+".dll");
                    if(File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;
            int code=0;
            bool mismatchedTarget=false;
            Application.LogCallback checkTarget=(message,trace,type)=>
            {if(message.Contains("Dimensions of color surface does not match")) mismatchedTarget=true;};
            Application.logMessageReceived+=checkTarget;
            try
            {
                verify();
                if(mismatchedTarget) throw new InvalidOperationException("Snow pass bound incompatible color/depth targets");
                Debug.Log("SNOW_RENDER_TARGETS_OK: HDR/LDR and empty/non-empty surface paths have matching targets.");
            }
            catch(Exception error) {Debug.LogException(error);code=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;Application.logMessageReceived-=checkTarget;}
            EditorApplication.Exit(code);
        }
    }
}
