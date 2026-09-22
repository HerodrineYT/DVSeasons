# Native vehicle snow material sharing

Base: the requested 0.3.3 Nexus archive plus the cold-start settings/hints build
(`artifacts/releases/base-033-cold-start-20260921`). Shader bundles, textures,
snow coverage, car limit and cold-start behavior are unchanged. MP protocol is 13.

The reported run contains one post-startup winter window at 40.7 FPS,
`render-commands=2.49/14.96 ms`, `snow-vehicle-culling=1.98/13.92 ms`, and
12,638 average native material slots. It does not contain GPU timing or a matched
summer/baseline comparison. The whole-game FPS gain needs another live run.

## Changes

- Compatible ordinary MeshRenderers share snow materials even when their original
  Standard material does not enable instancing. Only the mod-owned variants enable
  instancing, with per-car IDs in the existing shader's instanced property.
- Indexed property blocks, custom vertex streams, static batching and skinned
  renderers keep private variants. New indexed overrides request a rebind.
- Source materials and their instancing flags remain untouched. Source updates
  reapply the variant's instancing flag. Release preserves original updates,
  external clones, individual property blocks and repainting.
- Fallback batching uses a cached list of only the parts that require extra
  surface/exclusion geometry, across all LODs. Native-only parts/slots do not
  construct fallback batches. RefreshParts rebuilds this list on streaming,
  material and cargo changes; camera movement does not rebuild it.
- Reports add `snow-native-sharing(shared-slots/private-slots/materials)`,
  `snow-fallback-index`, `cold-start-hints` and `season-network-publish`.

## Verification

314 xUnit tests and project/localization checks pass; no build warnings or errors.
Unity 2019.4 / D3D11 tests use production assemblies and the unchanged bundle:

| Controlled 128-car fixture | Before | After |
| --- | ---: | ---: |
| Native materials, source instancing disabled | 512 | 7 |
| Camera render time with native snow, ms | 2.010 | 0.834 |
| Fallback index rebuild: 1,025 parts / 256 fallback parts, ms | 0.6614 | 0.1742 |
| Meshes in the fallback index | 9 | 2 |

Camera timing includes CPU and completed GPU work in an 800x600 regression scene;
it is not an estimate of gameplay FPS. A repeated fixture with another indexed
property override uses 8 materials and preserves the override.

Checks cover dry GBuffer parity, normals/detail/cutouts, accumulation, heat/melt,
directional side snow, car limits, original restoration and repainting. Mixed
native/emissive-fallback surfaces match the ordered reference pixel output,
including a material changing back to fallback. The full yard suite also passes
motion, frustum, overlapping cars, custom streams, skinned/static/mirrored renderers,
deleted geometry, origin shifts, and limits 0/96/64.

Logs: `artifacts/verification/native-fps-*.log`. Pre-change Player.log and assemblies:
`artifacts/backups/before-native-fps-20260921`.

Unity entry points in namespace `DVSeasons.AssetBundleBuild`:
`SnowNativeMaterialVerification.RunAutomaticInstancing`,
`SnowFallbackIndexVerification.Run`, `SnowYardBatchVerification.Run`.
Set `DVSEASONS_VERIFY_GAME` to the game directory and optionally
`DVSEASONS_VERIFY_MOD` to the baseline runtime for comparisons.
