using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowDustVerification
    {
        public static void Verify(AssetBundle bundle)
        {
            Verify(bundle.LoadAsset<Shader>("assets/dvseasons/dv99/shaders/snowdust.shader"));
        }

        public static void Verify(Shader shader)
        {
            if(shader==null || !shader.isSupported)throw new Exception("Snow powder shader unavailable");
            var dust=new Material(shader);dust.mainTexture=Texture2D.whiteTexture;
            var snow=new Material(Shader.Find("Standard"));snow.SetFloat("_Glossiness",.02f);
            var quad=GameObject.CreatePrimitive(PrimitiveType.Quad);quad.layer=30;
            var mesh=UnityEngine.Object.Instantiate(quad.GetComponent<MeshFilter>().sharedMesh);
            var normals=new Vector3[mesh.vertexCount];var colors=new Color[mesh.vertexCount];
            for(int i=0;i<normals.Length;i++){normals[i]=Vector3.up;colors[i]=Color.white;}
            mesh.normals=normals;mesh.colors=colors;quad.GetComponent<MeshFilter>().sharedMesh=mesh;
            var cameraObject=new GameObject("Snow colour camera");var camera=cameraObject.AddComponent<Camera>();
            camera.enabled=false;camera.transform.position=new Vector3(0,0,-2);camera.cullingMask=1<<30;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
            camera.renderingPath=RenderingPath.Forward;camera.allowHDR=true;
            camera.depthTextureMode=DepthTextureMode.Depth;camera.nearClipPlane=.1f;camera.farClipPlane=20;
            var target=new RenderTexture(32,32,24,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
            camera.targetTexture=target;target.Create();
            var readback=new Texture2D(32,32,TextureFormat.RGBAFloat,false,true);
            var sunObject=new GameObject("Snow colour sun");var sun=sunObject.AddComponent<Light>();
            sun.type=LightType.Directional;sun.transform.rotation=Quaternion.Euler(90,0,0);sun.cullingMask=1<<30;
            var ambient=RenderSettings.ambientLight;var mode=RenderSettings.ambientMode;var fog=RenderSettings.fog;
            var oldGlare=Shader.GetGlobalFloat("_DVPSGlareReduction");var oldTarget=RenderTexture.active;
            var oldDepth=Shader.GetGlobalTexture("_CameraDepthTexture");
            GameObject occluder=null;Material occluderMaterial=null;
            Func<Color> readFrame=()=>
            {
                camera.Render();RenderTexture.active=target;
                readback.ReadPixels(new Rect(0,0,32,32),0,0);readback.Apply();return readback.GetPixel(16,16);
            };
            Func<Material,Color> render=material=>
            {
                quad.GetComponent<Renderer>().sharedMaterial=material;return readFrame();
            };
            try
            {
                RenderSettings.fog=false;RenderSettings.ambientMode=AmbientMode.Flat;
                foreach(float glare in new[]{0f,2f})foreach(float daylight in new[]{1f,.2f,.01f})
                {
                    sun.intensity=daylight;RenderSettings.ambientLight=Color.white*(daylight*.3f);
                    Shader.SetGlobalFloat("_DVPSGlareReduction",glare);
                    // Standard material converts its sRGB colour to linear, while
                    // the snow overlay writes this albedo directly to GBuffer0.
                    var albedo=new Color(.78f,.81f,.84f)*(.96f*Mathf.Lerp(1,.55f,glare*.5f));
                    snow.color=QualitySettings.activeColorSpace==ColorSpace.Linear?albedo.gamma:albedo;
                    var expected=render(snow);var actual=render(dust);
                    if(daylight==1 && expected.maxColorComponent<.05f)throw new Exception("Snow colour fixture did not render a lit surface");
                    float error=Mathf.Max(Mathf.Abs(actual.r-expected.r),Mathf.Abs(actual.g-expected.g),Mathf.Abs(actual.b-expected.b));
                    if(error>Mathf.Max(.035f,expected.maxColorComponent*.12f))
                        throw new Exception("Snow dust colour differs from lit snow: daylight="+daylight+", glare="+glare+", snow="+expected+", dust="+actual);
                }
                Debug.Log("SNOW_DUST_COLOUR_OK: matches snow albedo under daylight, overcast/night lighting and glare reduction; no inherited earth-dust material.");

                // Render an actual opaque surface into the camera depth buffer.
                // Merely inspecting shader properties would miss a wrong depth
                // projection or a transparent pass that ignores the buffer.
                sun.intensity=1;RenderSettings.ambientLight=Color.white*.3f;
                Shader.SetGlobalFloat("_DVPSGlareReduction",0);
                occluder=GameObject.CreatePrimitive(PrimitiveType.Cube);occluder.layer=30;
                occluder.transform.localScale=new Vector3(4,4,.2f);
                occluderMaterial=new Material(Shader.Find("Standard"));occluderMaterial.color=Color.black;
                occluderMaterial.SetFloat("_Glossiness",0);occluderMaterial.SetFloat("_SpecularHighlights",0);
                occluderMaterial.SetFloat("_GlossyReflections",0);
                occluderMaterial.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
                occluderMaterial.EnableKeyword("_GLOSSYREFLECTIONS_OFF");
                occluder.GetComponent<Renderer>().sharedMaterial=occluderMaterial;
                var quadRenderer=quad.GetComponent<Renderer>();quadRenderer.sharedMaterial=dust;
                dust.SetFloat("_SoftIntersectionDistance",.4f);
                foreach(bool orthographic in new[]{false,true})
                {
                    camera.orthographic=orthographic;camera.orthographicSize=1;
                    occluder.transform.position=new Vector3(0,0,.7f);
                    quadRenderer.enabled=false;float background=readFrame().grayscale;quadRenderer.enabled=true;
                    float full=readFrame().grayscale-background;
                    if(full<.05f)throw new Exception("Snow depth fixture did not render visible dust, orthographic="+orthographic);
                    foreach(float gap in new[]{.3f,.2f,.04f})
                    {
                        occluder.transform.position=new Vector3(0,0,gap+.1f);
                        float ratio=(readFrame().grayscale-background)/full;
                        float expected=gap/.4f;
                        if(Mathf.Abs(ratio-expected)>.08f)
                            throw new Exception("Snow intersection did not fade smoothly: gap="+gap+", expected="+expected+", actual="+ratio+", orthographic="+orthographic);
                    }
                    occluder.transform.position=new Vector3(0,0,.05f);
                    float hidden=readFrame().grayscale-background;
                    if(Mathf.Abs(hidden)>.01f)
                        throw new Exception("Snow particles render through opaque geometry: "+hidden+", orthographic="+orthographic);
                }
                Debug.Log("SNOW_DUST_DEPTH_OK: continuous 0.4 m intersection fade and opaque occlusion verified in perspective and orthographic cameras.");
            }
            finally
            {
                RenderSettings.ambientLight=ambient;RenderSettings.ambientMode=mode;RenderSettings.fog=fog;
                Shader.SetGlobalFloat("_DVPSGlareReduction",oldGlare);RenderTexture.active=oldTarget;
                Shader.SetGlobalTexture("_CameraDepthTexture",oldDepth);
                foreach(var item in new UnityEngine.Object[]{quad,mesh,dust,snow,cameraObject,sunObject,target,readback,occluder,occluderMaterial})
                    if(item!=null)UnityEngine.Object.DestroyImmediate(item);
            }
        }
    }
}
