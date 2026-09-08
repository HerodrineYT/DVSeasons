using System;
using System.Collections.Generic;
using DV.WeatherSystem;
using DVSeasons.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using WetStuffComponent = PlaceholderSoftware.WetStuff.WetStuff;

namespace DVSeasons.Mod
{
    /// <summary>
    /// Keeps vanilla puddles, surface wetness and all albedo colours intact.
    /// Ice changes only roughness in the screen-space WetStuff mask. No world-space helper
    /// geometry is created, so puddle ice cannot intersect roofs, walls or vehicles.
    /// </summary>
    internal sealed class WinterPuddleController : IDisposable
    {
        private const string HarmonyId = "Herodrine.DVSeasons.WinterPuddles";
        private const string IceTextureAssetName = "WaterIceAlbedo";
        private const string PuddleShaderName = "Hidden/DVSeasons/PuddleIceGBuffer";
        private const string PuddleShaderFileName = "PuddleIceGBuffer";
        private const float ScanInterval = 2f;
        private const float ResourceRetryInterval = 5f;
        private const float IceTileSize = 3.5f;
        private const int TextureSize = 1024;

        private static readonly int PuddleSmoothnessId =
            Shader.PropertyToID("_PuddleSmoothness");
        private static readonly int SpecularCopyId =
            Shader.PropertyToID("_DVOriginalSpecular");
        private static readonly int IceTextureId = Shader.PropertyToID("_IceTex");
        private static readonly int IceAmountId = Shader.PropertyToID("_IceAmount");
        private static readonly int TileSizeId = Shader.PropertyToID("_TileSize");
        private static readonly int InverseViewProjectionId =
            Shader.PropertyToID("_DVInverseViewProjection");


        private sealed class WetStuffBinding
        {
            internal WetStuffComponent Component;
            internal Camera Camera;
            internal Action<CommandBuffer> Handler;
        }

        private readonly SeasonAssetBundleRepository texturePack;
        private readonly Harmony harmony;
        private readonly Dictionary<int, WetStuffBinding> bindings =
            new Dictionary<int, WetStuffBinding>();
        private readonly List<int> staleBindingIds = new List<int>();

        private Texture2D iceTexture;
        private Material gbufferMaterial;
        private Mesh fullscreenQuad;
        private float originalPuddleSmoothness;
        private float activeAmount;
        private float nextResourceLoadAttempt;
        private float nextScanTime;
        private bool smoothnessCaptured;
        private bool missingResourcesLogged;
        private bool disposed;

        private static WinterPuddleController activeController;

        public WinterPuddleController(SeasonAssetBundleRepository texturePack)
        {
            this.texturePack = texturePack ?? throw new ArgumentNullException(nameof(texturePack));
            activeController = this;
            harmony = new Harmony(HarmonyId);

            var target = AccessTools.Method(typeof(PuddleSettings),
                nameof(PuddleSettings.UploadSettings));
            if (target != null)
            {
                harmony.Patch(target, null, new HarmonyMethod(AccessTools.Method(
                    typeof(WinterPuddleController), nameof(PuddleSettingsUploadPostfix))));
            }
            else
            {
                Debug.LogWarning("[DVSeasons] Winter puddle upload hook is unavailable.");
            }
        }

        public void Apply(float iceAmount, bool enabled)
        {
            if (disposed) return;
            activeAmount = enabled ? Mathf.Clamp01(iceAmount) : 0f;
            if (activeAmount <= 0.001f)
            {
                Restore();
                return;
            }

            TryLoadResources();
            if (iceTexture == null || gbufferMaterial == null || fullscreenQuad == null)
                return;

            CapturePuddleSmoothness();
            ApplyPuddleSmoothness();
            if (Time.realtimeSinceStartup >= nextScanTime)
            {
                ScanWetStuffCameras();
                nextScanTime = Time.realtimeSinceStartup + ScanInterval;
            }
        }

        public void ResetForSession()
        {
            activeAmount = 0f;
            Restore();
            if (iceTexture != null) UnityEngine.Object.Destroy(iceTexture);
            if (gbufferMaterial != null) UnityEngine.Object.Destroy(gbufferMaterial);
            if (fullscreenQuad != null) UnityEngine.Object.Destroy(fullscreenQuad);
            iceTexture = null;
            gbufferMaterial = null;
            fullscreenQuad = null;
            smoothnessCaptured = false;
            missingResourcesLogged = false;
            nextResourceLoadAttempt = 0f;
            nextScanTime = 0f;
        }

        public void Dispose()
        {
            if (disposed) return;
            ResetForSession();
            if (activeController == this) activeController = null;
            harmony.UnpatchAll(HarmonyId);
            disposed = true;
        }

        private void TryLoadResources()
        {
            if (iceTexture != null && gbufferMaterial != null && fullscreenQuad != null)
                return;
            if (Time.realtimeSinceStartup < nextResourceLoadAttempt) return;
            nextResourceLoadAttempt = Time.realtimeSinceStartup + ResourceRetryInterval;

            if (iceTexture == null)
            {
                Color32[] pixels;
                if (texturePack.TryLoadPixels(IceTextureAssetName, SeasonKind.Winter,
                    TextureSize, TextureSize, out pixels))
                {
                    iceTexture = new Texture2D(TextureSize, TextureSize,
                        TextureFormat.RGBA32, true, false)
                    {
                        name = "DVSeasons Screen-Space Puddle Ice",
                        wrapMode = TextureWrapMode.Repeat,
                        filterMode = FilterMode.Trilinear,
                        anisoLevel = 4,
                        hideFlags = HideFlags.HideAndDontSave
                    };
                    iceTexture.SetPixels32(pixels);
                    iceTexture.Apply(true, false);
                }
            }

            if (gbufferMaterial == null)
            {
                var shader = FindBundledShader(PuddleShaderName, PuddleShaderFileName);
                if (shader != null)
                {
                    gbufferMaterial = new Material(shader)
                    {
                        name = "DVSeasons Puddle Ice GBuffer",
                        hideFlags = HideFlags.HideAndDontSave
                    };
                }
            }
            if (fullscreenQuad == null) fullscreenQuad = CreateFullscreenQuad();

            if (iceTexture != null && gbufferMaterial != null && fullscreenQuad != null)
            {
                if (missingResourcesLogged)
                    Debug.Log("[DVSeasons] Winter puddle GBuffer resources became available.");
                missingResourcesLogged = false;
                return;
            }

            if (!missingResourcesLogged)
            {
                Debug.LogWarning("[DVSeasons] Screen-space winter puddle ice resources are " +
                    "unavailable; vanilla puddles remain unchanged.");
                missingResourcesLogged = true;
            }
        }

        private Shader FindBundledShader(string shaderName, string fileName)
        {
            return texturePack.LoadShader(fileName);
        }

        private void ScanWetStuffCameras()
        {
            // WetStuff is a camera component. Avoid a global resource scan every
            // two seconds; cameras activated later are picked up on the next poll.
            var cameras = Camera.allCameras;
            var added = 0;
            for (var i = 0; i < cameras.Length; i++)
            {
                var component = cameras[i].GetComponent<WetStuffComponent>();
                if (component == null || !component.gameObject.scene.IsValid() ||
                    !component.gameObject.scene.isLoaded) continue;
                var id = component.GetInstanceID();
                if (bindings.ContainsKey(id)) continue;

                var camera = component.GetComponent<Camera>();
                if (camera == null || camera.actualRenderingPath != RenderingPath.DeferredShading)
                    continue;
                camera.depthTextureMode |= DepthTextureMode.Depth;
                var binding = new WetStuffBinding
                {
                    Component = component,
                    Camera = camera
                };
                binding.Handler = commandBuffer => RecordPuddleIce(binding, commandBuffer);
                component.AfterDecalRender += binding.Handler;
                bindings.Add(id, binding);
                added++;
            }

            staleBindingIds.Clear();
            foreach (var pair in bindings)
                if (pair.Value.Component == null || pair.Value.Camera == null)
                    staleBindingIds.Add(pair.Key);
            for (var i = 0; i < staleBindingIds.Count; i++)
            {
                WetStuffBinding stale;
                if (!bindings.TryGetValue(staleBindingIds[i], out stale)) continue;
                if (stale.Component != null)
                    stale.Component.AfterDecalRender -= stale.Handler;
                bindings.Remove(staleBindingIds[i]);
            }

            if (added > 0)
                Debug.Log("[DVSeasons] Bound " + added +
                    " WetStuff deferred camera(s) for geometry-free puddle ice.");
        }

        private void RecordPuddleIce(WetStuffBinding binding, CommandBuffer commandBuffer)
        {
            if (disposed || activeAmount <= 0.001f || binding.Camera == null ||
                iceTexture == null || gbufferMaterial == null || fullscreenQuad == null)
                return;

            var camera = binding.Camera;
            var projection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            var inverseViewProjection = (projection * camera.worldToCameraMatrix).inverse;

            commandBuffer.GetTemporaryRT(SpecularCopyId, -1, -1, 0,
                FilterMode.Point, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            commandBuffer.Blit(BuiltinRenderTextureType.GBuffer1, SpecularCopyId);
            commandBuffer.SetGlobalTexture(SpecularCopyId, SpecularCopyId);
            commandBuffer.SetGlobalTexture(IceTextureId, iceTexture);
            commandBuffer.SetGlobalFloat(IceAmountId, Smooth(activeAmount));
            commandBuffer.SetGlobalFloat(TileSizeId, IceTileSize);
            commandBuffer.SetGlobalMatrix(InverseViewProjectionId, inverseViewProjection);
            // Never bind/write GBuffer0: wet vehicles and buildings must retain
            // their exact native albedo, with no winter blue/grey colour filter.
            commandBuffer.SetRenderTarget(BuiltinRenderTextureType.GBuffer1);
            commandBuffer.DrawMesh(fullscreenQuad, Matrix4x4.identity,
                gbufferMaterial, 0, 0);
            commandBuffer.ReleaseTemporaryRT(SpecularCopyId);
        }

        private void CapturePuddleSmoothness(bool refresh = false)
        {
            if (smoothnessCaptured && !refresh) return;
            originalPuddleSmoothness = Shader.GetGlobalFloat(PuddleSmoothnessId);
            smoothnessCaptured = true;
        }

        private void ApplyPuddleSmoothness()
        {
            if (!smoothnessCaptured || activeAmount <= 0.001f) return;
            Shader.SetGlobalFloat(PuddleSmoothnessId,
                Mathf.Lerp(originalPuddleSmoothness,
                    0.82f, Smooth(activeAmount)));
        }

        private void Restore()
        {
            UnbindWetStuffCameras();
            if (!smoothnessCaptured) return;
            var settings = Resources.FindObjectsOfTypeAll<PuddleSettings>();
            if (settings.Length > 0 && settings[0] != null)
                settings[0].UploadSettings();
            else
                Shader.SetGlobalFloat(PuddleSmoothnessId, originalPuddleSmoothness);
            smoothnessCaptured = false;
        }

        private void UnbindWetStuffCameras()
        {
            foreach (var binding in bindings.Values)
                if (binding.Component != null)
                    binding.Component.AfterDecalRender -= binding.Handler;
            bindings.Clear();
        }

        private static Mesh CreateFullscreenQuad()
        {
            var mesh = new Mesh
            {
                name = "DVSeasons Puddle Ice Fullscreen Quad",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    new Vector3(-1f, -1f, 0f),
                    new Vector3(1f, 1f, 0f),
                    new Vector3(1f, -1f, 0f),
                    new Vector3(-1f, 1f, 0f)
                },
                uv = new[]
                {
                    new Vector2(0f, 0f), new Vector2(1f, 1f),
                    new Vector2(1f, 0f), new Vector2(0f, 1f)
                },
                triangles = new[] { 0, 1, 2, 1, 0, 3 }
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static float Smooth(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - (2f * value));
        }

        private static void PuddleSettingsUploadPostfix()
        {
            var controller = activeController;
            if (controller == null || controller.disposed ||
                controller.activeAmount <= 0.001f) return;
            controller.CapturePuddleSmoothness(true);
            controller.ApplyPuddleSmoothness();
        }
    }
}
