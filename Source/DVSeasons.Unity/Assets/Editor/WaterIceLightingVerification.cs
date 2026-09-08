using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class WaterIceLightingVerification
    {
        public static void Verify()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            var modPath = Path.Combine(root, "artifacts/build/DVSeasons");
            var assembly = Assembly.LoadFrom(Path.Combine(modPath, "DVSeasons.dll"));
            var repositoryType = assembly.GetType("DVSeasons.Mod.SeasonAssetBundleRepository", true);
            var controllerType = assembly.GetType("DVSeasons.Mod.WaterIceController", true);
            var repository = Activator.CreateInstance(repositoryType, new object[] { modPath });
            var controller = Activator.CreateInstance(controllerType, new[] { repository });
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var target = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(16, 16, TextureFormat.RGBA32, false, true);
            var quad = new Mesh
            {
                vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0),
                    new Vector3(1,1,0), new Vector3(-1,1,0) },
                triangles = new[] { 0, 2, 1, 0, 3, 2 }
            };
            var commands = new CommandBuffer();
            var previous = RenderTexture.active;
            try
            {
                controllerType.GetMethod("TryLoadIceAlbedo", flags).Invoke(controller, null);
                var material = (Material)controllerType.GetField("iceOverlayMaterial", flags).GetValue(controller);
                if (material == null || material.shader.name != "DVSeasons/WaterIceOverlay")
                    throw new Exception("Runtime did not load the bundled water shader.");
                material.mainTexture = Texture2D.whiteTexture;
                target.Create();
                // Keep coverage fixed while time changes, including the return to day.
                foreach (var light in new[] { 1f, 0.5f, 0f, 1f })
                {
                    controllerType.GetMethod("DrawIceOverlays", flags).Invoke(controller, new object[] { 1f, light });
                    var tint = material.GetColor("_Color");
                    if (Mathf.Abs(tint.a - 0.34f) > 0.001f)
                        throw new Exception("Lighting changed ice coverage.");
                    commands.Clear();
                    commands.SetRenderTarget(target);
                    commands.ClearRenderTarget(false, true, Color.black);
                    commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                    commands.DrawMesh(quad, Matrix4x4.identity, material);
                    Graphics.ExecuteCommandBuffer(commands);
                    RenderTexture.active = target;
                    readback.ReadPixels(new Rect(0, 0, 16, 16), 0, 0); readback.Apply();
                    var actual = readback.GetPixel(8, 8);
                    var expected = new Color(0.95f * light * 0.34f, 0.98f * light * 0.34f, light * 0.34f);
                    if (Mathf.Abs(actual.r - expected.r) > 0.015f ||
                        Mathf.Abs(actual.g - expected.g) > 0.015f || Mathf.Abs(actual.b - expected.b) > 0.015f)
                        throw new Exception("Water ice lighting GPU mismatch: " + actual + " expected " + expected);
                }
                Debug.Log("DVSeasons water ice lighting verified: runtime shader loading, day/dusk/night/day GPU pixels, fixed coverage, no night emission.");
            }
            finally
            {
                RenderTexture.active = previous;
                commands.Dispose();
                ((IDisposable)controller).Dispose(); ((IDisposable)repository).Dispose();
                UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(readback);
                UnityEngine.Object.DestroyImmediate(quad);
            }
        }
    }
}
