# Runtime and cabin cache audit

This is the non-render portion of the optimization pass following iteration (9). It targets repeated allocations, native Unity property reads, discovery work and retained object graphs. It does not change thermal equations, gameplay update rates, multiplayer ownership, particle density, window resolution, wiper behavior or the heater controls.

The latest supplied run still puts most winter CPU cost in vehicle culling. These secondary changes are not evidence of a particular FPS gain on low-end hardware.

## Changes

| Files | Removed work | Preserved behavior |
| --- | --- | --- |
| `SeasonModSettings.cs` | `ToSnapshot()` previously constructed another immutable settings object, including enum validation, on every authoritative runtime tick. The six raw constructor inputs now key an instance-local cache. | Any calendar/adhesion input change takes effect on the next call. Existing snapshots remain immutable. Raw invalid inputs still pass through the original constructor normalization. Private cache fields are not serialized. |
| `ColdPowertrainController.cs` | Periodic registration no longer reads simulation components for every unrelated wagon. Only the two previously eligible legacy types enter the component loop. Multiple DE6 engines within a flow reuse its primer lookup during that scan. | All engine Harmony patches still run at their native simulation cadence. BE2 battery registration, DE6 primer behavior and support for custom engines remain. Eligible flows are still inspected periodically, so replacing a component or port cannot be hidden by a permanent flow cache. |
| `WinterWindowController.cs` | Discovery snapshots only locomotive candidates, computes each distance once, and sorts managed values instead of repeatedly reading transforms while sorting the entire yard. Car, root, window and mesh-renderer scratch buffers are reused. A later root already covered by an earlier ancestor is skipped. Per-frame camera/pane positions are read once. | Interior masters retain priority over exterior duplicates; detached external roots remain included. Root membership is rediscovered each cycle, including streamed replacements. The existing discovery interval, pane masks, overlays, thermal integration and wiper paths are unchanged. Scratch references are cleared on completion, iterator disposal and controller disposal. |
| `CabEngineHeating.cs`, `CabHeaterService.cs` | Destroyed Unity `TrainCar` wrappers no longer retain their whole heater binding graph, controls, interiors and simulation references for the session. Cleanup uses reusable scratch storage. | Only destroyed objects are pruned. Their small climate snapshots are retained by GUID for restoration and saving. Late-initialized GUIDs are refreshed from live logic cars, and a replacement captures destroyed predecessors before restoring their history. Only retired custom-car histories are republished; restored native-cab records cannot overwrite newer values written by winter windows. Session reset clears these caches. |

The source backups are in `artifacts/backups/full-opt20260919/DVSeasons.Game`.

## Reviewed and retained

| Area | Reason for retaining the existing implementation |
| --- | --- |
| `SeasonRuntime`, `SeasonGameClock`, season/calendar profiles | Host authority, streamed-world readiness, time-jump handling, randomized transition duration and local-calendar preservation are correctness boundaries. Repeated immutable settings allocation was fixed at the producer; skipping runtime ticks would change weather, input response or simulation. |
| `WeatherAdapter`, `SeasonalClimateController` | Weather-driver probing already stops once bound and is rate limited while unavailable. Harmony lookup/installation is setup work, not repeated per-frame reflection. Live seasonal/atmospheric inputs must remain responsive to the game and weather editor. |
| `WeatherNetworkSync`, `SeasonalWeatherIsolation`, weather ownership profiles | Received state has dirty/change filtering, manual time edits have a revision, and native MP reloads restore host overrides without unnecessary time jumps. Override suspension protects native drying, saves and outgoing snapshots from feeding seasonal modifiers back into themselves. Those ownership/restoration paths are retained. |
| `MultiplayerSeasonBridge`, season/heater packets, `OfflineSeasonBridge` | Normal weather broadcasts are already gated to one second before copying/serializing state; retries are gated to two seconds. Heater changes and ready/disconnect registration are event driven. Reducing synchronization or reusing mutable transmitted snapshots would risk late joins, authority or ordering. Packet validation, protocol and broadcast cadence remain unchanged. |
| `SeasonalThermalController`, thermal/cold powertrain profiles | Harmony patches are installed once. Host-only simulation protection prevents client postfixes from overwriting replicated state. Native heat, starter, battery, brake and firebox time integration is retained. |
| `CabEngineHeating`, `CatenaryCabPower`, `CabOpeningScope` | Engine reflection and electric port/fuse lookup already happen when binding a changed flow. Door/window bindings track streamed roots. Live control values, running state, RPM, cab side and catenary voltage must not become stale. Retained precise electric support and the two-cab opening logic. |
| `CabHeaterSwitchSystem`, `Dm1uCabControls`, heater geometry/lamp helpers | Existing successful bindings are reused. Setup retries are bounded; fan/heater restoration waits for native control initialization and thereafter uses change events. No input, control-state or initialization behavior was altered. |
| `WinterWindowController`, `WindowWinterClimate` | Masks already use bounded updates and pooled per-pane storage. In the supplied log, wiper/mask averages were near zero, with maxima around 0.30/0.33 ms. Thermal inertia, mask decay and exact wiper sweeps were therefore retained rather than replaced by a lower-quality approximation. |
| `Main`, `ModLocalization`, `WeatherEditorHintLayout` | Settings rendering belongs to the open settings UI. Localization startup caches and persistent translation sources already exist. Weather hint reflection metadata is cached and its layout behavior is hover/panel driven; its `LateUpdate` disables itself when detached. Caching displayed content indefinitely would break live language, weather or network-state changes. |
| `WinterRainAudioController` | Rain replacement state and one-time clearing of surface splatters are already separated. Native rain suppression must remain active across weather transitions; removing the hooks could reintroduce rain emitters/audio during snow. |
| `MultiplayerLogSpamFilter` | Installation is setup work. Filtering retains the first useful message and only suppresses repeated known categories. Unrelated warnings/errors and non-unload exceptions remain visible. |
| Save and common value/profile classes | Small save-time documents and immutable/network snapshots are required at persistence/transport boundaries. The changes do not pool an object while another consumer may retain it, and do not change save formats or numerical models. |

## Verification

The combined build logs `artifacts/verification/full-opt-build.log` and `artifacts/verification/full-opt-final-build.log` report **285 passing unit tests**, including four new production-linked tests in `SeasonSettingsCacheTests.cs`:

- 10,000 warmed unchanged snapshot calls allocate zero managed bytes in the .NET test runtime.
- Each of the six input changes invalidates immediately, without mutating earlier snapshots.
- Visual-only changes preserve the cached snapshot and runtime cache fields do not enter saved settings.
- Invalid raw inputs preserve the constructor's existing normalization and can subsequently be corrected.

`DVSeasons.AssetBundleBuild.WinterRuntimeCacheVerification.Run` passed its final **49 checks**, recorded in `artifacts/verification/full-opt-runtime-cache-final.log`: actual Unity/game objects exercise near/far locomotive ordering, nested/master/detached window roots, inactive/freight exclusion, streamed external replacement, canceled-iterator cleanup, destroyed Unity wrappers, retained thermal history and native-cab save isolation.

The final run also validates late GUID assignment, clearing a logic car while preserving its last valid ID, save-time identity refresh and replacement before the periodic cleanup tick. Unit allocation measurements do not constitute a measured Unity/Mono or in-game FPS improvement.

## Reviewed visibility experiment, excluded from release

A read-only review of the experimental `SnowVehicleRegistry.RecordAfterCull` and its `ProceduralSnowController` caller found no confirmed correctness defect in that candidate. It used the world camera's `onPreRender` callback, `Camera.current` and a stereo guard. It cleared temporary context in `finally` and retained renderer enable/force-off state, object activity, layers, manual LOD selection, current bounds, independent frustum tests and shader depth matching. **The shortcut, context flag and runtime counter were subsequently removed from the release implementation because its measurements did not show a benefit.**

Unity 2019.4 documents `Renderer.isVisible` as visibility to any camera, with shadow and editor-camera rendering able to keep it true. Those extra true results can retain unnecessary work. Unity's rendering order places camera culling and visibility callbacks before `OnPreRender`. The inference that this is a conservative rejection for the current mono native pass is also supported by the real callback fixture; it is not a claim that `isVisible` is a camera-specific visibility API. See the official [Renderer.isVisible documentation](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Renderer-isVisible.html) and [rendering execution order](https://docs.unity3d.com/2019.4/Documentation/Manual/ExecutionOrder.html).

The initial fixture passed 507 checks; its expanded final version passed **1,475 checks**, including first-frame spawn, camera teleport/rotation, disable/reactivate, layers, LOD, skinned renderers, auxiliary camera renders and shadow visibility, comparing ID/coverage/slope outputs inside the real callback. However, three timing runs with 582 renderers retained all 582 as native-visible, rejected no candidates and added approximately 1–9% recording overhead. Correctness parity alone did not justify shipping another native getter per part.

The fixture and experimental DLL are retained as audit evidence, not as a released optimization. The test scene contains no baked Umbra occlusion data, so it does not establish yard occlusion performance. The shipping path continues its independent culling checks without this fast rejection.
