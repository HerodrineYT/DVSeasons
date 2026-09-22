# Partial yard batching and weather-panel hints — 2026-09-18

The latest game log confirms that full vehicle instancing is almost always inactive in the large yard. The previous whole-fleet bounds check rejected batching after the first intersecting pair. This is common for coupled stock, even when several independent consists could share draw commands.

The scheduler now collects the complete visible fleet and builds connected components from padded, intersecting bounds. Each component retains native vehicle order and each vehicle retains full-snow/exclusion order. Independent components release compatible draw requests together. Exact unchanged bounds reuse the component topology; unchanged draw keys and predecessor chains reuse the scheduling plan. Transform and snow-ID values remain current on every frame. A single overlapping pair no longer flushes or disables the entire fleet.

Each cached full batch owns an exact-sized material property block. Unchanged vehicle matrices and snow IDs skip array uploads. This avoids uploading 128 matrices for a four-car batch. Native fallbacks remain for mirrored, skinned, static-batched and custom-vertex-stream renderers. Object limits still affect only rolling stock, and capped cars retain their exclusion silhouettes.

The native path already batches exclusion-only runs efficiently. When at most half of visible cars receive full snow, it remains the selected path; this avoids the measured collection overhead at limit 64 in the 128-car fixture. This choice does not change the selected cars, coverage, distance or exclusions. Bounds extrema and matrix equality use exact scalar comparisons to avoid repeated Unity value-type copies. Cached plans never reuse stale moving-part transforms or IDs.

The weather-label screenshot shows the native HUD hover hint, originally anchored to the screen corner. A narrow hover callback now places weather-control names inside the editor header, with clipping and no interception of input. The original parent, sibling order, anchors, pivot, position, rotation and scale are restored on pointer exit, panel close, disable and mod shutdown. The multiplayer weather permissions and the earlier Alt/reset-loop fix are unchanged.

Initial distant-terrain discovery uses bounded scene-root traversal instead of a global native-object snapshot or recursive subtree query. The native MicroSplat instance cache remains a priority source. Initial partial terrain arrays allocate and fill incrementally, with at most two slice copies per frame; all slices and mipmaps must finish before the texture is published. Previous valid material bindings remain in use while work is pending. Fully prepared bundled winter arrays still bind directly.

The Release build passed 281 unit tests and 48 localization checks, with zero warnings or errors. The runtime shader bundle is unchanged from the previous side-snow fix.

The final packed-bundle fixture uses the production registry and shader, 128 cars with eight model parts, at 800x600. Render times include command recording, the native camera render and GPU completion; medians use five alternating 40-frame trials. Both paths render the same scene and use the real snow-object limiter.

| Limit | Selected cars | Draw commands, old → current | Render + GPU, old → current |
| --- | ---: | ---: | ---: |
| 0 (unlimited) | 128 | 1025 → 72 | 5.4744 → 3.6184 ms |
| 96 | 96 | 777 → 132 | 4.5290 → 4.2324 ms |
| 64 | 64 | 537 → 537 | 4.0948 → 3.7209 ms |

Limit 64 uses the same native path in both trials, so its timing difference is run variability, not an additional optimization. Unlimited rendering improves by about 34% in this fixture, but CPU recording alone increases from 1.6730 to 2.3166 ms. The smaller 32-car connected scene regresses by about 0.20 ms (1.3787 → 1.5754 ms). There is no universal speedup and these are not whole-game FPS measurements. A real-yard gameplay comparison remains necessary.

All pixel comparisons are exact for IDs/coverage, R8 slopes and captured half-float coordinates, including intersecting groups, moving joins/splits, cutouts, interleaved exclusions, native-only renderers, hidden/destroyed parts and origin shifts. Stationary ordered frames reuse 350 plans with no component rebuilds or matrix-array uploads. The standalone scheduler audit passed 1,865 checks and 633,209 draw requests, including randomized changing overlap graphs and batch-size changes; its warmed managed scheduler loop allocated zero bytes.

Additional Unity checks passed: 40 weather-hint hover cycles with exact native transform restoration; bounded discovery through 3,000- and 30,000-node scenes without global terrain snapshots; at most two initial array slice copies per frame; atomic publication after complete slices/mipmaps; mid-build retargeting and exact thaw pixels; native material-clone lifecycle checks.

Evidence: `artifacts/verification/snow-yard-partial-groups-final.log`, `weather-hint-layout.log`, `terrain-incremental-discovery.log`, `terrain-incremental-clones.log`, and `SchedulerAudit` in the same verification directory. No live multiplayer or full gameplay performance session was run for these changes.
