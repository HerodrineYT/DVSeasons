#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    /// <summary>
    /// Exercises the same Unity streaming-mipmap API used by the runtime guard.
    /// The fixture is imported with mip streaming enabled and is intentionally
    /// non-readable, so a successful check proves that the guard is waiting for
    /// the GPU resident mip rather than accidentally using Texture2D.GetPixels.
    /// </summary>
    public static class StreamingTextureReadinessVerification
    {
        private const string FixtureAssetPath = "Assets/Editor/StreamingReadinessFixture.png";
        private static readonly BindingFlags StaticFlags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static Texture2D fixture;
        private static MethodInfo tryAcquire;
        private static MethodInfo selectMipmapLevel;
        private static MethodInfo reset;
        private static int stage;
        private static int attempts;
        private static int expectedMipmapLevel;
        private static bool requestSetterWorks;
        private static bool previousStreamingMipmapsActive;
        private static bool previousForceLoadAll;
        private static bool havePreviousStreamingState;

        private static bool liveStreaming;
        private static bool forceResident;
        public static void VerifyWithStreaming() { liveStreaming = true; Verify(); }
        public static void VerifyForceResident() { liveStreaming = true; forceResident = true; Verify(); }
        public static void Verify()
        {
            try
            {
                Setup();
                EditorApplication.update += Poll;
                Poll();
            }
            catch (Exception exception)
            {
                Finish(1, exception);
            }
        }

        private static void Setup()
        {
            var args = Environment.GetCommandLineArgs();
            var flag = Array.IndexOf(args, "-dvseasonsModPath");
            var modPath = flag >= 0 && flag + 1 < args.Length
                ? args[flag + 1]
                : Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/build/DVSeasons"));
            var assemblyPath = Path.Combine(modPath, "DVSeasons.dll");
            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException("Built DVSeasons.dll was not found.", assemblyPath);

            var assembly = Assembly.LoadFrom(assemblyPath);
            var helper = assembly.GetType("DVSeasons.Mod.StreamingTextureReadiness", true);
            tryAcquire = helper.GetMethod("TryAcquire", StaticFlags);
            selectMipmapLevel = helper.GetMethod("SelectMipmapLevel", StaticFlags);
            reset = helper.GetMethod("Reset", StaticFlags);
            Require(tryAcquire != null && selectMipmapLevel != null && reset != null,
                "Streaming readiness API is incomplete.");

            var assetsDirectory = Path.Combine(Application.dataPath, "Editor");
            Directory.CreateDirectory(assetsDirectory);
            AssetDatabase.DeleteAsset(FixtureAssetPath);
            var fixturePath = Path.Combine(assetsDirectory, "StreamingReadinessFixture.png");
            if (File.Exists(fixturePath)) File.Delete(fixturePath);

            previousStreamingMipmapsActive = QualitySettings.streamingMipmapsActive;
            previousForceLoadAll = Texture.streamingTextureForceLoadAll;
            havePreviousStreamingState = true;
            // Verify is also usable with NullGfxDevice, which cannot stream mips.
            // The two live entry points must run with a real graphics device.
            QualitySettings.streamingMipmapsActive = liveStreaming;
            Texture.streamingTextureForceLoadAll = forceResident;

            var generated = new Texture2D(1024, 1024, TextureFormat.RGBA32, true, false);
            var pixels = new Color32[1024 * 1024];
            for (var y = 0; y < 1024; y++)
            for (var x = 0; x < 1024; x++)
            {
                var value = (byte)((x * 13 + y * 7) & 255);
                pixels[(y * 1024) + x] = new Color32(value, (byte)(255 - value),
                    (byte)((x + y) & 255), 255);
            }
            generated.SetPixels32(pixels);
            generated.Apply(false, false);
            File.WriteAllBytes(fixturePath, generated.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(generated);
            AssetDatabase.ImportAsset(FixtureAssetPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(FixtureAssetPath) as TextureImporter;
            Require(importer != null, "Could not create the streaming fixture importer.");
            importer.mipmapEnabled = true;
            importer.streamingMipmaps = true;
            importer.streamingMipmapsPriority = 2;
            importer.isReadable = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
            importer.SaveAndReimport();
            fixture = AssetDatabase.LoadAssetAtPath<Texture2D>(FixtureAssetPath);
            Require(fixture != null && fixture.streamingMipmaps && fixture.mipmapCount > 1,
                "Fixture was not imported with streaming mipmaps enabled.");
            Require(!fixture.isReadable, "Fixture unexpectedly remained CPU-readable.");

            fixture.ClearRequestedMipmapLevel();
            var selected = (int)selectMipmapLevel.Invoke(null,
                new object[] { fixture, 256, 256 });
            var expected = Mathf.Max(0, Mathf.FloorToInt(Mathf.Log(
                Mathf.Max(fixture.width / 256f, fixture.height / 256f), 2f)));
            expected = Mathf.Clamp(expected, 0, fixture.mipmapCount - 1);
            Require(selected == expected, "Unexpected mip level selected for a " + fixture.width +
                "->256 readback: " + selected + " (expected " + expected + ").");
            expectedMipmapLevel = expected;
            fixture.requestedMipmapLevel = 1;
            requestSetterWorks = fixture.requestedMipmapLevel == 1;
            fixture.ClearRequestedMipmapLevel();
            stage = 0;
            attempts = 0;
        }

        private static void Poll()
        {
            try
            {
                attempts++;
                object[] arguments = { fixture, 256, 256, null };
                var ready = (bool)tryAcquire.Invoke(null, arguments);
                if (!ready)
                {
                    if(attempts==1 || attempts==299)
                        Debug.Log("STREAMING_WAIT: request="+fixture.requestedMipmapLevel+" loaded="+fixture.loadedMipmapLevel+
                            " loading="+fixture.loadingMipmapLevel+" complete="+fixture.IsRequestedMipmapLevelLoaded()+" streaming="+QualitySettings.streamingMipmapsActive);
                    // This is a test timeout only. Runtime waits for sufficient
                    // resident detail, without accepting a blurry timeout fallback.
                    if (attempts < 300) return;
                    throw new InvalidOperationException(
                        "Unity did not report the requested streaming mip as loaded within 300 editor updates.");
                }

                var lease = arguments[3] as IDisposable;
                Require(lease != null, "Streaming texture was accepted without a read lease.");
                var selected = fixture.requestedMipmapLevel;
                Debug.Log("DVSEASONS_STREAMING_PROBE: request=" + selected +
                    ", loaded=" + fixture.loadedMipmapLevel + ", loading=" +
                    fixture.loadingMipmapLevel + ", desired=" + fixture.desiredMipmapLevel +
                    ", calculated=" + fixture.calculatedMipmapLevel + ", setter=" + requestSetterWorks);
                Require(fixture.loadedMipmapLevel >= 0 && fixture.loadedMipmapLevel <= expectedMipmapLevel,
                    "Read lease was granted before a sufficiently detailed mip was resident (loaded " +
                    fixture.loadedMipmapLevel + ", requested " + expectedMipmapLevel + ").");
                if (forceResident)
                {
                    Require(fixture.loadedMipmapLevel == 0 && expectedMipmapLevel > 0,
                        "Fixture did not retain a finer mip than the readback requested.");
                    Require(!fixture.IsRequestedMipmapLevelLoaded(),
                        "Fixture did not reproduce Unity's false exact-mip completion signal.");
                }
                if (requestSetterWorks)
                    Require(selected == expectedMipmapLevel,
                        "Read lease did not hold the selected mip level; Unity reports " + selected + ".");
                lease.Dispose();

                if (stage == 0)
                {
                    if (requestSetterWorks)
                        Require(fixture.requestedMipmapLevel < 0,
                            "Read lease did not restore Unity's automatic mip request.");
                    stage = 1;
                    attempts = 0;
                    if (requestSetterWorks) fixture.requestedMipmapLevel = 1;
                    return;
                }

                if (requestSetterWorks)
                    Require(fixture.requestedMipmapLevel == 1,
                        "Read lease did not restore an explicit pre-existing mip request.");
                reset.Invoke(null, null);
                Finish(0, null);
            }
            catch (Exception exception)
            {
                Finish(1, exception);
            }
        }

        private static void Finish(int exitCode, Exception exception)
        {
            EditorApplication.update -= Poll;
            try
            {
                if (reset != null) reset.Invoke(null, null);
            }
            catch (Exception cleanupException)
            {
                if (exception == null) exception = cleanupException;
            }
            if (havePreviousStreamingState)
            {
                QualitySettings.streamingMipmapsActive = previousStreamingMipmapsActive;
                Texture.streamingTextureForceLoadAll = previousForceLoadAll;
            }
            AssetDatabase.DeleteAsset(FixtureAssetPath);
            AssetDatabase.Refresh();
            if (exception != null)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(exitCode);
                return;
            }
            Debug.Log("DVSEASONS_STREAMING_READINESS_OK: sufficient resident mip detail, " +
                "non-readable fixture, and automatic/explicit request restoration verified.");
            EditorApplication.Exit(0);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
