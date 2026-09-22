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
        private static readonly HashSet<string> PreparedWinterTextureNames =
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
                "WaterIceNormal.png",
                "Coal_01d.png",
                "WinterBallastBalanced.png"
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
            if (sourceTextures.Count != 173)
                throw new InvalidOperationException("Expected 173 source Texture2D assets, found " +
                    sourceTextures.Count);
            ValidatePreparedTextureMirrors(sourceTextures);

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
            if (shaderAssets.Count != 11)
                throw new InvalidOperationException("Expected 11 Shader assets, found " +
                    shaderAssets.Count);
            assets.AddRange(shaderAssets);
            assets.Add(AssetRoot + "/Generated/Terrain_spring.asset");
            assets.Add(AssetRoot + "/Generated/Terrain_autumn.asset");
            assets.Add(AssetRoot + "/Generated/Terrain_winter.asset");

            var winterAssets = new List<string>();
            var trackAssets = new List<string>();
            foreach (var path in sourceTextures)
            {
                if (!IsPreparedTexture(path)) continue;
                var relative = path.Substring(AssetRoot.Length + 1);
                if (string.Equals(relative, GetCanonicalRuntimeRelativePath(relative), StringComparison.OrdinalIgnoreCase))
                {
                    if (relative.StartsWith("winter_track/", StringComparison.OrdinalIgnoreCase)) trackAssets.Add(path);
                    else winterAssets.Add(path);
                }
            }
            if (winterAssets.Count != 22 || trackAssets.Count != 20)
                throw new InvalidOperationException("Expected 22 prepared winter and 20 track textures, found " + winterAssets.Count + "/" + trackAssets.Count);
            var definitions = new[]
            {
                new UnityEditor.AssetBundleBuild { assetBundleName = BundleName, assetNames = assets.ToArray() },
                new UnityEditor.AssetBundleBuild { assetBundleName = "dvseasons_winter", assetNames = winterAssets.ToArray() },
                new UnityEditor.AssetBundleBuild { assetBundleName = "dvseasons_tracks", assetNames = trackAssets.ToArray() }
            };
            var manifest = BuildWithRuntimeInstancing(output, definitions);
            if (manifest == null || !File.Exists(Path.Combine(output, BundleName)))
                throw new InvalidOperationException("Unity did not produce " + BundleName);
            foreach (var shaderPath in shaderAssets)
                if (ShaderUtil.ShaderHasError(AssetDatabase.LoadAssetAtPath<Shader>(shaderPath)))
                {
                    foreach(var error in ShaderUtil.GetShaderMessages(AssetDatabase.LoadAssetAtPath<Shader>(shaderPath)))
                        Debug.LogError("SHADER_BUILD_ERROR: "+error.message+" at "+error.file+":"+error.line+" ("+error.platform+")");
                    throw new InvalidOperationException("Shader compilation failed: " + shaderPath);
                }
            var verifiedBundle = AssetBundle.LoadFromFile(Path.Combine(output, BundleName));
            if (verifiedBundle == null) throw new InvalidOperationException("Cannot reopen built bundle.");
            try
            {
                VerifyBundleContents(verifiedBundle, assets);
                IceShaderVerification.Verify(verifiedBundle);
                WinterWindowVerification.Verify(verifiedBundle);
                AutumnLeafVerification.Verify(verifiedBundle);
                SpringTerrainVerification.Verify(verifiedBundle);
                SnowGlareVerification.Verify(verifiedBundle);
                SnowAmbientCopyVerification.Verify(verifiedBundle);
                SnowProceduralCostVerification.Verify(verifiedBundle);
                VehicleSideSnowVerification.Verify(verifiedBundle);
                SnowFullInstancingVerification.Verify(verifiedBundle.LoadAsset<Shader>("assets/dvseasons/dv99/shaders/snowvehicle.shader"));
                SnowDustVerification.Verify(verifiedBundle);
            }
            finally { verifiedBundle.Unload(true); }
            VerifyPreparedBundle(output, "dvseasons_winter", winterAssets);
            VerifyPreparedBundle(output, "dvseasons_tracks", trackAssets);
            Debug.Log("DVSeasons AssetBundle built: " + Path.Combine(output, BundleName) +
                " (75 main textures + 22 prepared winter + 20 track textures + 11 shaders + 3 terrain arrays)");
        }

        private static AssetBundleManifest BuildWithRuntimeInstancing(string output, UnityEditor.AssetBundleBuild[] definitions)
        {
            // Snow exclusion materials enable instancing at runtime. Unity
            // cannot infer that from scene materials when stripping a bundle.
            // Keep its variant for this build and restore the user's settings,
            // including the exact on-disk bytes if the build saved them.
            const string path = "ProjectSettings/GraphicsSettings.asset";
            var originalBytes = File.ReadAllBytes(path);
            var objects = AssetDatabase.LoadAllAssetsAtPath(path);
            if (objects.Length == 0) throw new InvalidOperationException("Cannot read Unity graphics settings.");
            var settings = new SerializedObject(objects[0]);
            var stripping = settings.FindProperty("m_InstancingStripping");
            if (stripping == null) throw new InvalidOperationException("Unity instancing stripping setting is unavailable.");
            int keepAll = Array.FindIndex(stripping.enumNames,
                name => string.Equals(name.Replace(" ", string.Empty), "KeepAll", StringComparison.OrdinalIgnoreCase));
            if (keepAll < 0) throw new InvalidOperationException("Unity does not expose the KeepAll instancing mode.");
            int original = stripping.intValue;
            bool wasDirty = EditorUtility.IsDirty(objects[0]);
            const string playerPath = "ProjectSettings/ProjectSettings.asset";
            var playerBytes = File.ReadAllBytes(playerPath);
            var stereoPath = PlayerSettings.stereoRenderingPath;
#pragma warning disable 618
            var vrSupported = PlayerSettings.GetVirtualRealitySupported(BuildTargetGroup.Standalone);
            var vrSdks = PlayerSettings.GetVirtualRealitySDKs(BuildTargetGroup.Standalone);
#pragma warning restore 618
            try
            {
                stripping.enumValueIndex = keepAll;
                settings.ApplyModifiedPropertiesWithoutUndo();
                // Unity strips its built-in stereo keyword even when a shader
                // explicitly declares multi_compile, unless XR is enabled for
                // the build target. Keep both mono and the game's OpenVR
                // double-wide programs in the distributable AssetBundle.
#pragma warning disable 618
                PlayerSettings.SetVirtualRealitySDKs(BuildTargetGroup.Standalone, new[] { "None", "OpenVR" });
                PlayerSettings.SetVirtualRealitySupported(BuildTargetGroup.Standalone, true);
#pragma warning restore 618
                PlayerSettings.stereoRenderingPath = StereoRenderingPath.SinglePass;
                return BuildPipeline.BuildAssetBundles(output, definitions,
                    BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.DeterministicAssetBundle |
                    BuildAssetBundleOptions.ForceRebuildAssetBundle,
                    BuildTarget.StandaloneWindows64);
            }
            finally
            {
                settings.Update();
                settings.FindProperty("m_InstancingStripping").intValue = original;
                settings.ApplyModifiedPropertiesWithoutUndo();
                if (!wasDirty) EditorUtility.ClearDirty(objects[0]);
                File.WriteAllBytes(path, originalBytes);
#pragma warning disable 618
                PlayerSettings.SetVirtualRealitySupported(BuildTargetGroup.Standalone, vrSupported);
                PlayerSettings.SetVirtualRealitySDKs(BuildTargetGroup.Standalone, vrSdks);
#pragma warning restore 618
                PlayerSettings.stereoRenderingPath = stereoPath;
                File.WriteAllBytes(playerPath, playerBytes);
            }
        }

        private static void VerifyPreparedBundle(string output, string bundleName, List<string> assets)
        {
            var preparedBundle = AssetBundle.LoadFromFile(Path.Combine(output, bundleName));
            if (preparedBundle == null) throw new InvalidOperationException("Cannot reopen prepared winter bundle.");
            try
            {
                var names = preparedBundle.GetAllAssetNames();
                if (names.Length != assets.Count) throw new InvalidOperationException("Prepared texture bundle asset count differs: " + bundleName);
                foreach (var path in assets)
                {
                    var texture = preparedBundle.LoadAsset<Texture2D>(path);
                    if (texture == null || !texture.isReadable || texture.mipmapCount < 2 ||
                        (texture.format != TextureFormat.RGBA32 && texture.format != TextureFormat.RGB24))
                        throw new InvalidOperationException("Prepared winter texture must retain readable RGBA pixels and mipmaps: " + path);
                }
            }
            finally { preparedBundle.Unload(true); }
        }

        private static bool ShouldPackTexture(string path)
        {
            return !IsTerrainSourceTexture(path) && !IsPreparedTexture(path);
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

        private static bool IsPreparedTexture(string path)
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
                PreparedWinterTextureNames.Contains(fileName))
                return true;
            return false;
        }

        private static void ValidatePreparedTextureMirrors(List<string> sourceTextures)
        {
            var repositoryRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            var runtimeRoot = Path.Combine(repositoryRoot,
                RuntimeSeasonalTextureRoot.Replace('/', Path.DirectorySeparatorChar));
            var validated = 0;
            var canonicalRuntimePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < sourceTextures.Count; i++)
            {
                var sourceAssetPath = sourceTextures[i];
                if (!IsPreparedTexture(sourceAssetPath)) continue;
                var relativePath = sourceAssetPath.Substring(AssetRoot.Length + 1);
                var runtimeRelativePath = GetCanonicalRuntimeRelativePath(relativePath);
                var sourcePath = Path.Combine(Application.dataPath,
                    sourceAssetPath.Substring("Assets/".Length)
                        .Replace('/', Path.DirectorySeparatorChar));
                var runtimePath = Path.Combine(runtimeRoot,
                    runtimeRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (runtimeRelativePath == "winter/WinterBallastBalanced.png")
                    runtimePath = Path.Combine(repositoryRoot, "Resources/Runtime/Textures/winter_ballast_balanced.png");
                // Source archives retain one authoring copy under Assets. The old
                // loose runtime mirrors need not be shipped just to rebuild it.
                if (!Directory.Exists(runtimeRoot))
                    runtimePath = Path.Combine(Application.dataPath, "DVSeasons/DV99", runtimeRelativePath);
                if (!File.Exists(runtimePath))
                    throw new InvalidOperationException("Loose runtime texture is missing: " + runtimePath);
                if (!FilesEqual(sourcePath, runtimePath))
                    throw new InvalidOperationException("Loose runtime texture differs from its Unity source: " +
                        relativePath + " -> " + runtimeRelativePath);
                canonicalRuntimePaths.Add(runtimeRelativePath);
                validated++;
            }
            if (validated != 50)
                throw new InvalidOperationException("Expected 50 prepared texture sources, found " +
                    validated);
            if (canonicalRuntimePaths.Count != 42)
                throw new InvalidOperationException("Expected 42 canonical prepared runtime textures, found " +
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
                var prepared = IsPreparedTexture(path);
                // These were lossless loose PNGs. Store raw RGBA pixels, including
                // the RGB normal, so bundling introduces no compression/swizzle loss.
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = !isIceNormal;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = !prepared && !isIceNormal;
                importer.mipmapEnabled = true;
                var isTerrainTexture = path.IndexOf("TerrainTexture",
                    StringComparison.OrdinalIgnoreCase) >= 0;
                importer.isReadable = true;
                importer.wrapMode = prepared || isTerrainTexture || isIceNormal || isIceAlbedo || isRoadSurface
                    ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Trilinear;
                importer.anisoLevel = 4;
                importer.textureCompression = prepared ? TextureImporterCompression.Uncompressed : TextureImporterCompression.CompressedHQ;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.maxTextureSize = prepared ? 8192 : path.EndsWith("/AsphaltRoad_01d.png",
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
