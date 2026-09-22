# Vehicle surface submission — 19 September 2026

The completed in-game probe located the large winter cost in vehicle surface preparation/submission/rendering: 38.91 FPS normally, 58.54 with surface commands omitted, 38.12 with only final snow shading omitted, and 37.16 after restoration. This is diagnostic evidence, **not an optimization to ship by disabling snow**. The precise interpretation and preserved log are in [WINTER_SUMMER_2026-09-19.md](WINTER_SUMMER_2026-09-19.md).

The pre-change DLL/source baseline is preserved in `artifacts/backups/surface-opt20260919/`. Production changes are limited to `SnowVehicleRegistry.cs`, `SnowVehicleDrawScheduler.cs` and diagnostic counters in `SnowPerformance.cs`; snow shaders, resolution, accumulation, heat, wind, visibility tests and user settings retain their behavior.

## Accepted changes

**Prepared request metadata.** Discovery builds an immutable token per eligible renderer/submesh, containing the renderer, mesh, cutoff, alpha texture and UV transform. Request ingestion can compare token identity instead of reconstructing and comparing those same values every frame. Full/exclusion pass selection and instancing eligibility remain current. Object matrices, captured vehicle matrices and snow IDs are always supplied live. Rediscovered equal metadata uses exact semantic comparison, so harmless token replacement need not invalidate a plan. The original ingestion API remains available for fixtures/fallbacks.

**Adaptive part ordering.** The existing vehicle overlap DAG and its two-plan cache remain intact. A separate part scheduler can batch independent parts within connected consists while preserving native order for actual padded overlaps. It is considered only with 2–2048 visible part streams, more than 64 vehicle commands, and a vehicle command count greater than one sixteenth of the request count. It requires eight unchanged submissions after detecting the exact ordered part layout and conservative published envelopes. Geometry escaping an envelope, visibility/LOD/interior changes, and excessive stream counts immediately return to the vehicle path. The quarter-metre envelope margin tolerates physics jitter; matrices are not frozen. Unsupported geometry retains the conservative vehicle envelope. A fine plan is rejected for that stable layout unless it reduces commands by **more than 25%**. Independent caches avoid destroying the vehicle plan while trying fine ordering.

**Scalar overlap tests.** Sweep intervals now retain all six AABB extrema. The narrow phase compares those floats directly instead of repeatedly constructing `Bounds.min/max` vectors for every pair. Local Unity 2019.4 `Bounds.Intersects` IL was checked: comparison order, touching bounds, negative extents and NaN behavior are retained. Sweep order, OBB checks, hysteresis and dependency direction do not change. `snow-scheduler-topology-build` measures actual graph construction separately, nested inside scheduler recording.

## Verification and measured limits

The final [paired production-DLL run](../artifacts/verification/surface-opt-paired-scalar-final.log) passed **346,525 checks** on a shared 224-car scene with 7,168 renderers and 921 LOD groups. It checks ordered parts/draws, bounds and exact surface pixels across camera/LOD/state changes, physics jitter, coincident/near-coplanar geometry, limits, detached parts, streaming, destruction and origin shifts. Both DLL hashes, bundle hash, seven alternating timing trials and all cold-transition frames are recorded in that log.

| Final synthetic scenario | CPU record/release, old → new | Record + camera + GPU completion, old → new | Commands |
| --- | ---: | ---: | ---: |
| Stationary independent cars | 5.336 → 4.706 ms | 8.107 → 8.064 ms | 18 → 18 |
| Moving camera / LOD | 5.108 → 4.774 ms | 7.086 → 7.099 ms | 18 → 18 |
| Physics jitter | 6.503 → 5.794 ms | 8.722 → 8.094 ms | 18 → 18 |
| Renderer/interior changes | 6.263 → 6.232 ms | 9.437 → 7.652 ms | 21 → 21 |
| 79-car snow limit | 3.590 → 3.493 ms | 6.982 → 6.655 ms | 740 → 740 |
| Connected consists, independent detailed parts | 5.794 → 5.850 ms | 9.324 → 9.227 ms | 250 → 30 |

These timings are Unity Editor/D3D11/RTX 4060 at **800 × 600**, not the user's 2560 × 1440 gameplay. Connected-layout command reduction is strong, but the final run's total render gain is small and noisy; reducing commands does not establish proportional FPS improvement. The first fine submission costs **11.902 ms total**, including graph construction, versus a 6.477 ms baseline frame. One graph is built, then reused. The earlier non-scalar prototype took 49.355 ms at that transition. Twenty alternating camera/renderer/interior submissions produce **zero** fine-graph builds and preserve the vehicle command counts. Cold graph creation is still synchronous and can be noticeable on slower CPUs; the 2048-stream cap is a work bound, not a universal frame-time guarantee.

The [final extreme two-consist test](../artifacts/verification/surface-opt-heavy-final.log) passed **12,889 checks**. Its trial fine plan saved only 960 → 900 commands, so the gate rejected it and returned to 960 on the next submission without repeated retries. That trial frame cost 17.204 ms; the first scene submission was 37.467 ms versus 42.928 ms for the baseline. Steady CPU improved 7.603 → 7.035 ms, while total render timing was 15.253 → 15.497 ms: **no total-render/FPS improvement is established for this pathological case**.

Additional evidence:

- [Final 128-car yard suite](../artifacts/verification/surface-opt-yard-final.log): exact IDs, captured coordinates and slopes across cutouts, interleaved full/exclusion ordering, native fallbacks, moving parts, origin shifts and snow limits 0/96/64.
- [Prepared-request fixture](../artifacts/verification/surface-opt-prepared-initial.log): eight exact pixel comparisons passed. At 224 cars / 2240 requests, request ingestion fell from 1.004 to 0.561 ms; mixed total recording fell from 1.507 to 0.984 ms. Native-only recording fell from 2.027 to 1.470 ms. These direct-delegate tests exclude reflection from timed loops and record no steady plan rebuilds.
- [Actual Unity bounds parity](../artifacts/verification/surface-opt-bounds-final.log): **750,726** comparisons against the production scalar predicate, including random IEEE floats, infinities, NaNs, touching bounds and signed zero.
- [Managed scheduler audit](../artifacts/verification/surface-opt-scheduler-managed-final.json): **2,832 checks**, 1,000 randomized scenarios, 800 dynamic graph frames, over 1.12 million requests and zero measured steady managed allocations. Unity doubles make this a scheduler correctness/allocation test, not a native/GPU benchmark.
- [Build and unit tests](../artifacts/verification/surface-opt-final-build.log): zero compiler warnings/errors, **293 tests** and **52 English/Russian translations** pass.

## Rejected experiments

The whole-vehicle frustum acceptance helper passed correctness but was slower in every measured case, including 0.218 → 0.340 ms for fully contained mono geometry and 0.267 → 0.554 ms for mixed stereo. It is absent from production; its source is archived under `artifacts/verification/surface-opt-experiments/` and [results](../artifacts/verification/surface-opt-frustum-initial.log) are retained.

Unconditional part scheduling also passed rendering parity but raised ordinary independent-car commands from 18 to 22 and produced approximately 30 ms recording during renderer/interior churn. [Prototype results](../artifacts/verification/surface-opt-paired-third.log) explain why it was replaced by the adaptive gate and separate caches. A pathological two-consist trial also rejected its fine plan after the trial submission rather than retrying every frame; [that evidence](../artifacts/verification/surface-opt-heavy-initial.log) predates the scalar narrow-phase fix.

No live-game FPS increase or performance on low-end hardware has been measured for this final build. It preserves winter functionality and removes verified request/overlap overhead, but the remaining native state/LOD checks, surface rendering and one-time fine-plan construction still cost time. Follow-up gameplay logs should compare fixed views and include the new topology-build scope; the synthetic results do not justify a promise of stable 60 FPS.
