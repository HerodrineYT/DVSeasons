# World systems audit and unchanged-quality work reduction

This audit is part of the full optimization pass after iteration (9). These changes do not reduce particle counts, snow distance, texture resolution, rail history, snowfall/heat rules, or discovery coverage. Actual low-end hardware FPS still requires a game run.

## Implemented

| Path | Previous work | Change and correctness boundary |
| --- | --- | --- |
| `RailSnowTracks` | Every dirty chunk uploaded its vertices before the visibility test; any dirty chunk made `HasVisibleTracks` return true. Up to 128 retained chunks can include tracks written by trains outside the camera. | Maintain conservative CPU bounds as ribbon endpoints change; test both eye frusta before upload. Offscreen dirty data stays in CPU lists and the spatial contact index, is saved normally, and uploads before its first visible draw. Mesh bounds reuse the CPU envelope instead of a separate bounds recomputation. |
| `RailSnowContactIndex` | Each following-wheel query recalculated every candidate ribbon's axis length and normalized width. Every endpoint extension revisited the same spatial hash cells. | Derived geometry is refreshed at the existing mandatory `Update`; queries use identical equations with cached operands. Cell registration is skipped only when the current min/max cell range is unchanged. Removal resets the registration flag. |
| `IncrementalSceneScan` | Depth-first `MoveNext` pushed every child of a node before yielding; a node with thousands of children could defeat the per-step node/time budget. Stack/list allocations repeated on each scan. | Retain reusable parent/index cursors and process one child per yield, preserving depth-first hierarchy order. Root list capacity is prepared before Unity fills it. Inactive objects, rescan discovery and streamed/destroyed hierarchy handling remain. A scene-root native snapshot is still atomic; this change does not claim to make that API interruptible. |
| `RailSnowGameSource` | Scratch set/list allocations on every cleanup; repeated native camera/car/bogie transforms and direction normalization for each axle. | Reuse cleanup collections; read immutable per-call/per-car/per-bogie inputs once. Every axle still gets its own contact point, rail-history update and per-side snow samples. No movement threshold changes. |
| `TrainSnowTrailController` | Each side of one axle recalculated the same powder and camera-distance fade. | Compute these two inputs once per axle. Emission credits, budgets, shelter, random samples, size/lifetime and separate rail sides are unchanged. |
| `SeasonVisualController` | The 20 Hz glass-collision pass fetched `camera.transform.position` inside the loop for every snowflake. | Read the camera position once per collision pass. Particle sweep and window collision behavior are unchanged. |

The largest expected saving among these world changes is avoided **offscreen rail mesh work**, not a general solution to the much larger vehicle-culling cost seen in the latest log. Rail contact and particle changes remove repeated calculations/native calls; they are not used to claim a measured FPS multiplier.

## Reviewed and retained

- `MicroSplatSeasonalTerrainController`: pooled scene discovery and staged array construction already address the old spikes. Its global material scan belongs to exceptional restore/reconciliation, not normal update. Preserve exact built-in material signatures and streamed distant terrain behavior.
- `SeasonalTextureController`: lazy profiles, priority track queue, chunk pixel budgets, delayed binding checks are already present. The remaining 30-second material snapshot also discovers VSP/prefab materials without live renderers. Replacing it with a renderer-only scan would silently lose coverage; left unchanged.
- `FixedTexturePackRepository`: bundled ready textures, override precedence and asynchronous pixel readback remain. Existing synchronous compatibility fallback was not replaced with a lossy or unsupported path.
- `WaterIceController`: bundled albedo/normal and shader world-space UV already avoid image readback and mesh copies. Water amount setter has change filtering. Current live shared-mesh check is necessary for streaming/static batching.
- `TenderCoalSnowController`: material/source-step render textures are shared, released by usage and reused; material CRC avoids unconditional property copying. Preserve dynamic source changes, heat and object-budget integration.
- `WinterPuddleController`: only smoothness is copied in R8 when supported and original RGB is preserved. Eliminating the original-smoothness copy without a WetStuff shader integration changes the result; retained.
- `TurntableSnowSource`: event-driven scene registration and incremental traversal retained; no return to periodic full-world scans.
- `JunctionSnowSource`: live animated blade geometry retained; avoid treating animated switches as static cached meshes.
- `AutumnLeafGroundController`: pooled particle arrays, bounded surface queries, source caches and local vehicle attachments retained. `Physics.SyncTransforms` protects late-updated detached WALKABLE colliders; removing it risks leaves crossing moving vehicles.
- `SpringLifeController`: small bounded insect population, lazy meshes/materials and audio lookup retained.
- `SnowFootstepAudioController`: native surface handoff and cab-trigger tests retained. Per-step hierarchy checks preserve changing modded cab layouts; they are not a winter frame-rate bottleneck.
- `SeasonVisualController` / snowfall: existing pooled particle buffer, collision cadence, shelter checks and emitter placement retained; no lower density or radius.

## Verification entry

`DVSeasons.AssetBundleBuild.SnowWorldWorkVerification.Run` uses production runtime classes with real Unity meshes and scene hierarchies:

- 7,200 deterministic queries compare cached rail geometry to the previous equations, including changes to endpoint/width, same-cell cache hits, removal/reinsertion and snowfall aging.
- 1,800 wheel stamps and 720 subsequent movements validate no offscreen mesh uploads; visibility, stereo-only visibility, boundary contact and rebasing publish the full current vertex data.
- Saved rail records and snow refill retain their behavior while GPU data is deferred.
- A hierarchy with 4,096 siblings verifies a one-node step retains one cursor, full native DFS order, inactive children, newly created nodes, destroyed objects, reset and roots-first traversal. Separate deletion/reparenting cases mutate a visited sibling between steps: cursor reconciliation must visit both pending siblings in that same scan, without waiting for its rescan interval.

Executed successfully in Unity 2019.4.40f1: `artifacts/verification/full-opt-world-work.log` reports **112,204 passed checks**. The independent formula comparison passed all 7,200 samples. The rail fixture had eight chunks: the offscreen stamps and extensions performed zero uploads, all 3,600 saved marks survived, and the current geometry uploaded on visibility. The 4,161-node hierarchy retained its traversal order; all four mid-scan deletion/reparenting cases found the pending siblings in the same scan.

## Native visibility experiment: rejected

The separate `SnowNativeVisibilityVerification.Run` fixture recorded legacy and candidate registry paths inside the same real `Camera.onPreRender` callback. The expanded final run, `artifacts/verification/full-opt-native-visibility-final.log`, reports **1,475 passed checks**, including 20 image comparisons. Every comparison had zero changed IDs/coverage, zero changed R8 slope values and zero local-coordinate error across spawn, first-frame camera movement, layers, LOD, skinned geometry, nested cameras and shadows.

Each benchmark used 32 warmup camera renders, then 100 alternating samples of each path. All 582 renderers remained native-visible; `FrameNativeCulledCount` averaged zero in every view:

| View | Legacy CPU recording | Candidate CPU recording |
| --- | ---: | ---: |
| Front, full field of view | 1.4397 ms | 1.5415 ms |
| Looking away | 0.0603 ms | 0.0611 ms |
| Narrow partial view | 0.8933 ms | 0.9726 ms |

The extra native visibility query added approximately 1–9% CPU in these scenarios and did not reduce draw counts. **The optimization was rejected and removed from production.** This fixture has no baked Umbra occlusion data and cannot establish game-yard occlusion savings or FPS improvement. The parity result is limited to the tested scenarios.

The fixture remains reproducible using `DVSEASONS_VERIFY_NATIVE_EXPERIMENT_DLL` set to `artifacts/verification/full-opt-native-experimental/DVSeasons.dll`; it still obtains resources from the normal build directory. A current production DLL without `RecordAfterCull` produces an explanatory error rather than silently testing the wrong implementation.
