using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;

namespace DVSeasons.AssetBundleBuild
{
    public static class HeaterMaterialVerification
    {
        public static void Run()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var mod = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD");
            var managed = Path.Combine(Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME"),"DerailValley_Data/Managed");
            AppDomain.CurrentDomain.AssemblyResolve += (s,e) => {
                foreach (var dir in new[] { mod, managed, Path.Combine(managed,"UnityModManager") })
                { var path = Path.Combine(dir,new AssemblyName(e.Name).Name+".dll"); if(File.Exists(path))return Assembly.LoadFrom(path); }
                return null;
            };
            int code = 0;
            try
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/SnowVehicleStandard.shader");
                Assembly.LoadFrom(Path.Combine(root,"artifacts/verification/VerifyHeaterMaterial.dll")).GetType("VerifyHeaterMaterial")
                    .GetMethod("Run").Invoke(null,new object[] { mod, shader });
            }
            catch(Exception e) { Debug.LogException(e); code = 1; }
            EditorApplication.Exit(code);
        }
    }
}
