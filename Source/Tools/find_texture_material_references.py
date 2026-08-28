import os
import sys

import UnityPy


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: find_texture_material_references.py <asset-file> <texture-name-prefix>", file=sys.stderr)
        return 64

    asset_path, prefix = sys.argv[1:]
    environment = UnityPy.load(asset_path)
    objects = {obj.path_id: obj for obj in environment.objects}
    texture_ids = set()
    for obj in environment.objects:
        if obj.type.name != "Texture2D":
            continue
        data = obj.read_typetree()
        if str(data.get("m_Name", "")).startswith(prefix):
            texture_ids.add(obj.path_id)

    matches = 0
    for obj in environment.objects:
        if obj.type.name != "Material":
            continue
        material = obj.read_typetree()
        for property_name, value in material.get("m_SavedProperties", {}).get("m_TexEnvs", []):
            pointer = value.get("m_Texture", {})
            path_id = pointer.get("m_PathID")
            if pointer.get("m_FileID") == 0 and path_id in texture_ids:
                texture = objects[path_id].read_typetree()
                print(material.get("m_Name"), property_name, "=>", texture.get("m_Name"))
                matches += 1
    print("MATCHES", matches)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
