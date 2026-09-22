# Oriented vehicle overlap and discovery reuse — 2026-09-19

This follow-up targets unnecessarily ordered snow draws after the successful AABB hysteresis iteration. The existing AABB movement margin, depth-padding policy, sweep, two-plan LRU and MicroSplat implementation are retained. No shaders or AssetBundles change.

## Ordering and safety

The scheduler still uses its cached world AABB sweep. Candidate AABB intersections receive an additional oriented-box separating-axis test (six face normals and nine cross-product axes). Only a proven separation removes an edge. Connected components retain native vehicle order; this iteration does not introduce part-level dependency scheduling.

The oriented envelope starts from the cached vehicle-local mesh bounds. An orthogonal basis and projections of all affine half-edges conservatively cover nonuniform/mirrored scales and parent shear. A separate 0.25-metre movement reserve keeps the published oriented box stable under small pose changes; it does not modify the existing AABB hysteresis. Scheduler validity checks both published shapes. Touching boxes remain ordered, and degenerate/nonfinite boxes fall back to AABB ordering.

Before submission, current visible part geometry is checked against the oriented envelope. Ordinary meshes use their local bounds and current renderer matrix; skinned meshes use current localBounds. Static batches, additional vertex streams and meshes replaced since discovery use native world bounds. Replaced meshes also retain DrawRenderer rather than instancing a stale mesh. Unchanged part validation is cached by exact source bounds, matrix, depth padding and oriented-envelope revision. Near-axis-aligned roots retain the already tight AABB path, avoiding oriented-part checks when they would add little value. All these fallback decisions can add ordering constraints, never remove necessary ones.

## Diagnostics

The existing 20-second report adds:

- `snow-components(count-mean/largest-max/size-avg/multi-vehicle-mean)`: graph snapshots include all registered streams, including empty offscreen streams. Count and multi-vehicle component count are submission averages, largest is the maximum observed size, and size-avg is total vehicle memberships divided by total component counts.
- `snow-instancing-eligibility-full(eligible/unique-mesh/skinned/static/mirrored/streams/other/unique-key)`: full surface draw counts per ordered submission. The first seven entries partition requests by eligibility/reason. `streams` means additional vertex streams. `unique-mesh` means insufficient registered mesh sharing, not a count of distinct meshes. `unique-key` is a subset of eligible draws with no matching full draw key in the current submission; its count is cached with each of the two plans. Matching mesh alone does not suffice when submesh, cutout, texture or UV state differ.
- `snow-obb-window(tests/rejected)`: totals of valid oriented candidate tests and rejected AABB overlaps. Reused topology adds neither. `actual-overlaps` now counts accepted overlaps after narrow phase; `pair-tests` remains the broad-phase candidate count.

Component and eligibility snapshots describe submissions which use the ordered scheduler. Frames taking the existing native/cheap-exclusion path do not add snapshots. These metrics are CPU-side diagnostics, not GPU timings. Large connected components caused by real couplers/parts are still possible; a live yard log should determine whether part-level dependencies are worth the added complexity.

## Parts refresh

RefreshParts now reuses renderer/group lists, ID sets and LOD lookup storage. A root already covered by an earlier ancestor is not traversed again, including inactive descendants. Later ancestors remain in original order to preserve first-seen ordering in unusual modded hierarchies. Value-type LOD membership replaces temporary tuples. Scratch references are cleared after every refresh.

This removes redundant work but deliberately retains dynamic material/LOD reclassification and height/topology invalidation. It does not claim to eliminate every enter/exit spike. A full body/interior/cargo cache split needs additional lifecycle work because the native interior container survives load/unload, and external/dummy objects can change roles without changing identity.

## Verification

- Release: 281 unit tests, 48 localization checks, zero warnings/errors.
- Independent Unity geometry oracle: 211,335 checks, 4,109 box pairs including 60 separations requiring cross-product axes. No missed intersections. Contacts, degenerate fallback, scales/shear, animated guards and origin shifts pass. 5,000 changing jitter poses retain the same published oriented box.
- A synthetic 224-car rotated yard has 796 AABB overlaps and 208 oriented overlaps. These are geometry counts, not real-game FPS.
- Production Unity renderer: 128 long cars rotated 37 degrees produce 1,025 commands with AABB-only dependencies and 40 with oriented dependencies. The largest component falls from 128 to 4. Native-vs-batched IDs, slopes and captured coordinates match exactly, including animated exclusions, replaced meshes, real overlaps, native fallbacks and origin shift. 120 jitter frames make no component rebuilds.
- On that scene, five alternating 40-frame trials give a median submission + native camera + GPU-completion time of 5.3756 ms with AABB-only ordering and 3.8269 ms with OBB. CPU-only recording is 2.6618 versus 3.2509 ms: oriented validation has a CPU cost despite the reduction in total rendering time. This is a local synthetic measurement, not game FPS or a promise for every yard layout.
- Existing 128-car jitter regression: 240 frames, zero component builds and zero pair tests, with actual draw matrices still updating. A car moving through another still updates ordering and preserves pixels.
- The final full 128-car rendering regression retains exact pixels for overlap groups, native fallbacks, destroyed renderers and limits 0/96/64. A moving camera/LOD/visibility test makes zero component builds across 240 frames. Alternating two warmed plans yields 80 hits, zero plan builds, zero component builds and zero matrix uploads.
- Parts discovery: 2,368 assertions across 31 refreshes match original renderer/LOD order, classification and material data. Hierarchy queries fall from 322 to 122 across the fixture. Nested, detached, inactive, later-ancestor roots, dynamic cab/cargo/external/dummy changes and released scratch references are covered.
- Existing AABB scheduler audit: 2,826 checks. Counter audit: 60 checks including cache hits, two-plan restoration, empty submission and recovery. These API-double audits retain conservative narrow-phase fallback; actual OBB math is tested separately in Unity.
- Performance aggregation: 114 checks, 100,000 warmed snapshot calls and 100,000 scope calls with zero managed allocation.

Evidence is under `artifacts/verification`: `obb-yard-build.log`, `obb-final-geometry.log`, `obb-registry-final.log`, `obb-final-render-parity.log`, `obb-final-camera-lru.log`, `obb-final-hysteresis.log`, `obb-parts-refresh.log`, `scheduler-regression-audit20260919.log`, `scheduler-counter-audit20260919.log`, and `snow-batching-performance-audit20260919.json`. Backups are under `artifacts/backups/obb-yard20260919`. Results demonstrate rendering safety and reduced dependencies in fixtures; the next comparable live log is required to measure game FPS and decide further work.
