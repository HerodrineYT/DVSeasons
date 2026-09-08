# DVSeasons AssetBundle project

This project must be opened with Unity `2019.4.40f1`, the same editor branch used by Derail Valley build 99.

The source textures are stored under `Assets/DVSeasons/DV99/{spring,autumn,winter}`. The editor build method is:

`DVSeasons.AssetBundleBuild.DVSeasonsAssetBundleBuilder.Build`

It imports all 160 images as real Unity `Texture2D` assets with mipmaps, alpha support and high-quality standalone texture compression. This includes 24 early/middle/late railway textures, seven winter road/sidewalk/station textures, the linear repeating `WaterIceNormal`, and the seamless `WaterIceAlbedo`. It builds three 16-slice `Texture2DArray` assets for MicroSplat terrain, then creates a Windows x64 LZ4 bundle in the path supplied with `-bundleOutput` (or `Build/Windows` when run manually).

Run `Tools/build_assetbundle.ps1` from the repository root to build, verify, repack to LZMA and update `Resources/Runtime/AssetBundles/dvseasons_dv99`. The 24 staged railway PNGs, seven road/sidewalk/station PNGs and both ice textures are also shipped as runtime overrides so texture-only releases remain inspectable and do not depend on rebuilding the large terrain bundle.
