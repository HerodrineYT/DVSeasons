using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace DVSeasons.AssetBundleBuild
{
    public static class StationOfficeVerification
    {
        public static void Run()
        {
            var seasonsRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            var survivalRoot = Path.GetFullPath(Path.Combine(seasonsRoot, "../DVSurvival"));
            var mod = Path.Combine(survivalRoot, "artifacts/build/DVSurvival");
            var managed = Path.Combine(Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME"), "DerailValley_Data/Managed");
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
                foreach (var directory in new[] { mod, managed, Path.Combine(managed, "UnityModManager") })
                {
                    var path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            int code = 0;
            try
            {
                Assembly.LoadFrom(Path.Combine(survivalRoot, "artifacts/verification/VerifyStationOffice.dll"))
                    .GetType("VerifyStationOffice").GetMethod("Run").Invoke(null, new object[] { mod });
            }
            catch (Exception exception) { Debug.LogException(exception); code = 1; }
            EditorApplication.Exit(code);
        }
    }
}
