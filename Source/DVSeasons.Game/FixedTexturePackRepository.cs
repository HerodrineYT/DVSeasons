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
        private readonly Dictionary<string, Texture2D> loadedTextures =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Texture2D> ownedTextures = new List<Texture2D>();
        private readonly HashSet<string> checkedOverrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> validTrackSets =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly string winterBallastPath;
        private readonly string externalSeasonalRoot;
        private readonly string overrideSeasonalRoot;
        private readonly string bundlePath;
        private readonly string winterBundlePath;
        private AssetBundle winterBundle;
        private AssetBundleCreateRequest winterBundleLoadRequest;
        private bool winterBundleLoadFinished;
        private readonly string tracksBundlePath;
        private AssetBundle tracksBundle;
        private AssetBundleCreateRequest tracksBundleLoadRequest;
        private bool tracksBundleLoadFinished;
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
            overrideSeasonalRoot = Path.Combine(modPath ?? string.Empty, "Overrides", "Seasonal");
            bundlePath = Path.Combine(modPath ?? string.Empty, "AssetBundles", "dvseasons_dv99");
            winterBundlePath = Path.Combine(modPath ?? string.Empty, "AssetBundles", "dvseasons_winter");
            tracksBundlePath = Path.Combine(modPath ?? string.Empty, "AssetBundles", "dvseasons_tracks");
            // Do not load a world asset at UMM startup in the main menu. DV unloads
            // AssetBundles between worlds, so every access also checks Unity's
            // destroyed-object state rather than trusting the managed reference.
        }

        public AssetBundle Bundle { get; private set; }
        public int TextureDecodeCount { get; private set; }
        public int PixelReadbackCount { get; private set; }

        public void BeginLoad()
        {
            EnsureWinterBundleLoaded();
            EnsureTracksBundleLoaded();
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
                return (Bundle != null || bundleLoadFinished) && winterBundleLoadFinished && tracksBundleLoadFinished;
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

        private bool EnsureBundleLoaded(bool requireWinter = false)
        {
            var winterReady = EnsureWinterBundleLoaded();
            // Evaluate both independently so all archive I/O can overlap.
            var tracksReady = EnsureTracksBundleLoaded();
            winterReady &= tracksReady;
            if (Bundle != null) return !requireWinter || winterReady;
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
                if (winterBundle != null)
                    foreach (var asset in winterBundle.GetAllAssetNames()) IndexAsset(asset);
                if (tracksBundle != null)
                    foreach (var asset in tracksBundle.GetAllAssetNames()) IndexAsset(asset);
                bundleLoadFailureLogged = false;
                Debug.Log("[DVSeasons] Loaded DV99 Unity AssetBundle with " + assets.Length + " assets.");
                return !requireWinter || winterReady;
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

        private bool EnsureWinterBundleLoaded()
        {
            if (winterBundle != null) return true;
            if (!ReferenceEquals(winterBundle, null))
            {
                winterBundle = null;
                winterBundleLoadFinished = false;
            }
            if (winterBundleLoadRequest == null)
            {
                if (winterBundleLoadFinished) return true;
                if (!File.Exists(winterBundlePath))
                {
                    winterBundleLoadFinished = true;
                    return true;
                }
                try { winterBundleLoadRequest = AssetBundle.LoadFromFileAsync(winterBundlePath); }
                catch (Exception exception)
                {
                    winterBundleLoadFinished = true;
                    Debug.LogWarning("[DVSeasons] Winter texture AssetBundle unavailable: " + exception.Message);
                    return true;
                }
                return false;
            }
            if (!winterBundleLoadRequest.isDone) return false;
            winterBundle = winterBundleLoadRequest.assetBundle;
            winterBundleLoadRequest = null;
            winterBundleLoadFinished = true;
            if (winterBundle == null)
                Debug.LogWarning("[DVSeasons] Winter texture AssetBundle could not be loaded: " + winterBundlePath);
            else
            {
                foreach (var asset in winterBundle.GetAllAssetNames()) IndexAsset(asset);
                Debug.Log("[DVSeasons] Prepared winter texture AssetBundle loaded asynchronously.");
            }
            return true;
        }

        private bool EnsureTracksBundleLoaded()
        {
            if (tracksBundle != null) return true;
            if (!ReferenceEquals(tracksBundle, null))
            {
                tracksBundle = null;
                tracksBundleLoadFinished = false;
            }
            if (tracksBundleLoadRequest == null)
            {
                if (tracksBundleLoadFinished) return true;
                if (!File.Exists(tracksBundlePath))
                {
                    tracksBundleLoadFinished = true;
                    return true;
                }
                try { tracksBundleLoadRequest = AssetBundle.LoadFromFileAsync(tracksBundlePath); }
                catch (Exception exception)
                {
                    tracksBundleLoadFinished = true;
                    Debug.LogWarning("[DVSeasons] Track texture AssetBundle unavailable: " + exception.Message);
                    return true;
                }
                return false;
            }
            if (!tracksBundleLoadRequest.isDone) return false;
            tracksBundle = tracksBundleLoadRequest.assetBundle;
            tracksBundleLoadRequest = null;
            tracksBundleLoadFinished = true;
            if (tracksBundle == null)
                Debug.LogWarning("[DVSeasons] Track texture AssetBundle could not be loaded: " + tracksBundlePath);
            else
            {
                foreach (var asset in tracksBundle.GetAllAssetNames()) IndexAsset(asset);
                Debug.Log("[DVSeasons] Prepared track texture AssetBundle loaded asynchronously.");
            }
            return true;
        }

        private Texture2D LoadPackedTexture(string assetName)
        {
            if (tracksBundle != null && tracksBundle.Contains(assetName))
                return tracksBundle.LoadAsset<Texture2D>(assetName);
            return winterBundle != null && winterBundle.Contains(assetName)
                ? winterBundle.LoadAsset<Texture2D>(assetName)
                : Bundle.LoadAsset<Texture2D>(assetName);
        }

        private void LogBundleUnavailable()
        {
            if (!bundleLoadFailureLogged)
                Debug.LogWarning("[DVSeasons] Seasonal AssetBundle unavailable; will retry: " + bundlePath);
            bundleLoadFailureLogged = true;
        }

        public void ResetForSession()
        {
            loadedTextures.Clear();
            checkedOverrides.Clear();
            validTrackSets.Clear();
            for (var i = 0; i < ownedTextures.Count; i++)
                if (ownedTextures[i] != null) UnityEngine.Object.Destroy(ownedTextures[i]);
            ownedTextures.Clear();
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
            winterBundleLoadFinished = winterBundle != null;
            tracksBundleLoadFinished = tracksBundle != null;
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
            if (winterBundleLoadRequest != null && winterBundleLoadRequest.isDone)
            {
                var pending = winterBundleLoadRequest.assetBundle;
                if (pending != null && pending != winterBundle) pending.Unload(true);
                winterBundleLoadRequest = null;
            }
            if (winterBundle != null) winterBundle.Unload(true);
            winterBundle = null;
            winterBundleLoadFinished = true;
            if (tracksBundleLoadRequest != null && tracksBundleLoadRequest.isDone)
            {
                var pending = tracksBundleLoadRequest.assetBundle;
                if (pending != null && pending != tracksBundle) pending.Unload(true);
                tracksBundleLoadRequest = null;
            }
            if (tracksBundle != null) tracksBundle.Unload(true);
            tracksBundle = null;
            tracksBundleLoadFinished = true;
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
            if (!HasWinterTrack(sourceTextureName, WinterTrackTextureStage.Early) ||
                !HasWinterTrack(sourceTextureName, WinterTrackTextureStage.Middle) ||
                !HasWinterTrack(sourceTextureName, WinterTrackTextureStage.Late)) return false;
            var name = CanonicalTextureName(sourceTextureName);
            bool valid;
            if (validTrackSets.TryGetValue(name, out valid)) return valid;
            // The old eager pixel path rejected a broken custom stage even when
            // the current coverage used only another stage. Keep that fallback,
            // but do not read bundled pixels for unused stages.
            for (var stage = WinterTrackTextureStage.Early; stage <= WinterTrackTextureStage.Late; stage++)
            {
                var index = (int)stage - 1;
                var relative = "winter_track/" + GetWinterTrackStageFolder(stage) + "/" + name + ".png";
                var custom = File.Exists(Path.Combine(overrideSeasonalRoot, relative)) ||
                    (!winterTrackAssets[index].ContainsKey(name) && File.Exists(Path.Combine(externalSeasonalRoot, relative)));
                if (stage == WinterTrackTextureStage.Late && IsSleeperTexture(name))
                {
                    var winterRelative = "winter/" + name + ".png";
                    custom |= File.Exists(Path.Combine(overrideSeasonalRoot, winterRelative)) ||
                        (!assetsBySeason[(int)SeasonKind.Winter].ContainsKey(name) &&
                         File.Exists(Path.Combine(externalSeasonalRoot, winterRelative)));
                }
                Texture2D texture;
                if (custom && !TryLoadWinterTrackTexture(name, stage, out texture))
                {
                    validTrackSets[name] = false;
                    return false;
                }
            }
            validTrackSets[name] = true;
            return true;
        }

        // Returned textures are repository-owned. Consumers must not destroy them.
        // User PNGs live in Overrides/Seasonal; ordinary installations use prepared
        // bundle textures, even when an older mod install left loose PNGs behind.
        public bool TryLoadTexture(string sourceTextureName, SeasonKind season, out Texture2D texture)
        {
            texture = null;
            var index = (int)season;
            if (index < 0 || index >= assetsBySeason.Length) return false;
            var name = CanonicalTextureName(sourceTextureName);
            var relative = season.ToString().ToLowerInvariant() + "/" + name + ".png";
            if (TryLoadProfileTexture(relative, assetsBySeason[index], name, out texture)) return true;
            int layer;
            return TryParseTerrainLayerIndex(name, out layer) && TryGetTerrainLayer(season, layer, out texture);
        }

        public bool TryLoadWinterTrackTexture(string sourceTextureName,
            WinterTrackTextureStage stage, out Texture2D texture)
        {
            texture = null;
            var stageIndex = (int)stage - 1;
            if (stageIndex < 0 || stageIndex >= winterTrackAssets.Length) return false;
            var name = CanonicalTextureName(sourceTextureName);
            var relative = "winter_track/" + GetWinterTrackStageFolder(stage) + "/" + name + ".png";
            if (TryLoadProfileTexture(relative, winterTrackAssets[stageIndex], name, out texture)) return true;
            return stage == WinterTrackTextureStage.Late && IsSleeperTexture(name) &&
                TryLoadTexture(name, SeasonKind.Winter, out texture);
        }

        private bool TryLoadProfileTexture(string relative, Dictionary<string, string> assets,
            string name, out Texture2D texture)
        {
            texture = null;
            if (loadedTextures.TryGetValue(relative, out texture) && texture != null) return true;
            if (checkedOverrides.Add(relative) && TryLoadExternalTexture(
                Path.Combine(overrideSeasonalRoot, relative), name, out texture))
            {
                loadedTextures[relative] = texture;
                return true;
            }
            string assetName;
            if (EnsureBundleLoaded(true) && assets.TryGetValue(name, out assetName))
            {
                texture = LoadPackedTexture(assetName);
                if (texture != null)
                {
                    texture.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    loadedTextures[relative] = texture;
                    return true;
                }
            }
            // Old custom texture packs remain usable if their key is not bundled.
            // Never decode the former distribution PNG while its bundle is loading.
            if (bundleLoadRequest == null && bundleLoadFinished && winterBundleLoadFinished && tracksBundleLoadFinished &&
                TryLoadExternalTexture(Path.Combine(externalSeasonalRoot, relative), name, out texture))
            {
                loadedTextures[relative] = texture;
                return true;
            }
            return false;
        }

        public bool TryLoadWinterTrackPixels(string sourceTextureName,
            WinterTrackTextureStage stage, int width, int height, out Color32[] pixels)
        {
            pixels = null;
            if (width <= 0 || height <= 0) return false;
            var cacheKey = PixelCacheKey("track", (int)stage - 1, sourceTextureName, width, height);
            if (loadedPixelProfiles.TryGetValue(cacheKey, out pixels)) return true;
            Texture2D texture;
            if (!TryLoadWinterTrackTexture(sourceTextureName, stage, out texture)) return false;
            pixels = ReadScaledPixels(texture, width, height);
            if (pixels != null) loadedPixelProfiles[cacheKey] = pixels;
            return pixels != null;
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
            if (width <= 0 || height <= 0) return false;
            var cacheKey = PixelCacheKey("season", (int)season, sourceTextureName, width, height);
            if (loadedPixelProfiles.TryGetValue(cacheKey, out pixels)) return true;
            Texture2D texture;
            if (!TryLoadTexture(sourceTextureName, season, out texture)) return false;
            pixels = ReadScaledPixels(texture, width, height);
            if (pixels != null) loadedPixelProfiles[cacheKey] = pixels;
            return pixels != null;
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
            var atlas = LoadPackedTexture(assetName);
            if (atlas == null || atlas.height <= 0) return false;

            var sourceFrameCount = Mathf.RoundToInt(atlas.width / (float)atlas.height);
            if (sourceFrameCount < 1 || sourceFrameCount > 16 ||
                Mathf.Abs(atlas.width - (sourceFrameCount * atlas.height)) > sourceFrameCount)
                return false;

            var targetFrameWidth = width / targetFrameCount;
            var scaledWidth = targetFrameWidth * sourceFrameCount;
            var viewOffset = StableHash(sourceTextureName) % sourceFrameCount;
            // Generated game billboard names differ, but only their bounded
            // source-view offset affects the atlas. Share each transformed view
            // and the scaled source, never one large pixel array per tree name.
            var cacheKey = PixelCacheKey("bare-billboard", viewOffset,
                "T_Maple_01_Cross_A_T", width, height);
            if (loadedPixelProfiles.TryGetValue(cacheKey, out pixels)) return true;
            var scaledKey = PixelCacheKey("bare-source", 0,
                "T_Maple_01_Cross_A_T", scaledWidth, height);
            Color32[] scaled;
            if (!loadedPixelProfiles.TryGetValue(scaledKey, out scaled))
            {
                scaled = ReadScaledPixels(atlas, scaledWidth, height);
                if (scaled != null) loadedPixelProfiles[scaledKey] = scaled;
            }
            if (scaled == null || scaled.Length != scaledWidth * height) return false;

            pixels = new Color32[width * height];
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
            loadedPixelProfiles[cacheKey] = pixels;
            return true;
        }

        private bool TryLoadExternalTexture(string path, string sourceName, out Texture2D texture)
        {
            texture = null;
            if (!File.Exists(path)) return false;
            try
            {
                var linear = string.Equals(sourceName, "WaterIceNormal", StringComparison.OrdinalIgnoreCase);
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear)
                {
                    name = "DVSeasons override " + sourceName,
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 4,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (!texture.LoadImage(File.ReadAllBytes(path), false))
                {
                    UnityEngine.Object.Destroy(texture);
                    texture = null;
                    return false;
                }
                TextureDecodeCount++;
                ownedTextures.Add(texture);
                Debug.Log("[DVSeasons] Loaded texture override '" + path + "'.");
                return true;
            }
            catch (Exception exception)
            {
                if (texture != null) UnityEngine.Object.Destroy(texture);
                texture = null;
                Debug.LogWarning("[DVSeasons] Texture override '" + path + "' could not be loaded: " + exception.Message);
                return false;
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
            if (TryLoadTexture("WinterBallastBalanced", SeasonKind.Winter, out texture)) return true;
            if (!IsLoadFinished) return false;
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
            // Classification runs while discovering native materials, even in
            // summer. Loading every seasonal array here caused a large startup
            // stall simply to reject a vanilla texture. An array can only have
            // been applied by this repository after TryGetTerrainArray cached it.
            // Use the cache directly, including while another bundle is pending.
            if (texture == null) return false;
            foreach (var seasonal in loadedTerrainArrays.Values)
                if (seasonal != null && seasonal == texture) return true;
            return false;
        }

        private bool Has(string sourceTextureName, SeasonKind season)
        {
            var normalized = CanonicalTextureName(sourceTextureName);
            if (EnsureBundleLoaded() && assetsBySeason[(int)season].ContainsKey(normalized)) return true;
            var seasonFolder = season.ToString().ToLowerInvariant();
            if (File.Exists(Path.Combine(overrideSeasonalRoot, seasonFolder, normalized + ".png"))) return true;
            return File.Exists(Path.Combine(externalSeasonalRoot, seasonFolder, normalized + ".png"));
        }

        private bool HasWinterTrack(string sourceTextureName, WinterTrackTextureStage stage)
        {
            var stageIndex = (int)stage - 1;
            if (stageIndex < 0 || stageIndex >= winterTrackAssets.Length) return false;
            var normalized = CanonicalTextureName(sourceTextureName);
            if (File.Exists(Path.Combine(overrideSeasonalRoot, "winter_track", GetWinterTrackStageFolder(stage), normalized + ".png"))) return true;
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

        private Color32[] ReadScaledPixels(Texture source, int width, int height)
        {
            var packed = source as Texture2D;
            if (packed != null && packed.isReadable)
            {
                // Common 1024 -> 512/256/etc. requests are an exact prepared mip:
                // copy CPU pixels directly, without a render, GPU fence or upload.
                var mip = 0;
                var mipWidth = packed.width;
                var mipHeight = packed.height;
                while (mip + 1 < packed.mipmapCount &&
                    Mathf.Max(1, mipWidth / 2) >= width && Mathf.Max(1, mipHeight / 2) >= height)
                {
                    mip++;
                    mipWidth = Mathf.Max(1, mipWidth / 2);
                    mipHeight = Mathf.Max(1, mipHeight / 2);
                }
                var pixels = packed.GetPixels32(mip);
                if (mipWidth == width && mipHeight == height) return pixels;
                return ResizePixels(pixels, mipWidth, mipHeight, width, height,
                    UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(packed.graphicsFormat) &&
                    QualitySettings.activeColorSpace == ColorSpace.Linear);
            }
            // Compatibility for old non-readable bundles and GPU-only terrain
            // layers. The shipped seasonal/track textures do not enter this path.
            PixelReadbackCount++;
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

        private static readonly float[] LinearChannel = CreateLinearChannels();

        private static float[] CreateLinearChannels()
        {
            var result = new float[256];
            for (var i = 0; i < result.Length; i++) result[i] = Mathf.GammaToLinearSpace(i / 255f);
            return result;
        }

        private static Color32[] ResizePixels(Color32[] source, int sourceWidth, int sourceHeight,
            int width, int height, bool linearSampling)
        {
            var output = new Color32[width * height];
            for (var y = 0; y < height; y++)
            {
                var py = Mathf.Clamp((y + 0.5f) * sourceHeight / height - 0.5f, 0, sourceHeight - 1);
                var y0 = (int)py;
                var y1 = Mathf.Min(y0 + 1, sourceHeight - 1);
                for (var x = 0; x < width; x++)
                {
                    var px = Mathf.Clamp((x + 0.5f) * sourceWidth / width - 0.5f, 0, sourceWidth - 1);
                    var x0 = (int)px;
                    var x1 = Mathf.Min(x0 + 1, sourceWidth - 1);
                    var a = source[y0 * sourceWidth + x0];
                    var b = source[y0 * sourceWidth + x1];
                    var c = source[y1 * sourceWidth + x0];
                    var d = source[y1 * sourceWidth + x1];
                    var fx = px - x0;
                    var fy = py - y0;
                    output[y * width + x] = new Color32(
                        ResizeChannel(a.r,b.r,c.r,d.r,fx,fy,linearSampling),
                        ResizeChannel(a.g,b.g,c.g,d.g,fx,fy,linearSampling),
                        ResizeChannel(a.b,b.b,c.b,d.b,fx,fy,linearSampling),
                        ResizeChannel(a.a,b.a,c.a,d.a,fx,fy,false));
                }
            }
            return output;
        }

        private static byte ResizeChannel(byte a, byte b, byte c, byte d, float x, float y, bool linear)
        {
            if (!linear) return (byte)Mathf.RoundToInt(Mathf.Lerp(Mathf.Lerp(a,b,x),Mathf.Lerp(c,d,x),y));
            var value = Mathf.Lerp(Mathf.Lerp(LinearChannel[a],LinearChannel[b],x),
                Mathf.Lerp(LinearChannel[c],LinearChannel[d],x),y);
            return (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(value) * 255f);
        }
    }
}
