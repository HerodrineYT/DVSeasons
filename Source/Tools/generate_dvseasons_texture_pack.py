#!/usr/bin/env python3
"""Build fixed seasonal PNG replacements from extracted Derail Valley albedo atlases.

The script is a development tool. It preserves the source UV layout and alpha channel,
but writes new RGB/alpha content for spring, autumn, and winter texture packs.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image


TERRAIN_NAMES = [f"TerrainTexture{i}" for i in range(1, 17)]
VEGETATION_NAMES = [
    "Elephant_Grass_Mobile_Cluster_2_Atlas",
    "GrassForest01",
    "GrassSoft03",
    "T_Fir_bark_01_BC",
    "T_Fir_bark_02_BC",
    "T_Fir_leaves_BC_T",
    "T_Poplar_leaves_BC",
    "T_Poplar_trunk_04_BC",
    "T_Willow_Bush_04_Cross_A_T",
    "T_beech_bark_01_BC",
    "T_beech_tree_05_BC_A",
    "T_fern_beech_forest_01_BC_M",
    "T_maple_bush_BC",
    "T_plant_beech_forest_03_BC_M",
]


def periodic_noise(height: int, width: int, seed: int) -> np.ndarray:
    y, x = np.mgrid[0:height, 0:width]
    u = x / max(1, width)
    v = y / max(1, height)
    phase = (seed % 997) / 997.0 * math.tau
    noise = (
        np.sin(math.tau * (3 * u + 2 * v) + phase)
        + 0.55 * np.sin(math.tau * (7 * u - 5 * v) + phase * 1.7)
        + 0.28 * np.cos(math.tau * (13 * u + 11 * v) - phase * 0.6)
        + 0.16 * np.sin(math.tau * (29 * u - 23 * v) + phase * 2.2)
    )
    noise -= noise.min()
    return noise / max(1e-6, noise.max())


def mix(source: np.ndarray, target: np.ndarray, amount: np.ndarray | float) -> np.ndarray:
    if not isinstance(amount, np.ndarray):
        amount = np.full(source.shape[:2], amount, dtype=np.float32)
    return source * (1.0 - amount[..., None]) + target * amount[..., None]


def terrain_variant(rgba: np.ndarray, season: str, seed: int) -> np.ndarray:
    rgb = rgba[..., :3].copy()
    alpha = rgba[..., 3:4]
    height, width = rgb.shape[:2]
    noise = periodic_noise(height, width, seed)
    detail = periodic_noise(height, width, seed + 113)
    luminance = rgb[..., 0] * 0.2126 + rgb[..., 1] * 0.7152 + rgb[..., 2] * 0.0722

    if season == "spring":
        damp = np.array([0.12, 0.16, 0.10], dtype=np.float32)
        moss = np.array([0.23, 0.38, 0.12], dtype=np.float32)
        rgb = mix(rgb, damp, 0.12 + detail * 0.10)
        moss_mask = np.clip((noise - 0.40) * 1.15, 0.0, 0.34)
        rgb = mix(rgb, moss, moss_mask)
    elif season == "autumn":
        earth = np.array([0.31, 0.18, 0.075], dtype=np.float32)
        ochre = np.array([0.66, 0.25, 0.035], dtype=np.float32)
        rgb = mix(rgb, earth, 0.20 + detail * 0.10)
        leaf_mask = ((noise > 0.73) & (detail > 0.48)).astype(np.float32) * (0.38 + detail * 0.36)
        rgb = mix(rgb, ochre, leaf_mask)
    elif season == "winter":
        frozen = np.stack([luminance * 0.68, luminance * 0.75, luminance * 0.84], axis=-1)
        rgb = mix(rgb, frozen, 0.38)
        snow = np.stack([0.86 + detail * 0.10, 0.90 + detail * 0.08, 0.95 + detail * 0.045], axis=-1)
        snow_mask = np.clip(0.34 + luminance * 0.46 + (noise - 0.50) * 0.42, 0.05, 0.94)
        rgb = mix(rgb, snow, snow_mask)
    return np.concatenate([np.clip(rgb, 0, 1), alpha], axis=-1)


def vegetation_variant(rgba: np.ndarray, season: str, name: str, seed: int) -> np.ndarray:
    rgb = rgba[..., :3].copy()
    alpha = rgba[..., 3].copy()
    height, width = rgb.shape[:2]
    noise = periodic_noise(height, width, seed)
    fine = periodic_noise(height, width, seed + 211)
    maximum_other = np.maximum(rgb[..., 0], rgb[..., 2])
    green = np.clip((rgb[..., 1] - maximum_other * 0.78) * 3.2, 0.0, 1.0)
    luminance = rgb[..., 0] * 0.2126 + rgb[..., 1] * 0.7152 + rgb[..., 2] * 0.0722
    visible = (alpha > 0.03).astype(np.float32)
    lower_name = name.lower()
    is_bark = any(term in lower_name for term in ("bark", "trunk"))
    is_conifer = "fir_" in lower_name or "fir " in lower_name
    is_grass = "grass" in lower_name or "fern" in lower_name or "plant" in lower_name

    if season == "spring":
        young = np.stack([0.14 + fine * 0.08, 0.48 + fine * 0.28, 0.08 + fine * 0.06], axis=-1)
        amount = green * (0.38 if is_bark else 0.72) * visible
        rgb = mix(rgb, young, amount)
        bud_mask = ((noise > 0.86) & (green > 0.22)).astype(np.float32) * 0.45
        rgb = mix(rgb, np.array([0.72, 0.82, 0.28], dtype=np.float32), bud_mask)
    elif season == "autumn":
        yellow = np.stack([0.72 + fine * 0.20, 0.30 + fine * 0.30, 0.025 + fine * 0.04], axis=-1)
        red = np.stack([0.52 + fine * 0.26, 0.075 + fine * 0.12, 0.018 + fine * 0.025], axis=-1)
        autumn = mix(yellow, red, np.clip((noise - 0.48) * 2.3, 0, 1))
        amount = green * (0.20 if is_conifer else 0.90) * (0.08 if is_bark else 1.0) * visible
        rgb = mix(rgb, autumn, amount)
        rgb = mix(rgb, np.array([0.25, 0.14, 0.055], dtype=np.float32), 0.09 * visible)
    elif season == "winter":
        cold = np.stack([luminance * 0.66, luminance * 0.72, luminance * 0.80], axis=-1)
        rgb = mix(rgb, cold, 0.36 * visible)
        if not is_conifer and not is_bark:
            keep_threshold = 0.76 if not is_grass else 0.52
            falling = (green > 0.18) & (noise < keep_threshold)
            alpha[falling] *= 0.08 if not is_grass else 0.34
            dry = np.array([0.28, 0.20, 0.095], dtype=np.float32)
            rgb = mix(rgb, dry, green * 0.68)
        snow = np.stack([0.88 + fine * 0.08, 0.92 + fine * 0.06, 0.97 + fine * 0.025], axis=-1)
        snow_base = 0.20 if is_bark else (0.62 if is_conifer else 0.38)
        snow_mask = np.clip((noise - 0.42) * 1.4 + luminance * 0.18, 0, 1) * snow_base * visible
        rgb = mix(rgb, snow, snow_mask)

    return np.dstack([np.clip(rgb, 0, 1), np.clip(alpha, 0, 1)])


def load_rgba(path: Path, max_edge: int) -> np.ndarray:
    image = Image.open(path).convert("RGBA")
    if max(image.size) > max_edge:
        scale = max_edge / max(image.size)
        image = image.resize((max(16, round(image.width * scale)), max(16, round(image.height * scale))), Image.Resampling.LANCZOS)
    return np.asarray(image, dtype=np.float32) / 255.0


def save_rgba(array: np.ndarray, path: Path) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    image = Image.fromarray(np.round(np.clip(array, 0, 1) * 255).astype(np.uint8), "RGBA")
    image.save(path, optimize=True, compress_level=9)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-dir", type=Path, required=True)
    parser.add_argument("--out-dir", type=Path, required=True)
    args = parser.parse_args()

    entries = []
    for name in TERRAIN_NAMES + VEGETATION_NAMES:
        source = args.source_dir / f"{name}.png"
        if not source.exists():
            raise FileNotFoundError(source)
        category = "terrain" if name in TERRAIN_NAMES else "vegetation"
        maximum = 512 if category == "terrain" else 1024
        rgba = load_rgba(source, maximum)
        for season in ("spring", "autumn", "winter"):
            seed = int(hashlib.sha256(f"{name}:{season}".encode()).hexdigest()[:8], 16)
            output = terrain_variant(rgba, season, seed) if category == "terrain" else vegetation_variant(rgba, season, name, seed)
            relative = Path(season) / f"{name}.png"
            destination = args.out_dir / relative
            save_rgba(output, destination)
            entries.append({
                "sourceTexture": name,
                "category": category,
                "season": season,
                "file": relative.as_posix(),
                "sha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
            })

    manifest = {
        "id": "DV99FixedSeasonalTextures",
        "version": 1,
        "gameBuild": 99,
        "summerUsesOriginalTexture": True,
        "entries": entries,
    }
    (args.out_dir / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
