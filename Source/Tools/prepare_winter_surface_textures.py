"""Prepare authored winter road textures and a seamless ice albedo.

The six user-supplied images are resized to the exact dimensions of DV99's
original textures.  Their original alpha channels are retained so atlas cutouts
and material coverage remain identical to the base game.  The generated ice
reference is mirrored into a periodic 2x2 tile, guaranteeing matching opposite
edges without painting over the generated detail.
"""

from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageOps


SURFACES = {
    "SidewalkTiles_01d": "SidewalkTiles_01d snow.jpeg",
    "AsphaltRoad_01d": "AsphaltRoad_01d snow.png",
    "Sidewalk_01d": "Sidewalk_01d snow.jpeg",
    "Roads_LOD_01d": "Roads_LOD_01d snow.jpeg",
    "RoadDetail": "RoadDetail snow.jpeg",
    "AsphaltTiling_01d": "AsphaltTiling_01d snow.jpeg",
}


def fitted(image: Image.Image, size: tuple[int, int]) -> Image.Image:
    return ImageOps.fit(image.convert("RGB"), size, Image.Resampling.LANCZOS,
                        centering=(0.5, 0.5))


def prepare_surface(source: Path, original: Path, destination: Path) -> None:
    with Image.open(source) as winter_source, Image.open(original) as base_source:
        base = base_source.convert("RGBA")
        winter = fitted(winter_source, base.size).convert("RGBA")
        # The game's atlas opacity is authoritative.  In particular Roads_LOD
        # must retain its transparent UV islands instead of the JPEG's black fill.
        winter.putalpha(base.getchannel("A"))
        destination.parent.mkdir(parents=True, exist_ok=True)
        winter.save(destination, "PNG", optimize=True)


def prepare_periodic_ice(source: Path, destination: Path) -> None:
    with Image.open(source) as generated:
        tile = fitted(generated, (512, 512))
    periodic = Image.new("RGB", (1024, 1024))
    periodic.paste(tile, (0, 0))
    periodic.paste(ImageOps.mirror(tile), (512, 0))
    periodic.paste(ImageOps.flip(tile), (0, 512))
    periodic.paste(ImageOps.flip(ImageOps.mirror(tile)), (512, 512))
    destination.parent.mkdir(parents=True, exist_ok=True)
    periodic.save(destination, "PNG", optimize=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--downloads", type=Path, required=True)
    parser.add_argument("--originals", type=Path, required=True)
    parser.add_argument("--generated-ice", type=Path, required=True)
    parser.add_argument("--runtime-winter", type=Path, required=True)
    parser.add_argument("--unity-winter", type=Path, required=True)
    args = parser.parse_args()

    for texture_name, supplied_name in SURFACES.items():
        source = args.downloads / supplied_name
        original = args.originals / f"{texture_name}.png"
        if not source.is_file():
            raise FileNotFoundError(source)
        if not original.is_file():
            raise FileNotFoundError(original)
        runtime_output = args.runtime_winter / f"{texture_name}.png"
        unity_output = args.unity_winter / f"{texture_name}.png"
        prepare_surface(source, original, runtime_output)
        prepare_surface(source, original, unity_output)
        print(runtime_output)
        print(unity_output)

    if not args.generated_ice.is_file():
        raise FileNotFoundError(args.generated_ice)
    for folder in (args.runtime_winter, args.unity_winter):
        output = folder / "WaterIceAlbedo.png"
        prepare_periodic_ice(args.generated_ice, output)
        print(output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
