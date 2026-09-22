using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowExclusionVerification
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
                Require(bundle!=null,"Snow bundle unavailable");
                Verify(bundle.LoadAsset<Shader>("assets/dvseasons/dv99/shaders/snowvehicle.shader"));
                Debug.Log("SNOW_EXCLUSION_OK: cheap pass matches full exclusion silhouettes at 4/400m, perspective/orthographic, opaque/cutout and partial occlusion; local RGB and slope buffers remain untouched.");
            }
            catch(Exception error){Debug.LogException(error);code=1;}
            finally {if(bundle!=null) bundle.Unload(true);}
            EditorApplication.Exit(code);
        }
        private static void Verify(Shader shader)
        {
            Require(shader!=null && shader.isSupported,"Snow vehicle shader unsupported");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var camera=new GameObject("Snow exclusion camera").AddComponent<Camera>();
            camera.enabled=false;camera.renderingPath=RenderingPath.DeferredShading;camera.depthTextureMode=DepthTextureMode.Depth;
            camera.allowHDR=true;camera.allowMSAA=false;camera.nearClipPlane=.1f;camera.farClipPlane=1000;
            var target=new RenderTexture(128,128,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);target.Create();camera.targetTexture=target;
            var surface=new RenderTexture(128,128,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);surface.Create();
            var slope=new RenderTexture(128,128,0,RenderTextureFormat.R8,RenderTextureReadWrite.Linear);slope.Create();
            var read=new Texture2D(128,128,TextureFormat.RGBAFloat,false,true);
            var cutout=new Texture2D(8,8,TextureFormat.RGBA32,false,true) {filterMode=FilterMode.Point};
            for(int y=0;y<8;y++) for(int x=0;x<8;x++) cutout.SetPixel(x,y,new Color(1,1,1,(x+y)%2==0?1:0));
            cutout.Apply();
            var native=new Material(Shader.Find("Standard"));var snow=new Material(shader);
            Require(snow.FindPass("SNOW_EXCLUSION")==5,"Exclusion pass index changed");
            var mesh=new Mesh {vertices=new[]{new Vector3(-1,0,-1),new Vector3(-1,0,1),new Vector3(1,0,1),new Vector3(1,0,-1)},
                normals=new[]{Vector3.up,Vector3.up,Vector3.up,Vector3.up},uv=new[]{Vector2.zero,Vector2.up,Vector2.one,Vector2.right},triangles=new[]{0,1,2,0,2,3}};
            mesh.RecalculateBounds();
            var owner=new GameObject("Exclusion test roof");owner.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=owner.AddComponent<MeshRenderer>();renderer.sharedMaterial=native;
            var blocker=GameObject.CreatePrimitive(PrimitiveType.Cube);blocker.name="Native depth occluder";
            blocker.transform.position=new Vector3(-.7f,.5f,0);blocker.transform.localScale=new Vector3(.6f,.2f,2);
            var commands=new CommandBuffer {name="Compare snow exclusions"};camera.AddCommandBuffer(CameraEvent.BeforeReflections,commands);
            var previous=RenderTexture.active;
            try
            {
                foreach(bool orthographic in new[]{false,true}) foreach(float distance in new[]{4f,400f}) foreach(bool alpha in new[]{false,true})
                {
                    camera.orthographic=orthographic;camera.orthographicSize=1.4f;
                    camera.transform.position=Vector3.up*distance;camera.transform.LookAt(Vector3.zero,Vector3.forward);
                    camera.fieldOfView=2*Mathf.Atan(1.4f/distance)*Mathf.Rad2Deg;
                    native.SetTexture("_MainTex",alpha?cutout:Texture2D.whiteTexture);native.SetFloat("_Cutoff",.5f);
                    if(alpha) {native.EnableKeyword("_ALPHATEST_ON");native.renderQueue=2450;}
                    else {native.DisableKeyword("_ALPHATEST_ON");native.renderQueue=2000;}
                    Color[] reference=null;int hits=0,empty=0;
                    foreach(int pass in new[]{0,5})
                    {
                        commands.Clear();commands.SetRenderTarget(new[]{new RenderTargetIdentifier(surface),new RenderTargetIdentifier(slope)},BuiltinRenderTextureType.CameraTarget);
                        commands.ClearRenderTarget(false,true,new Color(.25f,.125f,.5f,0));
                        commands.SetGlobalMatrix("_DVPSVehicleWorldToLocal",Matrix4x4.identity);
                        commands.SetGlobalFloat("_DVPSVehicleIndex",-1);commands.SetGlobalFloat("_DVPSVehicleCutoff",alpha?.5f:0);
                        commands.SetGlobalTexture("_DVPSVehicleAlbedo",cutout);commands.SetGlobalVector("_DVPSVehicleST",new Vector4(1,1,0,0));
                        commands.DrawRenderer(renderer,snow,0,pass);commands.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);camera.Render();
                        RenderTexture.active=surface;read.ReadPixels(new Rect(0,0,128,128),0,0);read.Apply();var pixels=read.GetPixels();
                        if(pass==0) {reference=pixels;continue;}
                        for(int i=0;i<pixels.Length;i++)
                        {
                            Require(pixels[i].a==reference[i].a,"Cheap exclusion changed exact silhouette/depth/alpha coverage");
                            Require(pixels[i].r==.25f && pixels[i].g==.125f && pixels[i].b==.5f,"Cheap exclusion wrote unused local coordinates");
                            if(pixels[i].a==-1) hits++;else empty++;
                        }
                        RenderTexture.active=slope;read.ReadPixels(new Rect(0,0,128,128),0,0);read.Apply();
                        foreach(var value in read.GetPixels()) Require(Mathf.Abs(value.r-.25f)<1.1f/255f,"Cheap exclusion wrote the slope target");
                    }
                    Require(hits>500 && empty>500,"Exclusion fixture did not exercise both marked and unmarked pixels");
                }
            }
            finally
            {
                RenderTexture.active=previous;camera.RemoveCommandBuffer(CameraEvent.BeforeReflections,commands);commands.Dispose();camera.targetTexture=null;
                foreach(var item in new UnityEngine.Object[]{target,surface,slope,read,cutout,native,snow,mesh,owner,blocker,camera.gameObject})
                    if(item!=null) UnityEngine.Object.DestroyImmediate(item);
            }
        }
    }
}
