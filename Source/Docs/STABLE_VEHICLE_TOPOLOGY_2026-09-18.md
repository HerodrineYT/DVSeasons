# Stable vehicle topology — 2026-09-18

This 0.3.3 follow-up targets camera-driven topology invalidation. Seasonal textures, turntable discovery and snow shaders remain unchanged.

## Stable graph, live rendering

The previous registry passed an AABB built from the current camera's visible renderer subset to the scheduler. LOD, frustum membership and continuously varying depth padding changed this AABB even in a stationary yard. Sweep-and-prune reduced the cost of each rebuild, but did not remove those rebuilds.

Each car now owns a separate local topology envelope. Parts discovery includes every opaque snow or exclusion part across LODs, including detached interior/external/cargo roots. A cached root matrix transforms this envelope into world space only when the pose changes. The absolute matrix/extents formula is equivalent to transforming its eight corners and supports mirrored/nonuniform scale and affine shear.

Rendering still selects parts by the current LOD, camera frustum, layer mask and native visibility. Before scheduling visible draws, their actual bounds must fit the topology envelope. Animated or detached geometry that escapes it expands the local envelope immediately. The shader's depth tolerance remains part of the ordering bounds: padding starts at 0.125 world metres and grows in powers of two when required by the current projection/distance. It never shrinks merely because the camera returns. Parts rediscovery resets the envelope. These conservative bounds can join more vehicles than a tight per-view box, but must never omit a necessary ordering dependency.

All registered, non-destroyed cars enter the scheduler in the same native order. Invisible cars contribute empty request streams. A changing visible subset therefore changes the draw plan without rebuilding component topology. A hidden bridge car may conservatively keep visible cars connected; empty streams retain ordering without rendering.

## Two recent draw plans

The stable-membership managed fixture first demonstrated the remaining repeated-view miss: 40 A/B frames rebuilt 40 plans despite zero component/pair work. The scheduler now keeps two most recently used plans. Consecutive frames retain the existing fast validation path; restoring another plan checks exact stream counts, predecessor chains, renderer/mesh/texture references and draw keys. Hash or Unity instance-ID equality alone cannot authorize reuse. Per-frame matrices and snow IDs always come from current requests.

Each plan is capped at 2,048 streams, 16,384 requests and 8,192 full-snow requests. Larger submissions still render through the uncached working path. Eviction clears obsolete object references; unused batch/matrix arrays are released. Repeated views retain their own material-property blocks, avoiding unnecessary uploads when returning to unchanged cars.

## Terrain spike

The new local Player.log contains a single `terrain-material-discovery=22.55 ms` maximum, with no array build in that window and a Gen0 collection somewhere in the same 20 seconds. This does not establish the exact cause or prove that GC caused that sample. The patch adds aggregate `terrain-scene-roots`, `terrain-node-materials`, and `terrain-node-layers` timings. Scene walks reuse roots, queue and material-ID buffers instead of allocating fresh containers every scan. Each live iterator has exclusive storage; completed/cancelled/unloaded walks clear references before returning storage to a pool capped at four buffers. No discovered materials or scan steps are omitted.

## Verification

- The final build passed 281 unit tests and 48 translation checks with zero warnings/errors.
- A real Unity registry fixture with 128 cars moves the camera for 240 frames and varies frustum, FOV, renderer visibility and LOD selection. After warm-up it performs zero component rebuilds and zero exact pair tests while the visible draw count varies substantially. Six native-vs-ordered GPU comparisons have identical surface IDs, coverage, slopes and captured coordinates.
- Safety cases retain exact native pixels when a detached exclusion mesh escapes the envelope, depth padding grows, a car moves/scales/mirrors, and the world origin shifts.
- The existing 128-car yard fixture passes all partial overlap, moving merge/split, fallback, hidden/destroyed renderer and limit 0/96/64 cases. Draw counts for the connected yard remain 72/132/537 respectively.
- Terrain discovery passes its 3,000/30,000-node and streamed/native-material checks, plus exclusive pooled-buffer ownership, reuse, cancellation, unload and reset. Existing incremental array publication and thaw checks also pass.
- The final Unity fixture alternates two visibility plans for 80 warmed frames: 80 cache hits, zero plan/component builds and zero full matrix uploads. Both variants and later real matrix/snow-ID changes match native pixels exactly. The camera tour also asserts that at least two LOD representations are selected, rather than assuming a camera movement crosses a threshold.
- Managed scheduler audit: 2,826 checks, 1,127,306 requests; exact signature/reference mutations, three-pattern LRU eviction, predecessor changes, empty streams, oversized uncached frames, bounded retained arrays and disposal. Warm stationary and alternating loops allocate zero managed bytes.

An identical managed A/B fixture (220 cars × 8 parts, five trials of 1,000 alternating frames, stable JIT configuration) compares the exact previous scheduler source with the new one. Median scheduler time is 152.14 ms before versus 96.48 ms after. Each old trial rebuilds 1,000 plans and allocates 3.12 MB; each new trial hits 1,000 cached plans and allocates zero managed bytes. Both retain one initial topology build and 2,090 initial pair tests. This measures only the managed scheduler against Unity API doubles, not native rendering or game FPS.

These are regression and synthetic performance checks. They do not measure whole-game FPS or guarantee a particular count in every yard. Real vehicle motion, streamed parts, different projections that require larger padding and genuine fleet membership changes still invalidate topology. A fresh stationary-yard gameplay log is needed for the final comparison.

Evidence: `artifacts/verification/stable-topology-lru-camera.log`, `stable-topology-lru-yard-final.log`, `stable-topology-terrain.log`, `stable-topology-final-build.log`, `scheduler-stable-lru-audit20260918.json`, `scheduler-lru-baseline20260918.json`, and `scheduler-lru-current20260918.json`. The previous runtime, sources and user log are backed up under `artifacts/backups/stable-topology20260918`.
