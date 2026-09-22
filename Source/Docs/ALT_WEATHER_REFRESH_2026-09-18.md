# Client Alt freeze: weather panel refresh feedback

Date: 2026-09-18. Audited game build: Derail Valley 99.7, Multiplayer 0.1.16.

## Finding

DVSeasons' weather authority protection could make the native weather panel repeatedly try to clear overrides that the same protection refused to clear. With two persistent overrides, each refresh scheduled two more refresh coroutines. This is a concrete feedback defect in the interaction between `SeasonalWeatherIsolation` and the shipped `PhotoModeWeatherController`.

The native controller was read directly from the installed `DerailValley_Data/Managed/DV.UI.dll`. No game or Multiplayer assembly was edited during this audit.

## Before the fix

1. On a multiplayer client, `SeasonalWeatherIsolation.SliderInteractable` forced the native provider's `IsSliderInteractable` result to `false`.
2. `PhotoModeWeatherController.UpdateWeatherValues` copied `Provider.IsWeatherOverridden(type)` into each `WeatherSlider.isOverrideOn`, then called `UpdateInteractable`.
3. Native `UpdateInteractable` called `ResetDefaultSettings` for every slider that was both non-interactable and overridden.
4. `ResetDefaultSettings` temporarily cleared the slider's local `isOverrideOn`, called `Provider.ClearWeatherOverride`, and started `DelayedRefresh`.
5. DVSeasons' `ManualClear` prefix rejected the client's clear request. The actual weather override therefore remained set.
6. `DelayedRefresh` yielded one frame, then called `UpdateWeatherValues`, rediscovering the same overrides and repeating the process.

The reset path requires the controller's `isOn` flag. One persistent override sustains an endless refresh chain; two or more can multiply the number of queued refreshes on successive frames. For two unchanged overrides, the idealized call counts are `1, 2, 4, 8, ...` until another condition interrupts the chain or frame processing becomes overwhelmed.

Winter supplies two such overrides without manual weather editing:

- `WeatherAdapter.ApplyWinterAdhesion` engages `WeatherDriver.WetnessValue`.
- `WeatherAdapter.ApplyWinterThunderSuppression` engages `WeatherDriver.ThunderValue`.
- Both settings default to enabled, and `SeasonRuntime.ApplyOwnedWeatherOverrides` maintains them.

`WeatherSlider.Value` uses `SetValueWithoutNotify`. The identified growth is caused by the native reset/refresh path, not by an ordinary slider value-change event.

## Why pressing Alt can trigger it with the weather menu closed

`PhotoModeWeatherSettingsProvider` subscribes to `ScreenspaceMouse.ValueChanged`. Its `ScreenspaceChanged` method calls `RefreshControllerState`, which invokes:

```text
controller.ToggleOn(IsWeatherEditorActive())
controller.ToggleInteractable(IsWeatherEditorInteractable())
```

The first flag is based on the session's weather-editor permission and photo-mode state. It is independent of whether the user has opened the weather panel. The second flag includes mouse mode.

For an allowed editor, Alt makes `PhotoModeWeatherController.ToggleInteractable` call `panel.SetVisible(true)`, even when `panelOpen` is false. Native `HUDPanel.SetVisible` activates its GameObject. That activation invokes the weather controller's `OnEnable`, which calls `UpdateWeatherValues`.

This relationship was checked in the shipped `DerailValley_Data/level4` asset with UnityPy, using read-only inspection:

| Asset object | Path ID | Serialized location |
| --- | ---: | --- |
| PhotoModeWeatherController | 10301 | GameObject 1886, `PhotoModeWeatherSettings` |
| HUDPanel | 7841 | The same GameObject 1886 |
| PhotoModeWeatherSettingsProvider | 8714 | GameObject 2545, `HUD scripts` |

The panel/controller hierarchy is `HUDManager/ControlsGroup/PhotoModeWeatherSettings`. The provider is on a separate object and can receive the mouse-mode event while the panel object is inactive. After mouse mode closes, `HUDPanel.DoUpdate` eventually deactivates a panel that is neither open nor visible.

Thus a manually closed weather menu does not exclude this trigger. The permission condition still matters: the feedback requires the weather controller to be on. The supplied logs do not serialize that private runtime flag, so the logs alone do not establish it for the exact frozen frame.

## User observations and log limits

The user reported freezes after pressing Alt, with the player list appearing first; disabling the Multiplayer player list did not fix the freeze. They also reported reproduction by three clients after installing the mod, including while outdoors looking at the sky. These observations do not require hovering a cab control or rendering the player list.

`D:/Player (2).log` is a client session: it records client creation, successful world synchronization, and later client shutdown. It contains frames of approximately 15–16 seconds, followed by large gaps in Multiplayer snapshots. Those records show stalled progress but do not identify a blocking method.

`D:/Player (3).log` records the client's winter state and active seasonal adhesion. It ends after an adhesion message. Its last performance summary includes loading-period work and a maximum frame of about 6 seconds; there is no Alt marker, managed stack trace at the freeze, or completed post-freeze measurement. The small reported DVSeasons CPU scopes and zero synchronous seasonal texture readbacks do not measure the native weather-panel coroutine workload or GPU completion.

The Windows bare-Alt guard was a previous unsuccessful hypothesis and has been rolled back. It is not the basis of this finding. The Multiplayer log filter only intercepts selected Multiplayer warning/error categories; static inspection found no cursor/UI callback, waiting, or recursive logging in that filter.

## Production change reviewed

The fix is in `DVSeasons.Game/SeasonalWeatherIsolation.cs`:

- Intercept native `PhotoModeWeatherController.UpdateInteractable` for a client panel and disable its sliders/reset buttons directly, without executing the native override-reset routine.
- Reject `ResetDefaultSettings` and `SliderChanged` for the same client panel, before they can mutate panel state or queue a delayed refresh.
- Reapply the disabled controls after `ToggleOn`, `OnEnable`, and `NotifyOverrideChanged`, because those native callbacks can enable reset buttons.
- Preserve the existing provider-side restrictions on clearing or setting authoritative weather and time.

`IsClientPanel` requires both a non-authoritative active adapter and a `PhotoModeWeatherSettingsProvider` instance. Host/offline execution, a missing active adapter, and unrelated implementations of `APhotoModeWeatherProvider` pass through the new controller patches. Subclasses of the native provider are included deliberately. The fix preserves weather values and override flags; it changes how client controls are disabled.

Independent static review found no additional actionable defect in these changes. Production files were not edited by the reviewer.

## Validation status

- Production build: no warnings or errors. All 262 existing tests and 48 localization checks passed.
- Real-controller Unity regression using the shipped `DV.UI.dll`: **passed**. The pre-fix DLL fails the same regression with pending refresh counts `2 -> 4 -> 8` and 14 scheduled refreshes after the initial update plus two coroutine generations. The fixed DLL produces `0 -> 0 -> 0`, with no reset or refresh requests.
- Host controls still edit and reset native weather, producing one finite delayed refresh per action. Two independent client worlds preserve all nine synchronized overrides through native weather packets, reject local edits, and complete 12 refresh/reopening cycles each. Six additional cycles per client preserve seasonal wetness and thunder after the host clears manual overrides.
- Multiplayer client session after installing the final fixed package: **pending**.

The regression uses the actual controller, provider, weather slots, sliders, buttons and `DelayedRefresh` iterator from DV99 in Unity Mono. Only coroutine scheduling is captured for the fixture controller and advanced in bounded generations, so reproducing the exponential growth cannot hang the test process. It invokes native activation callbacks directly rather than driving a full game UI or injecting Alt. The unrelated-provider restriction was statically reviewed, not exercised by the runtime fixture.

Reproducible runner: `Tools/verify_weather_network.ps1`, with `-DVInstallDir`, `-UnityEditor`, optional `-ModDirectory` and `-LogName`. The preserved pre-fix DLL is `artifacts/backups/alt-weather-refresh-20260918/DVSeasons.dll`. Logs: `artifacts/verification/alt-weather-baseline-unity.log` (expected failure) and `artifacts/verification/alt-weather-fixed-unity.log` (pass).

Fixed DLL SHA256: `7AB0A97F3CF647376E9B9C536904C33B427F18E23A3B0E86400816E6BD35D01A`. Version remains 0.3.3. No Windows Alt hook or diagnostic watchdog was added.

No full live multiplayer reproduction is claimed by this audit. The static call graph and serialized activation relationship establish the defect; runtime regression and client confirmation establish the practical result of the fix.
