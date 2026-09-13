"""Export installed DE6 meshes for local GPU verification; never ship game assets.

Usage: python Tools/prepare_de6_roof_verification.py <DerailValley_Data/resources.assets>
Requires UnityPy. Output stays under artifacts/, outside both release packages.
"""
import json
import sys
from pathlib import Path

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

destination = Path(__file__).resolve().parents[1] / "artifacts/verification/de6-geometry"
destination.mkdir(parents=True, exist_ok=True)
environment = UnityPy.load(sys.argv[1])
mesh_names = {"diesel_body", "cab", "cab_LOD1", "cab_LOD2"}
mesh_names.update(f"diesel_body_LOD{i}" for i in range(1, 5))
textures = {"LocoDE6_Body_01n": "_BumpMap", "OldPaintDetail_01n": "_DetailNormalMap",
            "LocoDE6_Body_01d": "_MainTex"}
found = set()


def vectors(values, components):
    return [dict(zip(components, value)) for value in values] if values else []


for obj in environment.objects:
    if obj.type.name not in ("Mesh", "Texture2D"):
        continue
    data = obj.read()
    name = data.m_Name
    if obj.type.name == "Mesh" and name in mesh_names:
        handler = MeshHandler(data)
        handler.process()
        result = dict(vertices=vectors(handler.m_Vertices, "xyz"),
                      normals=vectors(handler.m_Normals, "xyz"),
                      uv=vectors(handler.m_UV0, "xy"),
                      tangents=vectors(handler.m_Tangents, "xyzw"),
                      indices=handler.m_IndexBuffer)
        (destination / (name + ".json")).write_text(json.dumps(result), encoding="utf8")
        found.add(name)
    elif obj.type.name == "Texture2D" and name in textures:
        data.image.save(destination / (textures[name] + ".png"))
        found.add(name)

missing = (mesh_names | set(textures)) - found
if missing:
    raise RuntimeError("Required DE6 verification assets missing: " + ", ".join(sorted(missing)))
print("Local DE6 verification assets:", destination)
