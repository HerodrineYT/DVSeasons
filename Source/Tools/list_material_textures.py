import os
import re
import sys

import UnityPy


def main() -> int:
    if len(sys.argv) not in {2, 3}:
        print("usage: list_material_textures.py <assets-file> [regex]", file=sys.stderr)
        return 64
    pattern = re.compile(sys.argv[2], re.IGNORECASE) if len(sys.argv) == 3 else None
    environment = UnityPy.load(sys.argv[1])
    objects = {obj.path_id: obj for obj in environment.objects}
    for obj in environment.objects:
        if obj.type.name != "Material":
            continue
        data = obj.read_typetree()
        rows = []
        for property_name, value in data.get("m_SavedProperties", {}).get("m_TexEnvs", []):
            pointer = value.get("m_Texture", {})
            target_name = ""
            if pointer.get("m_FileID") == 0 and pointer.get("m_PathID") in objects:
                target = objects[pointer["m_PathID"]]
                try:
                    target_name = str(target.read_typetree().get("m_Name", ""))
                except Exception:
                    target_name = target.type.name
            rows.append((property_name, target_name, pointer.get("m_FileID"), pointer.get("m_PathID")))
        description = data.get("m_Name", "") + " " + " ".join(row[1] for row in rows)
        if pattern is not None and not pattern.search(description):
            continue
        shader_pointer = data.get("m_Shader", {})
        shader_name = ""
        if shader_pointer.get("m_FileID") == 0 and shader_pointer.get("m_PathID") in objects:
            shader = objects[shader_pointer["m_PathID"]]
            try:
                shader_name = str(shader.read_typetree().get("m_Name", ""))
            except Exception:
                shader_name = shader.type.name
        print("MATERIAL", data.get("m_Name", ""), "path", obj.path_id,
              "shader", shader_name, "shader-file", shader_pointer.get("m_FileID"),
              "shader-path", shader_pointer.get("m_PathID"))
        for row in rows:
            print(" ", row[0], "=>", row[1], "file", row[2], "path", row[3])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
