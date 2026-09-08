using System;
using System.Collections.Generic;
using System.IO;
using DVSeasons.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    /// <summary>
    /// Turns DV99's world-lake material into a static, cold-coloured ice surface.
    /// The controller deliberately targets the exact BadDog/BGWater material instead
    /// of similarly named water effects used by locomotives, splashes and UI.
    /// </summary>
    internal sealed class WaterIceController : IDisposable
    {
        internal const string IceNormalAssetName = "WaterIceNormal";
        internal const string IceAlbedoAssetName = "WaterIceAlbedo";

        private const float TextureEnableThreshold = 0.12f;
        private const float TextureDisableThreshold = 0.04f;
        private const float ScanIntervalSeconds = 5f;
        private const int FallbackTextureSize = 512;
        private const float IceWorldTileSize = 22f;

        private static readonly Color IceBaseColor = new Color(0.43f, 0.57f, 0.65f, 1f);
        private static readonly Color IceMuddyColor = new Color(0.24f, 0.32f, 0.36f, 1f);

        private sealed class WaterMaterialRecord
        {
            public Material Material;
            public Texture MainWave;
            public Texture SecondWave;
            public Vector4 MainWaveTilingOffset;
            public Vector4 SecondWaveTilingOffset;
            public Color WaterBaseColor;
            public Color WaterMuddyColor;
            public float MainWaveBumpScale;
            public float SecondWaveBumpScale;
            public float WaterDistortScale;
            public float Roughness;
            public float SsrIntensity;
            public float ReflectionMin;
            public float ReflectionMax;
            public float SpecularIntensity;
            public bool IceTextureApplied;
            public bool WasModified;
            public float LastAmount = -1f;
        }

        private sealed class WaterRendererRecord
        {
            public MeshRenderer Renderer;
            public Mesh Mesh;
            public int Submesh;
            public bool OwnsMesh;
        }

        private readonly SeasonAssetBundleRepository texturePack;
        private readonly IncrementalSceneScan<MeshRenderer> waterScan;
        private readonly Dictionary<int, WaterMaterialRecord> materials =
            new Dictionary<int, WaterMaterialRecord>();
        private readonly Dictionary<long, WaterRendererRecord> renderers =
            new Dictionary<long, WaterRendererRecord>();

        private Texture2D iceNormal;
        private Texture2D iceAlbedo;
        private Material iceOverlayMaterial;
        private bool ownsIceNormal;
        private bool missingNormalLogged;
        private bool missingAlbedoLogged;
        private float nextNormalLoadAttempt;
        private float nextAlbedoLoadAttempt;

        public WaterIceController(SeasonAssetBundleRepository texturePack)
        {
            this.texturePack = texturePack ?? throw new ArgumentNullException(nameof(texturePack));
            waterScan=new IncrementalSceneScan<MeshRenderer>(ScanIntervalSeconds,192,BindWaterRenderer);
        }

        /// <param name="iceAmount">
        /// Normalized winter coverage. SeasonState.SnowAmount is the intended input,
        /// so save loading and multiplayer use the same host-authoritative value.
        /// </param>
        public void Apply(float iceAmount, float lightFactor)
        {
            iceAmount = Mathf.Clamp01(iceAmount);
            if (iceAmount > 0.001f && iceNormal == null) TryLoadIceNormal();
            if (iceAmount > 0.001f && iceAlbedo == null) TryLoadIceAlbedo();
            if(iceAmount>0.001f) waterScan.Step();

            foreach (var record in materials.Values)
                Apply(record, iceAmount);
            DrawIceOverlays(iceAmount, lightFactor);
        }

        public void ResetForSession()
        {
            foreach (var record in materials.Values) Restore(record);
            materials.Clear();

            foreach (var record in renderers.Values)
                if (record.OwnsMesh && record.Mesh != null)
                    UnityEngine.Object.Destroy(record.Mesh);

            if (ownsIceNormal && iceNormal != null) UnityEngine.Object.Destroy(iceNormal);
            if (iceAlbedo != null) UnityEngine.Object.Destroy(iceAlbedo);
            if (iceOverlayMaterial != null) UnityEngine.Object.Destroy(iceOverlayMaterial);
            iceNormal = null;
            iceAlbedo = null;
            iceOverlayMaterial = null;
            ownsIceNormal = false;
            missingNormalLogged = false;
            missingAlbedoLogged = false;
            nextNormalLoadAttempt = 0f;
            nextAlbedoLoadAttempt = 0f;
            waterScan.Dispose();
            renderers.Clear();
        }

        public void Dispose()
        {
            ResetForSession();
        }

        private void TryLoadIceNormal()
        {
            if (Time.realtimeSinceStartup < nextNormalLoadAttempt) return;
            nextNormalLoadAttempt = Time.realtimeSinceStartup + ScanIntervalSeconds;

            // TryLoadPixels both validates the agreed winter asset key and asks the
            // repository to load/reload its bundle after DV changes worlds.
            Color32[] fallbackPixels;
            if (!texturePack.TryLoadPixels(IceNormalAssetName, SeasonKind.Winter,
                FallbackTextureSize, FallbackTextureSize, out fallbackPixels))
            {
                if (!missingNormalLogged)
                {
                    Debug.LogWarning("[DVSeasons] Winter water ice normal '" +
                        IceNormalAssetName + "' is missing; water will remain unchanged.");
                    missingNormalLogged = true;
                }
                return;
            }

            // Prefer the original AssetBundle Texture2D. It retains compression,
            // authored mip maps and the linear (non-sRGB) normal-map import flag.
            var bundle = texturePack.Bundle;
            if (bundle != null)
            {
                var assetNames = bundle.GetAllAssetNames();
                for (var i = 0; i < assetNames.Length; i++)
                {
                    var assetName = assetNames[i];
                    var fileName = Path.GetFileNameWithoutExtension(
                        assetName.Replace('/', Path.DirectorySeparatorChar));
                    if (!string.Equals(fileName, IceNormalAssetName,
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (assetName.IndexOf("/winter/", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var packed = bundle.LoadAsset<Texture2D>(assetName);
                    if (packed == null) continue;
                    packed.wrapMode = TextureWrapMode.Repeat;
                    packed.filterMode = FilterMode.Trilinear;
                    packed.anisoLevel = 4;
                    packed.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                    iceNormal = packed;
                    ownsIceNormal = false;
                    missingNormalLogged = false;
                    Debug.Log("[DVSeasons] Loaded winter water ice normal from the AssetBundle.");
                    return;
                }
            }

            // Loose PNG overrides have no Texture2D handle in the repository. Build
            // a linear repeating texture from the already scaled pixels in that case.
            var generated = new Texture2D(FallbackTextureSize, FallbackTextureSize,
                TextureFormat.RGBA32, true, true)
            {
                name = "DVSeasons Water Ice Normal",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                hideFlags = HideFlags.HideAndDontSave
            };
            generated.SetPixels32(fallbackPixels);
            generated.Apply(true, false);
            iceNormal = generated;
            ownsIceNormal = true;
            missingNormalLogged = false;
            Debug.Log("[DVSeasons] Loaded loose winter water ice normal override.");
        }

        private void TryLoadIceAlbedo()
        {
            if (Time.realtimeSinceStartup < nextAlbedoLoadAttempt) return;
            nextAlbedoLoadAttempt = Time.realtimeSinceStartup + ScanIntervalSeconds;

            Color32[] pixels;
            const int size = 1024;
            if (!texturePack.TryLoadPixels(IceAlbedoAssetName, SeasonKind.Winter,
                size, size, out pixels))
            {
                if (!missingAlbedoLogged)
                {
                    Debug.LogWarning("[DVSeasons] Winter water ice albedo '" +
                        IceAlbedoAssetName + "' is missing; only the water normal can freeze.");
                    missingAlbedoLogged = true;
                }
                return;
            }

            iceAlbedo = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
            {
                name = "DVSeasons Water Ice Albedo",
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 4,
                hideFlags = HideFlags.HideAndDontSave
            };
            iceAlbedo.SetPixels32(pixels);
            iceAlbedo.Apply(true, false);

            // Use the bundled world-projected shader with an explicit tint.
            // Stock Unlit/Transparent has no tint control, so it stayed bright
            // at night. Lighting is supplied every frame from the weather driver.
            var shader = texturePack.LoadShader("WaterIceOverlay");
            if (shader == null || !shader.isSupported)
            {
                Debug.LogWarning("[DVSeasons] A transparent shader for the water ice overlay is unavailable.");
                UnityEngine.Object.Destroy(iceAlbedo);
                iceAlbedo = null;
                return;
            }
            iceOverlayMaterial = new Material(shader)
            {
                name = "DVSeasons Water Ice Overlay",
                mainTexture = iceAlbedo,
                mainTextureScale = Vector2.one,
                renderQueue = 3100,
                hideFlags = HideFlags.HideAndDontSave
            };
            missingAlbedoLogged = false;
            Debug.Log("[DVSeasons] Loaded seamless winter ice albedo for world water using shader '" +
                shader.name + "' after the BGWater pass.");
        }

        private void BindWaterRenderer(MeshRenderer renderer)
        {
            var added = 0;
            var projectedCount = 0;
            if (renderer == null) return;
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return;
            var sharedMaterials = renderer.sharedMaterials;
            for (var submesh = 0; submesh < sharedMaterials.Length &&
                submesh < filter.sharedMesh.subMeshCount; submesh++)
            {
                if (!IsWorldWaterMaterial(sharedMaterials[submesh])) continue;
                var waterMaterial=sharedMaterials[submesh];
                if(!materials.ContainsKey(waterMaterial.GetInstanceID()))
                    materials.Add(waterMaterial.GetInstanceID(),Capture(waterMaterial));
                var key = ((long)(uint)renderer.GetInstanceID() << 32) | (uint)submesh;
                if (renderers.ContainsKey(key)) continue;
                var projected = CreateWorldProjectedSubmesh(renderer,
                    filter.sharedMesh, submesh);
                if (projected != null) projectedCount++;
                renderers.Add(key, new WaterRendererRecord
                {
                    Renderer = renderer,
                    Mesh = projected ?? filter.sharedMesh,
                    Submesh = projected == null ? submesh : 0,
                    OwnsMesh = projected != null
                });
                added++;
            }
            if (added > 0)
                Debug.Log("[DVSeasons] Bound " + added +
                    " world-water renderer submesh(es) for the winter ice albedo; " +
                    projectedCount + " received world-projected UVs.");
        }

        internal static Color OverlayTint(float amount, float lightFactor)
        {
            amount = Mathf.Clamp01(amount);
            lightFactor = Mathf.Clamp01(lightFactor);
            var eased = amount * amount * (3f - (2f * amount));
            // No minimum emissive brightness: at zero sunlight the albedo layer
            // is dark, while native water reflections remain visible underneath.
            return new Color(0.95f * lightFactor, 0.98f * lightFactor,
                lightFactor, 0.34f * eased);
        }

        private void DrawIceOverlays(float amount, float lightFactor)
        {
            if (amount <= 0.001f || iceOverlayMaterial == null) return;
            // Keep the native water colour and reflections visible. 0.2.5 used
            // 82% alpha and reduced the lake to an opaque grey sheet.
            var tint = OverlayTint(amount, lightFactor);
            iceOverlayMaterial.SetFloat("_TileSize", IceWorldTileSize);
            if (iceOverlayMaterial.HasProperty("_Color"))
                iceOverlayMaterial.SetColor("_Color", tint);
            if (iceOverlayMaterial.HasProperty("_TintColor"))
                iceOverlayMaterial.SetColor("_TintColor", tint);

            foreach (var record in renderers.Values)
            {
                var renderer = record.Renderer;
                if (renderer == null || record.Mesh == null || !renderer.enabled ||
                    !renderer.gameObject.activeInHierarchy) continue;
                var matrix = Matrix4x4.Translate(Vector3.up * 0.035f) *
                    renderer.localToWorldMatrix;
                Graphics.DrawMesh(record.Mesh, matrix, iceOverlayMaterial,
                    renderer.gameObject.layer, null, record.Submesh, null, false, false);
            }
        }

        private static Mesh CreateWorldProjectedSubmesh(MeshRenderer renderer,
            Mesh source, int submesh)
        {
            try
            {
                var vertices = source.vertices;
                if (vertices == null || vertices.Length == 0) return null;
                var indices = source.GetIndices(submesh);
                if (indices == null || indices.Length == 0) return null;

                var uv = new Vector2[vertices.Length];
                var localToWorld = renderer.localToWorldMatrix;
                for (var i = 0; i < vertices.Length; i++)
                {
                    var world = localToWorld.MultiplyPoint3x4(vertices[i]);
                    uv[i] = new Vector2(world.x / IceWorldTileSize,
                        world.z / IceWorldTileSize);
                }

                var projected = new Mesh
                {
                    name = source.name + " [DVSeasons World-Projected Ice]",
                    hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = vertices.Length > ushort.MaxValue
                        ? IndexFormat.UInt32 : IndexFormat.UInt16,
                    vertices = vertices,
                    uv = uv
                };
                projected.SetIndices(indices, source.GetTopology(submesh), 0, false);
                projected.bounds = source.bounds;
                return projected;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[DVSeasons] Could not create world-projected ice UVs for water mesh '" +
                    source.name + "'; keeping its original UVs. " + exception.Message);
                return null;
            }
        }

        private static bool IsWorldWaterMaterial(Material material)
        {
            if (material == null || material.shader == null) return false;
            var name = material.name ?? string.Empty;
            const string instanceSuffix = " (Instance)";
            if (name.EndsWith(instanceSuffix, StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - instanceSuffix.Length);

            return string.Equals(name, "WaterLake", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(material.shader.name, "BadDog/BGWater", StringComparison.Ordinal) &&
                material.HasProperty("_MainWave") &&
                material.HasProperty("_SecondWave") &&
                material.HasProperty("_WaterBaseColor") &&
                material.HasProperty("_MainWaveTilingOffset") &&
                material.HasProperty("_SecondWaveTilingOffset");
        }

        private static WaterMaterialRecord Capture(Material material)
        {
            return new WaterMaterialRecord
            {
                Material = material,
                MainWave = material.GetTexture("_MainWave"),
                SecondWave = material.GetTexture("_SecondWave"),
                MainWaveTilingOffset = material.GetVector("_MainWaveTilingOffset"),
                SecondWaveTilingOffset = material.GetVector("_SecondWaveTilingOffset"),
                WaterBaseColor = material.GetColor("_WaterBaseColor"),
                WaterMuddyColor = GetColor(material, "_WaterMuddyColor", Color.black),
                MainWaveBumpScale = GetFloat(material, "_MainWaveBumpScale"),
                SecondWaveBumpScale = GetFloat(material, "_SecondWaveBumpScale"),
                WaterDistortScale = GetFloat(material, "_WaterDistortScale"),
                Roughness = GetFloat(material, "_Roughness"),
                SsrIntensity = GetFloat(material, "_SSRIntensity"),
                ReflectionMin = GetFloat(material, "_ReflectionMin"),
                ReflectionMax = GetFloat(material, "_ReflectionMax"),
                SpecularIntensity = GetFloat(material, "_SpecularIntensity")
            };
        }

        private void Apply(WaterMaterialRecord record, float amount)
        {
            var material = record.Material;
            if (material == null) return;

            if (amount <= 0.001f)
            {
                Restore(record);
                return;
            }
            if (iceNormal == null) return;
            if (Mathf.Abs(record.LastAmount - amount) < 0.002f) return;

            if (!record.IceTextureApplied && amount >= TextureEnableThreshold)
                record.IceTextureApplied = true;
            else if (record.IceTextureApplied && amount <= TextureDisableThreshold)
                record.IceTextureApplied = false;

            // BGWater samples its two wave slots at independent scales. Feeding the
            // same crack normal into both produced two visibly offset ice sheets.
            // Keep a single authored ice normal and fade the second water wave out.
            material.SetTexture("_MainWave", record.IceTextureApplied ? iceNormal : record.MainWave);
            material.SetTexture("_SecondWave", record.SecondWave);

            var eased = amount * amount * (3f - (2f * amount));
            material.SetColor("_WaterBaseColor", Color.Lerp(record.WaterBaseColor,
                WithAlpha(IceBaseColor, record.WaterBaseColor.a), eased));
            SetColor(material, "_WaterMuddyColor", Color.Lerp(record.WaterMuddyColor,
                WithAlpha(IceMuddyColor, record.WaterMuddyColor.a), eased));

            SetFloat(material, "_MainWaveBumpScale",
                Mathf.Lerp(record.MainWaveBumpScale, 0.08f, eased));
            SetFloat(material, "_SecondWaveBumpScale",
                Mathf.Lerp(record.SecondWaveBumpScale, 0f, eased));
            SetFloat(material, "_WaterDistortScale",
                Mathf.Lerp(record.WaterDistortScale, 0.08f, eased));
            SetFloat(material, "_Roughness", Mathf.Lerp(record.Roughness, 0.18f, eased));
            SetFloat(material, "_SSRIntensity", Mathf.Lerp(record.SsrIntensity, 0.35f, eased));
            SetFloat(material, "_ReflectionMin", Mathf.Lerp(record.ReflectionMin, 0.12f, eased));
            SetFloat(material, "_ReflectionMax", Mathf.Lerp(record.ReflectionMax, 0.55f, eased));
            SetFloat(material, "_SpecularIntensity",
                Mathf.Lerp(record.SpecularIntensity, 1.4f, eased));

            // xy contains tiling; zw contains the two wave scroll speeds. Preserve
            // scale while bringing all motion to a complete stop as water freezes.
            material.SetVector("_MainWaveTilingOffset",
                FreezeWave(record.MainWaveTilingOffset, eased));
            material.SetVector("_SecondWaveTilingOffset",
                FreezeWave(record.SecondWaveTilingOffset, eased));

            record.WasModified = true;
            record.LastAmount = amount;
        }

        private static void Restore(WaterMaterialRecord record)
        {
            if (!record.WasModified || record.Material == null) return;
            var material = record.Material;
            material.SetTexture("_MainWave", record.MainWave);
            material.SetTexture("_SecondWave", record.SecondWave);
            material.SetVector("_MainWaveTilingOffset", record.MainWaveTilingOffset);
            material.SetVector("_SecondWaveTilingOffset", record.SecondWaveTilingOffset);
            material.SetColor("_WaterBaseColor", record.WaterBaseColor);
            SetColor(material, "_WaterMuddyColor", record.WaterMuddyColor);
            SetFloat(material, "_MainWaveBumpScale", record.MainWaveBumpScale);
            SetFloat(material, "_SecondWaveBumpScale", record.SecondWaveBumpScale);
            SetFloat(material, "_WaterDistortScale", record.WaterDistortScale);
            SetFloat(material, "_Roughness", record.Roughness);
            SetFloat(material, "_SSRIntensity", record.SsrIntensity);
            SetFloat(material, "_ReflectionMin", record.ReflectionMin);
            SetFloat(material, "_ReflectionMax", record.ReflectionMax);
            SetFloat(material, "_SpecularIntensity", record.SpecularIntensity);
            record.IceTextureApplied = false;
            record.WasModified = false;
            record.LastAmount = -1f;
        }

        private static Vector4 FreezeWave(Vector4 original, float amount)
        {
            return new Vector4(original.x, original.y,
                Mathf.Lerp(original.z, 0f, amount),
                Mathf.Lerp(original.w, 0f, amount));
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }

        private static float GetFloat(Material material, string property)
        {
            return material.HasProperty(property) ? material.GetFloat(property) : 0f;
        }

        private static Color GetColor(Material material, string property, Color fallback)
        {
            return material.HasProperty(property) ? material.GetColor(property) : fallback;
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property)) material.SetFloat(property, value);
        }

        private static void SetColor(Material material, string property, Color value)
        {
            if (material.HasProperty(property)) material.SetColor(property, value);
        }
    }
}
