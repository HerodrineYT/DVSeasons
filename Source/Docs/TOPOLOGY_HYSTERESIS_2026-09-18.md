# Tight topology geometry and movement hysteresis — 2026-09-18

This follow-up addresses the real-game result that the earlier camera-stable topology still rebuilt almost every frame. The local log confirms frequent component builds despite draw-plan cache hits. It does not contain individual poses, so PhysX jitter remains a plausible cause, not a proven attribution. The old world-AABB → vehicle-AABB → world-AABB inflation is directly demonstrable from the implementation.

## Geometry and invalidation

For an ordinary mesh, the registry now transforms `Mesh.bounds` directly into the vehicle frame using `vehicle.worldToLocalMatrix * renderer.localToWorldMatrix`. Skinned renderers use `localBounds`. All opaque snow and exclusion parts remain included, across LODs and detached interior/external/cargo roots. Static-batched renderers retain a conservative native-world-bounds fallback: Unity can replace their shared mesh with a combined mesh spanning other objects in the static batch root's coordinate system.

`SnowVehicleTopologyBounds` keeps the live world envelope separate from the published scheduler box. Exact matrix comparisons and live draw matrices are retained. Whenever a pose changes, the live box is recomputed. The scheduler box changes only when it no longer contains the live geometry plus required shader depth padding; the replacement includes 0.25 metres of movement reserve on each side. Containment, rather than a matrix epsilon, is the safety criterion. Real movement, rotation, scale changes and world-origin shifts therefore update ordering bounds when necessary.

Visible animated or detached geometry still has a pre-submission guard. If it leaves the cached geometry, its source-local mesh bounds can expand the local envelope. The final native visible world AABB is checked in world space and never fed back through an inverse rotation into the local envelope. This prevents the safety path from reintroducing the original inflation. Depth padding retains its conservative high-water buckets. The 0.25-metre reserve is additional to the existing depth padding, not a replacement for it.

No snow quality, limits, accumulation, thermal behaviour, draw-plan LRU policy, sweep algorithm, MicroSplat scanning or shaders are changed.

## New report fields

Both fields report totals for the current 20-second window:

- `snow-topology-window(pose-changes/envelope-expands/padding-grows/bound-publications)` distinguishes exact root-pose changes, growth of the cached local envelope after initialization, increased depth-padding buckets, and publication of a new scheduler world box. Initial publications count; initial pose assignment and initial part inclusion do not count as changes/expands. A publication count is per vehicle event, not a graph rebuild count.
- `snow-overlap-window(actual-overlaps/component-unions)` counts exact AABB intersections and merges of previously separate components. Several true edges can belong to an already joined component, so overlaps can exceed unions. Reusing unchanged topology adds neither.

The existing scheduler field and two-plan cache remain unchanged. Many pose changes with few publications and few component builds is the intended jitter-resistant result. Genuine moving cars can still make the fleet graph rebuild frequently; a stationary-yard expectation is not a guarantee for moving trains.

## Validation

- Release build: 281 tests, 48 localization checks, zero warnings/errors.
- Production performance aggregation: 46 assertions, including new event totals, multiple cameras/registries, window resets and counter overflow. 100,000 warmed measurements allocate no managed memory. Actual scheduler counters pass five additional intersection/union/cache/reset cases; the existing scheduler audit still passes 2,826 checks.
- Unity production registry: 128 cars are rotated 37 degrees before discovery, then receive simulated millimetre position and small angle jitter for 240 frames. After warm-up there are zero component builds and zero new pair tests. Actual matrix uploads continue (23,040 in this fixture); four native-vs-ordered pixel comparisons match exact IDs, slopes and local coordinates.
- Moving one car 3.8 metres through another over 100 steps produces 14 component builds and preserves native pixels at four checkpoints. Detached exclusion motion, changed depth tolerance and world-origin relocation also retain exact pixels.
- The full 128-car yard regression preserves partial overlap ordering, mirrored/skinned/static/custom-stream fallbacks, hidden/destroyed renderers and limits 0/96/64.
- The existing camera and two-plan LRU regression also passes: 240 camera frames produce zero component builds/pair tests; 80 warmed alternating-plan frames produce 80 hits, zero builds and zero matrix uploads.
- An independent eight-corner geometry reference passes 23,494 assertions across 8,693 poses, including mirrored/nonuniform/sheared transforms, animated parts, rotation, reversal, depth-padding growth and world-origin shifts. All 6,000 changing jitter poses produce zero new publications after warm-up. In a synthetic 224-car parallel yard rotated 37 degrees, intersecting AABB pairs are 588 for exact geometry, 796 with the new conservative padding and 2,900 with the old roundtrip. These are geometric pair counts, not gameplay scheduler timings or FPS.

These are simulated transform and rendering regressions, not a live PhysX session or measured game FPS. New gameplay counters are needed to establish the remaining causes and the practical frame-rate improvement.

Evidence: `artifacts/verification/topology-hysteresis-build.log`, `topology-hysteresis-yard.log`, `topology-hysteresis-regression.log`, `topology-hysteresis-camera-lru.log`, `topology-hysteresis-geometry.log`, `snow-performance-topology-audit20260918.json`, `scheduler-counter-audit20260918.log`. Backups and the preceding user log are under `artifacts/backups/topology-hysteresis20260918`.
