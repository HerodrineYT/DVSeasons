# Wagon-yard rendering — 2026-09-18

Historical first iteration. The fleet-wide overlap fallback and its measurements below are superseded by [partial overlap-component batching](PARTIAL_YARD_WEATHER_UI_2026-09-18.md), with updated validation and performance limits.

After the cache update, the user still reports around 30 FPS in a winter wagon yard. The latest local Player.log (11:39) shows stable terrain discovery around 0.3 ms, but 829–892 full snow surfaces, 831–1026 exclusions and 1298–1335 actual vehicle commands per frame. CPU command preparation averages 4.63–6.09 ms. These counters do not measure GPU execution and are not a complete explanation of frame time.

## Changes

- Respect ordinary, enabled, non-fading LOD groups for excluded axles and cabin detail. Previously every interior/axle LOD was submitted regardless of the native LOD selection. Disabled/fading groups retain conservative behavior. Cache all LOD memberships because some native brake pads and interior labels appear in more than one level.
- Add a separate instanced full-snow surface shader pass. Each instance carries its actual vehicle ID and captured-frame transform, preserving independent snow masks and moving-part mapping. Keep the previous shader passes as native fallbacks.
- Schedule compatible mesh/submesh/cutout draws across vehicles whose padded world bounds do not intersect. Keep each vehicle's full/exclusion ordering. A visible fleet with any overlapping padded bounds uses the previous draw path: this avoids both ordering changes and expensive partial scheduling for tightly coupled stock. Consecutive exclusion writes commute and can share batches. No texture readback or asynchronous visibility guess is used.
- Cache the scheduling plan when renderer/mesh/material/pass sequences remain unchanged. Supply current transforms and snow IDs on every frame; motion does not freeze the snow. An unchanged set of exact bounds reuses the separation proof. Failed overlap probes are retried after 0.5 seconds, with current native rendering between attempts, so the negative cache cannot hide or delay a surface update.
- Reuse request lists, queues, matrices and property blocks. Full batches contain at most 128 instances, exclusion batches at most 1023. Mirrored, skinned, statically batched and custom-vertex-stream renderers retain native draws. Fleets without shared eligible meshes bypass the scheduler.
- Report actual full-snow instancing counts in the performance log, in addition to exclusion counts.

## Verification

The interior LOD fixture compares actual deferred surface-ID pixels to the former conservative path. At 20 wagons/40 bogies, 160 unused axle exclusions become zero while all 40 visible combined bogie draws remain. Near/middle/cull distances, reused renderers, disabled/fading groups and origin shift pass.

Complete-render synthetic LOD comparison at 512x384, seven alternating 100-frame trials after 20 warm-up frames, with reference manipulation and parity readbacks outside timing: 20 wagons 0.8073 -> 0.3449 ms; 100 wagons 3.1713 -> 0.9248 ms. This includes registry command recording, native deferred rendering and a GPU-completion drain. The 100-wagon case retains 200 full visible bogie surfaces and removes 800 unused individual axle exclusions. See `artifacts/verification/yard-interior-lod-benchmark.log`; these are not real-yard FPS forecasts.

The separate shader fixture compares pass 0 to pass 6 with 24 vehicle IDs (including 80 and 1024), 192 parts, eight mesh types, cutouts/UV transforms, nonuniform scales, moving captured frames and large coordinate shifts. Surface IDs, local coordinates and slope pixels match exactly. Initial synthetic complete-render median at 800x600: 0.8528 ms native versus 0.5390 ms instanced, for this fixture only.

The scheduler's standalone recording harness covers 1,056 checks and 402,592 draw requests, including randomized dependency ordering, capacity splits, plan invalidation and live matrices/IDs during cache reuse. Warmed request collection produces no managed allocations in the harness; this is not a claim of zero allocations for the whole game or Unity command-buffer implementation.

The integrated packed-bundle fixture uses the production registry at 800x600 with 128 cars and eight model parts per car, five alternating 40-frame trials with GPU completion. Shared, separated stock: 1,025 commands become 16; median complete render is 4.2898 -> 3.5300 ms. CPU recording alone is higher (1.3989 -> 3.0144 ms), so this optimization must not be described as a CPU-only saving. The independent LOD reduction above removes actual unused surfaces and benefits both paths.

Intersecting stock retains 1,025 commands, 4.2440 -> 4.2926 ms; distinct singleton meshes retain 1,025 commands, 4.1527 -> 4.0898 ms. Before adding negative-probe caching the overlapping case regressed by about 0.62 ms: this is why the cache gates bounds construction itself. The final fixture checks that separated cars still render correctly during the cached native fallback, resume batching after expiry, and return to the native path immediately if overlap reappears.

All integrated ID/coverage, R8 slope and captured-coordinate comparisons are exact, including cutouts, changing visibility, moving parts, nonuniform/mirrored transforms, native-only renderers and a large world shift. See `artifacts/verification/snow-yard-final-packed-128.log`. These measurements cover the synthetic rendering fixture, not a measured whole-game FPS increase; the real wagon-yard scene still needs a new gameplay log after installation.

Final Release build: zero warnings/errors, 262 unit tests and 48 localization checks passed. The rebuilt bundle passed its shader regression suite. Separate packed-runtime checks passed for the rolling-stock snow limit, 80-car fleet/texture-array growth and the native handcar handle at both angles with origin shifts. No color/depth target mismatch was logged in these checks. Version stays 0.3.3; installed settings are preserved.
