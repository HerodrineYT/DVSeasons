using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    // Tests the actual surface-data shader without allocating 1024 vehicle
    // height/snow slices. IDs are supplied directly to its normal draw pass.
    public static class VehicleSlopePrecisionVerification
    {
        private static void Require(bool condition,string message)
        {if(!condition) throw new InvalidOperationException(message);}

        public static void Run()
        {
            int code=0;AssetBundle bundle=null;
            try
            {
                var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
                bundle=AssetBundle.LoadFromFile(Path.Combine(root,"artifacts/build/DVSeasons/AssetBundles/dvseasons_dv99"));
                Require(bundle!=null,"Snow bundle could not be loaded");
                var shader=bundle.LoadAsset<Shader>("assets/dvseasons/dv99/shaders/snowvehicle.shader");
                Verify(shader);
                Debug.Log("VEHICLE_SLOPE_PRECISION_OK: actual snow data pass; exact half-float IDs 1/80/1024; independent R8 slopes at 0/30/60/70/90 degrees and 4/400 m; animal/interior -1 and junction -3 markers remain exact.");
            }
            catch(Exception exception) {Debug.LogException(exception);code=1;}
            finally {if(bundle!=null) bundle.Unload(true);}
            EditorApplication.Exit(code);
        }

        public static void Verify(Shader shader)
        {
            Require(shader!=null && shader.isSupported,"Snow surface shader unavailable");
            Require(SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8),"R8 slope target unsupported on verification device");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var camera=new GameObject("High ID slope verification camera").AddComponent<Camera>();
            camera.enabled=false;camera.renderingPath=RenderingPath.DeferredShading;
            camera.allowHDR=true;camera.allowMSAA=false;camera.depthTextureMode=DepthTextureMode.Depth;
            camera.nearClipPlane=.1f;camera.farClipPlane=1000;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
            camera.targetTexture=new RenderTexture(128,128,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);camera.targetTexture.Create();
            var surface=new RenderTexture(128,128,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear) {filterMode=FilterMode.Point};surface.Create();
            var slope=new RenderTexture(128,128,0,RenderTextureFormat.R8,RenderTextureReadWrite.Linear) {filterMode=FilterMode.Point};slope.Create();
            var readSurface=new Texture2D(128,128,TextureFormat.RGBAFloat,false,true);
            var readSlope=new Texture2D(128,128,TextureFormat.RGBAFloat,false,true);
            var snow=new Material(shader);
            var native=new Material(Shader.Find("Standard")) {color=new Color(.08f,.08f,.08f)};
            var mesh=new Mesh {name="Precision roof and wall",vertices=new[]{new Vector3(-1,0,-1),new Vector3(-1,0,1),new Vector3(1,0,1),new Vector3(1,0,-1)},
                normals=new[]{Vector3.up,Vector3.up,Vector3.up,Vector3.up},uv=new[]{Vector2.zero,Vector2.up,Vector2.one,Vector2.right},triangles=new[]{0,1,2,0,2,3}};
            mesh.RecalculateBounds();
            var go=new GameObject("Precision surface");go.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=native;
            var commands=new CommandBuffer {name="High vehicle ID and slope MRT verification"};
            camera.AddCommandBuffer(CameraEvent.BeforeReflections,commands);
            var previous=RenderTexture.active;
            try
            {
                foreach(float distance in new[]{4f,400f}) foreach(float angle in new[]{0f,30f,60f,70f,90f})
                {
                    go.transform.rotation=Quaternion.Euler(angle,0,0);
                    var normal=go.transform.up;float expected=Mathf.Clamp01(normal.y);
                    camera.transform.position=go.transform.position+normal*distance;
                    camera.transform.LookAt(go.transform.position,go.transform.forward);
                    camera.fieldOfView=2*Mathf.Atan(1.4f/distance)*Mathf.Rad2Deg;
                    float reference=-1;
                    foreach(float id in new[]{1f,80f,1024f,-1f,-3f})
                    {
                        commands.Clear();
                        commands.SetRenderTarget(new[]{new RenderTargetIdentifier(surface),new RenderTargetIdentifier(slope)},BuiltinRenderTextureType.CameraTarget);
                        commands.ClearRenderTarget(false,true,Color.clear);
                        commands.SetGlobalMatrix("_DVPSVehicleWorldToLocal",Matrix4x4.identity);
                        commands.SetGlobalMatrix("_DVPSJunctionDelta",Matrix4x4.zero);
                        commands.SetGlobalFloat("_DVPSVehicleIndex",id);
                        commands.SetGlobalFloat("_DVPSVehicleCutoff",0);
                        commands.DrawRenderer(renderer,snow,0,0);
                        commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                        camera.Render();
                        RenderTexture.active=surface;readSurface.ReadPixels(new Rect(0,0,128,128),0,0);readSurface.Apply();
                        RenderTexture.active=slope;readSlope.ReadPixels(new Rect(0,0,128,128),0,0);readSlope.Apply();
                        var data=readSurface.GetPixels();var slopes=readSlope.GetPixels();
                        float total=0;int count=0;
                        for(int y=48;y<80;y++) for(int x=48;x<80;x++)
                        {
                            int pixel=y*128+x;
                            Require(data[pixel].a==id,"Half-float vehicle ID changed: requested="+id+", received="+data[pixel].a+", angle="+angle+", distance="+distance);
                            Require(Mathf.Abs(slopes[pixel].r-expected)<=1.1f/255f,"Slope precision lost at vehicle ID "+id+", angle="+angle+": expected="+expected+", received="+slopes[pixel].r);
                            total+=slopes[pixel].r;count++;
                        }
                        float mean=total/count;
                        if(reference<0) reference=mean;
                        else Require(Mathf.Abs(mean-reference)<.000001f,"Slope depends on high vehicle ID: "+id+" changed "+reference+" to "+mean);
                        if(angle==90) Require(mean<.001f,"Vertical wall became an upward snow surface at ID "+id);
                        if(angle==0) Require(mean>.999f,"Flat roof lost its upward slope at ID "+id);
                    }
                    Debug.Log("Slope precision: distance="+distance+", angle="+angle+", expected="+expected+", R8="+reference+", IDs1/80/1024 and negative markers identical");
                }
            }
            finally
            {
                RenderTexture.active=previous;camera.RemoveCommandBuffer(CameraEvent.BeforeReflections,commands);commands.Dispose();
                var target=camera.targetTexture;camera.targetTexture=null;
                foreach(var resource in new UnityEngine.Object[]{target,surface,slope,readSurface,readSlope,snow,native,mesh,go,camera.gameObject})
                    if(resource!=null) UnityEngine.Object.DestroyImmediate(resource);
            }
        }
    }
}
