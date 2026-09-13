using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    // Real game component types in Unity; no active train simulation or network.
    public static class CabDoorVerification
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        public static void Run()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            var game = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            var managed = Path.Combine(game, "DerailValley_Data/Managed");
            var modDir = Path.Combine(root, "artifacts/build/DVSeasons");
            ResolveEventHandler resolve = (s, e) =>
            {
                foreach (var dir in new[] { modDir, managed, Path.Combine(managed, "UnityModManager") })
                {
                    var path = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            var objects = new List<GameObject>();
            Func<string, GameObject> create = name => { var go = new GameObject(name); go.SetActive(false); objects.Add(go); return go; };
            int code = 0;
            try
            {
                var gameAssembly = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
                var mod = Assembly.LoadFrom(Path.Combine(modDir, "DVSeasons.dll"));
                var carType = gameAssembly.GetType("TrainCar", true);
                var car = create("Cab door fixture").AddComponent(carType);
                var interior = create("Detached interior");
                var external = create("Detached external interactables");
                var persistent = create("Persistent interior");
                carType.GetProperty("loadedInterior").SetValue(car, interior, null);
                carType.GetProperty("loadedExternalInteractables").SetValue(car, external, null);
                carType.GetField("interior").SetValue(car, persistent.transform);
                var openableType = gameAssembly.GetType("DV.Openables.OpenableControl", true);
                var controlType = gameAssembly.GetType("DV.CabControls.NonVR.LeverNonVR", true);
                var baseType = gameAssembly.GetType("DV.CabControls.ControlImplBase", true);
                var valueField = baseType.GetField("<Value>k__BackingField", All);
                var door = external.AddComponent(openableType);
                var control = external.AddComponent(controlType);
                var detachedDoor = create("Serialized detached door").AddComponent(openableType);
                var detachedControl = detachedDoor.gameObject.AddComponent(controlType);
                var controllerType = gameAssembly.GetType("DV.Openables.DoorsAndWindowsController", true);
                var controller = persistent.AddComponent(controllerType);
                var entries = Array.CreateInstance(openableType, 1); entries.SetValue(detachedDoor, 0);
                controllerType.GetField("entries").SetValue(controller, entries);
                var winter = mod.GetType("DVSeasons.Mod.WinterWindowController", true);
                var cabType = winter.GetNestedType("Cab", All);
                var cab = Activator.CreateInstance(cabType, true); cabType.GetField("Car").SetValue(cab, car);
                var bind = winter.GetMethod("Bind", All);
                var isOpen = winter.GetMethod("AnythingOpen", All);
                bind.Invoke(null, new object[] { cab, 0f });
                Require(((Array)cabType.GetField("Openables").GetValue(cab)).Length == 2, "Detached openings were missed");
                Require(!(bool)isOpen.Invoke(null, new[] { cab }), "Closed cab reported open");
                valueField.SetValue(control, .5f);
                Require((bool)isOpen.Invoke(null, new[] { cab }), "Uninitialized exterior door ignored");
                valueField.SetValue(control, 0f); valueField.SetValue(detachedControl, .5f);
                Require((bool)isOpen.Invoke(null, new[] { cab }), "Serialized detached door ignored");
                openableType.GetField("closedAtZero").SetValue(detachedDoor, false);
                valueField.SetValue(detachedControl, 1f);
                Require(!(bool)isOpen.Invoke(null, new[] { cab }), "Reversed closed door reported open");
                valueField.SetValue(detachedControl, .5f);
                Require((bool)isOpen.Invoke(null, new[] { cab }), "Reversed open door ignored");
                bind.Invoke(null, new object[] { cab, 2f });
                var cached = cabType.GetField("Openables").GetValue(cab);
                for (int i = 3; i < 30; i++) bind.Invoke(null, new object[] { cab, (float)i });
                Require(ReferenceEquals(cached, cabType.GetField("Openables").GetValue(cab)), "Periodic full cabin rescan remains");
                carType.GetProperty("loadedExternalInteractables").SetValue(car, create("Reloaded external"), null);
                bind.Invoke(null, new object[] { cab, 29.1f });
                Require(!ReferenceEquals(cached, cabType.GetField("Openables").GetValue(cab)), "Streaming did not refresh bindings");
                Require(((Array)cabType.GetField("Openables").GetValue(cab)).Length == 1, "Old external door retained after streaming");
                Debug.Log("CAB_DOOR_BINDINGS_OK: external/persistent/detached controls, reversed values, stable cache and streamed replacement.");
            }
            catch (Exception e) { Debug.LogException(e); code = 1; }
            finally
            {
                foreach (var go in objects) if (go != null) UnityEngine.Object.DestroyImmediate(go);
                AppDomain.CurrentDomain.AssemblyResolve -= resolve;
            }
            EditorApplication.Exit(code);
        }
    }
}
