import glob
import os
import re
import sys

import UnityPy


def main() -> int:
    if len(sys.argv) < 3:
        print("usage: search_unity_assets.py <data-dir> <regex>", file=sys.stderr)
        return 64

    data_dir = sys.argv[1]
    pattern = re.compile(sys.argv[2], re.IGNORECASE)
    matches = []
    asset_files = glob.glob(os.path.join(data_dir, "sharedassets*.assets"))
    asset_files.extend(glob.glob(os.path.join(data_dir, "resources.assets")))
    for path in asset_files:
        if os.path.getsize(path) < 5000:
            continue
        try:
            environment = UnityPy.load(path)
        except Exception:
            continue
        for obj in environment.objects:
            if obj.type.name not in {"Material", "Texture2D", "Texture2DArray"}:
                continue
            try:
                data = obj.read_typetree()
            except Exception:
                continue
            name = str(data.get("m_Name", ""))
            if pattern.search(name):
                matches.append(
                    (
                        os.path.basename(path),
                        obj.type.name,
                        name,
                        data.get("m_Width", ""),
                        data.get("m_Height", ""),
                        obj.path_id,
                    )
                )

    for match in matches:
        print("{} | {} | {} | {}x{} | path={}".format(*match))
    print("MATCHES", len(matches))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
