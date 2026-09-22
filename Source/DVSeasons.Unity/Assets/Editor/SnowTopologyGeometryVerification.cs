using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    // Exercise the production helper through reflection. The geometry reference
    // enumerates all eight corners; it does not reuse its absolute-matrix formula.
    public static class SnowTopologyGeometryVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static Type helperType,performanceType;
        static int checks,frames;

        sealed class Envelope
        {
            readonly object value;
            readonly MethodInfo include,finish,update,ensure,reset,contains;
            public Envelope()
            {
                value=Activator.CreateInstance(helperType,true);
                include=helperType.GetMethod("Include",All);finish=helperType.GetMethod("Finish",All);
                update=helperType.GetMethod("Update",All);ensure=helperType.GetMethod("EnsureContains",All);
                reset=helperType.GetMethod("Reset",All);contains=helperType.GetMethod("ContainsGeometry",All);
            }
            public Bounds Local {get {return (Bounds)helperType.GetField("local",All).GetValue(value);}}
            public Bounds Exact {get {return (Bounds)helperType.GetProperty("ExactWorld",All).GetValue(value,null);}}
            public Bounds Published {get {return (Bounds)helperType.GetProperty("World",All).GetValue(value,null);}}
            public float Padding {get {return (float)helperType.GetProperty("Padding",All).GetValue(value,null);}}
            public bool Initialized {get {return (bool)helperType.GetProperty("Initialized",All).GetValue(value,null);}}
            public void Include(Bounds source,Matrix4x4 relative) {include.Invoke(value,new object[]{source,relative});}
            public void Finish() {finish.Invoke(value,null);}
            public void Update(Matrix4x4 pose) {update.Invoke(value,new object[]{pose});}
            public void Ensure(Bounds native,float padding) {ensure.Invoke(value,new object[]{native,padding});}
            public void Reset() {reset.Invoke(value,null);}
            public bool Contains(Bounds bounds) {return (bool)contains.Invoke(value,new object[]{bounds});}
        }

        public static void Run()
        {
            int result=0;
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string mod=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME")??"F:/steam/steamapps/common/Derail Valley";
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,args)=> {
                foreach(string directory in new[]{mod,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {string path=Path.Combine(directory,new AssemblyName(args.Name).Name+".dll");if(File.Exists(path))return Assembly.LoadFrom(path);}return null;};
            try
            {
                Assembly assembly=Assembly.LoadFrom(Path.Combine(mod,"DVSeasons.dll"));
                helperType=assembly.GetType("DVSeasons.Mod.SnowVehicleTopologyBounds",true);
                performanceType=assembly.GetType("DVSeasons.Mod.SnowPerformance",true);
                checks=frames=0;
                VerifyDiagonalAndStableGuard();
                VerifyJitter();
                VerifyTranslationRotationAndOrigin();
                VerifyAffineGeometry();
                VerifyAnimatedGeometryAndReset();
                VerifyWideNativeGuard();
                VerifyParallelYard();
                Debug.Log("SNOW_TOPOLOGY_GEOMETRY_OK: "+checks+" checks across "+frames+" poses; independent corner reference, diagonal-car inflation avoided, 6000 changing poses with zero publications after warmup, real motion/rotation/reversal/origin shift, shear/mirrors/nonuniform scales, animated parts/native guard/padding/reset.");
            }
            catch(Exception exception) {Debug.LogException(exception);result=1;}
            EditorApplication.Exit(result);
        }

        static void Require(bool condition,string message)
        {checks++;if(!condition)throw new Exception(message);}

        static Bounds Corners(Bounds source,Matrix4x4 transform)
        {
            Vector3 low=source.min,high=source.max;
            Vector3 first=transform.MultiplyPoint3x4(low);
            Bounds result=new Bounds(first,Vector3.zero);
            for(int i=1;i<8;i++)result.Encapsulate(transform.MultiplyPoint3x4(new Vector3(
                (i&1)==0?low.x:high.x,(i&2)==0?low.y:high.y,(i&4)==0?low.z:high.z)));
            return result;
        }

        static bool Contains(Bounds outer,Bounds inner,float tolerance=.002f)
        {
            Vector3 lo=inner.min,hi=inner.max,min=outer.min,max=outer.max;
            return lo.x>=min.x-tolerance && lo.y>=min.y-tolerance && lo.z>=min.z-tolerance &&
                hi.x<=max.x+tolerance && hi.y<=max.y+tolerance && hi.z<=max.z+tolerance;
        }

        static void EqualBounds(Bounds expected,Bounds actual,string context,float tolerance=.002f)
        {
            Require((expected.min-actual.min).sqrMagnitude<=tolerance*tolerance &&
                (expected.max-actual.max).sqrMagnitude<=tolerance*tolerance,
                context+": expected "+expected+", got "+actual);
        }

        static float Volume(Bounds bounds) {Vector3 size=bounds.size;return size.x*size.y*size.z;}
        static long Counter(string name) {return (long)performanceType.GetField(name,All).GetValue(null);}

        static Envelope New(Bounds source,Matrix4x4 relative,Matrix4x4 pose)
        {
            var envelope=new Envelope();envelope.Include(source,relative);envelope.Finish();envelope.Update(pose);
            return envelope;
        }

        static void CheckPose(Envelope envelope,Matrix4x4 pose,Bounds native,float padding,string context)
        {
            frames++;envelope.Update(pose);envelope.Ensure(native,padding);
            EqualBounds(Corners(envelope.Local,pose),envelope.Exact,context+" exact world",.012f);
            Bounds padded=envelope.Exact;padded.Encapsulate(native);padded.Expand(envelope.Padding*2f);
            Require(Contains(envelope.Published,padded),context+" lost geometric or shader-padded bounds");
        }

        static void VerifyDiagonalAndStableGuard()
        {
            Bounds mesh=new Bounds(new Vector3(.35f,1.2f,-.6f),new Vector3(3f,4f,25f));
            Matrix4x4 pose=Matrix4x4.TRS(new Vector3(321.25f,17f,-487.75f),Quaternion.Euler(0f,45f,0f),Vector3.one);
            var envelope=New(mesh,Matrix4x4.identity,pose);
            Bounds expectedLocal=mesh;expectedLocal.Expand(.04f);
            EqualBounds(expectedLocal,envelope.Local,"Direct mesh-to-vehicle local bounds");
            Bounds native=Corners(mesh,pose);
            Bounds oldLocal=Corners(native,pose.inverse);
            Bounds oldWorld=Corners(oldLocal,pose);
            Require(Volume(oldLocal)>Volume(envelope.Local)*4f,"Diagonal reference did not reproduce old inverse-AABB inflation");
            Require(Volume(oldWorld)>Volume(envelope.Exact)*3f,"Diagonal world reference was not materially inflated");
            Bounds original=envelope.Local;
            for(int frame=0;frame<500;frame++)
            {
                // Same visible guard used by the registry. Re-evaluate precise
                // geometry only if its native bound exceeds the cached envelope.
                if(!envelope.Contains(native))envelope.Include(mesh,Matrix4x4.identity);
                CheckPose(envelope,pose,native,.12f,"Stable diagonal frame "+frame);
            }
            Require(envelope.Local.Equals(original),"Stable visible guard grew the local envelope repeatedly");
            Debug.Log("SNOW_TOPOLOGY_DIAGONAL: old/new local volume="+Volume(oldLocal)+"/"+Volume(envelope.Local)+
                ", old/new world volume="+Volume(oldWorld)+"/"+Volume(envelope.Exact));
        }

        static void VerifyJitter()
        {
            Bounds source=new Bounds(new Vector3(.2f,2.7f,-.7f),new Vector3(3.2f,4.5f,26f));
            Vector3 position=new Vector3(532.25f,18.5f,-371.75f);
            Matrix4x4 pose=Matrix4x4.TRS(position,Quaternion.Euler(2.1f,31.5f,.3f),Vector3.one);
            var envelope=New(source,Matrix4x4.identity,pose);
            CheckPose(envelope,pose,Corners(source,pose),.2f,"Jitter warmup");
            Bounds published=envelope.Published,local=envelope.Local;
            long poses=Counter("topologyPoses"),publications=Counter("topologyPublications"),expands=Counter("topologyEnvelopes");
            for(int frame=1;frame<=6000;frame++)
            {
                float phase=frame*.071f;
                Vector3 offset=new Vector3(Mathf.Sin(phase),Mathf.Cos(phase*.87f),Mathf.Sin(phase*.63f))*.025f;
                pose=Matrix4x4.TRS(position+offset,Quaternion.Euler(2.1f+Mathf.Sin(phase)*.02f,
                    31.5f+Mathf.Cos(phase)*.02f,.3f+Mathf.Sin(phase*.7f)*.02f),Vector3.one);
                CheckPose(envelope,pose,Corners(source,pose),.2f,"Jitter frame "+frame);
                Require(envelope.Published.Equals(published),"Sub-margin jitter changed published bounds at "+frame);
            }
            Require(Counter("topologyPoses")-poses==6000,"Pose counter did not record each changed jitter matrix");
            Require(Counter("topologyPublications")==publications,"Jitter published new bounds after warmup");
            Require(Counter("topologyEnvelopes")==expands && envelope.Local.Equals(local),"Pose jitter changed local geometry");
            Debug.Log("SNOW_TOPOLOGY_JITTER: changing poses=6000, publications="+(Counter("topologyPublications")-publications));
        }

        static void VerifyTranslationRotationAndOrigin()
        {
            Bounds source=new Bounds(new Vector3(.6f,1.1f,-1.3f),new Vector3(3.1f,4f,22f));
            Vector3 position=new Vector3(-217.25f,23.5f,618.75f);
            Quaternion orientation=Quaternion.Euler(1.5f,37f,.4f);
            Matrix4x4 pose=Matrix4x4.TRS(position,orientation,Vector3.one);
            var envelope=New(source,Matrix4x4.identity,pose);
            CheckPose(envelope,pose,Corners(source,pose),.1f,"Translation warmup");
            int lastPublication=0,publications=0;
            Bounds previous=envelope.Published;
            for(int step=1;step<=400;step++)
            {
                pose=Matrix4x4.TRS(position+Vector3.right*(step*.01f),orientation,Vector3.one);
                CheckPose(envelope,pose,Corners(source,pose),.1f,"Translation step "+step);
                if(!envelope.Published.Equals(previous))
                {
                    int gap=step-lastPublication;
                    Require(gap>=24 && gap<=27,"Translation publication did not retain the quarter-metre margin: "+gap+" cm");
                    previous=envelope.Published;lastPublication=step;publications++;
                }
            }
            Require(publications>=14 && publications<=16,"Unexpected publication count over four metres: "+publications);
            for(int step=399;step>=0;step--)
            {
                pose=Matrix4x4.TRS(position+Vector3.right*(step*.01f),orientation,Vector3.one);
                CheckPose(envelope,pose,Corners(source,pose),.1f,"Reverse translation step "+step);
            }
            long beforeRotation=Counter("topologyPublications");
            for(int step=1;step<=360;step++)
            {
                pose=Matrix4x4.TRS(position,Quaternion.Euler(1.5f,37f+step*.25f,.4f),Vector3.one);
                CheckPose(envelope,pose,Corners(source,pose),.1f,"Rotation step "+step);
            }
            Require(Counter("topologyPublications")>beforeRotation,"True rotation never republished bounds");
            previous=envelope.Published;
            pose=Matrix4x4.TRS(position+new Vector3(-10000f,270f,12000f),Quaternion.Euler(13f,-141f,-7f),Vector3.one);
            CheckPose(envelope,pose,Corners(source,pose),.1f,"Floating-origin shift");
            Require(!envelope.Published.Equals(previous),"Large origin shift retained old world bounds");
        }

        static Matrix4x4 Shear(float xy,float xz,float yz)
        {Matrix4x4 matrix=Matrix4x4.identity;matrix.m01=xy;matrix.m02=xz;matrix.m12=yz;return matrix;}

        static void VerifyAffineGeometry()
        {
            Bounds first=new Bounds(new Vector3(.7f,1.4f,-2.8f),new Vector3(2.8f,3.2f,19f));
            Bounds second=new Bounds(new Vector3(-.4f,.2f,1.5f),new Vector3(.6f,1.7f,3.1f));
            for(int scenario=0;scenario<24;scenario++)
            {
                Matrix4x4 relative=Matrix4x4.TRS(new Vector3(1.3f,-.6f,4.4f),Quaternion.Euler(scenario*7f,23f,-11f),
                    new Vector3((scenario&1)==0?-1.2f:1.2f,.7f,1.8f))*Shear(.17f,-.23f,.31f);
                Matrix4x4 other=Matrix4x4.TRS(new Vector3(-2.6f,3.2f,-6.1f),Quaternion.Euler(-9f,scenario*13f,16f),
                    new Vector3(.8f,(scenario&2)==0?-1.4f:1.4f,1.1f));
                Matrix4x4 pose=Matrix4x4.TRS(new Vector3(129f+scenario*83f,-31f+scenario,702f-scenario*37f),
                    Quaternion.Euler(7f,scenario*17f,-4f),new Vector3(.9f,1.1f,(scenario&4)==0?-1.3f:1.3f))*Shear(-.11f,.09f,.14f);
                var envelope=new Envelope();envelope.Include(first,relative);envelope.Include(second,other);envelope.Finish();
                Bounds expected=Corners(first,relative);expected.Encapsulate(Corners(second,other));expected.Expand(.04f);
                EqualBounds(expected,envelope.Local,"Affine local union "+scenario);
                Bounds native=Corners(first,pose*relative);native.Encapsulate(Corners(second,pose*other));
                CheckPose(envelope,pose,native,.38f,"Affine world union "+scenario);
                Require(Contains(envelope.Published,Corners(first,pose*relative)),"Affine first mesh escaped");
                Require(Contains(envelope.Published,Corners(second,pose*other)),"Affine second mesh escaped");
            }
        }

        static void VerifyAnimatedGeometryAndReset()
        {
            Bounds car=new Bounds(new Vector3(.3f,2f,-.7f),new Vector3(3f,4f,20f));
            Bounds part=new Bounds(new Vector3(.1f,.3f,-.2f),new Vector3(.3f,1.4f,1.8f));
            Matrix4x4 pose=Matrix4x4.TRS(new Vector3(413f,32f,-877f),Quaternion.Euler(4f,53f,-2f),Vector3.one);
            var envelope=New(car,Matrix4x4.identity,pose);
            Bounds original=envelope.Local;
            long expands=Counter("topologyEnvelopes");
            for(int step=0;step<80;step++)
            {
                Matrix4x4 animation=Matrix4x4.TRS(new Vector3(2f+step*.2f,2.3f,-2f),Quaternion.Euler(0f,step*1.3f,0f),Vector3.one);
                Bounds native=Corners(car,pose);native.Encapsulate(Corners(part,pose*animation));
                bool refreshed=!envelope.Contains(native);
                if(refreshed)envelope.Include(part,animation);
                CheckPose(envelope,pose,native,.1f,"Animated or detached part "+step);
                // A rotated world AABB can safely contain geometry outside the
                // cached local OBB. Refresh is needed only once world bounds fail.
                if(refreshed)Require(Contains(envelope.Local,Corners(part,animation)),"Animated mesh did not expand the cached local union");
            }
            Require(Counter("topologyEnvelopes")>expands && !envelope.Local.Equals(original),"Animated geometry did not record local expansion");
            Bounds local=envelope.Local;long padding=Counter("topologyPadding");
            Bounds visible=Corners(local,pose);
            CheckPose(envelope,pose,visible,1.3f,"Shader depth padding growth");
            Require(envelope.Padding>=1.3f && Counter("topologyPadding")==padding+1,"Shader padding did not grow exactly once");
            float largePadding=envelope.Padding;Bounds published=envelope.Published;
            for(int step=0;step<200;step++)CheckPose(envelope,pose,visible,.01f,"Stable padded envelope "+step);
            Require(envelope.Padding==largePadding && envelope.Published.Equals(published),"Camera returning nearby shrank or republished padding");
            Require(envelope.Local.Equals(local),"Shader padding contaminated local geometry");
            envelope.Reset();
            Require(!envelope.Initialized && envelope.Padding==.125f && envelope.Published.Equals(default(Bounds)),"Reset retained published geometry or padding");
            Bounds replacement=new Bounds(new Vector3(-2f,1.3f,8f),new Vector3(1f,2f,3f));
            envelope.Include(replacement,Matrix4x4.identity);envelope.Finish();
            pose=Matrix4x4.TRS(new Vector3(-2031f,51f,4432f),Quaternion.Euler(0f,-79f,0f),Vector3.one);
            CheckPose(envelope,pose,Corners(replacement,pose),.1f,"Geometry after reset");
            replacement.Expand(.04f);EqualBounds(replacement,envelope.Local,"Reset retained old local union");
        }

        static void VerifyWideNativeGuard()
        {
            Bounds source=new Bounds(new Vector3(.2f,2f,-.8f),new Vector3(3f,4f,21f));
            Matrix4x4 pose=Matrix4x4.TRS(new Vector3(615f,11f,-123f),Quaternion.Euler(0f,47f,0f),Vector3.one);
            var envelope=New(source,Matrix4x4.identity,pose);
            Bounds local=envelope.Local,native=Corners(source,pose);native.Expand(new Vector3(12f,3f,7f));
            for(int frame=0;frame<500;frame++)
            {
                // Model a larger native skinned/static-batch/custom-stream box.
                // The guard may publish it, but cannot feed it back into local.
                if(!envelope.Contains(native))envelope.Include(source,Matrix4x4.identity);
                CheckPose(envelope,pose,native,.3f,"Oversized native guard "+frame);
            }
            Require(envelope.Local.Equals(local),"Repeated native-world guard inflated mesh-local geometry");
        }

        static int Overlaps(Bounds[] bounds)
        {
            int count=0;
            for(int first=0;first<bounds.Length;first++)
                for(int second=first+1;second<bounds.Length;second++)
                    if(bounds[first].Intersects(bounds[second]))count++;
            return count;
        }

        static void VerifyParallelYard()
        {
            const int rows=16,cars=14,total=rows*cars;
            var exact=new Bounds[total];var published=new Bounds[total];var legacy=new Bounds[total];
            Bounds source=new Bounds(new Vector3(.1f,2.1f,-.3f),new Vector3(3.2f,4.2f,22f));
            Quaternion rotation=Quaternion.Euler(0f,37f,0f);
            for(int row=0;row<rows;row++)for(int car=0;car<cars;car++)
            {
                int index=row*cars+car;
                Vector3 position=new Vector3(613f,23f,-817f)+rotation*new Vector3(row*5.4f,0f,car*24.7f);
                Matrix4x4 pose=Matrix4x4.TRS(position,rotation,Vector3.one);
                var envelope=New(source,Matrix4x4.identity,pose);
                exact[index]=Corners(source,pose);
                CheckPose(envelope,pose,exact[index],.1f,"Parallel yard car "+index);
                published[index]=envelope.Published;
                Bounds oldLocal=Corners(exact[index],pose.inverse);oldLocal.Expand(.04f);
                legacy[index]=Corners(oldLocal,pose);legacy[index].Expand(.25f);
            }
            int exactPairs=Overlaps(exact),newPairs=Overlaps(published),oldPairs=Overlaps(legacy);
            Require(newPairs>=exactPairs,"Yard published bounds omitted an exact-world-AABB overlap");
            Require(newPairs<oldPairs,"Direct local geometry did not reduce inflated AABB overlaps in a diagonal yard");
            Debug.Log("SNOW_TOPOLOGY_PARALLEL_YARD: cars="+total+", candidate pairs="+(total*(total-1)/2)+
                ", overlapping world AABBs exact/new-padded/old-roundtrip="+exactPairs+"/"+newPairs+"/"+oldPairs+
                "; geometric count only, not an in-game timing estimate.");
        }
    }
}
