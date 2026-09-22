# Host-authoritative cabin climate and snow melt

The season packet now uses protocol **12**. Update the host and every client together;
older protocols are rejected before their payload is read. Mod metadata remains 0.3.3.

Already replicated: ambient temperature, seasonal amount, accumulated surface cover,
wind/precipitation and the four persistent vehicle side-snow amounts. Added per locomotive,
keyed by CarGUID: initialized climate, engine warmth, heater/air/glass temperatures,
frost, fog and engine-driven melted snow fraction. Native engine/oil/brake simulation
remains owned by Multiplayer's existing host replication; the client seasonal physics
hooks continue to avoid writing over it.

The host updates cabin climate independently of camera and window-effect settings for
all loaded locomotives at 2 Hz. The existing cached component bindings are reused.
Snow simulation also runs independently of the host's procedural-snow render switch in
multiplayer. Clients consume the host climate without advancing their own heater, frost
or melt model. The custom-locomotive temperature API (also used by Survival) returns the
same confirmed state. The host remains the source of custom-car engine/electric heating.

Thermal snapshots share the existing once-per-second fleet cadence, capped at 1024
locomotives and 80 ASCII characters per ID. Each entry is value-only (roughly 68 bytes
for a 36-character GUID and climate); no textures, readbacks or per-frame network sends.
The bridge retains the last fleet snapshot across weather-only edits for late joins.
An omitted snapshot keeps history, an explicit empty snapshot clears it. Session reset
clears caches. Loaded/replacement cabs reuse the same GUID-keyed confirmed climate;
already bound panes observe updates through their existing climate reference.

Snowflake positions, wiper paths and per-pixel window impact masks remain local visual
simulation; this update synchronizes thermal/coverage state, not an identical particle
or window-mask texture across GPUs. Local visual quality settings remain local.

## Verification

- Build: zero warnings/errors; 309 xUnit tests; 59 translations validated.
- Tests exercise production packet serialization and MP bridge with host, two clients,
  late join, weather-only updates, retained snapshot ownership, empty reset, runtime
  loading order, once-per-second scheduling and malformed/truncated packet bounds.
- Unity 2019.4.40f1 fixture with real game TrainCar and production controllers:
  host warms an off-camera cab without window rendering; 300 client updates with different
  local temperature/heater inputs do not advance confirmed temperatures or melt; new
  snapshots update existing pane references; a streamed replacement receives the same
  state; explicit empty and session cleanup remove it.
- Logs: `artifacts/verification/thermal-sync-build.log` and
  `artifacts/verification/thermal-sync-runtime.log`.

These are automated transport and Unity runtime checks, not a live two-computer session.
For the in-game check, occupy the same locomotive, switch its heater, compare temperature
and glass frost, then leave/re-enter and reconnect while snow is partially melted.
