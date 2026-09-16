using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class TurntableSnowVerification
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static;
        private static object Get(object owner, string name) { return owner.GetType().GetField(name, All).GetValue(owner); }
        private static void Set(object owner, string name, object value) { owner.GetType().GetField(name, All).SetValue(owner, value); }
        private static object Call(object owner, string name, params object[] args)
        { return owner.GetType().GetMethod(name, All).Invoke(owner, args); }
        private static int Count(object owner, string name)
        { return (int)owner.GetType().GetProperty(name, All).GetValue(owner, null); }
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            string modPath = Path.Combine(root, "artifacts/build/DVSeasons");
            string output = Path.Combine(root, "artifacts/verification/0.3.14-turntable");
            string game = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            ResolveEventHandler resolver = (sender, args) =>
            {
                foreach (var directory in new[] { modPath, Path.Combine(game, "DerailValley_Data/Managed"),
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
                Directory.CreateDirectory(output);
                Verify(Assembly.LoadFrom(Path.Combine(modPath, "DVSeasons.dll")), modPath, game, output);
                Debug.Log("TURNTABLE_SNOW_OK: exact native moving root, local asymmetric GPU snow mask, rotation without recapture, static pit exposure and destroyed-root cleanup.");
            }
            catch (Exception exception) { Debug.LogException(exception); code = 1; }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
            EditorApplication.Exit(code);
        }

        private static void Verify(Assembly mod, string modPath, string game, string output)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = Color.gray;
            RenderSettings.fog = false;
            QualitySettings.antiAliasing = 0;
            var camera = new GameObject("Turntable snow camera") { tag = "MainCamera" }.AddComponent<Camera>();
            camera.renderingPath = RenderingPath.DeferredShading;
            camera.fieldOfView = 48;
            camera.allowHDR = true; camera.allowMSAA = false;
            camera.farClipPlane = 100;
            camera.targetTexture = new RenderTexture(512, 384, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            camera.targetTexture.Create();
            var albedo = new RenderTexture(512, 384, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            albedo.Create();
            var probe = new CommandBuffer { name = "Turntable snow verification albedo" };
            probe.Blit(BuiltinRenderTextureType.GBuffer0, albedo);
            camera.AddCommandBuffer(CameraEvent.AfterLighting, probe);
            var material = new Material(Shader.Find("Standard")) { color = new Color(.09f, .09f, .09f) };
            material.SetFloat("_Glossiness", 0);
            var table = new GameObject("Custom map stationary turntable anchor");
            table.SetActive(false);
            var visuals = new GameObject("Custom map moving bridge").transform;
            visuals.SetParent(table.transform, false); visuals.localPosition = new Vector3(0, 1.5f, 0);
            var bridge = Cube("Turntable deck", visuals, Vector3.zero, new Vector3(2, .3f, 12), material);
            var pit = Cube("Stationary pit floor", table.transform, new Vector3(0, -.25f, 0), new Vector3(16, .5f, 16), material);

            // Keep the fixture's native rail simulation dormant; only discovery
            // and its real public visuals reference are under test here.
            var gameAssembly = Assembly.LoadFrom(Path.Combine(game, "DerailValley_Data/Managed/Assembly-CSharp.dll"));
            var track = table.AddComponent(gameAssembly.GetType("TurntableRailTrack", true));
            var railType = Assembly.LoadFrom(Path.Combine(game, "DerailValley_Data/Managed/DV.RailTrack.dll")).GetType("RailTrack", true);
            var nativeRail = table.GetComponent(railType) ?? table.AddComponent(railType);
            Require(nativeRail != null, "Native RailTrack fixture component was not created");
            var initialized = railType.GetField("initialized", All);
            Require(initialized != null, "Native RailTrack initialization guard was not found in " + railType.Assembly.FullName);
            initialized.SetValue(nativeRail, true);
            track.GetType().GetField("visuals").SetValue(track, visuals);
            table.SetActive(true);

            var source = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.TurntableSnowSource", true), true);
            Func<IEnumerable<Transform>> discover = () => (IEnumerable<Transform>)Call(source, "GetRoots");
            var discovered = new List<Transform>(discover());
            Require(discovered.Count == 1 && discovered[0] == visuals,
                "Native turntable discovery did not select exactly TurntableRailTrack.visuals");
            var repositoryType = mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository", true);
            var repository = Activator.CreateInstance(repositoryType, new object[] { modPath });
            var bundle = AssetBundle.LoadFromFile(Path.Combine(modPath, "AssetBundles/dvseasons_dv99"));
            repositoryType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository, new object[] { bundle });
            var controller = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.ProceduralSnowController", true), new[] { repository });
            Call(controller, "SetMovingSurfaceDiscovery", discover);
            Call(controller, "SetVehicleDiscovery", new Func<IEnumerable<Component>>(() => new Component[0]));
            object registry = Get(controller, "vehicles");
            Texture2D mask = null;
            try
            {
                PositionCamera(camera, visuals);
                Call(controller, "SetWeather", 1f);
                for (int i = 0; i < 8; i++) { Call(controller, "Apply", 1f, true); camera.Render(); }
                Require((bool)controller.GetType().GetProperty("IsActive").GetValue(controller, null),
                    "Procedural snow did not bind to fixture camera (path=" + camera.actualRenderingPath + ")");
                var entries = (IList)Get(registry, "vehicles");
                Require(entries.Count == 1 && (Transform)Get(entries[0], "Root") == visuals,
                    "Snow cache registered the stationary turntable anchor or pit (entries=" + entries.Count + ")");
                Require((bool)Get(entries[0], "SnowReady"), "Moving bridge snow mask was not initialized");
                Call(registry, "HideForStaticCapture", new Vector4(0, 0, 128, 0));
                try
                {
                    Require(bridge.forceRenderingOff, "Bridge was baked into static world exposure");
                    Require(!pit.forceRenderingOff, "Stationary pit was excluded with the bridge");
                }
                finally { Call(registry, "RestoreAfterStaticCapture"); }
                Require(!bridge.forceRenderingOff && !pit.forceRenderingOff, "Static capture did not restore renderers");

                // A half-bare deck makes movement observable even at full winter
                // coverage. An all-white fixture would miss a world-space mask.
                int slot = (int)Get(entries[0], "Slot");
                mask = new Texture2D(256, 256, TextureFormat.RHalf, false, true);
                var pixels = new Color[256 * 256];
                for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
                    pixels[y * 256 + x] = new Color(y < 128 ? 0 : 1, 0, 0, 1);
                mask.SetPixels(pixels); mask.Apply(false, false);
                var snow = (RenderTexture)Get(registry, "snow");
                Graphics.CopyTexture(mask, 0, 0, snow, slot, 0);
                Call(controller, "SetWeather", 0f);
                var heights = ReadSlice((RenderTexture)Get(registry, "heights"), slot);
                var snowBefore = ReadSlice(snow, slot);
                int captures = Count(registry, "CaptureCount");
                int exposures = Count(controller, "ExposureCaptureCount");
                var localPoints = new[] { new Vector3(-.45f, .15f, -3), new Vector3(.45f, .15f, -3),
                    new Vector3(-.45f, .15f, 3), new Vector3(.45f, .15f, 3) };
                var before = Render(controller, camera, albedo, Path.Combine(output, "bridge-before.png"));
                var values = Samples(before, camera, visuals, localPoints);
                Require(Mathf.Abs(values[0] - values[2]) > .25f,
                    "Asymmetric deck snow mask was not visible: " + values[0] + ", " + values[2]);
                UnityEngine.Object.DestroyImmediate(before);

                visuals.localRotation = Quaternion.Euler(0, 90, 0);
                var rotated = Render(controller, camera, albedo, Path.Combine(output, "bridge-rotated.png"));
                Compare(values, Samples(rotated, camera, visuals, localPoints), "fixed camera after bridge rotation");
                float clearedPit = Sample(rotated, camera, new Vector3(0, 0, 4));
                Require(clearedPit > .55f, "Former bridge footprint left a static snow hole in the pit: " + clearedPit);
                UnityEngine.Object.DestroyImmediate(rotated);
                PositionCamera(camera, visuals);
                var equivalent = Render(controller, camera, albedo, Path.Combine(output, "bridge-equivalent-camera.png"));
                Compare(values, Samples(equivalent, camera, visuals, localPoints), "equivalent local camera after rotation");
                UnityEngine.Object.DestroyImmediate(equivalent);
                Require(Count(registry, "CaptureCount") == captures, "Bridge rotation rebuilt its local GPU height map");
                Require(Count(controller, "ExposureCaptureCount") == exposures, "Bridge rotation rebuilt static exposure");
                Require(Equal(heights, ReadSlice((RenderTexture)Get(registry, "heights"), slot)), "Local height texture changed during rotation");
                Require(Equal(snowBefore, ReadSlice(snow, slot)), "Dry bridge rotation changed its accumulated snow mask");

                UnityEngine.Object.DestroyImmediate(visuals.gameObject);
                Require(new List<Transform>(discover()).Count == 0, "Destroyed bridge retained by discovery cache");
                Set(registry, "nextScan", 0f); Call(registry, "Update", camera);
                Require(((IList)Get(registry, "vehicles")).Count == 0 && ((IDictionary)Get(registry, "staticVehicles")).Count == 0,
                    "Destroyed bridge retained its snow slot or static exclusion");
                Debug.Log("Turntable samples before rotation: " + string.Join(", ", values) +
                    "; exposed pit=" + clearedPit + "; local captures=" + captures + "; static captures=" + exposures);
            }
            finally
            {
                ((IDisposable)source).Dispose();
                ((IDisposable)controller).Dispose(); ((IDisposable)repository).Dispose();
                camera.RemoveCommandBuffer(CameraEvent.AfterLighting, probe); probe.Dispose();
                var target = camera.targetTexture; camera.targetTexture = null;
                foreach (var item in new UnityEngine.Object[] { mask, table, material, target, albedo, camera.gameObject })
                    if (item != null) UnityEngine.Object.DestroyImmediate(item);
            }
        }

        private static Renderer Cube(string name, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.name = name;
            cube.transform.SetParent(parent, false); cube.transform.localPosition = position; cube.transform.localScale = scale;
            var renderer = cube.GetComponent<Renderer>(); renderer.sharedMaterial = material;
            return renderer;
        }

        private static void PositionCamera(Camera camera, Transform bridge)
        {
            camera.transform.position = bridge.position + Vector3.up * 20;
            camera.transform.LookAt(bridge.position, bridge.forward);
        }

        private static Texture2D Render(object controller, Camera camera, RenderTexture albedo, string path)
        {
            Call(controller, "Apply", 1f, true); camera.Render();
            var previous = RenderTexture.active; RenderTexture.active = albedo;
            var read = new Texture2D(albedo.width, albedo.height, TextureFormat.RGBAFloat, false, true);
            read.ReadPixels(new Rect(0, 0, albedo.width, albedo.height), 0, 0); read.Apply();
            var png = new Texture2D(albedo.width, albedo.height, TextureFormat.RGB24, false);
            var colors = read.GetPixels(); for (int i = 0; i < colors.Length; i++) colors[i] = colors[i].gamma;
            png.SetPixels(colors); png.Apply(); File.WriteAllBytes(path, png.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(png); RenderTexture.active = previous;
            return read;
        }

        private static float Sample(Texture2D texture, Camera camera, Vector3 point)
        {
            var uv = camera.WorldToViewportPoint(point);
            return texture.GetPixel(Mathf.RoundToInt(uv.x * (texture.width - 1)), Mathf.RoundToInt(uv.y * (texture.height - 1))).r;
        }

        private static float[] Samples(Texture2D texture, Camera camera, Transform bridge, Vector3[] points)
        {
            var samples = new float[points.Length];
            for (int i = 0; i < points.Length; i++) samples[i] = Sample(texture, camera, bridge.TransformPoint(points[i]));
            return samples;
        }

        private static void Compare(float[] expected, float[] actual, string label)
        {
            for (int i = 0; i < expected.Length; i++) Require(Mathf.Abs(expected[i] - actual[i]) < .08f,
                "Snow detached from bridge at sample " + i + " (" + label + "): " + expected[i] + " -> " + actual[i]);
        }

        private static byte[] ReadSlice(RenderTexture texture, int slot)
        {
            var request = AsyncGPUReadback.Request(texture, 0, 0, 256, 0, 256, slot, 1); request.WaitForCompletion();
            Require(!request.hasError, "GPU texture readback failed");
            var data = request.GetData<byte>(); var bytes = new byte[data.Length]; data.CopyTo(bytes); return bytes;
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
