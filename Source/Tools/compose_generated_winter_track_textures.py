"""Prepare ImageGen winter track atlases while preserving the game's alpha masks."""

from pathlib import Path
import argparse

from PIL import Image


def compose(generated: Path, original: Path, output: Path, weight: float,
            preserve_alpha: bool = True) -> None:
    with Image.open(original) as source_image, Image.open(generated) as generated_image:
        source = source_image.convert("RGBA")
        edited = generated_image.convert("RGBA").resize(source.size, Image.Resampling.LANCZOS)
        result = Image.blend(source, edited, weight)
        if preserve_alpha:
            result.putalpha(source.getchannel("A"))
        output.parent.mkdir(parents=True, exist_ok=True)
        result.save(output, optimize=True)
        print("prepared {} ({}x{}, ImageGen weight {:.0%})".format(
            output, result.width, result.height, weight))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("generated", type=Path)
    parser.add_argument("original", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--weight", type=float, default=0.88)
    parser.add_argument("--replace-alpha", action="store_true")
    args = parser.parse_args()
    compose(args.generated, args.original, args.output,
            max(0.0, min(1.0, args.weight)), not args.replace_alpha)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
