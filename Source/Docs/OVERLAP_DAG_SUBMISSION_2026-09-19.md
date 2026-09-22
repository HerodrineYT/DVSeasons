# Actual-overlap DAG and submission cost — iteration 8

The preceding live report shows that oriented bounds increase instancing but ordered submission remains expensive. This follow-up retains the AABB sweep, OBB narrow phase, movement hysteresis, two-entry plan cache, parts-discovery pooling and existing shaders. It targets ordering freedom and the cost of preparing the same geometry repeatedly.

## Dependency scheduling

Each accepted overlap between native stream indices i < j adds only the edge i → j. Connected components remain available as diagnostics; they no longer serialize unrelated members. A stream becomes ready after every actual predecessor finishes. An iterative queue releases successors, including empty/offscreen streams, without recursion. Native order within each vehicle, full/exclusion barriers, and all actual overlap ordering are retained.

The two plans store exact sorted successor lists as part of their signatures. Equal component sizes or equal predecessor counts cannot validate a plan after edges change. A maximum of 131,072 cached edges per plan bounds cache memory; denser graphs still execute all dependencies but do not retain a plan. No edges are dropped to fit a budget. Cached grouping never freezes current draw matrices or snow IDs.

## Reusing visibility geometry

Visibility already reads each renderer's native world bounds. The oriented guard now first proves that this complete AABB, including depth padding, lies inside the current oriented envelope. Passing parts skip mesh lookup, local bounds, world-matrix reads and the detailed per-part envelope cache. Three interval projections use double arithmetic; no epsilon admits an escaping corner. Large diagonal bodies whose AABB does not fit retain the exact mesh/localBounds fallback. This geometric proof also remains valid for moved/detached or animated parts; the code does not assume bogies or modded renderers are permanently rigid.

OBB update and validation run once per fleet submission, instead of updating a visible vehicle twice. Detailed fallback reads bounds once per shared Mesh per submission and shares static/streams/mesh-identity checks with the instancing path. The temporary mesh dictionary is cleared in finally; no new persistent mesh cache can retain unloaded assets or miss a next-frame mutation. Actual draw matrices still update, and swapped meshes still take native rendering until discovery catches up.

A failed AABB fast check is remembered for that part's current oriented-envelope revision and padding. It bypasses only the unsuccessful fast test; detailed validation still executes. A moved part could miss a new opportunity to use the fast path until the box changes, which is conservative. Successful checks are never reused without validating the current bounds. This avoids paying for two validations on long diagonal body parts every frame.

## Diagnostic meanings

- `snow-obb-validation`: accumulated CPU time validating the oriented envelopes for one camera submission.
- `snow-scheduler-plan-record`: CPU time preparing dependencies, matching/building a plan and recording its commands. This includes the upload time below; scopes must not be added together.
- `snow-full-batch-upload`: the sum of native SetFloatArray/SetMatrixArray times for full batches in a submission. A cache hit with unchanged properties records zero upload time. This is not GPU execution time and does not include all DrawMeshInstanced or driver cost.
- `snow-ready-streams(avg/max)`: mean number of dependency-released, unfinished, nonempty streams at batch decisions, weighted by decision count; maximum across the report window. A reused plan retains its planning frontier statistics without replaying graph scheduling.
- `snow-full-batch-size(avg/max)`: total full-instanced requests divided by full batch count, plus the largest such batch. Native singleton draws are not instance batches.

Existing components, OBB, eligibility, plan and matrix counters keep their definitions. The new fields help distinguish dependency limitations from plan/array-upload costs. They do not establish whole-game FPS causality on their own.

## Validation

The direct-overlap scheduling audit covers 1,000 randomized scenarios, 800 graph-change frames and 1,127,315 requests (2,829 assertions), including exact accepted-edge equality and preserved draw order. The counter/cache audit has 1,128 assertions: a graph can change edges while retaining identical component and indegree counts; the old plan must not be reused, while an exact second signature can be recovered. It also covers 134,940-edge dense fallback and a 4,096-stream empty chain. Warmed scheduling and broadphase audit loops allocate no managed memory.

Unity's production-registry fixture compares surface IDs, coverage, R8 slopes and captured coordinates with native rendering. Fork/join, diamond, chain, touching boxes, an empty invisible bridge, captured frames, changing snow IDs, two-plan visibility patterns, changed edges, native fallbacks and origin relocation match. The fork produces 16 commands instead of 24; the diamond produces 24 instead of 32. Every request appears once, and all direct/transitive overlap dependencies and full/exclusion barriers remain ordered. These are fixture results, not game FPS estimates.

Performance aggregation passes 155 checks, including weighted ready counts, resets, counter rollover and accumulated upload timing. Its warmed measured paths allocate no managed memory.

The final release build passes 281 unit tests and 48 localization checks with zero warnings/errors. Full 128-car Unity rendering preserves limits 0/96/64, native fallback and destroyed-renderer handling. A moving camera/LOD/visibility test produces zero topology rebuilds over 240 frames; 80 warmed alternating plans produce 80 hits and zero builds/uploads. The 128-car simulated pose-jitter test also produces zero component builds/pair tests over 240 frames, while real motion, detached details, depth tolerance and origin shifts remain correct. The OBB and AABB hysteresis source files are byte-for-byte unchanged from iteration 7.

The production OBB-validation fixture passes 3,241 assertions across 130 submissions: 2,335 parts take the fast guard and 259 take detailed validation, with 131 native shared-mesh bounds reads. It covers 120 jitter frames, bogie animation, detach, mutable/replaced meshes, runtime vertex streams, skinned/static-batched parts, mirrored scale, depth-padding growth and origin shifts. Independent corner checks verify that accepted geometry stays inside the published box. The negative memo remains conservative and does not reset other systems' Transform.hasChanged flags.

## Comparable submission measurements

Separate fresh Unity processes load the preceding iteration-7 DLL and the current DLL against the same bundles and 128-car scenes. Each scene verifies native/ordered pixel equality, then measures five alternating 40-frame render trials and 150 CPU-only submissions. Timings include the same local RTX 4060/D3D11 setup; they still vary between runs and are not game FPS.

| Scene | Previous commands | Current commands | Previous/current ordered CPU ms | Previous/current render + GPU completion ms |
| --- | ---: | ---: | ---: | ---: |
| 32 independent diamond groups | 40 | 32 | 2.3680 / 2.2210 | 3.3460 / 2.9611 |
| Long parallel cars, 37-degree yaw | 40 | 24 | 2.5383 / 2.8521 | 3.4055 / 3.8164 |

The rotated case does not demonstrate an absolute timing improvement in this run. Its unchanged native control also increased from 1.3596 to 1.5255 ms CPU and from 4.1432 to 4.8565 ms rendering. Do not attribute all between-process variation to the mod or claim a universal speedup. Only nine parts fit the native-AABB fast guard in that deliberately stretched scene; 1,016 use the detailed path, but just eight shared mesh bounds are read. The diamond scene and smaller-detail validation fixture exercise different costs. A new live performance log should determine the practical benefit and remaining bottleneck.

Evidence lives under `artifacts/verification`: `dag-yard-build.log`, `dag-final-render-parity.log`, `dag-final-dependencies.log`, `dag-final-camera-lru.log`, `dag-final-hysteresis.log`, `dag-obb-validation.log`, `dag-submission-baseline.log`, `dag-submission-final.log`, `scheduler-dag-regression-audit20260919.log`, `scheduler-dag-counter-audit20260919.log`, `snow-dag-performance-audit20260919.json`. Backups and the preceding runtime are under `artifacts/backups/dag-yard20260919`.
