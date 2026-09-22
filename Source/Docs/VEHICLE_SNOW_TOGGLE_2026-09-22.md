# Local rolling-stock snow switch

The UMM settings now include **Отключить снег на вагонах и локомотивах** / **Disable snow on wagons and locomotives**, beside the existing car limit. Snow stays enabled by default, including when loading older settings without the new `VehicleSnowEnabled` field.

The setting applies to native Standard snow materials, fallback surfaces, distant snow surfaces, and tender coal. Native shaders retain their exclusion marker while rejecting snow shading; unsupported materials keep inexpensive exclusion rendering so world snow does not get projected onto a disabled wagon. Moving infrastructure, animals, buildings, terrain and weather retain their existing behavior. Window frost has its own setting.

This is a local visual preference: accumulation, melt history and multiplayer simulation are not reset. Re-enabling restores the current snow state. The nearest-car slider retains its saved value while disabled. Changing the switch cancels a running render benchmark; a vehicle-snow A/B benchmark cannot start while vehicle snow is off.

No shader bundle or multiplayer protocol change is needed. The switch uses existing native per-vehicle state and fallback exclusion paths. It does not promise a particular FPS improvement: exclusion work and snow simulation remain necessary.

Verification: the regular build runs the 315 common/network tests and validates 68 bilingual localization entries. `SnowObjectLimitVerification.Run` and `.RunFallback` in the Unity editor check both snow rendering paths, bare cars while switched off, retained building/turntable snow, animal exclusion, and exact preservation/restoration of a partially cleared vehicle snow mask.
