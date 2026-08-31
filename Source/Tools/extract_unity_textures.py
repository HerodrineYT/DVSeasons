import os
import sys

import UnityPy


def main() -> int:
    if len(sys.argv) < 4:
        print("usage: extract_unity_textures.py <asset-file> <output-dir> <name> [name ...]", file=sys.stderr)
        return 64

    asset_path, output_dir, *names = sys.argv[1:]
    requested = set(names)
    os.makedirs(output_dir, exist_ok=True)
    environment = UnityPy.load(asset_path)
    extracted = set()
    for obj in environment.objects:
        if obj.type.name != "Texture2D":
            continue
        data = obj.read()
        if data.m_Name not in requested:
            continue
        output_path = os.path.join(output_dir, data.m_Name + ".png")
        data.image.save(output_path)
        print(output_path)
        extracted.add(data.m_Name)

    missing = requested - extracted
    if missing:
        print("missing: " + ", ".join(sorted(missing)), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
