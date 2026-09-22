# DVSeasons AssetBundle project

This project must be opened with Unity `2019.4.40f1`, the same editor branch used by Derail Valley build 99.

The source textures are stored under `Assets/DVSeasons/DV99/{spring,autumn,winter}`. The editor build method is:

`DVSeasons.AssetBundleBuild.DVSeasonsAssetBundleBuilder.Build`

The project contains 173 source PNGs, including terrain sources and texture aliases. The builder creates three Windows x64 bundles with mipmaps and alpha support:

- `dvseasons_dv99`: 75 textures, 10 shaders and three 16-slice terrain arrays;
- `dvseasons_winter`: 22 prepared winter textures;
- `dvseasons_tracks`: 20 prepared railway textures.

The 42 prepared textures retain their original dimensions and readable, uncompressed RGBA/RGB pixels. This avoids runtime PNG decoding and GPU readback when a CPU pixel profile is needed. Normal maps retain their raw linear RGB channels. Aliases resolve to the canonical asset rather than duplicating it.

Run `Tools/build_assetbundle.ps1` from the repository root to build, verify, repack to LZMA and update all three files under `Resources/Runtime/AssetBundles`. Splitting the bundles keeps each file below the source package's 100 MB limit without reducing image quality. Bundle loading is asynchronous; shader lookup does not wait for the two texture bundles. Standard runtime PNGs are no longer shipped. Optional replacements belong in `Overrides/Seasonal`; see `Resources/Runtime/Overrides/README.txt`.
