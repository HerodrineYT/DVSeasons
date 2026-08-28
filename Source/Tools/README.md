# Development tools

- `build.ps1` — normal C# build, tests and layout verification.
- `verify_project.ps1` — version, source-resource and output-layout checks.
- `package_dvseasons.ps1` — validated release staging and ZIP creation.
- `package_source.ps1` — GitHub-style source ZIP and combined UMM-with-source ZIP.
- `build_assetbundle.ps1` — Unity 2019.4.40f1 AssetBundle build and LZMA repack.
- `generate_dvseasons_texture_pack.py` — generator for the original 90-texture terrain/vegetation subset; the current authoritative 123 PNG inputs are stored in `DVSeasons.Unity/Assets/DVSeasons/DV99`.
- Other Python scripts inspect, extract, patch or verify Unity assets during development.
- `VerifyDvSeasonsAssemblyLoad.cs` is the source of the optional assembly-load smoke-test helper kept for diagnostics.

Python asset tools use packages listed in `requirements.txt`. None of these tools or dependencies is included in the installed mod.
