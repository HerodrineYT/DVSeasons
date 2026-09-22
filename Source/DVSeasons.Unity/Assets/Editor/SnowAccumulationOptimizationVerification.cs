using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    // Compare the actual accumulation pass with the complete pre-optimization
    // shader, including empty captures, shelter edges and existing snow masks.
    public static class SnowAccumulationOptimizationVerification
    {
        const int Size=256,WorldSize=1024;
        const string Production="Assets/DVSeasons/DV99/Shaders/SnowVehicle.shader";
        const string Reference="Assets/Editor/SnowAccumulationReference20260919.shader";
        public static void RunSource() {Run(false);}
        public static void RunPacked() {Run(true);}
        public static void RunPackedBenchmark() {Run(true,true);}
        static void Run(bool packed,bool benchmark=false)
        {
            int exit=0;AssetBundle bundle=null;
            try
            {
                if(packed)
                {
                    string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
                    string runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
                    bundle=AssetBundle.LoadFromFile(Path.Combine(runtime,"AssetBundles/dvseasons_dv99"));
                    Require(bundle!=null,"Packed accumulation bundle unavailable");
                }
                Verify(bundle);
                if(benchmark)
                {
                    Shader shader=bundle.LoadAsset<Shader>(Production.ToLowerInvariant());
                    Shader reference=AssetDatabase.LoadAssetAtPath<Shader>(Reference);
                    using(var fixture=new Fixture(shader,reference))fixture.Benchmark();
                }
            }
            catch(Exception error) {Debug.LogException(error);exit=1;}
            finally {if(bundle!=null)bundle.Unload(true);}
            EditorApplication.Exit(exit);
        }
        static void Require(bool condition,string message) {if(!condition)throw new Exception(message);}
        public static void Verify(AssetBundle bundle)
        {
            Shader shader=bundle==null?AssetDatabase.LoadAssetAtPath<Shader>(Production):bundle.LoadAsset<Shader>(Production.ToLowerInvariant());
            Shader reference=AssetDatabase.LoadAssetAtPath<Shader>(Reference);
            Require(shader!=null && shader.isSupported && reference!=null && reference.isSupported,"Accumulation shader unsupported");
            using(var fixture=new Fixture(shader,reference)) fixture.Verify();
        }
        sealed class Fixture:IDisposable
        {
            readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
            readonly Material current,reference;
            readonly Texture2DArray heights,snow;
            readonly Texture2D[] shelter=new Texture2D[3];
            readonly RenderTexture target;
            readonly Texture2D read,fence;
            readonly Mesh quad;
            readonly CommandBuffer commands=new CommandBuffer();
            readonly RenderTexture oldTarget;
            T Keep<T>(T value) where T:UnityEngine.Object {owned.Add(value);return value;}
            public Fixture(Shader shader,Shader baseline)
            {
                oldTarget=RenderTexture.active;
                current=Keep(new Material(shader));reference=Keep(new Material(baseline));
                ShaderUtil.CompilePass(current,3,true);ShaderUtil.CompilePass(reference,3,true);
                Require(!ShaderUtil.ShaderHasError(shader) && !ShaderUtil.ShaderHasError(baseline),"Accumulation compilation failed");
                heights=Keep(new Texture2DArray(Size,Size,3,TextureFormat.RFloat,false,true)
                    {filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});
                snow=Keep(new Texture2DArray(Size,Size,3,TextureFormat.RHalf,false,true)
                    {filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp});
                for(int level=0;level<3;level++)
                {
                    shelter[level]=Keep(new Texture2D(WorldSize,WorldSize,TextureFormat.RFloat,false,true)
                        {filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});
                    var pixels=new Color[WorldSize*WorldSize];
                    for(int y=0;y<WorldSize;y++) for(int x=0;x<WorldSize;x++)
                        pixels[y*WorldSize+x]=new Color(((x/13+y/19+level)%3==0?8f:.05f)+((x*17+y*31)%101)*.002f,0,0,1);
                    shelter[level].SetPixels(pixels);shelter[level].Apply(false,false);
                    var previous=new Color[Size*Size];
                    for(int i=0;i<previous.Length;i++)previous[i]=new Color(((i*17+level*29)%101)/100f,0,0,1);
                    snow.SetPixels(previous,level);
                }
                snow.Apply(false,false);
                target=Keep(new RenderTexture(Size,Size,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear)
                    {filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});Require(target.Create(),"Accumulation target allocation failed");
                read=Keep(new Texture2D(Size,Size,TextureFormat.RGBAFloat,false,true));
                fence=Keep(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                quad=Keep(new Mesh {vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)},
                    uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up},triangles=new[]{0,1,2,0,2,3}});
                foreach(var material in new[]{current,reference})
                {
                    material.SetTexture("_DVPSVehicleHeights",heights);material.SetTexture("_DVPSVehicleSnow",snow);
                    material.SetTexture("_DVPSNearHeight",shelter[0]);material.SetTexture("_DVPSFarHeight",shelter[1]);
                    material.SetTexture("_DVPSDistantHeight",shelter[2]);material.SetVector("_DVPSVehicleArea",new Vector4(0,0,2.37f,11.19f));
                }
            }
            void Capture(int fill)
            {
                for(int slot=0;slot<3;slot++)
                {
                    var pixels=new Color[Size*Size];
                    for(int y=0;y<Size;y++)for(int x=0;x<Size;x++)
                    {
                        bool empty=fill==0 || (fill==1 && ((x/5+y/9+slot)%3!=0));
                        pixels[y*Size+x]=new Color(empty?-100000f:.02f+(x*11+y*7+slot*5)%31*.007f,0,0,1);
                    }
                    heights.SetPixels(pixels,slot);
                }
                heights.Apply(false,false);
            }
            Color[] Draw(Material material)
            {
                commands.Clear();commands.SetRenderTarget(target);commands.ClearRenderTarget(false,true,Color.magenta);
                commands.DrawMesh(quad,Matrix4x4.identity,material,0,3);Graphics.ExecuteCommandBuffer(commands);
                RenderTexture.active=target;read.ReadPixels(new Rect(0,0,Size,Size),0,0);read.Apply(false,false);return read.GetPixels();
            }
            public void Verify()
            {
                int scenarios=0;long pixelsCompared=0;float worst=0;
                foreach(int fill in new[]{0,1,2})
                {
                    Capture(fill);
                    foreach(float center in new[]{0f,109f,500f,893f,1800f,3980f})
                    foreach(float origin in new[]{0f,10000f})
                    foreach(bool initial in new[]{true,false})
                    foreach(float delta in new[]{0f,.003f,.17f})
                    {
                        int slot=scenarios%3;
                        foreach(var material in new[]{current,reference})
                        {
                            material.SetFloat("_DVPSVehicleIndex",slot);
                            material.SetMatrix("_DVPSVehicleLocalToWorld",Matrix4x4.TRS(new Vector3(origin+center,.014f,-origin),Quaternion.Euler(0,37.7f,0),Vector3.one));
                            material.SetVector("_DVPSNearArea",new Vector4(origin,-origin,128,.25f));
                            material.SetVector("_DVPSFarArea",new Vector4(origin,-origin,960,1.875f));
                            material.SetVector("_DVPSDistantArea",new Vector4(origin,-origin,4096,8));
                            material.SetVector("_DVPSPreviousSnowArea",scenarios%2==0?new Vector4(0,0,2.37f,11.19f):new Vector4(.19f,1.73f,1.82f,9.07f));
                            material.SetVector("_DVPSAccumulation",new Vector4(initial?1:0,delta,.023f,-.061f));
                            material.SetFloat("_DVPSDistantOffset",.089f);
                        }
                        var before=Draw(reference);var after=Draw(current);
                        for(int i=0;i<before.Length;i++)
                        {
                            for(int component=0;component<4;component++)
                            {
                                float error=Mathf.Abs(before[i][component]-after[i][component]);
                                if(float.IsNaN(error) || error>.00001f)
                                    throw new Exception("Accumulation parity changed: scenario="+scenarios+" pixel="+i+" component="+component+" error="+error+
                                        " expected="+before[i].ToString("G9")+" actual="+after[i].ToString("G9")+" fill="+fill+" initial="+initial+" delta="+delta+" center="+center+" origin="+origin);
                                worst=Mathf.Max(worst,error);
                            }
                            if(fill==0)Require(after[i]==Color.clear,"Empty capture retained snow");
                        }
                        scenarios++;pixelsCompared+=before.Length;
                    }
                }
                Debug.Log("SNOW_ACCUMULATION_OPTIMIZATION_OK: scenarios="+scenarios+" pixels="+pixelsCompared+" max_error="+worst.ToString("G9")+
                    "; empty/sparse/dense captures, initial/restored masks, zero/positive snowfall, near/far/distant edges, rotation, floating origin; unchanged 1024-square height maps, 256-square vehicle masks, exact four-sample visibility semantics");
            }
            void Drain(RenderTexture output)
            {
                RenderTexture.active=output;fence.ReadPixels(new Rect(0,0,1,1),0,0,false);
            }
            double Time(CommandBuffer batch,RenderTexture output)
            {
                Drain(output);var timer=System.Diagnostics.Stopwatch.StartNew();
                Graphics.ExecuteCommandBuffer(batch);Drain(output);timer.Stop();return timer.Elapsed.TotalMilliseconds;
            }
            CommandBuffer Batch(Material material,int count,RenderTexture output)
            {
                var batch=new CommandBuffer {name="Snow accumulation throughput comparison"};batch.SetRenderTarget(output);
                for(int i=0;i<count;i++)batch.DrawMesh(quad,Matrix4x4.identity,material,0,3);
                return batch;
            }
            public void Benchmark()
            {
                const int Draws=128,Trials=7;
                var output=Keep(new RenderTexture(Size,Size,0,RenderTextureFormat.RHalf,RenderTextureReadWrite.Linear)
                    {filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp});
                Require(output.Create(),"Runtime-format accumulation benchmark target unavailable");
                foreach(int fill in new[]{1,2})
                foreach(float center in new[]{0f,500f,1800f})
                {
                    Capture(fill);
                    foreach(var material in new[]{current,reference})
                    {
                        material.SetFloat("_DVPSVehicleIndex",1);
                        material.SetMatrix("_DVPSVehicleLocalToWorld",Matrix4x4.TRS(new Vector3(center,.014f,0),Quaternion.Euler(0,37.7f,0),Vector3.one));
                        material.SetVector("_DVPSNearArea",new Vector4(0,0,128,.25f));
                        material.SetVector("_DVPSFarArea",new Vector4(0,0,960,1.875f));
                        material.SetVector("_DVPSDistantArea",new Vector4(0,0,4096,8));
                        material.SetVector("_DVPSPreviousSnowArea",new Vector4(.19f,1.73f,1.82f,9.07f));
                        material.SetVector("_DVPSAccumulation",new Vector4(0,.013f,.023f,-.061f));
                        material.SetFloat("_DVPSDistantOffset",.089f);
                    }
                    var before=new double[Trials];var after=new double[Trials];
                    using(var oldBatch=Batch(reference,Draws,output))
                    using(var newBatch=Batch(current,Draws,output))
                    {
                        for(int warm=0;warm<3;warm++){Time(oldBatch,output);Time(newBatch,output);}
                        for(int trial=0;trial<Trials;trial++)for(int run=0;run<2;run++)
                        {
                            bool next=((trial+run)&1)!=0;(next?after:before)[trial]=Time(next?newBatch:oldBatch,output);
                        }
                    }
                    Array.Sort(before);Array.Sort(after);
                    Debug.Log("SNOW_ACCUMULATION_BENCH: density="+(fill==1?"sparse":"dense")+" map="+(center==0?"near":center==500?"far":"distant")+
                        " draws="+Draws+" reference_batch_ms="+before[Trials/2].ToString("F6")+" gather_batch_ms="+after[Trials/2].ToString("F6")+
                        " reference_us_per_capture="+(before[Trials/2]*1000/Draws).ToString("F6")+
                        " gather_us_per_capture="+(after[Trials/2]*1000/Draws).ToString("F6")+
                        " ratio="+(after[Trials/2]/before[Trials/2]).ToString("F4")+
                        " capture=256x256_RHalf height_maps=1024x1024_RFloat trials=7_alternating median=true; cached_resources_and_prebuilt_commands; CPU_submission_plus_GPU_completion_not_GPU_timestamp; runtime_at_most_one_capture_per_frame; not_game_FPS=true");
                }
            }
            public void Dispose()
            {
                commands.Release();RenderTexture.active=oldTarget;
                foreach(var item in owned)if(item!=null)UnityEngine.Object.DestroyImmediate(item);
            }
        }
    }
}
