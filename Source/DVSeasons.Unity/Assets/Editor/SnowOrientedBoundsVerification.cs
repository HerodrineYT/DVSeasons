using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    // Geometry oracle: convex half-space feasibility in double precision, not
    // the runtime separating-axis formula. A bounded nonempty intersection has
    // a vertex among intersections of three of its twelve boundary planes.
    public static class SnowOrientedBoundsVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static Type orientedType,envelopeType;
        static int checks,pairs,overlaps,separated,crossAxisSeparations;

        struct D3
        {
            public double X,Y,Z;
            public D3(double x,double y,double z) {X=x;Y=y;Z=z;}
            public D3(Vector3 value) {X=value.x;Y=value.y;Z=value.z;}
            public static D3 operator +(D3 a,D3 b) {return new D3(a.X+b.X,a.Y+b.Y,a.Z+b.Z);}
            public static D3 operator -(D3 a,D3 b) {return new D3(a.X-b.X,a.Y-b.Y,a.Z-b.Z);}
            public static D3 operator *(D3 a,double b) {return new D3(a.X*b,a.Y*b,a.Z*b);}
            public static double Dot(D3 a,D3 b) {return a.X*b.X+a.Y*b.Y+a.Z*b.Z;}
            public static D3 Cross(D3 a,D3 b) {return new D3(a.Y*b.Z-a.Z*b.Y,a.Z*b.X-a.X*b.Z,a.X*b.Y-a.Y*b.X);}
        }

        struct PlaneD {public D3 Normal;public double Distance;}

        sealed class Box
        {
            public object Native;
            public bool Valid;
            public Vector3 Center,Extents;
            public readonly Vector3[] Axes=new Vector3[3];
            public Bounds Aabb;
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
                orientedType=assembly.GetType("DVSeasons.Mod.SnowOrientedBounds",true);
                envelopeType=assembly.GetType("DVSeasons.Mod.SnowVehicleOrientedEnvelope",true);
                checks=pairs=overlaps=separated=crossAxisSeparations=0;
                VerifyContacts();
                VerifyRandomPairs();
                VerifyTransformedGeometry();
                VerifyPublishedHysteresis();
                VerifyParallelYard();
                Debug.Log("SNOW_ORIENTED_BOUNDS_OK: checks="+checks+", independent half-space pairs="+pairs+
                    ", overlaps="+overlaps+", separated="+separated+", cross-axis-only separations="+crossAxisSeparations+
                    "; geometry/padding, contacts, mirrored/nonuniform/sheared transforms, tiny motion and real movement verified.");
            }
            catch(Exception exception) {Debug.LogException(exception);result=1;}
            EditorApplication.Exit(result);
        }

        static void Require(bool condition,string message) {checks++;if(!condition)throw new Exception(message);}
        static object Call(object target,string method,params object[] args)
        {return target.GetType().GetMethod(method,All).Invoke(target,args);}
        static object Member(object target,string name)
        {
            Type type=target.GetType();FieldInfo field=type.GetField(name,All);
            return field!=null?field.GetValue(target):type.GetProperty(name,All).GetValue(target,null);
        }

        // These four adapters deliberately contain no intersection or bounds
        // arithmetic. They bind the independent fixture to the runtime helper.
        static Box Create(Bounds source,Matrix4x4 pose,float padding)
        {return Read(orientedType.GetMethod("Create",All).Invoke(null,new object[]{source,pose,padding}));}
        static Box Read(object value)
        {
            var result=new Box {Native=value,Valid=(bool)Member(value,"Valid"),Center=(Vector3)Member(value,"Center"),Extents=(Vector3)Member(value,"Extents")};
            result.Axes[0]=(Vector3)Member(value,"AxisX");result.Axes[1]=(Vector3)Member(value,"AxisY");result.Axes[2]=(Vector3)Member(value,"AxisZ");
            result.Aabb=Aabb(Corners(result));return result;
        }
        static bool Intersects(Box a,Box b)
        {return (bool)orientedType.GetMethod("Intersects",All).Invoke(null,new[]{a.Native,b.Native});}
        static Box Published(object topology)
        {return Read(Member(topology,"World"));}

        static Vector3[] Corners(Bounds source,Matrix4x4 transform)
        {
            var result=new Vector3[8];Vector3 lo=source.min,hi=source.max;
            for(int i=0;i<8;i++)result[i]=transform.MultiplyPoint3x4(new Vector3(
                (i&1)==0?lo.x:hi.x,(i&2)==0?lo.y:hi.y,(i&4)==0?lo.z:hi.z));
            return result;
        }

        static Bounds Aabb(Vector3[] vertices)
        {Bounds result=new Bounds(vertices[0],Vector3.zero);for(int i=1;i<vertices.Length;i++)result.Encapsulate(vertices[i]);return result;}

        static Vector3[] Corners(Box box)
        {
            var result=new Vector3[8];
            for(int i=0;i<8;i++)result[i]=box.Center+box.Axes[0]*(box.Extents.x*((i&1)==0?-1f:1f))+
                box.Axes[1]*(box.Extents.y*((i&2)==0?-1f:1f))+box.Axes[2]*(box.Extents.z*((i&4)==0?-1f:1f));
            return result;
        }

        static PlaneD[] Planes(Box first,Box second,double padding)
        {
            var result=new PlaneD[12];D3 origin=new D3(first.Center);
            for(int box=0;box<2;box++)
            {
                Box current=box==0?first:second;D3 center=new D3(current.Center)-origin;
                for(int axis=0;axis<3;axis++)for(int side=0;side<2;side++)
                {
                    D3 normal=new D3(current.Axes[axis])*(side==0?-1d:1d);
                    result[box*6+axis*2+side]=new PlaneD {Normal=normal,
                        Distance=D3.Dot(normal,center)+current.Extents[axis]+padding};
                }
            }
            return result;
        }

        static bool Inside(PlaneD[] planes,D3 point)
        {
            for(int i=0;i<planes.Length;i++)if(D3.Dot(planes[i].Normal,point)>planes[i].Distance+1e-9)return false;
            return true;
        }

        static bool Feasible(Box first,Box second,double padding)
        {
            PlaneD[] planes=Planes(first,second,padding);
            if(Inside(planes,new D3(0d,0d,0d)) || Inside(planes,new D3(second.Center)-new D3(first.Center)))return true;
            for(int a=0;a<planes.Length-2;a++)for(int b=a+1;b<planes.Length-1;b++)for(int c=b+1;c<planes.Length;c++)
            {
                D3 bc=D3.Cross(planes[b].Normal,planes[c].Normal);
                double determinant=D3.Dot(planes[a].Normal,bc);
                if(Math.Abs(determinant)<1e-12)continue;
                D3 point=(bc*planes[a].Distance+D3.Cross(planes[c].Normal,planes[a].Normal)*planes[b].Distance+
                    D3.Cross(planes[a].Normal,planes[b].Normal)*planes[c].Distance)*(1d/determinant);
                if(Inside(planes,point))return true;
            }
            return false;
        }

        static void VerifyPair(Box a,Box b,string context)
        {
            Require(a.Valid && b.Valid,context+": unexpectedly invalid input");
            pairs++;bool reference=Feasible(a,b,0d),actual=Intersects(a,b);
            Require(actual==Intersects(b,a),context+": asymmetric intersection");
            if(reference)
            {overlaps++;Require(actual,context+": SAT rejected a double-precision half-space intersection");}
            else
            {
                separated++;
                if(FaceAxesOverlap(a,b))crossAxisSeparations++;
                // Runtime float SAT may conservatively retain a nearly touching
                // pair. It must not merge boxes with a material geometric gap.
                Require(!actual || Feasible(a,b,.002d),context+": SAT retained a pair separated by more than float tolerance");
            }
        }

        static bool FaceAxesOverlap(Box a,Box b)
        {
            Vector3[] first=Corners(a),second=Corners(b);
            for(int set=0;set<2;set++)for(int axis=0;axis<3;axis++)
            {
                D3 direction=new D3((set==0?a:b).Axes[axis]);
                double minA=double.PositiveInfinity,maxA=double.NegativeInfinity,minB=double.PositiveInfinity,maxB=double.NegativeInfinity;
                for(int i=0;i<8;i++)
                {
                    double projected=D3.Dot(new D3(first[i]),direction);minA=Math.Min(minA,projected);maxA=Math.Max(maxA,projected);
                    projected=D3.Dot(new D3(second[i]),direction);minB=Math.Min(minB,projected);maxB=Math.Max(maxB,projected);
                }
                if(maxA<minB-1e-4 || maxB<minA-1e-4)return false;
            }
            return true;
        }

        static void Covers(Box box,Vector3[] geometry,float padding,string context)
        {
            Require(box.Valid,context+": conservative box invalid");
            for(int axis=0;axis<3;axis++)
            {
                Require(Mathf.Abs(box.Axes[axis].sqrMagnitude-1f)<.001f,context+": nonunit box axis");
                for(int other=axis+1;other<3;other++)Require(Mathf.Abs(Vector3.Dot(box.Axes[axis],box.Axes[other]))<.001f,context+": nonorthogonal box axes");
                for(int point=0;point<geometry.Length;point++)
                {
                    double projection=Math.Abs(D3.Dot(new D3(geometry[point])-new D3(box.Center),new D3(box.Axes[axis])));
                    Require(projection+padding<=box.Extents[axis]+.005d,context+": transformed mesh corner/shader padding escaped axis "+axis);
                }
            }
        }

        static void VerifyContacts()
        {
            Bounds source=new Bounds(Vector3.zero,new Vector3(2f,4f,8f));
            Vector3 origin=new Vector3(312.25f,17.5f,-601.75f);
            Box a=Create(source,Matrix4x4.TRS(origin,Quaternion.identity,Vector3.one),0f);
            foreach(Vector3 displacement in new[]{new Vector3(2f,0f,0f),new Vector3(2f,4f,0f),new Vector3(2f,4f,8f),
                new Vector3(1.9f,3.9f,7.9f),Vector3.zero,new Vector3(0f,4.2f,0f),new Vector3(2.2f,0f,0f)})
                VerifyPair(a,Create(source,Matrix4x4.TRS(origin+displacement,Quaternion.identity,Vector3.one),0f),"Face/edge/corner contact "+displacement);
            Quaternion yaw=Quaternion.Euler(0f,37f,0f);
            Bounds train=new Bounds(new Vector3(.1f,2f,-.3f),new Vector3(3f,4f,24f));
            a=Create(train,Matrix4x4.TRS(origin,yaw,Vector3.one),.125f);
            Box parallel=Create(train,Matrix4x4.TRS(origin+yaw*new Vector3(5.5f,0f,0f),yaw,Vector3.one),.125f);
            Require(a.Aabb.Intersects(parallel.Aabb),"Parallel diagonal reference lacks AABB overlap");
            Require(!Intersects(a,parallel),"Parallel tracks incorrectly share an OBB component");
            VerifyPair(a,parallel,"Parallel diagonal tracks");
            foreach(float angle in new[]{.00001f,.001f,.01f,.1f,13f,67f,89.99f})
            {
                Box crossed=Create(train,Matrix4x4.TRS(origin+yaw*new Vector3(4f,.2f,2f),Quaternion.Euler(4f,37f+angle,3f),Vector3.one),.125f);
                VerifyPair(a,crossed,"Tilted crossing angle "+angle);
            }
            foreach(Vector3 size in new[]{Vector3.zero,new Vector3(0f,0f,8f),new Vector3(0f,4f,8f),new Vector3(.0001f,4f,8f)})
            {
                Box thin=Create(new Bounds(Vector3.zero,size),Matrix4x4.TRS(origin,yaw,Vector3.one),0f);
                VerifyPair(a,thin,"Degenerate/thin size "+size);
                VerifyPair(thin,Create(source,Matrix4x4.TRS(origin+new Vector3(70f,12f,20f),yaw,Vector3.one),0f),"Separated thin size "+size);
            }
            Box invalid=Read(Activator.CreateInstance(orientedType));
            Require(!invalid.Valid && Intersects(invalid,a) && Intersects(a,invalid) && Intersects(invalid,invalid),"Unknown/invalid geometry must conservatively overlap");
            foreach(Matrix4x4 malformed in new[]{Matrix4x4.zero,Matrix4x4.TRS(origin,yaw,new Vector3(0f,1f,1f))})
            {
                invalid=Create(source,malformed,.125f);
                Require(!invalid.Valid && Intersects(invalid,a),"Singular basis must use conservative intersection fallback");
            }
        }

        static float RandomValue(System.Random random,float minimum,float maximum)
        {return minimum+(maximum-minimum)*(float)random.NextDouble();}
        static Vector3 RandomVector(System.Random random,float minimum,float maximum)
        {return new Vector3(RandomValue(random,minimum,maximum),RandomValue(random,minimum,maximum),RandomValue(random,minimum,maximum));}

        static void VerifyRandomPairs()
        {
            var random=new System.Random(19092026);
            for(int i=0;i<3200;i++)
            {
                Vector3 center=RandomVector(random,-1500f,1500f);
                Vector3 sizeA=new Vector3(RandomValue(random,.1f,8f),RandomValue(random,.1f,7f),RandomValue(random,2f,35f));
                Vector3 sizeB=new Vector3(RandomValue(random,.1f,8f),RandomValue(random,.1f,7f),RandomValue(random,2f,35f));
                Quaternion rotationA=Quaternion.Euler(RandomVector(random,-180f,180f));
                Quaternion rotationB=i%5==0?rotationA:Quaternion.Euler(RandomVector(random,-180f,180f));
                Vector3 offset=RandomVector(random,-(i%3==0?4f:22f),i%3==0?4f:22f);
                Box a=Create(new Bounds(RandomVector(random,-2f,2f),sizeA),Matrix4x4.TRS(center,rotationA,Vector3.one),.125f);
                Box b=Create(new Bounds(RandomVector(random,-2f,2f),sizeB),Matrix4x4.TRS(center+offset,rotationB,Vector3.one),.125f);
                VerifyPair(a,b,"Random pair "+i);
            }
            Require(overlaps>300 && separated>1000,"Random corpus did not exercise both intersection outcomes");
            Require(crossAxisSeparations>10,"Random corpus did not exercise separating edge-cross-edge axes");
        }

        static Matrix4x4 Shear(float xy,float xz,float yz)
        {Matrix4x4 result=Matrix4x4.identity;result.m01=xy;result.m02=xz;result.m12=yz;return result;}

        static void VerifyTransformedGeometry()
        {
            Bounds source=new Bounds(new Vector3(.7f,2.1f,-1.3f),new Vector3(3.2f,4.7f,25f));
            for(int i=0;i<90;i++)
            {
                Vector3 scale=new Vector3((i&1)==0?-1.4f:1.4f,(i&2)==0?.7f:-.7f,(i&4)==0?1.8f:-1.8f);
                Matrix4x4 pose=Matrix4x4.TRS(new Vector3(813f+i*27f,51f,-641f-i*31f),Quaternion.Euler(i*3f,37f+i*11f,-8f),scale);
                if(i%3==0)pose*=Shear(.21f,-.17f,.31f);
                Box box=Create(source,pose,.375f);
                Covers(box,Corners(source,pose),.375f,"Affine geometry "+i);
                Box other=Create(source,Matrix4x4.TRS(new Vector3(813f+i*27f+2f,51f,-641f-i*31f+4f),Quaternion.Euler(3f,13f,7f),Vector3.one),.125f);
                VerifyPair(box,other,"Affine fallback intersection "+i);
            }
        }

        static void VerifyPublishedHysteresis()
        {
            object envelope=Activator.CreateInstance(envelopeType,true);
            Bounds source=new Bounds(new Vector3(.2f,2.7f,-.7f),new Vector3(3.2f,4.5f,26f));
            Vector3 position=new Vector3(532.25f,18.5f,-371.75f);
            Quaternion rotation=Quaternion.Euler(2.1f,31.5f,.3f);
            Matrix4x4 pose=Matrix4x4.TRS(position,rotation,Vector3.one);
            Call(envelope,"Update",source,pose,.25f);
            Box first=Published(envelope);Covers(first,Corners(source,pose),.25f,"Envelope warmup");
            for(int frame=1;frame<=5000;frame++)
            {
                float phase=frame*.071f;
                Vector3 offset=new Vector3(Mathf.Sin(phase),Mathf.Cos(phase*.87f),Mathf.Sin(phase*.63f))*.025f;
                pose=Matrix4x4.TRS(position+offset,Quaternion.Euler(2.1f+Mathf.Sin(phase)*.02f,
                    31.5f+Mathf.Cos(phase)*.02f,.3f+Mathf.Sin(phase*.7f)*.02f),Vector3.one);
                Call(envelope,"Update",source,pose,.25f);
                Call(envelope,"EnsureContains",source,pose,.25f);
                Box current=Published(envelope);
                Require((bool)Call(current.Native,"Same",first.Native),"Sub-margin pose jitter changed published OBB at frame "+frame);
                Covers(current,Corners(source,pose),.25f,"Frozen OBB jitter "+frame);
            }
            int publications=0;Box prior=Published(envelope);
            for(int frame=0;frame<600;frame++)
            {
                pose=Matrix4x4.TRS(position+rotation*new Vector3(frame*.02f,0f,0f),rotation,Vector3.one);
                Call(envelope,"Update",source,pose,.25f);Box current=Published(envelope);
                if(!(bool)Call(current.Native,"Same",prior.Native))publications++;
                Covers(current,Corners(source,pose),.25f,"Moving envelope "+frame);prior=current;
            }
            Require(publications>20 && publications<100,"Published OBB did not retain a useful motion margin: "+publications);
            for(int frame=0;frame<240;frame++)
            {
                pose=Matrix4x4.TRS(position+new Vector3(-10000f,270f,12000f),Quaternion.Euler(7f,31.5f+frame*.75f,-3f),new Vector3(-1.2f,.8f,1.6f));
                if(frame%7==0)pose*=Shear(.15f,-.21f,.11f);
                Call(envelope,"Update",source,pose,.25f);
                Covers(Published(envelope),Corners(source,pose),.25f,"Rotating shifted/sheared envelope "+frame);
            }
            Bounds native=new Bounds(new Vector3(934f,17f,-612f),new Vector3(17f,6f,31f));
            Call(envelope,"EnsureContains",native,Matrix4x4.identity,.75f);
            Covers(Published(envelope),Corners(native,Matrix4x4.identity),.75f,"Native static-batch/custom-stream fallback");
            Bounds animated=new Bounds(new Vector3(.2f,.5f,-.3f),new Vector3(.3f,1f,2f));
            for(int frame=0;frame<100;frame++)
            {
                Matrix4x4 animation=Matrix4x4.TRS(new Vector3(941f+frame*.2f,17f,-612f),Quaternion.Euler(0f,frame*3f,0f),Vector3.one);
                Call(envelope,"EnsureContains",animated,animation,.75f);
                Covers(Published(envelope),Corners(animated,animation),.75f,"Detached/animated part "+frame);
            }
            Covers(Published(envelope),Corners(source,pose),.25f,"Expanded native guard retained earlier geometry");
            Call(envelope,"Reset");
            Require(!Published(envelope).Valid,"Envelope reset retained valid published geometry");
            Matrix4x4 restored=Matrix4x4.TRS(new Vector3(-736f,31f,815f),Quaternion.Euler(-3f,-21f,7f),Vector3.one);
            Call(envelope,"Update",source,restored,.125f);
            Covers(Published(envelope),Corners(source,restored),.125f,"Envelope restored after reset");
            Debug.Log("SNOW_ORIENTED_HYSTERESIS: changing jitter poses=5000, OBB changes=0, 12-metre translation publications="+publications);
        }

        static void VerifyParallelYard()
        {
            var boxes=new List<Box>();Bounds source=new Bounds(new Vector3(.1f,2.1f,-.3f),new Vector3(3.2f,4.2f,22f));
            Quaternion yaw=Quaternion.Euler(0f,37f,0f);int aabb=0,obb=0;
            for(int row=0;row<16;row++)for(int car=0;car<14;car++)
            {
                Matrix4x4 pose=Matrix4x4.TRS(new Vector3(613f,23f,-817f)+yaw*new Vector3(row*5.4f,0f,car*22.5f),yaw,Vector3.one);
                Box box=Create(source,pose,.375f);Covers(box,Corners(source,pose),.375f,"Yard box "+boxes.Count);boxes.Add(box);
            }
            for(int first=0;first<boxes.Count;first++)for(int second=first+1;second<boxes.Count;second++)
            {
                if(!boxes[first].Aabb.Intersects(boxes[second].Aabb))continue;
                aabb++;VerifyPair(boxes[first],boxes[second],"Yard pair "+first+"/"+second);
                if(Intersects(boxes[first],boxes[second]))obb++;
            }
            Require(obb>0 && obb<aabb,"Yard OBB filtering lost adjacent cars or did not separate parallel tracks");
            Debug.Log("SNOW_ORIENTED_YARD: 224 cars, AABB overlaps="+aabb+", OBB overlaps="+obb+"; geometry counts only, no FPS claim.");
        }
    }
}
