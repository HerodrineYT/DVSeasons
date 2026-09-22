# Full mod optimization audit — 19 September 2026

This pass reviews the vehicle snow renderer, world discovery, rail history, textures, shaders, cabin systems, weather, multiplayer and settings. It keeps changes that remove repeated work or preserve equivalent output, and rejects measured regressions. It does not remove winter features or reduce rendering resolution, texture resolution, particles, snow distance, thermal simulation or multiplayer synchronization to obtain a benchmark result.

The result is a set of bounded optimizations, rather than a claim that the game can run well on every low-end device. The supplied live run still identifies substantial vehicle-culling cost. New synthetic checks establish behavior and several eliminated operations; they do not establish a new live yard FPS or a minimum supported GPU.

Detailed subsystem inventories and their evidence are in:

- [GPU audit](GPU_AUDIT_2026-09-19.md).
- [World systems audit](WORLD_WORK_AUDIT_2026-09-19.md).
- [Runtime and cabin cache audit](RUNTIME_CACHE_AUDIT_2026-09-19.md).

## Starting evidence

The pre-change player log is retained at `artifacts/backups/full-opt20260919/Player.log`. In its steady windows, the approximate mean CPU costs were:

| Scope | Observed range |
| --- | ---: |
| `snow-vehicle-culling` | 3.05–6.90 ms |
| `snow-vehicle-request-build` | 0.43–1.50 ms |
| `snow-scheduler-plan-record` | 0.80–1.54 ms |

The log also included a 35.06 ms `vehicle-parts` maximum, a 6.80 ms window-discovery maximum, and a 34.23 ms initial seasonal-texture maximum. These are different types of cost: a persistent per-frame culling cost cannot be explained away by an occasional discovery peak. CPU scopes exclude actual GPU execution. The recorded 2560 × 1440 run used a snow-car limit of 79; it is not a controlled before/after comparison with a new build.

The existing vehicle renderer, OBB envelopes, motion hysteresis, overlap DAG, active-stream dependencies and two-plan cache are retained unchanged. Proposed renderer micro-optimizations were tested and reverted after an inconsistent timing result. Rebuilding those systems again without evidence would risk previously fixed snow visibility and ordering behavior.

## Accepted runtime changes

| Area / files | Change | Correctness boundary |
| --- | --- | --- |
| Rail meshes — `RailSnowTracks.cs` | Maintain conservative CPU bounds while ribbon endpoints change. Reject offscreen chunks before uploading dirty vertices; upload their complete current data on the first visible draw. Reuse the CPU envelope as mesh bounds. | CPU history and contact queries remain current, saves retain the same marks, both eye frusta count, and floating-origin offsets are applied at visibility/render time. Dirty geometry is deferred, not discarded. |
| Rail queries — `RailSnowContactIndex.cs` | Cache axis length and normalized width at the existing geometry-update boundary. Avoid registering unchanged spatial cell ranges again. | Sampling retains the original equations and per-rail history. Width/endpoint changes refresh the derived values; removal resets registration. |
| Scene scanning — `IncrementalSceneScan.cs` | Reuse root/cursor storage and process one child per yield instead of pushing a large sibling set atomically. Reconcile live cursors after deletion or reparenting. | Inactive, detached and newly loaded content remains discoverable. Pending siblings are visited during the same scan after tested mid-scan mutations. Unity's scene-root snapshot is still an atomic native operation. |
| Wheel effects — `RailSnowGameSource.cs`, `TrainSnowTrailController.cs` | Reuse cleanup sets/lists and hoist camera, car, bogie, direction, powder and distance calculations out of repeated axle/side work. | Each axle and rail side still has its own contact, snow history, shelter check, emission credits and particles. Thresholds, budgets, size and lifetime curves are unchanged. |
| Snowfall collision — `SeasonVisualController.cs` | Read camera position once per glass-collision pass. | Same particles, sweep tests and collision cadence. |
| Glass discovery — `WinterWindowController.cs` | Sort only locomotives, compute distance once, reuse discovery buffers, skip already covered child roots, and reuse frame-local position reads. | Interior masters and external duplicates retain their order; streamed replacements remain visible. The climate model, masks, overlays and wiper behavior remain. |
| Settings — `SeasonModSettings.cs` | Reuse the immutable season snapshot while its six raw inputs remain unchanged. | Changes invalidate immediately. Old snapshots cannot be mutated by a caller, normalization is retained, and caches are not saved. |
| Cold powertrain — `ColdPowertrainController.cs` | Skip unrelated car types before traversing simulation components; reuse the DE6 primer lookup within a scan. | Existing BE2/DE6 registration and all native engine patches retain their cadence and conditions. No permanent flow cache hides changed components. |
| Custom heating — `CabEngineHeating.cs`, `CabHeaterService.cs` | Prune destroyed Unity car wrappers and their control/interior/simulation graphs while keeping the small climate state. Refresh late GUIDs from live logic cars and capture destroyed predecessors before replacement binding. | Thermal state survives same-GUID replacement. Current native-cab state is not overwritten by stale restored records. Heat curves, RPM/catenary behavior and MP authority remain. |

These accepted changes touch 11 runtime C# files. `SnowVehicleRegistry.cs` was restored byte-for-byte from the pre-change backup; the scheduler and topology/hysteresis implementation are unchanged. The original runtime sources, bundles, installed binaries and package backups are retained under `artifacts/backups/full-opt20260919`.

## Accepted shader change and absolute measurements

Only accumulation pass 3 in `SnowVehicle.shader` changes its sampling strategy. It reuses the shared height sampler: on D3D11, Gather obtains the same four height texels, compares visibility per texel and interpolates those visibility results. Other backends retain four reads. Near/far/distant selection uses explicit branches. Interpolating heights before comparison would change shelter edges and was not substituted.

Source and packed comparisons each passed **216 scenarios / 14,155,776 RGBA pixels with maximum component difference 0**. Cases include sparse/empty/dense captures, saved masks, zero/positive snowfall, map edges, rotation and origin offsets. Map sizes remain 1024 × 1024 and vehicle masks 256 × 256. Stereo shader source was not changed; packed stereo verification passed after the bundle rebuild.

The packed benchmark used seven alternating trials, 128 captures per batch, cached resources, a 256 × 256 RHalf destination and 1024 × 1024 RFloat shelter maps. It measures CPU submission plus GPU completion, not isolated GPU timestamp duration or game FPS:

| Workload | Reference / capture | Changed / capture | Absolute saving / capture |
| --- | ---: | ---: | ---: |
| Sparse, near | 6.536 µs | 4.594 µs | 1.942 µs |
| Sparse, far | 6.868 µs | 5.291 µs | 1.577 µs |
| Sparse, distant | 6.593 µs | 4.260 µs | 2.333 µs |
| Dense, near | 5.748 µs | 3.847 µs | 1.902 µs |
| Dense, far | 5.016 µs | 3.715 µs | 1.301 µs |
| Dense, distant | 5.012 µs | 4.433 µs | 0.579 µs |

These are improvements of an equivalent pass, but only approximately **0.58–2.33 µs per capture** in this fixture. The runtime updates at most one vehicle mask per frame. The numbers cannot reasonably be presented as a large overall FPS increase.

## Rejected experiments

| Candidate | Evidence | Decision |
| --- | --- | --- |
| Managed prepared frustum testing | 803,719 exact parity checks passed, but seven alternating million-box trials per workload were 3.03–3.81 times slower than Unity's native frustum test. | Removed from runtime. Editor-only fixture retained. |
| `Renderer.isVisible` fast rejection after native mono culling | 1,475 checks passed, but three timing workloads retained all 582 renderers as visible, rejected none, and added approximately 1–9% CPU recording overhead. Baked Umbra was absent from the fixture. | Removed from runtime, including the entry point, context flag and counter. Fixture and experimental DLL retained separately. |
| Vehicle registry identity caches and repeated matrix/depth-read hoisting | Whole-submission A/B/B/A CPU measurements were inconsistent. Branched yard: baseline 2.7185 / 2.6459 ms, candidate 3.0131 / 2.4180 ms. Rotated yard: baseline 3.3780 / 2.8940 ms, candidate 3.4221 / 3.4420 ms. Correctness passed, but the repeated comparison did not establish a reliable gain. | Reverted `SnowVehicleRegistry.cs` byte-for-byte. Candidate source and DLL retained in `artifacts/verification/full-opt-registry-experimental`; this experiment is separate from the native-visibility experiment. |
| Early return for empty snow-capture texels | A sparse restored-mask case with zero accumulation changed by 0.001953125. | Rejected; tolerance was not widened. Exact parity restored before accepting Gather. |

The released implementation contains **none of these experimental renderer changes**. It uses the prior vehicle registry, scheduler, native Unity frustum test, OBB and hysteresis paths.

## Reviewed systems deliberately retained

MicroSplat already uses pooled discovery and staged array construction. Seasonal textures use lazy profiles, prepared bundle textures and asynchronous readback; the material snapshot also covers prefab/VSP materials without live renderers. Removing that coverage would change behavior. Water already uses direct texture assets and world-space shader UVs. Coal blends are shared/reused, and puddle smoothness copying prevents unsafe feedback into the same render target.

The audit retains turntable event-driven discovery, animated junction geometry, animal exclusion behavior, leaf collision synchronization, bounded insects, footstep/audio handoffs and existing snowfall shelter logic. It also retains weather override ownership, host-only simulation, protocol validation, network cadence, initialization retries, UI localization, cab controls and saves. The linked subsystem documents explain these decisions by file.

Vehicle height/snow arrays still retain winter history. At 256 slots their nominal layer storage is approximately 96 MiB before temporary targets and other textures. A new resident-map cache could lower memory use, but would require tested state eviction/restoration and bounded readback/compression. This pass does not discard accumulated masks or silently lower their resolution to claim memory savings.

## Validation status

| Check | Result / evidence |
| --- | --- |
| Release build, unit suite and localization | Passed: 285 unit tests, zero build warnings/errors, 48 English/Russian translation checks. `full-opt-final-build.log`. |
| Source and packed accumulation parity | Passed: 216 scenarios and 14,155,776 pixels each, zero error. `full-opt-snow-accumulation-source2.log`, `full-opt-snow-accumulation-packed.log`. |
| Packed stereo programs | Passed. `full-opt-stereo-bundle.log`. |
| World work and discovery | Passed: 112,204 checks, including 7,200 reference-equation samples, zero offscreen uploads for 1,800 stamps plus 720 extensions, 3,600 saved marks and mid-scan hierarchy mutations. `full-opt-world-work.log`. |
| Runtime/cabin caches | Passed: 49 Unity checks, including late GUIDs and replacement before cleanup. `full-opt-runtime-cache-final.log`. |
| Vehicle rendering and limits 0/96/64 | Passed against the clean rebuilt shipping DLL with exact ID/coverage/R8-slope/captured-coordinate parity, overlap recovery and native fallbacks. `full-opt-shipping2-yard.log`. |
| Overlap DAG and active streams | Passed: direct dependencies, invisible bridges, restoration, ordering and two-entry visibility LRU. `full-opt-dag-final.log`. |
| Stable topology and two-plan cache | Passed: 240 camera/LOD/visibility frames with zero component rebuilds or pair tests; 80 warmed alternating frames with zero plan builds/matrix uploads. `full-opt-topology-final.log`. |
| Final hysteresis regression | Passed: retained stationary-jitter envelopes and safe changes after actual motion. `full-opt-hysteresis-final.log`. |
| Final oriented-bounds regression | Passed against the clean rebuilt shipping DLL: 3,241 checks across 130 submissions, including root jitter, moving bogies, detached/mutable/replaced meshes, streams, skins, static batches, mirror, padding and origin shift. `full-opt-shipping2-obb.log`. |
| Final leaf/trail regression | Passed: snow colour/depth, wheel contact and cleared rails, speed/reverse/teleport exclusions, wind/settling, rail save/restore/origin shift, leaf atlas/collisions/moving surfaces. `full-opt-leaf-trail.log`. |
| Repeated whole-submission baseline/candidate comparison | Completed: matching 128-car fleets and separate DLL runs did not establish a consistent CPU improvement. Registry candidate rejected, as detailed above. Logs include `full-opt-submission-baseline.log` and `full-opt-submission-final.log`. |
| Shipping regression after registry reversion | Prior vehicle renderer restored byte for byte. A forced `Rebuild` prevents restored source timestamps from retaining the candidate DLL; removed experimental members are absent from the binary. Final shipping yard and OBB runs passed as recorded above. |
| Installation | Installed with game closed; all ten binary/metadata/bundle hashes match the build. Settings preserved byte for byte during installation. `full-opt-installed-hashes.json`. |
| Archive integrity procedure | `verify_cpu_yard_packages.ps1 -ReportName full-opt-package-hashes.json` compares every runtime/source ZIP entry with the build/workspace and rejects bundled Settings.xml. The hash report is stored separately from the source package. |

All log paths above are relative to `artifacts/verification`. Prior binaries, packages and obsolete loader caches are backed up under `artifacts/backups/full-opt20260919`. Version remains 0.3.3.

## Remaining practical check

A fresh in-game run is still needed with the same save, camera, settings, resolution and snow-car limit, first stationary and then moving/entering/exiting cabs. Compare culling and total frame time as well as draw counts, and verify snow persistence, wind-driven side accumulation, rail traces, windows, heaters and multiplayer weather. Hardware and the base game still set a lower performance limit; this audit does not promise a frame rate for unspecified low-end systems.
