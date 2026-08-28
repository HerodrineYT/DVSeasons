using System;
using System.IO;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class SeasonAssetBundleRepository
    {
        private readonly Dictionary<string, string>[] assetsBySeason =
        {
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
        private readonly Dictionary<SeasonKind, string> terrainArrays = new Dictionary<SeasonKind, string>();

        public SeasonAssetBundleRepository(string modPath)
        {
            var path = Path.Combine(modPath ?? string.Empty, "AssetBundles", "dvseasons_dv99");
            Bundle = File.Exists(path) ? AssetBundle.LoadFromFile(path) : null;
            if (Bundle == null)
            {
                Debug.LogError("[DVSeasons] Seasonal AssetBundle could not be loaded: " + path);
                return;
            }

            var assets = Bundle.GetAllAssetNames();
            for (var i = 0; i < assets.Length; i++) IndexAsset(assets[i]);
            Debug.Log("[DVSeasons] Loaded DV99 Unity AssetBundle with " + assets.Length + " assets.");
        }

        public AssetBundle Bundle { get; private set; }

        public bool HasCompleteSet(string sourceTextureName)
        {
            return Has(sourceTextureName, SeasonKind.Spring) &&
                Has(sourceTextureName, SeasonKind.Autumn) &&
                Has(sourceTextureName, SeasonKind.Winter);
        }

        public Color32[] LoadPixels(string sourceTextureName, SeasonKind season, int width, int height)
        {
            string assetName;
            if (Bundle == null || !assetsBySeason[(int)season].TryGetValue(NormalizeName(sourceTextureName), out assetName))
                throw new FileNotFoundException("Seasonal texture is absent from the Unity AssetBundle.", sourceTextureName);

            var packed = Bundle.LoadAsset<Texture2D>(assetName);
            if (packed == null) throw new InvalidDataException("Unity could not load Texture2D asset " + assetName);
            return ReadScaledPixels(packed, width, height);
        }

        public bool TryLoadPixels(string sourceTextureName, SeasonKind season, int width, int height,
            out Color32[] pixels)
        {
            pixels = null;
            string assetName;
            if (Bundle == null || !assetsBySeason[(int)season].TryGetValue(NormalizeName(sourceTextureName), out assetName))
                return false;
            var packed = Bundle.LoadAsset<Texture2D>(assetName);
            if (packed == null) return false;
            pixels = ReadScaledPixels(packed, width, height);
            return pixels != null;
        }

        public bool TryLoadGenericWinterSnowPixels(int width, int height, out Color32[] pixels)
        {
            return TryLoadPixels("TerrainTexture2", SeasonKind.Winter, width, height, out pixels);
        }

        public bool TryGetTerrainArray(SeasonKind season, out Texture2DArray array)
        {
            array = null;
            string assetName;
            if (Bundle == null || !terrainArrays.TryGetValue(season, out assetName)) return false;
            array = Bundle.LoadAsset<Texture2DArray>(assetName);
            return array != null;
        }

        private bool Has(string sourceTextureName, SeasonKind season)
        {
            return Bundle != null && assetsBySeason[(int)season].ContainsKey(NormalizeName(sourceTextureName));
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
