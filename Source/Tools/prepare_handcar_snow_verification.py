"""Export installed H1 handle meshes for local verification; never ship game assets.

Usage: python Tools/prepare_handcar_snow_verification.py <DerailValley_Data/resources.assets>
"""
import json
import sys
from pathlib import Path
import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler

destination = Path(__file__).resolve().parents[1] / 'artifacts/verification/handcar-geometry'
destination.mkdir(parents=True, exist_ok=True)
environment = UnityPy.load(sys.argv[1])
required = {'handlebar', 'handlebar_LOD2'}
found = set()
for obj in environment.objects:
    if obj.type.name != 'Mesh':
        continue
    data = obj.read()
    if data.m_Name not in required:
        continue
    handler = MeshHandler(data)
    handler.process()
    def vectors(values, axes):
        return [dict(zip(axes, value)) for value in values] if values else []
    result = dict(vertices=vectors(handler.m_Vertices, 'xyz'), normals=vectors(handler.m_Normals, 'xyz'),
                  uv=vectors(handler.m_UV0, 'xy'), indices=handler.m_IndexBuffer)
    (destination / (data.m_Name + '.json')).write_text(json.dumps(result), encoding='utf-8')
    found.add(data.m_Name)
if found != required:
    raise RuntimeError('Missing handcar geometry: ' + ', '.join(sorted(required - found)))
print('Local H1 verification meshes:', destination)
