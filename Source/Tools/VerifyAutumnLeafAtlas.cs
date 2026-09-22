using System;
using System.Diagnostics;
using System.IO;
using DVSeasons.Mod;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class VerifyAutumnLeafAtlas
{
    private static void Require(bool value, string message)
    { if (!value) throw new Exception(message); }

    public static void Run(string root, bool bake)
    {
        var runtime = Path.Combine(root, "Resources/Runtime");
        var path = Path.Combine(runtime, "Textures", AutumnLeafParticleTexture.AtlasFileName);
        Texture2D reference = null, loaded = null, runtimeTexture = null, fallback = null, tiny = null;
        try
        {
            var timer = Stopwatch.StartNew();
            reference = AutumnLeafParticleTexture.CreateReadable("Leaf atlas procedural reference");
            double generationMs = timer.Elapsed.TotalMilliseconds;
            if (bake)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(reference));
            }
            timer.Restart();
            Require(AutumnLeafParticleTexture.TryLoad(runtime, "Leaf atlas cached reference", true, out loaded),
                "Ready leaf atlas missing or invalid");
            double readableLoadMs = timer.Elapsed.TotalMilliseconds;
            Require(loaded.mipmapCount == reference.mipmapCount && loaded.mipmapCount == 11,
                "Atlas mip chain changed");
            long compared = 0;
            for (int mip = 0; mip < reference.mipmapCount; mip++)
            {
                var expected = reference.GetPixels32(mip);
                var actual = loaded.GetPixels32(mip);
                Require(expected.Length == actual.Length, "Atlas mip dimensions changed");
                for (int i = 0; i < expected.Length; i++)
                {
                    var a = expected[i]; var b = actual[i];
                    Require(a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a,
                        "Atlas pixel changed at mip " + mip + ", pixel " + i);
                }
                compared += expected.Length;
            }
            timer.Restart();
            runtimeTexture = AutumnLeafParticleTexture.LoadOrCreate(runtime, "Leaf atlas runtime");
            double runtimeLoadMs = timer.Elapsed.TotalMilliseconds;
            Require(runtimeTexture.width == 1024 && runtimeTexture.height == 1024 &&
                runtimeTexture.mipmapCount == 11 && !runtimeTexture.isReadable &&
                runtimeTexture.filterMode == FilterMode.Trilinear && runtimeTexture.wrapMode == TextureWrapMode.Clamp,
                "Runtime atlas settings changed");

            var absent = Path.Combine(root, "artifacts/verification/leaf-atlas-missing");
            Texture2D rejected;
            Require(!AutumnLeafParticleTexture.TryLoad(absent, "Missing", false, out rejected) && rejected == null,
                "Missing atlas should use fallback");
            var invalid = Path.Combine(root, "artifacts/verification/leaf-atlas-invalid");
            Directory.CreateDirectory(Path.Combine(invalid, "Textures"));
            tiny = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            File.WriteAllBytes(Path.Combine(invalid, "Textures", AutumnLeafParticleTexture.AtlasFileName),
                ImageConversion.EncodeToPNG(tiny));
            Require(!AutumnLeafParticleTexture.TryLoad(invalid, "Invalid", false, out rejected) && rejected == null,
                "Wrong-sized atlas should use fallback");
            fallback = AutumnLeafParticleTexture.LoadOrCreate(absent, "Leaf procedural fallback");
            Require(fallback.width == 1024 && fallback.height == 1024 && fallback.mipmapCount == 11 && !fallback.isReadable,
                "Procedural fallback unavailable");
            Debug.Log("LEAF_ATLAS_CACHE_OK: exact pixels across " + reference.mipmapCount + " mip levels, " + compared +
                " pixels; PNG bytes=" + new FileInfo(path).Length + "; generation-ms=" + generationMs.ToString("F2") +
                "; readable-load-ms=" + readableLoadMs.ToString("F2") + "; runtime-load-ms=" + runtimeLoadMs.ToString("F2") +
                "; missing/invalid fallback passed.");
        }
        finally
        {
            foreach (var texture in new[] { reference, loaded, runtimeTexture, fallback, tiny })
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
