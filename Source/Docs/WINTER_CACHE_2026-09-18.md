# Runtime caches — 2026-09-18

The user reports improvement after the previous rendering patch but remaining stutters and explicitly requests caching. The current local Player.log contains winter windows with terrain-native-discovery maxima around 79–81 ms, vehicle render command submission around 1.3–3.2 ms mean, and a first autumn leaf activation lasting 2854.69 ms. These CPU scopes are observations, not a complete GPU profile or proof that all pauses have the same cause.

## Changes

- Preserve distant-terrain source and renderer caches across unrelated scene loads. Inspect the newly loaded scene, retaining the initial/native-grid full snapshot. Continue reading current material bindings, including inactive rings. Existing bounded fallback discovery also finds native components created later within an already loaded scene, including their private material lists. Shader replacement and live texture values remain checked.
- Cache repeated vehicle mesh/submesh/material groups until fleet geometry changes. Parts directly reference their reusable groups instead of constructing and hashing grouping keys for every draw. Reuse arrays when another car streams in. Actual renderer state and transform are refreshed each frame; only unchanged matrix determinant results are retained. Flush each active exclusion run at the same detailed/native draw barriers as before.
- Cache the train limiter's branch ownership until registry membership changes. Cargo, external controls, interiors, newly discovered/removed trains and rolling-stock classification invalidate it. Nearest-car selection retains its existing camera/motion timer; the limit still applies only to rolling stock. Unlimited mode already skipped this work.
- Bake the existing 1024² autumn leaf atlas once for distribution. Load the ready PNG directly without a GPU readback, retain it across season switches, and retain the procedural fallback for missing/invalid files. All source pixels, mips, filters, shapes and colors are preserved.

The caches do not store final camera images, freeze snow accumulation or retain old transforms. This patch changes no shaders, snow distance, snow texture resolution, weather/network authority, heater behavior or graphics settings.

## Validation

- Release build: 262 tests, 48 localization checks, no compiler warnings/errors.
- Packed-shader vehicle regression: 201 logical exclusions remain 7 commands, with exact pixel parity. 240 repeated records do not rebuild grouping; movement, mirrored transforms, additional vertex streams, hidden/inactive parts, detailed/exclusion ordering and geometry replacement remain correct.
- Limiter regression: 1200 stable updates do not rebuild branch ownership. Exercises production hooks, cargo/interior/external replacement, teleport, world shift, moving trains, destroyed owners and disabling/re-enabling the limit.
- Terrain discovery regression: scene notifications coalesce, unrelated/new distant scenes cause no global snapshot, unloaded queued scenes are skipped, late-created private-only native materials are found, and native grid initialization still causes a full refresh. Original 30,000-node backlog, hidden terrain materials, slice ordering and thaw tests pass.
- Full snow-limit and 80-vehicle fleet regressions pass: masks survive distance changes, camera relocation, floating origin and GPU-array growth; static scenery remains unaffected by the car limit.
- Leaf atlas: all 1,398,101 pixels across 11 mip levels match. Synthetic local generation took 1595.77 ms versus 33.37 ms for loading the ready file; this is first-activation work, not a whole-game FPS multiplier. See LEAF_ATLAS_CACHE_2026-09-18.md.

A separate weak-key shader-property cache was measured and rejected: 200,000 helper calls took a median 52.200 ms cached versus 49.950 ms native. The release retains the original shader-property query implementation. That experiment does not establish an in-game FPS difference.

Full-game FPS and remaining stutters require a fresh gameplay measurement after installation. Newly loaded scene-local native hierarchy scans and the initial terrain snapshot remain synchronous; their cost is reduced in scope, not eliminated. Ordinary performance logs measure CPU preparation, not GPU execution.
