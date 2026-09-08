using System;
using System.IO;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class SeasonAssetBundleRepository : IDisposable
    {
        private readonly Dictionary<string, string>[] assetsBySeason =
        {
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        private readonly Dictionary<string, string>[] winterTrackAssets =
        {
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        private readonly Dictionary<SeasonKind, string> terrainArrays = new Dictionary<SeasonKind, string>();
        private readonly Dictionary<SeasonKind, Texture2DArray> loadedTerrainArrays =
            new Dictionary<SeasonKind, Texture2DArray>();
        private readonly Dictionary<string, Texture2D> loadedTerrainLayers =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Texture2D> generatedTerrainLayers = new List<Texture2D>();
        private readonly Dictionary<string, Color32[]> loadedPixelProfiles =
            new Dictionary<string, Color32[]>(StringComparer.OrdinalIgnoreCase);
        private readonly string winterBallastPath;
        private readonly string externalSeasonalRoot;
        private readonly string bundlePath;
        private Texture2D winterBallastTexture;
        private bool winterBallastLoadAttempted;
        private float nextBundleLoadAttempt;
        private bool bundleLoadFailureLogged;
        private AssetBundleCreateRequest bundleLoadRequest;
        private bool bundleLoadFinished;

        public SeasonAssetBundleRepository(string modPath)
        {
            winterBallastPath = Path.Combine(modPath ?? string.Empty, "Textures", "winter_ballast_balanced.png");
            externalSeasonalRoot = Path.Combine(modPath ?? string.Empty, "Textures", "Seasonal");
            bundlePath = Path.Combine(modPath ?? string.Empty, "AssetBundles", "dvseasons_dv99");
            // Do not load a world asset at UMM startup in the main menu. DV unloads
            // AssetBundles between worlds, so every access also checks Unity's
            // destroyed-object state rather than trusting the managed reference.
        }

        public AssetBundle Bundle { get; private set; }

        public void BeginLoad()
        {
            if (Bundle != null || bundleLoadRequest != null ||
                Time.realtimeSinceStartup < nextBundleLoadAttempt) return;
            nextBundleLoadAttempt = Time.realtimeSinceStartup + 5f;
            bundleLoadFinished = false;
            loadedTerrainArrays.Clear();
            loadedTerrainLayers.Clear();
            terrainArrays.Clear();
            foreach (var assets in assetsBySeason) assets.Clear();
            foreach (var assets in winterTrackAssets) assets.Clear();
            try
            {
                if (File.Exists(bundlePath))
                {
                    bundleLoadRequest = AssetBundle.LoadFromFileAsync(bundlePath);
                    Debug.Log("[DVSeasons] Seasonal AssetBundle loading started asynchronously.");
                    return;
                }
            }
            catch (Exception exception)
            {
                if (!bundleLoadFailureLogged)
                    Debug.LogWarning("[DVSeasons] Seasonal AssetBundle loading failed: " + exception.Message);
            }
            bundleLoadFinished = true;
            LogBundleUnavailable();
        }

        public bool IsLoadFinished
        {
            get
            {
                EnsureBundleLoaded();
                return Bundle != null || bundleLoadFinished;
            }
        }

        public Shader LoadShader(string fileName)
        {
            if (!EnsureBundleLoaded()) return null;
            var suffix = "/shaders/" + fileName + ".shader";
            foreach (var asset in Bundle.GetAllAssetNames())
            {
                if (!asset.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var shader = Bundle.LoadAsset<Shader>(asset);
                if (shader != null && shader.isSupported)
                {
                    shader.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    return shader;
                }
            }
            return null;
        }

        private bool EnsureBundleLoaded()
        {
            if (Bundle != null) return true;
            if (bundleLoadRequest == null)
            {
                BeginLoad();
                return Bundle != null;
            }
            if (!bundleLoadRequest.isDone) return false;
            try
            {
                Bundle = bundleLoadRequest.assetBundle;
                bundleLoadRequest = null;
                bundleLoadFinished = true;
                if (Bundle == null)
                {
                    LogBundleUnavailable();
                    return false;
                }
                var assets = Bundle.GetAllAssetNames();
                for (var i = 0; i < assets.Length; i++) IndexAsset(assets[i]);
                bundleLoadFailureLogged = false;
                Debug.Log("[DVSeasons] Loaded DV99 Unity AssetBundle with " + assets.Length + " assets.");
                return true;
            }
            catch (Exception exception)
            {
                bundleLoadRequest = null;
                bundleLoadFinished = true;
                if (!bundleLoadFailureLogged)
                    Debug.LogWarning("[DVSeasons] Seasonal AssetBundle loading failed: " + exception.Message);
                LogBundleUnavailable();
                return false;
            }
        }

        private void LogBundleUnavailable()
        {
            if (!bundleLoadFailureLogged)
                Debug.LogWarning("[DVSeasons] Seasonal AssetBundle unavailable; will retry: " + bundlePath);
            bundleLoadFailureLogged = true;
        }

        public void ResetForSession()
        {
            loadedTerrainArrays.Clear();
            loadedTerrainLayers.Clear();
            for (var i = 0; i < generatedTerrainLayers.Count; i++)
                if (generatedTerrainLayers[i] != null)
                    UnityEngine.Object.Destroy(generatedTerrainLayers[i]);
            generatedTerrainLayers.Clear();
            loadedPixelProfiles.Clear();
            if (winterBallastTexture != null) UnityEngine.Object.Destroy(winterBallastTexture);
            winterBallastTexture = null;
            winterBallastLoadAttempted = false;
            nextBundleLoadAttempt = 0f;
            bundleLoadFailureLogged = false;
            bundleLoadFinished = Bundle != null;
        }

        public void Dispose()
        {
            ResetForSession();
            if (bundleLoadRequest != null && bundleLoadRequest.isDone)
            {
                var pendingBundle = bundleLoadRequest.assetBundle;
                if (pendingBundle != null && pendingBundle != Bundle) pendingBundle.Unload(true);
                bundleLoadRequest = null;
            }
            if (Bundle != null) Bundle.Unload(true);
            Bundle = null;
            bundleLoadFinished = true;
        }

        public bool HasCompleteSet(string sourceTextureName)
        {
            return Has(sourceTextureName, SeasonKind.Spring) &&
                Has(sourceTextureName, SeasonKind.Autumn) &&
                Has(sourceTextureName, SeasonKind.Winter);
        }

        public bool HasCompleteWinterTrackSet(string sourceTextureName)
        {
            return HasWinterTrack(sourceTextureName, WinterTrackTextureStage.Early) &&
                HasWinterTrack(sourceTextureName, WinterTrackTextureStage.Middle) &&
                HasWinterTrack(sourceTextureName, WinterTrackTextureStage.Late);
        }

        public bool TryLoadWinterTrackPixels(string sourceTextureName,
            WinterTrackTextureStage stage, int width, int height, out Color32[] pixels)
        {
            pixels = null;
            var stageIndex = (int)stage - 1;
            if (stageIndex < 0 || stageIndex >= winterTrackAssets.Length) return false;
            var normalizedName = CanonicalTextureName(sourceTextureName);
            var cacheKey = PixelCacheKey("track", stageIndex, normalizedName, width, height);
            if (loadedPixelProfiles.TryGetValue(cacheKey, out pixels)) return true;

            var stageFolder = GetWinterTrackStageFolder(stage);
            if (stageFolder == null) return false;
            var path = Path.Combine(externalSeasonalRoot, "winter_track", stageFolder,
                normalizedName + ".png");
            if (TryLoadExternalPath(path, "winter track " + stage,
                sourceTextureName, width, height, out pixels))
            {
                loadedPixelProfiles[cacheKey] = pixels;
                return true;
            }

            // The authored full-winter sleeper is byte-identical to the late-stage
            // sleeper. Keeping one canonical PNG avoids four copies while preserving
            // both game texture names and the complete three-stage track profile.
            if (stage == WinterTrackTextureStage.Late && IsSleeperTexture(normalizedName))
            {
                var winterPath = Path.Combine(externalSeasonalRoot, "winter",
                    normalizedName + ".png");
                if (TryLoadExternalPath(winterPath, "winter track " + stage,
                    sourceTextureName, width, height, out pixels))
                {
                    loadedPixelProfiles[cacheKey] = pixels;
                    return true;
                }
            }

            string assetName;
            if (EnsureBundleLoaded() &&
                winterTrackAssets[stageIndex].TryGetValue(
                    normalizedName, out assetName))
            {
                var packed = Bundle.LoadAsset<Texture2D>(assetName);
                if (packed != null)
                {
                    pixels = ReadScaledPixels(packed, width, height);
                    if (pixels != null)
                    {
                        loadedPixelProfiles[cacheKey] = pixels;
                        return true;
                    }
                }
            }

            if (stage == WinterTrackTextureStage.Late && IsSleeperTexture(normalizedName) &&
                EnsureBundleLoaded() && assetsBySeason[(int)SeasonKind.Winter].TryGetValue(
                    normalizedName, out assetName))
            {
                var packed = Bundle.LoadAsset<Texture2D>(assetName);
                if (packed != null)
                {
                    pixels = ReadScaledPixels(packed, width, height);
                    if (pixels != null)
                    {
                        loadedPixelProfiles[cacheKey] = pixels;
                        return true;
                    }
                }
            }

            return false;
        }

        public Color32[] LoadPixels(string sourceTextureName, SeasonKind season, int width, int height)
        {
            Color32[] pixels;
            if (TryLoadPixels(sourceTextureName, season, width, height, out pixels)) return pixels;
            throw new FileNotFoundException("Seasonal texture is absent from the texture pack.", sourceTextureName);
        }

        public bool TryLoadPixels(string sourceTextureName, SeasonKind season, int width, int height,
            out Color32[] pixels)
        {
            pixels = null;
            var normalizedName = CanonicalTextureName(sourceTextureName);
            var cacheKey = PixelCacheKey("season", (int)season, normalizedName, width, height);
            if (loadedPixelProfiles.TryGetValue(cacheKey, out pixels)) return true;
            // Loose PNGs are intentional hotfix/override assets. Prefer them to
            // the packed copy so a corrected atlas does not require the large
            // terrain bundle to be rebuilt for every texture-only update.
            if (TryLoadExternalPixels(sourceTextureName, season, width, height, out pixels))
            {
                loadedPixelProfiles[cacheKey] = pixels;
                return true;
            }
            string assetName;
            if (EnsureBundleLoaded() &&
                assetsBySeason[(int)season].TryGetValue(normalizedName, out assetName))
            {
                var packed = Bundle.LoadAsset<Texture2D>(assetName);
                if (packed != null)
                {
                    pixels = ReadScaledPixels(packed, width, height);
                    if (pixels != null)
                    {
                        loadedPixelProfiles[cacheKey] = pixels;
                        return true;
                    }
                }
            }

            int terrainLayerIndex;
            Texture2D terrainLayer;
            if (TryParseTerrainLayerIndex(normalizedName, out terrainLayerIndex) &&
                TryGetTerrainLayer(season, terrainLayerIndex, out terrainLayer))
            {
                pixels = ReadScaledPixels(terrainLayer, width, height);
                if (pixels != null)
                {
                    loadedPixelProfiles[cacheKey] = pixels;
                    return true;
                }
            }
            return false;
        }

        public bool TryLoadBareTreeBillboardPixels(string sourceTextureName, int width, int height,
            out Color32[] pixels)
        {
            pixels = null;
            if (!EnsureBundleLoaded() || width <= 0 || height <= 0) return false;

            var targetFrameCount = Mathf.RoundToInt(width / (float)height);
            if (targetFrameCount < 2 || targetFrameCount > 16 ||
                Mathf.Abs(width - (targetFrameCount * height)) > targetFrameCount)
                return false;

            string assetName;
            if (!assetsBySeason[(int)SeasonKind.Winter].TryGetValue(
                NormalizeName("T_Maple_01_Cross_A_T"), out assetName))
                return false;
            var atlas = Bundle.LoadAsset<Texture2D>(assetName);
            if (atlas == null || atlas.height <= 0) return false;

            var sourceFrameCount = Mathf.RoundToInt(atlas.width / (float)atlas.height);
            if (sourceFrameCount < 1 || sourceFrameCount > 16 ||
                Mathf.Abs(atlas.width - (sourceFrameCount * atlas.height)) > sourceFrameCount)
                return false;

            var targetFrameWidth = width / targetFrameCount;
            var scaledWidth = targetFrameWidth * sourceFrameCount;
            var scaled = ReadScaledPixels(atlas, scaledWidth, height);
            if (scaled == null || scaled.Length != scaledWidth * height) return false;

            pixels = new Color32[width * height];
            var viewOffset = StableHash(sourceTextureName) % sourceFrameCount;
            for (var targetFrame = 0; targetFrame < targetFrameCount; targetFrame++)
            {
                // The pack contains four real camera views while the game's generated
                // billboard atlas contains eight. Duplicate the nearest source view
                // instead of stretching all four trees across the whole strip.
                var sourceFrame = (((targetFrame * sourceFrameCount) / targetFrameCount) +
                    viewOffset) % sourceFrameCount;
                for (var y = 0; y < height; y++)
                {
                    Array.Copy(scaled, (y * scaledWidth) + (sourceFrame * targetFrameWidth),
                        pixels, (y * width) + (targetFrame * targetFrameWidth),
                        targetFrameWidth);
                }
            }
            return true;
        }

        private bool TryLoadExternalPixels(string sourceTextureName, SeasonKind season,
            int width, int height, out Color32[] pixels)
        {
            var seasonFolder = season.ToString().ToLowerInvariant();
            var fileName = CanonicalTextureName(sourceTextureName) + ".png";
            var path = Path.Combine(externalSeasonalRoot, seasonFolder, fileName);
            return TryLoadExternalPath(path, season.ToString(), sourceTextureName,
                width, height, out pixels);
        }

        private static bool TryLoadExternalPath(string path, string profileName,
            string sourceTextureName, int width, int height, out Color32[] pixels)
        {
            pixels = null;
            if (!File.Exists(path)) return false;

            Texture2D texture = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = "DVSeasons external " + profileName + " " + sourceTextureName,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 4,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (!texture.LoadImage(File.ReadAllBytes(path), false)) return false;
                pixels = ReadScaledPixels(texture, width, height);
                if (pixels != null)
                    Debug.Log("[DVSeasons] Loaded external " + profileName +
                        " texture '" + sourceTextureName + "'.");
                return pixels != null;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[DVSeasons] External seasonal texture '" + path +
                    "' could not be loaded: " + exception.Message);
                return false;
            }
            finally
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
        }

        public bool TryLoadGenericWinterSnowPixels(int width, int height, out Color32[] pixels)
        {
            var cacheKey = PixelCacheKey("generic", 0, "winter_ballast", width, height);
            if (loadedPixelProfiles.TryGetValue(cacheKey, out pixels)) return true;
            Texture2D ballast;
            if (TryLoadWinterBallastTexture(out ballast))
            {
                pixels = ReadScaledPixels(ballast, width, height);
                if (pixels != null) loadedPixelProfiles[cacheKey] = pixels;
                return pixels != null;
            }
            return TryLoadPixels("TerrainTexture2", SeasonKind.Winter, width, height, out pixels);
        }

        private bool TryLoadWinterBallastTexture(out Texture2D texture)
        {
            if (!winterBallastLoadAttempted)
            {
                winterBallastLoadAttempted = true;
                try
                {
                    if (File.Exists(winterBallastPath))
                    {
                        var loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                        {
                            name = "DVSeasons winter ballast",
                            wrapMode = TextureWrapMode.Repeat,
                            filterMode = FilterMode.Trilinear,
                            anisoLevel = 4,
                            hideFlags = HideFlags.HideAndDontSave
                        };
                        if (loaded.LoadImage(File.ReadAllBytes(winterBallastPath), false))
                            winterBallastTexture = loaded;
                        else
                            UnityEngine.Object.Destroy(loaded);
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[DVSeasons] Winter ballast texture could not be loaded: " +
                        exception.Message);
                }
            }
            texture = winterBallastTexture;
            return texture != null;
        }

        public bool TryGetTerrainArray(SeasonKind season, out Texture2DArray array)
        {
            array = null;
            if (!EnsureBundleLoaded()) return false;
            if (loadedTerrainArrays.TryGetValue(season, out array) && array != null) return true;
            string assetName;
            if (Bundle == null || !terrainArrays.TryGetValue(season, out assetName)) return false;
            array = Bundle.LoadAsset<Texture2DArray>(assetName);
            if (array == null) return false;
            array.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            loadedTerrainArrays[season] = array;
            return true;
        }

        public bool TryGetTerrainLayer(SeasonKind season, int layerIndex, out Texture2D texture)
        {
            texture = null;
            if (layerIndex < 0 || layerIndex >= 16) return false;
            if (!EnsureBundleLoaded()) return false;
            var sourceName = "TerrainTexture" + (layerIndex + 1);
            var cacheKey = ((int)season) + "|" + sourceName;
            if (loadedTerrainLayers.TryGetValue(cacheKey, out texture) && texture != null)
                return true;

            string assetName;
            if (Bundle != null &&
                assetsBySeason[(int)season].TryGetValue(sourceName, out assetName))
            {
                texture = Bundle.LoadAsset<Texture2D>(assetName);
                if (texture == null) return false;
                texture.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                loadedTerrainLayers[cacheKey] = texture;
                return true;
            }

            // Terrain arrays already contain the exact RGBA pixels of all sixteen
            // source layers. Material-free Unity terrains need Texture2D objects,
            // so create those with a GPU-to-GPU copy instead of shipping another
            // 48 Texture2D assets or synchronously reading the array back to the CPU.
            Texture2DArray array;
            if (!TryGetTerrainArray(season, out array) || array == null ||
                layerIndex >= array.depth)
                return false;
            try
            {
                texture = new Texture2D(array.width, array.height, array.format,
                    array.mipmapCount > 1, false)
                {
                    name = "DVSeasons Terrain " + season + " layer " + (layerIndex + 1),
                    wrapMode = array.wrapMode,
                    filterMode = array.filterMode,
                    anisoLevel = array.anisoLevel,
                    hideFlags = HideFlags.HideAndDontSave
                };
                for (var mip = 0; mip < array.mipmapCount; mip++)
                    Graphics.CopyTexture(array, layerIndex, mip, texture, 0, mip);
                generatedTerrainLayers.Add(texture);
            }
            catch (Exception exception)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                texture = null;
                Debug.LogWarning("[DVSeasons] Terrain layer " + (layerIndex + 1) +
                    " could not be copied from the " + season + " array: " + exception.Message);
                return false;
            }
            loadedTerrainLayers[cacheKey] = texture;
            return true;
        }

        public bool IsTerrainArray(Texture texture)
        {
            if (texture == null || !EnsureBundleLoaded()) return false;
            foreach (var season in terrainArrays.Keys)
            {
                Texture2DArray seasonal;
                if (TryGetTerrainArray(season, out seasonal) && seasonal == texture) return true;
            }
            return false;
        }

        private bool Has(string sourceTextureName, SeasonKind season)
        {
            var normalized = CanonicalTextureName(sourceTextureName);
            if (EnsureBundleLoaded() && assetsBySeason[(int)season].ContainsKey(normalized)) return true;
            var seasonFolder = season.ToString().ToLowerInvariant();
            return File.Exists(Path.Combine(externalSeasonalRoot, seasonFolder, normalized + ".png"));
        }

        private bool HasWinterTrack(string sourceTextureName, WinterTrackTextureStage stage)
        {
            var stageIndex = (int)stage - 1;
            if (stageIndex < 0 || stageIndex >= winterTrackAssets.Length) return false;
            var normalized = CanonicalTextureName(sourceTextureName);
            if (EnsureBundleLoaded() && winterTrackAssets[stageIndex].ContainsKey(normalized))
                return true;
            if (stage == WinterTrackTextureStage.Late && IsSleeperTexture(normalized))
            {
                if (EnsureBundleLoaded() &&
                    assetsBySeason[(int)SeasonKind.Winter].ContainsKey(normalized)) return true;
                if (File.Exists(Path.Combine(externalSeasonalRoot, "winter", normalized + ".png")))
                    return true;
            }
            var stageFolder = GetWinterTrackStageFolder(stage);
            return stageFolder != null && File.Exists(Path.Combine(externalSeasonalRoot,
                "winter_track", stageFolder, normalized + ".png"));
        }

        private void IndexAsset(string assetName)
        {
            if (string.IsNullOrEmpty(assetName)) return;
            var normalized = assetName.Replace('\\', '/');
            if (normalized.EndsWith("terrain_spring.asset", StringComparison.OrdinalIgnoreCase))
            {
                terrainArrays[SeasonKind.Spring] = assetName;
                return;
            }
            if (normalized.EndsWith("terrain_autumn.asset", StringComparison.OrdinalIgnoreCase))
            {
                terrainArrays[SeasonKind.Autumn] = assetName;
                return;
            }
            if (normalized.EndsWith("terrain_winter.asset", StringComparison.OrdinalIgnoreCase))
            {
                terrainArrays[SeasonKind.Winter] = assetName;
                return;
            }
            if (!normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;
            WinterTrackTextureStage winterTrackStage;
            if (normalized.IndexOf("/winter_track/early/", StringComparison.OrdinalIgnoreCase) >= 0)
                winterTrackStage = WinterTrackTextureStage.Early;
            else if (normalized.IndexOf("/winter_track/middle/", StringComparison.OrdinalIgnoreCase) >= 0)
                winterTrackStage = WinterTrackTextureStage.Middle;
            else if (normalized.IndexOf("/winter_track/late/", StringComparison.OrdinalIgnoreCase) >= 0)
                winterTrackStage = WinterTrackTextureStage.Late;
            else
                winterTrackStage = WinterTrackTextureStage.SnowFree;
            if (winterTrackStage != WinterTrackTextureStage.SnowFree)
            {
                IndexPreferred(winterTrackAssets[(int)winterTrackStage - 1],
                    Path.GetFileNameWithoutExtension(normalized), assetName);
                return;
            }
            SeasonKind season;
            if (normalized.IndexOf("/spring/", StringComparison.OrdinalIgnoreCase) >= 0) season = SeasonKind.Spring;
            else if (normalized.IndexOf("/autumn/", StringComparison.OrdinalIgnoreCase) >= 0) season = SeasonKind.Autumn;
            else if (normalized.IndexOf("/winter/", StringComparison.OrdinalIgnoreCase) >= 0) season = SeasonKind.Winter;
            else return;
            IndexPreferred(assetsBySeason[(int)season],
                Path.GetFileNameWithoutExtension(normalized), assetName);
        }

        private static void IndexPreferred(Dictionary<string, string> assets,
            string sourceName, string assetName)
        {
            var normalized = NormalizeName(sourceName);
            var canonical = CanonicalTextureName(normalized);
            string existing;
            if (!assets.TryGetValue(canonical, out existing) ||
                string.Equals(normalized, canonical, StringComparison.OrdinalIgnoreCase))
                assets[canonical] = assetName;
        }

        private static string GetWinterTrackStageFolder(WinterTrackTextureStage stage)
        {
            switch (stage)
            {
                case WinterTrackTextureStage.Early: return "early";
                case WinterTrackTextureStage.Middle: return "middle";
                case WinterTrackTextureStage.Late: return "late";
                default: return null;
            }
        }

        private static string NormalizeName(string value)
        {
            var name = (value ?? string.Empty).Trim();
            const string cloneSuffix = " (Clone)";
            if (name.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - cloneSuffix.Length);
            return name;
        }

        private static string CanonicalTextureName(string value)
        {
            var name = NormalizeName(value);
            if (string.Equals(name, "SleeperOld_d", StringComparison.OrdinalIgnoreCase))
                return "SleeperNew_d";
            if (string.Equals(name, "AsphaltTiling_01d_White", StringComparison.OrdinalIgnoreCase))
                return "AsphaltTiling_01d";
            if (string.Equals(name, "MB_concrete_01d_blue", StringComparison.OrdinalIgnoreCase))
                return "MB_concrete_01d";
            return name;
        }

        private static bool IsSleeperTexture(string name)
        {
            return string.Equals(CanonicalTextureName(name), "SleeperNew_d",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseTerrainLayerIndex(string name, out int layerIndex)
        {
            layerIndex = -1;
            const string prefix = "TerrainTexture";
            if (string.IsNullOrEmpty(name) ||
                !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            int oneBased;
            if (!int.TryParse(name.Substring(prefix.Length), out oneBased) ||
                oneBased < 1 || oneBased > 16) return false;
            layerIndex = oneBased - 1;
            return true;
        }

        private static string PixelCacheKey(string group, int variant, string name,
            int width, int height)
        {
            return group + "|" + variant + "|" + CanonicalTextureName(name) + "|" +
                width + "x" + height;
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                var hash = 17;
                value = value ?? string.Empty;
                for (var i = 0; i < value.Length; i++) hash = (hash * 31) + value[i];
                return hash & 0x7fffffff;
            }
        }

        private static Color32[] ReadScaledPixels(Texture source, int width, int height)
        {
            var temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            var previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                readable.Apply(false, false);
                return readable.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
                if (readable != null) UnityEngine.Object.Destroy(readable);
            }
        }
    }
}
