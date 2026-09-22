# Cold start settings and tutorial hints

The cold-start iteration started from the explicitly selected `artifacts/releases/packages/DVSeasons-0.3.3-Nexus.zip` (SHA-256 `180d1ff9566e6ea19c4971e5daba4283e8004fc213e7a7e8b4e843f0955abdd6`). Its matching GitHub source archive was verified to contain the identical runtime. That iteration applied only cold-start settings and hints. The later FPS optimization is documented separately in [NATIVE_SNOW_SHARING_2026-09-21.md](NATIVE_SNOW_SHARING_2026-09-21.md).

Compared with that Nexus archive, only the three DVSeasons DLLs and `Localization/DVSeasons.csv` change. All 18 other runtime files, including all three AssetBundles, remain byte-identical. The original archive is preserved.

Two independent UMM options are available next to the cabin heating settings:

- **Холод не влияет на запуск ванильных локомотивов** / **Cold does not affect vanilla locomotive starts**. Off by default. Restores native diesel starter timing and removes the cold DE6 primer requirement. The host owns this rule in multiplayer; clients see its current value but cannot change it. Custom locomotive starters, fuel/fuses, working temperatures, cooling, overheating and battery physics retain their existing behavior.
- **Показывать подсказки холодного запуска и подсоса DE6**. On by default and local to each player. Shows remaining starter hold time in the game's tutorial notification area. The DE6 reminder appears during cranking, then displays the remaining continuous hold after ignition. As of 2026-09-22, open the engine-room primer **or set the cab throttle to maximum within five seconds**, then hold either condition continuously for three seconds. The actual cab input (`throttle.EXT_IN`) is cached alongside the primer input; the engine governor output is not used as a substitute for the handle position. Turning hints off does not change the mechanics.

The countdown uses native engine state and the existing cold-start model. Releasing the starter resets the attempt. A warm start requires no cold-start reminder. If the engine has not ignited when the predicted time reaches zero, the message asks the player to keep holding the starter rather than claiming success.

MP protocol **13** carries the authoritative rule and active per-car starter/primer states with the existing one-second season broadcast. Empty snapshots clear old hints. A client predicts only starter time; it never assumes the primer is held. Hints expire after three seconds without an update and are cleared on session reset. Install this same build on the host and all clients; protocol 12 is rejected before decoding. No save migration is needed. Release version remains 0.3.3.

The notification updates its existing text object and does not clear another tutorial. Engine-to-car ownership and simulation flows are cached; no renderer discovery was added.

## Verification

- 315 xUnit tests pass, including packet truncation/validation, host plus two clients, late join, snapshot cloning, explicit empty snapshots, local hint settings and the five-second DE6 response boundary.
- English/Russian localization validation: 66 entries and matching placeholders.
- Unity 2019.4.40f1 Play Mode fixture against installed DV99 simulation assemblies: three native diesel starter implementations, interrupted cold starts, warm restarts, vanilla bypass versus custom engines and existing battery/lamp behavior. DE6 cases cover waiting 4.5 seconds before either control, a full throttle preset before ignition, rejection of partial throttle, interruption, and continuous handover between primer and cab throttle during the three-second hold.
- Native `NotificationManager` fixture: countdown rounding and in-place text updates, starter-to-primer transition, independent tutorial preservation, disable and cleanup. This uses a minimal notification prefab; in-headset placement and a live multiplayer drive still require in-game verification.

Run after building:

```powershell
.\Tools\verify_cold_starts.ps1 -DVInstallDir "F:\steam\steamapps\common\Derail Valley" -UnityEditor "D:\UnityHub\Editor\2019.4.40f1\Editor\Unity.exe"
```

Logs: `artifacts/verification/cold-start-hints-build.log` and `cold-start-hints-runtime.log`.
Startup marker: `Cold-start hints build 2026-09-21`.
