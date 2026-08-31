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
        private readonly Dictionary<SeasonKind, string> terrainArrays = new Dictionary<SeasonKind, string>();
        private readonly Dictionary<SeasonKind, Texture2DArray> loadedTerrainArrays =
            new Dictionary<SeasonKind, Texture2DArray>();
        private readonly Dictionary<string, Texture2D> loadedTerrainLayers =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly string winterBallastPath;
        private readonly string externalSeasonalRoot;
        private readonly string bundlePath;
        private Texture2D winterBallastTexture;
        private bool winterBallastLoadAttempted;
        private float nextBundleLoadAttempt;
        private bool bundleLoadFailureLogged;

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

        private bool EnsureBundleLoaded()
        {
            if (Bundle != null) return true;
            if (Time.realtimeSinceStartup < nextBundleLoadAttempt) return false;
            nextBundleLoadAttempt = Time.realtimeSinceStartup + 5f;
            loadedTerrainArrays.Clear();
            loadedTerrainLayers.Clear();
            terrainArrays.Clear();
            foreach (var assets in assetsBySeason) assets.Clear();
            try
            {
                Bundle = File.Exists(bundlePath) ? AssetBundle.LoadFromFile(bundlePath) : null;
                if (Bundle != null)
                {
                    var assets = Bundle.GetAllAssetNames();
                    for (var i = 0; i < assets.Length; i++) IndexAsset(assets[i]);
                    bundleLoadFailureLogged = false;
                    Debug.Log("[DVSeasons] Loaded DV99 Unity AssetBundle with " + assets.Length + " assets.");
                    return true;
                }
            }
            catch (Exception exception)
            {
                if (!bundleLoadFailureLogged)
                    Debug.LogWarning("[DVSeasons] Seasonal AssetBundle loading failed: " + exception.Message);
            }
            if (!bundleLoadFailureLogged)
                Debug.LogWarning("[DVSeasons] Seasonal AssetBundle unavailable; will retry: " + bundlePath);
            bundleLoadFailureLogged = true;
            return false;
        }

        public void ResetForSession()
        {
            loadedTerrainArrays.Clear();
            loadedTerrainLayers.Clear();
            if (winterBallastTexture != null) UnityEngine.Object.Destroy(winterBallastTexture);
            winterBallastTexture = null;
            winterBallastLoadAttempted = false;
            nextBundleLoadAttempt = 0f;
            bundleLoadFailureLogged = false;
        }

        public void Dispose()
        {
            ResetForSession();
            if (Bundle != null) Bundle.Unload(true);
            Bundle = null;
        }

        public bool HasCompleteSet(string sourceTextureName)
        {
            return Has(sourceTextureName, SeasonKind.Spring) &&
                Has(sourceTextureName, SeasonKind.Autumn) &&
                Has(sourceTextureName, SeasonKind.Winter);
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
            string assetName;
            if (EnsureBundleLoaded() &&
                assetsBySeason[(int)season].TryGetValue(NormalizeName(sourceTextureName), out assetName))
            {
                var packed = Bundle.LoadAsset<Texture2D>(assetName);
                if (packed != null)
                {
                    pixels = ReadScaledPixels(packed, width, height);
                    if (pixels != null) return true;
                }
            }
            return TryLoadExternalPixels(sourceTextureName, season, width, height, out pixels);
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
            pixels = null;
            var seasonFolder = season.ToString().ToLowerInvariant();
            var fileName = NormalizeName(sourceTextureName) + ".png";
            var path = Path.Combine(externalSeasonalRoot, seasonFolder, fileName);
            if (!File.Exists(path)) return false;

            Texture2D texture = null;
            try
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = "DVSeasons external " + season + " " + sourceTextureName,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 4,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (!texture.LoadImage(File.ReadAllBytes(path), false)) return false;
                pixels = ReadScaledPixels(texture, width, height);
                if (pixels != null)
                    Debug.Log("[DVSeasons] Loaded external " + season +
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
            Texture2D ballast;
            if (TryLoadWinterBallastTexture(out ballast))
            {
                pixels = ReadScaledPixels(ballast, width, height);
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
            if (Bundle == null ||
                !assetsBySeason[(int)season].TryGetValue(sourceName, out assetName))
                return false;
            texture = Bundle.LoadAsset<Texture2D>(assetName);
            if (texture == null) return false;
            texture.hideFlags |= HideFlags.DontUnloadUnusedAsset;
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
            var normalized = NormalizeName(sourceTextureName);
            if (EnsureBundleLoaded() && assetsBySeason[(int)season].ContainsKey(normalized)) return true;
            var seasonFolder = season.ToString().ToLowerInvariant();
            return File.Exists(Path.Combine(externalSeasonalRoot, seasonFolder, normalized + ".png"));
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
            SeasonKind season;
            if (normalized.IndexOf("/spring/", StringComparison.OrdinalIgnoreCase) >= 0) season = SeasonKind.Spring;
            else if (normalized.IndexOf("/autumn/", StringComparison.OrdinalIgnoreCase) >= 0) season = SeasonKind.Autumn;
            else if (normalized.IndexOf("/winter/", StringComparison.OrdinalIgnoreCase) >= 0) season = SeasonKind.Winter;
            else return;
            assetsBySeason[(int)season][NormalizeName(Path.GetFileNameWithoutExtension(normalized))] = assetName;
        }

        private static string NormalizeName(string value)
        {
            var name = (value ?? string.Empty).Trim();
            const string cloneSuffix = " (Clone)";
            if (name.EndsWith(cloneSuffix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - cloneSuffix.Length);
            return name;
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
