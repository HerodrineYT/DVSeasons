using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    // Reproduces full-res residency + false IsRequestedMipmapLevelLoaded in a
    // real D3D11 render loop, then checks the shipped profiles on bound materials.
    public static class SeasonalTrackStreamingVerification
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const string FixtureFolder = "Assets/Editor/SeasonalTrackStreamingFixture";
        static readonly string[] Names = { "BallastNew_d", "BallastMed_d", "BallastOld_d", "SleeperNew_d", "SleeperOld_d" };
        static readonly List<Texture2D> sources = new List<Texture2D>();
        static readonly List<Material> materials = new List<Material>();
        static readonly List<Color32[]> summerPixels = new List<Color32[]>();
        static readonly List<Texture2D> outputs = new List<Texture2D>();
        static object controller, repository, settings, state;
        static Type stateType, seasonType, trackStageType, readbackType;
        static Camera camera;
        static RenderTexture target;
        static bool previousStreaming, previousForce, ownsFolder;
        static int stage;
        static double deadline;

        static object Get(object owner, string field) { return owner.GetType().GetField(field, All).GetValue(owner); }
        static void Set(object owner, string field, object value) { owner.GetType().GetField(field, All).SetValue(owner, value); }
        static object Call(object owner, string method, params object[] args) { return owner.GetType().GetMethod(method, All).Invoke(owner, args); }
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }

        public static void Run()
        {
            previousStreaming = QualitySettings.streamingMipmapsActive;
            previousForce = Texture.streamingTextureForceLoadAll;
            try
            {
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
                var modPath = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD") ?? Path.Combine(root, "artifacts/build/DVSeasons");
                var game = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
                AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
                    foreach (var dir in new[] { modPath, Path.Combine(game, "DerailValley_Data/Managed"), Path.Combine(game, "DerailValley_Data/Managed/UnityModManager") })
                    {
                        var file = Path.Combine(dir, new AssemblyName(args.Name).Name + ".dll");
                        if (File.Exists(file)) return Assembly.LoadFrom(file);
                    }
                    return null;
                };
                var mod = Assembly.LoadFrom(Path.Combine(modPath, "DVSeasons.dll"));
                var core = Assembly.LoadFrom(Path.Combine(modPath, "DVSeasons.Core.dll"));
                stateType = core.GetType("DVSeasons.Core.SeasonState", true);
                seasonType = core.GetType("DVSeasons.Core.SeasonKind", true);
                trackStageType = core.GetType("DVSeasons.Core.WinterTrackTextureStage", true);
                readbackType = mod.GetType("DVSeasons.Mod.SeasonTextureReadback", true);
                QualitySettings.streamingMipmapsActive = true;
                Texture.streamingTextureForceLoadAll = true;
                Require(SystemInfo.supportsAsyncGPUReadback, "Run with a real GPU and -force-d3d11.");
                Require(!AssetDatabase.IsValidFolder(FixtureFolder), "Fixture folder already exists; refusing to overwrite.");
                AssetDatabase.CreateFolder("Assets/Editor", "SeasonalTrackStreamingFixture");
                ownsFolder = true;
                foreach (var name in Names) CreateMaterial(name);
                repository = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository", true), new object[] { modPath });
                controller = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SeasonalTextureController", true), new[] { repository });
                settings = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SeasonModSettings", true));
                Set(settings, "SeasonalTexturesEnabled", true); Set(settings, "TerrainTextureChanges", true);
                Set(settings, "VegetationTextureChanges", false); Set(settings, "TextureChangeStrength", 1f);
                Set(settings, "SeasonalTextureResolution", 256); Set(settings, "TextureUpdatesPerFrame", 1);
                Set(settings, "VehicleSnowEnabled", false);
                camera = new GameObject("Seasonal track test camera").AddComponent<Camera>();
                camera.enabled = false;
                target = new RenderTexture(32, 32, 16); target.Create(); camera.targetTexture = target;
                stage = 0; SelectState();
                EditorApplication.update += Poll;
            }
            catch (Exception error) { Finish(error); }
        }

        static void CreateMaterial(string name)
        {
            var size = name.StartsWith("Ballast", StringComparison.Ordinal) ? 2048 : 1024;
            var generated = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[size * size];
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                    pixels[y * size + x] = ((x / 16 + y / 16) & 1) == 0 ? new Color32(64, 85, 47, 255) : new Color32(93, 62, 35, 255);
                generated.SetPixels32(pixels); generated.Apply();
                var asset = FixtureFolder + "/" + name + ".png";
                File.WriteAllBytes(asset, generated.EncodeToPNG());
                AssetDatabase.ImportAsset(asset, ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(asset);
                importer.mipmapEnabled = true; importer.streamingMipmaps = true; importer.streamingMipmapsPriority = 0;
                importer.isReadable = false; importer.maxTextureSize = 2048; importer.textureCompression = TextureImporterCompression.Compressed;
                importer.SaveAndReimport();
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(asset);
                Require(texture.streamingMipmaps && !texture.isReadable, "Fixture is not a streamed GPU-only texture.");
                sources.Add(texture);
                var material = new Material(Shader.Find("Standard")) { name = "Track fixture " + name, mainTexture = texture };
                materials.Add(material);
            }
            finally { UnityEngine.Object.DestroyImmediate(generated); }
        }

        static void SelectState()
        {
            var current = stage == 0 ? "Summer" : stage == 1 ? "Autumn" : "Winter";
            var next = stage == 0 ? "Autumn" : stage == 1 ? "Winter" : "Spring";
            var snow = stage < 2 || stage == 6 ? 0f : stage == 2 || stage == 5 ? .28f : stage == 3 ? .62f : 1f;
            state = Activator.CreateInstance(stateType, new object[] { (double)stage, Enum.Parse(seasonType, current), Enum.Parse(seasonType, next), 0f, snow, snow > 0 ? -20f : 15f, 0f });
            deadline = EditorApplication.timeSinceStartup + 40;
        }

        static void Poll()
        {
            try
            {
                EditorApplication.QueuePlayerLoopUpdate(); camera.Render();
                Require(EditorApplication.timeSinceStartup < deadline, "Track initialization/transition stalled at stage " + stage);
                if (!(bool)repository.GetType().GetProperty("IsLoadFinished", All).GetValue(repository, null)) return;
                Call(controller, "Apply", state, settings, stage != 7, null);
                if (Get(controller, "materialScan") != null) return;
                var sets = (IDictionary)Get(controller, "sets");
                for (int i = 0; i < sources.Count; i++)
                {
                    object found = null;
                    foreach (var set in sets.Values) if ((Texture2D)Get(set, "source") == sources[i]) { found = set; break; }
                    Require(found != null, "Track material discovery missed " + Names[i]);
                    Require(!(bool)Get(found, "failed"), "Conversion failed for " + Names[i]);
                    if ((int)Get(found, "lastStyleKey") != (int)Get(controller, "lastStyleKey") || (int)Get(found, "pendingStyleKey") != int.MinValue) return;
                }
                Call(controller, "ApplyBindings");
                for (int i = 0; i < sources.Count; i++)
                {
                    var output = materials[i].mainTexture as Texture2D;
                    Require(output != null && output != sources[i], "Seasonal texture never reached " + Names[i] + " material");
                    var actual = output.GetPixels32();
                    if (stage == 0) { outputs.Add(output); summerPixels.Add(actual); }
                    else
                    {
                        Require(output == outputs[i], "A mode/season change discarded the warm track texture cache.");
                        if (stage == 6 || (stage == 1 && i < 3))
                            Require(Same(actual, summerPixels[i]), "Snow-free ballast did not restore its original pixels.");
                        else if (stage == 1)
                            Require(!Same(actual, summerPixels[i]), "Autumn sleeper profile was not applied.");
                        else
                        {
                            var profile = stage == 2 || stage == 5 ? "Early" : stage == 3 ? "Middle" : "Late";
                            var args = new object[] { Names[i], Enum.Parse(trackStageType, profile), output.width, output.height, null };
                            Require((bool)Call(repository, "TryLoadWinterTrackPixels", args), "Shipped track profile is missing: " + Names[i] + "/" + profile);
                            Require(Same(actual, (Color32[])args[4]), "Bound output does not match authored " + Names[i] + "/" + profile);
                        }
                    }
                    Require(sources[i].requestedMipmapLevel < 0, "Readback retained the original texture's mip request.");
                }
                Debug.Log("TRACK_STREAMING_STAGE_OK: " + stage + ", all five materials, procedural=" + (stage != 7));
                if (++stage <= 8) { SelectState(); return; }
                Require((int)readbackType.GetProperty("SynchronousReadCount", All).GetValue(null, null) == 0, "Texture setup used a synchronous readback.");
                Require((int)readbackType.GetProperty("ActiveRequests", All).GetValue(null, null) == 0, "Readbacks are still active after texture completion.");
                Debug.Log("TRACK_STREAMING_OK: five streamed non-readable native-size sources; summer, autumn sleepers, three winter stages and thaw; procedural/legacy toggles preserve caches; vehicle snow disabled; zero synchronous readbacks.");
                Finish(null);
            }
            catch (Exception error) { Finish(error); }
        }

        static bool Same(Color32[] a, Color32[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!a[i].Equals(b[i])) return false;
            return true;
        }

        static void Finish(Exception error)
        {
            EditorApplication.update -= Poll;
            if (controller != null)
            {
                // Runtime disposal schedules Destroy for player frames. This
                // fixture is in edit mode: release its outputs immediately first.
                foreach (var set in ((IDictionary)Get(controller, "sets")).Values)
                {
                    var output = (Texture2D)set.GetType().GetProperty("Output", All).GetValue(set, null);
                    if (output != null) UnityEngine.Object.DestroyImmediate(output);
                }
                ((IDisposable)controller).Dispose();
            }
            AsyncGPUReadback.WaitAllRequests();
            if (repository != null) ((IDisposable)repository).Dispose();
            foreach (var material in materials) UnityEngine.Object.DestroyImmediate(material);
            if (camera != null) UnityEngine.Object.DestroyImmediate(camera.gameObject);
            if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            QualitySettings.streamingMipmapsActive = previousStreaming;
            Texture.streamingTextureForceLoadAll = previousForce;
            if (ownsFolder) AssetDatabase.DeleteAsset(FixtureFolder);
            if (error != null) Debug.LogException(error);
            EditorApplication.Exit(error == null ? 0 : 1);
        }
    }
}
