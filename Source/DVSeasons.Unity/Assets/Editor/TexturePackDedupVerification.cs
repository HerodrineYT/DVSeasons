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
                    VerifySeasonAliases(repositoryType, seasonType, repository);
                    VerifyTrackAliases(repositoryType, stageType, repository);
                    VerifyTerrainArrayCopy(repositoryType, seasonType, repository, fixture);
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

            var sourceRoot = Path.Combine(root, "Resources/Runtime/Textures/Seasonal");
            var destinations = new Dictionary<string, string>
            {
                { "autumn/SleeperNew_d.png", "autumn/SleeperNew_d.png" },
                { "winter_track/early/SleeperNew_d.png", "winter_track/early/SleeperNew_d.png" },
                { "winter_track/middle/SleeperNew_d.png", "winter_track/middle/SleeperNew_d.png" },
                { "winter/SleeperNew_d.png", "winter/SleeperNew_d.png" },
                { "winter/AsphaltTiling_01d.png", "winter/AsphaltTiling_01d.png" },
                { "winter/MB_concrete_01d.png", "winter/MB_concrete_01d.png" }
            };
            foreach (var pair in destinations)
            {
                var destination = Path.Combine(fixture, "Textures/Seasonal", pair.Value);
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(Path.Combine(sourceRoot, pair.Key), destination, true);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
