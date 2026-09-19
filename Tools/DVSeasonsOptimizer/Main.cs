using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;

namespace DVSeasonsOptimizer
{
    public static class Main
    {
        private const string HarmonyId = "HerodrineYT.DVSeasonsOptimizer";
        private static UnityModManager.ModEntry _mod;
        private static Harmony _harmony;
        private static readonly HashSet<string> LoggedFields = new HashSet<string>();
        private static readonly HashSet<string> LoggedMethods = new HashSet<string>();

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            _mod = modEntry;
            _harmony = new Harmony(HarmonyId);

            try
            {
                Assembly dv = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, "DVSeasons", StringComparison.OrdinalIgnoreCase));

                if (dv == null)
                {
                    modEntry.Logger.Error("DVSeasons.dll is not loaded. Optimizer disabled.");
                    return true;
                }

                Type registry = SafeTypes(dv).FirstOrDefault(t => t.Name == "SnowVehicleRegistry");
                if (registry == null)
                {
                    modEntry.Logger.Error("SnowVehicleRegistry was not found. This optimizer requires DVSeasons 0.3.3 GitHub(10)-style vehicle snow code.");
                    return true;
                }

                MethodInfo record = registry.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "Record");

                if (record == null)
                {
                    modEntry.Logger.Error("SnowVehicleRegistry.Record was not found. Optimizer disabled.");
                    return true;
                }

                MethodInfo before = typeof(Main).GetMethod(nameof(BeforeRecord), BindingFlags.Static | BindingFlags.NonPublic);
                _harmony.Patch(record, prefix: new HarmonyMethod(before));

                int decisionPatches = 0;
                foreach (Type t in SafeTypes(dv).Where(t => t.Name.IndexOf("SnowVehicle", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (m.ReturnType != typeof(bool) || m.IsAbstract || m.ContainsGenericParameters)
                            continue;

                        string n = m.Name.ToLowerInvariant();
                        if (!n.Contains("part"))
                            continue;
                        if (!(n.Contains("batch") || n.Contains("schedul") || n.Contains("mode") ||
                              n.Contains("prefer") || n.Contains("allow") || n.Contains("enable") ||
                              n.Contains("usepart")))
                            continue;

                        try
                        {
                            MethodInfo postfix = typeof(Main).GetMethod(nameof(ForceFalse), BindingFlags.Static | BindingFlags.NonPublic);
                            _harmony.Patch(m, postfix: new HarmonyMethod(postfix));
                            decisionPatches++;
                            LogMethodOnce(t.FullName + "." + m.Name);
                        }
                        catch (Exception ex)
                        {
                            modEntry.Logger.Warning("Could not patch decision method " + t.FullName + "." + m.Name + ": " + ex.Message);
                        }
                    }
                }

                modEntry.OnUnload = Unload;
                modEntry.Logger.Log("Loaded.");
                modEntry.Logger.Log("DVSeasons vehicle scheduler optimizer active. Record hook + " + decisionPatches + " part-scheduler decision patch(es).");
                return true;
            }
            catch (Exception ex)
            {
                modEntry.Logger.Error("Initialization failed: " + ex);
                return true;
            }
        }

        private static bool Unload(UnityModManager.ModEntry modEntry)
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch { }
            return true;
        }

        private static void ForceFalse(ref bool __result)
        {
            __result = false;
        }

        private static void BeforeRecord(object __instance)
        {
            if (__instance == null)
                return;

            try
            {
                ForcePartSchedulerFlagsOff(__instance, __instance.GetType(), instanceOnly: true);

                Assembly dv = __instance.GetType().Assembly;
                foreach (Type t in SafeTypes(dv).Where(t => t.Name.IndexOf("SnowVehicle", StringComparison.OrdinalIgnoreCase) >= 0))
                    ForcePartSchedulerFlagsOff(null, t, instanceOnly: false);

                // Some versions keep the actual scheduler as an instance field.
                foreach (FieldInfo f in __instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType.IsPrimitive || f.FieldType == typeof(string))
                        continue;
                    string fn = f.Name.ToLowerInvariant();
                    string tn = f.FieldType.Name.ToLowerInvariant();
                    if (!(fn.Contains("scheduler") || tn.Contains("scheduler")))
                        continue;

                    object child = null;
                    try { child = f.GetValue(__instance); } catch { }
                    if (child != null)
                        ForcePartSchedulerFlagsOff(child, child.GetType(), instanceOnly: true);
                }
            }
            catch (Exception ex)
            {
                _mod?.Logger.Warning("Per-frame optimizer guard failed: " + ex.Message);
            }
        }

        private static void ForcePartSchedulerFlagsOff(object instance, Type type, bool instanceOnly)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                 (instanceOnly ? BindingFlags.Instance : BindingFlags.Static);

            foreach (FieldInfo f in type.GetFields(flags))
            {
                if (f.FieldType != typeof(bool) || f.IsLiteral)
                    continue;

                string n = Normalize(f.Name);
                if (!n.Contains("part"))
                    continue;

                bool relevant =
                    n.Contains("batch") ||
                    n.Contains("schedul") ||
                    n.Contains("mode") ||
                    n.Contains("prefer") ||
                    n.Contains("candidate") ||
                    n.Contains("selected") ||
                    n.Contains("active");

                if (!relevant)
                    continue;

                try
                {
                    object target = f.IsStatic ? null : instance;
                    if (!f.IsStatic && target == null)
                        continue;

                    bool value = (bool)f.GetValue(target);
                    if (value)
                    {
                        f.SetValue(target, false);
                        LogFieldOnce(type.FullName + "." + f.Name);
                    }
                }
                catch { }
            }
        }

        private static string Normalize(string s)
        {
            return (s ?? string.Empty).Replace("_", string.Empty).ToLowerInvariant();
        }

        private static IEnumerable<Type> SafeTypes(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }

        private static void LogFieldOnce(string name)
        {
            if (_mod != null && LoggedFields.Add(name))
                _mod.Logger.Log("Forced vehicle-level scheduler flag: " + name + " = false");
        }

        private static void LogMethodOnce(string name)
        {
            if (_mod != null && LoggedMethods.Add(name))
                _mod.Logger.Log("Patched part-scheduler decision: " + name + " -> false");
        }
    }
}