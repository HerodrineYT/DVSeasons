# Dynamic Seasons for Derail Valley

`DVSeasons` is a separate Unity Mod Manager package. It does not patch jobs, tracks, junctions, cars, or saves owned by other mods.

## Runtime design

- The authoritative season phase is a number in `[0, 4)`: spring, summer, autumn, winter.
- Progress is driven by the game's `WeatherPresetManager.DateTime`; the real-time fallback is only used before the weather system exists.
- During the final configurable days of a season, visual and physical profiles use smoothstep interpolation into the next season.
- Winter precipitation reads the native `WeatherDriver.RainValue`, fades rain particle emission, and drives a separate snow particle system.
- Terrain materials are cloned per loaded terrain and only known snow properties (`_Snow_Amount`, `_SnowAmount`, `_SnowBlendFactor`, `_UseSnow`) are changed. Original materials and grass tints are restored when the mod stops.
- Derail Valley build 99 terrain layers and vegetation materials are matched by their original albedo texture names. Spring, autumn, and winter use 123 `Texture2D` assets and three terrain `Texture2DArray` assets from the Windows Unity AssetBundle `AssetBundles/dvseasons_dv99`, built with Unity 2019.4.40f1; summer uses the original game textures. Runtime code loads and smoothly blends the supported fixed atlases. Unknown custom-map textures are left unchanged instead of being procedurally recolored.
- Winter adhesion uses `WeatherDriver.WetnessValue`. Derail Valley build 99 maps wetness `0..0.5` to up to 50% native friction reduction, so the GUI deliberately clamps the equivalent wetness to `0.5`.

## Multiplayer

`DVSeasons.Multiplayer.dll` is loaded only when `MultiplayerAPI` is present. The host owns the phase and broadcasts a versioned state packet every five seconds and after manual changes. Clients request a state on connect; `OnPlayerReady` supplies late-join snapshots. Clients never advance or author the cycle.

## Compatibility

- **Passenger Jobs:** no Passenger Jobs objects are read or changed.
- **DoubleTrack:** no topology is cached. Terrain streaming is rescanned, so terrain loaded or changed after DoubleTrack remains eligible for seasonal visuals.
- **Custom cars and other mods:** snow and adhesion use game-global weather plus terrain shader capabilities. The mod does not rewrite car prefabs or cargo/job data.
- **External weather overrides:** `RespectExternalWetnessOverride` avoids taking ownership if another system already overrides wetness when DVSeasons first attaches.

## Known limits of the first playable build

- Snow coverage depends on whether a terrain material exposes one of the known built-in snow properties. Unsupported custom-map shaders still receive falling snow and foliage tint, but not guaranteed ground accumulation.
- Automatic vegetation discovery uses material, shader, and texture names. A custom shader or texture outside the DV99 fixed pack needs an explicit compatibility texture and rule.
- Texture arrays and procedural shaders without a `Texture2D` albedo source are deliberately skipped. Original material properties and `TerrainLayer.diffuseTexture` references are restored only when they are still owned by DVSeasons, so later changes made by another mod are not overwritten during shutdown.
- Rain-to-snow detection is name-based for rain particle systems. It is reversible and does not alter the native rain value, wetness simulation, sounds, or forecasts.
- A full in-game smoke test is still required for each custom map and shader pack.
