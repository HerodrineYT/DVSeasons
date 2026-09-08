using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    // Bounded world-space ribbons. Only new wheel movement uploads geometry;
    // dry weather never ages a mark. Coordinates survive floating-origin shifts.
    internal sealed class RailSnowTracks : IDisposable
    {
        private sealed class Chunk
        {
            public readonly Mesh Mesh = new Mesh { name="DVSeasons cleared rails", hideFlags=HideFlags.HideAndDontSave };
            public readonly List<Vector3> Vertices = new List<Vector3>();
            public readonly List<Vector2> Uv = new List<Vector2>();
            public readonly List<int> Indices = new List<int>();
            public bool Dirty;
            public bool TopologyDirty;
            public Chunk() { Mesh.MarkDynamic(); }
            public int Generation;
            public float LastStamp;
        }
        private sealed class Wheel
        {
            public Vector3 Position, Start, Direction;
            public float Stamp;
            public readonly Chunk[] Chunks=new Chunk[2];
            public readonly int[] Ends=new int[2];
            public readonly int[] Generations=new int[2];
        }
        private readonly Dictionary<int,Wheel> previous = new Dictionary<int,Wheel>();
        private readonly List<Chunk> chunks = new List<Chunk>();
        public float SnowClock { get; private set; }
        public Vector3 WorldOffset;
        public int SegmentCount { get; private set; }
        public void Advance(float snowfall, float seconds)
        { SnowClock += Mathf.Clamp01(snowfall)*Mathf.Max(0,seconds)/180f; }

        public void WheelAt(int id, Vector3 position, Vector3 right, Vector3 forward)
        {
            position-=WorldOffset;
            Wheel old;
            bool known=previous.TryGetValue(id,out old);
            var distance=known ? Vector3.Distance(old.Position,position) : 0;
            if (known && distance<0.04f && SnowClock-old.Stamp<0.015f) return;
            // Never draw a stripe through the world after a spawn/teleport.
            var start=known && distance>=0.04f && distance<12f ? old.Position : position-forward*0.08f;
            var direction=(position-start).normalized;
            bool extend=known && distance>=0.04f && distance<12f &&
                SnowClock-old.Stamp<0.015f && Vector3.Distance(old.Start,position)<8f &&
                Vector3.Dot(old.Direction,direction)>0.9998f;
            if(extend) for(int i=0;i<2;i++)
                if(old.Chunks[i]==null || old.Chunks[i].Generation!=old.Generations[i]) extend=false;
            if(!known)
            {
                if(previous.Count>=8192) previous.Clear();
                old=new Wheel(); previous[id]=old;
            }
            for(int i=0;i<2;i++)
            {
                var offset=right*((i*2-1)*0.75f); // 1.435m inner gauge + rail head width.
                var end=position+offset+direction*0.04f;
                var width=right*0.085f;
                if(extend)
                {
                    var chunk=old.Chunks[i];int n=old.Ends[i];
                    chunk.Vertices[n]=end-width; chunk.Vertices[n+1]=end+width;chunk.Dirty=true;
                }
                else Add(start+offset,end,width,old,i);
            }
            old.Position=position;
            if(!extend) { old.Start=start; old.Direction=direction; old.Stamp=SnowClock; }
        }
        private void Add(Vector3 a,Vector3 b,Vector3 width,Wheel wheel,int side)
        {
            Chunk chunk=chunks.Count>0 ? chunks[chunks.Count-1] : null;
            if(chunk==null || chunk.Vertices.Count>=2048)
            {
                if(chunks.Count>=128)
                {
                    chunk=chunks[0]; chunks.RemoveAt(0); // reuse oldest allocation
                    chunk.Vertices.Clear(); chunk.Uv.Clear(); chunk.Indices.Clear(); chunk.Mesh.Clear(); chunk.Generation++;
                }
                else chunk=new Chunk();
                chunks.Add(chunk);
            }
            int n=chunk.Vertices.Count;
            wheel.Chunks[side]=chunk;wheel.Ends[side]=n+2;wheel.Generations[side]=chunk.Generation;
            chunk.Vertices.Add(a-width); chunk.Vertices.Add(a+width);
            chunk.Vertices.Add(b-width); chunk.Vertices.Add(b+width);
            chunk.Uv.Add(new Vector2(-1,SnowClock)); chunk.Uv.Add(new Vector2(1,SnowClock));
            chunk.Uv.Add(new Vector2(-1,SnowClock)); chunk.Uv.Add(new Vector2(1,SnowClock));
            chunk.Indices.Add(n);chunk.Indices.Add(n+2);chunk.Indices.Add(n+1);
            chunk.Indices.Add(n+1);chunk.Indices.Add(n+2);chunk.Indices.Add(n+3);
            chunk.Dirty=chunk.TopologyDirty=true; chunk.LastStamp=SnowClock; SegmentCount++;
        }
        public bool HasVisibleTracks(Plane[] frustum)
        {
            foreach(var chunk in chunks)
            {
                if(SnowClock-chunk.LastStamp>=1f) continue;
                if(chunk.Dirty) return true;
                var bounds=chunk.Mesh.bounds;bounds.center+=WorldOffset;
                if(GeometryUtility.TestPlanesAABB(frustum,bounds)) return true;
            }
            return false;
        }
        public void Record(CommandBuffer buffer, Material material, Plane[] frustum)
        {
            buffer.SetGlobalFloat("_DVPSRailSnowClock",SnowClock);
            var matrix=Matrix4x4.Translate(WorldOffset);
            foreach(var chunk in chunks)
            {
                if(SnowClock-chunk.LastStamp>=1f) continue;
                if(chunk.Dirty)
                {
                    chunk.Mesh.SetVertices(chunk.Vertices);
                    if(chunk.TopologyDirty)
                    {chunk.Mesh.SetUVs(0,chunk.Uv);chunk.Mesh.SetTriangles(chunk.Indices,0);chunk.TopologyDirty=false;}
                    else chunk.Mesh.RecalculateBounds();
                    chunk.Dirty=false;
                }
                var bounds=chunk.Mesh.bounds; bounds.center+=WorldOffset;
                if(GeometryUtility.TestPlanesAABB(frustum,bounds)) buffer.DrawMesh(chunk.Mesh,matrix,material,0,2);
            }
        }
        public void Dispose()
        {
            foreach(var c in chunks)
                if(Application.isPlaying) UnityEngine.Object.Destroy(c.Mesh); else UnityEngine.Object.DestroyImmediate(c.Mesh);
            chunks.Clear(); previous.Clear(); SnowClock=0; SegmentCount=0;
        }
    }
}
