"""Read shipped prefab metadata; never modifies game assets."""
import json, sys
from pathlib import Path
import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

env = UnityPy.load(sys.argv[1])
generator = TypeTreeGenerator('2019.4.40f1')
generator.load_local_dll_folder(str(Path(sys.argv[1]).parent / 'Managed'))
rows = []
def go_path(go):
    names = [go.m_Name]
    transform = next(c.component.read() for c in go.m_Component if c.component.deref().type.name == 'Transform')
    for _ in range(25):
        if transform.m_Father.path_id == 0: break
        transform = transform.m_Father.read()
        names.append(transform.m_GameObject.read().m_Name)
    return '/'.join(reversed(names))

for obj in env.objects:
    if obj.type.name not in ('GameObject', 'MonoBehaviour'): continue
    try:
        data = obj.read(check_read=False)
        if obj.type.name == 'GameObject':
            name = data.m_Name
            if any(word in name.lower() for word in ('dm1', 'de6', 'flower', 'dais', 'poppy')):
                rows.append(dict(type='GameObject', id=obj.path_id, name=name))
            if any(word in name.lower() for word in ('window', 'glass', 'throttle', 'primer', 'flower')):
                path = go_path(data)
                if 'DM1U' in path or 'DE6' in path:
                    comps=[]
                    for c in data.m_Component:
                        comp=c.component.deref().read(check_read=False)
                        entry=dict(type=c.component.deref().type.name,id=c.component.path_id)
                        if entry['type']=='MeshRenderer':
                            entry['materials']=[dict(id=p.path_id,name=p.read().m_Name) for p in comp.m_Materials]
                        if entry['type']=='MeshFilter':
                            entry['mesh']=comp.m_Mesh.read().m_Name
                        if entry['type']=='MonoBehaviour':
                            sc=comp.m_Script.read(); entry['class']=sc.m_ClassName
                            if any(s in sc.m_ClassName for s in ('Port','Control','Lever')):
                                node=generator.get_nodes_up(sc.m_AssemblyName,(sc.m_Namespace+'.' if sc.m_Namespace else '')+sc.m_ClassName)
                                entry['data']=c.component.deref().read_typetree(nodes=node)
                        comps.append(entry)
                    rows.append(dict(type='PrefabNode',id=obj.path_id,path=path,components=comps))
            continue
        script = data.m_Script.read()
        kind = script.m_ClassName
        if kind in ('Window', 'VegetationPackagePro', 'DieselEnginePowerSourceDefinition') or 'DieselEngine' in kind:
            node = generator.get_nodes_up(script.m_AssemblyName, (script.m_Namespace + '.' if script.m_Namespace else '') + kind)
            tree = obj.read_typetree(nodes=node)
            if kind == 'Window':
                go = data.m_GameObject.read()
                chain = [go.m_Name]
                transform = next(c.component.read() for c in go.m_Component if c.component.read().object_reader.type.name == 'Transform')
                for _ in range(25):
                    if transform.m_Father.path_id == 0: break
                    transform = transform.m_Father.read()
                    chain.append(transform.m_GameObject.read().m_Name)
                rows.append(dict(type=kind, id=obj.path_id, path='/'.join(reversed(chain)), data=tree))
            elif kind == 'VegetationPackagePro':
                items=tree.get('VegetationInfoList', [])
                rows.append(dict(type=kind,id=obj.path_id,name=tree.get('m_Name'),items=[{k:v for k,v in i.items() if k in ('Name','VegetationItemID','VegetationType','VegetationPrefab')} for i in items]))
            else:
                rows.append(dict(type=kind,id=obj.path_id,path=go_path(data.m_GameObject.read()),data=tree))
    except Exception as exc:
        if obj.type.name == 'MonoBehaviour' and 'kind' in locals() and (kind in ('Window', 'VegetationPackagePro', 'DieselEnginePowerSourceDefinition') or 'DieselEngine' in kind):
            rows.append(dict(error=str(exc),id=obj.path_id))
Path(sys.argv[2]).write_text(json.dumps(rows,ensure_ascii=False,indent=2), encoding='utf8')
print('Metadata entries:', len(rows))



