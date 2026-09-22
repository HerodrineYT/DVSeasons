Optional texture replacements

Standard seasonal textures are prepared in AssetBundles. To replace one, place a PNG in:
  Overrides/Seasonal/winter/WaterIceAlbedo.png
  Overrides/Seasonal/winter/WaterIceNormal.png
  Overrides/Seasonal/winter/Coal_01d.png
  Overrides/Seasonal/winter/WinterBallastBalanced.png
  Overrides/Seasonal/winter_track/early/RailMed_d.png

Use the original asset name and spring/autumn/winter or winter_track/early|middle|late folder.
Restart the world after changing an override. Normal maps use raw linear RGB normals;
albedo textures use sRGB. All original resolutions are supported.
SleeperOld_d aliases SleeperNew_d; AsphaltTiling_01d_White aliases AsphaltTiling_01d;
MB_concrete_01d_blue aliases MB_concrete_01d. Override the canonical file name.

Old Textures/Seasonal files are a compatibility fallback only for missing bundled keys.
Move intentional manual replacements into Overrides/Seasonal to override bundled assets.
