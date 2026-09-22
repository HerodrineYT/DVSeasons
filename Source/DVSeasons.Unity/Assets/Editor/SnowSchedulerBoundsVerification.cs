using System;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    // Calls the production interval predicate, against this Editor's actual
    // Bounds.Intersects implementation, including unordered float operands.
    public static class SnowSchedulerBoundsVerification
    {
        const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        [StructLayout(LayoutKind.Explicit)] struct FloatBits
        {
            [FieldOffset(0)] public uint Bits;
            [FieldOffset(0)] public float Value;
        }
        public static void Run()
        {
            int result=0;
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            var game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME")??"F:/Steam/steamapps/common/Derail Valley";
            ResolveEventHandler resolver=(sender,args)=> {
                foreach(var directory in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var file=Path.Combine(directory,new AssemblyName(args.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;
            try
            {
                var assembly=Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll"));
                var scheduler=assembly.GetType("DVSeasons.Mod.SnowVehicleDrawScheduler",true);
                var interval=scheduler.GetNestedType("SweepInterval",All);
                var a=Expression.Parameter(typeof(Bounds),"a");var b=Expression.Parameter(typeof(Bounds),"b");
                var x=Expression.Variable(interval,"x");var y=Expression.Variable(interval,"y");
                var assignments=new System.Collections.Generic.List<Expression>();
                foreach(var tuple in new[]{Tuple.Create(a,x),Tuple.Create(b,y)})
                {
                    var center=Expression.Property(tuple.Item1,"center");var extents=Expression.Property(tuple.Item1,"extents");
                    foreach(var axis in new[]{"x","y","z"})
                    {
                        var c=Expression.Field(center,axis);var e=Expression.Field(extents,axis);
                        assignments.Add(Expression.Assign(Expression.Field(tuple.Item2,"Min"+axis.ToUpperInvariant()),Expression.Subtract(c,e)));
                        assignments.Add(Expression.Assign(Expression.Field(tuple.Item2,"Max"+axis.ToUpperInvariant()),Expression.Add(c,e)));
                    }
                }
                assignments.Add(Expression.Call(x,interval.GetMethod("Intersects",All),y));
                var intersects=Expression.Lambda<Func<Bounds,Bounds,bool>>(Expression.Block(new[]{x,y},assignments),a,b).Compile();
                int checks=0;var random=new System.Random(78123);
                for(int i=0;i<250000;i++)
                {
                    var left=new Bounds(Vector(random,30000),Vector(random,100));
                    var right=new Bounds(Vector(random,30000),Vector(random,100));
                    if(i%3==0)right.center=left.center+Vector(random,50);
                    if(i%5==0) {left.extents=Abs(left.extents);right.extents=Abs(right.extents);right.center=left.max+right.extents;}
                    Check(intersects,left,right,ref checks);Check(intersects,right,left,ref checks);
                }
                for(int i=0;i<125000;i++)
                {
                    var left=new Bounds {center=RawVector(random),extents=RawVector(random)};
                    var right=new Bounds {center=RawVector(random),extents=RawVector(random)};
                    Check(intersects,left,right,ref checks);Check(intersects,right,left,ref checks);
                }
                var values=new[]{0f,-0f,float.Epsilon,-float.Epsilon,1f,-1f,float.MaxValue,float.MinValue,
                    float.PositiveInfinity,float.NegativeInfinity,float.NaN};
                foreach(float c in values)foreach(float e in values)
                {
                    var left=new Bounds {center=new Vector3(c,0,0),extents=new Vector3(e,1,1)};
                    var right=new Bounds(Vector3.zero,Vector3.one*2);
                    Check(intersects,left,right,ref checks);Check(intersects,right,left,ref checks);
                    left.center=new Vector3(0,c,0);left.extents=new Vector3(1,e,1);
                    Check(intersects,left,right,ref checks);Check(intersects,right,left,ref checks);
                    left.center=new Vector3(0,0,c);left.extents=new Vector3(1,1,e);
                    Check(intersects,left,right,ref checks);Check(intersects,right,left,ref checks);
                }
                Debug.Log("SNOW_SCHEDULER_BOUNDS_OK checks="+checks+"; actual production scalar interval versus Unity Bounds.Intersects; touching, negative extents, random IEEE bit patterns, infinities, NaN and signed zero; sweep ordering and OBB unchanged.");
            }
            catch(Exception error) {Debug.LogException(error);result=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(result);
        }
        static Vector3 Vector(System.Random random,float scale)
        {return new Vector3((float)(random.NextDouble()-.5)*scale,(float)(random.NextDouble()-.5)*scale,(float)(random.NextDouble()-.5)*scale);}
        static Vector3 Abs(Vector3 value) {return new Vector3(Mathf.Abs(value.x),Mathf.Abs(value.y),Mathf.Abs(value.z));}
        static float Raw(System.Random random)
        {return new FloatBits {Bits=((uint)random.Next(65536)<<16)|(uint)random.Next(65536)}.Value;}
        static Vector3 RawVector(System.Random random) {return new Vector3(Raw(random),Raw(random),Raw(random));}
        static void Check(Func<Bounds,Bounds,bool> actual,Bounds left,Bounds right,ref int checks)
        {
            checks++;
            if(actual(left,right)!=left.Intersects(right))throw new InvalidOperationException("Scalar interval mismatch at case "+checks+": "+left+" / "+right);
        }
    }
}
