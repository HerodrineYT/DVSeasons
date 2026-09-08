#!/usr/bin/env python3
"""Generate the seamless linear normal map used by winter lake ice.

The output is deliberately deterministic and uses only Python's standard
library so release builds do not depend on Pillow.  The same PNG is written to
the Unity authoring project and to the loose runtime override consumed by the
currently shipped AssetBundle.
"""

from __future__ import annotations

import math
import struct
import zlib
from pathlib import Path


SIZE = 512
TAU = math.tau


def hash01(x: int, y: int, salt: int) -> float:
    value = (x * 0x1F123BB5) ^ (y * 0x5F356495) ^ (salt * 0x6C8E9CF5)
    value = (value ^ (value >> 15)) * 0x2C1B3C6D
    value = (value ^ (value >> 12)) * 0x297A2D39
    value ^= value >> 15
    return (value & 0xFFFFFFFF) / 0xFFFFFFFF


def cellular_edge(u: float, v: float, cells: int, salt: int) -> float:
    """Distance to a periodic, jittered Voronoi edge."""
    scaled_u = u * cells
    scaled_v = v * cells
    cell_x = math.floor(scaled_u)
    cell_y = math.floor(scaled_v)
    nearest = 1.0e9
    second = 1.0e9
    for oy in (-1, 0, 1):
        for ox in (-1, 0, 1):
            unwrapped_x = cell_x + ox
            unwrapped_y = cell_y + oy
            wrapped_x = unwrapped_x % cells
            wrapped_y = unwrapped_y % cells
            feature_x = unwrapped_x + 0.18 + 0.64 * hash01(wrapped_x, wrapped_y, salt)
            feature_y = unwrapped_y + 0.18 + 0.64 * hash01(wrapped_x, wrapped_y, salt + 17)
            dx = feature_x - scaled_u
            dy = feature_y - scaled_v
            distance = math.sqrt(dx * dx + dy * dy)
            if distance < nearest:
                second = nearest
                nearest = distance
            elif distance < second:
                second = distance
    return second - nearest


def height(u: float, v: float) -> float:
    # Periodic low-amplitude ice grain. Integer frequencies keep every edge
    # tileable when the water shader repeats the normal map.
    grain = (
        0.42 * math.sin(TAU * (3.0 * u + 2.0 * v))
        + 0.26 * math.sin(TAU * (7.0 * u - 5.0 * v) + 0.8)
        + 0.17 * math.cos(TAU * (13.0 * u + 9.0 * v) + 1.7)
        + 0.10 * math.sin(TAU * (23.0 * u - 17.0 * v) + 2.4)
    )

    # Jittered periodic Voronoi edges give the ice an irregular fracture
    # network instead of recognisable parallel lines. A second, finer network
    # adds sparse branches while keeping the normal map inexpensive in-game.
    warp_u = (u + 0.018 * math.sin(TAU * (2.0 * u + 3.0 * v))) % 1.0
    warp_v = (v + 0.015 * math.cos(TAU * (3.0 * u - 2.0 * v))) % 1.0
    major_edge = cellular_edge(warp_u, warp_v, 7, 11)
    minor_edge = cellular_edge(warp_u, warp_v, 13, 37)
    major_crack = math.exp(-((major_edge / 0.035) ** 2))
    minor_crack = math.exp(-((minor_edge / 0.018) ** 2))
    return grain * 0.014 - major_crack * 0.065 - minor_crack * 0.020


def make_pixels() -> bytes:
    step = 1.0 / SIZE
    strength = 5.0
    heights = [
        height(x * step, y * step)
        for y in range(SIZE)
        for x in range(SIZE)
    ]
    pixels = bytearray(SIZE * SIZE * 4)
    offset = 0
    for y in range(SIZE):
        for x in range(SIZE):
            dx = (
                heights[y * SIZE + ((x + 1) % SIZE)]
                - heights[y * SIZE + ((x - 1) % SIZE)]
            ) * strength
            dy = (
                heights[((y + 1) % SIZE) * SIZE + x]
                - heights[((y - 1) % SIZE) * SIZE + x]
            ) * strength
            nx, ny, nz = -dx, -dy, 1.0
            inv_length = 1.0 / math.sqrt(nx * nx + ny * ny + nz * nz)
            pixels[offset] = round((nx * inv_length * 0.5 + 0.5) * 255.0)
            pixels[offset + 1] = round((ny * inv_length * 0.5 + 0.5) * 255.0)
            pixels[offset + 2] = round((nz * inv_length * 0.5 + 0.5) * 255.0)
            pixels[offset + 3] = 255
            offset += 4
    return bytes(pixels)


def png_chunk(kind: bytes, payload: bytes) -> bytes:
    return (
        struct.pack(">I", len(payload))
        + kind
        + payload
        + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
    )


def write_png(path: Path, pixels: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    stride = SIZE * 4
    scanlines = b"".join(
        b"\x00" + pixels[y * stride : (y + 1) * stride] for y in range(SIZE)
    )
    header = struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)
    encoded = (
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", header)
        + png_chunk(b"IDAT", zlib.compress(scanlines, 9))
        + png_chunk(b"IEND", b"")
    )
    path.write_bytes(encoded)


def main() -> None:
    root = Path(__file__).resolve().parents[1]
    pixels = make_pixels()
    outputs = (
        root / "DVSeasons.Unity/Assets/DVSeasons/DV99/winter/WaterIceNormal.png",
        root / "Resources/Runtime/Textures/Seasonal/winter/WaterIceNormal.png",
    )
    for output in outputs:
        write_png(output, pixels)
        print(f"wrote {output} ({SIZE}x{SIZE})")


if __name__ == "__main__":
    main()
