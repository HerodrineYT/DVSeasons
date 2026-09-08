using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowModeVerification
    {
        public static void Verify()
        {
            object controller = null, repository = null;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                QualitySettings.antiAliasing = 0;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = Color.gray * 0.3f;
                var camera = new GameObject("Snow mode camera") { tag = "MainCamera" }.AddComponent<Camera>();
                camera.renderingPath = RenderingPath.DeferredShading;
                camera.allowHDR = true;
                camera.transform.position = new Vector3(0, 7, -8);
                camera.transform.LookAt(Vector3.zero);
                camera.targetTexture = new RenderTexture(128, 128, 24, RenderTextureFormat.ARGBHalf);
                camera.targetTexture.Create();
                var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                floor.transform.localScale = new Vector3(30, 0.2f, 30);
                floor.transform.position = new Vector3(0, -0.1f, 0);
                var surface = new Material(Shader.Find("Standard"));
                surface.color = new Color(0.12f, 0.1f, 0.08f);
                floor.GetComponent<Renderer>().sharedMaterial = surface;
                var sun = new GameObject("Sun").AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.transform.rotation = Quaternion.Euler(50, 20, 0);
                var modPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/build/DVSeasons"));
                var assembly = Assembly.LoadFrom(Path.Combine(modPath, "DVSeasons.dll"));
                var repositoryType = assembly.GetType("DVSeasons.Mod.SeasonAssetBundleRepository", true);
                repository = Activator.CreateInstance(repositoryType, new object[] { modPath });
                // The game stages this asynchronous load across frames. This
                // synchronous editor check waits before exercising the renderer.
                repositoryType.GetMethod("BeginLoad").Invoke(repository, null);
                var request = (AssetBundleCreateRequest)repositoryType.GetField("bundleLoadRequest",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(repository);
                Require(request != null && request.assetBundle != null, "Test AssetBundle failed to load.");
                Require((bool)repositoryType.GetProperty("IsLoadFinished").GetValue(repository, null), "AssetBundle not ready.");
                var type = assembly.GetType("DVSeasons.Mod.ProceduralSnowController", true);
                controller = Activator.CreateInstance(type, new[] { repository });
                var apply = type.GetMethod("Apply");
                var weather = type.GetMethod("SetWeather");
                int callbacks = 0;
                type.GetField("BeforeSnowRender").SetValue(controller, new Action(() => callbacks++));
                weather.Invoke(controller, new object[] { 1f });
                var bare = Sample(camera);
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    // A dry re-enable still initializes from the current season.
                    weather.Invoke(controller, new object[] { 0f });
                    apply.Invoke(controller, new object[] { 0.8f, true });
                    Sample(camera);
                    apply.Invoke(controller, new object[] { 0.8f, true });
                    var snow = Sample(camera);
                    Require(snow > bare + 0.1f, "Re-enable did not restore visible snow.");
                    Require(camera.GetCommandBuffers(CameraEvent.BeforeReflections).Length == 1,
                        "Duplicate or missing snow command buffer.");
                    Require(Mathf.Abs((float)type.GetProperty("Coverage").GetValue(controller, null) - 0.8f) < 0.001f,
                        "Re-enable reused stale coverage.");
                    apply.Invoke(controller, new object[] { 1f, false });
                    Require(!(bool)type.GetProperty("IsActive").GetValue(controller, null), "Disabled controller is active.");
                    Require(camera.GetCommandBuffers(CameraEvent.BeforeReflections).Length == 0,
                        "Disable left a camera command buffer.");
                    foreach (var field in new[] { "material", "quad", "noiseTexture", "exposureCamera", "terrainExposureCommands" })
                        Require(type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller) == null,
                            "Disable retained " + field);
                    foreach (var field in new[] { "near", "far" })
                    {
                        var map = type.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                        Require(map.GetType().GetField("Texture").GetValue(map) == null, "Disable retained exposure texture.");
                    }
                    var captures = type.GetProperty("ExposureCaptureCount").GetValue(controller, null);
                    var before = callbacks;
                    weather.Invoke(controller, new object[] { 1f });
                    for (int frame = 0; frame < 5; frame++)
                    {
                        apply.Invoke(controller, new object[] { 1f, false });
                        Require(Mathf.Abs(Sample(camera) - bare) < 0.01f, "Disable left snow in the rendered image.");
                    }
                    Require(callbacks == before, "Disabled snow still receives rendering callbacks.");
                    Require(captures.Equals(type.GetProperty("ExposureCaptureCount").GetValue(controller, null)),
                        "Disabled snow captured exposure maps.");
                    var rails = type.GetField("RailTracks").GetValue(controller);
                    Require((float)rails.GetType().GetProperty("SnowClock").GetValue(rails, null) == 0f,
                        "Disabled snow advanced the snowfall clock.");
                }
                Debug.Log("DVSEASONS_SNOW_MODE_OK: three on/off cycles; visible cover, dry re-enable, resource release, no disabled render callbacks or exposure captures.");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
            finally
            {
                if (controller != null) ((IDisposable)controller).Dispose();
                if (repository != null) ((IDisposable)repository).Dispose();
            }
        }

        private static float Sample(Camera camera)
        {
            camera.Render();
            var previous = RenderTexture.active;
            RenderTexture.active = camera.targetTexture;
            var texture = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            texture.ReadPixels(new Rect(64, 64, 1, 1), 0, 0);
            texture.Apply();
            var value = texture.GetPixel(0, 0).r;
            UnityEngine.Object.DestroyImmediate(texture);
            RenderTexture.active = previous;
            return value;
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
