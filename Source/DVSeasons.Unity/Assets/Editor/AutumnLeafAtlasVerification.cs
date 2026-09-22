using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class AutumnLeafAtlasVerification
    {
        public static void Run()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            int code = 0;
            try
            {
                Assembly.LoadFrom(Path.Combine(root, "artifacts/verification/VerifyAutumnLeafAtlas.dll"))
                    .GetType("VerifyAutumnLeafAtlas", true).GetMethod("Run")
                    .Invoke(null, new object[] { root, Environment.GetEnvironmentVariable("DVSEASONS_BAKE_LEAF_ATLAS") == "1" });
            }
            catch (Exception exception) { Debug.LogException(exception); code = 1; }
            EditorApplication.Exit(code);
        }
    }
}
