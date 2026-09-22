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
    public static class JunctionSnowVerification
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static object Get(object o, string n) { return o.GetType().GetField(n, All).GetValue(o); }
        private static object Call(object o, string n, params object[] args) { return o.GetType().GetMethod(n, All).Invoke(o, args); }
        private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            string modPath = Path.Combine(root, "artifacts/build/DVSeasons");
            string game = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            ResolveEventHandler resolver = (sender, args) =>
            {
                foreach (var directory in new[] { modPath, Path.Combine(game, "DerailValley_Data/Managed"),
                    Path.Combine(game, "DerailValley_Data/Managed/UnityModManager") })
                {
                    var path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            int code = 0;
            try
            {
                Verify(Assembly.LoadFrom(Path.Combine(modPath, "DVSeasons.dll")), modPath, game);
                Debug.Log("JUNCTION_SNOW_OK: native VisualSwitch animator binding, both blade poses, partial/full snow, recapture, floating origin, stationary rails and no vehicle cache slots.");
            }
            catch (Exception e) { Debug.LogException(e); code = 1; }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
            EditorApplication.Exit(code);
        }

        private static void Verify(Assembly mod, string modPath, string game)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Color.gray; RenderSettings.fog = false;
            QualitySettings.antiAliasing = 0;
            var camera = new GameObject("Snow junction camera") { tag = "MainCamera" }.AddComponent<Camera>();
            camera.renderingPath = RenderingPath.DeferredShading; camera.fieldOfView = 35;
            camera.allowHDR = true; camera.allowMSAA = false; camera.farClipPlane = 100;
            camera.targetTexture = new RenderTexture(1024, 768, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            camera.targetTexture.Create();
            var albedo = new RenderTexture(1024, 768, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear); albedo.Create();
            var probe = new CommandBuffer { name = "Junction snow albedo" };
            probe.Blit(BuiltinRenderTextureType.GBuffer0, albedo); camera.AddCommandBuffer(CameraEvent.AfterLighting, probe);
            var material = new Material(Shader.Find("Standard")) { color = new Color(.08f, .08f, .08f) }; material.SetFloat("_Glossiness", 0);
            var junction = new GameObject("Native junction fixture");
            var animator = junction.AddComponent<Animator>(); animator.enabled = false;
            var native = Assembly.LoadFrom(Path.Combine(game, "DerailValley_Data/Managed/Assembly-CSharp.dll"));
            var owner = junction.AddComponent(native.GetType("VisualSwitch", true));
            ((Behaviour)owner).enabled = false; owner.GetType().GetField("animator").SetValue(owner, animator);
            var graphical = new GameObject("Graphical").transform; graphical.SetParent(junction.transform, false);
            var blade = Cube("Blades", graphical, new Vector3(-.75f, .15f, 0), new Vector3(.32f, .3f, 12), material);
            var mesh = UnityEngine.Object.Instantiate(blade.GetComponent<MeshFilter>().sharedMesh); mesh.name = "rails_moving";
            blade.GetComponent<MeshFilter>().sharedMesh = mesh;
            var fixedRail = Cube("Fixed rail", graphical, new Vector3(.75f, .15f, 0), new Vector3(.32f, .3f, 12), material);
            Cube("Ground", junction.transform, new Vector3(0, -.1f, 0), new Vector3(24, .2f, 24), material);
            // A same-named mesh without the actual native switch is not a blade.
            var decoy = Cube("Unrelated object", null, new Vector3(100, 0, 0), Vector3.one, material);
            decoy.GetComponent<MeshFilter>().sharedMesh = mesh;
            var repoType = mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository", true);
            var repository = Activator.CreateInstance(repoType, new object[] { modPath });
            repoType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,
                new object[] { AssetBundle.LoadFromFile(Path.Combine(modPath, "AssetBundles/dvseasons_dv99")) });
            var controller = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.ProceduralSnowController", true), new[] { repository });
            Call(controller, "SetVehicleDiscovery", new Func<IEnumerable<Component>>(() => new Component[0]));
            var registry = Get(controller, "vehicles"); var source = Get(registry, "junctions");
            try
            {
                camera.transform.position = new Vector3(0, 17, 0); camera.transform.LookAt(Vector3.zero, Vector3.forward);
                Call(controller, "SetWeather", 0f);
                for (int i = 0; i < 5; i++) Render(controller, camera, albedo, 1f, blade, fixedRail);
                Require((int)source.GetType().GetProperty("Count").GetValue(source, null) == 1, "Discovery did not select exactly the native switch blade mesh");
                Require(((IList)Get(registry, "vehicles")).Count == 0, "Junctions consumed limited vehicle cache slots");
                var before = Render(controller, camera, albedo, 1f, blade, fixedRail);
                Require(before[0] > .4f && before[1] > .4f, "Fixture rails were not visibly snow covered: " + string.Join(",", before));
                // Exaggerate native 0.3 degree motion to make a stale world map
                // clearly fail; the actual shader does not assume its magnitude.
                blade.transform.localPosition += Vector3.left * .35f;
                blade.transform.localRotation = Quaternion.Euler(0, 3, 0);
                var switched = Render(controller, camera, albedo, 1f, blade, fixedRail);
                Compare(before, switched, .07f, "snow at switched pose");
                int captures = (int)controller.GetType().GetProperty("ExposureCaptureCount").GetValue(controller, null);
                var partial = Render(controller, camera, albedo, .48f, blade, fixedRail);
                blade.transform.localPosition -= Vector3.left * .35f; blade.transform.localRotation = Quaternion.identity;
                var returned = Render(controller, camera, albedo, .48f, blade, fixedRail);
                Compare(partial, returned, .10f, "partial snow moving back");
                Require((int)controller.GetType().GetProperty("ExposureCaptureCount").GetValue(controller, null) == captures,
                    "Switching blades rebuilt the world height maps");
                blade.transform.localPosition += Vector3.left * .35f; blade.transform.localRotation = Quaternion.Euler(0, 3, 0);
                Call(controller, "InvalidateGeometry");
                for (int refresh = 0; refresh < 3; refresh++)
                {
                    // Keep valid maps until replaced, just as a streamed scene
                    // does. Clearing Ready on every map simulates a new session,
                    // which intentionally creates its near/far maps over two frames.
                    controller.GetType().GetField("nextProxyRefresh", All).SetValue(controller, 0f);
                    var recaptured = Render(controller, camera, albedo, 1f, blade, fixedRail);
                    Compare(switched, recaptured, .07f, "recaptured switched pose frame " + refresh);
                }
                Require((int)controller.GetType().GetProperty("ExposureCaptureCount").GetValue(controller, null) == captures + 3,
                    "Forced geometry invalidation did not recapture each exposure map");
                Require(Mathf.Abs(blade.transform.localPosition.x + 1.1f) < .001f &&
                    Quaternion.Angle(blade.transform.localRotation, Quaternion.Euler(0, 3, 0)) < .001f,
                    "Static capture failed to restore current blade pose");
                var tracks = Get(controller, "RailTracks");
                Call(tracks, "WheelAt", 101, new Vector3(0, .3f, -5), Vector3.right, Vector3.forward);
                Call(tracks, "WheelAt", 101, new Vector3(0, .3f, 5), Vector3.right, Vector3.forward);
                var cleared = Render(controller, camera, albedo, 1f, blade, fixedRail);
                Require(cleared[1] < .18f && cleared[0] > .35f,
                    "Existing wheel clearing marker was confused with the junction marker: " + string.Join(",", cleared));
                ((IDisposable)tracks).Dispose();
                var partialBeforeShift = Render(controller, camera, albedo, .48f, blade, fixedRail);
                var shift = new Vector3(15000, 0, 15000); junction.transform.position += shift; camera.transform.position += shift;
                Call(controller, "SetWorldOffset", shift);
                var rebased = Render(controller, camera, albedo, .48f, blade, fixedRail);
                Compare(partialBeforeShift, rebased, .12f, "far-map origin precision");
                // A junction is often the last global marker written in a frame.
                // A later vehicle height capture must ignore that stale marker.
                var vehicleRoot = new GameObject("Late streamed car");
                try
                {
                    Cube("Car roof", vehicleRoot.transform, new Vector3(0, 2, 0), new Vector3(3, .2f, 6), material);
                    Call(registry, "Register", vehicleRoot.transform, null, null);
                    var entry = ((IList)Get(registry, "vehicles"))[0];
                    Call(registry, "RefreshParts", entry);
                    Shader.SetGlobalFloat("_DVPSVehicleIndex", -3f);
                    Shader.SetGlobalMatrix("_DVPSJunctionDelta", Matrix4x4.zero);
                    Call(registry, "CaptureHeight", entry);
                    var heights = (RenderTexture)Get(registry, "heights"); int slot = (int)Get(entry, "Slot");
                    var request = AsyncGPUReadback.Request(heights, 0, 0, 256, 0, 256, slot, 1); request.WaitForCompletion();
                    Require(!request.hasError, "Vehicle height readback failed");
                    var samples = request.GetData<float>();
                    Require(Mathf.Abs(samples[128 * 256 + 128] - 2.1f) < .02f,
                        "Stale junction marker corrupted a subsequent vehicle height capture: " + samples[128 * 256 + 128]);
                }
                finally { UnityEngine.Object.DestroyImmediate(vehicleRoot); }
                Debug.Log("Junction snow samples: original=" + string.Join(",", before) + " switched=" + string.Join(",", switched) +
                    " partial=" + string.Join(",", partial) + " rebased=" + string.Join(",", rebased));
            }
            finally
            {
                ((IDisposable)controller).Dispose(); ((IDisposable)repository).Dispose();
                camera.RemoveCommandBuffer(CameraEvent.AfterLighting, probe); probe.Dispose();
                var target = camera.targetTexture; camera.targetTexture = null;
                foreach (var o in new UnityEngine.Object[] { junction, decoy.gameObject, material, mesh, target, albedo, camera.gameObject })
                    if (o != null) UnityEngine.Object.DestroyImmediate(o);
            }
        }

        private static Renderer Cube(string name, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.name = name;
            cube.transform.SetParent(parent, false); cube.transform.localPosition = position; cube.transform.localScale = scale;
            var renderer = cube.GetComponent<Renderer>(); renderer.sharedMaterial = material; return renderer;
        }

        private static float[] Render(object controller, Camera camera, RenderTexture albedo, float amount, Renderer blade, Renderer rail)
        {
            Call(controller, "SetNetworkCoverage", (float?)amount);
            Call(controller, "Apply", amount, true); camera.Render();
            var previous = RenderTexture.active; RenderTexture.active = albedo;
            var texture = new Texture2D(albedo.width, albedo.height, TextureFormat.RGBAFloat, false, true);
            texture.ReadPixels(new Rect(0, 0, albedo.width, albedo.height), 0, 0); texture.Apply();
            var values = new float[2]; int n = 0;
            foreach (var renderer in new[] { blade, rail })
            {
                float total = 0;
                for (int i = -4; i <= 4; i++)
                {
                    var point = renderer.transform.TransformPoint(new Vector3(0, .5f, i * .085f));
                    var uv = camera.WorldToViewportPoint(point);
                    total += texture.GetPixel(Mathf.RoundToInt(uv.x * (texture.width - 1)), Mathf.RoundToInt(uv.y * (texture.height - 1))).r;
                }
                values[n++] = total / 9;
            }
            UnityEngine.Object.DestroyImmediate(texture); RenderTexture.active = previous; return values;
        }
        private static void Compare(float[] a, float[] b, float tolerance, string label)
        { for (int i = 0; i < a.Length; i++) Require(Mathf.Abs(a[i] - b[i]) < tolerance, label + " changed snow sample " + i + ": " + a[i] + " -> " + b[i]); }
    }
}
