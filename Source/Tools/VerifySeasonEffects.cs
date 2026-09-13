using System;
using System.IO;
using System.Reflection;
using DV.WeatherSystem;
using DVSeasons.Core;
using UnityEngine;

// Run inside Unity Mono against the real DV99 weather methods and compiled mod.
public static class VerifySeasonEffects
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    static object Get(object target, string field) { return target.GetType().GetField(field, All).GetValue(target); }
    static void Set(object target, string field, object value) { target.GetType().GetField(field, All).SetValue(target, value); }
    static object Call(object target, string method, params object[] args)
    { return target.GetType().GetMethod(method, All).Invoke(target, args); }

    public static void Run(string modDirectory)
    {
        var mod = Assembly.LoadFrom(Path.Combine(modDirectory, "DVSeasons.dll"));
        VerifyWeather(mod);
        VerifyLeaves(mod);
        Debug.Log("SEASON_EFFECTS_OK: native wetness integration, snapshot/exception restoration, local leaf attachment and wind release.");
    }

    static void VerifyWeather(Assembly mod)
    {
        // The editor invokes OnValidate before serialized TOD parameters exist.
        var fixturePatch = new HarmonyLib.Harmony("DVSeasons.VerifySeasonEffects.Fixture");
        fixturePatch.Patch(HarmonyLib.AccessTools.Method(typeof(TOD_Sky), "OnValidate"),
            prefix: new HarmonyLib.HarmonyMethod(typeof(VerifySeasonEffects), "SkipValidate"));
        var go = new GameObject("weather isolation verification"); go.SetActive(false);
        var driver = go.AddComponent<WeatherDriver>();
        var manager = go.AddComponent<WeatherPresetManager>();
        manager.todSky = go.AddComponent<TOD_Sky>();
        manager.todSky.Cycle = new TOD_CycleParameters();
        manager.todTime = go.AddComponent<TOD_Time>(); driver.manager = manager;
        driver.todAnimation = go.AddComponent<TOD_Animation>();
        Set(driver, "s_", new WeatherStateChungus(0f));
        driver.dryingLengthMin = 5; driver.dryingLengthMax = 10;
        var adapter = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.WeatherAdapter", true), true);
        Set(adapter, "driver", driver);
        var isolation = Get(adapter, "isolation");
        try
        {
            Call(isolation, "Enable", adapter);
            driver.WetnessValue.RealValue = .1f;
            var winter = new SeasonCycle(new SeasonSettingsSnapshot(true, 14, 3, SeasonKind.Winter, true, .5f), 3).GetState();
            Call(adapter, "ApplyWinterAdhesion", winter, true, false);
            Call(adapter, "ApplyWinterThunderSuppression", winter, true);
            Call(driver, "UpdateWetnessHours", .1f, 1f);
            Require(driver.WetnessValue.RealValue < .1f, "Winter override fed back into native wetness");
            Require(driver.WetnessValue.CurrentValue == .5f, "Drying removed live winter adhesion");

            // Observe the actual serialized override fields, not a test double.
            var snapshot = driver.GetSaveData(true);
            string json = snapshot["Overrides"].ToString();
            Require(!json.Contains("0.5"), "Seasonal wetness leaked into serialized overrides: " + json);
            Require(driver.WetnessValue.IsOverridden && driver.ThunderValue.IsOverridden,
                "Snapshot did not restore live effects");
            Debug.Log("SEASON_SNAPSHOT: " + json);

            driver.manager = null;
            try { driver.GetSaveData(true); throw new Exception("Expected invalid-manager failure"); }
            catch (NullReferenceException) { }
            Require(driver.WetnessValue.IsOverridden && driver.WetnessValue.CurrentValue == .5f &&
                driver.ThunderValue.IsOverridden, "Failed snapshot did not restore effects");
            driver.manager = manager;

            Call(adapter, "ReleaseWetnessOverride"); Call(adapter, "ReleaseThunderOverride");
            Require(!driver.WetnessValue.IsOverridden && driver.WetnessValue.CurrentValue < .1f,
                "Winter left residual adhesion after release");
            Require(!driver.ThunderValue.IsOverridden, "Winter left thunder disabled");
            driver.WetnessValue.EngageOverride(.2f);
            Call(adapter, "ApplyWinterAdhesion", winter, true, false);
            Call(adapter, "ReleaseWetnessOverride");
            Require(driver.WetnessValue.IsOverridden && driver.WetnessValue.CurrentValue == .2f,
                "External wetness baseline was not restored");
        }
        finally
        {
            ((IDisposable)adapter).Dispose(); UnityEngine.Object.DestroyImmediate(go);
            fixturePatch.UnpatchAll("DVSeasons.VerifySeasonEffects.Fixture");
        }
    }

    static bool SkipValidate() { return false; }

    static void VerifyLeaves(Assembly mod)
    {
        var type = mod.GetType("DVSeasons.Mod.AutumnLeafGroundController", true);
        var controller = Activator.CreateInstance(type, All, null, new object[] { null }, null);
        var bodyType = type.GetNestedType("LeafBody", All);
        var leaf = Activator.CreateInstance(bodyType, true);
        var deck = new GameObject("walkable leaf anchor");
        try
        {
            var array = (Array)Get(controller, "leaves"); array.SetValue(leaf, 0); Set(controller, "leafCount", 1);
            Set(leaf, "SurfaceTransform", deck.transform);
            Set(leaf, "SurfaceLocalPosition", new Vector3(1, 2, 3));
            Set(leaf, "SurfaceLocalNormal", Vector3.up);
            Set(leaf, "SurfaceLocalRotation", Quaternion.identity);
            Set(leaf, "Settled", true);
            var originShift = new Vector3(1000, 0, -500);
            deck.transform.position = originShift + new Vector3(20, 0, 5);
            deck.transform.rotation = Quaternion.Euler(4, 77, 6);
            Call(controller, "UpdateAnchoredLeaves", originShift, 0f);
            Vector3 expected = deck.transform.TransformPoint(new Vector3(1, 2, 3)) - originShift + deck.transform.up * .018f;
            Require(Vector3.Distance((Vector3)Get(leaf, "Position"), expected) < .0002f,
                "Leaf moved relative to its rotating/origin-shifted surface");
            Set(leaf, "SurfaceVelocity", new Vector3(10, 0, 0));
            Call(controller, "LiftLeafByWind", leaf, new Vector3(0, 0, 12), 1f);
            Require(!(bool)Get(leaf, "Settled") && Get(leaf, "SurfaceTransform") == null,
                "Wind could not detach an attached leaf");
            Require(((Vector3)Get(leaf, "Velocity")).x > 9.5f, "Leaf lost vehicle momentum on release");
            Set(controller, "surfaceQueriesRemaining", 0);
            Set(leaf, "Position", Vector3.zero); Set(leaf, "LandingPosition", Vector3.zero);
            Set(leaf, "Velocity", Vector3.down);
            Call(controller, "SimulateLeaves", 1f, Vector3.zero, Vector3.zero, Vector3.zero, .05f);
            Require(!(bool)Get(leaf, "Settled"), "Query exhaustion created an unanchored phantom landing");
        }
        finally { ((IDisposable)controller).Dispose(); UnityEngine.Object.DestroyImmediate(deck); }
    }
}
