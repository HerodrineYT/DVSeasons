using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    // Native Unity/game-assembly checks supplement the host/two-client transport
    // fixtures. This is not a live network connection to another game process.
    public static class MultiplayerCompatibilityVerification
    {
        const BindingFlags All = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        static object Get(object owner, string name) { return owner.GetType().GetField(name, All).GetValue(owner); }
        static object Call(object owner, string name, params object[] args) { return owner.GetType().GetMethod(name, All).Invoke(owner, args); }
        public static void Run()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            var game = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            var modDir = Path.Combine(root, "artifacts/build/DVSeasons");
            var managed = Path.Combine(game, "DerailValley_Data/Managed");
            AppDomain.CurrentDomain.AssemblyResolve += (s,e) =>
            {
                foreach (var dir in new[] { modDir, managed, Path.Combine(managed, "UnityModManager"), Path.Combine(game,"Mods/Multiplayer") })
                {
                    var file = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            int code = 0;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var mod = Assembly.LoadFrom(Path.Combine(modDir,"DVSeasons.dll"));
                var thermalType = mod.GetType("DVSeasons.Mod.SeasonalThermalController", true);
                var thermal = Activator.CreateInstance(thermalType, true);
                try
                {
                    Call(thermal, "Apply", -30f, true);
                    Require((bool)Get(thermal,"installed"), "Host thermal patches failed to install");
                    var cold = Get(thermal,"coldPowertrain");
                    Require((bool)Get(cold,"installed"), "Host starter patches failed to install");
                    Call(thermal, "Apply", -30f, false);
                    Require(!(bool)Get(thermal,"installed") && !(bool)Get(cold,"installed"), "Client still owns simulation patches");
                    Require(thermalType.GetField("active",All).GetValue(null) == null, "Client thermal hook still active");
                    Require(cold.GetType().GetField("active",All).GetValue(null) == null, "Client cold postfix still active");
                    Require((bool)Get(thermal,"lampsInstalled"), "Client lost visual temperature lamp protection");
                    Call(thermal, "Apply", 25f, true);
                    Require((bool)Get(thermal,"installed") && (bool)Get(cold,"installed"), "Next local session cannot resume simulation");
                }
                finally { ((IDisposable)thermal).Dispose(); }
                var snowType = mod.GetType("DVSeasons.Mod.ProceduralSnowController", true);
                var snow = Activator.CreateInstance(snowType, new object[] { null });
                try
                {
                    Call(snow,"SetNetworkCoverage", .42f);
                    Call(snow,"SetWeather", 1f);
                    Call(snow,"Apply", 1f, true);
                    Require(Mathf.Abs((float)snowType.GetProperty("Coverage").GetValue(snow,null) - .42f) < .0001f,
                        "Local snowfall replaced the host's saved snow");
                    Call(snow,"ReseedSeasonCoverage");
                    Call(snow,"SetNetworkCoverage", 1f);
                    Call(snow,"Apply", 1f, true);
                    Require((float)snowType.GetProperty("Coverage").GetValue(snow,null) == 1f, "Repeated winter selection failed");
                    Call(snow,"Apply", 0f, true);
                    Require((float)snowType.GetProperty("Coverage").GetValue(snow,null) == 0f, "Summer retained host snow");
                }
                finally { ((IDisposable)snow).Dispose(); }
                var bridge = Assembly.LoadFrom(Path.Combine(modDir,"DVSeasons.Multiplayer.dll"))
                    .GetType("DVSeasons.Multiplayer.MultiplayerSeasonBridge",true);
                ((IDisposable)Activator.CreateInstance(bridge)).Dispose();
                ColdFeaturesVerification.Run();
                Debug.Log("MP_016_COMPATIBILITY_OK: installed API load, host/client thermal ownership, restored snow, native diesel/BE2 regressions.");
            }
            catch (Exception e) { Debug.LogException(e); code = 1; }
            EditorApplication.Exit(code);
        }
    }
}
