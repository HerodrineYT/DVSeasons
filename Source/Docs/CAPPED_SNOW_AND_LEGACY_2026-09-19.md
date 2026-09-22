# Capped vehicle snow and legacy texture switching

The supplied Player.log isolates vehicle surface submission: baseline 32.37 FPS,
NoVehicleSurfaces 58.97, NoShading 32.88, NoAll 59.09, restored baseline 33.09.
The user had a 35-car limit, yet a normal winter submission visited about 19,534
parts and 2,099 LOD groups, then submitted 767 full and 1,380 excluded surfaces.
Exclusion work defeated the purpose of the limit.

## Changes

- Capped cars at least 20 metres from the camera now use a single instanced box
  exclusion draw for the fleet. The fragment shader reconstructs the **visible
  depth** and writes the exclusion marker only inside the current car volume.
  Ground/sky behind a volume is not its silhouette. These cars bypass per-part
  LOD, visibility, bounds, OBB validation, request collection and native draws.
- Selected cars retain their exact surface IDs, snow coordinates, slope, thermal
  response, accumulated masks and wind-driven side snow. Exact selected, rail,
  junction and animal draws override the approximate masks.
- Ordered full batching now considers the remaining native cars; capped volume
  cars no longer cause the full-snow batching ratio guard to reject the fleet.
- Near cars, pending geometry, exploded cars, mirrored roots, far-clip crossings,
  missing shader support, non-rolling-stock objects and stereo rendering retain
  the native path. **Limit 0 remains unlimited and does not use volume exclusion.**
- A distant capped car's volume is deliberately an approximation: other visible
  geometry actually inside that volume can also lose snow, and animated parts
  outside its envelope can require native rendering. This trade-off applies only
  to cars the user already excluded from detailed snow, outside the near zone.
- Performance reports include `snow-capped-volumes(cars/commands)` and count the
  extra volume command in `snow-vehicle-commands`.
- Switching snow systems no longer resets the entire seasonal texture repository.
  Prepared ballast/vegetation and legacy road/concrete profiles survive. Legacy
  bindings and per-renderer surface materials are restored/suspended selectively;
  a fresh discovery scan starts immediately for previously skipped road materials.
  Cold first-use textures still prepare incrementally. A warmed legacy mode
  reuses its completed outputs immediately. Resolution/category settings still
  perform the full reset, and session disposal releases every cached resource.

## Verification

Unity 2019.4.40f1, D3D11, RTX 4060; compiled runtime DLL and packaged shader.
The timings below are **synthetic fixtures, not game FPS or a predicted FPS gain**.
Five alternating trials, median of 60 frames including native camera rendering
and GPU completion; CPU-only timings use 180 submissions.

| Scene, limit 35 | Record + render + GPU, old → new | CPU record, old → new | Native surface requests |
| --- | --- | --- | --- |
| 128 cars, 1,025 parts | 3.397 → 1.769 ms | 1.846 → 1.088 ms | 1,025 → 280 + 93 volumes in one command |
| 224 cars, ~17,921 parts, 1,792 LOD groups | 18.130 → 4.819 ms | 13.629 → 2.509 ms | 3,585 → 560 + 189 volumes in one command |

`limit-volume-unity.log` and `limit-volume-heavy.log`: limits 0/64/35/1,
movement, rotation, origin shift and orthographic rendering. Zero changed selected
snow IDs/coordinates/slopes, zero lost exclusions, zero changed background/ground
pixels in these fixtures. Pending/exploded/near-camera guards verified.

`limit-legacy-textures.log`: cold startup waits for the authored bundles; real
road/concrete material bindings receive finished winter pixels; ballast reaches
the late winter stage; procedural mode restores native textures/material slots;
returning to legacy restores the existing winter outputs without recreating sets.

`limit-legacy-yard-parity.log`: existing exact geometry scheduler regressions pass,
including cutouts, native fallbacks, motion, overlap, object limits and origin shift.
This suite explicitly disables volume approximation, which has the separate suite
above. Existing stereo release/descriptor/culling checks pass; the packed original
mono/stereo variants are present. No headset run was performed. Build: 293 core
tests and 52 localization entries pass.

For the next real yard comparison, keep camera/weather/graphics identical and
compare unlimited, 64 and 35. Check capped-volume counts, parts visited, native
draws and whole-frame FPS; a zero volume count means a fallback condition applies.
