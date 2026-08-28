import sys

import UnityPy


def main() -> int:
    if len(sys.argv) not in {2, 3}:
        print("usage: inspect_microsplat_material.py <sharedassets-file> [material-name]", file=sys.stderr)
        return 64

    requested_name = sys.argv[2] if len(sys.argv) == 3 else "MicroSplat"
    environment = UnityPy.load(sys.argv[1])
    objects = {obj.path_id: obj for obj in environment.objects}
    for obj in environment.objects:
        if obj.type.name != "Material":
            continue
        material = obj.read_typetree()
        if material.get("m_Name") != requested_name:
            continue

        print("MATERIAL", material["m_Name"])
        for property_name, value in material["m_SavedProperties"]["m_TexEnvs"]:
            pointer = value.get("m_Texture", {})
            file_id = pointer.get("m_FileID")
            path_id = pointer.get("m_PathID")
            description = "none"
            if file_id == 0 and path_id in objects:
                target = objects[path_id]
                try:
                    data = target.read_typetree()
                    description = "{}:{} {}x{} depth={}".format(
                        target.type.name,
                        data.get("m_Name"),
                        data.get("m_Width", ""),
                        data.get("m_Height", ""),
                        data.get("m_Depth", ""),
                    )
                except Exception:
                    description = target.type.name
            print(property_name, "file", file_id, "path", path_id, "=>", description)
        return 0

    print("Material not found: " + requested_name, file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
