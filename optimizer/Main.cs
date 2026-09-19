using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityModManagerNet;

namespace DVSeasonsOptimizer
{
    public static class Main
    {
        private const string HarmonyId = "HerodrineYT.DVSeasons.VehicleSurfaceOptimizer";
        private static readonly object Gate = new object();
        private static readonly ConditionalWeakTable<object, VehicleCache> VehicleCaches =
            new ConditionalWeakTable<object, VehicleCache>();

        private static Harmony harmony;
        private static object modEntry;
        private static bool patched;
        private static FieldInfo vehiclesField;
        private static FieldInfo partVehicleBatchesField;
        private static PropertyInfo partVehicleBatchesProperty;
        private static FieldInfo vehiclePartsField;
        private static FieldInfo vehicleSignatureField;
        private static FieldInfo partLodField;
        private static FieldInfo partLodMaskField;
        private static FieldInfo partInteriorField;
        private static FieldInfo lodCurrentField;
        private static FieldInfo lodFilterInteriorField;

        [ThreadStatic]
        private static List<RestoreEntry> pendingRestores;

        public static bool Load(UnityModManager.ModEntry entry)
        {
            modEntry = entry;
            harmony = new Harmony(HarmonyId);
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                TryPatch(assembly);
            Log("Loaded. Vehicle-level snow scheduling is forced and inactive LOD renderers are filtered before the hot culling loop.");
            return true;
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            TryPatch(args.LoadedAssembly);
        }

        private static void TryPatch(Assembly assembly)
        {
            if (assembly == null || !string.Equals(assembly.GetName().Name, "DVSeasons", StringComparison.Ordinal))
                return;
            lock (Gate)
            {
                if (patched) return;
                var registry = assembly.GetType("DVSeasons.Mod.SnowVehicleRegistry", false);
                if (registry == null)
                {
                    Log("DVSeasons loaded, but SnowVehicleRegistry was not found. Optimizer left inactive.");
                    return;
                }

                vehiclesField = FindField(registry, "vehicles");
                partVehicleBatchesField = FindField(registry, "PartVehicleBatchesEnabled");
                partVehicleBatchesProperty = registry.GetProperty("PartVehicleBatchesEnabled",
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var recordCore = FindMethod(registry, "RecordCore");
                var selectPartBatching = FindMethod(registry, "SelectPartBatching");
                if (vehiclesField == null || recordCore == null)
                {
                    Log("DVSeasons internals do not match 0.3.3 GitHub(10); optimizer left inactive.");
                    return;
                }

                harmony.Patch(recordCore,
                    prefix: new HarmonyMethod(typeof(Main), nameof(BeforeRecordCore)),
                    postfix: new HarmonyMethod(typeof(Main), nameof(AfterRecordCore)),
                    finalizer: new HarmonyMethod(typeof(Main), nameof(FinalizeRecordCore)));
                if (selectPartBatching != null)
                    harmony.Patch(selectPartBatching,
                        prefix: new HarmonyMethod(typeof(Main), nameof(ForceVehicleScheduler)));

                DisablePartBatches(null);
                patched = true;
                Log("DVSeasons vehicle surface optimizer patches applied.");
            }
        }

        private static MethodInfo FindMethod(Type type, string name)
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                                   BindingFlags.Public | BindingFlags.NonPublic))
                if (string.Equals(method.Name, name, StringComparison.Ordinal)) return method;
            return null;
        }

        private static FieldInfo FindField(Type type, string name)
        {
            return type.GetField(name, BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.Public | BindingFlags.NonPublic);
        }

        // The part-level graph reduced draw commands but cost more CPU than it
        // saved in the measured 224-car yard. Keep the cheaper vehicle stream
        // scheduler until a clean whole-frame A/B proves otherwise.
        private static bool ForceVehicleScheduler(ref bool __result)
        {
            __result = false;
            return false;
        }

        private static void DisablePartBatches(object instance)
        {
            try
            {
                if (partVehicleBatchesField != null && (partVehicleBatchesField.IsStatic || instance != null))
                    partVehicleBatchesField.SetValue(partVehicleBatchesField.IsStatic ? null : instance, false);
                if (partVehicleBatchesProperty != null && partVehicleBatchesProperty.CanWrite)
                {
                    var setter = partVehicleBatchesProperty.GetSetMethod(true);
                    if (setter != null && (setter.IsStatic || instance != null))
                        partVehicleBatchesProperty.SetValue(setter.IsStatic ? null : instance, false, null);
                }
            }
            catch
            {
                // SelectPartBatching is patched as a fallback even if this flag
                // changes shape in a later DVSeasons build.
            }
        }

        private static void BeforeRecordCore(object __instance)
        {
            try
            {
                DisablePartBatches(__instance);
                var vehicles = vehiclesField.GetValue(__instance) as IEnumerable;
                if (vehicles == null) return;
                if (pendingRestores == null) pendingRestores = new List<RestoreEntry>(256);
                pendingRestores.Clear();

                foreach (var vehicle in vehicles)
                {
                    if (vehicle == null) continue;
                    EnsureVehicleAccess(vehicle.GetType());
                    var parts = vehiclePartsField.GetValue(vehicle) as IList;
                    if (parts == null || parts.IsReadOnly) continue;
                    var signature = Convert.ToInt32(vehicleSignatureField.GetValue(vehicle));
                    var cache = VehicleCaches.GetValue(vehicle, ignored => new VehicleCache());
                    if (!cache.Ready || cache.Signature != signature || cache.FullParts.Length != parts.Count)
                        RebuildCache(cache, parts, signature);
                    if (!cache.Ready) continue;

                    for (var i = 0; i < cache.Lods.Count; i++)
                    {
                        var lod = cache.Lods[i];
                        lod.Current = Convert.ToInt32(lodCurrentField.GetValue(lod.Instance));
                        lod.FilterInterior = Convert.ToBoolean(lodFilterInteriorField.GetValue(lod.Instance));
                    }

                    parts.Clear();
                    var entries = cache.Entries;
                    for (var i = 0; i < entries.Length; i++)
                    {
                        var part = entries[i];
                        if (IsActive(part)) parts.Add(part.Instance);
                    }
                    pendingRestores.Add(new RestoreEntry(parts, cache.FullParts));
                }
            }
            catch (Exception exception)
            {
                RestoreAll();
                Log("LOD filtering failed safely; original DVSeasons path retained. " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static void AfterRecordCore()
        {
            RestoreAll();
        }

        private static Exception FinalizeRecordCore(Exception __exception)
        {
            RestoreAll();
            return __exception;
        }

        private static void RestoreAll()
        {
            var restores = pendingRestores;
            if (restores == null || restores.Count == 0) return;
            for (var i = 0; i < restores.Count; i++)
            {
                var restore = restores[i];
                try
                {
                    restore.Parts.Clear();
                    for (var j = 0; j < restore.FullParts.Length; j++)
                        restore.Parts.Add(restore.FullParts[j]);
                }
                catch
                {
                    // A streamed vehicle can disappear while rendering. The
                    // original registry will remove it on its next discovery.
                }
            }
            restores.Clear();
        }

        private static void EnsureVehicleAccess(Type vehicleType)
        {
            if (vehiclePartsField != null) return;
            vehiclePartsField = FindField(vehicleType, "Parts");
            vehicleSignatureField = FindField(vehicleType, "Signature");
            if (vehiclePartsField == null || vehicleSignatureField == null)
                throw new MissingFieldException(vehicleType.FullName, "Parts/Signature");
        }

        private static void EnsurePartAccess(Type partType)
        {
            if (partLodField != null) return;
            partLodField = FindField(partType, "Lod");
            partLodMaskField = FindField(partType, "LodMask");
            partInteriorField = FindField(partType, "Interior");
            if (partLodField == null || partLodMaskField == null || partInteriorField == null)
                throw new MissingFieldException(partType.FullName, "Lod/LodMask/Interior");
        }

        private static void EnsureLodAccess(Type lodType)
        {
            if (lodCurrentField != null) return;
            lodCurrentField = FindField(lodType, "Current");
            lodFilterInteriorField = FindField(lodType, "FilterInterior");
            if (lodCurrentField == null || lodFilterInteriorField == null)
                throw new MissingFieldException(lodType.FullName, "Current/FilterInterior");
        }

        private static void RebuildCache(VehicleCache cache, IList parts, int signature)
        {
            cache.Ready = false;
            cache.Signature = signature;
            cache.FullParts = new object[parts.Count];
            cache.Entries = new PartEntry[parts.Count];
            cache.Lods.Clear();
            var lodMap = new Dictionary<object, LodState>(ReferenceComparer.Instance);

            for (var i = 0; i < parts.Count; i++)
            {
                var instance = parts[i];
                cache.FullParts[i] = instance;
                if (instance == null)
                {
                    cache.Entries[i] = new PartEntry(null, null, 0, false);
                    continue;
                }
                EnsurePartAccess(instance.GetType());
                var lodObject = partLodField.GetValue(instance);
                LodState lod = null;
                if (lodObject != null)
                {
                    EnsureLodAccess(lodObject.GetType());
                    if (!lodMap.TryGetValue(lodObject, out lod))
                    {
                        lod = new LodState(lodObject);
                        lodMap.Add(lodObject, lod);
                        cache.Lods.Add(lod);
                    }
                }
                cache.Entries[i] = new PartEntry(instance, lod,
                    Convert.ToInt32(partLodMaskField.GetValue(instance)),
                    Convert.ToBoolean(partInteriorField.GetValue(instance)));
            }
            cache.Ready = true;
        }

        private static bool IsActive(PartEntry part)
        {
            if (part.Instance == null) return false;
            if (part.Lod == null) return true;
            if (part.Interior && !part.Lod.FilterInterior) return true;
            var current = part.Lod.Current;
            return current >= 0 && current < 32 && (part.LodMask & (1 << current)) != 0;
        }

        private static void Log(string message)
        {
            var line = "[DVSeasonsOptimizer] " + message;
            try
            {
                if (modEntry != null)
                {
                    var type = modEntry.GetType();
                    object logger = null;
                    var property = type.GetProperty("Logger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property != null) logger = property.GetValue(modEntry, null);
                    if (logger == null)
                    {
                        var field = type.GetField("Logger", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (field != null) logger = field.GetValue(modEntry);
                    }
                    if (logger != null)
                    {
                        var log = logger.GetType().GetMethod("Log", new[] { typeof(string) });
                        if (log != null) { log.Invoke(logger, new object[] { message }); return; }
                    }
                }
            }
            catch
            {
            }
            Console.WriteLine(line);
        }

        private sealed class VehicleCache
        {
            public bool Ready;
            public int Signature;
            public object[] FullParts = new object[0];
            public PartEntry[] Entries = new PartEntry[0];
            public readonly List<LodState> Lods = new List<LodState>();
        }

        private sealed class LodState
        {
            public readonly object Instance;
            public int Current;
            public bool FilterInterior;
            public LodState(object instance) { Instance = instance; }
        }

        private struct PartEntry
        {
            public readonly object Instance;
            public readonly LodState Lod;
            public readonly int LodMask;
            public readonly bool Interior;
            public PartEntry(object instance, LodState lod, int lodMask, bool interior)
            {
                Instance = instance;
                Lod = lod;
                LodMask = lodMask;
                Interior = interior;
            }
        }

        private struct RestoreEntry
        {
            public readonly IList Parts;
            public readonly object[] FullParts;
            public RestoreEntry(IList parts, object[] fullParts)
            {
                Parts = parts;
                FullParts = fullParts;
            }
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
