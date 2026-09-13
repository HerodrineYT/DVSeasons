"""Export installed DH4 WALKABLE geometry for local leaf physics checks.

Usage: python Tools/prepare_leaf_cab_verification.py <resources.assets>
Requires UnityPy. Game geometry stays in artifacts and is never packaged.
"""
import json
import sys
from pathlib import Path

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

destination = Path(__file__).resolve().parents[1] / "artifacts/verification/leaf-cab-geometry.json"
environment = UnityPy.load(sys.argv[1])
parts = []
for obj in environment.objects:
    if obj.type.name != "MeshCollider":
        continue
    data = obj.read()
    game_object = data.m_GameObject.read()
    if game_object.m_Layer != 11 or not data.m_Enabled:
        continue
    transform = next(c.component.read() for c in game_object.m_Component
                     if c.component.type.name == "Transform")
    names, poses = [], []
    while True:
        names.append(transform.m_GameObject.read().m_Name)
        poses.append(dict(position={c: getattr(transform.m_LocalPosition, c) for c in 'xyz'},
                          rotation={c: getattr(transform.m_LocalRotation, c) for c in 'xyzw'},
                          scale={c: getattr(transform.m_LocalScale, c) for c in 'xyz'}))
        if not transform.m_Father.path_id:
            break
        transform = transform.m_Father.read()
    if names[-1] != "LocoDH4" or "[walkable]" not in names:
        continue
    mesh = MeshHandler(data.m_Mesh.read())
    mesh.process()
    parts.append(dict(name="/".join(reversed(names)), poses=list(reversed(poses[:-1])),
                      vertices=[dict(zip("xyz", v)) for v in mesh.m_Vertices],
                      indices=mesh.m_IndexBuffer))
if len(parts) != 2:
    raise RuntimeError(f"Expected DH4 WALKABLE body and windows, got {len(parts)}")
destination.parent.mkdir(parents=True, exist_ok=True)
destination.write_text(json.dumps(dict(parts=parts)), encoding="utf8")
print(destination)
