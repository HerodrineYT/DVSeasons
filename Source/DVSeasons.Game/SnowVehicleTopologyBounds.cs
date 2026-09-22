using UnityEngine;

namespace DVSeasons.Mod
{
    // Separate live geometric bounds from the conservative box published to the
    // scheduler. Exact draw matrices still update every frame; small physical
    // motion inside the published box cannot invalidate overlap topology.
    internal sealed class SnowVehicleTopologyBounds
    {
        private const float MinimumPadding=.125f;
        private const float MotionMargin=.25f;
        private Bounds local, world, published;
        private Matrix4x4 matrix;
        private bool hasLocal, worldKnown, matrixKnown, publishedKnown;
        private float padding=MinimumPadding;
        public bool Initialized {get; private set;}
        public Bounds World => published;
        internal Bounds ExactWorld => world;
        internal Bounds Local => local;
        internal float Padding => padding;
        public void Reset()
        {hasLocal=worldKnown=matrixKnown=publishedKnown=Initialized=false;padding=MinimumPadding;local=world=published=default(Bounds);}
        public void Include(Bounds sourceBounds,Matrix4x4 sourceToVehicle)
        {
            // sourceBounds is mesh/localBounds, not the renderer's world AABB.
            var converted=Transform(sourceBounds,sourceToVehicle);
            if(hasLocal && Contains(local,converted))return;
            if(hasLocal)local.Encapsulate(converted);
            else {local=converted;hasLocal=true;}
            if(Initialized) {local.Expand(.04f);SnowPerformance.Topology(0,1,0,0);}
            worldKnown=false;
        }
        public void Finish()
        {
            if(!hasLocal)local=new Bounds(Vector3.zero,Vector3.zero);
            // Covers roundoff from world->local->world at distant positions;
            // actual bounds are still checked before any visible draws are queued.
            local.Expand(.04f);hasLocal=true;Initialized=true;worldKnown=false;
        }
        public void Update(Matrix4x4 localToWorld)
        {
            bool same=matrixKnown && SnowVehicleDrawScheduler.SameMatrix(ref matrix,ref localToWorld);
            if(worldKnown && same)return;
            if(matrixKnown && !same)SnowPerformance.Topology(1,0,0,0);
            matrix=localToWorld;matrixKnown=true;world=Transform(local,localToWorld);worldKnown=true;
            Publish(world);
        }
        public bool ContainsGeometry(Bounds visibleGeometry) {return Contains(world,visibleGeometry);}
        public void EnsureContains(Bounds visibleGeometry,float requiredPadding)
        {
            // Shader depth tolerance depends on the viewing projection/distance.
            // Keep a high-water bound, never shrink it when the camera comes back.
            float before=padding;
            while(padding<requiredPadding)padding*=2f;
            if(padding!=before)SnowPerformance.Topology(0,0,1,0);
            // The native renderer can have a wider animated/static-batch bound.
            // Keep the world-space guard, without an inverse-AABB roundtrip that
            // permanently inflates the local geometry on rotated cars.
            var required=world;required.Encapsulate(visibleGeometry);Publish(required);
        }
        private void Publish(Bounds required)
        {
            required.Expand(2f*padding);
            if(publishedKnown && Contains(published,required))return;
            published=required;published.Expand(2f*MotionMargin);publishedKnown=true;
            SnowPerformance.Topology(0,0,0,1);
        }
        private static bool Contains(Bounds outer,Bounds inner)
        {
            var lo=inner.min;var hi=inner.max;var min=outer.min;var max=outer.max;
            return lo.x>=min.x && lo.y>=min.y && lo.z>=min.z && hi.x<=max.x && hi.y<=max.y && hi.z<=max.z;
        }
        private static Bounds Transform(Bounds bounds,Matrix4x4 transform)
        {
            var center=transform.MultiplyPoint3x4(bounds.center);var e=bounds.extents;
            // Exact AABB of all eight transformed corners for an affine matrix,
            // including nonuniform/mirrored scale and parent-induced shear.
            var extent=new Vector3(Mathf.Abs(transform.m00)*e.x+Mathf.Abs(transform.m01)*e.y+Mathf.Abs(transform.m02)*e.z,
                Mathf.Abs(transform.m10)*e.x+Mathf.Abs(transform.m11)*e.y+Mathf.Abs(transform.m12)*e.z,
                Mathf.Abs(transform.m20)*e.x+Mathf.Abs(transform.m21)*e.y+Mathf.Abs(transform.m22)*e.z);
            return new Bounds(center,extent*2f);
        }
    }
}
