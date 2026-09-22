# Winter versus summer follow-up — 19 September 2026

The user reports no visible yard FPS improvement after the broad optimization pass: approximately 30 FPS in winter becomes a stable 60 after switching to summer. The new log confirms substantial persistent winter rendering cost. It does not contain a complete, uncontaminated summer reporting interval, so the observed 60 FPS remains the user's measurement rather than a number established by the performance logger.

## Preserved evidence

`artifacts/backups/winter-summer20260919/` contains the original `Player.log` (last written 14:49:53 local time), `Player-prev.log` (14:41:40), installed DLL/PDB files, settings, metadata and the main runtime AssetBundle. `SHA256.json` records their hashes. Runtime source backups in the same directory were captured separately before this iteration's changes.

The current run uses Unity 2019.4.40f1, D3D11, RTX 4060 with 7956 MB VRAM, Ryzen 5 5600, approximately 32 GB RAM, and 2560 × 1440 rendering. DVSeasons is 0.3.3. The recorded snow-car limit is 79, with 224 cars tracked by side-snow history. There is no active snowfall in the reported windows. `Player-prev.log` contains only one startup performance report and is unsuitable as a steady-state comparison.

Line references below use newline-delimited lines in the preserved current `Player.log`, matching `rg -n`.

## Five winter windows after startup

All CPU times are the logger's mean milliseconds per scope invocation.

| Log line | Whole-game FPS | Render commands | Vehicle culling | Request build | Scheduler plan/record | Snow commands |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1943 | 41.0 | 4.54 | 3.45 | 0.63 | 0.90 | 816.9 |
| 1950 | 33.2 | 7.66 | 5.29 | 1.28 | 1.01 | 1398.5 |
| 1953 | 30.1 | 8.50 | 5.59 | 1.51 | 0.88 | 1421.8 |
| 1960 | 36.9 | 7.43 | 4.76 | 1.17 | 1.13 | 1054.9 |
| 2040 | 35.1 | 6.89 | 4.01 | 1.51 | 0.91 | 1358.5 |
| Arithmetic mean of window means | 35.26 | 7.00 | 4.62 | 1.22 | 0.97 | 1210.1 |

The sample is not a controlled fixed-camera benchmark. Nevertheless, the measured CPU target is clear: collection and visibility testing before scheduling consume approximately two thirds of command-recording time. The 30.1 FPS window has only 11 component builds, 40 plan builds versus 550 hits, and a 0.01 ms batch-upload mean. Reworking topology or matrix upload again would not address the largest measured cost.

`snow-vehicle-culling` includes the nested surface-preparation scope. `snow-scheduler-plan-record` includes batch upload. Counters divide elapsed ticks by invocation count, not by all game frames; rare exposure/discovery means therefore must not be added as though every scope ran once per frame. These are CPU submission measurements, with no GPU execution timestamps.

## The season switch

- Line 2046 records the season change.
- Line 2049 applies ground coverage 0/32.
- Line 2052 changes tracks to SnowFree, with snow 0%.
- Line 2055 reports 46.0 FPS over a mixed winter/summer window. Its remaining 300 scheduler hits and 6.45 ms render mean belong to calls that actually ran; the mean is not the cost on subsequent snow-free frames.
- The next report, line 2079, has zero snow draw commands and zero vehicle-render scopes. Its 1.1 FPS and 232627.1 ms maximum include pause/save/quit activity and cannot describe ordinary summer gameplay. Save and quit messages appear immediately before it.

There is no clean logged 60 FPS summer window. The disappearance of snow submissions agrees with the direction of the user's observation; the report must not turn that observation into a fabricated logger result or claim a measured winter improvement.

## Why a limit of 79 still leaves substantial work

The slider budgets full snow on the nearest selected rolling stock, not every renderer or draw call. One selected vehicle has many parts and submeshes. Unselected vehicles still require the existing cheaper silhouette/exclusion path so that ground snow does not appear over or through their geometry. They consequently still participate in visibility collection. The log's 761–1568 detailed draws and 191–301 cheap draws are part/submesh counts, not evidence that the 79-car selection limit is ignored.

Reducing this limit alone cannot remove the general per-part culling loop, all exclusion drawing, or the full-screen procedural snow pass. This explains why a car limit can have less influence on FPS than its name might suggest, without establishing that the limit has no effect in every view.

## Other findings and next target

- Steady seasonal-texture preparation is 0.02–0.03 ms and asynchronous readback counters remain 0/0. Texture startup is not the persistent 30 FPS cause in this run.
- In the final winter window, exposure rendering is 0/0 while FPS remains 35.1. Repeated exposure captures are not necessary for the observed slowdown.
- Water reports rebinding one renderer 25 times. Its mean CPU cost is 0.22–0.31 ms: worth investigating separately, but not an explanation for the full winter/summer gap.
- No DVSeasons error or color/depth-size mismatch appears. The duplicate `2WE3MultiplayerSpawnFix` manager error and `Bolt.SceneVariables` destruction exception are separate existing messages.

The immediate CPU target is the repeated vehicle-part visibility/LOD/state collection and request construction, with correctness coverage for changing cameras, transforms, renderer state and streamed interiors. Conservative reusable results must remain valid when those inputs change. The previous unsuccessful native-visibility and managed-frustum experiments should not be reintroduced merely because caching is desirable.

GPU/driver execution remains an open measurement, especially the full-screen procedural snow work at 2560 × 1440 and the remaining 800–1400 vehicle commands. The measured 7–8.5 ms of CPU submission is significant, but the log cannot attribute the entire approximately 30-to-60 FPS difference to CPU or GPU alone. Any new optimization needs separate correctness and timing verification, followed by an actual fixed-view winter/summer run; no new FPS gain is claimed by this analysis.

## Follow-up diagnostic build

The normal rendering algorithm and snow quality remain unchanged. An indexed LOD traversal experiment was rejected: ordinary LOD-heavy workloads did not improve consistently. Its preserved evidence is in `artifacts/verification/winter-summer-lod-experimental`; the experimental flag and traversal are absent from production. The direct private-culling timing experiment is not evidence of an in-game improvement.

The remaining diagnostic scopes separate coarse vehicle selection, LOD preparation, part state/bounds/frustum work and topology validation. Timings aggregate at car/phase boundaries, not around every renderer. The report also includes part counts, LOD rejections, state rejections, frustum tests and evaluated LOD groups. Report windows now identify their seasons, total game frames and snow submissions; `snow-record-ms-per-game-frame` uses the entire frame window rather than only calls during its winter portion.

A manually started **Run snow performance comparison** button is available beside the snow-car limit. Stop the train, face the troublesome yard, start it, close settings and keep the camera still. About 55 seconds plus frame-boundary rounding cover:

1. Baseline winter rendering.
2. No vehicle-surface recording (also omits its rail, junction and animal exclusion surface commands). Full-screen snow still runs, so temporary snow on normally excluded surfaces is expected.
3. No final full-screen snow shader; vehicle buffers and preparation still run.
4. No procedural snow command buffer; simulation, histories, exposure preparation, windows, ice and other winter systems continue.
5. Restored baseline rendering.

Each phase has two seconds of warmup and eight seconds of measurement, preceded by five seconds to close settings. No setting, weather value, accumulated snow mask or save state is rewritten by the probe. It never starts automatically. Cancellation, loss of the renderer, camera movement/settings changes, pauses after warmup, season changes, coverage drift, snow-limit changes and render errors restore ordinary rendering. VR eyes share one phase per game frame. No GPU readback or blocking fence is introduced.

Each result logs `Snow render A/B phase=...`, frame count, whole-game FPS, average/max frame time and missing-render contamination. Compare the first and final baselines before attributing an intermediate difference to a pass. These are whole-frame CPU/driver/GPU comparisons, not isolated GPU timestamps; the sum of individual differences is not necessarily additive. The reported result is still subject to the game's FPS cap and changing scene activity.

`Baseline` versus `WithoutShading` retains identical surface-buffer work and isolates omission of the final snow draw. `WithoutShading` versus `WithoutProceduralSnow` includes the surface preparation/recording/drawing and GBuffer preparation. `WithoutVehicleSurfaces` also changes which branch the full-screen snow shader executes on vehicle pixels, so that difference alone must not be presented as isolated vehicle GPU time.

## Verification

The diagnostic build passed 293 unit tests (including eight session tests) and 52 bilingual translation checks. `winter-summer-probe-final.log` verifies real HDR/LDR camera rendering for all five phases, zero-alpha empty surface data, exact normal image restoration after completion/cancellation, unchanged snow coverage/history, command-buffer lifetime and cancellation guards. `winter-summer-yard-final.log` passes the existing 128-car output-parity suite with the probe inactive, including limits 0/96/64, animated parts, cutouts, overlaps, native fallbacks and origin shifts. These establish safety of the diagnostic path; they do not establish an FPS fix.

## Second live run: completed surface/shading comparison

The subsequent `Player.log`, last written at **15:30:56** on 19 September, contains a completed five-phase comparison. It is preserved separately under `artifacts/backups/snow-ab20260919/Player.log`, SHA-256 `691D90732CC7EF8CEE41CC4D8EC7F602147B9BED36030B945B5717DBC60456BC`. Its `Player-prev.log` matches the preceding 14:49 run; the earlier evidence was not overwritten.

The comparison starts at line 1916 with snow limit 79, 2560 × 1440, HDR enabled and stereo disabled. Each phase reports zero missing-render frames and `contaminated=False`. There is no cancellation, camera/settings-change message or enter/exit activity during the measured sequence. Normal rendering is explicitly restored at line 2031.

| Log line | Phase | Measured frames | FPS | Mean frame time | Maximum frame time |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1929 | Baseline | 311 | 38.91 | 25.703 ms | 39.913 ms |
| 2013 | Without vehicle surfaces | 469 | 58.54 | 17.081 ms | 44.390 ms |
| 2019 | Without final snow shading | 305 | 38.12 | 26.230 ms | 34.423 ms |
| 2025 | Without procedural snow commands | 460 | 57.43 | 17.414 ms | 33.941 ms |
| 2028 | Restored baseline | 298 | 37.16 | 26.912 ms | 45.813 ms |

This is stronger evidence than the initial winter/summer observation. Omitting the surface stage reduces frame time by **8.622–9.831 ms**, or approximately **34–37%**, relative to the two bracketing baselines. Omitting only the final full-screen snow draw produces **38.12 FPS**, between the baseline measurements. There is no measurable large benefit from removing that final draw in this run. Omitting all procedural commands is in the same performance range as omitting vehicle surfaces; their 1.111 FPS difference must not be interpreted as negative shader cost.

The evidence therefore points to the **vehicle surface path, including its CPU preparation, command submission and subsequent rendering**, as the primary measured target in this fixed view. It does not establish isolated GPU time or split the saving into CPU and GPU portions. The surface-omission phase also omits rail/junction/animal surface commands and supplies empty surface data to the remaining full-screen shader. Both that broader omission and the shader's changed input branches are part of the result. Disabling this path is a diagnostic, not a performance fix that preserves winter functionality.

The first and final baselines differ by 1.75 FPS / 1.209 ms, approximately 4.5–4.7%. This scene drift is much smaller than the surface-stage difference but limits precise attribution. The normal simulation continues: an active DE2 melts from 8% to 30% cover during the surrounding sequence. The near-60 results can also be constrained by the game's frame cap. The zero contamination flag establishes continuous tracked camera rendering, not laboratory isolation of every game subsystem.

### CPU detail after rendering was restored

The following complete performance window, line **2039**, shows **38.6 FPS** with the normal snow path. It precedes the enter/exit activity beginning at line 2042, so it is more useful than the mixed 20-second reports that overlap several probe phases.

| Scope | Mean / maximum CPU time |
| --- | ---: |
| All render-command recording | 6.02 / 8.24 ms |
| Vehicle culling and preparation | 3.29 / 4.89 ms |
| Part filtering/state/frustum work, nested within culling | 1.77 / 2.90 ms |
| LOD preparation, nested within culling | 0.79 / 1.15 ms |
| Coarse visibility, nested within culling | 0.49 / 0.79 ms |
| Topology update, nested within culling | 0.11 / 0.22 ms |
| Draw-request construction | 1.52 / 2.32 ms |
| Scheduler plan/record | 0.77 / 1.94 ms |
| OBB validation | 0.40 / 0.92 ms |

The same window reports approximately **7079 inspected parts, 3256 LOD rejections, 1398 state rejections, 2206 frustum tests and 921 LOD groups per submission**. It emits **1404.1 snow commands** from 1593.1 detailed and 281.2 cheap draw requests. The graph performs **zero component rebuilds and zero pair tests**; there are **12 plan builds versus 760 cache hits**, and just 58 matrix uploads across the whole reporting window. The continued cost is therefore neither a topology-rebuild storm nor failed plan caching.

The actionable next work is to remove repeated part/LOD/state queries and draw-request construction in stable views while keeping invalidation correct for vehicle motion, animation, camera changes and renderer/interior changes, and to reduce the actual surface submission cost where safe. Rewriting the full-screen shader first is not supported by this A/B result. A change still needs output parity and timing verification; the temporary 58.54 FPS mode is not a delivered feature-preserving optimization.

No DVSeasons error or color/depth mismatch appears in this second run. The duplicate-mod manager error, network TLS messages and `Bolt.SceneVariables` exception at shutdown remain separate messages; no probe failure is reported.

### Follow-up implementation checks

No production rendering change was made while inspecting this run. Two apparently simple shortcuts do not address the measured gap:

- Persistent per-car command buffers would retain recorded custom world-to-vehicle matrices and copied instancing data. Native renderer motion alone does not update those recorded values. Keeping them correct would still require invalidation on pose changes; separate per-car buffers would also lose cross-car instancing. This approach does not eliminate the live visibility queries or the GPU draw count.
- Collapsing material slots has little potential in the shipped freight geometry. A read-only `resources.assets` inventory found one submesh in 2728 of 2770 meshes; the train-name subset, excluding collider/shard names, had 845 single-submesh meshes out of 852. Even merging every remaining slot would reduce that subset from 884 to 852 submissions, about 3.6%, before accounting for glass/cutout differences. This is an asset inventory, not a weighted count of this live yard. Unity 2019.4 also clamps a negative `CommandBuffer.DrawRenderer`/`DrawMesh` submesh index to zero; it is not an all-submesh draw.

A private depth attachment plus unrestricted batching is a larger architectural candidate, not a validated optimization. It would change the present last-eligible-draw semantics to nearest-surface semantics; coplanar full-snow/exclusion surfaces can still depend on order. Existing overlap, exclusion, animated-part and stereo fixtures must demonstrate the intended output before such a change can replace the current path. The measured 8.6–9.8 ms saving must not be promised for any of these unimplemented approaches.
