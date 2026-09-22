using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace DVSeasons.AssetBundleBuild
{
    // The candidate passed 203,163 checks but was slower in all four workloads.
    // Its byte-identical archived source now compiles only into Unity's Editor
    // test assembly (SnowVehicleFrustumAcceptanceExperiment.cs), never Game.
    // Calls use typed delegates, including timing: no reflection invocation or
    // allocations are timed. This is not a full submission or FPS benchmark.
    public static class SnowFrustumAcceptanceVerification
    {
        private delegate bool Inside(Plane[] primary, Plane[] secondary, Bounds bounds);
        private delegate bool Contains(Bounds envelope, Bounds part);
        private static Inside fullyInside, native;
        private static Contains contains;
        private static long checks, accepted, fallback;
        private static int sink;
        private const BindingFlags All = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            string runtime = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD") ?? Path.Combine(root, "artifacts/build/DVSeasons");
            string game = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME") ?? "F:/steam/steamapps/common/Derail Valley";
            ResolveEventHandler resolver = (sender, args) =>
            {
                foreach (var directory in new[] { runtime, Path.Combine(game, "DerailValley_Data/Managed"), Path.Combine(game, "DerailValley_Data/Managed/UnityModManager") })
                {
                    string path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            int result = 0;
            try
            {
                var assembly = Assembly.LoadFrom(Path.Combine(runtime, "DVSeasons.dll"));
                var helper = typeof(DVSeasons.Mod.SnowVehicleFrustumAcceptance);
                fullyInside = (Inside)Delegate.CreateDelegate(typeof(Inside), helper.GetMethod("FullyInside", All));
                contains = (Contains)Delegate.CreateDelegate(typeof(Contains), helper.GetMethod("Contains", All));
                native = (Inside)Delegate.CreateDelegate(typeof(Inside), assembly.GetType("DVSeasons.Mod.SnowCameraFrustum", true).GetMethod("Intersects", All));
                VerifyBoundaries();
                VerifyRandomCameras();
                Benchmark("mono fully inside", 0, false);
                Benchmark("mono boundary and detached", 1, false);
                Benchmark("mono outside conservative fallback", 2, false);
                Benchmark("stereo mixed", 1, true);
                Debug.Log("SNOW_FRUSTUM_ACCEPTANCE_OK checks=" + checks + " accepted=" + accepted + " fallback=" + fallback +
                    "; exact native visibility parity; no rejection shortcuts; per-part bounds remain live; timing excludes bounds reads and submission; not_game_FPS=true");
            }
            catch (Exception exception) { Debug.LogException(exception); result = 1; }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
            EditorApplication.Exit(result);
        }

        private static void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }

        private static Plane[] Box(Vector3 center, Vector3 extents)
        {
            return new[]
            {
                new Plane(Vector3.right, -(center.x - extents.x)), new Plane(Vector3.left, center.x + extents.x),
                new Plane(Vector3.up, -(center.y - extents.y)), new Plane(Vector3.down, center.y + extents.y),
                new Plane(Vector3.forward, -(center.z - extents.z)), new Plane(Vector3.back, center.z + extents.z)
            };
        }

        private static void Check(Plane[] primary, Plane[] secondary, Bounds envelope, Bounds part, string label)
        {
            bool ready = fullyInside(primary, secondary, envelope);
            bool shortcut = ready && contains(envelope, part);
            bool expected = native(primary, secondary, part);
            bool actual = shortcut || expected;
            Require(actual == expected, label + " accepted a native-rejected live bound; envelope=" + envelope + " part=" + part);
            if (shortcut) accepted++; else fallback++;
        }

        private static void VerifyBoundaries()
        {
            var planes = Box(Vector3.zero, Vector3.one * 10f);
            var envelope = new Bounds(Vector3.zero, Vector3.one * 8f);
            Require(fullyInside(planes, null, envelope), "Interior envelope not accepted");
            Check(planes, null, envelope, envelope, "Exact contained box");
            Require(!contains(envelope, new Bounds(new Vector3(.000001f, 0, 0), envelope.size)), "Escaped part admitted");
            Require(!fullyInside(planes, null, new Bounds(Vector3.zero, Vector3.one * 20f)), "Plane-touching envelope accepted");
            Require(!fullyInside(planes, null, new Bounds(Vector3.zero, Vector3.one * 20.00001f)), "Outside envelope accepted");
            Require(!fullyInside(new Plane[5], null, envelope), "Invalid plane count accepted");
            Require(!fullyInside(null, null, envelope), "Missing planes accepted");
            var invalid = new Bounds(Vector3.zero, Vector3.one); invalid.extents = new Vector3(-1, 1, 1);
            Require(!fullyInside(planes, null, invalid), "Negative envelope extent accepted");
            Require(!contains(envelope, invalid), "Negative live extent accepted");
            foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                invalid = new Bounds(new Vector3(value, 0, 0), Vector3.one);
                Require(!fullyInside(planes, null, invalid), "Nonfinite envelope accepted");
                Require(!contains(envelope, invalid), "Nonfinite live bound accepted");
                var malformed = (Plane[])planes.Clone(); malformed[2] = new Plane(Vector3.up, value);
                Require(!fullyInside(malformed, null, envelope), "Nonfinite plane accepted");
            }
            // An envelope can touch both eyes without lying entirely in either.
            // No artificial combined set of planes may classify it as accepted.
            var left = Box(new Vector3(-6, 0, 0), new Vector3(4, 10, 10));
            var right = Box(new Vector3(6, 0, 0), new Vector3(4, 10, 10));
            Require(!fullyInside(left, right, new Bounds(Vector3.zero, new Vector3(18, 2, 2))), "Disconnected eye union accepted");
            var rightEnvelope = new Bounds(new Vector3(6, 0, 0), Vector3.one * 4);
            Require(fullyInside(left, right, rightEnvelope), "Secondary eye interior not accepted");
            Check(left, right, rightEnvelope, rightEnvelope, "Secondary eye");

            var random = new System.Random(71983);
            for (int scene = 0; scene < 40; scene++)
            {
                float origin = scene % 4 == 0 ? 1000000f : scene % 4 == 1 ? -5000f : 0f;
                var shift = new Vector3(origin, -origin * .25f, origin * .75f);
                planes = Box(shift, Vector3.one * 10);
                for (int sample = 0; sample < 400; sample++)
                {
                    float side = sample % 2 == 0 ? 1 : -1;
                    float inset = (float)Math.Pow(10, -7 + random.NextDouble() * 8);
                    envelope = new Bounds(shift + new Vector3(side * (8f - inset), 0, 0), Vector3.one * 4);
                    var part = new Bounds(envelope.center + new Vector3(Range(random, -.1f, .1f), 0, 0), envelope.size * Range(random, .01f, 1.05f));
                    Check(planes, null, envelope, part, "Float-boundary/origin case");
                }
            }
        }

        private static float Range(System.Random random, float minimum, float maximum)
        { return minimum + (float)random.NextDouble() * (maximum - minimum); }

        private static void VerifyRandomCameras()
        {
            var random = new System.Random(532987);
            var cameraObject = new GameObject("Frustum acceptance verification camera");
            var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
            var primary = new Plane[6]; var secondary = new Plane[6];
            try
            {
                for (int scene = 0; scene < 72; scene++)
                {
                    camera.transform.position = new Vector3(Range(random, -100000, 100000), Range(random, -1000, 1000), Range(random, -100000, 100000));
                    camera.transform.rotation = Quaternion.Euler(Range(random, -80, 80), Range(random, -180, 180), Range(random, -180, 180));
                    camera.orthographic = scene % 3 == 0;
                    camera.orthographicSize = Range(random, 2, 500);
                    camera.fieldOfView = Range(random, 5, 150);
                    camera.aspect = Range(random, .5f, 3.5f);
                    camera.nearClipPlane = scene % 4 == 0 ? .001f : Range(random, .05f, 3f);
                    camera.farClipPlane = Range(random, 100, 40000);
                    Matrix4x4 projection = camera.projectionMatrix;
                    projection.m02 += Range(random, -.3f, .3f);
                    projection.m12 += Range(random, -.2f, .2f);
                    Matrix4x4 view = camera.worldToCameraMatrix;
                    GeometryUtility.CalculateFrustumPlanes(projection * view, primary);
                    var otherProjection = projection; otherProjection.m02 += .4f;
                    var otherView = Matrix4x4.Translate(new Vector3(.08f, 0, 0)) * view;
                    GeometryUtility.CalculateFrustumPlanes(otherProjection * otherView, secondary);
                    Plane[] secondEye = scene % 2 == 0 ? null : secondary;
                    for (int car = 0; car < 100; car++)
                    {
                        float distance = Range(random, .01f, camera.farClipPlane * 1.2f);
                        float spread = camera.orthographic ? camera.orthographicSize : distance * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f);
                        var position = camera.transform.TransformPoint(new Vector3(Range(random, -spread * camera.aspect, spread * camera.aspect), Range(random, -spread, spread), distance));
                        var envelope = new Bounds(position, new Vector3(Range(random, .01f, 10), Range(random, .01f, 6), Range(random, .01f, 30)));
                        for (int partIndex = 0; partIndex < 24; partIndex++)
                        {
                            float escape = partIndex % 4 == 0 ? 2f : .2f;
                            var offset = Vector3.Scale(envelope.extents, new Vector3(Range(random, -escape, escape), Range(random, -escape, escape), Range(random, -escape, escape)));
                            var part = new Bounds(envelope.center + offset, envelope.size * Range(random, 0, partIndex % 4 == 0 ? 1.2f : .7f));
                            Check(primary, secondEye, envelope, part, "Perspective/orthographic/asymmetric stereo fuzz");
                        }
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(cameraObject); }
            Require(accepted > 10000, "Fuzz failed to exercise inside acceptance");
            Require(fallback > 10000, "Fuzz failed to exercise boundary/detached fallback");
        }

        private sealed class Car
        {
            internal Bounds Envelope;
            internal Bounds[] Parts;
        }

        private static int Traverse(Car[] cars, Plane[] primary, Plane[] secondary, bool optimized)
        {
            int visible = 0;
            foreach (var car in cars)
            {
                bool inside = optimized && fullyInside(primary, secondary, car.Envelope);
                foreach (var part in car.Parts)
                    if ((inside && contains(car.Envelope, part)) || native(primary, secondary, part)) visible++;
            }
            return visible;
        }

        private static void Benchmark(string label, int mode, bool stereo)
        {
            var primary = Box(Vector3.zero, new Vector3(100, 80, 100));
            var secondary = stereo ? Box(new Vector3(3, 0, 0), new Vector3(100, 80, 100)) : null;
            var cars = new Car[224]; var random = new System.Random(917);
            int acceptedParts = 0, count = 0;
            for (int index = 0; index < cars.Length; index++)
            {
                float x = mode == 2 ? 102f : mode == 1 && index % 2 == 0 ? 97.5f : Range(random, -70, 70);
                var car = new Car { Envelope = new Bounds(new Vector3(x, Range(random, -30, 30), Range(random, -70, 70)), new Vector3(6, 6, 18)), Parts = new Bounds[16] };
                bool ready = fullyInside(primary, secondary, car.Envelope);
                for (int part = 0; part < car.Parts.Length; part++)
                {
                    var offset = new Vector3(Range(random, -1, 1), Range(random, -1, 1), Range(random, -5, 5));
                    if (mode == 1 && part % 7 == 0) offset.x += 8;
                    car.Parts[part] = new Bounds(car.Envelope.center + offset, new Vector3(2, 2, 3));
                    Check(primary, secondary, car.Envelope, car.Parts[part], label);
                    if (ready && contains(car.Envelope, car.Parts[part])) acceptedParts++;
                    count++;
                }
                cars[index] = car;
            }
            Require(Traverse(cars, primary, secondary, false) == Traverse(cars, primary, secondary, true), "Benchmark visible count changed");
            for (int warm = 0; warm < 12; warm++) { sink ^= Traverse(cars, primary, secondary, false); sink ^= Traverse(cars, primary, secondary, true); }
            const int trials = 7, samples = 64;
            var baseline = new List<double>(); var candidate = new List<double>();
            for (int trial = 0; trial < trials; trial++)
                for (int pass = 0; pass < 2; pass++)
                {
                    bool optimized = ((trial + pass) & 1) != 0;
                    var watch = Stopwatch.StartNew();
                    for (int sample = 0; sample < samples; sample++) sink ^= Traverse(cars, primary, secondary, optimized);
                    watch.Stop();
                    (optimized ? candidate : baseline).Add(watch.Elapsed.TotalMilliseconds / samples);
                }
            baseline.Sort(); candidate.Sort();
            Debug.Log("SNOW_FRUSTUM_ACCEPTANCE_BENCH " + label + " baseline_median_ms=" + baseline[3].ToString("F4", CultureInfo.InvariantCulture) +
                " candidate_median_ms=" + candidate[3].ToString("F4", CultureInfo.InvariantCulture) + " cars=" + cars.Length + " parts=" + count +
                " native_tests_avoided=" + acceptedParts + "; 7 alternating trials x64 traversals; native bounds reads and submission excluded; not_game_FPS=true");
        }
    }
}
