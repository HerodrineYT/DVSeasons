import os
import sys

import UnityPy


def pointer_id(pointer):
    if not isinstance(pointer, dict) or pointer.get("m_FileID") != 0:
        return None
    return pointer.get("m_PathID")


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: inspect_material_renderers.py <asset-file> <material-name>", file=sys.stderr)
        return 64

    path, material_name = sys.argv[1:]
    environment = UnityPy.load(path)
    objects = {obj.path_id: obj for obj in environment.objects}
    material_ids = set()
    for obj in environment.objects:
        if obj.type.name != "Material":
            continue
        data = obj.read_typetree()
        if str(data.get("m_Name", "")).lower() == material_name.lower():
            material_ids.add(obj.path_id)

    mesh_by_game_object = {}
    for obj in environment.objects:
        if obj.type.name != "MeshFilter":
            continue
        data = obj.read_typetree()
        game_object = pointer_id(data.get("m_GameObject"))
        mesh = pointer_id(data.get("m_Mesh"))
        if game_object and mesh:
            mesh_by_game_object[game_object] = mesh

    matches = 0
    for obj in environment.objects:
        if obj.type.name != "MeshRenderer":
            continue
        data = obj.read_typetree()
        materials = [pointer_id(value) for value in data.get("m_Materials", [])]
        if not any(value in material_ids for value in materials):
            continue
        game_object_id = pointer_id(data.get("m_GameObject"))
        game_object = objects.get(game_object_id)
        game_object_name = ""
        if game_object is not None:
            game_object_name = str(game_object.read_typetree().get("m_Name", ""))
        mesh_id = mesh_by_game_object.get(game_object_id)
        mesh = objects.get(mesh_id)
        mesh_data = mesh.read_typetree() if mesh is not None else {}
        print(
            os.path.basename(path),
            "renderer", obj.path_id,
            "game-object", game_object_name,
            "mesh", mesh_data.get("m_Name"),
            "readable", mesh_data.get("m_IsReadable"),
            "vertices", mesh_data.get("m_VertexData", {}).get("m_VertexCount"),
            "submeshes", len(mesh_data.get("m_SubMeshes", [])),
        )
        matches += 1
    print("MATCHES", matches)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
