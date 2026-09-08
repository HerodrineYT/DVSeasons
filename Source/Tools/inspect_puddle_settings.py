import os
import sys

import UnityPy


def main() -> int:
    if len(sys.argv) < 2:
        print("usage: inspect_puddle_settings.py <asset-file>...", file=sys.stderr)
        return 64

    matches = 0
    for path in sys.argv[1:]:
        environment = UnityPy.load(path)
        for obj in environment.objects:
            if obj.type.name != "MonoBehaviour":
                continue
            try:
                data = obj.read_typetree()
            except Exception:
                continue
            if not {"puddleTexture", "puddleScale", "puddleThreshold"}.issubset(data):
                continue
            print(
                os.path.basename(path),
                "path", obj.path_id,
                "scale", data.get("puddleScale"),
                "threshold", data.get("puddleThreshold"),
                "smoothness", data.get("puddleSmoothness"),
                "texture", data.get("puddleTexture"),
            )
            matches += 1
    print("MATCHES", matches)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
