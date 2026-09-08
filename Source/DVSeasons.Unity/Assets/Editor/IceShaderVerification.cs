using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    // Offscreen regression checks, run by the bundle builder without starting DV.
    public static class IceShaderVerification
    {
        public static void Verify(AssetBundle bundle)
        {
            const int size = 16;
            var shader = bundle.LoadAsset<Shader>(
                "assets/dvseasons/dv99/shaders/puddleicegbuffer.shader");
            var water = bundle.LoadAsset<Shader>(
                "assets/dvseasons/dv99/shaders/watericeoverlay.shader");
            if (shader == null || water == null || !shader.isSupported || !water.isSupported)
                throw new InvalidOperationException("Bundled ice shaders are missing or unsupported.");

            var material = new Material(shader);
            var original = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            var mask = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            var diffuse = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            var specular = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Linear);
            var quad = new Mesh
            {
                vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0),
                    new Vector3(1,1,0), new Vector3(-1,1,0) },
                uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up },
                triangles = new[] { 0, 2, 1, 0, 3, 2 }
            };
            var commands = new CommandBuffer();
            var previous = RenderTexture.active;
            var oldDiffuse = Shader.GetGlobalTexture("_DVOriginalDiffuse");
            var oldSpecular = Shader.GetGlobalTexture("_DVOriginalSpecular");
            var oldMask = Shader.GetGlobalTexture("_WetDecalSaturationMask");
            var oldDepth = Shader.GetGlobalTexture("_CameraDepthTexture");
            try
            {
                original.filterMode = mask.filterMode = FilterMode.Point;
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    // Asymmetric input detects vertical flipping as well as changes
                    // outside the wet mask. The whole left half must remain exact.
                    original.SetPixel(x, y, new Color(0.15f + y * 0.025f, 0.2f, 0.3f, 0.4f));
                    mask.SetPixel(x, y, x > size / 2 ? Color.white : Color.black);
                }
                original.Apply(); mask.Apply(); diffuse.Create(); specular.Create();
                foreach (var amount in new[] { 0f, 0.5f, 1f })
                {
                    commands.Clear();
                    commands.SetGlobalTexture("_DVOriginalDiffuse", original);
                    commands.SetGlobalTexture("_DVOriginalSpecular", original);
                    commands.SetGlobalTexture("_WetDecalSaturationMask", mask);
                    commands.SetGlobalTexture("_CameraDepthTexture", Texture2D.whiteTexture);
                    commands.SetGlobalTexture("_IceTex", Texture2D.whiteTexture);
                    commands.SetGlobalFloat("_IceAmount", amount);
                    commands.SetGlobalFloat("_TileSize", 3.5f);
                    commands.SetGlobalMatrix("_DVInverseViewProjection", Matrix4x4.identity);
                    // Preload target so discarded dry pixels retain native data.
                    commands.Blit(original, specular);
                    commands.SetRenderTarget(specular);
                    commands.DrawMesh(quad, Matrix4x4.identity, material);
                    Graphics.ExecuteCommandBuffer(commands);
                    RenderTexture.active = specular;
                    var readback = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
                    try
                    {
                        readback.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                        readback.Apply();
                        for (var y = 1; y < size - 1; y++)
                        for (var x = 1; x < size - 1; x++)
                        {
                            var source = original.GetPixel(x, y);
                            var actual = readback.GetPixel(x, y);
                            var expected = source;
                            if (x > size / 2) expected.a = Mathf.Lerp(source.a, 0.86f, amount);
                            if (Mathf.Abs(actual.r - expected.r) > 0.015f ||
                                Mathf.Abs(actual.g - expected.g) > 0.015f ||
                                Mathf.Abs(actual.b - expected.b) > 0.015f ||
                                Mathf.Abs(actual.a - expected.a) > 0.015f)
                                throw new InvalidOperationException("Puddle GBuffer GPU check failed at " +
                                    x + "," + y + ", amount=" + amount + ": " + actual + " != " + expected);
                        }
                    }
                    finally { UnityEngine.Object.DestroyImmediate(readback); }
                }
                Debug.Log("DVSeasons GPU checks passed: bundled shaders supported; puddle mask, " +
                    "UV orientation, unchanged dry pixels and specular RGB, zero/half/full roughness coverage. No albedo target.");
            }
            finally
            {
                RenderTexture.active = previous;
                Shader.SetGlobalTexture("_DVOriginalDiffuse", oldDiffuse);
                Shader.SetGlobalTexture("_DVOriginalSpecular", oldSpecular);
                Shader.SetGlobalTexture("_WetDecalSaturationMask", oldMask);
                Shader.SetGlobalTexture("_CameraDepthTexture", oldDepth);
                commands.Dispose();
                UnityEngine.Object.DestroyImmediate(material);
                UnityEngine.Object.DestroyImmediate(original);
                UnityEngine.Object.DestroyImmediate(mask);
                UnityEngine.Object.DestroyImmediate(quad);
                UnityEngine.Object.DestroyImmediate(diffuse);
                UnityEngine.Object.DestroyImmediate(specular);
            }
        }
    }
}
