# Seasonal ballast and sleeper initialization

The supplied local Player.log registers BallastNew_d, BallastMed_d, BallastOld_d,
BallastDirt, DetailGravel_01d, SleeperNew_d and SleeperOld_d, but only BallastLODMed
reports completed winter track stages. The main, winter and track bundles load
successfully. The native close-range ballast/sleeper sources are non-readable,
streamed textures; their seasonal sets must first obtain the source pixels.

`StreamingTextureReadiness.TryAcquire` required both
`IsRequestedMipmapLevelLoaded()` and a sufficiently detailed `loadedMipmapLevel`.
On Unity 2019.4 / D3D11, retaining mip 0 while requesting mip 2 can leave the first
query false indefinitely, although all pixels needed by the readback are resident.
The regression fixture reproduces this with `Texture.streamingTextureForceLoadAll`.
This setting is confined to the test; the mod does not change the game's streaming
policy or force textures to full resolution.

The guard now accepts a known resident mip at least as detailed as the target.
Unknown or coarser levels still wait. Its request remains held through completion
of the asynchronous GPU copy, and the original automatic/explicit mip request is
restored afterward. There is no timer-based low-quality fallback, extra synchronous
readback, replacement artwork, or change to the chunked seasonal blend/cache.
Unity documents `loadedMipmapLevel` as the currently loaded streaming level:
https://docs.unity3d.com/2019.4/Documentation/ScriptReference/Texture2D-loadedMipmapLevel.html

This fixes a reproduced initialization stall consistent with the user's log; the
log itself does not record the game's force-load flag or resident mip values.
An in-game retest is still needed to confirm the user's particular scene.

## Verification

- Normal build: 315 tests pass; 68 English/Russian translations validated.
- Before fix, `StreamingTextureReadinessVerification.VerifyForceResident` times
  out after 300 polls: request 2, loaded 0, loading 0, completion false.
- After fix the same fixture succeeds, including restoration of both automatic
  and pre-existing explicit mip requests. `.VerifyWithStreaming` checks ordinary
  streaming with the requested mip actually loaded.
- `SeasonalTrackStreamingVerification.Run` imports three 2048-square ballast
  sources and two 1024-square sleeper sources as compressed, streamed, non-readable
  textures. A real D3D11 render loop drives the repository and seasonal controller.
  All five bound material outputs complete summer, autumn, early/middle/late winter,
  reverse thaw and procedural/legacy/procedural mode changes. Winter output pixels
  match the actual bundled profiles exactly; snow-free ballast returns to its
  cached source pixels; autumn sleepers change. Vehicle snow is switched off for
  this fixture. Output texture instances survive the mode changes.
- The fixture finishes with no active or synchronous seasonal readbacks.
- `SeasonTextureReadbackVerification.Run` retains scaled sRGB/alpha parity,
  the two-request limit, cancellation safety and zero synchronous fallback.
- `SeasonalStartupVerification.Run` retains road/concrete winter bindings in
  legacy mode and warm caches through mode changes.

Logs: `artifacts/verification/track-streaming-*.log`.
Backup: `artifacts/backups/before-track-streaming-20260922`.
No asset bundle, version (0.3.3), settings format or multiplayer protocol change.
