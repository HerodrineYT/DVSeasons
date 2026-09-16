using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    // A small offline scene for reviewing the real renderer, size, lifetime and
    // soft intersections together. It never advances or edits the caller's system.
    public static class SnowPlumePreview
    {
        public static void Verify(ParticleSystem particles,string repoRoot,float trailSize = 0)
        {
            if(particles==null)throw new ArgumentNullException("particles");
            var cleanup=new List<UnityEngine.Object>();
            var oldActive=RenderTexture.active;
            var oldAmbient=RenderSettings.ambientLight;var oldMode=RenderSettings.ambientMode;
            var oldFog=RenderSettings.fog;var oldGlare=Shader.GetGlobalFloat("_DVPSGlareReduction");
            var oldDepth=Shader.GetGlobalTexture("_CameraDepthTexture");
            try
            {
                const int layer=29;const int width=1280,height=720;
                var origin=new Vector3(18000,40,18000);
                var root=new GameObject("Snow plume preview");root.transform.position=origin;cleanup.Add(root);
                var clone=UnityEngine.Object.Instantiate(particles.gameObject,root.transform);
                clone.name="Preview of runtime snow particles";clone.transform.localPosition=Vector3.zero;
                clone.transform.localRotation=Quaternion.identity;clone.transform.localScale=Vector3.one;clone.layer=layer;
                var preview=clone.GetComponent<ParticleSystem>();preview.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
                preview.useAutoRandomSeed=false;preview.randomSeed=1103;
                var main=preview.main;main.simulationSpace=ParticleSystemSimulationSpace.Custom;
                main.customSimulationSpace=root.transform;main.useUnscaledTime=false;
                var renderer=clone.GetComponent<ParticleSystemRenderer>();
                var material=new Material(renderer.sharedMaterial);cleanup.Add(material);renderer.sharedMaterial=material;
                var nativeTexture=new Texture2D(2,2,TextureFormat.RGBA32,false);cleanup.Add(nativeTexture);
                var nativePath=Path.Combine(repoRoot,"artifacts/verification/0.3.2-native-dust.png");
                if(!nativeTexture.LoadImage(File.ReadAllBytes(nativePath)))throw new Exception("Native dust preview silhouette did not load");
                var texels=nativeTexture.GetPixels32();
                for(int i=0;i<texels.Length;i++)texels[i]=new Color32(242,248,253,texels[i].a);
                nativeTexture.SetPixels32(texels);nativeTexture.Apply();material.mainTexture=nativeTexture;

                var groundMaterial=new Material(Shader.Find("Standard"));cleanup.Add(groundMaterial);
                groundMaterial.color=new Color(.20f,.22f,.24f);groundMaterial.SetFloat("_Glossiness",.05f);
                var railMaterial=new Material(Shader.Find("Standard"));cleanup.Add(railMaterial);
                railMaterial.color=new Color(.28f,.29f,.30f);railMaterial.SetFloat("_Glossiness",.35f);
                var bodyMaterial=new Material(Shader.Find("Standard"));cleanup.Add(bodyMaterial);
                bodyMaterial.color=new Color(.10f,.14f,.12f);bodyMaterial.SetFloat("_Glossiness",.08f);
                Action<string,Vector3,Vector3,Material> box=(name,position,scale,mat)=>
                {
                    var item=GameObject.CreatePrimitive(PrimitiveType.Cube);item.name=name;item.layer=layer;
                    item.transform.SetParent(root.transform,false);item.transform.localPosition=position;
                    item.transform.localScale=scale;item.GetComponent<Renderer>().sharedMaterial=mat;
                    UnityEngine.Object.DestroyImmediate(item.GetComponent<Collider>());
                };
                box("Ballast",new Vector3(0,-.12f,-1),new Vector3(8,.2f,20),groundMaterial);
                for(int i=-10;i<=8;i++)box("Sleeper",new Vector3(0,.005f,i*.7f),new Vector3(2.2f,.055f,.18f),bodyMaterial);
                foreach(float side in new[]{-.75f,.75f})
                    box("Rail",new Vector3(side,.07f,-1),new Vector3(.07f,.13f,20),railMaterial);
                box("Wagon side for depth occlusion",new Vector3(0,1.25f,4.2f),new Vector3(2.7f,1.25f,4),bodyMaterial);
                box("Bogie",new Vector3(0,.39f,2.75f),new Vector3(1.9f,.45f,1.3f),railMaterial);

                var cameraObject=new GameObject("Snow preview camera");cameraObject.transform.SetParent(root.transform,false);
                var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;camera.cullingMask=1<<layer;
                camera.transform.localPosition=new Vector3(6.5f,3.1f,-8.2f);camera.transform.LookAt(origin+new Vector3(0,.55f,-.4f));
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.24f,.29f,.34f);
                camera.renderingPath=RenderingPath.Forward;camera.depthTextureMode=DepthTextureMode.Depth;
                camera.nearClipPlane=.1f;camera.farClipPlane=100;camera.fieldOfView=48;camera.allowHDR=false;
                var target=new RenderTexture(width,height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB);
                cleanup.Add(target);target.Create();camera.targetTexture=target;
                var image=new Texture2D(width,height,TextureFormat.RGB24,false);cleanup.Add(image);
                var sunObject=new GameObject("Snow preview sunlight");sunObject.transform.SetParent(root.transform,false);
                var sun=sunObject.AddComponent<Light>();sun.type=LightType.Directional;sun.cullingMask=1<<layer;
                sun.intensity=1;sun.transform.rotation=Quaternion.Euler(55,-35,0);
                RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.white*.3f;RenderSettings.fog=false;
                Shader.SetGlobalFloat("_DVPSGlareReduction",0);
                Func<Color32[]> capture=()=>
                {
                    camera.Render();RenderTexture.active=target;
                    image.ReadPixels(new Rect(0,0,width,height),0,0);image.Apply();return image.GetPixels32();
                };
                var baseline=capture();

                var random=new System.Random(1103);Func<float,float,float> range=(min,max)=>min+(max-min)*(float)random.NextDouble();
                var flow=preview.velocityOverLifetime;flow.enabled=true;flow.space=ParticleSystemSimulationSpace.World;
                flow.x=.4f;flow.y=0;flow.z=.1f;
                preview.Play(false);
                // Emit along a short stretch of track. All other visual modules
                // (noise, drag, gravity, growth, alpha lifetime) remain the real ones.
                const float dt=1f/30f;
                for(int frame=0;frame<126;frame++)
                {
                    float z=-5+frame*.06f;
                    preview.Emit(new ParticleSystem.EmitParams
                    {
                        position=new Vector3(range(-.3f,.3f),.22f,z),
                        velocity=new Vector3(range(-.35f,.35f),range(.15f,.5f),range(.65f,1f)),
                        startLifetime=range(3f,5.2f),startColor=new Color(1,1,1,.35f),
                        startSize=trailSize>0 ? trailSize : main.startSize.Evaluate(0,.5f),
                        rotation=range(0,360),applyShapeToPosition=false
                    },1);
                    if(frame%2==0)foreach(float side in new[]{-1f,1f})preview.Emit(new ParticleSystem.EmitParams
                    {
                        position=new Vector3(side*.75f,.17f,z),velocity=new Vector3(side*range(.25f,.7f),range(.25f,.65f),.5f),
                        startSize=range(.16f,.3f),startLifetime=range(.7f,1.2f),startColor=new Color(1,1,1,.65f),
                        rotation=range(0,360),applyShapeToPosition=false
                    },1);
                    preview.Simulate(dt,false,false,false);
                }
                if(preview.particleCount<5)throw new Exception("Snow plume preview has no sustained airborne particles");
                var rendered=capture();int changed=0;
                for(int i=0;i<rendered.Length;i++)
                    if(Math.Abs(rendered[i].r-baseline[i].r)+Math.Abs(rendered[i].g-baseline[i].g)+Math.Abs(rendered[i].b-baseline[i].b)>9)changed++;
                if(changed<30)throw new Exception("Runtime snow particles are invisible in the preview: "+changed+" pixels");
                if(changed>width*height/3)throw new Exception("Runtime snow particles cover an implausible area: "+changed+" pixels");
                var output=Path.Combine(repoRoot,"artifacts/verification/0.3.13-lifetime-heater/plume-preview.png");
                Directory.CreateDirectory(Path.GetDirectoryName(output));File.WriteAllBytes(output,image.EncodeToPNG());
                Debug.Log("SNOW_PLUME_PREVIEW_OK: "+preview.particleCount+" airborne particles, "+changed+" visible pixels, "+output);
            }
            finally
            {
                RenderSettings.ambientLight=oldAmbient;RenderSettings.ambientMode=oldMode;RenderSettings.fog=oldFog;
                Shader.SetGlobalFloat("_DVPSGlareReduction",oldGlare);Shader.SetGlobalTexture("_CameraDepthTexture",oldDepth);
                RenderTexture.active=oldActive;
                foreach(var item in cleanup)if(item!=null)UnityEngine.Object.DestroyImmediate(item);
            }
        }
    }
}
