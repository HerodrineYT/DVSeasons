# Winter rendering cost — 2026-09-18

The user reports a persistent winter frame-rate drop near cliffs and when viewing a wagon yard, including while stationary. The screenshot is not a GPU profile. Earlier client logs show hundreds of additional vehicle surface draws, but do not establish the complete CPU/GPU cause of the reported 30 FPS.

## Changes

- Batch compatible repeated mesh exclusion draws using GPU instancing. These draws prevent snow inside cabins and on capped cars or surfaces already using a native winter texture. Only consecutive exclusion runs are combined; every full snow draw and unsupported renderer flushes the run, preserving ordering.
- Preserve native `DrawRenderer` for skinned meshes, static batches, mirrored transforms, additional vertex streams and singleton batches. Keep the original alpha cutoff, UV transform, visible-depth comparison, layer/frustum checks and mesh silhouettes. Matrices and batch containers are reused, and their storage grows only when needed.
- Cache mesh reuse counts until topology changes. Unique meshes bypass matrix reads and grouping entirely.
- Evaluate remaining vehicle snow only for visible, selected, prepared vehicles. Upload that array only when a positive vehicle surface ID is drawn.
- In the fullscreen snow shader, reject sky before reading vehicle data, avoid unused world-normal work on vehicle pixels, skip vehicle height sampling when snow or remaining coverage is zero, skip the far-map result after the distant transition is complete, and skip grain texture sampling at zero weight.
- Preserve all height comparisons, surface derivatives, map sizes, coverage, distance and shader output channels. This does not reduce the number of snow-covered cars or remove glare protection or ice effects.
- Add actual vehicle command counts and batched-object counts to the performance log. GPU profiler command-buffer samples distinguish snow buffer preparation, vehicle surfaces and final snow shading; ordinary performance scopes still measure CPU submission only.

## Verification

Production build: 262 tests and 48 translation checks passed, no compiler warnings/errors. The previous client Alt/weather-panel fix remains in place.

The source-shader exclusion regression reduced 201 logical exclusion submeshes to 7 actual draw commands (196 instances in 2 batches, plus native fallbacks), with identical surface-ID pixels. The extended fixture also exercises singleton meshes and interleaved full/exclusion draws. This is a synthetic scene, not a measurement of game FPS.

The snow shader comparison uses the preserved pre-change shader, not a reimplementation. It covers 96 HDR/LDR combinations of full/partial/no snow, near/far/distant transitions, slopes, shelter, vehicle masks, blade offsets, sky, depth ranges and object exclusions. All coverage decisions agree; maximum channel difference is 1.788e-7 (tolerance 1e-6).

Synthetic shader timing is variable. Vehicle-only shading improved by approximately 3% in two runs and sky-heavy shading improved; distant-rock timings were inconclusive. These figures concern one shader pass and must not be presented as an expected whole-game FPS increase. Command recording alone can become slightly more expensive because instancing groups meshes and copies transforms; complete-render timing is needed to evaluate the tradeoff.

Sources: `SnowExclusionInstancingVerification` (source and packed-bundle modes), `SnowProceduralCostVerification` (source/reference and packed/reference), and `SnowObjectLimitVerification`. Initial source evidence is in `artifacts/verification/snow-exclusion-instancing-source-r2.log` and `snow-procedural-cost-20260918-clean.log`. Packed-bundle and complete-render results are recorded below after verification.

The shader reference and development fixtures are excluded from the runtime bundle. The builder preserves runtime instancing variants and restores the original Unity graphics settings after building.

## Packed-build results

- Runtime bundle build and shader regression passed. The instancing variant works from the actual distributed bundle, not only editor source shaders. Unity graphics settings retained SHA256 `D84F024F18F044080F995EBE6F35FB5DE4C7A40AF32F4A8D54A8786233698C41` before/after the build.
- Final exclusion regression: 201 logical submeshes -> 7 commands; all surface-ID comparisons exact, including mixed detailed/exclusion writes, cutouts, movement and unique-mesh fallback. See `artifacts/verification/snow-exclusion-instancing-final.log`.
- Complete 120-render intervals on the synthetic scene, with both paths warmed and A/B/B/A ordering: native draws 1.002 and 0.882 ms/render, instanced 0.836 and 0.803. This includes command recording, native scene rendering and GPU completion at the interval boundary. This measured benefit is not a prediction for a real wagon yard.
- Singleton command-recording overhead fell from approximately 0.276 ms before the mesh-reuse cache to 0.050 ms after it in this fixture. Timing varies; these are not controlled in-game CPU profiles.
- `SnowObjectLimitVerification` passed for capped/uncapped rolling stock, preserved masks, camera relocation, floating origin, buildings and turntables. See `artifacts/verification/winter-render-object-limit.log`.
- Final fleet regression passed with 80 vehicle roots, GPU-array growth from 32 to 128 slices, partial snow preservation, 100/400 m views and camera relocation. See `artifacts/verification/winter-render-fleet-final.log`.

Live gameplay FPS on the reported client's cliff/yard scene still needs measurement after installation. This patch does not claim to restore 60 FPS or identify every contributor to the winter/summer difference.
