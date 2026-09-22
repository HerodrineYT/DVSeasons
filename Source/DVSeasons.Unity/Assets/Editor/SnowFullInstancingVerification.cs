using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace DVSeasons.AssetBundleBuild
{
    // Raw shader comparison isolates the per-instance transform/ID contract from
    // registry grouping. Registry order and native-only fallbacks have separate tests.
    public static class SnowFullInstancingVerification
    {
        const int Width=800,Height=600;
        static readonly int Index=Shader.PropertyToID("_DVPSVehicleIndex");
        static readonly int Frame=Shader.PropertyToID("_DVPSVehicleWorldToLocal");
        static readonly int InstanceIndex=Shader.PropertyToID("_DVPSInstanceVehicleIndex");
        static readonly int InstanceFrame=Shader.PropertyToID("_DVPSInstanceWorldToLocal");
        sealed class Part
        {
            public MeshRenderer Renderer;
            public Transform Car;
            public Mesh Mesh;
            public float Index;
            public int Group;
            public Matrix4x4 Adjustment;
            public Matrix4x4 WorldToLocal => Adjustment*Car.worldToLocalMatrix;
        }
        sealed class Batch
        {
            public Mesh Mesh;
            public int Group;
            public readonly List<Part> Parts=new List<Part>();
            public readonly Matrix4x4[] Poses=new Matrix4x4[1023],Frames=new Matrix4x4[1023];
            public readonly float[] Ids=new float[1023];
            public readonly MaterialPropertyBlock Properties=new MaterialPropertyBlock();
        }
        public static void RunSource()
        {
            int code=0;
            try { Verify(AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/SnowVehicle.shader")); }
            catch(Exception error) {Debug.LogException(error);code=1;}
            EditorApplication.Exit(code);
        }
        public static void Run()
        {
            int code=0;AssetBundle bundle=null;
            try
            {
                var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
                bundle=AssetBundle.LoadFromFile(Path.Combine(root,"artifacts/build/DVSeasons/AssetBundles/dvseasons_dv99"));
                Require(bundle!=null,"Packed snow bundle unavailable");
                Verify(bundle.LoadAsset<Shader>("assets/dvseasons/dv99/shaders/snowvehicle.shader"));
            }
            catch(Exception error) {Debug.LogException(error);code=1;}
            finally {if(bundle!=null)bundle.Unload(true);}
            EditorApplication.Exit(code);
        }
        public static void Verify(Shader shader)
        {
            Require(shader!=null && shader.isSupported && SystemInfo.supportsInstancing,"Instanced snow shader unsupported");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            using(var fixture=new Fixture(shader))
            {
                Require(fixture.Material.FindPass("INSTANCED_SNOW")==6,"Instanced pass displaced existing pass numbers");
                ShaderUtil.CompilePass(fixture.Material,0,true);ShaderUtil.CompilePass(fixture.Material,6,true);
                Require(!ShaderUtil.ShaderHasError(shader),"Snow shader compile failed");
                fixture.Compare("different vehicles, nonuniform scales, cutouts, custom captured frames");
                fixture.MoveParts();fixture.Compare("moving parts and remapped captured frame");
                fixture.Shift(new Vector3(5000,200,-7000));fixture.Compare("large world position");
                fixture.Shift(new Vector3(-5000,-200,7000));fixture.Compare("floating origin restored");
                fixture.Benchmark();
            }
            Debug.Log("SNOW_FULL_INSTANCING_OK: actual pass0 DrawRenderer versus pass6 DrawMeshInstanced; exact pixel coverage, vehicle IDs and R8 slopes; half-float local positions; 24 vehicles with 8 different mesh parts each; per-instance captured frames and nonuniform scales; cutout UV transforms; moving parts and floating origin; source/packed shader supported.");
        }
        sealed class Fixture:IDisposable
        {
            public readonly Material Material;
            readonly List<UnityEngine.Object> resources=new List<UnityEngine.Object>();
            readonly List<Part> parts=new List<Part>();
            readonly List<Batch> batches=new List<Batch>();
            readonly GameObject fleet;
            readonly Camera camera;
            readonly RenderTexture surface,slope,target;
            readonly RenderTargetIdentifier[] mrt;
            readonly Texture2D read,fence,alpha;
            readonly CommandBuffer commands=new CommandBuffer {name="Full vehicle snow instancing parity"};
            readonly RenderTexture previous;
            readonly Vector4 alphaST=new Vector4(2,1,.125f,0);

            T Keep<T>(T value) where T:UnityEngine.Object {resources.Add(value);return value;}
            public Fixture(Shader shader)
            {
                previous=RenderTexture.active;QualitySettings.antiAliasing=0;RenderSettings.fog=false;
                fleet=Keep(new GameObject("Full snow repeated fleet"));
                camera=Keep(new GameObject("Full snow fixture camera")).AddComponent<Camera>();
                camera.enabled=false;camera.transform.position=new Vector3(0,28,-8);camera.transform.LookAt(Vector3.zero);
                camera.fieldOfView=48;camera.nearClipPlane=.1f;camera.farClipPlane=100;
                camera.renderingPath=RenderingPath.DeferredShading;camera.depthTextureMode=DepthTextureMode.Depth;
                camera.allowHDR=true;camera.allowMSAA=false;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                target=Keep(new RenderTexture(Width,Height,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));target.Create();camera.targetTexture=target;
                surface=Keep(new RenderTexture(Width,Height,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear));surface.Create();
                slope=Keep(new RenderTexture(Width,Height,0,RenderTextureFormat.R8,RenderTextureReadWrite.Linear));slope.Create();
                mrt=new[]{new RenderTargetIdentifier(surface),new RenderTargetIdentifier(slope)};
                read=Keep(new Texture2D(Width,Height,TextureFormat.RGBAFloat,false,true));
                fence=Keep(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                Material=Keep(new Material(shader){enableInstancing=true});
                var opaque=Keep(new Material(Shader.Find("Standard")) {color=Color.gray});
                alpha=Keep(new Texture2D(8,8,TextureFormat.RGBA32,false,true));alpha.filterMode=FilterMode.Point;alpha.wrapMode=TextureWrapMode.Repeat;
                var pixels=new Color[64];for(int y=0;y<8;y++)for(int x=0;x<8;x++)pixels[y*8+x]=new Color(1,1,1,(x+y)%2);
                alpha.SetPixels(pixels);alpha.Apply(false,false);
                var cutout=Keep(new Material(opaque));cutout.mainTexture=alpha;cutout.mainTextureScale=new Vector2(alphaST.x,alphaST.y);cutout.mainTextureOffset=new Vector2(alphaST.z,alphaST.w);
                cutout.SetFloat("_Mode",1);cutout.SetFloat("_Cutoff",.5f);cutout.SetOverrideTag("RenderType","TransparentCutout");cutout.EnableKeyword("_ALPHATEST_ON");cutout.renderQueue=2450;
                var meshTypes=new Mesh[8];
                for(int kind=0;kind<meshTypes.Length;kind++)
                {
                    // Each car has multiple distinct model parts. Only the same
                    // part across different cars can share an instanced draw.
                    float width=.27f+(kind%3)*.015f;
                    var normal=Quaternion.Euler(kind*9,0,0)*Vector3.up;
                    var mesh=Keep(new Mesh {name="Shared car model part "+kind,
                        vertices=new[]{new Vector3(-width,0,-.29f),new Vector3(-width,0,.29f),new Vector3(width,0,.29f),new Vector3(width,0,-.29f)},
                        normals=new[]{normal,normal,normal,normal},uv=new[]{Vector2.zero,Vector2.up,Vector2.one,Vector2.right},triangles=new[]{0,1,2,0,2,3}});
                    mesh.RecalculateBounds();meshTypes[kind]=mesh;
                    batches.Add(new Batch {Mesh=mesh,Group=kind%2});
                }
                for(int car=0;car<24;car++)
                {
                    var root=new GameObject("Snow car "+car).transform;root.SetParent(fleet.transform,false);
                    root.localPosition=new Vector3((car%6-2.5f)*3.5f,0,(car/6-1.5f)*3.5f);
                    root.localRotation=Quaternion.Euler(0,(car%3)*7,0);
                    root.localScale=new Vector3(1+(car%3)*.04f,1,1-(car%2)*.08f);
                    for(int kind=0;kind<8;kind++)
                    {
                        var go=new GameObject("Car mesh part "+kind);go.transform.SetParent(root,false);
                        go.transform.localPosition=new Vector3((kind%4-1.5f)*.68f,.05f*(kind%2),(kind/4-.5f)*.8f);
                        go.transform.localRotation=Quaternion.Euler((car%4)*5,kind*3,(kind%3)*4);
                        go.AddComponent<MeshFilter>().sharedMesh=meshTypes[kind];
                        var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=kind%2==0?opaque:cutout;
                        var part=new Part {Renderer=renderer,Car=root,Mesh=meshTypes[kind],Index=car==23?1024:car==22?80:car+1,Group=kind%2,
                            Adjustment=kind%3==0?Matrix4x4.TRS(new Vector3(.07f,.15f,-.11f),Quaternion.Euler(0,12,0),Vector3.one):Matrix4x4.identity};
                        parts.Add(part);batches[kind].Parts.Add(part);
                    }
                }
                camera.AddCommandBuffer(CameraEvent.BeforeReflections,commands);
            }
            void Cutout(int group)
            {
                commands.SetGlobalFloat("_DVPSVehicleCutoff",group==0?0:.5f);
                commands.SetGlobalVector("_DVPSVehicleST",alphaST);commands.SetGlobalTexture("_DVPSVehicleAlbedo",alpha);
            }
            void Record(bool instanced)
            {
                commands.Clear();commands.SetRenderTarget(mrt,BuiltinRenderTextureType.CameraTarget);commands.ClearRenderTarget(false,true,Color.clear);
                if(!instanced)
                {
                    foreach(var part in parts)
                    {
                        Cutout(part.Group);commands.SetGlobalFloat(Index,part.Index);commands.SetGlobalMatrix(Frame,part.WorldToLocal);
                        commands.DrawRenderer(part.Renderer,Material,0,0);
                    }
                }
                else foreach(var batch in batches)
                {
                    for(int i=0;i<batch.Parts.Count;i++)
                    {var part=batch.Parts[i];batch.Poses[i]=part.Renderer.localToWorldMatrix;batch.Frames[i]=part.WorldToLocal;batch.Ids[i]=part.Index;}
                    batch.Properties.Clear();batch.Properties.SetFloatArray(InstanceIndex,batch.Ids);batch.Properties.SetMatrixArray(InstanceFrame,batch.Frames);
                    Cutout(batch.Group);commands.DrawMeshInstanced(batch.Mesh,0,Material,6,batch.Poses,batch.Parts.Count,batch.Properties);
                }
                commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            }
            Color[][] Draw(bool instanced)
            {
                Record(instanced);camera.Render();var result=new Color[2][];
                RenderTexture.active=surface;read.ReadPixels(new Rect(0,0,Width,Height),0,0);read.Apply(false,false);result[0]=read.GetPixels();
                RenderTexture.active=slope;read.ReadPixels(new Rect(0,0,Width,Height),0,0);read.Apply(false,false);result[1]=read.GetPixels();return result;
            }
            public void Compare(string phase)
            {
                var baseline=Draw(false);var current=Draw(true);int covered=0,ids=0,slopes=0;float localError=0;
                var seenIds=new HashSet<int>();
                for(int pixel=0;pixel<baseline[0].Length;pixel++)
                {
                    var a=baseline[0][pixel];var b=current[0][pixel];
                    if(a.a>0) {covered++;seenIds.Add((int)a.a);}
                    if(a.a!=b.a)ids++;
                    if(baseline[1][pixel].r!=current[1][pixel].r)slopes++;
                    localError=Mathf.Max(localError,Mathf.Max(Mathf.Abs(a.r-b.r),Mathf.Max(Mathf.Abs(a.g-b.g),Mathf.Abs(a.b-b.b))));
                }
                Require(covered>15000 && seenIds.Count==24,"Insufficient fixture coverage: "+covered+" pixels, "+seenIds.Count+" cars");
                Require(ids==0 && slopes==0 && localError<=.002f,phase+": changed ID/coverage="+ids+" slope="+slopes+" max local="+localError);
                Debug.Log("SNOW_FULL_INSTANCE_DIFF: "+phase+" covered="+covered+" cars="+seenIds.Count+" ID_changes="+ids+" slope_changes="+slopes+" max_local_error="+localError.ToString("G9"));
            }
            public void MoveParts()
            {
                foreach(var part in parts)
                {
                    part.Renderer.transform.localPosition+=new Vector3(.03f,.07f,-.03f);
                    part.Renderer.transform.localRotation*=Quaternion.Euler(7,11,3);
                    part.Adjustment=Matrix4x4.TRS(new Vector3(.03f,-.02f,.05f),Quaternion.Euler(0,6,0),Vector3.one)*part.Adjustment;
                }
            }
            public void Shift(Vector3 offset) {fleet.transform.position+=offset;camera.transform.position+=offset;}
            void Drain() {RenderTexture.active=target;fence.ReadPixels(new Rect(0,0,1,1),0,0,false);}
            double TimeFrames(bool instanced,int count)
            {
                Drain();var watch=Stopwatch.StartNew();
                for(int i=0;i<count;i++) {Record(instanced);camera.Render();}
                Drain();watch.Stop();return watch.Elapsed.TotalMilliseconds/count;
            }
            public void Benchmark()
            {
                TimeFrames(false,10);TimeFrames(true,10);
                var old=new double[5];var current=new double[5];
                for(int trial=0;trial<5;trial++)
                    if((trial&1)==0) {old[trial]=TimeFrames(false,40);current[trial]=TimeFrames(true,40);}
                    else {current[trial]=TimeFrames(true,40);old[trial]=TimeFrames(false,40);}
                Array.Sort(old);Array.Sort(current);
                Debug.Log("SNOW_FULL_INSTANCE_RENDER_BENCH: parts="+parts.Count+" distinct_mesh_parts="+batches.Count+" commands="+parts.Count+"->"+batches.Count+
                    " native_ms="+old[2].ToString("F4")+" instanced_ms="+current[2].ToString("F4")+" resolution="+Width+"x"+Height+
                    " frames_per_trial=40 alternating_trials=5 includes_record_native_camera_and_GPU_completion not_game_FPS=true");
            }
            public void Dispose()
            {
                RenderTexture.active=previous;camera.RemoveCommandBuffer(CameraEvent.BeforeReflections,commands);commands.Dispose();camera.targetTexture=null;
                foreach(var resource in resources)if(resource!=null)UnityEngine.Object.DestroyImmediate(resource);
            }
        }
        static void Require(bool value,string message) {if(!value)throw new Exception(message);}
    }
}
