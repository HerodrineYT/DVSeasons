using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class TexturePackDedupVerification
    {
        public static void Verify()
        {
            try
            {
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
                var builtMod = Path.Combine(root, "artifacts/build/DVSeasons");
                var fixture = Path.Combine(root, "artifacts/verification/texture-pack-dedup-" +
                    DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
                CreateLeanFixture(root, builtMod, fixture);

                var assembly = Assembly.LoadFrom(Path.Combine(builtMod, "DVSeasons.dll"));
                var coreAssembly = Assembly.LoadFrom(Path.Combine(builtMod, "DVSeasons.Core.dll"));
                var repositoryType = assembly.GetType("DVSeasons.Mod.SeasonAssetBundleRepository", true);
                var seasonType = coreAssembly.GetType("DVSeasons.Core.SeasonKind", true);
                var stageType = coreAssembly.GetType("DVSeasons.Core.WinterTrackTextureStage", true);
                var repository = Activator.CreateInstance(repositoryType, new object[] { fixture });
                try
                {
                    LoadFixtureBundles(repositoryType, repository, fixture);
                    VerifyPreparedTextures(repositoryType, seasonType, stageType, repository, root);
                    VerifySeasonAliases(repositoryType, seasonType, repository);
                    VerifyTrackAliases(repositoryType, stageType, repository);
                    VerifyTerrainArrayCopy(repositoryType, seasonType, repository, fixture);
                    VerifyOverride(repositoryType, seasonType, repository, fixture);
                }
                finally
                {
                    ((IDisposable)repository).Dispose();
                }

                Debug.Log("DVSEASONS_TEXTURE_PACK_DEDUP_OK " + fixture);
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        private static void LoadFixtureBundles(Type repositoryType, object repository, string fixture)
        {
            var main = AssetBundle.LoadFromFile(Path.Combine(fixture, "AssetBundles/dvseasons_dv99"));
            var winter = AssetBundle.LoadFromFile(Path.Combine(fixture, "AssetBundles/dvseasons_winter"));
            var tracks = AssetBundle.LoadFromFile(Path.Combine(fixture, "AssetBundles/dvseasons_tracks"));
            Require(main != null && winter != null && tracks != null, "All three texture bundles must load.");
            repositoryType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository, new object[] { main });
            repositoryType.GetField("winterBundle", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(repository, winter);
            repositoryType.GetField("winterBundleLoadFinished", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(repository, true);
            repositoryType.GetField("tracksBundle", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(repository, tracks);
            repositoryType.GetField("tracksBundleLoadFinished", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(repository, true);
            var index = repositoryType.GetMethod("IndexAsset", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var name in main.GetAllAssetNames()) index.Invoke(repository, new object[] { name });
            foreach (var name in winter.GetAllAssetNames()) index.Invoke(repository, new object[] { name });
            foreach (var name in tracks.GetAllAssetNames()) index.Invoke(repository, new object[] { name });
        }

        private static void VerifyPreparedTextures(Type repositoryType, Type seasonType, Type stageType,
            object repository, string root)
        {
            var winterBundle = (AssetBundle)repositoryType.GetField("winterBundle",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(repository);
            var tracksBundle = (AssetBundle)repositoryType.GetField("tracksBundle",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(repository);
            Require(winterBundle.GetAllAssetNames().Length == 22 && tracksBundle.GetAllAssetNames().Length == 20,
                "Prepared bundles must contain 22 winter and 20 track canonical textures.");
            foreach (var preparedBundle in new[] { winterBundle, tracksBundle })
            foreach (var path in preparedBundle.GetAllAssetNames())
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var track = path.IndexOf("/winter_track/", StringComparison.Ordinal) >= 0;
                var pieces = path.Split('/');
                var variant = pieces[pieces.Length - 2];
                var profile = Enum.Parse(track ? stageType : seasonType, variant, true);
                var textureMethod = repositoryType.GetMethod(track ? "TryLoadWinterTrackTexture" : "TryLoadTexture");
                var textureArgs = new[] { (object)name, profile, null };
                Require((bool)textureMethod.Invoke(repository, textureArgs), "Direct lookup failed: " + path);
                var texture = (Texture2D)textureArgs[2];
                Require(texture == preparedBundle.LoadAsset<Texture2D>(path), "Direct lookup copied the bundled texture: " + path);
                var second = new[] { (object)name, profile, null };
                Require((bool)textureMethod.Invoke(repository, second) && ReferenceEquals(second[2], texture),
                    "Direct texture cache failed: " + path);
                Require(texture.isReadable && texture.mipmapCount > 1, "Prepared pixels/mips unavailable: " + path);
                var original = new Texture2D(2, 2, TextureFormat.RGBA32, false, name == "watericenormal");
                try
                {
                    var source = Path.Combine(root, "DVSeasons.Unity", path);
                    Require(original.LoadImage(File.ReadAllBytes(source), false), "Cannot read authoring PNG: " + source);
                    Require(original.width == texture.width && original.height == texture.height,
                        "Bundling reduced texture dimensions: " + path);
                    var expected = original.GetPixels32();
                    var actual = texture.GetPixels32();
                    Require(expected.Length == actual.Length, "Pixel dimensions differ: " + path);
                    for (var i = 0; i < expected.Length; i++)
                        if (!expected[i].Equals(actual[i]))
                            throw new InvalidOperationException("Bundling changed RGBA at " + path + " pixel " + i + ": " + actual[i] + " != " + expected[i]);
                    var width = Mathf.Max(1, texture.width / 2);
                    var height = Mathf.Max(1, texture.height / 2);
                    var pixels = (Color32[])InvokePixels(repositoryType.GetMethod(track ? "TryLoadWinterTrackPixels" : "TryLoadPixels"),
                        repository, new[] { (object)name, profile, width, height, null });
                    var mip = texture.GetPixels32(1);
                    for (var i = 0; i < mip.Length; i++)
                        Require(pixels[i].Equals(mip[i]), "CPU profile differs from prepared mip: " + path);
                    // graphicsFormat reports UNorm for both types in a Gamma
                    // player/editor. Inspect the serialized asset's color space,
                    // which also controls its upload in DV's Linear renderer.
                    var colorSpace = new SerializedObject(texture).FindProperty("m_ColorSpace");
                    Require(colorSpace != null, "Bundled texture color-space metadata missing: " + path);
                    var srgb = colorSpace.intValue == 1;
                    Require(srgb != string.Equals(name, "WaterIceNormal", StringComparison.OrdinalIgnoreCase),
                        "Normal/albedo color space changed: " + path);
                }
                finally { UnityEngine.Object.DestroyImmediate(original); }
            }
            Require((int)repositoryType.GetProperty("TextureDecodeCount").GetValue(repository, null) == 0,
                "Standard texture lookup decoded runtime PNGs.");
            Require((int)repositoryType.GetProperty("PixelReadbackCount").GetValue(repository, null) == 0,
                "Standard texture lookup performed a synchronous GPU readback.");
            Debug.Log("DVSEASONS_PREPARED_TEXTURES_OK 42 exact RGBA/fullsize/mips; no PNG decode or GPU readback");
        }

        private static void VerifyOverride(Type repositoryType, Type seasonType, object repository, string fixture)
        {
            var path = Path.Combine(fixture, "Overrides/Seasonal/winter/Coal_01d.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var overrideImage = new Texture2D(8, 8, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color32[64];
                for (var i = 0; i < pixels.Length; i++) pixels[i] = new Color32(43, 119, 201, 173);
                overrideImage.SetPixels32(pixels); overrideImage.Apply();
                File.WriteAllBytes(path, overrideImage.EncodeToPNG());
            }
            finally { UnityEngine.Object.DestroyImmediate(overrideImage); }
            repositoryType.GetMethod("ResetForSession").Invoke(repository, null);
            var args = new[] { (object)"Coal_01d", Enum.Parse(seasonType, "Winter"), null };
            var method = repositoryType.GetMethod("TryLoadTexture");
            Require((bool)method.Invoke(repository, args), "Explicit override failed.");
            var texture = (Texture2D)args[2];
            Require(texture.width == 8 && texture.height == 8 && texture.GetPixels32()[0].Equals(new Color32(43, 119, 201, 173)),
                "Explicit override did not replace bundled RGBA.");
            args[2] = null;
            Require((bool)method.Invoke(repository, args) && ReferenceEquals(args[2], texture), "Override texture was decoded twice.");
            Require((int)repositoryType.GetProperty("TextureDecodeCount").GetValue(repository, null) == 1,
                "Only one explicit override PNG should be decoded.");
            Require((int)repositoryType.GetProperty("PixelReadbackCount").GetValue(repository, null) == 0,
                "Overrides must not require GPU readback either.");
        }

        private static void VerifySeasonAliases(Type repositoryType, Type seasonType,
            object repository)
        {
            var autumn = Enum.Parse(seasonType, "Autumn");
            var winter = Enum.Parse(seasonType, "Winter");
            RequireSamePixels(repositoryType, repository, "SleeperNew_d", "SleeperOld_d",
                autumn, 64, 64);
            RequireSamePixels(repositoryType, repository, "SleeperNew_d", "SleeperOld_d",
                winter, 64, 64);
            RequireSamePixels(repositoryType, repository, "AsphaltTiling_01d",
                "AsphaltTiling_01d_White", winter, 64, 64);
            RequireSamePixels(repositoryType, repository, "MB_concrete_01d",
                "MB_concrete_01d_blue", winter, 64, 64);
        }

        private static void RequireSamePixels(Type repositoryType, object repository,
            string canonicalName, string aliasName, object season, int width, int height)
        {
            var method = repositoryType.GetMethod("TryLoadPixels");
            var canonical = InvokePixels(method, repository,
                new[] { (object)canonicalName, season, width, height, null });
            var alias = InvokePixels(method, repository,
                new[] { (object)aliasName, season, width, height, null });
            Require(ReferenceEquals(canonical, alias), aliasName + " did not use the cached canonical pixels.");
        }

        private static void VerifyTrackAliases(Type repositoryType, Type stageType,
            object repository)
        {
            var method = repositoryType.GetMethod("TryLoadWinterTrackPixels");
            foreach (var stageName in new[] { "Early", "Middle", "Late" })
            {
                var stage = Enum.Parse(stageType, stageName);
                var canonical = InvokePixels(method, repository,
                    new[] { (object)"SleeperNew_d", stage, 64, 64, null });
                var alias = InvokePixels(method, repository,
                    new[] { (object)"SleeperOld_d", stage, 64, 64, null });
                Require(ReferenceEquals(canonical, alias),
                    stageName + " sleeper alias did not use canonical pixels.");
            }
        }

        private static Array InvokePixels(MethodInfo method, object repository, object[] arguments)
        {
            Require((bool)method.Invoke(repository, arguments),
                "Pixel lookup failed for " + arguments[0] + ".");
            var pixels = arguments[arguments.Length - 1] as Array;
            Require(pixels != null && pixels.Length > 0,
                "Pixel lookup returned no data for " + arguments[0] + ".");
            return pixels;
        }

        private static void VerifyTerrainArrayCopy(Type repositoryType, Type seasonType,
            object repository, string fixture)
        {
            var bundleProperty = repositoryType.GetProperty("Bundle");
            var bundle = bundleProperty.GetValue(repository, null) as AssetBundle;
            var injectBundle = bundle == null;
            if (bundle == null)
                bundle = AssetBundle.LoadFromFile(Path.Combine(fixture, "AssetBundles", "dvseasons_dv99"));
            Require(bundle != null, "Lean bundle could not be opened.");
            var names = bundle.GetAllAssetNames();
            foreach (var name in names)
                Require(name.IndexOf("terraintexture", StringComparison.OrdinalIgnoreCase) < 0,
                    "Lean bundle still contains an individual terrain texture: " + name);

            if (injectBundle)
            {
                bundleProperty.GetSetMethod(true).Invoke(repository, new object[] { bundle });
                var index = repositoryType.GetMethod("IndexAsset",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (var name in names) index.Invoke(repository, new object[] { name });
            }

            var completeTracks = repositoryType.GetMethod("HasCompleteWinterTrackSet");
            Require((bool)completeTracks.Invoke(repository, new object[] { "SleeperOld_d" }),
                "The canonical files did not provide the complete old-sleeper track set.");

            var winter = Enum.Parse(seasonType, "Winter");
            var getLayer = repositoryType.GetMethod("TryGetTerrainLayer");
            var arguments = new[] { winter, (object)13, null };
            Require((bool)getLayer.Invoke(repository, arguments),
                "A Texture2D terrain layer was not copied from the array.");
            var layer = arguments[2] as Texture2D;
            Require(layer != null && layer.width == 512 && layer.height == 512,
                "The copied terrain layer has invalid dimensions.");
            Require(layer.name.StartsWith("DVSeasons Terrain Winter layer ",
                StringComparison.Ordinal), "The repository loaded a packed duplicate instead of an array copy.");
        }

        private static void CreateLeanFixture(string root, string builtMod, string fixture)
        {
            var bundleFolder = Path.Combine(fixture, "AssetBundles");
            Directory.CreateDirectory(bundleFolder);
            File.Copy(Path.Combine(builtMod, "AssetBundles", "dvseasons_dv99"),
                Path.Combine(bundleFolder, "dvseasons_dv99"), true);

            File.Copy(Path.Combine(builtMod, "AssetBundles", "dvseasons_winter"),
                Path.Combine(bundleFolder, "dvseasons_winter"), true);
            File.Copy(Path.Combine(builtMod, "AssetBundles", "dvseasons_tracks"),
                Path.Combine(bundleFolder, "dvseasons_tracks"), true);
            // An obsolete loose file from a prior install must not shadow prepared
            // bundle textures or reintroduce runtime PNG decoding.
            var legacy = Path.Combine(fixture, "Textures/Seasonal/winter/Coal_01d.png");
            Directory.CreateDirectory(Path.GetDirectoryName(legacy));
            File.WriteAllBytes(legacy, new byte[] { 1, 2, 3, 4 });
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
