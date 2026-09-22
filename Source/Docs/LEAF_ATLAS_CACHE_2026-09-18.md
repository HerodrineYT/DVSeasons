# Cached autumn leaf atlas

The local `Player.log` dated 2026-09-18 contains a 2854.69 ms
`autumn-leaf-cover` maximum immediately after the first `Physical autumn leaf
cover ready` message. The controller generated a 1024 by 1024 atlas with 16
species/colour combinations synchronously on first activation, including roughly
two million Perlin-noise evaluations. Its texture was already retained across
season changes; another in-memory dictionary would not remove that initial work.

The exact procedural output is now baked into
`Resources/Runtime/Textures/autumn_leaf_atlas.png` and delivered as
`Textures/autumn_leaf_atlas.png`. The first leaf activation reads and decodes this
ready image directly, without GPU readback or procedural pixel generation. The
controller retains the loaded texture until session disposal. A missing or
invalid file falls back to the original generator.

Appearance is unchanged: 1024 by 1024 RGBA, 16 tiles, sRGB, 11 mip levels,
trilinear filtering and clamp wrapping. The runtime texture releases its CPU
pixel storage after upload.

## Reproduce

Run `Tools/verify_autumn_leaf_atlas.ps1` with `-DVInstallDir` and `-UnityEditor`.
Add `-Bake` only when intentionally regenerating the asset after changes to the
procedural authoring function. The script compiles the actual runtime source
alongside its test helper and uses Unity to generate the reference, decode the
delivered PNG and compare every channel at every mip level. No game build or
AssetBundle rebuild is needed for this check.

Recorded result in `artifacts/verification/leaf-atlas-cache-unity.log`:

- Exact equality for 1,398,101 pixels across all 11 mip levels.
- Procedural generation: 1595.77 ms.
- Ready texture runtime loading: 33.37 ms.
- Missing-file and invalid-dimension fallback checks passed.
- PNG size: 1,316,971 bytes.
- PNG SHA-256: `C2B767B138802416071F74BFFD1983C7D08146CC21B7A4CFB0B09B9E73300781`.

These are one-run editor timings on the development computer, measuring atlas
creation/loading only. They do not measure whole-game FPS or prove that every
remaining seasonal hitch is removed.
