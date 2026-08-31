using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class SeasonalTextureController : IDisposable
    {
        private enum TextureCategory : byte { Foliage, Bark, Ballast, Billboard, Sleeper }

        private sealed class SeasonalTextureSet
        {
            private readonly Texture2D source;
            private readonly TextureCategory category;
            private readonly int maximumResolution;
            private readonly SeasonAssetBundleRepository texturePack;
            private readonly bool evergreen;
            private Color32[] basePixels;
            private Color32[][] profiles;
            private Color32[] outputPixels;
            private int lastStyleKey = int.MinValue;
            private int pendingStyleKey = int.MinValue;
            private int pendingPixelIndex;
            private Color32[] pendingCurrent;
            private Color32[] pendingNext;
            private float pendingTransition;
            private float pendingSeasonalStrength;
            private float pendingWinterWeight;
            private Color32[][] billboardMipPixels;
            private bool failed;

            public SeasonalTextureSet(Texture2D source, TextureCategory category, int maximumResolution,
                SeasonAssetBundleRepository texturePack, bool evergreenHint)
            {
                this.source = source;
                this.category = category;
                this.maximumResolution = maximumResolution;
                this.texturePack = texturePack;
                var normalizedName = (source == null ? string.Empty : source.name).ToLowerInvariant();
                evergreen = evergreenHint || IsEvergreenDescription(normalizedName);
            }

            public Texture2D Output { get; private set; }
            public bool IsReady { get { return Output != null; } }
            public bool IsFailed { get { return failed; } }
            public bool HasPendingUpdate { get { return pendingStyleKey != int.MinValue && !failed; } }

            public int UpdateChunk(SeasonState state, float strength, int styleKey, int pixelBudget)
            {
                if (failed || pixelBudget <= 0) return 0;
                try
                {
                    if (!IsReady) Initialize();
                    if (!IsReady || (styleKey == lastStyleKey && pendingStyleKey == int.MinValue)) return 0;
                    if (pendingStyleKey != styleKey)
                    {
                        pendingStyleKey = styleKey;
                        pendingPixelIndex = 0;
                        pendingCurrent = profiles[(int)state.Current];
                        pendingNext = profiles[(int)state.Next];
                        pendingTransition = GetTextureTransition(state);
                        pendingSeasonalStrength = Mathf.Clamp01(strength);
                        pendingWinterWeight = GetWinterWeight(state, pendingTransition);
                    }

                    var firstPixel = pendingPixelIndex;
                    var lastPixel = Mathf.Min(outputPixels.Length, firstPixel + pixelBudget);
                    for (var i = firstPixel; i < lastPixel; i++)
                    {
                        var seasonal = Lerp(pendingCurrent[i], pendingNext[i], pendingTransition);
                        outputPixels[i] = Lerp(basePixels[i], seasonal, pendingSeasonalStrength);
                        if (category == TextureCategory.Foliage)
                        {
                            var alphaStrength = Mathf.Max(pendingSeasonalStrength,
                                Mathf.Clamp01(pendingWinterWeight));
                            var seasonalAlpha = seasonal.a;
                            if (!evergreen)
                                seasonalAlpha = (byte)Mathf.RoundToInt(seasonalAlpha *
                                    Mathf.Lerp(1f, 0.08f, Mathf.Clamp01(pendingWinterWeight)));
                            outputPixels[i].a = (byte)Mathf.RoundToInt(Mathf.Lerp(basePixels[i].a,
                                seasonalAlpha, alphaStrength));
                        }
                    }
                    pendingPixelIndex = lastPixel;
                    if (pendingPixelIndex >= outputPixels.Length)
                    {
                        UploadOutput(outputPixels, pendingWinterWeight >= 0.48f);
                        lastStyleKey = pendingStyleKey;
                        ClearPendingUpdate();
                    }
                    return lastPixel - firstPixel;
                }
                catch (Exception exception)
                {
                    failed = true;
                    Debug.LogWarning("[DVSeasons] Texture '" + (source == null ? "<destroyed>" : source.name) +
                        "' could not be converted: " + exception.Message);
                    DisposeOutput();
                    ClearPendingUpdate();
                    return 0;
                }
            }

            private void ClearPendingUpdate()
            {
                pendingStyleKey = int.MinValue;
                pendingPixelIndex = 0;
                pendingCurrent = null;
                pendingNext = null;
            }

            private float GetTextureTransition(SeasonState state)
            {
                var transition = Mathf.Clamp01(state.Transition);
                if (category == TextureCategory.Ballast || category == TextureCategory.Sleeper)
                {
                    // Ballast and sleepers are visually isolated by hard mesh
                    // boundaries. Let their winter texture follow actual snow cover
                    // so leaves do not remain between already snowy track beds.
                    var winterWeight = Mathf.Sqrt(Mathf.Clamp01(state.SnowAmount));
                    if (state.Current == SeasonKind.Winter && state.Next == SeasonKind.Spring)
                        return 1f - winterWeight;
                    if (state.Next == SeasonKind.Winter) return winterWeight;
                }
                var delayedVegetation = category == TextureCategory.Foliage ||
                    category == TextureCategory.Billboard;
                if (delayedVegetation && state.Current == SeasonKind.Winter &&
                    state.Next == SeasonKind.Spring)
                {
                    var winterWeight = SnowCoverProfile.GetVegetationWinterWeight(
                        state.SnowAmount, true);
                    return 1f - winterWeight;
                }
                if (delayedVegetation && state.Next == SeasonKind.Winter)
                    return SnowCoverProfile.GetVegetationWinterWeight(state.SnowAmount, false);
                return transition;
            }

            private static float GetWinterWeight(SeasonState state, float textureTransition)
            {
                if (state.Current == SeasonKind.Winter && state.Next == SeasonKind.Spring)
                    return SnowCoverProfile.GetVegetationWinterWeight(state.SnowAmount, true);
                if (state.Next == SeasonKind.Winter)
                    return SnowCoverProfile.GetVegetationWinterWeight(state.SnowAmount, false);
                var winterWeight = 0f;
                if (state.Current == SeasonKind.Winter) winterWeight += 1f - textureTransition;
                if (state.Next == SeasonKind.Winter) winterWeight += textureTransition;
                return Mathf.Clamp01(winterWeight);
            }

            public void Dispose()
            {
                DisposeOutput();
                basePixels = null;
                profiles = null;
                outputPixels = null;
                billboardMipPixels = null;
                ClearPendingUpdate();
            }

            private void Initialize()
            {
                if (source == null) return;
                // Keep ballast detailed enough for a cab view, but cap its CPU blend
                // buffer at 512px. The fixed source retains small stones and the work
                // is completed incrementally, avoiding a million-pixel transition
                // on a single frame.
                var detailedTrackSurface = category == TextureCategory.Ballast ||
                    category == TextureCategory.Sleeper;
                var effectiveMaximumResolution = detailedTrackSurface
                    ? Mathf.Max(maximumResolution, 512)
                    : maximumResolution;
                if (detailedTrackSurface)
                    effectiveMaximumResolution = Mathf.Min(effectiveMaximumResolution, 512);
                var scale = Mathf.Min(1f, effectiveMaximumResolution /
                    (float)Mathf.Max(source.width, source.height));
                var width = Mathf.Max(16, Mathf.RoundToInt(source.width * scale));
                var height = Mathf.Max(16, Mathf.RoundToInt(source.height * scale));
                if (category == TextureCategory.Billboard && source.height > 0)
                {
                    var frameCount = Mathf.RoundToInt(source.width / (float)source.height);
                    if (frameCount >= 2 && frameCount <= 16 &&
                        Mathf.Abs(source.width - (frameCount * source.height)) <= frameCount)
                    {
                        // A generated tree billboard is an eight-frame horizontal
                        // atlas. Scaling its total width to the normal 256px texture
                        // budget left only 32x32 per view and erased real branches.
                        // Keep 128px per view; the transition remains chunked and the
                        // complete set is still only 0.5 MB of RGBA pixels.
                        var frameResolution = Mathf.Min(128, source.height);
                        width = frameResolution * frameCount;
                        height = frameResolution;
                    }
                }
                basePixels = ReadScaledPixels(source, width, height);

                profiles = new Color32[4][];
                var keepSpringNeutral = category == TextureCategory.Ballast || category == TextureCategory.Bark ||
                    category == TextureCategory.Sleeper;
                var keepAutumnNeutral = category == TextureCategory.Ballast || category == TextureCategory.Bark;
                profiles[(int)SeasonKind.Spring] = keepSpringNeutral
                    ? basePixels
                    : LoadOrCreateProfile(SeasonKind.Spring, width, height);
                profiles[(int)SeasonKind.Summer] = basePixels;
                profiles[(int)SeasonKind.Autumn] = keepAutumnNeutral
                    ? basePixels
                    : LoadOrCreateProfile(SeasonKind.Autumn, width, height);
                profiles[(int)SeasonKind.Winter] = LoadOrCreateProfile(SeasonKind.Winter, width, height);
                outputPixels = new Color32[basePixels.Length];

                Output = new Texture2D(width, height, TextureFormat.RGBA32,
                    category == TextureCategory.Billboard)
                {
                    name = source.name + " [DVSeasons " + category + "]",
                    wrapMode = source.wrapMode,
                    filterMode = category == TextureCategory.Billboard
                        ? FilterMode.Trilinear
                        : source.filterMode,
                    mipMapBias = category == TextureCategory.Billboard ? -0.6f : 0f,
                    anisoLevel = source.anisoLevel,
                    hideFlags = HideFlags.HideAndDontSave
                };
                UploadOutput(basePixels, false);
            }

            private void UploadOutput(Color32[] pixels, bool preserveThinBillboardCoverage)
            {
                Output.SetPixels32(pixels, 0);
                if (category != TextureCategory.Billboard)
                {
                    Output.Apply(false, false);
                    return;
                }
                if (!preserveThinBillboardCoverage)
                {
                    Output.mipMapBias = 0f;
                    Output.Apply(true, false);
                    return;
                }

                // Prefer one slightly sharper mip in winter. The generated bare
                // branches are only one or two texels wide in each billboard frame.
                Output.mipMapBias = -0.6f;

                if (billboardMipPixels == null || billboardMipPixels.Length != Output.mipmapCount)
                    billboardMipPixels = new Color32[Output.mipmapCount][];
                var previous = pixels;
                var previousWidth = Output.width;
                var previousHeight = Output.height;
                for (var mip = 1; mip < Output.mipmapCount; mip++)
                {
                    var mipWidth = Mathf.Max(1, previousWidth / 2);
                    var mipHeight = Mathf.Max(1, previousHeight / 2);
                    var requiredLength = mipWidth * mipHeight;
                    var current = billboardMipPixels[mip];
                    if (current == null || current.Length != requiredLength)
                    {
                        current = new Color32[requiredLength];
                        billboardMipPixels[mip] = current;
                    }
                    BuildCoveragePreservingMip(previous, previousWidth, previousHeight,
                        current, mipWidth, mipHeight);
                    Output.SetPixels32(current, mip);
                    previous = current;
                    previousWidth = mipWidth;
                    previousHeight = mipHeight;
                }
                Output.Apply(false, false);
            }

            private static void BuildCoveragePreservingMip(Color32[] sourcePixels,
                int sourceWidth, int sourceHeight, Color32[] destinationPixels,
                int destinationWidth, int destinationHeight)
            {
                for (var y = 0; y < destinationHeight; y++)
                for (var x = 0; x < destinationWidth; x++)
                {
                    long alphaTotal = 0;
                    long redTotal = 0;
                    long greenTotal = 0;
                    long blueTotal = 0;
                    var maximumAlpha = 0;
                    var sampleCount = 0;
                    for (var sampleY = y * 2;
                        sampleY <= Mathf.Min(sourceHeight - 1, (y * 2) + 1); sampleY++)
                    for (var sampleX = x * 2;
                        sampleX <= Mathf.Min(sourceWidth - 1, (x * 2) + 1); sampleX++)
                    {
                        var sample = sourcePixels[(sampleY * sourceWidth) + sampleX];
                        sampleCount++;
                        maximumAlpha = Mathf.Max(maximumAlpha, sample.a);
                        alphaTotal += sample.a;
                        redTotal += sample.r * sample.a;
                        greenTotal += sample.g * sample.a;
                        blueTotal += sample.b * sample.a;
                    }

                    var destinationIndex = (y * destinationWidth) + x;
                    if (maximumAlpha == 0 || alphaTotal == 0)
                    {
                        destinationPixels[destinationIndex] = new Color32(0, 0, 0, 0);
                        continue;
                    }
                    var averageAlpha = (int)(alphaTotal / Mathf.Max(1, sampleCount));
                    // Preserve enough coverage for a thin branch to survive alpha
                    // testing, but let isolated pixels fade at lower mips. Keeping
                    // almost 100% of the maximum alpha on every level dilated each
                    // branch repeatedly and recreated a solid, leafy-looking crown.
                    var preservedAlpha = Mathf.Max(averageAlpha,
                        Mathf.RoundToInt(maximumAlpha * 0.78f));
                    destinationPixels[destinationIndex] = new Color32(
                        (byte)Mathf.Clamp((int)(redTotal / alphaTotal), 0, 255),
                        (byte)Mathf.Clamp((int)(greenTotal / alphaTotal), 0, 255),
                        (byte)Mathf.Clamp((int)(blueTotal / alphaTotal), 0, 255),
                        (byte)Mathf.Clamp(preservedAlpha, 0, 255));
                }
            }

            private Color32[] LoadOrCreateProfile(SeasonKind season, int width, int height)
            {
                Color32[] packed;
                if (season == SeasonKind.Winter && category == TextureCategory.Ballast &&
                    texturePack.TryLoadGenericWinterSnowPixels(width, height, out packed))
                    return packed;
                if (texturePack.TryLoadPixels(source.name, season, width, height, out packed))
                    return AdjustProfile(packed, season, category, width, height);
                packed = CreateFallbackProfile(basePixels, width, height, season, source.name, category);
                return AdjustProfile(packed, season, category, width, height);
            }

            private Color32[] AdjustProfile(Color32[] pixels, SeasonKind season,
                TextureCategory category, int width, int height)
            {
                var adjusted = season == SeasonKind.Autumn ? EnhanceAutumnProfile(pixels) : pixels;
                if (category == TextureCategory.Billboard && season == SeasonKind.Winter && !evergreen)
                {
                    Color32[] bareTreeAtlas;
                    if (texturePack.TryLoadBareTreeBillboardPixels(
                        source == null ? string.Empty : source.name,
                        width, height, out bareTreeAtlas))
                        adjusted = bareTreeAtlas;
                }
                if (category != TextureCategory.Billboard) return adjusted;
                var brightness = season == SeasonKind.Winter ? 0.65f :
                    season == SeasonKind.Autumn ? 0.64f : 0.76f;
                var result = new Color32[adjusted.Length];
                for (var i = 0; i < adjusted.Length; i++)
                {
                    var pixel = adjusted[i];
                    result[i] = new Color32(
                        (byte)Mathf.RoundToInt(pixel.r * brightness),
                        (byte)Mathf.RoundToInt(pixel.g * brightness),
                        (byte)Mathf.RoundToInt(pixel.b * Mathf.Min(1f, brightness + 0.03f)),
                        pixel.a);
                }
                return result;
            }

            private static Color32[] EnhanceAutumnProfile(Color32[] pixels)
            {
                var result = new Color32[pixels.Length];
                for (var i = 0; i < pixels.Length; i++)
                {
                    var pixel = pixels[i];
                    if (pixel.a == 0)
                    {
                        result[i] = pixel;
                        continue;
                    }
                    var red = pixel.r / 255f;
                    var green = pixel.g / 255f;
                    var blue = pixel.b / 255f;
                    var luminance = (red * 0.30f) + (green * 0.59f) + (blue * 0.11f);
                    const float saturation = 1.34f;
                    red = luminance + ((red - luminance) * saturation);
                    green = luminance + ((green - luminance) * saturation);
                    blue = luminance + ((blue - luminance) * saturation);
                    var warmth = Mathf.Clamp01(luminance * 1.25f);
                    red += 0.09f * warmth;
                    green += 0.018f * warmth;
                    blue -= 0.055f * warmth;
                    result[i] = new Color32(
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(red) * 255f),
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(green) * 255f),
                        (byte)Mathf.RoundToInt(Mathf.Clamp01(blue) * 255f),
                        pixel.a);
                }
                return result;
            }

            private static Color32[] CreateFallbackProfile(Color32[] sourcePixels, int width, int height,
                SeasonKind season, string textureName, TextureCategory category)
            {
                var result = new Color32[sourcePixels.Length];
                var name = (textureName ?? string.Empty).ToLowerInvariant();
                var evergreen = name.Contains("fir") || name.Contains("pine") || name.Contains("spruce");
                for (var i = 0; i < sourcePixels.Length; i++)
                {
                    var pixel = sourcePixels[i];
                    if (season == SeasonKind.Spring)
                    {
                        result[i] = new Color32((byte)Mathf.Clamp(pixel.r * 0.92f + 8f, 0f, 255f),
                            (byte)Mathf.Clamp(pixel.g * 1.08f + 5f, 0f, 255f),
                            (byte)Mathf.Clamp(pixel.b * 0.90f + 4f, 0f, 255f), pixel.a);
                        continue;
                    }
                    if (season == SeasonKind.Autumn)
                    {
                        var luminance = (pixel.r * 0.30f) + (pixel.g * 0.59f) + (pixel.b * 0.11f);
                        result[i] = new Color32((byte)Mathf.Clamp(luminance * 1.38f + 28f, 0f, 255f),
                            (byte)Mathf.Clamp(luminance * 0.58f + 11f, 0f, 255f),
                            (byte)Mathf.Clamp(luminance * 0.18f + 4f, 0f, 255f), pixel.a);
                        continue;
                    }

                    var light = (pixel.r * 0.30f) + (pixel.g * 0.59f) + (pixel.b * 0.11f);
                    var greenDominant = pixel.g > pixel.r * 1.08f && pixel.g > pixel.b * 1.08f;
                    var y = i / width;
                    var upperSnow = Mathf.Clamp01((y / (float)Mathf.Max(1, height - 1) - 0.35f) * 1.2f);
                    if (category == TextureCategory.Billboard)
                    {
                        var frost = greenDominant ? 0.78f : 0.28f;
                        result[i] = new Color32((byte)Mathf.Lerp(pixel.r, light * 0.68f + 70f, frost),
                            (byte)Mathf.Lerp(pixel.g, light * 0.72f + 76f, frost),
                            (byte)Mathf.Lerp(pixel.b, light * 0.78f + 88f, frost), pixel.a);
                    }
                    else if (category == TextureCategory.Foliage && evergreen)
                    {
                        var snow = Mathf.Clamp01(upperSnow * (light / 255f) * 1.4f);
                        result[i] = new Color32((byte)Mathf.Lerp(light * 0.58f, 225f, snow),
                            (byte)Mathf.Lerp(light * 0.66f, 234f, snow),
                            (byte)Mathf.Lerp(light * 0.70f, 244f, snow), pixel.a);
                    }
                    else
                    {
                        var alpha = pixel.a;
                        if (category == TextureCategory.Foliage && greenDominant)
                            alpha = (byte)Mathf.RoundToInt(pixel.a * 0.08f);
                        result[i] = new Color32((byte)Mathf.Clamp(light * 0.70f + 38f, 0f, 255f),
                            (byte)Mathf.Clamp(light * 0.68f + 40f, 0f, 255f),
                            (byte)Mathf.Clamp(light * 0.72f + 48f, 0f, 255f), alpha);
                    }
                }
                return result;
            }

            private void DisposeOutput()
            {
                if (Output != null) UnityEngine.Object.Destroy(Output);
                Output = null;
            }

            private static Color32[] ReadScaledPixels(Texture sourceTexture, int width, int height)
            {
                var temporary = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default);
                var previous = RenderTexture.active;
                Texture2D readable = null;
                try
                {
                    Graphics.Blit(sourceTexture, temporary);
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

            private static Color32 Lerp(Color32 a, Color32 b, float t)
            {
                return new Color32((byte)Mathf.RoundToInt(Mathf.Lerp(a.r, b.r, t)),
                    (byte)Mathf.RoundToInt(Mathf.Lerp(a.g, b.g, t)),
                    (byte)Mathf.RoundToInt(Mathf.Lerp(a.b, b.b, t)),
                    (byte)Mathf.RoundToInt(Mathf.Lerp(a.a, b.a, t)));
            }
        }

        private sealed class MaterialBinding
        {
            public Material Material;
            public string Property;
            public Texture2D Original;
            public SeasonalTextureSet Set;
        }

        private readonly Dictionary<string, SeasonalTextureSet> sets = new Dictionary<string, SeasonalTextureSet>();
        private readonly Dictionary<string, MaterialBinding> materialBindings = new Dictionary<string, MaterialBinding>();
        private readonly HashSet<int> scannedMaterials = new HashSet<int>();
        private readonly Queue<SeasonalTextureSet> updateQueue = new Queue<SeasonalTextureSet>();
        private readonly HashSet<SeasonalTextureSet> queued = new HashSet<SeasonalTextureSet>();
        private readonly SeasonAssetBundleRepository texturePack;
        private float nextScanTime;
        private int lastStyleKey = int.MinValue;
        private int configurationKey = int.MinValue;
        private bool active;

        public SeasonalTextureController(SeasonAssetBundleRepository texturePack)
        {
            this.texturePack = texturePack;
        }

        public void Apply(SeasonState state, SeasonModSettings settings)
        {
            if (!settings.SeasonalTexturesEnabled)
            {
                if (active) Reset();
                return;
            }
            active = true;
            var newConfigurationKey = settings.SeasonalTextureResolution * 397 ^ settings.MaximumSeasonalTextures ^
                (settings.TerrainTextureChanges ? 0x10000 : 0) ^
                (settings.VegetationTextureChanges ? 0x20000 : 0);
            if (configurationKey != int.MinValue && configurationKey != newConfigurationKey) Reset();
            configurationKey = newConfigurationKey;
            if (Time.realtimeSinceStartup >= nextScanTime)
            {
                // The full material registry is large and enumeration can briefly
                // stall a frame. Seasonal assets rarely appear after world load, so
                // a slower rescan keeps streamed content support without periodic
                // twelve-second hitches.
                nextScanTime = Time.realtimeSinceStartup + 30f;
                if (settings.TerrainTextureChanges || settings.VegetationTextureChanges)
                    ScanVegetationMaterials(settings);
            }

            var styleKey = BuildStyleKey(state, settings.TextureChangeStrength);
            if (styleKey != lastStyleKey)
            {
                lastStyleKey = styleKey;
                foreach (var set in sets.Values) Enqueue(set);
            }
            // TextureUpdatesPerFrame now controls both the number of texture sets
            // touched and a strict pixel budget. Large albedos therefore span
            // several frames instead of executing a full CPU blend and upload in a
            // single frame during a season transition.
            var setBudget = Mathf.Clamp(settings.TextureUpdatesPerFrame, 1, 4);
            var pixelBudget = setBudget * 32768;
            for (var i = 0; i < setBudget && updateQueue.Count > 0 && pixelBudget > 0; i++)
            {
                var set = updateQueue.Dequeue();
                queued.Remove(set);
                var processed = set.UpdateChunk(state, settings.TextureChangeStrength,
                    styleKey, pixelBudget);
                pixelBudget -= Mathf.Max(0, processed);
                if (set.HasPendingUpdate) Enqueue(set);
            }
            ApplyBindings();
        }

        public void Dispose() { Reset(); }

        private void ScanVegetationMaterials(SeasonModSettings settings)
        {
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            for (var i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null || material.name.IndexOf("DVSeasons", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                var materialId = material.GetInstanceID();
                if (!scannedMaterials.Add(materialId)) continue;
                var shaderName = material.shader == null ? string.Empty : material.shader.name;
                if (IsTerrainImposterMaterial(material.name + " " + shaderName)) continue;
                var propertyNames = material.GetTexturePropertyNames();
                for (var j = 0; j < propertyNames.Length; j++)
                {
                    var property = propertyNames[j];
                    if (!IsAlbedoProperty(property)) continue;
                    var texture = material.GetTexture(property) as Texture2D;
                    if (texture == null) continue;
                    var description = material.name + " " + texture.name + " " + shaderName;
                    TextureCategory category;
                    if (!TryClassifyVegetation(description, out category)) continue;
                    var trackSurface = category == TextureCategory.Ballast ||
                        category == TextureCategory.Sleeper;
                    if (trackSurface && !settings.TerrainTextureChanges) continue;
                    if (!trackSurface && !settings.VegetationTextureChanges) continue;
                    var bindingKey = materialId + "|" + property;
                    if (materialBindings.ContainsKey(bindingKey)) continue;
                    var set = GetOrCreate(texture, category, settings,
                        IsEvergreenDescription(description));
                    if (set == null) continue;
                    materialBindings.Add(bindingKey, new MaterialBinding
                    {
                        Material = material,
                        Property = property,
                        Original = texture,
                        Set = set
                    });
                }
            }
        }

        private SeasonalTextureSet GetOrCreate(Texture2D texture, TextureCategory category,
            SeasonModSettings settings, bool evergreenHint)
        {
            var evergreenProfile = evergreenHint || IsEvergreenDescription(texture.name);
            var key = texture.GetInstanceID() + "|" + (int)category + "|" +
                (evergreenProfile ? "E" : "D");
            SeasonalTextureSet set;
            if (sets.TryGetValue(key, out set)) return set;
            // Track materials are few, highly visible and often discovered after
            // the streamed vegetation atlases. Never let the foliage safety cap
            // silently exclude ballast or sleepers from the winter pass.
            var priorityTrackSurface = category == TextureCategory.Ballast ||
                category == TextureCategory.Sleeper;
            if (!priorityTrackSurface && sets.Count >= settings.MaximumSeasonalTextures)
                return null;
            set = new SeasonalTextureSet(texture, category, settings.SeasonalTextureResolution,
                texturePack, evergreenProfile);
            sets.Add(key, set);
            if (priorityTrackSurface)
                Debug.Log("[DVSeasons] Registered seasonal " + category +
                    " texture '" + texture.name + "'.");
            else if (category == TextureCategory.Billboard)
                Debug.Log("[DVSeasons] Registered " + (evergreenProfile ? "evergreen" : "deciduous") +
                    " distant-tree billboard texture '" + texture.name + "'.");
            Enqueue(set);
            return set;
        }

        private void ApplyBindings()
        {
            foreach (var binding in materialBindings.Values)
            {
                if (binding.Material != null && binding.Set.IsReady && binding.Material.GetTexture(binding.Property) != binding.Set.Output)
                    binding.Material.SetTexture(binding.Property, binding.Set.Output);
            }
        }

        private void Reset()
        {
            foreach (var binding in materialBindings.Values)
            {
                if (binding.Material == null) continue;
                var current = binding.Material.GetTexture(binding.Property);
                if (binding.Set.Output == null || current == binding.Set.Output) binding.Material.SetTexture(binding.Property, binding.Original);
            }
            foreach (var set in sets.Values) set.Dispose();
            sets.Clear();
            materialBindings.Clear();
            scannedMaterials.Clear();
            updateQueue.Clear();
            queued.Clear();
            nextScanTime = 0f;
            lastStyleKey = int.MinValue;
            configurationKey = int.MinValue;
            active = false;
        }

        private void Enqueue(SeasonalTextureSet set)
        {
            if (!set.IsFailed && queued.Add(set)) updateQueue.Enqueue(set);
        }

        private static int BuildStyleKey(SeasonState state, float strength)
        {
            var transitionStep = Mathf.RoundToInt(Mathf.Clamp01(state.Transition) * 32f);
            var strengthStep = Mathf.RoundToInt(Mathf.Clamp01(strength) * 20f);
            return ((int)state.Current << 16) | ((int)state.Next << 12) | (transitionStep << 5) | strengthStep;
        }

        private static bool IsAlbedoProperty(string property)
        {
            if (string.IsNullOrEmpty(property)) return false;
            var value = property.ToLowerInvariant();
            if (value.Contains("normal") || value.Contains("mask") || value.Contains("metal") ||
                value.Contains("spec") || value.Contains("rough") || value.Contains("height") || value.Contains("bump")) return false;
            return value.StartsWith("_maintex", StringComparison.Ordinal) || value.Contains("albedo") || value.Contains("diffuse") ||
                value.Contains("basemap") || value.Contains("basecolor");
        }

        private static bool TryClassifyVegetation(string description, out TextureCategory category)
        {
            var value = (description ?? string.Empty).ToLowerInvariant();
            if (ContainsAny(value, "sleeper", "railway tie", "railroad tie", "cross tie", "crosstie"))
            {
                category = TextureCategory.Sleeper;
                return true;
            }
            if (ContainsAny(value, "ballast", "railbed", "rail bed", "trackbed", "track bed",
                "track_base", "track base", "embankment", "gravel"))
            {
                category = TextureCategory.Ballast;
                return true;
            }
            var treeBillboard = value.Contains("billboard") && ContainsAny(value,
                "tree", "speedtree", "forest", "leaf", "beech", "poplar", "willow", "maple",
                "fir", "pine", "spruce", "conifer");
            if (value.Contains("billboard_") || value.Contains("tree billboard") ||
                value.Contains("speedtree billboard") || treeBillboard)
            {
                category = TextureCategory.Billboard;
                return true;
            }
            if (ContainsAny(value, "bark", "trunk", "branch", "wood"))
            {
                category = TextureCategory.Bark;
                return true;
            }
            if (ContainsAny(value, "leaf", "leaves", "foliage", "tree", "pine", "spruce", "fir", "bush",
                "grass", "vegetation", "forest", "shrub", "plant"))
            {
                category = TextureCategory.Foliage;
                return true;
            }
            category = TextureCategory.Foliage;
            return false;
        }

        private static bool IsTerrainImposterMaterial(string description)
        {
            var value = (description ?? string.Empty).ToLowerInvariant();
            return ContainsAny(value, "terrainimposter", "terrain imposter", "terrain_imposter",
                "terrain impostor", "terrain_impostor", "distantterrain", "distant terrain");
        }

        private static bool IsEvergreenDescription(string description)
        {
            var value = (description ?? string.Empty).ToLowerInvariant();
            return ContainsAny(value, "fir", "pine", "spruce", "conifer", "evergreen", "abies", "picea");
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            for (var i = 0; i < terms.Length; i++) if (value.Contains(terms[i])) return true;
            return false;
        }
    }
}
