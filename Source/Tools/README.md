# Development tools

- `build.ps1` — normal C# build, tests and layout verification.
- `verify_project.ps1` — version, source-resource and output-layout checks.
- `build_assetbundle.ps1` — Unity 2019.4.40f1 AssetBundle build and LZMA repack.
- `generate_dvseasons_texture_pack.py` — generator for the original 90-texture terrain/vegetation subset; the current authoritative 127 PNG inputs are stored in `DVSeasons.Unity/Assets/DVSeasons/DV99`.
- `patch_terrain_texture_sets.py` — updates selected terrain `Texture2D` assets and their 16-slice `Texture2DArray` directly in an existing bundle when the matching Unity editor is unavailable.
- Other Python scripts inspect, extract, patch or verify Unity assets during development.
- `VerifyDvSeasonsAssemblyLoad.cs` is the source of the optional assembly-load smoke-test helper kept for diagnostics.

Python asset tools use packages listed in `requirements.txt`. None of these tools or dependencies is included in the installed mod.
