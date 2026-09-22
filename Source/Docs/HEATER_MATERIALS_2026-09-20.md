# Heater control material ownership

The reported DE6 screenshot shows a magenta heater rocker. The same ownership
bug affects the common generated-control path used by DE2 and DH4. DM3 uses the
same factory with a rotary template. DM1U retains its existing stock controls:
`Dm1uCabControls` does not copy renderers or their materials.

`TryCreateSwitch` copied the loaded interior renderer's `sharedMaterials`.
If native vehicle snow had already bound that renderer, these were temporary
`SnowVehicleNativeMaterials` variants. The newly created control was not yet in
the snow registry's renderer list. A parts refresh or seasonal release restored
the known source renderer and destroyed the variant, leaving the control with
a destroyed material reference. Recreating the cab acquired a fresh material.

Controls now own material copies made once during creation. Copies preserve the
current texture, tint, surface properties and keywords. The known native snow
replacement is converted back to Standard, so an unregistered control does not
depend on seasonal buffers or a stale vehicle ID. Subsequent snow discovery can
bind and release this renderer normally, restoring the owned copy. Destruction
and partial setup failure release only these copies. No frame polling, scene
scans, shader edits, control-input changes or network changes were added.

## Verification

- `Tools/verify_heater_material.ps1` invokes the production switch factory and
  native snow `Bind`/`Release` in Unity 2019.4. It substitutes only the control
  template/input bootstrap and uses small fixture meshes for native-detail
  splitting. It is not a full in-game or VR input test.
- The previous installed binary fails the regression after the native variant
  is destroyed: `DE6 rocker retained a destroyed winter material (pink switch)`.
  Log: `artifacts/verification/heater-material-before.log`.
- The fixed binary passes for DE6, DH4, DE2 and DM3, including twelve further
  snow bind/release cycles per cab, unchanged tint/smoothness, on/off values,
  preservation of stock materials and release of owned copies. The fixture
  invokes the destruction callback explicitly because it runs in edit mode.
  Log: `artifacts/verification/heater-material-fixed.log`.
- Build: zero warnings/errors, 300 .NET tests and 58 translations pass.
  Log: `artifacts/verification/heater-material-build.log`.

The supplied Player.log also contains Multiplayer interior-hook exceptions and
undefined packet warnings. They do not identify the material lifetime failure;
the regression above reproduces it directly without a network session.
Live gameplay confirmation remains necessary.
