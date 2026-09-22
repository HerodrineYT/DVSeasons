using System;
using UnityEngine;

namespace DVSeasons.Mod
{
    // A conservative orthogonal box. The AABB sweep remains the broad phase;
    // this box only rejects pairs with a proven separating axis.
    internal struct SnowOrientedBounds
    {
        public Vector3 Center, AxisX, AxisY, AxisZ, Extents;
        public bool Valid;

        public static SnowOrientedBounds Create(Bounds local, Matrix4x4 transform, float padding)
        {
            var x=new Vector3(transform.m00,transform.m10,transform.m20);
            var y=new Vector3(transform.m01,transform.m11,transform.m21);
            if(x.sqrMagnitude<1e-12f || y.sqrMagnitude<1e-12f)return default(SnowOrientedBounds);
            x.Normalize();y-=x*Vector3.Dot(x,y);
            if(y.sqrMagnitude<1e-12f)return default(SnowOrientedBounds);
            y.Normalize();var z=Vector3.Cross(x,y).normalized;
            y=Vector3.Cross(z,x).normalized;
            var result=new SnowOrientedBounds {Center=transform.MultiplyPoint3x4(local.center),AxisX=x,AxisY=y,AxisZ=z,Valid=true};
            // Project all three affine half-edges, including mirrored scale and
            // shear. Treating lossyScale as exact would underbound sheared cars.
            result.Extents=new Vector3(Radius(local,transform,x),Radius(local,transform,y),Radius(local,transform,z))+Vector3.one*padding;
            result.Valid=Finite(result.Center) && Finite(result.Extents) && Finite(x) && Finite(y) && Finite(z) &&
                result.Extents.x>=0 && result.Extents.y>=0 && result.Extents.z>=0;
            return result;
        }

        private static bool Finite(Vector3 value)
        {return !float.IsNaN(value.x) && !float.IsInfinity(value.x) && !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);}
        private static float Radius(Bounds local,Matrix4x4 transform,Vector3 axis)
        {
            var e=local.extents;
            return Mathf.Abs(axis.x*transform.m00+axis.y*transform.m10+axis.z*transform.m20)*e.x+
                Mathf.Abs(axis.x*transform.m01+axis.y*transform.m11+axis.z*transform.m21)*e.y+
                Mathf.Abs(axis.x*transform.m02+axis.y*transform.m12+axis.z*transform.m22)*e.z;
        }
        private Vector3 Required(Bounds local,Matrix4x4 transform,float padding)
        {
            var delta=transform.MultiplyPoint3x4(local.center)-Center;
            return new Vector3(Mathf.Abs(Vector3.Dot(delta,AxisX))+Radius(local,transform,AxisX)+padding,
                Mathf.Abs(Vector3.Dot(delta,AxisY))+Radius(local,transform,AxisY)+padding,
                Mathf.Abs(Vector3.Dot(delta,AxisZ))+Radius(local,transform,AxisZ)+padding);
        }
        public bool Contains(Bounds local,Matrix4x4 transform,float padding)
        {
            if(!Valid)return false;
            var needed=Required(local,transform,padding);
            return Finite(needed) && needed.x<=Extents.x && needed.y<=Extents.y && needed.z<=Extents.z;
        }
        public SnowOrientedBounds Encapsulate(Bounds local,Matrix4x4 transform,float padding,float reserve)
        {
            if(!Valid)return this;
            var needed=Required(local,transform,padding);
            if(!Finite(needed)){var invalid=this;invalid.Valid=false;return invalid;}
            var result=this;
            if(needed.x>Extents.x)result.Extents.x=needed.x+reserve;
            if(needed.y>Extents.y)result.Extents.y=needed.y+reserve;
            if(needed.z>Extents.z)result.Extents.z=needed.z+reserve;
            return result;
        }
        public bool Same(SnowOrientedBounds other)
        {return Valid==other.Valid && Center.Equals(other.Center) && AxisX.Equals(other.AxisX) && AxisY.Equals(other.AxisY) && AxisZ.Equals(other.AxisZ) && Extents.Equals(other.Extents);}

        public static bool Intersects(SnowOrientedBounds a,SnowOrientedBounds b)
        {
            if(!a.Valid || !b.Valid)return true;
            var delta=b.Center-a.Center;
            if(Separated(a,b,delta,a.AxisX) || Separated(a,b,delta,a.AxisY) || Separated(a,b,delta,a.AxisZ) ||
                Separated(a,b,delta,b.AxisX) || Separated(a,b,delta,b.AxisY) || Separated(a,b,delta,b.AxisZ))return false;
            return !(Separated(a,b,delta,Vector3.Cross(a.AxisX,b.AxisX)) || Separated(a,b,delta,Vector3.Cross(a.AxisX,b.AxisY)) ||
                Separated(a,b,delta,Vector3.Cross(a.AxisX,b.AxisZ)) || Separated(a,b,delta,Vector3.Cross(a.AxisY,b.AxisX)) ||
                Separated(a,b,delta,Vector3.Cross(a.AxisY,b.AxisY)) || Separated(a,b,delta,Vector3.Cross(a.AxisY,b.AxisZ)) ||
                Separated(a,b,delta,Vector3.Cross(a.AxisZ,b.AxisX)) || Separated(a,b,delta,Vector3.Cross(a.AxisZ,b.AxisY)) ||
                Separated(a,b,delta,Vector3.Cross(a.AxisZ,b.AxisZ)));
        }
        private static double Dot(Vector3 a,Vector3 b) {return (double)a.x*b.x+(double)a.y*b.y+(double)a.z*b.z;}
        private static bool Separated(SnowOrientedBounds a,SnowOrientedBounds b,Vector3 delta,Vector3 axis)
        {
            double length=Math.Sqrt(Dot(axis,axis));
            if(length<1e-7)return false;
            double radius=Math.Abs(Dot(a.AxisX,axis))*a.Extents.x+Math.Abs(Dot(a.AxisY,axis))*a.Extents.y+Math.Abs(Dot(a.AxisZ,axis))*a.Extents.z+
                Math.Abs(Dot(b.AxisX,axis))*b.Extents.x+Math.Abs(Dot(b.AxisY,axis))*b.Extents.y+Math.Abs(Dot(b.AxisZ,axis))*b.Extents.z;
            // Touching/nearly parallel boxes stay ordered. The extra tolerance
            // is conservative and far smaller than the existing depth reserve.
            return Math.Abs(Dot(delta,axis))>radius+.001*length+radius*1e-6;
        }
    }

    // Companion to the proven AABB hysteresis. It never changes that envelope
    // or its publication policy; only its own SAT cache needs validation.
    internal sealed class SnowVehicleOrientedEnvelope
    {
        private const float Reserve=.25f;
        private SnowOrientedBounds world;
        private Bounds previousLocal;
        private Matrix4x4 previousMatrix;
        private float previousPadding;
        private bool known;
        public SnowOrientedBounds World => world;
        public int Revision {get;private set;}
        public void Reset(){known=false;world=default(SnowOrientedBounds);unchecked{Revision++;}}
        public void Update(Bounds local,Matrix4x4 matrix,float padding)
        {
            if(known && previousLocal.Equals(local) && previousPadding==padding &&
                SnowVehicleDrawScheduler.SameMatrix(ref previousMatrix,ref matrix))return;
            previousLocal=local;previousMatrix=matrix;previousPadding=padding;known=true;
            // Near-axis-aligned roots gain effectively nothing from SAT. Their
            // existing AABB is already tight: skip per-part OBB validation too.
            // This only retains extra ordering edges, never drops one.
            if(AxisAligned(matrix)){Publish(default(SnowOrientedBounds));return;}
            if(!world.Contains(local,matrix,padding))Publish(SnowOrientedBounds.Create(local,matrix,padding+Reserve));
        }
        public void EnsureContains(Bounds source,Matrix4x4 matrix,float padding)
        {Publish(world.Encapsulate(source,matrix,padding,Reserve));}
        private void Publish(SnowOrientedBounds value)
        {if(world.Same(value))return;world=value;unchecked{Revision++;}}
        private static bool AxisAligned(Matrix4x4 matrix)
        {
            return Aligned(matrix.m00,matrix.m10,matrix.m20) && Aligned(matrix.m01,matrix.m11,matrix.m21) && Aligned(matrix.m02,matrix.m12,matrix.m22);
        }
        private static bool Aligned(float x,float y,float z)
        {
            x*=x;y*=y;z*=z;float length=x+y+z;
            return length>1e-12f && Mathf.Max(x,Mathf.Max(y,z))>=length*.99999f;
        }
    }
}
