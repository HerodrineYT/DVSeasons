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
        private const string RuntimeSeasonalTextureRoot =
            "Resources/Runtime/Textures/Seasonal";
        private static readonly HashSet<string> LooseWinterTextureNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AsphaltRoad_01d.png",
                "AsphaltTiling_01d.png",
                "AsphaltTiling_01d_White.png",
                "MB_cobblestone_pavement_01d.png",
                "MB_concrete_01d.png",
                "MB_concrete_01d_blue.png",
                "MB_concrete_rough_01d.png",
                "MB_roofsheets_01d_blue.png",
                "MB_roofsheets_01d_gray.png",
                "MB_roofsheets_rusty_01d.png",
                "MB_rooftile_brown_01d.png",
                "MB_rooftile_red_01d.png",
                "MB_rooftop_cinder_01d.png",
                "RoadDetail.png",
                "Roads_LOD_01d.png",
                "Sidewalk_01d.png",
                "SidewalkTiles_01d.png",
                "SleeperNew_d.png",
                "SleeperOld_d.png",
                "SnowSurfaceDense.png",
                "WaterIceAlbedo.png",
                "WaterIceNormal.png"
            };

        public static void Build()
        {
            ConfigureTextureImporters();
            CreateTerrainArrays();
            var output = GetArgument("-bundleOutput") ?? Path.Combine(Directory.GetCurrentDirectory(), "Build", "Windows");
            Directory.CreateDirectory(output);

            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { AssetRoot });
            var sourceTextures = new List<string>(guids.Length);
            for (var i = 0; i < guids.Length; i++)
                sourceTextures.Add(AssetDatabase.GUIDToAssetPath(guids[i]));
            sourceTextures.Sort(StringComparer.Ordinal);
            if (sourceTextures.Count != 171)
                throw new InvalidOperationException("Expected 171 source Texture2D assets, found " +
                    sourceTextures.Count);
            ValidateLooseTextureMirrors(sourceTextures);

            var assets = new List<string>(sourceTextures.Count);
            for (var i = 0; i < sourceTextures.Count; i++)
                if (ShouldPackTexture(sourceTextures[i])) assets.Add(sourceTextures[i]);
            assets.Sort(StringComparer.Ordinal);
            if (assets.Count != 75)
                throw new InvalidOperationException("Expected 75 non-duplicated Texture2D assets, found " +
                    assets.Count);

            var shaderGuids = AssetDatabase.FindAssets("t:Shader",
                new[] { AssetRoot + "/Shaders" });
            var shaderAssets = new List<string>(shaderGuids.Length);
            for (var i = 0; i < shaderGuids.Length; i++)
                shaderAssets.Add(AssetDatabase.GUIDToAssetPath(shaderGuids[i]));
            shaderAssets.Sort(StringComparer.Ordinal);
            if (shaderAssets.Count != 5)
                throw new InvalidOperationException("Expected 5 Shader assets, found " +
                    shaderAssets.Count);
            assets.AddRange(shaderAssets);
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
            foreach (var shaderPath in shaderAssets)
                if (ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(shaderPath)))
                    throw new InvalidOperationException("Shader compilation failed: " + shaderPath);
            var verifiedBundle = AssetBundle.LoadFromFile(Path.Combine(output, BundleName));
            if (verifiedBundle == null) throw new InvalidOperationException("Cannot reopen built bundle.");
            try
            {
                VerifyBundleContents(verifiedBundle, assets);
                IceShaderVerification.Verify(verifiedBundle);
            }
            finally { verifiedBundle.Unload(true); }
            Debug.Log("DVSeasons AssetBundle built: " + Path.Combine(output, BundleName) +
                " (75 textures + 5 shaders + 3 MicroSplat terrain arrays; " +
                "96 duplicate source/override textures omitted)");
        }

        private static bool ShouldPackTexture(string path)
        {
            return !IsTerrainSourceTexture(path) && !IsLooseOverrideTexture(path);
        }

        private static bool IsTerrainSourceTexture(string path)
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/');
            var fileName = Path.GetFileName(normalized);
            return fileName.StartsWith("TerrainTexture", StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                && (normalized.IndexOf("/spring/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    normalized.IndexOf("/autumn/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    normalized.IndexOf("/winter/", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsLooseOverrideTexture(string path)
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/');
            var fileName = Path.GetFileName(normalized);
            if (normalized.IndexOf("/winter_track/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (normalized.IndexOf("/autumn/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (string.Equals(fileName, "SleeperNew_d.png", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(fileName, "SleeperOld_d.png", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (normalized.IndexOf("/winter/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                LooseWinterTextureNames.Contains(fileName))
                return true;
            return false;
        }

        private static void ValidateLooseTextureMirrors(List<string> sourceTextures)
        {
            var repositoryRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            var runtimeRoot = Path.Combine(repositoryRoot,
                RuntimeSeasonalTextureRoot.Replace('/', Path.DirectorySeparatorChar));
            var validated = 0;
            var canonicalRuntimePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < sourceTextures.Count; i++)
            {
                var sourceAssetPath = sourceTextures[i];
                if (!IsLooseOverrideTexture(sourceAssetPath)) continue;
                var relativePath = sourceAssetPath.Substring(AssetRoot.Length + 1);
                var runtimeRelativePath = GetCanonicalRuntimeRelativePath(relativePath);
                var sourcePath = Path.Combine(Application.dataPath,
                    sourceAssetPath.Substring("Assets/".Length)
                        .Replace('/', Path.DirectorySeparatorChar));
                var runtimePath = Path.Combine(runtimeRoot,
                    runtimeRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(runtimePath))
                    throw new InvalidOperationException("Loose runtime texture is missing: " + runtimePath);
                if (!FilesEqual(sourcePath, runtimePath))
                    throw new InvalidOperationException("Loose runtime texture differs from its Unity source: " +
                        relativePath + " -> " + runtimeRelativePath);
                canonicalRuntimePaths.Add(runtimeRelativePath);
                validated++;
            }
            if (validated != 48)
                throw new InvalidOperationException("Expected 48 loose texture sources, found " +
                    validated);
            if (canonicalRuntimePaths.Count != 40)
                throw new InvalidOperationException("Expected 40 canonical loose runtime textures, found " +
                    canonicalRuntimePaths.Count);
        }

        private static string GetCanonicalRuntimeRelativePath(string relativePath)
        {
            var normalized = (relativePath ?? string.Empty).Replace('\\', '/');
            var fileName = Path.GetFileName(normalized);
            if (string.Equals(fileName, "SleeperOld_d.png", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - fileName.Length) +
                    "SleeperNew_d.png";
            else if (string.Equals(fileName, "AsphaltTiling_01d_White.png",
                StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - fileName.Length) +
                    "AsphaltTiling_01d.png";
            else if (string.Equals(fileName, "MB_concrete_01d_blue.png",
                StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - fileName.Length) +
                    "MB_concrete_01d.png";

            if (normalized.StartsWith("winter_track/late/", StringComparison.OrdinalIgnoreCase) &&
                normalized.EndsWith("/SleeperNew_d.png", StringComparison.OrdinalIgnoreCase))
                return "winter/SleeperNew_d.png";
            return normalized;
        }

        private static bool FilesEqual(string firstPath, string secondPath)
        {
            var first = new FileInfo(firstPath);
            var second = new FileInfo(secondPath);
            if (!first.Exists || !second.Exists || first.Length != second.Length) return false;
            const int bufferSize = 64 * 1024;
            var firstBuffer = new byte[bufferSize];
            var secondBuffer = new byte[bufferSize];
            using (var firstStream = File.OpenRead(firstPath))
            using (var secondStream = File.OpenRead(secondPath))
            {
                int firstRead;
                while ((firstRead = firstStream.Read(firstBuffer, 0, firstBuffer.Length)) > 0)
                {
                    var secondRead = secondStream.Read(secondBuffer, 0, secondBuffer.Length);
                    if (secondRead != firstRead) return false;
                    for (var i = 0; i < firstRead; i++)
                        if (firstBuffer[i] != secondBuffer[i]) return false;
                }
                return secondStream.ReadByte() == -1;
            }
        }

        private static void VerifyBundleContents(AssetBundle bundle, List<string> expectedAssets)
        {
            var actualAssets = new HashSet<string>(bundle.GetAllAssetNames(),
                StringComparer.OrdinalIgnoreCase);
            if (actualAssets.Count != expectedAssets.Count)
                throw new InvalidOperationException("Built bundle contains " + actualAssets.Count +
                    " assets; expected " + expectedAssets.Count + ".");
            for (var i = 0; i < expectedAssets.Count; i++)
                if (!actualAssets.Contains(expectedAssets[i]))
                    throw new InvalidOperationException("Built bundle is missing: " + expectedAssets[i]);

            foreach (var season in new[] { "spring", "autumn", "winter" })
            {
                var path = AssetRoot + "/Generated/Terrain_" + season + ".asset";
                var array = bundle.LoadAsset<Texture2DArray>(path);
                if (array == null || array.depth != 16)
                    throw new InvalidOperationException("Built bundle has an invalid terrain array: " + path);
            }
        }

        private static void ConfigureTextureImporters()
        {
            var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { AssetRoot });
            for (var i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;
                var isIceNormal = path.EndsWith("/WaterIceNormal.png",
                    StringComparison.OrdinalIgnoreCase);
                var isIceAlbedo = path.EndsWith("/WaterIceAlbedo.png",
                    StringComparison.OrdinalIgnoreCase);
                var isRoadSurface = path.EndsWith("/SidewalkTiles_01d.png",
                        StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("/AsphaltRoad_01d.png", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("/Sidewalk_01d.png", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("/Roads_LOD_01d.png", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("/RoadDetail.png", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("/AsphaltTiling_01d.png", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("/MB_concrete_01d.png", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith("/SnowSurfaceDense.png", StringComparison.OrdinalIgnoreCase);
                isRoadSurface |= path.IndexOf("/winter/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    (Path.GetFileName(path).StartsWith("MB_", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith("/AsphaltTiling_01d_White.png", StringComparison.OrdinalIgnoreCase));
                importer.textureType = isIceNormal
                    ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = !isIceNormal;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = !isIceNormal;
                importer.mipmapEnabled = true;
                var isTerrainTexture = path.IndexOf("TerrainTexture",
                    StringComparison.OrdinalIgnoreCase) >= 0;
                importer.isReadable = isTerrainTexture || isIceNormal;
                importer.wrapMode = isTerrainTexture || isIceNormal || isIceAlbedo || isRoadSurface
                    ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Trilinear;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.maxTextureSize = path.EndsWith("/AsphaltRoad_01d.png",
                    StringComparison.OrdinalIgnoreCase) ? 2048 : 1024;
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
