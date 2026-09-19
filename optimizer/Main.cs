using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityModManagerNet;

namespace DVSeasonsOptimizer
{
    public static class Main
    {
        private const string HarmonyId = "HerodrineYT.DVSeasons.VehicleSurfaceOptimizer";
        private const int StatsIntervalFrames = 600;

        private static readonly object Gate = new object();
        private static readonly ConditionalWeakTable<object, VehicleCache> VehicleCaches =
            new ConditionalWeakTable<object, VehicleCache>();

        private static Harmony harmony;
        private static object modEntry;
        private static bool patched;
        private static FieldInfo partVehicleBatchesField;
        private static PropertyInfo partVehicleBatchesProperty;
        private static FieldInfo vehiclePartsField;
        private static FieldInfo vehicleSignatureField;
        private static FieldInfo partLodField;
        private static FieldInfo partLodMaskField;
        private static FieldInfo partInteriorField;
        private static FieldInfo lodCurrentField;
        private static FieldInfo lodFilterInteriorField;

        [ThreadStatic] private static List<RestoreEntry> pendingRestores;
        [ThreadStatic] private static HashSet<object> filteredVehicles;
        [ThreadStatic] private static bool recordCoreActive;

        private static long filterTicks;
        private static long fullPartsSeen;
        private static long activePartsUsed;
        private static long selectionRebuilds;
        private static long referenceSwaps;
        private static long mutationFallbacks;
        private static int recordFrames;

        public static bool Load(UnityModManager.ModEntry entry)
        {
            modEntry = entry;
            harmony = new Harmony(HarmonyId);
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                TryPatch(assembly);
            Log("Loaded. Vehicle-level scheduler + cached active-LOD buckets enabled.");
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

                partVehicleBatchesField = FindField(registry, "PartVehicleBatchesEnabled");
                partVehicleBatchesProperty = registry.GetProperty("PartVehicleBatchesEnabled",
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                var recordCore = FindMethod(registry, "RecordCore");
                var updateLods = FindMethod(registry, "UpdateLods");
                var selectPartBatching = FindMethod(registry, "SelectPartBatching");

                if (recordCore == null || updateLods == null)
                {
                    Log("DVSeasons internals do not match 0.3.3 GitHub(10); optimizer left inactive.");
                    return;
                }

                harmony.Patch(recordCore,
                    prefix: new HarmonyMethod(typeof(Main), nameof(BeforeRecordCore)),
                    postfix: new HarmonyMethod(typeof(Main), nameof(AfterRecordCore)),
                    finalizer: new HarmonyMethod(typeof(Main), nameof(FinalizeRecordCore)));

                harmony.Patch(updateLods,
                    postfix: new HarmonyMethod(typeof(Main), nameof(AfterUpdateLods)));

                if (selectPartBatching != null)
                    harmony.Patch(selectPartBatching,
                        prefix: new HarmonyMethod(typeof(Main), nameof(ForceVehicleScheduler)));

                DisablePartBatches(null);
                patched = true;
                Log("DVSeasons LOD bucket optimizer patches applied.");
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
                // SelectPartBatching is patched as a fallback.
            }
        }

        private static void BeforeRecordCore(object __instance)
        {
            if (recordCoreActive) CompleteRecordCore();

            recordCoreActive = true;
            DisablePartBatches(__instance);

            if (pendingRestores == null) pendingRestores = new List<RestoreEntry>(256);
            else pendingRestores.Clear();

            if (filteredVehicles == null) filteredVehicles = new HashSet<object>(ReferenceComparer.Instance);
            else filteredVehicles.Clear();
        }

        private static void AfterUpdateLods(object __0)
        {
            if (!recordCoreActive || __0 == null) return;
            if (!filteredVehicles.Add(__0)) return;

            var started = Stopwatch.GetTimestamp();
            try
            {
                FilterVehicle(__0);
            }
            catch (Exception exception)
            {
                CompleteRecordCore();
                Log("LOD bucket filtering failed safely; original DVSeasons path retained. " +
                    exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                filterTicks += Stopwatch.GetTimestamp() - started;
            }
        }

        private static void FilterVehicle(object vehicle)
        {
            EnsureVehicleAccess(vehicle.GetType());

            var fullParts = vehiclePartsField.GetValue(vehicle) as IList;
            if (fullParts == null || fullParts.IsReadOnly) return;

            var signature = Convert.ToInt32(vehicleSignatureField.GetValue(vehicle));
            var cache = VehicleCaches.GetValue(vehicle, ignored => new VehicleCache());

            if (!cache.Ready ||
                cache.Signature != signature ||
                !ReferenceEquals(cache.FullList, fullParts) ||
                cache.FullParts.Length != fullParts.Count)
            {
                RebuildCache(cache, fullParts, signature);
            }

            if (!cache.Ready) return;

            var selectionChanged = !cache.SelectionReady;
            for (var i = 0; i < cache.Lods.Count; i++)
            {
                var lod = cache.Lods[i];
                var current = Convert.ToInt32(lodCurrentField.GetValue(lod.Instance));
                var filterInterior = Convert.ToBoolean(lodFilterInteriorField.GetValue(lod.Instance));

                if (!lod.SelectionReady || lod.Current != current || lod.FilterInterior != filterInterior)
                    selectionChanged = true;

                lod.Current = current;
                lod.FilterInterior = filterInterior;
                lod.SelectionReady = true;
            }

            if (selectionChanged)
            {
                BuildActiveParts(cache);
                cache.SelectionReady = true;
                selectionRebuilds++;
            }

            fullPartsSeen += cache.FullParts.Length;
            activePartsUsed += cache.ActiveCount;

            // Fast path: temporarily swap the readonly List<Part> field reference.
            // Mono permits this through reflection and the integration fixture verifies
            // it. This avoids clearing/re-adding the full 10k+ part list every frame.
            var swapped = false;
            try
            {
                vehiclePartsField.SetValue(vehicle, cache.ActiveList);
                swapped = ReferenceEquals(vehiclePartsField.GetValue(vehicle), cache.ActiveList);
            }
            catch
            {
                swapped = false;
            }

            if (swapped)
            {
                pendingRestores.Add(RestoreEntry.ForSwap(vehicle, fullParts));
                referenceSwaps++;
                return;
            }

            // Conservative fallback for runtimes that refuse readonly-field swaps.
            var snapshot = cache.FullParts;
            pendingRestores.Add(RestoreEntry.ForMutation(fullParts, snapshot));
            fullParts.Clear();
            for (var i = 0; i < cache.ActiveCount; i++)
                fullParts.Add(cache.ActiveParts[i]);
            mutationFallbacks++;
        }

        private static void BuildActiveParts(VehicleCache cache)
        {
            cache.MarkGeneration++;
            if (cache.MarkGeneration == int.MaxValue)
            {
                Array.Clear(cache.Marks, 0, cache.Marks.Length);
                cache.MarkGeneration = 1;
            }

            var generation = cache.MarkGeneration;

            for (var i = 0; i < cache.AlwaysIndices.Length; i++)
                cache.Marks[cache.AlwaysIndices[i]] = generation;

            for (var i = 0; i < cache.Lods.Count; i++)
            {
                var lod = cache.Lods[i];
                var current = lod.Current;

                if (current >= 0 && current < lod.LevelIndices.Length)
                {
                    var level = lod.LevelIndices[current];
                    if (level != null)
                        for (var j = 0; j < level.Length; j++)
                            cache.Marks[level[j]] = generation;
                }

                if (!lod.FilterInterior)
                {
                    var interiors = lod.InteriorIndices;
                    for (var j = 0; j < interiors.Length; j++)
                        cache.Marks[interiors[j]] = generation;
                }
            }

            cache.ActiveList.Clear();
            cache.ActiveCount = 0;

            for (var i = 0; i < cache.FullParts.Length; i++)
            {
                if (cache.Marks[i] != generation) continue;
                var part = cache.FullParts[i];
                if (part == null) continue;

                cache.ActiveParts[cache.ActiveCount++] = part;
                cache.ActiveList.Add(part);
            }
        }

        private static void AfterRecordCore()
        {
            CompleteRecordCore();
        }

        private static Exception FinalizeRecordCore(Exception __exception)
        {
            CompleteRecordCore();
            return __exception;
        }

        private static void CompleteRecordCore()
        {
            RestoreAll();
            recordCoreActive = false;
            if (filteredVehicles != null) filteredVehicles.Clear();

            recordFrames++;
            if (recordFrames >= StatsIntervalFrames)
            {
                var ms = filterTicks * 1000.0 / Stopwatch.Frequency / Math.Max(1, recordFrames);
                var rejected = Math.Max(0L, fullPartsSeen - activePartsUsed);
                var ratio = fullPartsSeen > 0 ? (100.0 * rejected / fullPartsSeen) : 0.0;

                Log("LOD buckets window: filter-ms/frame=" + ms.ToString("F3") +
                    "; full-parts=" + fullPartsSeen +
                    "; active-parts=" + activePartsUsed +
                    "; pre-cull-rejected=" + rejected +
                    " (" + ratio.ToString("F1") + "%)" +
                    "; selection-rebuilds=" + selectionRebuilds +
                    "; list-swaps=" + referenceSwaps +
                    "; mutation-fallbacks=" + mutationFallbacks + ".");

                recordFrames = 0;
                filterTicks = 0;
                fullPartsSeen = 0;
                activePartsUsed = 0;
                selectionRebuilds = 0;
                referenceSwaps = 0;
                mutationFallbacks = 0;
            }
        }

        private static void RestoreAll()
        {
            var restores = pendingRestores;
            if (restores == null || restores.Count == 0) return;

            for (var i = restores.Count - 1; i >= 0; i--)
            {
                var restore = restores[i];
                try
                {
                    if (restore.Swapped)
                    {
                        vehiclePartsField.SetValue(restore.Vehicle, restore.OriginalList);
                    }
                    else
                    {
                        restore.OriginalList.Clear();
                        for (var j = 0; j < restore.OriginalParts.Length; j++)
                            restore.OriginalList.Add(restore.OriginalParts[j]);
                    }
                }
                catch
                {
                    // Streamed vehicles may disappear during rendering.
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
            cache.SelectionReady = false;
            cache.Signature = signature;
            cache.FullList = parts;

            var count = parts.Count;
            cache.FullParts = new object[count];
            cache.ActiveParts = new object[count];
            cache.Marks = new int[count];
            cache.ActiveCount = 0;
            cache.MarkGeneration = 0;
            cache.Lods.Clear();

            if (cache.ActiveList == null || cache.ActiveList.GetType() != parts.GetType())
                cache.ActiveList = Activator.CreateInstance(parts.GetType()) as IList;
            if (cache.ActiveList == null || cache.ActiveList.IsReadOnly)
                throw new InvalidOperationException("Unable to create writable active Parts list.");
            cache.ActiveList.Clear();

            var always = new List<int>(count);
            var lodMap = new Dictionary<object, LodStateBuilder>(ReferenceComparer.Instance);

            for (var i = 0; i < count; i++)
            {
                var part = parts[i];
                cache.FullParts[i] = part;

                if (part == null) continue;

                EnsurePartAccess(part.GetType());
                var lodObject = partLodField.GetValue(part);

                if (lodObject == null)
                {
                    always.Add(i);
                    continue;
                }

                EnsureLodAccess(lodObject.GetType());

                LodStateBuilder builder;
                if (!lodMap.TryGetValue(lodObject, out builder))
                {
                    builder = new LodStateBuilder(lodObject);
                    lodMap.Add(lodObject, builder);
                }

                var mask = Convert.ToInt32(partLodMaskField.GetValue(part));
                var interior = Convert.ToBoolean(partInteriorField.GetValue(part));

                for (var bit = 0; bit < 32; bit++)
                    if ((mask & (1 << bit)) != 0)
                        builder.Levels[bit].Add(i);

                if (interior)
                    builder.Interiors.Add(i);
            }

            cache.AlwaysIndices = always.ToArray();

            foreach (var builder in lodMap.Values)
            {
                var state = new LodState(builder.Instance);
                for (var i = 0; i < state.LevelIndices.Length; i++)
                    state.LevelIndices[i] = builder.Levels[i].Count == 0 ? null : builder.Levels[i].ToArray();
                state.InteriorIndices = builder.Interiors.ToArray();
                cache.Lods.Add(state);
            }

            cache.Ready = true;
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
                        if (log != null)
                        {
                            log.Invoke(logger, new object[] { message });
                            return;
                        }
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
            public bool SelectionReady;
            public int Signature;
            public IList FullList;
            public IList ActiveList;
            public object[] FullParts = new object[0];
            public object[] ActiveParts = new object[0];
            public int ActiveCount;
            public int[] Marks = new int[0];
            public int MarkGeneration;
            public int[] AlwaysIndices = new int[0];
            public readonly List<LodState> Lods = new List<LodState>();
        }

        private sealed class LodState
        {
            public readonly object Instance;
            public readonly int[][] LevelIndices = new int[32][];
            public int[] InteriorIndices = new int[0];
            public int Current;
            public bool FilterInterior;
            public bool SelectionReady;

            public LodState(object instance)
            {
                Instance = instance;
            }
        }

        private sealed class LodStateBuilder
        {
            public readonly object Instance;
            public readonly List<int>[] Levels = new List<int>[32];
            public readonly List<int> Interiors = new List<int>();

            public LodStateBuilder(object instance)
            {
                Instance = instance;
                for (var i = 0; i < Levels.Length; i++)
                    Levels[i] = new List<int>();
            }
        }

        private struct RestoreEntry
        {
            public readonly bool Swapped;
            public readonly object Vehicle;
            public readonly IList OriginalList;
            public readonly object[] OriginalParts;

            private RestoreEntry(bool swapped, object vehicle, IList originalList, object[] originalParts)
            {
                Swapped = swapped;
                Vehicle = vehicle;
                OriginalList = originalList;
                OriginalParts = originalParts;
            }

            public static RestoreEntry ForSwap(object vehicle, IList originalList)
            {
                return new RestoreEntry(true, vehicle, originalList, null);
            }

            public static RestoreEntry ForMutation(IList originalList, object[] originalParts)
            {
                return new RestoreEntry(false, null, originalList, originalParts);
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
