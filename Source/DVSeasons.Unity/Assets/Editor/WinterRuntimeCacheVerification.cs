using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    // Production discovery and binding cleanup with actual Unity/Game objects.
    public static class WinterRuntimeCacheVerification
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        static int checks;
        static readonly List<GameObject> objects = new List<GameObject>();
        static object Get(object value, string name) { return value.GetType().GetField(name, All).GetValue(value); }
        static void Set(object value, string name, object field) { value.GetType().GetField(name, All).SetValue(value, field); }
        static object Call(object value, string name, params object[] arguments)
        { return value.GetType().GetMethod(name, All).Invoke(value, arguments); }
        static void Require(bool success, string message) { checks++; if (!success) throw new InvalidOperationException(message); }
        static GameObject Object(string name, Transform parent = null)
        {
            var value = new GameObject(name); value.SetActive(false); objects.Add(value);
            if (parent != null) value.transform.SetParent(parent, false);
            return value;
        }
        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            string runtime = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD") ?? Path.Combine(root, "artifacts/build/DVSeasons");
            string game = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME") ?? "F:/steam/steamapps/common/Derail Valley";
            string managed = Path.Combine(game, "DerailValley_Data/Managed");
            ResolveEventHandler resolver = (sender, args) => {
                foreach (var directory in new[] { runtime, managed, Path.Combine(managed, "UnityModManager") })
                {
                    string file = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
                    if (File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            int result = 0;
            try
            {
                var gameAssembly = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
                var mod = Assembly.LoadFrom(Path.Combine(runtime, "DVSeasons.dll"));
                VerifyWindows(mod, gameAssembly); VerifyHeaterCleanup(mod, gameAssembly);
                Debug.Log("WINTER_RUNTIME_CACHE_OK checks=" + checks + ": locomotive priority, nested/detached roots, streaming, iterator disposal, destroyed heater cleanup and saved thermal history.");
            }
            catch (Exception error) { Debug.LogException(error); result = 1; }
            finally
            {
                foreach (var value in objects) if (value != null) UnityEngine.Object.DestroyImmediate(value);
                objects.Clear(); AppDomain.CurrentDomain.AssemblyResolve -= resolver;
            }
            EditorApplication.Exit(result);
        }
        static Component Car(Type type, string name, Vector3 position, bool locomotive)
        {
            var root = Object(name); root.transform.position = position;
            var car = root.AddComponent(type); Set(car, "_isLoco", (bool?)locomotive);
            root.SetActive(true); return car;
        }
        static Component Window(Type type, string name, Transform parent, bool simulate)
        {
            var window = Object(name, parent).AddComponent(type); Set(window, "simulate", simulate);
            window.gameObject.SetActive(true); return window;
        }
        static List<object> Enumerate(object controller, IList cars)
        {
            var result = new List<object>();
            var iterator = ((IEnumerable)Call(controller, "LoadedWindowsFromCars", cars, Vector3.zero)).GetEnumerator();
            try { while (iterator.MoveNext()) if (iterator.Current != null) result.Add(iterator.Current); }
            finally { ((IDisposable)iterator).Dispose(); }
            return result;
        }
        static void EmptyScratch(object controller)
        {
            foreach (var field in new[] { "nearbyWindowCars", "windowRoots", "windowScratch" })
                Require(((IList)Get(controller, field)).Count == 0, "Discovery retained references in " + field);
        }
        static void VerifyWindows(Assembly mod, Assembly game)
        {
            var type = mod.GetType("DVSeasons.Mod.WinterWindowController", true);
            var controller = Activator.CreateInstance(type, new object[] { null });
            var carType = game.GetType("TrainCar", true); var windowType = game.GetType("DV.Rain.Window", true);
            var cars = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(carType));
            var near = Car(carType, "Near locomotive", Vector3.right, true);
            var far = Car(carType, "Far locomotive", Vector3.right * 100, true);
            var freight = Car(carType, "Freight wagon", Vector3.zero, false);
            cars.Add(far); cars.Add(freight); cars.Add(near);
            var interior = Object("Nested loaded interior", near.transform); interior.SetActive(true);
            carType.GetProperty("loadedInterior").SetValue(near, interior, null);
            Set(near, "interior", interior.transform);
            var master = Window(windowType, "Interior master", interior.transform, true);
            var duplicate = Window(windowType, "Interior duplicate", interior.transform, false);
            var bodyWindow = Window(windowType, "Body window", near.transform, false);
            var external = Object("Detached external"); external.SetActive(true);
            carType.GetProperty("loadedExternalInteractables").SetValue(near, external, null);
            var externalWindow = Window(windowType, "Detached pane", external.transform, false);
            var farWindow = Window(windowType, "Far pane", far.transform, true);
            var excluded = Window(windowType, "Freight pane", freight.transform, true);
            var expected = new[] { master, duplicate, master, duplicate, bodyWindow, externalWindow, farWindow };
            var windows = Enumerate(controller, cars);
            Require(windows.Count == expected.Length, "Nested master/body/external window count changed");
            for (int i = 0; i < expected.Length; i++) Require(ReferenceEquals(windows[i], expected[i]), "Discovery order changed at " + i);
            Require(!windows.Contains(excluded), "Freight wagon was walked for locomotive windows");
            EmptyScratch(controller);
            // Existing capacity is reused, but hierarchy membership is never
            // cached across scans: replacement windows appear immediately.
            var replacement = Object("Replacement detached external"); replacement.SetActive(true);
            var replacementWindow = Window(windowType, "Replacement pane", replacement.transform, false);
            carType.GetProperty("loadedExternalInteractables").SetValue(near, replacement, null);
            windows = Enumerate(controller, cars);
            Require(windows.Contains(replacementWindow) && !windows.Contains(externalWindow), "Streamed external replacement was missed");
            far.gameObject.SetActive(false);
            windows = Enumerate(controller, cars);
            Require(!windows.Contains(farWindow), "Inactive locomotive remained in discovery");
            EmptyScratch(controller);
            var enumerator = ((IEnumerable)Call(controller, "LoadedWindowsFromCars", cars, Vector3.zero)).GetEnumerator();
            Require(enumerator.MoveNext(), "Fixture discovery had no work"); ((IDisposable)enumerator).Dispose();
            EmptyScratch(controller);
            Call(controller, "Dispose");
        }
        static void VerifyHeaterCleanup(Assembly mod, Assembly game)
        {
            var type = mod.GetType("DVSeasons.Mod.CabEngineHeating", true);
            var controller = Activator.CreateInstance(type, true);
            var carType = game.GetType("TrainCar", true);
            var livingRoot = Object("Living custom locomotive"); var living = livingRoot.AddComponent(carType);
            var expiredRoot = Object("Destroyed custom locomotive"); var expired = expiredRoot.AddComponent(carType);
            var bindingType = type.GetNestedType("Binding", All);
            var livingBinding = Activator.CreateInstance(bindingType, true);
            var expiredBinding = Activator.CreateInstance(bindingType, true);
            Set(livingBinding, "CarId", "living");
            Require(!(bool)Call(controller, "RefreshBindingIdentity", expired, expiredBinding), "Uninitialized car produced a GUID");
            var logicField = carType.GetField("logicCar", All);
            var logic = FormatterServices.GetUninitializedObject(logicField.FieldType);
            logicField.FieldType.GetField("carGuid", All).SetValue(logic, "expired");
            logicField.SetValue(expired, logic);
            Require((bool)Call(controller, "RefreshBindingIdentity", expired, expiredBinding), "Late GUID was not discovered");
            Require((string)Get(expiredBinding, "CarId") == "expired", "Late GUID was not retained for cleanup");
            logicField.SetValue(expired, null);
            Require(!(bool)Call(controller, "RefreshBindingIdentity", expired, expiredBinding) &&
                (string)Get(expiredBinding, "CarId") == "expired", "Pooling cleared the cached valid GUID");
            var climate = Get(expiredBinding, "Climate");
            Call(climate, "AdvanceElectricHeated", 100f, -20f, 1f, false, 1f);
            var capture = Call(climate, "Capture");
            var bindings = (IDictionary)Get(controller, "bindings");
            bindings.Add(living, livingBinding); bindings.Add(expired, expiredBinding);
            Call(controller, "PruneDestroyed"); Require(bindings.Count == 2, "Live bindings were pruned");
            UnityEngine.Object.DestroyImmediate(expiredRoot);
            Call(controller, "PruneDestroyed");
            Require(bindings.Count == 1 && bindings.Contains(living), "Destroyed managed TrainCar graph was retained");
            var saved = (IDictionary)Get(controller, "savedClimates");
            Require(saved.Contains("expired"), "Destroyed cab lost its thermal history");
            foreach (var field in capture.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                Require(Equals(field.GetValue(capture), field.GetValue(saved["expired"])), "Thermal state changed during pruning: " + field.Name);
            var destination = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), capture.GetType()));
            var currentNative = Activator.CreateInstance(capture.GetType());
            saved["native"] = capture; destination["native"] = currentNative;
            var livingLogic = FormatterServices.GetUninitializedObject(logicField.FieldType);
            logicField.FieldType.GetField("carGuid", All).SetValue(livingLogic, "living-final-guid");
            logicField.SetValue(living, livingLogic);
            Call(controller, "SaveClimates", destination);
            Require(destination.Contains("expired"), "Pruned cab thermal history was absent from save");
            Require(ReferenceEquals(destination["native"], currentNative), "Restored custom cache overwrote current native-cab climate");
            Require(destination.Contains("living-final-guid") && (string)Get(livingBinding, "CarId") == "living-final-guid",
                "Saving did not refresh the late live GUID used by future cleanup");
            logicField.SetValue(living, null);
            Require(((IList)Get(controller, "expiredBindings")).Count == 0, "Cleanup scratch retained destroyed objects");
            VerifyReplacementHistory(controller, bindings, bindingType, carType, capture);
            Call(controller, "Clear");
            Require(bindings.Count == 0 && saved.Count == 0, "Session reset kept thermal caches");
        }
        static void VerifyReplacementHistory(object controller, IDictionary bindings, Type bindingType, Type carType,
            object capture)
        {
            var oldRoot = Object("Old custom locomotive awaiting cleanup"); var oldCar = oldRoot.AddComponent(carType);
            var oldBinding = Activator.CreateInstance(bindingType, true); Set(oldBinding, "CarId", "replacement-guid");
            Call(Get(oldBinding, "Climate"), "Restore", capture); bindings.Add(oldCar, oldBinding);
            UnityEngine.Object.DestroyImmediate(oldRoot);
            var replacementRoot = Object("Replacement custom locomotive before cleanup tick");
            var replacement = replacementRoot.AddComponent(carType); Set(replacement, "_isLoco", (bool?)true);
            var legacyField = carType.GetField("carType", All); legacyField.SetValue(replacement, Enum.ToObject(legacyField.FieldType, -3003));
            var liveryField = carType.GetField("carLivery", All);
            var livery = ScriptableObject.CreateInstance(liveryField.FieldType);
            var parentField = livery.GetType().GetField("parentType", All);
            var parent = ScriptableObject.CreateInstance(parentField.FieldType);
            var logicField = carType.GetField("logicCar", All);
            try
            {
                Set(livery, "id", "runtime-cache-custom"); Set(parent, "id", "runtime-cache-custom");
                parentField.SetValue(livery, parent); liveryField.SetValue(replacement, livery);
                var logic = FormatterServices.GetUninitializedObject(logicField.FieldType);
                logicField.FieldType.GetField("carGuid", All).SetValue(logic, "replacement-guid"); logicField.SetValue(replacement, logic);
                var arguments = new object[] { replacement, true, 0f, false };
                Require((bool)Call(controller, "TryGetLevel", arguments), "Replacement custom locomotive was rejected");
                Require(!bindings.Contains(oldCar) && bindings.Contains(replacement), "Replacement did not prune its destroyed predecessor");
                var actual = Call(Get(bindings[replacement], "Climate"), "Capture");
                foreach (var field in capture.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                    Require(Equals(field.GetValue(capture), field.GetValue(actual)), "Replacement lost predecessor climate before periodic cleanup: " + field.Name);
            }
            finally
            {
                logicField.SetValue(replacement, null);
                UnityEngine.Object.DestroyImmediate(livery); UnityEngine.Object.DestroyImmediate(parent);
            }
        }
    }
}
