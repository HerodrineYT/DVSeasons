using System;
using System.IO;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal static class AutumnLeafParticleTexture
    {
        internal const string AtlasFileName = "autumn_leaf_atlas.png";
        internal const int AtlasSize = 1024;

        public static Texture2D LoadOrCreate(string modPath, string name)
        {
            Texture2D texture;
            return TryLoad(modPath, name, false, out texture) ? texture : Create(name);
        }

        // The authored pixels are baked once by verify_autumn_leaf_atlas.ps1.
        // Runtime only decodes that small ready atlas, with no procedural pixel
        // loop or GPU readback. The owning controller retains it across seasons.
        internal static bool TryLoad(string modPath, string name, bool keepReadable, out Texture2D texture)
        {
            texture = null;
            if (string.IsNullOrEmpty(modPath)) return false;
            var path = Path.Combine(modPath, "Textures", AtlasFileName);
            if (!File.Exists(path)) return false;
            Texture2D loaded = null;
            try
            {
                loaded = new Texture2D(2, 2, TextureFormat.RGBA32, true, false)
                {
                    name = name,
                    filterMode = FilterMode.Trilinear,
                    wrapMode = TextureWrapMode.Clamp,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (!ImageConversion.LoadImage(loaded, File.ReadAllBytes(path), !keepReadable) ||
                    loaded.width != AtlasSize || loaded.height != AtlasSize)
                    throw new InvalidDataException("The leaf atlas must be a 1024 by 1024 RGBA image.");
                texture = loaded;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[DVSeasons] Cached leaf atlas could not be loaded; using the procedural fallback: " +
                    exception.Message);
                if (loaded != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(loaded);
                    else UnityEngine.Object.DestroyImmediate(loaded);
                }
                return false;
            }
        }

        // Columns: maple, oak, birch, beech. Rows: gold, russet, brown, olive.
        private static readonly Vector2[] Maple =
        {
            new Vector2(.50f,.24f), new Vector2(.37f,.34f), new Vector2(.22f,.29f),
            new Vector2(.24f,.42f), new Vector2(.08f,.42f), new Vector2(.12f,.50f),
            new Vector2(.03f,.64f), new Vector2(.23f,.62f), new Vector2(.19f,.74f),
            new Vector2(.30f,.69f), new Vector2(.27f,.87f), new Vector2(.42f,.78f),
            new Vector2(.50f,.97f), new Vector2(.59f,.78f), new Vector2(.73f,.87f),
            new Vector2(.70f,.69f), new Vector2(.81f,.74f), new Vector2(.77f,.62f),
            new Vector2(.97f,.64f), new Vector2(.88f,.50f), new Vector2(.92f,.42f),
            new Vector2(.76f,.42f), new Vector2(.78f,.29f), new Vector2(.63f,.34f)
        };
        private static readonly Vector2[] MapleVeins =
        {
            new Vector2(.04f,.64f), new Vector2(.27f,.87f), new Vector2(.73f,.87f),
            new Vector2(.96f,.64f), new Vector2(.22f,.30f), new Vector2(.78f,.30f)
        };
        private static readonly Color[] Dark =
        {
            new Color(.37f,.23f,.075f), new Color(.30f,.085f,.045f),
            new Color(.26f,.17f,.08f), new Color(.22f,.25f,.08f)
        };
        private static readonly Color[] Light =
        {
            new Color(.73f,.56f,.22f), new Color(.62f,.29f,.12f),
            new Color(.59f,.42f,.23f), new Color(.65f,.55f,.23f)
        };

        public static Texture2D Create(string name) { return CreateCore(name, false); }

        internal static Texture2D CreateReadable(string name) { return CreateCore(name, true); }

        private static Texture2D CreateCore(string name, bool keepReadable)
        {
            const int tile = 256, width = tile * 4, height = tile * 4;
            // Authored in sRGB, with mipmaps for the small leaves in the distance.
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, true, false)
            {
                name = name,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            var pixels = new Color32[width * height];
            var margins = new float[tile * tile];
            var veins = new float[tile * tile];
            for (var species = 0; species < 4; species++)
            {
                for (var y = 0; y < tile; y++) for (var x = 0; x < tile; x++)
                {
                    var p = new Vector2((x + .5f) / tile, (y + .5f) / tile);
                    margins[y * tile + x] = ShapeMargin(species, p);
                    veins[y * tile + x] = VeinDistance(species, p);
                }
                for (var palette = 0; palette < 4; palette++)
                for (var y = 0; y < tile; y++) for (var x = 0; x < tile; x++)
                {
                    var u = (x + .5f) / tile;
                    var v = (y + .5f) / tile;
                    var margin = margins[y * tile + x];
                    var alpha = Mathf.Clamp01(margin * tile + .5f);
                    var stem = Mathf.Clamp01((.008f - Mathf.Abs(u - .5f - .014f * Mathf.Sin(v * 15f))) * tile + .5f) *
                        Smooth(.025f, .04f, v) * (1f - Smooth(.25f, .29f, v));
                    alpha = Mathf.Max(alpha, stem);
                    var mottling = Mathf.PerlinNoise(u * 8f + species * 9f + palette * 3f,
                        v * 11f + palette * 7f + 13f);
                    var grain = Mathf.PerlinNoise(u * 95f + 3f, v * 137f + species * 4f);
                    var pigment = Color.Lerp(Dark[palette], Light[palette], Smooth(.14f, .86f, mottling));
                    pigment *= .85f + grain * .26f;
                    var vein = 1f - Smooth(.001f, .006f, veins[y * tile + x]);
                    pigment = Color.Lerp(pigment, Color.Lerp(Dark[palette], Light[palette], .66f), vein * .8f);
                    var edge = 1f - Smooth(0f, .028f, margin);
                    pigment = Color.Lerp(pigment, Dark[palette] * .72f, edge * .65f);
                    if (v < .17f) pigment = Dark[palette];
                    pigment.a = alpha;
                    pixels[(palette * tile + y) * width + species * tile + x] = pigment;
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(true, !keepReadable);
            return texture;
        }

        private static float ShapeMargin(int species, Vector2 p)
        {
            if (species == 0) return PolygonMargin(p, Maple);
            var t = (p.y - .17f) / .79f;
            if (t <= 0f || t >= 1f) return -1f;
            var side = p.x < .5f ? -.35f : .25f;
            var centre = .5f + .018f * Mathf.Sin(t * 4f);
            var sine = Mathf.Sin(t * Mathf.PI);
            float width;
            if (species == 1)
                width = Mathf.Pow(sine, .58f) * (.21f + .068f * Mathf.Sin(t * 31f + side));
            else if (species == 2)
                width = .64f * (1f - t) * Mathf.Pow(sine, .68f) + .009f * Mathf.Sin(t * 132f + side);
            else
                width = .29f * Mathf.Pow(sine, .82f) + .006f * Mathf.Sin(t * 106f + side);
            return Mathf.Min(width - Mathf.Abs(p.x - centre), Mathf.Min(t, 1f - t) * .79f);
        }

        private static float VeinDistance(int species, Vector2 p)
        {
            var distance = SegmentDistance(p, new Vector2(.5f,.17f), new Vector2(.5f,.96f));
            if (species == 0)
            {
                foreach (var tip in MapleVeins)
                    distance = Mathf.Min(distance, SegmentDistance(p, new Vector2(.5f,.30f), tip));
            }
            else
            {
                var across = Mathf.Abs(p.x - .5f);
                var offset = p.x < .5f ? 0f : .024f;
                for (var i = 0; i < 8; i++)
                {
                    var curve = .21f + i * .084f + offset + across * .65f + across * across * .3f;
                    distance = Mathf.Min(distance, Mathf.Abs(p.y - curve) * .75f);
                }
            }
            return distance;
        }

        private static float PolygonMargin(Vector2 p, Vector2[] polygon)
        {
            var inside = false;
            var distance = 1f;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                var a = polygon[i]; var b = polygon[j];
                if ((a.y > p.y) != (b.y > p.y) &&
                    p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
                distance = Mathf.Min(distance, SegmentDistance(p, a, b));
            }
            return inside ? distance : -distance;
        }

        private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            var delta = b - a;
            return (p - a - delta * Mathf.Clamp01(Vector2.Dot(p - a, delta) / delta.sqrMagnitude)).magnitude;
        }

        private static float Smooth(float from, float to, float value)
        {
            return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(from, to, value));
        }
    }
}
