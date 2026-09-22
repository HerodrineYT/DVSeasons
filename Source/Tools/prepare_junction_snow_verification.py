"""Export installed junction meshes/poses only to ignored local verification data."""
import json
import struct
import sys
import zlib
from pathlib import Path

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

base = Path(sys.argv[1])
destination = Path(__file__).resolve().parents[1] / "artifacts/verification/junction-native"
destination.mkdir(parents=True, exist_ok=True)
level = UnityPy.load(str(base / "level520"))
objects = {o.path_id: o for o in level.objects}
assets = UnityPy.load(str(base / "sharedassets520.assets"))
asset_objects = {o.path_id: o for o in assets.objects}

def read(pointer):
    assert pointer['m_FileID'] == 0
    return objects[pointer['m_PathID']].read_typetree(check_read=False)

def vectors(values, components):
    return [dict(zip(components, value)) for value in values] if values else []

switches = []
for obj in level.objects:
    if obj.type.name != 'MonoBehaviour':
        continue
    data = obj.read_typetree(check_read=False)
    if data['m_Script']['m_PathID'] != 4972:
        continue
    animator_id = struct.unpack_from('<iq', obj.get_raw_data(), 44)[1]
    animator = objects[animator_id].read_typetree()
    go = read(animator['m_GameObject'])
    transform = read(go['m_Component'][0]['component'])
    for child in transform['m_Children']:
        child_t = read(child)
        child_go = read(child_t['m_GameObject'])
        if child_go['m_Name'] == 'Graphical':
            switches.append((go, child_go, child_t))
    if switches:
        break

go, graphical, graphical_t = switches[0]
parts = []
for ptr in graphical_t['m_Children']:
    transform = read(ptr)
    part_go = read(transform['m_GameObject'])
    for c in part_go['m_Component']:
        obj = objects[c['component']['m_PathID']]
        if obj.type.name != 'MeshFilter':
            continue
        mesh_ptr = obj.read_typetree()['m_Mesh']
        assert mesh_ptr['m_FileID'] == 4
        mesh = asset_objects[mesh_ptr['m_PathID']].read()
        handler = MeshHandler(mesh)
        handler.process()
        parts.append(dict(name=part_go['m_Name'], meshName=mesh.m_Name,
                          position=transform['m_LocalPosition'], rotation=transform['m_LocalRotation'],
                          scale=transform['m_LocalScale'],
                          vertices=vectors(handler.m_Vertices, 'xyz'), normals=vectors(handler.m_Normals, 'xyz'),
                          uv=vectors(handler.m_UV0, 'xy'), indices=handler.m_IndexBuffer))

poses = []
for obj in assets.objects:
    if obj.type.name != 'AnimationClip':
        continue
    clip = obj.read_typetree()
    if clip['m_Name'] != 'switch-outer-sign':
        continue
    index = 0
    for binding in clip['m_ClipBindingConstant']['genericBindings']:
        count = 4 if binding['attribute'] == 2 else 3 if binding['typeID'] == 4 else 1
        values = clip['m_MuscleClip']['m_ValueArrayDelta'][index:index + count]
        index += count
        if binding['path'] == zlib.crc32(b'Graphical/rails_moving') and binding['attribute'] == 4:
            poses = [dict(zip('xyz', [v[key] for v in values])) for key in ['m_Start', 'm_Stop']]
assert len(poses) == 2
payload = dict(nativeRoot=go['m_Name'], position=graphical_t['m_LocalPosition'],
               rotation=graphical_t['m_LocalRotation'], scale=graphical_t['m_LocalScale'],
               poses=poses, parts=parts)
(destination / 'junction.json').write_text(json.dumps(payload), encoding='utf8')
print(destination / 'junction.json', len(parts), 'meshes; blade poses:', poses)
