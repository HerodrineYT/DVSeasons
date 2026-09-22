# Winter blizzards

Winter can now bring a blizzard lasting 4–36 game hours. The host chooses and saves the timeline. Clients receive the same event, blackout state and announcement timeline; they do not roll their own weather events.

- Cloud cover, opacity, precipitation, wetness and the game's wind control reach their maximum. Rain is rendered as snow through the existing winter system. Blizzard distance fog uses a density of 0.021, 1.75 times the previous 0.012 extinction. The inherited height-fog layer is removed during the storm to prevent excessive fog in low areas.
- Snowfall density is multiplied by 2.75; the existing sheltered-emitter checks still apply. The particle capacity rises only during a blizzard and returns afterwards.
- Outside temperature falls toward −50 °C over nine game minutes. Existing engine/cab heating and Survival consume this same temperature.
- The supplied wind recording loops, fades in/out and becomes quieter during a radio announcement.
- Initial forecasts are randomly scheduled after 8–36 winter game hours, with a further 2–4 hours before the storm starts. Subsequent storms cannot begin until **at least 240 game hours after the preceding storm ended**. Ending an active storm manually also starts this cooldown. Changing season, disabling/re-enabling the feature and saving/loading do not remove it.

The six supplied OGG recordings are used unchanged. Early warning plays 2–4 game hours before the start, the second warning one hour before it, and the ending warning 1–2 hours before the end. Russian uses the RU recording; other game languages use EN. A carried radio is heard locally; a placed radio is heard within 12 metres with distance attenuation. Multiple radios do not multiply playback. Entering range during a broadcast receives the remaining segment instead of restarting it.

Radio announcement volume is a local 0–300% setting, available to both host and clients. The default is 100%, preserving the previous level. It applies immediately to the voice source, including a recording already playing and while the game is paused. Zero silences the voice without restarting or delaying the shared broadcast timeline; muted announcements no longer duck the wind. The preference is saved in the mod settings, not the multiplayer weather state or game save. It does not control native radio beeps or the wind recording. Above 100%, a voice-only `OnAudioFilterRead` gain supplies up to 3x sample amplitude: [Unity's AudioSource volume](https://docs.unity3d.com/2019.4/Documentation/ScriptReference/AudioSource-volume.html) alone is limited to 1. The voice lives on a separate child object so its DSP filter cannot amplify the wind source; the original recordings and seek positions are unchanged.

Broadcasts carry a host timestamp and a short shared lead. Each receiver follows the host's timeline without relying on its PC's wall-clock agreement. Network transit latency can still cause a small audible offset; there is no sample-accurate network audio transport. Late joins do not replay completed recordings. Skipping time past a forecast does not play an obsolete warning.

Power fails 15–120 game minutes after the blizzard starts. This applies to towns **and ordinary stations**: street lighting, building windows and non-office light volumes go dark. Shop scanners and checkout, fuel and maintenance transactions stop. Cash deposits into affected terminals are blocked before money is consumed; cancellation/refund remains available. Pump readiness becomes false, allowing native sound/particle shutdown to finish. Affected terminal displays are blank.

Shop interiors/furniture also switch their baked Bakery lighting to a shared black map, using native map registration and restoration. Ceiling-lamp emission is disabled on local material copies; office materials/maps remain unchanged. The same hooks cover shops streamed in after the outage began.

The shipped shop also contains four realtime point lights (`LightmapBakeType.Realtime`), while `ftLightmapsStorage.bakedLights` is empty. Shop registration now discovers these immediately from the small interior hierarchy, including inactive fixtures, rather than waiting for the incremental world scan. Discovery runs once per loaded interior; registered lights reuse the existing pre-camera enforcement and original-state restoration.

Nearby snowflakes now collide with cached physical walls/roofs regardless of camera direction or renderer visibility, including when window effects are disabled. A bounded local collider volume, reusable spatial grid and cached min/max rejection avoid global collision queries for every distant flake. Newly emitted flakes are checked along their incoming wind path to avoid spawning on the sheltered side of a wall.

Station office interiors keep their lighting. Job/career terminals and vending machines are not blocked. Train and portable lights keep their own power. Ending the event, leaving winter or disabling/unloading the mod restores the weather layer, native day/night lighting and terminal displays.

The mod settings contain a blizzard toggle, a forecast status, **Schedule the next blizzard** and **End blizzard / cancel forecast**. These controls are authoritative on the host. Manual scheduling respects the same ten-day cooldown; a forecast scheduled during cooldown waits until 2–4 hours before its eligible start to announce.

## Verification

- 300 .NET tests, including 500 random timeline fixtures, warning deduplication, sleep/time jumps, invalid packets, ten-day end-based cooldown and serialization.
- Unity 2019 with the installed game's assemblies: weather extrema and restoration of prior manual settings, seasonal override isolation, ordinary-station/office separation, native windows and street sprites, purchase guards, fuel readiness and save round-trip.
- Shop Bakery lightmaps/furniture/emission, an office sharing the original material/map, streaming during an outage, native Start rebinding and two blackout/restoration cycles.
- Realtime shop lamps with an empty baked-light list and the general world scan suspended: the previous installed build fails this regression; the new build passes for existing/streamed interiors, inactive fixtures and late native re-enabling. A deferred GPU fixture measures mean image brightness `0.60249 → 0` when power fails, `0.33055` with an independent flashlight, and `0.60249` after restoration. This verifies the local lighting fix, not a complete live game session.
- An invisible wall behind the camera, camera rotation, crossing/newborn snowflakes, open-air snow, moving obstacles and disabled window effects. A spatial-cache stress fixture matches 2,000 native PhysX raycasts. At 22,500 flakes and 512 nearby colliders it performs 3,895 local checks per update in about 4.04 ms (0.26 ms cache preparation); this is a synthetic CPU measurement, not game FPS.
- All seven supplied audio files decode and seek successfully in Unity, including the streamed 92.16-second wind MP3.
- Existing weather-menu regression: host and two independent client-world fixtures, late join, native save-load restoration and bounded UI refresh queues.

These are automated engine/component checks. A live multiplayer session with two game processes and a VR headset has not been tested here. Existing snow shaders and texture bundles are unchanged.

Runtime packet protocol is now 11. Host and clients must install the same build; older protocol packets are rejected before reading their payload.
