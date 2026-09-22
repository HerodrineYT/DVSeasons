using System;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Debug=UnityEngine.Debug;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowCullingOptimizationVerification
    {
        // This candidate passed parity but was 3.03–3.81x slower than native
        // Unity in all six measured workloads. Keep it only as reproducible
        // Editor evidence; production continues to use TestPlanesAABB.
        sealed class RejectedPrepared
        {
            struct Equation {public float X,Y,Z,D,AX,AY,AZ,AD;}
            readonly Equation[] first=new Equation[6],second=new Equation[6];
            Plane[] primary,secondary;
            static void Copy(Plane[] planes,Equation[] target)
            {
                for(int i=0;i<6;i++)
                {
                    var p=planes[i];var n=p.normal;
                    target[i]=new Equation {X=n.x,Y=n.y,Z=n.z,D=p.distance,
                        AX=Mathf.Abs(n.x),AY=Mathf.Abs(n.y),AZ=Mathf.Abs(n.z),AD=Mathf.Abs(p.distance)};
                }
            }
            public void Set(Plane[] left,Plane[] right)
            {primary=left;secondary=right;Copy(left,first);if(right!=null)Copy(right,second);}
            public bool Intersects(Bounds bounds)
            {return Test(first,primary,bounds)||(secondary!=null && Test(second,secondary,bounds));}
            static bool Test(Equation[] planes,Plane[] native,Bounds bounds)
            {
                var c=bounds.center;var e=bounds.extents;
                if(!(e.x>=0 && e.y>=0 && e.z>=0) ||
                    float.IsNaN(c.x)||float.IsNaN(c.y)||float.IsNaN(c.z)||
                    float.IsInfinity(c.x)||float.IsInfinity(c.y)||float.IsInfinity(c.z)||
                    float.IsInfinity(e.x)||float.IsInfinity(e.y)||float.IsInfinity(e.z))
                    return GeometryUtility.TestPlanesAABB(native,bounds);
                float ax=Mathf.Abs(c.x)+e.x,ay=Mathf.Abs(c.y)+e.y,az=Mathf.Abs(c.z)+e.z;
                for(int i=0;i<6;i++)
                {
                    var p=planes[i];
                    float support=p.X*c.x+p.Y*c.y+p.Z*c.z+p.D+p.AX*e.x+p.AY*e.y+p.AZ*e.z;
                    float tolerance=(p.AX*ax+p.AY*ay+p.AZ*az+p.AD)*.000001f+.000001f;
                    if(support < -tolerance)return false;
                    if(!(support>tolerance))return GeometryUtility.TestPlanesAABB(native,bounds);
                }
                return true;
            }
        }
        static long checks;
        static void Require(bool condition,string message) {if(!condition)throw new Exception(message);}
        public static void Run()
        {
            int exit=0;
            try {Verify();}
            catch(Exception error) {Debug.LogException(error);exit=1;}
            EditorApplication.Exit(exit);
        }
        static float Range(System.Random random,float minimum,float maximum)
        {return minimum+(maximum-minimum)*(float)random.NextDouble();}
        static void Compare(Func<Bounds,bool> current,Func<Bounds,bool> native,Bounds bounds,string scenario)
        {
            bool expected=native(bounds),actual=current(bounds);checks++;
            if(expected!=actual)throw new Exception("Prepared frustum mismatch "+scenario+" native="+expected+" prepared="+actual+" bounds="+bounds.ToString("G9"));
        }
        static void Verify()
        {
            var prepared=new RejectedPrepared();
            Action<Plane[],Plane[]> set=prepared.Set;Func<Bounds,bool> test=prepared.Intersects;
            var left=new Plane[6];var right=new Plane[6];bool stereo=false;
            Func<Bounds,bool> native=b=>GeometryUtility.TestPlanesAABB(left,b)||(stereo && GeometryUtility.TestPlanesAABB(right,b));
            var random=new System.Random(817241);
            foreach(float offset in new[]{0f,10000f,1000000f,100000000f})
            foreach(bool orthographic in new[]{false,true})
            foreach(bool twoEyes in new[]{false,true})
            for(int pose=0;pose<12;pose++)
            {
                stereo=twoEyes;float near=.015f+(pose%4)*.04f,far=100+(pose%5)*997;
                Vector3 position=new Vector3(offset,-offset*.3f,offset*.7f);
                Quaternion rotation=Quaternion.Euler(Range(random,-40,40),Range(random,-180,180),Range(random,-20,20));
                Matrix4x4 projection=orthographic?Matrix4x4.Ortho(-45,75,-37,43,near,far):
                    Matrix4x4.Frustum(-near*Range(random,.5f,2.1f),near*Range(random,.5f,2.1f),-near*.7f,near*1.4f,near,far);
                for(int eye=0;eye<2;eye++)
                {
                    var eyePosition=position+rotation*new Vector3(eye==0?-.032f:.032f,0,0);
                    Matrix4x4 view=Matrix4x4.Scale(new Vector3(1,1,-1))*Matrix4x4.TRS(eyePosition,rotation,Vector3.one).inverse;
                    var eyeProjection=projection;eyeProjection.m02+=(eye==0?-.027f:.027f);
                    GeometryUtility.CalculateFrustumPlanes(eyeProjection*view,eye==0?left:right);
                }
                set(left,stereo?right:null);
                string context="offset="+offset+" ortho="+orthographic+" stereo="+stereo+" pose="+pose;
                for(int i=0;i<4096;i++)
                {
                    var local=new Vector3(Range(random,-far*1.4f,far*1.4f),Range(random,-far*.7f,far*.7f),Range(random,-far*.3f,far*1.3f));
                    var extents=new Vector3(Range(random,0,40),Range(random,0,40),Range(random,0,40));
                    Compare(test,native,new Bounds(position+rotation*local,extents*2),context);
                }
                // Deliberately straddle every extracted plane by less than float
                // roundoff, exactly touch it, and move well across it. This tests
                // the native fallback instead of accepting over-conservative culls.
                foreach(var planes in new[]{left,right})
                for(int p=0;p<6;p++)
                foreach(float gap in new[]{-1f,-.001f,-.000001f,0f,.000001f,.001f,1f})
                {
                    Vector3 n=planes[p].normal,extents=new Vector3(.37f,1.9f,4.7f);
                    Vector3 center=position+rotation*new Vector3(0,0,(near+far)*.5f);
                    float support=Mathf.Abs(n.x)*extents.x+Mathf.Abs(n.y)*extents.y+Mathf.Abs(n.z)*extents.z;
                    center-=n*(planes[p].GetDistanceToPoint(center)+support-gap);
                    Compare(test,native,new Bounds(center,extents*2),context+" plane="+p+" gap="+gap);
                }
                foreach(float z in new[]{near-.001f,near,near+.001f,far-.001f,far,far+.001f})
                    Compare(test,native,new Bounds(position+rotation*new Vector3(0,0,z),Vector3.zero),context+" near/far point");
            }
            stereo=false;
            GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Perspective(70,1.7f,.1f,1000)*Matrix4x4.Scale(new Vector3(1,1,-1)),left);set(left,null);
            foreach(var invalid in new[]{
                new Bounds(new Vector3(float.NaN,0,5),Vector3.one),new Bounds(new Vector3(float.PositiveInfinity,0,5),Vector3.one),
                new Bounds(new Vector3(0,float.NegativeInfinity,5),Vector3.one),new Bounds(new Vector3(0,0,5),new Vector3(-1,1,1)),
                new Bounds(new Vector3(0,0,5),new Vector3(float.NaN,1,1)),new Bounds(new Vector3(0,0,5),new Vector3(float.PositiveInfinity,1,1)),
                new Bounds(new Vector3(1e30f,-1e30f,1e30f),Vector3.one*1e28f)})
                Compare(test,native,invalid,"invalid/extreme native fallback");
            Debug.Log("SNOW_PREPARED_CULLING_PARITY_OK: checks="+checks+" exact_native_answers=true; random perspective/orthographic off-axis stereo union, reused plane arrays, 0/10km/1000km/100000km origins, touching/near/far and invalid bounds; rejected_candidate_Editor_only=true cached_unboxed_delegate=true");
            foreach(bool twoEyes in new[]{false,true})
            {
                stereo=twoEyes;
                GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Perspective(70,1.7f,.1f,1000)*Matrix4x4.Scale(new Vector3(1,1,-1)),left);
                GeometryUtility.CalculateFrustumPlanes(Matrix4x4.Frustum(-.13f,.11f,-.07f,.07f,.1f,1000)*
                    Matrix4x4.Scale(new Vector3(1,1,-1))*Matrix4x4.Translate(new Vector3(-.064f,0,0)),right);set(left,stereo?right:null);
                foreach(int distribution in new[]{0,1,2})
                {
                    var boxes=new Bounds[8192];
                    for(int i=0;i<boxes.Length;i++)
                    {
                        Vector3 center=distribution==0?new Vector3(Range(random,-20,20),Range(random,-20,20),Range(random,60,150)):
                            distribution==1?new Vector3(Range(random,-1600,1600),Range(random,-800,800),Range(random,-500,1800)):
                            new Vector3(Range(random,-800,800),Range(random,-400,400),Range(random,20,1000));
                        boxes[i]=new Bounds(center,new Vector3(3,4,16));Compare(test,native,boxes[i],"benchmark inputs");
                    }
                    Benchmark(native,test,boxes,"stereo="+stereo+" distribution="+(distribution==0?"inside":distribution==1?"mostly-outside":"mixed"));
                }
            }
        }
        static long Timed(Func<Bounds,bool> test,Bounds[] boxes,int repeats,out double milliseconds)
        {
            long accepted=0;var timer=Stopwatch.StartNew();
            for(int repeat=0;repeat<repeats;repeat++)for(int i=0;i<boxes.Length;i++)if(test(boxes[i]))accepted++;
            timer.Stop();milliseconds=timer.Elapsed.TotalMilliseconds;return accepted;
        }
        static void Benchmark(Func<Bounds,bool> native,Func<Bounds,bool> prepared,Bounds[] boxes,string scenario)
        {
            const int Repeats=128,Trials=7;double ignored;Timed(native,boxes,4,out ignored);Timed(prepared,boxes,4,out ignored);
            var old=new double[Trials];var next=new double[Trials];long accepted=-1;
            for(int trial=0;trial<Trials;trial++)for(int run=0;run<2;run++)
            {
                bool usePrepared=((trial+run)&1)!=0;double elapsed;
                long count=Timed(usePrepared?prepared:native,boxes,Repeats,out elapsed);
                if(accepted<0)accepted=count;Require(count==accepted,"Benchmark accepted count differs");
                (usePrepared?next:old)[trial]=elapsed;
            }
            Array.Sort(old);Array.Sort(next);double tests=(double)boxes.Length*Repeats;
            Debug.Log("SNOW_PREPARED_CULLING_BENCH: "+scenario+" tests_per_trial="+tests+" native_ms="+old[Trials/2].ToString("F6")+
                " prepared_ms="+next[Trials/2].ToString("F6")+" ratio="+(next[Trials/2]/old[Trials/2]).ToString("F4")+
                " native_ns_per_box="+(old[Trials/2]*1e6/tests).ToString("F2")+" prepared_ns_per_box="+(next[Trials/2]*1e6/tests).ToString("F2")+
                " accepted="+accepted+" trials=7_alternating median=true; excludes_setup/reflection/allocations/GPU; synthetic_not_game_FPS=true");
        }
    }
}
