# Development tools

- `build.ps1` — normal C# build, tests and layout verification.
- `verify_project.ps1` — version, source-resource and output-layout checks.
- `build_assetbundle.ps1` — Unity 2019.4.40f1 AssetBundle build and LZMA repack.
- `package_combined.ps1` — creates the Nexus ZIP and one GitHub source ZIP, then verifies that every packaged file is below GitHub's 100,000,000-byte per-file limit.
- `generate_dvseasons_texture_pack.py` — generator for the original 90-texture terrain/vegetation subset; the current authoritative 160 PNG inputs are stored in `DVSeasons.Unity/Assets/DVSeasons/DV99`.
- `prepare_winter_surface_textures.py` — fits the supplied road/sidewalk images to DV99's exact source dimensions, restores original alpha, and converts the generated ice reference into a periodic albedo. The lightly snow-covered station concrete is supplied as a separate loose override.
- `generate_ice_normal.py` — deterministic standard-library generator for the seamless linear winter-water normal map mirrored into Unity sources and the loose runtime override.
- `prepare_staged_track_textures.py` — maps the supplied railway JPEG atlases to three monotonic snow stages, restores vanilla sizes/alpha, creates the missing ballast intermediate and mirrors the PNGs into runtime, Unity sources and the standalone texture library.
- `patch_terrain_texture_sets.py` — updates selected terrain `Texture2D` assets and their 16-slice `Texture2DArray` directly in an existing bundle when the matching Unity editor is unavailable.
- Other Python scripts inspect, extract, patch or verify Unity assets during development.
- `VerifyDvSeasonsAssemblyLoad.cs` is the source of the optional assembly-load smoke-test helper kept for diagnostics.

Python asset tools use packages listed in `requirements.txt`. None of these tools or dependencies is included in the installed mod.
# Проверка версий используемых модов

`check_mod_updates.ps1` читает `LoadAfter` из метаданных DVSeasons, находит установленные моды и сравнивает их версии с официальными UMM `Repository`-лентами. `build.ps1` запускает эту проверку перед каждой компиляцией. Ключ `-FailOnOutdated` подходит для строгой релизной проверки.
