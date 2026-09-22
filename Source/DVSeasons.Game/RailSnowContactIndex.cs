using System.Collections.Generic;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Index the same ribbons that remove snow in SnowVehicle.shader. No physics
    // query or GPU readback is needed to identify a previously cleared rail head.
    internal sealed class RailSnowContactIndex
    {
        internal sealed class Mark
        {
            public Vector3 A, B, Width;
            public float Stamp;
            public readonly HashSet<Vector3Int> Cells = new HashSet<Vector3Int>();
            internal Vector3 Axis, WidthDirection;
            internal float LengthSquared, HalfWidth;
            internal Vector3Int IndexedMin, IndexedMax;
            internal bool Indexed;
        }
        private readonly Dictionary<Vector3Int, HashSet<Mark>> cells = new Dictionary<Vector3Int, HashSet<Mark>>();
        private static Vector3Int Cell(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x/8),Mathf.FloorToInt(p.y/8),Mathf.FloorToInt(p.z/8));
        public void Update(Mark mark)
        {
            // Ribbons are sampled by every following axle. Their derived
            // geometry changes only when an endpoint/width is updated.
            mark.Axis=mark.B-mark.A;mark.LengthSquared=mark.Axis.sqrMagnitude;
            mark.HalfWidth=mark.Width.magnitude;mark.WidthDirection=mark.Width.normalized;
            var extent=new Vector3(mark.HalfWidth,.12f,mark.HalfWidth);
            var min=Cell(Vector3.Min(mark.A,mark.B)-extent);var max=Cell(Vector3.Max(mark.A,mark.B)+extent);
            if(mark.Indexed && min==mark.IndexedMin && max==mark.IndexedMax)return;
            mark.Indexed=true;mark.IndexedMin=min;mark.IndexedMax=max;
            for(int x=min.x;x<=max.x;x++)for(int y=min.y;y<=max.y;y++)for(int z=min.z;z<=max.z;z++)
            {
                var key=new Vector3Int(x,y,z);if(!mark.Cells.Add(key))continue;
                HashSet<Mark> bucket;if(!cells.TryGetValue(key,out bucket))cells.Add(key,bucket=new HashSet<Mark>());
                bucket.Add(mark);
            }
        }
        public void Remove(Mark mark)
        {
            foreach(var key in mark.Cells)
            {var bucket=cells[key];bucket.Remove(mark);if(bucket.Count==0)cells.Remove(key);}
            mark.Cells.Clear();
            mark.Indexed=false;
        }
        public float Remaining(Vector3 point,float clock)
        {
            HashSet<Mark> bucket;if(!cells.TryGetValue(Cell(point),out bucket))return 1;
            float remaining=1;
            foreach(var mark in bucket)
            {
                float age=Mathf.Clamp01(clock-mark.Stamp);if(age>=remaining)continue;
                if(mark.LengthSquared<.000001f)continue;
                float t=Vector3.Dot(point-mark.A,mark.Axis)/mark.LengthSquared;if(t<0 || t>1)continue;
                var delta=point-Vector3.Lerp(mark.A,mark.B,t);
                float width=mark.HalfWidth;
                if(width<.00001f)continue;
                float across=Vector3.Dot(delta,mark.WidthDirection);
                if((delta-mark.WidthDirection*across).sqrMagnitude>.12f*.12f)continue;
                float edge=Mathf.InverseLerp(.65f,1,Mathf.Abs(across)/width);
                float clear=(1-Mathf.SmoothStep(0,1,edge))*(1-age);
                remaining=Mathf.Min(remaining,1-clear);
                if(remaining<=.001f)return 0;
            }
            return remaining;
        }
        public void Clear() => cells.Clear();
    }
}
