"""Prepare the staged DV99 railway textures supplied for DVSeasons.

The source JPEGs are already complete texture edits.  This tool only maps them
to the game's atlas names, restores the vanilla atlas dimensions/alpha channel,
creates the one missing medium-ballast intermediate, and mirrors the resulting
PNGs into the runtime, Unity AssetBundle source, and standalone texture library.
"""

from __future__ import annotations

import argparse
import json
import shutil
from pathlib import Path
from typing import Dict, Iterable, Tuple

from PIL import Image


STAGES = ("early", "middle", "late")

# Some supplied filenames describe when the image was made rather than its
# actual coverage.  The mapping below is deliberately ordered by measured snow
# coverage so the in-game sequence never becomes less snowy while accumulating.
SOURCE_BY_STAGE: Dict[str, Dict[str, str]] = {
    "early": {
        "RailMed_d": "med_ранняя.jpeg",
        "RailOld_d": "old_поздняя рел.jpeg",
        "BallastNew_d": "new_ранняя.jpeg",
        "BallastMed_d": "med_средняя.jpeg",
        "BallastOld_d": "old_поздняя.jpeg",
        "SleeperBoth": "New_and_old ранняя.jpeg",
    },
    "middle": {
        "RailMed_d": "med_поздняя.jpeg",
        "RailOld_d": "old_ранняя.jpeg",
        "BallastNew_d": "new_поздняя.jpeg",
        "BallastOld_d": "old_поздняя 2.jpeg",
        "SleeperBoth": "New_and_old поздняя.jpeg",
    },
    "late": {
        "RailMed_d": "med_средняя рел.jpeg",
        "RailOld_d": "old_средняя.jpeg",
        "BallastNew_d": "new_средняя.jpeg",
        "BallastMed_d": "med_средняя 1.jpeg",
        "BallastOld_d": "old_поздняя 1.jpeg",
        "SleeperBoth": "New_and_old средняя.jpeg",
    },
}

TARGET_ORIGINALS = {
    "RailMed_d": Path("Rails/RailMed_d.png"),
    "RailOld_d": Path("Rails/RailOld_d.png"),
    "BallastNew_d": Path("BallastNew_d.png"),
    "BallastMed_d": Path("BallastMed_d.png"),
    "BallastLODMed": Path("BallastLODMed.png"),
    "BallastOld_d": Path("BallastOld_d.png"),
    "SleeperNew_d": Path("SleeperNew_d.png"),
    "SleeperOld_d": Path("SleeperOld_d.png"),
}


def _load_rgb(path: Path, size: Tuple[int, int]) -> Image.Image:
    with Image.open(path) as source:
        return source.convert("RGB").resize(size, Image.Resampling.LANCZOS)


def _target_size(original: Image.Image) -> Tuple[int, int]:
    longest = max(original.size)
    if longest <= 1024:
        return original.size
    scale = 1024.0 / longest
    return (max(1, round(original.width * scale)),
            max(1, round(original.height * scale)))


def _restore_alpha(edited: Image.Image, original_path: Path) -> Image.Image:
    with Image.open(original_path) as original:
        size = _target_size(original)
        rgba = edited.convert("RGBA").resize(size, Image.Resampling.LANCZOS)
        if "A" in original.getbands():
            alpha = original.getchannel("A").resize(size, Image.Resampling.LANCZOS)
            rgba.putalpha(alpha)
        return rgba


def _save_all(image: Image.Image, relative: Path, roots: Iterable[Path]) -> None:
    for root in roots:
        output = root / relative
        output.parent.mkdir(parents=True, exist_ok=True)
        image.save(output, format="PNG", optimize=True)
        print(output)


def _stage_source(input_root: Path, stage: str, role: str,
                  target_size: Tuple[int, int]) -> Image.Image:
    name = SOURCE_BY_STAGE[stage].get(role)
    if name:
        return _load_rgb(input_root / name, target_size)
    if role == "BallastMed_d" and stage == "middle":
        early = _load_rgb(
            input_root / SOURCE_BY_STAGE["early"][role], target_size)
        late = _load_rgb(
            input_root / SOURCE_BY_STAGE["late"][role], target_size)
        return Image.blend(early, late, 0.50)
    raise KeyError(f"No source mapping for {stage}/{role}")


def prepare(args: argparse.Namespace) -> None:
    input_root = args.input_root.resolve()
    original_root = args.original_root.resolve()
    runtime_seasonal = args.project_root.resolve() / "Resources/Runtime/Textures/Seasonal"
    unity_root = args.project_root.resolve() / "DVSeasons.Unity/Assets/DVSeasons/DV99"
    library_seasonal = args.texture_library.resolve() / "Seasonal"

    required_inputs = {
        value for stage in SOURCE_BY_STAGE.values() for value in stage.values()
    }
    required_inputs.add("осень_вся.jpeg")
    missing = sorted(name for name in required_inputs
                     if not (input_root / name).is_file())
    if missing:
        raise FileNotFoundError("Missing supplied texture(s): " + ", ".join(missing))

    supplied_archive = (args.texture_library.resolve() /
                        "Source/Supplied-Track-0.2.1")
    supplied_archive.mkdir(parents=True, exist_ok=True)
    for name in sorted(required_inputs):
        shutil.copy2(input_root / name, supplied_archive / name)

    for relative in TARGET_ORIGINALS.values():
        if not (original_root / relative).is_file():
            raise FileNotFoundError(original_root / relative)

    stage_roots = (
        runtime_seasonal / "winter_track",
        unity_root / "winter_track",
        library_seasonal / "winter_track",
    )
    generated_manifest = {"stages": {}, "notes": {
        "BallastMed_d/middle": "50% blend of supplied early and late variants",
        "ordering": "mapped by increasing visible snow coverage",
    }}

    for stage in STAGES:
        generated_manifest["stages"][stage] = {}
        for target_name, original_relative in TARGET_ORIGINALS.items():
            original_path = original_root / original_relative
            with Image.open(original_path) as original:
                size = _target_size(original)
            if target_name == "BallastLODMed":
                role = "BallastMed_d"
            elif target_name.startswith("Sleeper"):
                role = "SleeperBoth"
            else:
                role = target_name
            edited = _stage_source(input_root, stage, role, size)
            result = _restore_alpha(edited, original_path)
            relative = Path(stage) / f"{target_name}.png"
            _save_all(result, relative, stage_roots)
            generated_manifest["stages"][stage][target_name] = (
                SOURCE_BY_STAGE[stage].get(role) or
                "generated intermediate from early + late")

    # The regular winter sleeper entries remain as a compatibility fallback and
    # therefore point to the maximum-snow stage as well.
    for sleeper_name in ("SleeperNew_d", "SleeperOld_d"):
        staged = runtime_seasonal / "winter_track/late" / f"{sleeper_name}.png"
        with Image.open(staged) as image:
            fallback = image.copy()
        _save_all(fallback, Path("winter") / f"{sleeper_name}.png", (
            runtime_seasonal,
            unity_root,
            library_seasonal,
        ))

    # The supplied autumn atlas is intended for both old and new sleepers.
    for sleeper_name in ("SleeperNew_d", "SleeperOld_d"):
        original_path = original_root / TARGET_ORIGINALS[sleeper_name]
        with Image.open(original_path) as original:
            size = _target_size(original)
        autumn = _load_rgb(input_root / "осень_вся.jpeg", size)
        result = _restore_alpha(autumn, original_path)
        _save_all(result, Path("autumn") / f"{sleeper_name}.png", (
            runtime_seasonal,
            unity_root,
            library_seasonal,
        ))

    manifest_path = args.texture_library.resolve() / "Seasonal/winter_track/manifest.json"
    manifest_path.write_text(json.dumps(generated_manifest, ensure_ascii=False, indent=2),
                             encoding="utf-8")
    print(manifest_path)
    print(supplied_archive)


def main() -> int:
    parser = argparse.ArgumentParser()
    script_root = Path(__file__).resolve().parents[1]
    parser.add_argument("--project-root", type=Path, default=script_root)
    parser.add_argument("--input-root", type=Path,
                        default=Path.home() / "Downloads")
    parser.add_argument("--texture-library", type=Path,
                        default=script_root.parent / "DVSeasons-Textures")
    parser.add_argument("--original-root", type=Path,
                        default=script_root.parent /
                        "DVSeasons-Textures/Originals/Vanilla-DV99")
    prepare(parser.parse_args())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
