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
            public readonly List<RailSnowContactIndex.Mark> Marks = new List<RailSnowContactIndex.Mark>();
            public bool Dirty;
            public bool TopologyDirty;
            // CPU bounds stay current while the GPU mesh is deferred offscreen.
            // Extensions only grow this conservative envelope until chunk reuse.
            public Bounds Bounds;
            public bool HasBounds;
            public void Include(Vector3 point)
            {
                if (HasBounds) Bounds.Encapsulate(point);
                else { Bounds = new Bounds(point, Vector3.zero); HasBounds = true; }
            }
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
        private readonly RailSnowContactIndex contacts = new RailSnowContactIndex();
        public float RemainingAt(Vector3 worldPoint) => contacts.Remaining(worldPoint-WorldOffset,SnowClock);
        public float SnowClock { get; private set; }
        public Vector3 WorldOffset;
        public int SegmentCount { get; private set; }
        internal int MeshUploadCount { get; private set; }
        public void Advance(float snowfall, float seconds)
        { SnowClock += Mathf.Clamp01(snowfall)*Mathf.Max(0,seconds)/180f; }

        public void Save(List<RailSnowStamp> destination)
        {
            foreach (var chunk in chunks)
                for (int n=0; n+3<chunk.Vertices.Count; n+=4)
                {
                    float age=SnowClock-chunk.Uv[n].y;
                    if (age>=1) continue;
                    destination.Add(new RailSnowStamp {
                        A=(chunk.Vertices[n]+chunk.Vertices[n+1])*.5f,
                        B=(chunk.Vertices[n+2]+chunk.Vertices[n+3])*.5f,
                        Width=(chunk.Vertices[n+1]-chunk.Vertices[n])*.5f, Age=Mathf.Max(0,age) });
                }
        }
        public void Restore(List<RailSnowStamp> records)
        {
            Dispose();
            if (records==null) return;
            var wheel=new Wheel();
            for (int i=0; i<Math.Min(records.Count,65536); i++)
            {
                var record=records[i];
                if (record==null || !record.IsValid() || record.Age>=1) continue;
                SnowClock=-record.Age;
                Add(record.A,record.B,record.Width,wheel,0);
            }
            // Timestamps are relative to the snowfall clock, which is paused
            // while the save is closed and during dry weather.
            SnowClock=0;
            foreach(var chunk in chunks)
            {
                chunk.LastStamp=float.NegativeInfinity;
                foreach(var uv in chunk.Uv) chunk.LastStamp=Mathf.Max(chunk.LastStamp,uv.y);
            }
        }

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
                    chunk.Include(end-width);chunk.Include(end+width);
                    var mark=chunk.Marks[n/4];mark.B=end;contacts.Update(mark);
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
                    foreach(var mark in chunk.Marks)contacts.Remove(mark);chunk.Marks.Clear();
                    chunk.Vertices.Clear(); chunk.Uv.Clear(); chunk.Indices.Clear(); chunk.Mesh.Clear(); chunk.Generation++;
                    chunk.HasBounds=false;
                }
                else chunk=new Chunk();
                chunks.Add(chunk);
            }
            int n=chunk.Vertices.Count;
            var contactMark=new RailSnowContactIndex.Mark {A=a,B=b,Width=width,Stamp=SnowClock};
            chunk.Marks.Add(contactMark);contacts.Update(contactMark);
            wheel.Chunks[side]=chunk;wheel.Ends[side]=n+2;wheel.Generations[side]=chunk.Generation;
            chunk.Vertices.Add(a-width); chunk.Vertices.Add(a+width);
            chunk.Vertices.Add(b-width); chunk.Vertices.Add(b+width);
            chunk.Include(a-width);chunk.Include(a+width);chunk.Include(b-width);chunk.Include(b+width);
            chunk.Uv.Add(new Vector2(-1,SnowClock)); chunk.Uv.Add(new Vector2(1,SnowClock));
            chunk.Uv.Add(new Vector2(-1,SnowClock)); chunk.Uv.Add(new Vector2(1,SnowClock));
            chunk.Indices.Add(n);chunk.Indices.Add(n+2);chunk.Indices.Add(n+1);
            chunk.Indices.Add(n+1);chunk.Indices.Add(n+2);chunk.Indices.Add(n+3);
            chunk.Dirty=chunk.TopologyDirty=true; chunk.LastStamp=SnowClock; SegmentCount++;
        }
        public bool HasVisibleTracks(Plane[] frustum, Plane[] secondaryFrustum = null)
        {
            foreach(var chunk in chunks)
            {
                if(SnowClock-chunk.LastStamp>=1f) continue;
                var bounds=chunk.Bounds;bounds.center+=WorldOffset;
                if(SnowCameraFrustum.Intersects(frustum,secondaryFrustum,bounds)) return true;
            }
            return false;
        }
        public void Record(CommandBuffer buffer, Material material, Plane[] frustum, Plane[] secondaryFrustum = null)
        {
            buffer.SetGlobalFloat("_DVPSRailSnowClock",SnowClock);
            var matrix=Matrix4x4.Translate(WorldOffset);
            foreach(var chunk in chunks)
            {
                if(SnowClock-chunk.LastStamp>=1f) continue;
                var bounds=chunk.Bounds; bounds.center+=WorldOffset;
                if(!SnowCameraFrustum.Intersects(frustum,secondaryFrustum,bounds)) continue;
                if(chunk.Dirty)
                {
                    chunk.Mesh.SetVertices(chunk.Vertices);
                    if(chunk.TopologyDirty)
                    {chunk.Mesh.SetUVs(0,chunk.Uv);chunk.Mesh.SetTriangles(chunk.Indices,0,false);chunk.TopologyDirty=false;}
                    chunk.Mesh.bounds=chunk.Bounds;
                    chunk.Dirty=false;
                    MeshUploadCount++;
                }
                buffer.DrawMesh(chunk.Mesh,matrix,material,0,2);
            }
        }
        public void Dispose()
        {
            foreach(var c in chunks)
                if(Application.isPlaying) UnityEngine.Object.Destroy(c.Mesh); else UnityEngine.Object.DestroyImmediate(c.Mesh);
            chunks.Clear(); previous.Clear(); contacts.Clear(); SnowClock=0; SegmentCount=0; MeshUploadCount=0;
        }
    }
}
