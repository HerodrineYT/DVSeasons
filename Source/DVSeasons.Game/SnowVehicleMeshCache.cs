using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    // Only readable, rigid, opaque fallback meshes are combined. Native game
    // meshes need no extraction: their snow is already in their Standard pass.
    internal sealed class SnowVehicleMeshCache : IDisposable
    {
        internal sealed class Geometry
        {
            public Mesh Mesh;
            public Mesh[] Sources;
            public int[] Slots;
            public Matrix4x4[] Relative;
            public int Key,Users;
        }
        private static readonly Dictionary<int,List<Geometry>> shared=new Dictionary<int,List<Geometry>>();
        internal sealed class Group
        {
            public Mesh Mesh;
            public SnowVehicleRegistry.Part[] Parts;
            public Matrix4x4[] Relative;
            public SnowVehicleDrawScheduler.DrawMetadata Metadata;
            public Geometry Shared;
            public bool Matches(List<SnowVehicleRegistry.Part> visible,int start,Matrix4x4 worldToVehicle)
            {
                if(start+Parts.Length>visible.Count)return false;
                for(int i=0;i<Parts.Length;i++)
                {
                    if(!ReferenceEquals(visible[start+i],Parts[i]) || Parts[i].Renderer==null ||
                        Parts[i].Filter==null || Parts[i].Filter.sharedMesh!=Parts[i].Mesh)return false;
                    var relative=worldToVehicle*Parts[i].Renderer.localToWorldMatrix;
                    // Comparing local transforms avoids world-AABB inflation.
                    // Any actual articulation returns this run to native draws.
                    for(int cell=0;cell<16;cell++)if(Mathf.Abs(relative[cell]-Relative[i][cell])>.0001f)return false;
                }
                return true;
            }
        }
        private readonly Dictionary<SnowVehicleRegistry.Part,Group> groups=new Dictionary<SnowVehicleRegistry.Part,Group>();
        private readonly List<Group> owned=new List<Group>();
        public int Count=>owned.Count;
        public Group Find(SnowVehicleRegistry.Part first){Group group;return groups.TryGetValue(first,out group)?group:null;}
        public void Build(SnowVehicleRegistry.Vehicle vehicle)
        {
            Dispose();var run=new List<SnowVehicleRegistry.Part>();int vertices=0;
            foreach(var part in vehicle.Parts)
            {
                bool eligible=Eligible(part);
                if(run.Count>0 && (!eligible || vertices+part.Mesh.vertexCount>60000 ||
                    part.Lod!=run[0].Lod || part.LodMask!=run[0].LodMask || part.Interior!=run[0].Interior))
                {Finish(vehicle,run);run.Clear();vertices=0;}
                if(eligible){run.Add(part);vertices+=part.Mesh.vertexCount;}
            }
            Finish(vehicle,run);
        }
        private static bool Eligible(SnowVehicleRegistry.Part part)
        {
            if(part.NativeComplete || !part.HasOpaque || part.Mesh==null || !part.Mesh.isReadable ||
                part.MovingFrame!=null || !(part.Renderer is MeshRenderer) || part.Renderer.isPartOfStaticBatch ||
                ((MeshRenderer)part.Renderer).additionalVertexStreams!=null)return false;
            if(part.Renderer.GetComponentInParent<Animator>()!=null || part.Renderer.GetComponentInParent<Animation>()!=null)return false;
            for(int i=0;i<part.Opaque.Length;i++)
                if(!part.Opaque[i] || part.Cutoff[i]>0 || (part.NativeSlots!=null && part.NativeSlots[i]) ||
                    (part.Albedo[i]!=null && part.Albedo[i].name.IndexOf("coal",StringComparison.OrdinalIgnoreCase)>=0))return false;
            return true;
        }
        private void Finish(SnowVehicleRegistry.Vehicle vehicle,List<SnowVehicleRegistry.Part> run)
        {
            if(run.Count<2)return;
            // Repeated copies of one submesh already form an efficient instance
            // batch. Combining that run adds validation without removing keys.
            bool repeated=true;
            foreach(var part in run)if(part.Mesh!=run[0].Mesh || part.Opaque.Length!=1){repeated=false;break;}
            if(repeated)return;
            var sources=new List<CombineInstance>();var relative=new Matrix4x4[run.Count];
            int key=17;
            for(int i=0;i<run.Count;i++)
            {
                // Compose local transforms directly, so identical model copies
                // share geometry even at different floating-origin positions.
                var node=run[i].Renderer.transform;var matrix=Matrix4x4.identity;
                while(node!=null && node!=vehicle.Root)
                {matrix=Matrix4x4.TRS(node.localPosition,node.localRotation,node.localScale)*matrix;node=node.parent;}
                if(node!=vehicle.Root)return;
                relative[i]=matrix;
                if(relative[i].determinant<=0)return;
                unchecked{key=key*397^run[i].Mesh.GetInstanceID();key=key*397^matrix.GetHashCode();key=key*397^run[i].Opaque.Length;}
                for(int slot=0;slot<run[i].Opaque.Length;slot++)
                    sources.Add(new CombineInstance{mesh=run[i].Mesh,subMeshIndex=slot,transform=relative[i]});
            }
            List<Geometry> matches;Geometry geometry=null;
            if(shared.TryGetValue(key,out matches))foreach(var candidate in matches)
            {
                if(candidate.Sources.Length!=run.Count)continue;
                bool same=true;
                for(int i=0;i<run.Count;i++)
                    if(candidate.Sources[i]!=run[i].Mesh || candidate.Slots[i]!=run[i].Opaque.Length || !SnowVehicleDrawScheduler.SameMatrix(ref candidate.Relative[i],ref relative[i])){same=false;break;}
                if(same){geometry=candidate;break;}
            }
            if(geometry==null)
            {
                var combined=new Mesh{name="DVSeasons shared model snow mesh",hideFlags=HideFlags.HideAndDontSave,indexFormat=IndexFormat.UInt32};
                try{combined.CombineMeshes(sources.ToArray(),true,true,false);combined.UploadMeshData(true);}
                catch{Destroy(combined);return;}
                geometry=new Geometry{Mesh=combined,Relative=relative,Sources=new Mesh[run.Count],Slots=new int[run.Count],Key=key};
                for(int i=0;i<run.Count;i++){geometry.Sources[i]=run[i].Mesh;geometry.Slots[i]=run[i].Opaque.Length;}
                if(matches==null){matches=new List<Geometry>();shared.Add(key,matches);}matches.Add(geometry);
            }
            geometry.Users++;var mesh=geometry.Mesh;
            var group=new Group{Mesh=mesh,Parts=run.ToArray(),Relative=relative,
                Shared=geometry,
                Metadata=new SnowVehicleDrawScheduler.DrawMetadata(null,mesh,0,0,null,Vector4.zero,true)};
            owned.Add(group);groups.Add(run[0],group);
        }
        public void Dispose()
        {
            foreach(var group in owned)
            {
                var geometry=group.Shared;if(--geometry.Users!=0)continue;
                var entries=shared[geometry.Key];entries.Remove(geometry);if(entries.Count==0)shared.Remove(geometry.Key);
                Destroy(geometry.Mesh);
            }
            owned.Clear();groups.Clear();
        }
        private static void Destroy(UnityEngine.Object obj)
        {if(obj!=null){if(Application.isPlaying)UnityEngine.Object.Destroy(obj);else UnityEngine.Object.DestroyImmediate(obj);}}
    }
}
