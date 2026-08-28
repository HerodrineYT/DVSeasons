using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class DVSeasonsAssetBundleBuilder
    {
        private const string AssetRoot = "Assets/DVSeasons/DV99";
        private const string BundleName = "dvseasons_dv99";

        public static void Build()
        {
            ConfigureTextureImporters();
            CreateTerrainArrays();
            var output = GetArgument("-bundleOutput") ?? Path.Combine(Directory.GetCurrentDirectory(), "Build", "Windows");
            Directory.CreateDirectory(output);

            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { AssetRoot });
            var assets = new List<string>(guids.Length);
            for (var i = 0; i < guids.Length; i++) assets.Add(AssetDatabase.GUIDToAssetPath(guids[i]));
            assets.Sort(StringComparer.Ordinal);
            if (assets.Count != 123) throw new InvalidOperationException("Expected 123 Texture2D assets, found " + assets.Count);
            assets.Add(AssetRoot + "/Generated/Terrain_spring.asset");
            assets.Add(AssetRoot + "/Generated/Terrain_autumn.asset");
            assets.Add(AssetRoot + "/Generated/Terrain_winter.asset");

            var definition = new UnityEditor.AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = assets.ToArray()
            };
            var manifest = BuildPipeline.BuildAssetBundles(output, new[] { definition },
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.DeterministicAssetBundle,
                BuildTarget.StandaloneWindows64);
            if (manifest == null || !File.Exists(Path.Combine(output, BundleName)))
                throw new InvalidOperationException("Unity did not produce " + BundleName);
            Debug.Log("DVSeasons AssetBundle built: " + Path.Combine(output, BundleName) +
                " (123 textures + 3 MicroSplat terrain arrays)");
        }

        private static void ConfigureTextureImporters()
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { AssetRoot });
            for (var i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = true;
                importer.isReadable = path.IndexOf("TerrainTexture", StringComparison.OrdinalIgnoreCase) >= 0;
                importer.wrapMode = path.IndexOf("TerrainTexture", StringComparison.OrdinalIgnoreCase) >= 0
                    ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Trilinear;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.maxTextureSize = 1024;
                importer.SaveAndReimport();
            }
        }

        private static void CreateTerrainArrays()
        {
            const string generatedFolder = AssetRoot + "/Generated";
            if (!AssetDatabase.IsValidFolder(generatedFolder))
                AssetDatabase.CreateFolder(AssetRoot, "Generated");

            CreateTerrainArray("spring", generatedFolder + "/Terrain_spring.asset");
            CreateTerrainArray("autumn", generatedFolder + "/Terrain_autumn.asset");
            CreateTerrainArray("winter", generatedFolder + "/Terrain_winter.asset");
            AssetDatabase.SaveAssets();
        }

        private static void CreateTerrainArray(string season, string outputPath)
        {
            var sources = new Texture2D[16];
            for (var i = 0; i < sources.Length; i++)
            {
                var sourcePath = AssetRoot + "/" + season + "/TerrainTexture" + (i + 1) + ".png";
                sources[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(sourcePath);
                if (sources[i] == null || !sources[i].isReadable)
                    throw new InvalidOperationException("Readable terrain source is missing: " + sourcePath);
            }

            var width = sources[0].width;
            var height = sources[0].height;
            var array = new Texture2DArray(width, height, sources.Length, TextureFormat.RGBA32, true, false)
            {
                name = "DVSeasons Terrain " + season,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4
            };

            for (var slice = 0; slice < sources.Length; slice++)
            {
                if (sources[slice].width != width || sources[slice].height != height)
                    throw new InvalidOperationException("Terrain array sources must have identical dimensions.");
                for (var mip = 0; mip < array.mipmapCount; mip++)
                    array.SetPixels(sources[slice].GetPixels(mip), slice, mip);
            }
            array.Apply(false, false);

            if (AssetDatabase.LoadAssetAtPath<Texture2DArray>(outputPath) != null)
                AssetDatabase.DeleteAsset(outputPath);
            AssetDatabase.CreateAsset(array, outputPath);
        }

        private static string GetArgument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
