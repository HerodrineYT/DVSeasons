using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace DVSeasons.AssetBundleBuild
{
    public static class SeasonEffectsVerification
    {
        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            string managed = Path.Combine(Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME"), "DerailValley_Data/Managed");
            string mod = Path.Combine(root, "artifacts/build/DVSeasons");
            ResolveEventHandler resolve = (s, e) =>
            {
                foreach (var dir in new[] { mod, managed, Path.Combine(managed, "UnityModManager") })
                { var file = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll"); if (File.Exists(file)) return Assembly.LoadFrom(file); }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            int code = 0;
            try
            {
                var assembly = Assembly.LoadFrom(Path.Combine(root, "artifacts/verification/VerifySeasonEffects.dll"));
                assembly.GetType("VerifySeasonEffects", true).GetMethod("Run").Invoke(null, new object[] { mod });
            }
            catch (Exception e) { Debug.LogException(e); code = 1; }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
            EditorApplication.Exit(code);
        }
    }
}
