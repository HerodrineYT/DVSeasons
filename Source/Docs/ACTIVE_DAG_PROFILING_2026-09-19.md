# Active draw dependencies and submission profiling — iteration 9

Iteration 8 preserved direct overlap ordering, but an invisible vehicle could still impose a transitive dependency between two visible vehicles which never overlap. This follow-up removes ordering constraints contributed by streams with no draw requests in the current camera submission. It also separates the remaining vehicle preparation and recording costs in the performance report.

The geometric overlap graph, AABB sweep, OBB tests, movement hysteresis, two-entry plan cache, instancing eligibility, snow appearance and shaders remain unchanged. `SnowOrientedBounds.cs` and `SnowVehicleTopologyBounds.cs` are byte-for-byte identical to their iteration-8 backups. This change does not remove snow or reduce texture, exposure-map or render-target resolution.

## Geometric graph and active graph

The complete fleet retains its stable native stream order and its cached geometric DAG. Every accepted geometric overlap between indices `i < j` still contributes the direct edge `i -> j`. Connected-component diagnostics continue to describe this complete geometry, including vehicles outside the camera.

Scheduling uses the induced subgraph of nonempty streams: a direct edge constrains drawing only when both endpoints have at least one request. A stream containing cheap exclusion requests is nonempty and retains all applicable ordering. A truly empty stream has no framebuffer writes and contributes no runtime dependency.

For `A -> B -> C`, where A and C do not overlap, hiding all requests from B allows A and C to batch. Making B visible again restores both direct ordering constraints immediately. A direct `A -> C` overlap is always retained when A and C are active, regardless of any empty vehicle between their indices. Native order within each active stream, full/exclusion barriers and all active overlap constraints are preserved.

`PrepareActiveGraph` runs on plan misses. Each of the existing two cached plans retains its active-graph and ready-stream statistics. A cache hit restores those statistics without redoing graph scheduling. Per-stream request counts and the exact geometric successor lists remain part of plan validation, so transitions between zero and nonzero requests cannot reuse an incompatible plan. Visibility changes alone do not rebuild the geometric overlap graph. The existing request, stream and dependency cache bounds are unchanged.

## Performance field meanings

`snow-active-graph(active-streams/active-edges/empty-streams/empty-bridge-edges)` contains means per completed ordered submission, using its own sample count rather than the draw-report or whole-game frame denominator:

- `active-streams`: streams with at least one full or exclusion draw request.
- `active-edges`: geometric direct edges whose two endpoints are active.
- `empty-streams`: registered geometric streams with zero requests in this submission.
- `empty-bridge-edges`: every removed geometric direct edge with at least one empty endpoint, counted once. An empty-to-empty edge is counted once too. This is not the number of unique bridge vehicles or transitive paths.

For each snapshot, active streams plus empty streams equals the geometric stream count; active edges plus empty-bridge edges equals the geometric direct-edge count. Window totals use `long` storage and are cleared after each report and on `Reset()`.

Existing `snow-components` values still describe the complete geometric graph. Large geometric components therefore do not necessarily imply a serialized active submission. Existing ready-stream means remain weighted by recorded-batch decision count; cached plans provide their planning statistics. Full-batch size remains full-instanced requests divided by full instance batches, with a separate maximum; native singleton draws are excluded.

## CPU scope boundaries

The new scopes measure CPU preparation and command recording. They do not measure deferred command-buffer GPU execution.

The four outer scopes below partition `RecordCommands()` without overlapping one another:

| Outer scope | Work included |
| --- | --- |
| `snow-buffer-prep-record` | HDR/LDR material mode, temporary copy targets, GBuffer/AO copy commands and normal-buffer binding. |
| `snow-vehicle-record` | Animal visibility preparation, `vehicles.Record`, draw-statistic sampling and animal exclusion recording. All vehicle subscopes below are inside this scope; animal work is included even though it is outside the registry itself. |
| `snow-snow-shading-record` | Exposure/noise/weather/global state, ambient light, inverse view-projection matrices including stereo, target binding and the fullscreen snow draw command. |
| `snow-frame-cleanup-record` | Temporary buffer releases and vehicle frame-target release commands. |

The vehicle scope contains these finer measurements:

| Scope | Work included | Relationship to other scopes |
| --- | --- | --- |
| `snow-vehicle-culling` | Frame reset, shared-mesh refresh when dirty, camera and vehicle visibility, LOD/part selection, stable topology preparation and native-bound validation. | Contains `snow-vehicle-surface-prep-record`; do not add both as independent costs. |
| `snow-vehicle-surface-prep-record` | Surface render-target allocation/clear commands and junction/rail surface recording. | Nested within culling; GPU execution occurs later. |
| `snow-obb-validation` | Oriented-envelope update and validation for the ordered fleet. | Runs after culling, outside request construction. |
| `snow-vehicle-request-build` | Scheduler `Begin`, stream creation and draw requests, current matrices/IDs and eligibility classification; native-path draw recording when ordered scheduling is not selected. | Ends before scheduler `End`. |
| `snow-scheduler-plan-record` | Dependency preparation, cached-plan matching/restoration or plan construction, and command recording. | Includes `snow-full-batch-upload`. |
| `snow-full-batch-upload` | Sum of native `SetFloatArray`/`SetMatrixArray` elapsed ticks for full instance batches in one submission. | A nested subset of scheduler recording, not an additional independent cost or GPU timestamp. |
| `snow-vehicle-post-record` | Published draw/diagnostic snapshots, remaining exclusion flush, per-vehicle snow/rotation arrays and final texture/global bindings. | Runs after scheduler `End`. |

All four outer scopes are within the existing overall render submission measurement. Comparing their means is useful, but adding a parent scope to its children double-counts work. Vehicle subscopes can have different sample counts: an invisible scene may return during culling, while OBB and scheduler scopes run only on the ordered path. None should be interpreted as whole-game frame time. Existing command-buffer samples remain available to external graphics/profiling tools; their presence does not turn these CPU log fields into GPU timings.

## Validation

The release build passes 281 unit tests and 48 English/Russian localization checks, with zero warnings or errors.

The managed scheduler audit passes 2,832 scenario checks across 1,000 randomized scenarios, 800 graph-change frames and 1,127,322 requests. It independently verifies exact geometric successor edges, connected-component metrics, active/empty graph counts and ordering of all active direct dependencies. Warmed steady scheduling, broadphase and alternating-plan loops allocate no managed memory.

The counter/cache audit passes 1,272 checks. It covers hidden/restored bridges, both cached visibility patterns, direct active overlaps, empty-to-empty edge counting, all-empty submissions, disposal, dense uncached dependency fallback and large fleets. In its 224-stream geometric chain, hiding every other stream leaves 112 independent active streams and zero active edges: their single shared full draw combines into one instance command. Restoring all 224 streams restores 223 active edges and 224 ordered commands. Repeated visibility changes reuse the two plans without rebuilding geometric components. These are controlled fixture results, not game FPS measurements.

Performance aggregation passes 180 checks, including independent submission denominators, consecutive windows, reset, multiple cameras/registries, counter rollover and totals beyond `int`. Each of the warmed scope, eligibility, ready-stream, elapsed-tick and active-graph sampling loops performs 100,000 calls with zero managed allocations.

The real Unity registry/dependency fixture compares native and ordered surface IDs, coverage, R8 slopes and captured coordinates. With the middle bridge hidden, 16 requests produce eight recorded batches/commands and zero active edges. Restoring the middle vehicle produces 24 requests, 24 batches/commands and two active edges. Twenty warmed visibility alternations preserve parity and reuse cached plans without rebuilding geometry. Fork/join, diamond, touching bounds, changed edges, captured frames, native fallbacks and origin relocation also pass.

The complete 128-car Unity rendering fixture passes with snow limits 0/96/64 and preserves native fallbacks, cutouts, hidden/restored parts and destroyed-renderer handling. The camera/LOD/visibility fixture produces zero component rebuilds and zero pair tests over 240 frames. Eighty warmed alternating visibility plans produce 80 cache hits, zero plan builds and zero matrix uploads, while retaining native pixel parity and live matrix/ID correctness.

The simulated pose-jitter fixture keeps 128 cars at 37-degree yaw for 240 jitter frames with zero component rebuilds and zero pair tests. It still performs 1,440 draw-matrix uploads as exact poses change. A separate 100-frame real-movement sequence performs 24 component rebuilds and retains correct rendering. This confirms that graph reuse does not freeze actual draw transforms; it is not a PhysX or game-FPS measurement.

## Procedural-pass measurement methodology and limits

The separate GPU-cost experiment uses the packed production `ProceduralSnow` shader at 2560 x 1440 in HDR and LDR. It compares pass-off, copy-only and full-pass modes over identical prepared inputs for near ground, transition-distance rocks, distant rocks, a vehicle yard and a sky-heavy scene. Exposure maps remain 1024 square and vehicle arrays remain 256 square. The experiment changes no production shader, resolution or quality setting.

After warm-up, seven trials alternate mode order. Each measurement submits a pre-recorded batch of 32 passes and waits for GPU completion through readback; results are normalized per pass. Copy-only and off output hashes must agree, and repeated full passes must give stable, visibly changed output. Full-resolution RGBA8 hashes are a consistency check, not float-buffer semantic parity.

The verified public Unity 2019 recorder API does not provide isolated per-pass GPU timestamps. These timings therefore include CPU submission and GPU-completion synchronization; whole-frame timing is not substituted for an individual pass. A paired full-minus-copy delta estimates incremental work in this synthetic experiment but is not pure shader GPU time or a forecast of game FPS. Report the raw paired measurements and variation separately before drawing conclusions about the next optimization target.

The run passed all ten configurations on Unity 2019.4.40f1 / Direct3D11 / NVIDIA GeForce RTX 4060. Neither `Recorder.gpuElapsedNanoseconds` nor `ProfilerRecorder` exists in the loaded public API; `FrameTimingManager.GetGpuTimerFrequency()` returned zero. The table reports milliseconds per pass from the actual alternating trials, including the stated CPU/synchronization costs.

| Input pattern | HDR | Off median | Copy median | Full median | Paired full − copy median | Paired shading delta range |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Near ground | Yes | 0.002991 | 0.017422 | 0.249384 | 0.231613 | 0.229603–0.238609 |
| Near ground | No | 0.002941 | 0.168241 | 0.435753 | 0.267138 | 0.261322–0.268050 |
| Transition rocks | Yes | 0.003409 | 0.017919 | 0.268759 | 0.250759 | 0.248081–0.255556 |
| Transition rocks | No | 0.004731 | 0.161072 | 0.423438 | 0.261122 | 0.257597–0.284288 |
| Distant rocks | Yes | 0.002653 | 0.017344 | 0.234378 | 0.216963 | 0.211359–0.221556 |
| Distant rocks | No | 0.003244 | 0.161641 | 0.402328 | 0.239709 | 0.228163–0.245566 |
| Vehicle yard | Yes | 0.002559 | 0.017409 | 0.196378 | 0.178894 | 0.171897–0.186475 |
| Vehicle yard | No | 0.002722 | 0.162022 | 0.344197 | 0.184072 | 0.176350–0.188831 |
| Sky-heavy | Yes | 0.002622 | 0.017113 | 0.144328 | 0.126984 | 0.123684–0.135903 |
| Sky-heavy | No | 0.003734 | 0.203325 | 0.400209 | 0.195259 | 0.179106–0.203388 |

The incremental full-minus-copy median ranges from 0.127 to 0.267 ms in this workload. HDR copy-only medians are approximately 0.017–0.018 ms; LDR's three full-resolution copies are approximately 0.161–0.203 ms. This identifies copy preparation as a substantial part of the synthetic LDR cost. It does not establish the principal bottleneck in a live railway yard, justify reducing quality, or predict an FPS gain. No shader optimization was made on the basis of this experiment.

The timed textures have the full 2560 x 1440 dimensions, but contain deterministic 320 x 192 parity patterns expanded beforehand. They sample three vehicle-array slices rather than a streamed fleet. MRT formats are ARGB32/ARGB32/ARGBHalf; normal data uses ARGB2101010, depth RFloat, vehicle data ARGBHalf and slope R8. HDR uses the existing 1280 x 720 R8 AO copy. The 32 repeated draws reuse resources and accumulate native HDR blending within each initialized batch. This is a throughput test; geometry submission, map capture, scene rendering, VR and cold-cache behavior are excluded. Paired medians need not equal differences between separately calculated medians.

All ten output checks confirm that copy-only leaves the MRTs unchanged, full shading changes the image, and repeated identical full shading is stable. The log contains no rendering, shader, render-target-dimension or readback errors. Unity's initial licensing IPC connection failed and then successfully recovered before the experiment; this was unrelated to rendering. Exact values and limits are also recorded in `active-yard-procedural-gpu-ab-results.json`.

## Evidence

All verification artifacts are under `artifacts/verification`:

- `active-yard-build.log`
- `active-yard-install.json`
- `active-yard-dependencies.log`
- `active-yard-render-parity.log`
- `active-yard-camera-lru.log`
- `active-yard-hysteresis.log`
- `scheduler-active-regression-audit20260919.log`
- `scheduler-active-counter-audit20260919.log`
- `snow-active-performance-audit20260919.json`
- `active-yard-procedural-gpu-ab.log` (separate experiment; methodology and limitations above)
- `active-yard-procedural-gpu-ab-results.json` (ten exact medians, paired ranges, resources and limitations)

Pre-change source/runtime backups are under `artifacts/backups/active-yard20260919`. This note supersedes iteration 8's treatment of empty streams as transitive ordering bridges; it does not change the underlying geometric DAG or hysteresis.

The installed runtime DLL has SHA-256 `D8377C9E5FA4C9E5114170F4EB683F6C362025A4D9A98A45C250B7FF6F8A0AE2`. Existing user settings were preserved.
