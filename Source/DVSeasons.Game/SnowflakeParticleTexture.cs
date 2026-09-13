using System;
using System.IO;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal static class SnowflakeParticleTexture
    {
        public const float MinimumSize = .05f;
        public const float MaximumSize = .17f;

        public static Texture2D LoadOrCreate(string modPath, out bool usesAtlas)
        {
            var atlasPath = Path.Combine(modPath, "Textures", "snowflake_variations.png");
            var path = File.Exists(atlasPath)
                ? atlasPath
                : Path.Combine(modPath, "Textures", "snowflake_realistic.png");
            usesAtlas = string.Equals(path, atlasPath, StringComparison.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                Texture2D loaded = null;
                try
                {
                    loaded = new Texture2D(2, 2, TextureFormat.RGBA32, true)
                    {
                        name = "DVSeasons Irregular Snow Flake",
                        filterMode = FilterMode.Trilinear,
                        wrapMode = TextureWrapMode.Clamp,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    if (ImageConversion.LoadImage(loaded, File.ReadAllBytes(path), true)) return loaded;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[DVSeasons] Realistic snow particle texture could not be loaded: " +
                        exception.Message);
                }
                if (loaded != null) UnityEngine.Object.Destroy(loaded);
            }
            usesAtlas = false;
            return CreateFallbackSnowTexture();
        }

        private static Texture2D CreateFallbackSnowTexture()
        {
            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "DVSeasons Six-Arm Snowflake",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            var pixels = new Color[size * size];
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = ((x + 0.5f) / size * 2f) - 1f;
                var dy = ((y + 0.5f) / size * 2f) - 1f;
                var radius = Mathf.Sqrt((dx * dx) + (dy * dy));
                var alpha = Mathf.Clamp01((0.16f - radius) * 9f);
                for (var arm = 0; arm < 3; arm++)
                {
                    var angle = arm * Mathf.PI / 3f;
                    var along = Mathf.Abs((dx * Mathf.Cos(angle)) + (dy * Mathf.Sin(angle)));
                    var across = Mathf.Abs((-dx * Mathf.Sin(angle)) + (dy * Mathf.Cos(angle)));
                    var line = Mathf.Clamp01((0.052f - across) * 24f) *
                        Mathf.Clamp01((0.94f - along) * 8f);
                    alpha = Mathf.Max(alpha, line);

                    for (var branch = 1; branch <= 2; branch++)
                    {
                        var branchOrigin = branch * 0.28f;
                        var branchLength = 0.24f;
                        var localAlong = along - branchOrigin;
                        if (localAlong < 0f || localAlong > branchLength) continue;
                        var branchAcross = Mathf.Abs(across - (localAlong * 0.58f));
                        var branchLine = Mathf.Clamp01((0.045f - branchAcross) * 25f) *
                            Mathf.Clamp01((branchLength - localAlong) * 10f);
                        alpha = Mathf.Max(alpha, branchLine);
                    }
                }
                alpha *= Mathf.Clamp01((1f - radius) * 5f);
                pixels[(y * size) + x] = new Color(0.92f, 0.97f, 1f, alpha);
            }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

    }
}
