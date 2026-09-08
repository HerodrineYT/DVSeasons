using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    // Exercises the actual compiled, chunked runtime compositor without starting DV.
    public static class SurfaceSnowVerification
    {
        public static void Verify()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            var modPath = Path.Combine(root, "artifacts/build/DVSeasons");
            var assembly = Assembly.LoadFrom(Path.Combine(modPath, "DVSeasons.dll"));
            var core = Assembly.LoadFrom(Path.Combine(modPath, "DVSeasons.Core.dll"));
            var repositoryType = assembly.GetType("DVSeasons.Mod.SeasonAssetBundleRepository", true);
            var setType = assembly.GetType("DVSeasons.Mod.SeasonalTextureController+SeasonalTextureSet", true);
            var categoryType = assembly.GetType("DVSeasons.Mod.SeasonalTextureController+TextureCategory", true);
            var seasonType = core.GetType("DVSeasons.Core.SeasonKind", true);
            var stateType = core.GetType("DVSeasons.Core.SeasonState", true);
            var repository = Activator.CreateInstance(repositoryType, new object[] { modPath });
            var sheet = new Texture2D(512, 256, TextureFormat.RGBA32, false);
            try
            {
                var row = 0;
                foreach (var name in new[] { "MB_rooftile_red_01d", "MB_concrete_01d" })
                {
                    var source = new Texture2D(128, 128, TextureFormat.RGBA32, false) { name = name };
                    var original = new Color32[128 * 128];
                    for (var i = 0; i < original.Length; i++)
                        original[i] = new Color32((byte)(60 + i % 47), 65, 60, (byte)(i % 256));
                    source.SetPixels32(original); source.Apply();
                    var set = Activator.CreateInstance(setType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new[] { (object)source, Enum.Parse(categoryType, "RoadSurface"), 128, repository, false }, null);
                    try
                    {
                        var snapshots = new Color32[4][];
                        var amounts = new[] { 0f, 0.28f, 0.62f, 1f };
                        double previousBrightness = 0;
                        for (var step = 0; step < 8; step++)
                        {
                            var index = step < 4 ? step : 7 - step;
                            var thaw = step >= 4;
                            var state = Activator.CreateInstance(stateType, new object[] { 3.0,
                                Enum.Parse(seasonType, thaw ? "Winter" : "Autumn"),
                                Enum.Parse(seasonType, thaw ? "Spring" : "Winter"), 0.5f, amounts[index], -5f, 0f });
                            var calls = 0;
                            do
                            {
                                setType.GetMethod("UpdateChunk").Invoke(set, new[] { state, (object)1f, step, 257 });
                                if (++calls > 1000) throw new Exception("Snow chunk update failed to finish.");
                            } while ((bool)setType.GetProperty("HasPendingUpdate").GetValue(set, null));
                            var output = (Texture2D)setType.GetProperty("Output").GetValue(set, null);
                            if (output == null) throw new Exception("Snow output missing.");
                            var pixels = output.GetPixels32();
                            var baseline = (Color32[])setType.GetField("basePixels", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(set);
                            for (var i = 0; i < pixels.Length; i++)
                            {
                                if (pixels[i].a != baseline[i].a) throw new Exception("Snow changed source alpha.");
                                if (index == 0 && !pixels[i].Equals(baseline[i])) throw new Exception("Snow-free source not restored.");
                                if (thaw && !pixels[i].Equals(snapshots[index][i])) throw new Exception("Thaw differs from accumulation.");
                            }
                            if (!thaw)
                            {
                                double brightness = 0;
                                foreach (var pixel in pixels) brightness += pixel.r;
                                brightness /= pixels.Length;
                                if (index > 0 && brightness < previousBrightness + 15)
                                    throw new Exception("Snow stages did not visibly increase coverage.");
                                if (index == 3 && brightness < 185)
                                    throw new Exception("Full winter surface remains too dark.");
                                previousBrightness = brightness;
                                snapshots[index] = pixels;
                                // Preview ignores alpha, which is deliberately varied in the test.
                                var opaque = (Color32[])pixels.Clone();
                                for (var i = 0; i < opaque.Length; i++) opaque[i].a = 255;
                                sheet.SetPixels32(index * 128, row * 128, 128, 128, opaque);
                            }
                        }
                    }
                    finally { setType.GetMethod("Dispose").Invoke(set, null); UnityEngine.Object.DestroyImmediate(source); }
                    row++;
                }
                sheet.Apply();
                var directory = Path.Combine(root, "artifacts/verification/0.2.12");
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, "surface-snow-stages.png"), sheet.EncodeToPNG());
                Debug.Log("DVSeasons surface snow verified: real runtime chunks, 0/28/62/100%, exact thaw, unchanged alpha and restored originals.");
            }
            finally { ((IDisposable)repository).Dispose(); UnityEngine.Object.DestroyImmediate(sheet); }
        }
    }
}
