using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class SeasonalTextureController : IDisposable
    {
        private enum TextureCategory : byte
        {
            Foliage,
            Bark,
            Ballast,
            Billboard,
            Sleeper,
            Rail,
            RoadSurface
        }

        private sealed class SeasonalTextureSet
        {
            private readonly Texture2D source;
            private readonly TextureCategory category;
            private readonly int maximumResolution;
            private readonly SeasonAssetBundleRepository texturePack;
            private readonly bool evergreen;
            private readonly SeasonTextureReadback sourceReadback = new SeasonTextureReadback();
            private Color32[] basePixels;
            private Color32[][] profiles;
            private Color32[][] winterTrackProfiles;
            private byte[] surfaceSnowRanks;
            private readonly float[] surfaceSnowWeights = new float[256];
            private Color32[] outputPixels;
            private int lastStyleKey = int.MinValue;
            private int pendingStyleKey = int.MinValue;
            private int pendingPixelIndex;
            private Color32[] pendingCurrent;
            private Color32[] pendingNext;
            private float pendingTransition;
            private float pendingSeasonalStrength;
            private float pendingWinterWeight;
            private WinterTrackTextureStage pendingWinterTrackStage;
            private WinterTrackTextureStage lastAppliedWinterTrackStage =
                (WinterTrackTextureStage)byte.MaxValue;
            private float pendingWinterTrackSnowAmount;
            private Color32[][] billboardMipPixels;
            private bool denseSurfacePrepared;
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
            public bool LegacyOnly { get { return category == TextureCategory.Rail || category == TextureCategory.RoadSurface; } }
            public bool IsTrackSurface
            {
                get
                {
                    return category == TextureCategory.Ballast ||
                        category == TextureCategory.Sleeper ||
                        category == TextureCategory.Rail ||
                        category == TextureCategory.RoadSurface;
                }
            }
            public bool HasPendingUpdate { get { return pendingStyleKey != int.MinValue && !failed; } }
            public bool NeedsUpdate(int styleKey)
            {
                return !failed && lastStyleKey != styleKey;
            }

            public int UpdateChunk(SeasonState state, float strength, int styleKey, int pixelBudget)
            {
                if (failed || pixelBudget <= 0) return 0;
                try
                {
                    // Initialization can be deferred while Unity streams the
                    // source mip. Keep this set in the queue until the readiness
                    // guard returns true; never blend or cache a partial readback.
                    if (!IsReady)
                    {
                        if (!Initialize()) return 0;
                        // Source upload is already a full texture operation. Do
                        // not also prepare several profiles and blend this frame.
                        return pixelBudget;
                    }
                    if (!IsReady || (styleKey == lastStyleKey && pendingStyleKey == int.MinValue)) return 0;
                    if (pendingStyleKey == int.MinValue)
                    {
                        // At most one previously missing profile is prepared in
                        // this slice. Completed profiles survive subsequent seasons.
                        if (!PrepareProfiles(state)) return pixelBudget;
                        pendingStyleKey = styleKey;
                        pendingPixelIndex = 0;
                        pendingWinterTrackStage = WinterTrackTextureStage.SnowFree;
                        pendingWinterTrackSnowAmount = Mathf.Clamp01(state.SnowAmount);
                        if (!TryPrepareWinterTrackBlend(state))
                        {
                            pendingTransition = GetTextureTransition(state);
                            pendingCurrent = profiles[(int)(pendingTransition >= 1f ? state.Next : state.Current)];
                            pendingNext = profiles[(int)(pendingTransition <= 0f ? state.Current : state.Next)];
                        }
                        pendingSeasonalStrength = Mathf.Clamp01(strength);
                        pendingWinterWeight = GetWinterWeight(state, pendingTransition);
                        if (surfaceSnowRanks != null)
                        {
                            var coverage = SurfaceSnowAccumulation.Coverage(state.SnowAmount,
                                SnowSurfaceProfile.Classify(source.name) == SnowSurfaceKind.Roof);
                            for (var rank = 0; rank < surfaceSnowWeights.Length; rank++)
                                surfaceSnowWeights[rank] = SurfaceSnowAccumulation.Weight((byte)rank, coverage);
                        }
                    }

                    var firstPixel = pendingPixelIndex;
                    var lastPixel = Mathf.Min(outputPixels.Length, firstPixel + pixelBudget);
                    for (var i = firstPixel; i < lastPixel; i++)
                    {
                        var seasonal = Lerp(pendingCurrent[i], pendingNext[i], pendingTransition);
                        outputPixels[i] = Lerp(basePixels[i], seasonal, pendingSeasonalStrength);
                        if (surfaceSnowRanks != null)
                        {
                            outputPixels[i] = Lerp(basePixels[i], profiles[(int)SeasonKind.Winter][i],
                                surfaceSnowWeights[surfaceSnowRanks[i]] * pendingSeasonalStrength);
                            // Alpha may encode smoothness or atlas cutouts: never replace it with snow alpha.
                            outputPixels[i].a = basePixels[i].a;
                        }
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
                        LogAppliedWinterTrackStage();
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

            private void LogAppliedWinterTrackStage()
            {
                if (winterTrackProfiles == null ||
                    pendingWinterTrackStage == lastAppliedWinterTrackStage)
                    return;
                lastAppliedWinterTrackStage = pendingWinterTrackStage;
                Debug.Log("[DVSeasons] Applied winter track stage " +
                    pendingWinterTrackStage + " to " + category + " texture '" +
                    (source == null ? "<destroyed>" : source.name) + "' (snow " +
                    Mathf.RoundToInt(pendingWinterTrackSnowAmount * 100f) + "%).");
            }

            private float GetTextureTransition(SeasonState state)
            {
                var transition = Mathf.Clamp01(state.Transition);
                if (category == TextureCategory.Ballast || category == TextureCategory.Sleeper ||
                    category == TextureCategory.Rail || category == TextureCategory.RoadSurface)
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

            private bool TryPrepareWinterTrackBlend(SeasonState state)
            {
                if (winterTrackProfiles == null ||
                    (state.Next != SeasonKind.Winter && state.Current != SeasonKind.Winter))
                    return false;

                WinterTrackTextureStage lower;
                WinterTrackTextureStage upper;
                float blend;
                WinterTrackTextureProfile.GetBlend(state.SnowAmount,
                    out lower, out upper, out blend);
                pendingWinterTrackStage = blend < 0.5f ? lower : upper;
                var snowFree = profiles[(int)SnowFreeSeason(state)];
                pendingCurrent = GetWinterTrackProfile(blend >= 1f ? upper : lower, snowFree);
                pendingNext = GetWinterTrackProfile(blend <= 0f ? lower : upper, snowFree);
                pendingTransition = blend;
                return true;
            }

            private static SeasonKind SnowFreeSeason(SeasonState state)
            {
                return state.Current == SeasonKind.Winter
                    ? (state.Next == SeasonKind.Winter ? SeasonKind.Summer : state.Next)
                    : state.Current;
            }

            private bool PrepareProfiles(SeasonState state)
            {
                if (category == TextureCategory.RoadSurface && !denseSurfacePrepared)
                {
                    if (!PrepareProfile(SeasonKind.Winter)) return false;
                    PrepareDenseSurfaceSnow(Output.width, Output.height);
                    denseSurfacePrepared = true;
                    return false;
                }
                if (winterTrackProfiles != null &&
                    (state.Current == SeasonKind.Winter || state.Next == SeasonKind.Winter))
                {
                    WinterTrackTextureStage lower, upper;
                    float blend;
                    WinterTrackTextureProfile.GetBlend(state.SnowAmount, out lower, out upper, out blend);
                    if (blend < 1f && !PrepareTrackProfile(lower, state)) return false;
                    if (blend > 0f && !PrepareTrackProfile(upper, state)) return false;
                    return true;
                }
                var transition = GetTextureTransition(state);
                if (transition < 1f && !PrepareProfile(state.Current)) return false;
                if (transition > 0f && !PrepareProfile(state.Next)) return false;
                return true;
            }

            private bool PrepareTrackProfile(WinterTrackTextureStage stage, SeasonState state)
            {
                if (stage == WinterTrackTextureStage.SnowFree)
                    return PrepareProfile(SnowFreeSeason(state));
                var index = (int)stage - 1;
                if (winterTrackProfiles[index] != null) return true;
                Color32[] pixels;
                if (!texturePack.TryLoadWinterTrackPixels(source.name, stage,
                    Output.width, Output.height, out pixels))
                {
                    // Preserve the original non-staged fallback for an invalid
                    // custom track pack instead of failing the entire texture.
                    winterTrackProfiles = null;
                    profiles[(int)SeasonKind.Winter] = null;
                    return false;
                }
                winterTrackProfiles[index] = pixels;
                if (stage == WinterTrackTextureStage.Late) profiles[(int)SeasonKind.Winter] = pixels;
                return false;
            }

            private bool PrepareProfile(SeasonKind season)
            {
                if (profiles[(int)season] != null) return true;
                profiles[(int)season] = LoadOrCreateProfile(season, Output.width, Output.height);
                return false;
            }

            private Color32[] GetWinterTrackProfile(WinterTrackTextureStage stage,
                Color32[] snowFree)
            {
                if (stage == WinterTrackTextureStage.SnowFree) return snowFree;
                var index = (int)stage - 1;
                return index >= 0 && index < winterTrackProfiles.Length
                    ? winterTrackProfiles[index]
                    : snowFree;
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
                sourceReadback.Dispose();
                StreamingTextureReadiness.Cancel(source);
                DisposeOutput();
                basePixels = null;
                profiles = null;
                winterTrackProfiles = null;
                surfaceSnowRanks = null;
                outputPixels = null;
                billboardMipPixels = null;
                ClearPendingUpdate();
            }

            private bool Initialize()
            {
                if (source == null) return false;
                // Keep ballast detailed enough for a cab view, but cap its CPU blend
                // buffer at 512px. The fixed source retains small stones and the work
                // is completed incrementally, avoiding a million-pixel transition
                // on a single frame.
                var detailedTrackSurface = category == TextureCategory.Ballast ||
                    category == TextureCategory.Sleeper || category == TextureCategory.Rail;
                var roadSurface = category == TextureCategory.RoadSurface;
                var effectiveMaximumResolution = detailedTrackSurface
                    ? Mathf.Max(maximumResolution, 512)
                    : roadSurface ? Mathf.Max(maximumResolution,
                        string.Equals(source.name, "AsphaltRoad_01d",
                            StringComparison.OrdinalIgnoreCase) ? 2048 : 1024)
                    : maximumResolution;
                if (detailedTrackSurface)
                    effectiveMaximumResolution = Mathf.Min(effectiveMaximumResolution, 512);
                if (roadSurface)
                    effectiveMaximumResolution = Mathf.Min(effectiveMaximumResolution, 2048);
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
                // A streamed source can report valid dimensions before its requested
                // mip is resident. Keep the set pending instead of reading a stale
                // low-resolution mip and permanently caching the wrong season.
                if(!sourceReadback.TryRead(source,width,height,out basePixels)) return false;
                if (basePixels == null || basePixels.Length != width * height) return false;
                if (detailedTrackSurface && texturePack.HasCompleteWinterTrackSet(source.name))
                    winterTrackProfiles = new Color32[3][];

                profiles = new Color32[4][];
                var keepSpringNeutral = category == TextureCategory.Ballast || category == TextureCategory.Bark ||
                    category == TextureCategory.Sleeper || category == TextureCategory.Rail ||
                    category == TextureCategory.RoadSurface;
                var keepAutumnNeutral = category == TextureCategory.Ballast || category == TextureCategory.Bark ||
                    category == TextureCategory.Rail || category == TextureCategory.RoadSurface;
                profiles[(int)SeasonKind.Spring] = keepSpringNeutral ? basePixels : null;
                profiles[(int)SeasonKind.Summer] = basePixels;
                profiles[(int)SeasonKind.Autumn] = keepAutumnNeutral ? basePixels : null;
                outputPixels = new Color32[basePixels.Length];

                Output = new Texture2D(width, height, TextureFormat.RGBA32,
                    category == TextureCategory.Billboard || category == TextureCategory.RoadSurface)
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
                return true;
            }

            private void PrepareDenseSurfaceSnow(int width, int height)
            {
                Color32[] dense;
                if (!texturePack.TryLoadPixels("SnowSurfaceDense", SeasonKind.Winter, width, height, out dense))
                {
                    Debug.LogWarning("[DVSeasons] Dense surface snow PNG missing; retaining authored winter texture.");
                    return;
                }
                const int maskSize = 128;
                var ranks = SurfaceSnowAccumulation.CreateRanks(maskSize);
                surfaceSnowRanks = new byte[basePixels.Length];
                var winter = profiles[(int)SeasonKind.Winter];
                // Do not mutate the repository's cached pixel arrays shared by other consumers.
                var target = new Color32[basePixels.Length];
                for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    var mx = x * maskSize / (float)width;
                    var my = y * maskSize / (float)height;
                    var ix = (int)mx;
                    var iy = (int)my;
                    var nx = (ix + 1) % maskSize;
                    var ny = (iy + 1) % maskSize;
                    surfaceSnowRanks[i] = (byte)Mathf.RoundToInt(Mathf.Lerp(
                        Mathf.Lerp(ranks[iy * maskSize + ix], ranks[iy * maskSize + nx], mx - ix),
                        Mathf.Lerp(ranks[ny * maskSize + ix], ranks[ny * maskSize + nx], mx - ix), my - iy));
                    var snow = dense[i];
                    // Restrained albedo avoids a blown-white filter under direct sun.
                    snow.r = (byte)Mathf.Min(snow.r, 232);
                    snow.g = (byte)Mathf.Min(snow.g, 232);
                    snow.b = (byte)Mathf.Min(snow.b, 232);
                    target[i] = Lerp(winter[i], snow, 0.90f);
                    if (basePixels[i].r < 5 && basePixels[i].g < 5 && basePixels[i].b < 5 &&
                        winter[i].r < 5 && winter[i].g < 5 && winter[i].b < 5)
                        target[i] = basePixels[i]; // Keep unused black atlas islands empty.
                    target[i].a = basePixels[i].a;
                }
                profiles[(int)SeasonKind.Winter] = target;
                Debug.Log("[DVSeasons] Prepared growing snow patches for '" + source.name +
                    "': early 22%, middle 65%, full 95-98% coverage.");
            }

            private void UploadOutput(Color32[] pixels, bool preserveThinBillboardCoverage)
            {
                Output.SetPixels32(pixels, 0);
                if (category == TextureCategory.RoadSurface)
                {
                    Output.Apply(true, false);
                    return;
                }
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
                // These profiles are generated from the native source or the
                // bare-tree atlas. Loading another image first only discarded it.
                if (season == SeasonKind.Spring &&
                    (category == TextureCategory.Foliage || category == TextureCategory.Billboard))
                    return AdjustProfile(basePixels, season, category, width, height);
                if (season == SeasonKind.Winter && category == TextureCategory.Billboard && !evergreen &&
                    texturePack.TryLoadBareTreeBillboardPixels(source.name, width, height, out packed))
                    return AdjustProfile(packed, season, category, width, height);
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
                var adjusted = season == SeasonKind.Autumn && category != TextureCategory.Sleeper
                    ? EnhanceAutumnProfile(pixels)
                    : pixels;
                if (season == SeasonKind.Spring &&
                    (category == TextureCategory.Foliage || category == TextureCategory.Billboard))
                {
                    adjusted = new Color32[basePixels.Length];
                    for (int i=0;i<adjusted.Length;i++)
                    {
                        var p=basePixels[i];float r=p.r/255f,g=p.g/255f,b=p.b/255f;
                        SpringAppearance.Recolor(ref r,ref g,ref b,evergreen);
                        adjusted[i]=new Color32((byte)Mathf.RoundToInt(r*255),
                            (byte)Mathf.RoundToInt(g*255),(byte)Mathf.RoundToInt(b*255),p.a);
                    }
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

        private sealed class SurfaceMaterialBinding
        {
            public MeshRenderer Renderer;
            public int Slot;
            public Material Original;
            public Material Seasonal;
        }
        private readonly List<SurfaceMaterialBinding> surfaceMaterials = new List<SurfaceMaterialBinding>();
        private readonly HashSet<int> scannedSurfaceRenderers = new HashSet<int>();

        private readonly Dictionary<string, SeasonalTextureSet> sets = new Dictionary<string, SeasonalTextureSet>();
        private readonly Dictionary<string, MaterialBinding> materialBindings = new Dictionary<string, MaterialBinding>();
        private readonly HashSet<int> scannedMaterials = new HashSet<int>();
        private readonly Queue<SeasonalTextureSet> trackUpdateQueue = new Queue<SeasonalTextureSet>();
        private readonly HashSet<SeasonalTextureSet> queuedTrackUpdates = new HashSet<SeasonalTextureSet>();
        private readonly Queue<SeasonalTextureSet> updateQueue = new Queue<SeasonalTextureSet>();
        private readonly HashSet<SeasonalTextureSet> queued = new HashSet<SeasonalTextureSet>();
        private readonly SeasonAssetBundleRepository texturePack;
        private float nextScanTime;
        private IEnumerator<int> materialScan;
        private int lastStyleKey = int.MinValue;
        private int configurationKey = int.MinValue;
        private bool active;
        private bool proceduralSnow;
        private SeasonState coverageState;
        private float nextBindingCheck;

        public SeasonalTextureController(SeasonAssetBundleRepository texturePack)
        {
            this.texturePack = texturePack;
        }

        public void Apply(SeasonState state, SeasonModSettings settings, bool useProceduralSnow = false, float? coverageOverride = null)
        {
            if (!settings.SeasonalTexturesEnabled)
            {
                if (active) Reset();
                return;
            }
            if(coverageOverride.HasValue)
            {
                float transition=Mathf.Round(state.Transition*32f)/32f;
                if(coverageState==null || coverageState.Current!=state.Current || coverageState.Next!=state.Next ||
                    coverageState.Transition!=transition || Mathf.Abs(coverageState.SnowAmount-coverageOverride.Value)>0.0001f)
                    coverageState=new SeasonState(state.Phase,state.Current,state.Next,transition,
                        coverageOverride.Value,state.TemperatureCelsius,state.WinterWetnessEquivalent);
                state=coverageState;
            }
            var newConfigurationKey = settings.SeasonalTextureResolution * 397 ^ settings.MaximumSeasonalTextures ^
                (settings.TerrainTextureChanges ? 0x10000 : 0) ^
                (settings.VegetationTextureChanges ? 0x20000 : 0);
            if (configurationKey != int.MinValue && configurationKey != newConfigurationKey) Reset();
            if (proceduralSnow != useProceduralSnow) SwitchSnowMode(useProceduralSnow);
            active = true;
            configurationKey = newConfigurationKey;
            if (materialScan == null && Time.realtimeSinceStartup >= nextScanTime)
            {
                nextScanTime = Time.realtimeSinceStartup + 30f;
                materialScan = ScanMaterials(settings).GetEnumerator();
            }
            using (SnowPerformance.Measure("season-material-discovery"))
                FrameDiscovery.Advance(ref materialScan);

            var styleKey = BuildStyleKey(state, settings.TextureChangeStrength);
            if (styleKey != lastStyleKey)
            {
                lastStyleKey = styleKey;
                foreach (var set in sets.Values) Enqueue(set);
            }
            // Track surfaces have their own small, high-priority budget. They use
            // three authored winter stages and must finish a stage before the next
            // snow step arrives; otherwise a large vegetation queue can keep
            // restarting their 512px blends and only the final winter texture ever
            // reaches the material.
            ProcessTrackUpdates(state, settings, styleKey);

            // TextureUpdatesPerFrame controls both the number of ordinary texture
            // sets touched and a strict pixel budget. Large vegetation albedos
            // therefore span several frames instead of executing a full CPU blend
            // and upload in one transition frame.
            var setBudget = Mathf.Clamp(settings.TextureUpdatesPerFrame, 1, 4);
            var pixelBudget = setBudget * 32768;
            for (var i = 0; i < setBudget && updateQueue.Count > 0 && pixelBudget > 0; i++)
            {
                var set = updateQueue.Dequeue();
                queued.Remove(set);
                var processed = set.UpdateChunk(state, settings.TextureChangeStrength,
                    styleKey, pixelBudget);
                pixelBudget -= Mathf.Max(0, processed);
                if (set.HasPendingUpdate || set.NeedsUpdate(styleKey)) Enqueue(set);
            }
            // Ready textures retain their bindings; polling hundreds of native
            // material properties every frame does not improve seasonal blending.
            if(Time.realtimeSinceStartup>=nextBindingCheck)
            {nextBindingCheck=Time.realtimeSinceStartup+0.5f;ApplyBindings();}
        }

        private void ProcessTrackUpdates(SeasonState state, SeasonModSettings settings,
            int styleKey)
        {
            // One 64K blend chunk per frame: eight ready 512px track textures
            // need 32 slices. First-use readback/upload/profile preparation has
            // separate slices, independent of the ordinary vegetation queue.
            const int trackPixelBudget = 65536;
            if (trackUpdateQueue.Count == 0) return;
            var set = trackUpdateQueue.Dequeue();
            queuedTrackUpdates.Remove(set);
            set.UpdateChunk(state, settings.TextureChangeStrength, styleKey,
                trackPixelBudget);
            if (set.HasPendingUpdate || set.NeedsUpdate(styleKey)) Enqueue(set);
        }

        public void Dispose() { Reset(); }

        private IEnumerable<int> ScanMaterials(SeasonModSettings settings)
        {
            if (settings.TerrainTextureChanges || settings.VegetationTextureChanges)
                foreach (var step in ScanVegetationMaterials(settings)) yield return step;
            if (settings.TerrainTextureChanges && !proceduralSnow)
                foreach (var step in ScanBuiltSurfaceTextures(settings)) yield return step;
        }

        private IEnumerable<int> ScanVegetationMaterials(SeasonModSettings settings)
        {
            Material[] materials;
            using (SnowPerformance.Measure("season-material-snapshot"))
                materials = Resources.FindObjectsOfTypeAll<Material>();
            for (var i = 0; i < materials.Length; i++)
            {
                yield return 0;
                var material = materials[i];
                if (material == null) continue;
                var materialId = material.GetInstanceID();
                if (!scannedMaterials.Add(materialId)) continue;
                if (material.name.IndexOf("DVSeasons", StringComparison.OrdinalIgnoreCase) >= 0) continue;
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
                    if (IsRoadSurfaceTexture(texture.name))
                        category = TextureCategory.RoadSurface;
                    else if (!TryClassifyVegetation(texture.name, out category) && !TryClassifyVegetation(description, out category))
                        continue;
                    var trackSurface = category == TextureCategory.Ballast ||
                        category == TextureCategory.Sleeper || category == TextureCategory.Rail ||
                        category == TextureCategory.RoadSurface;
                    // Ballast/sleepers may render outside deferred and need native
                    // winter albedo even when the procedural overlay is active.
                    if (trackSurface && (!settings.TerrainTextureChanges ||
                        (proceduralSnow && (category==TextureCategory.Rail || category==TextureCategory.RoadSurface)))) continue;
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

        private IEnumerable<int> ScanBuiltSurfaceTextures(SeasonModSettings settings)
        {
            var added = 0;
            foreach (var renderer in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                yield return 0;
                if (renderer == null || !renderer.gameObject.scene.IsValid() ||
                    !renderer.gameObject.scene.isLoaded || !scannedSurfaceRenderers.Add(renderer.GetInstanceID())) continue;
                if (SnowAnimalExclusion.IsAnimal(renderer)) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                var shared = renderer.sharedMaterials;
                var changed = false;
                for (var slot = 0; slot < shared.Length && slot < filter.sharedMesh.subMeshCount; slot++)
                {
                    var original = shared[slot];
                    if (original == null || !original.HasProperty("_MainTex") || original.renderQueue > 2500) continue;
                    var texture = original.GetTexture("_MainTex") as Texture2D;
                    if (texture == null || IsRoadSurfaceTexture(texture.name) || texture.name.Contains("[DVSeasons")) continue;
                    var kind = SnowSurfaceProfile.Classify(texture.name);
                    if (kind == SnowSurfaceKind.None || !IsUpwardSurface(renderer, filter.sharedMesh, slot, texture.name)) continue;
                    var set = GetOrCreate(texture, TextureCategory.RoadSurface, settings, false);
                    if (set == null) continue;
                    // Clone only this renderer's material slot; retain the native
                    // shader, UVs, normals and all non-albedo properties.
                    var material = new Material(original) { name = original.name + " [DVSeasons Surface Texture]" };
                    surfaceMaterials.Add(new SurfaceMaterialBinding { Renderer = renderer, Slot = slot,
                        Original = original, Seasonal = material });
                    materialBindings.Add(material.GetInstanceID() + "|_MainTex", new MaterialBinding
                        { Material = material, Property = "_MainTex", Original = texture, Set = set });
                    shared[slot] = material;
                    changed = true;
                    added++;
                }
                if (changed) renderer.sharedMaterials = shared;
            }
            if (added > 0) Debug.Log("[DVSeasons] Bound " + added + " roof/pavement material slots to winter PNG textures (native shaders).");
        }

        private static bool IsUpwardSurface(MeshRenderer renderer, Mesh mesh, int slot, string textureName)
        {
            if (!mesh.isReadable)
            {
                // Tile-only roof materials are unambiguous. Shared sheet metal
                // and concrete without readable geometry need a flat bounds check.
                if (textureName.StartsWith("MB_rooftile_", StringComparison.OrdinalIgnoreCase)) return true;
                var size = renderer.bounds.size;
                return size.y < Mathf.Min(size.x, size.z) * 0.12f;
            }
            var vertices = mesh.vertices;
            var indices = mesh.GetTriangles(slot);
            var normalMatrix = renderer.localToWorldMatrix.inverse.transpose;
            double topArea = 0, totalArea = 0;
            // Bounded representative sampling; never alter or copy mesh topology.
            var step = Mathf.Max(1, indices.Length / (3 * 256));
            for (var triangle = 0; triangle < indices.Length / 3; triangle += step)
            {
                var i = triangle * 3;
                var normal = Vector3.Cross(vertices[indices[i+1]] - vertices[indices[i]],
                    vertices[indices[i+2]] - vertices[indices[i]]);
                var area = normal.magnitude;
                if (area <= 0.000001f) continue;
                totalArea += area;
                if (normalMatrix.MultiplyVector(normal).normalized.y >= 0.38f) topArea += area;
            }
            // Mixed wall+roof slots cannot be selectively recoloured by one UV
            // texture. Leave those untouched rather than painting whole facades.
            return SnowSurfaceProfile.IsMostlyUpward(topArea, totalArea);
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
                category == TextureCategory.Sleeper || category == TextureCategory.Rail ||
                category == TextureCategory.RoadSurface;
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
                if (proceduralSnow && binding.Set.LegacyOnly) continue;
                if (binding.Material != null && binding.Set.IsReady && binding.Material.GetTexture(binding.Property) != binding.Set.Output)
                    binding.Material.SetTexture(binding.Property, binding.Set.Output);
            }
        }

        private void SwitchSnowMode(bool procedural)
        {
            proceduralSnow = procedural;
            // A rendering mode is not a texture-resolution change. Keep source
            // readbacks, winter profiles and completed outputs warm across toggles.
            foreach (var binding in materialBindings.Values)
            {
                if (!binding.Set.LegacyOnly || binding.Material == null) continue;
                if (procedural && binding.Material.GetTexture(binding.Property) == binding.Set.Output)
                    binding.Material.SetTexture(binding.Property, binding.Original);
            }
            foreach (var binding in surfaceMaterials)
            {
                if (binding.Renderer == null) continue;
                var shared = binding.Renderer.sharedMaterials;
                if (binding.Slot >= shared.Length) continue;
                var from = procedural ? binding.Seasonal : binding.Original;
                if (shared[binding.Slot] != from) continue;
                shared[binding.Slot] = procedural ? binding.Original : binding.Seasonal;
                binding.Renderer.sharedMaterials = shared;
            }
            // The earlier procedural scan deliberately skipped legacy materials.
            // Start a new scan immediately, without destroying common bindings.
            if (materialScan != null) materialScan.Dispose();
            materialScan = null;
            scannedMaterials.Clear();
            nextScanTime = nextBindingCheck = 0f;
            trackUpdateQueue.Clear(); queuedTrackUpdates.Clear();
            updateQueue.Clear(); queued.Clear();
            foreach (var set in sets.Values) Enqueue(set);
        }

        private void Reset()
        {
            foreach (var binding in surfaceMaterials)
            {
                if (binding.Renderer == null) continue;
                var shared = binding.Renderer.sharedMaterials;
                if (binding.Slot < shared.Length && shared[binding.Slot] == binding.Seasonal)
                {
                    shared[binding.Slot] = binding.Original;
                    binding.Renderer.sharedMaterials = shared;
                }
            }
            foreach (var binding in materialBindings.Values)
            {
                if (binding.Material == null) continue;
                var current = binding.Material.GetTexture(binding.Property);
                if (binding.Set.Output == null || current == binding.Set.Output) binding.Material.SetTexture(binding.Property, binding.Original);
            }
            foreach (var set in sets.Values) set.Dispose();
            foreach (var binding in surfaceMaterials)
                if (binding.Seasonal != null) UnityEngine.Object.Destroy(binding.Seasonal);
            surfaceMaterials.Clear();
            scannedSurfaceRenderers.Clear();
            sets.Clear();
            materialBindings.Clear();
            scannedMaterials.Clear();
            trackUpdateQueue.Clear();
            queuedTrackUpdates.Clear();
            updateQueue.Clear();
            queued.Clear();
            if (materialScan != null) materialScan.Dispose();
            materialScan = null;
            nextScanTime = nextBindingCheck = 0f;
            lastStyleKey = int.MinValue;
            configurationKey = int.MinValue;
            active = false;coverageState=null;
        }

        private void Enqueue(SeasonalTextureSet set)
        {
            if (set.IsFailed || (proceduralSnow && set.LegacyOnly)) return;
            if (set.IsTrackSurface)
            {
                if (queuedTrackUpdates.Add(set)) trackUpdateQueue.Enqueue(set);
                return;
            }
            if (queued.Add(set)) updateQueue.Enqueue(set);
        }

        private static int BuildStyleKey(SeasonState state, float strength)
        {
            var transitionStep = Mathf.RoundToInt(Mathf.Clamp01(state.Transition) * 32f);
            var snowStep = SnowCoverProfile.GetGroundTextureStep(state.SnowAmount);
            var strengthStep = Mathf.RoundToInt(Mathf.Clamp01(strength) * 20f);
            return ((int)state.Current << 24) | ((int)state.Next << 22) |
                (transitionStep << 16) | (snowStep << 10) | strengthStep;
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
            if (ContainsAny(value, "railmed_d", "railold_d"))
            {
                category = TextureCategory.Rail;
                return true;
            }
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
            if (ContainsAny(value, "bark", "trunk", "branch", "wood", "stump", "stumps",
                "deadwood", "dead wood", "dead_log", "dead log", "root", "snag",
                "fallen_log", "fallen log", "log_", "_log", " logs"))
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

        private static bool IsRoadSurfaceTexture(string textureName)
        {
            return string.Equals(textureName, "SidewalkTiles_01d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(textureName, "AsphaltRoad_01d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(textureName, "Sidewalk_01d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(textureName, "Roads_LOD_01d", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(textureName, "RoadDetail", StringComparison.OrdinalIgnoreCase) ||
                // Concrete is shared with walls: the slope-masked surface snow
                // pass now handles it instead of replacing its entire albedo.
                string.Equals(textureName, "AsphaltTiling_01d", StringComparison.OrdinalIgnoreCase);
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
