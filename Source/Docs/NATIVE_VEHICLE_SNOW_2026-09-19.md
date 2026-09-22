# Vehicle snow rendering — 2026-09-19

## Implemented paths

1. `SnowVehiclePartCache` stores ordered active-LOD runs and the subset of LOD
   groups that still need the fallback renderer. Native-material parts do not
   enter per-frame bounds/frustum/request loops. Membership is rebuilt only when
   a LOD or parent activation changes; individual enabled/hidden states stay live.
2. `SnowVehicleMeshCache` combines consecutive readable opaque rigid meshes in
   car-local space, separately for each LOD/interior class. Identical model runs
   share one ref-counted GPU mesh across cars. The existing overlap DAG and
   two-plan cache also schedule merged meshes. Repeated copies of a single mesh
   keep their existing instancing path. Cutouts, skinning, animated hierarchies,
   additional vertex streams, negative transforms and unreadable meshes retain
   their supported native/fallback paths. Moving or hidden members invalidate a
   merged run immediately. Original game meshes are never edited or extracted
   into release files.
3. Compatible Standard materials receive snow in their **original deferred
   geometry pass**. The new shader calls Unity 2019's Standard implementation
   before applying the existing roof/heat/wind-side accumulation model. The
   GBuffer normal alpha marks ownership so the world snow pass does not apply
   snow twice. No second snow geometry draw is recorded for those material slots.
   Standard material properties, cutouts, normal/detail/metal maps and original
   forward/shadow/meta passes are retained. Animated emission and unsupported
   material features use the fallback. Coal keeps its separate texture owner.
4. Standard model materials with native GPU instancing remain shared across the
   fleet. One instanced renderer ID selects that car's cached snow/height slice,
   live matrix, heat and wind state from a small structured buffer. Existing
   controller property blocks, multi-material and unsupported sharing cases use
   private compatible variants. Paint/material replacements and property blocks
   survive refresh/restoration; a bounded material poll and original-material CRC
   checks preserve updates without another whole-fleet renderer scan.
5. Distant fallback cars use one instanced depth volume per car from 80 m. It
   reconstructs the visible surface inside the current car box rather than
   re-rendering all parts. Native-material cars use the game's own visual LODs.
   Visible snow fades smoothly from 260 to 300 m; logical accumulation is retained.
   The far volume is an approximation: geometric normals replace detail normals,
   and exposed geometry inside the car envelope can be classified with that car.
   Near/pending/exploded/coal/mirrored/clip-crossing/stereo cases retain geometry.

The fifth requested architecture supersedes the need for a separate persistent
vehicle CommandBuffer on supported materials: material bindings are persistent
and ordinary frames update just the shared car-state buffer. The fallback reuses
prepared part metadata, merged geometry and the existing two-plan LRU batch
structure. The outer world/weather CommandBuffer is still rebuilt when rendered;
freezing dynamic rails, animals, camera targets or other world effects was not
used as a shortcut. No gameplay/snow accumulation/network state was removed.

## Measurements and checks

Unity 2019.4.40f1, D3D11 / RTX 4060. These are synthetic fixture timings, **not game
FPS and not a promised percentage gain in a station**. No headset or multiplayer
session was run.

`fleet-native-final.log`: 128 cars / 1,025 supported material slots, original
Standard instancing enabled. Legacy surface submission + camera + GPU completion
was **3.054 ms**; native shaded snow including live car-buffer upload and material
polling was **0.829 ms**. CPU preparation/recording: **2.358 → 0.147 ms**. Additional
surface requests: **1,025 → 0**. The legacy side deliberately measures surface
geometry without its final world snow shading, so it does not overstate the old
snow shader's contribution.

An intermediate private-material-only implementation regressed on instanced
Standard wagons; it was replaced by the shared-material/instanced-ID path before
release. A separate alternating GPU probe confirmed that retaining an unused
private ID in a restored Standard property block does not disable its instancing.

`fleet-native-final.log` also checks zero-snow pixel equality of all GBuffer
content except the deliberate ownership marker, alpha cutouts, normal/detail/
metal maps, 35-car limit, roof melting, windward/leeward side state, original
material mutation, direct live repaint, external material clones and existing/
live controller property blocks. Both cached material sharing and private
renderer fallbacks are exercised.

`fleet-cache-final.log`: active/inactive LODs, rigid merging, shared geometry,
articulation and disabled members preserve IDs, coverage and R8 slopes. Local
coordinate differences stay within half-float precision (0.002 m tolerance).
For the far fixture: **1,793 per-part requests → 128 volumes in one command**,
with no lost or misassigned vehicle silhouette pixels and no added background.
Do not infer an FPS gain from merging every small run: existing instancing may
already be efficient; uniform repeated-submesh runs are left to that path.

`fleet-de6-final.log`: actual installed DE6 geometry and normal textures, all five
body LODs, both roof slopes, strong paint normals, vehicle rotation and a 17/18 km
origin shift. Native-material binding is asserted; full controller output retains
snow. Extracted game fixtures stay under `artifacts/` and are never packaged.

`fleet-limit-final.log` / `fleet-limit-fallback.log`: native and geometry paths,
1/2/unlimited car budget, static buildings/turntable/animal isolation, asymmetric
accumulated masks, camera relocation, floating origin and newly streamed capped
cars. Native covered/capped cars both need zero extra surface draws.

`fleet-legacy-final.log`: cold and warm mode switches preserve winter road,
concrete and ballast textures. `fleet-scheduler-final.log`: existing exact DAG/
instancing regressions. `fleet-stereo-final.log`: current stereo descriptor,
frustum and pixel-adapter checks. Packed D3D11 mono/stereo/instancing/material
variants are audited, including the new Standard deferred shader; this does not
replace headset validation. Runtime build: 293 tests and 52 localization entries.

Performance reports now include
`snow-native-materials/combined-commands/far-cars/far-commands` plus
`snow-native-materials` CPU time. Native material count is registered slots,
including inactive LODs, **not** additional draws. `snow-vehicle-commands` includes
far volume batches. Compare the same yard/camera/weather/settings before and after
and inspect whole-frame FPS as well as these counters.