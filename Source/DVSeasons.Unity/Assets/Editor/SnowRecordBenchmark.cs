using System;
using System.Collections;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DVSeasons.AssetBundleBuild
{
    // CPU command-construction microbenchmark. This deliberately does not measure
    // camera rendering, GPU execution, game FPS, discovery, or height-map capture.
    public static class SnowRecordBenchmark
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const int Cars = 80, PartsPerCar = 16, Warmup = 96, Samples = 512;
        [Serializable]
        private sealed class Result
        {
            public string label, runtimePath, runtimeSha256, bundleSha256, unity, operatingSystem, processor, graphicsDevice;
            public int cars, partsPerCar, warmup, samples, minimumDraws, maximumDraws, minimumCommandBytes, maximumCommandBytes;
            public double medianMilliseconds, p95Milliseconds, meanMilliseconds, maximumMilliseconds;
            public int gen0Collections, gen1Collections, gen2Collections;
            public string scope;
            public double[] milliseconds;
        }
        private static object Call(object instance, string method, params object[] args)
        { return instance.GetType().GetMethod(method, All).Invoke(instance, args); }
        private static object Get(object instance, string name)
        { return instance.GetType().GetField(name, All).GetValue(instance); }
        private static void Set(object instance, string name, object value)
        { instance.GetType().GetField(name, All).SetValue(instance, value); }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            string runtime = Environment.GetEnvironmentVariable("DVSEASONS_BENCH_RUNTIME");
            if (string.IsNullOrEmpty(runtime)) runtime = Path.Combine(root, "artifacts/build/DVSeasons");
            string label = Environment.GetEnvironmentVariable("DVSEASONS_BENCH_LABEL") ?? "current";
            string game = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            Require(!string.IsNullOrEmpty(game), "Set DVSEASONS_VERIFY_GAME to the installed game directory.");
            ResolveEventHandler resolver = (sender, args) =>
            {
                foreach (string directory in new[] { runtime, Path.Combine(game, "DerailValley_Data/Managed"),
                    Path.Combine(game, "DerailValley_Data/Managed/UnityModManager") })
                {
                    string path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            int code = 0;
            try
            {
                var result = Measure(Assembly.LoadFrom(Path.Combine(runtime, "DVSeasons.dll")), runtime, label);
                string output = Path.Combine(root, "artifacts/verification/snow-record-" + label + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                File.WriteAllText(output, JsonUtility.ToJson(result, true));
                Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "SNOW_RECORD_BENCHMARK_OK label={0} cars={1} parts={2} median={3:F4}ms p95={4:F4}ms mean={5:F4}ms draws={6}..{7} commandBytes={8}..{9} output={10}",
                    label, Cars, PartsPerCar, result.medianMilliseconds, result.p95Milliseconds,
                    result.meanMilliseconds, result.minimumDraws, result.maximumDraws,
                    result.minimumCommandBytes, result.maximumCommandBytes, output));
            }
            catch (Exception exception) { Debug.LogException(exception); code = 1; }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
            EditorApplication.Exit(code);
        }

        private static Result Measure(Assembly mod, string runtime, string label)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            QualitySettings.antiAliasing = 0; QualitySettings.lodBias = 1;
            var camera = new GameObject("CPU record benchmark camera").AddComponent<Camera>();
            camera.enabled = false; camera.orthographic = true; camera.orthographicSize = 65;
            camera.aspect = 16f / 9f; camera.nearClipPlane = .1f; camera.farClipPlane = 400;
            camera.transform.position = new Vector3(36, 160, 42);
            camera.transform.LookAt(new Vector3(36, 0, 42), Vector3.forward);
            camera.targetTexture = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGBHalf);
            var prototype = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = prototype.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(prototype);
            var material = new Material(Shader.Find("Standard"));
            var registry = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SnowVehicleRegistry", true));
            var rails = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.RailSnowTracks", true));
            var repoType = mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository", true);
            var repository = Activator.CreateInstance(repoType, new object[] { runtime });
            var bundle = AssetBundle.LoadFromFile(Path.Combine(runtime, "AssetBundles/dvseasons_dv99"));
            Require(bundle != null, "Unable to load benchmark shader bundle.");
            repoType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository, new object[] { bundle });
            var command = new CommandBuffer { name = "Snow Record CPU benchmark" };
            try
            {
                for (int car = 0; car < Cars; car++)
                {
                    var root = new GameObject("Benchmark car " + car).transform;
                    root.position = new Vector3((car % 10) * 8, 0, (car / 10) * 12);
                    var interior = new GameObject("interior").transform; interior.SetParent(root, false);
                    var renderers = new Renderer[PartsPerCar];
                    for (int part = 0; part < PartsPerCar; part++)
                    {
                        var node = new GameObject("Part " + part);
                        node.transform.SetParent(part < 12 ? root : interior, false);
                        node.transform.localPosition = new Vector3((part % 4 - 1.5f) * .8f, 1 + (part % 3) * .25f, (part / 4 - 1.5f) * 2);
                        node.AddComponent<MeshFilter>().sharedMesh = mesh;
                        var renderer = node.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                        renderers[part] = renderer;
                    }
                    var lod = root.gameObject.AddComponent<LODGroup>();
                    lod.SetLODs(new[] { new LOD(0, renderers) }); lod.RecalculateBounds();
                    Call(registry, "Register", root, interior, null);
                }
                Require((bool)Call(registry, "Initialize", repository), "Registry initialization failed.");
                var vehicles = (IList)Get(registry, "vehicles");
                Require(vehicles.Count == Cars, "Fixture lost registered cars.");
                foreach (object vehicle in vehicles)
                {
                    Call(registry, "RefreshParts", vehicle);
                    Call(registry, "CaptureHeight", vehicle);
                    Set(vehicle, "SnowReady", true);
                    Set(vehicle, "PartsPending", false);
                    Require(((IList)Get(vehicle, "Parts")).Count == PartsPerCar, "Unexpected prepared part count.");
                }
                // Compile one closed delegate before warm-up: no reflection Invoke
                // or argument-array allocation is part of the timed loop.
                var recordMethod = registry.GetType().GetMethod("Record", All);
                var record = Expression.Lambda<Action>(Expression.Call(Expression.Constant(registry), recordMethod,
                    Expression.Constant(command), Expression.Constant(camera), Expression.Constant(rails), Expression.Constant(false))).Compile();
                var drawGetter = Expression.Lambda<Func<int>>(Expression.Property(Expression.Constant(registry), "FrameDrawCount")).Compile();
                for (int i = 0; i < Warmup; i++) { command.Clear(); record(); }
                Require(drawGetter() == Cars * PartsPerCar, "All 1280 prepared parts must remain visible and drawn.");
                var result = new Result {
                    label = label, runtimePath = runtime, runtimeSha256 = Hash(Path.Combine(runtime, "DVSeasons.dll")),
                    bundleSha256 = Hash(Path.Combine(runtime, "AssetBundles/dvseasons_dv99")), unity = Application.unityVersion,
                    operatingSystem = SystemInfo.operatingSystem, processor = SystemInfo.processorType, graphicsDevice = SystemInfo.graphicsDeviceName,
                    cars = Cars, partsPerCar = PartsPerCar, warmup = Warmup, samples = Samples,
                    minimumDraws = int.MaxValue, minimumCommandBytes = int.MaxValue, milliseconds = new double[Samples],
                    scope = "CPU SnowVehicleRegistry.Record only. CommandBuffer.Clear, fixture setup, discovery, height capture, GPU execution and rendering excluded. All 80 cars remain visible: 12 exterior + 4 interior opaque parts each, one LOD group per car. No game FPS inference."
                };
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                for (int i = 0; i < Samples; i++)
                {
                    command.Clear();
                    long start = Stopwatch.GetTimestamp(); record(); long elapsed = Stopwatch.GetTimestamp() - start;
                    double ms = elapsed * 1000d / Stopwatch.Frequency; result.milliseconds[i] = ms;
                    result.meanMilliseconds += ms; result.maximumMilliseconds = Math.Max(result.maximumMilliseconds, ms);
                    int draws = drawGetter(), bytes = command.sizeInBytes;
                    result.minimumDraws = Math.Min(result.minimumDraws, draws); result.maximumDraws = Math.Max(result.maximumDraws, draws);
                    result.minimumCommandBytes = Math.Min(result.minimumCommandBytes, bytes); result.maximumCommandBytes = Math.Max(result.maximumCommandBytes, bytes);
                }
                result.gen0Collections = GC.CollectionCount(0) - gc0; result.gen1Collections = GC.CollectionCount(1) - gc1; result.gen2Collections = GC.CollectionCount(2) - gc2;
                var sorted = (double[])result.milliseconds.Clone(); Array.Sort(sorted);
                result.medianMilliseconds = (sorted[Samples / 2 - 1] + sorted[Samples / 2]) * .5;
                result.p95Milliseconds = sorted[(int)Math.Ceiling(Samples * .95) - 1]; result.meanMilliseconds /= Samples;
                Require(result.minimumDraws == Cars * PartsPerCar && result.maximumDraws == Cars * PartsPerCar, "Draw count changed during benchmark.");
                return result;
            }
            finally
            {
                command.Dispose(); Call(registry, "Dispose"); Call(rails, "Dispose"); bundle.Unload(true);
                UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(camera.targetTexture);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }
        private static string Hash(string path)
        { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    }
}
