using System;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowGlareVerification
    {
        public static void Verify(AssetBundle bundle)
        {
            var shader = bundle.LoadAsset<Shader>("assets/dvseasons/dv99/shaders/snowglare.shader");
            if (shader == null || !shader.isSupported) throw new Exception("Snow glare shader unavailable");
            var material = new Material(shader);
            var source = new Texture2D(8,1,TextureFormat.RGBAFloat,false,true);
            var depth = new Texture2D(1,1,TextureFormat.RFloat,false,true);
            var target = new RenderTexture(8,1,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
            var readback = new Texture2D(8,1,TextureFormat.RGBAFloat,false,true);
            var previous = RenderTexture.active;
            var previousDepth = Shader.GetGlobalTexture("_CameraDepthTexture");
            var colors = new[] { new Color(.04f,.05f,.06f,.7f),new Color(.3f,.3f,.3f,.7f),
                new Color(.6f,.6f,.6f,.7f),Color.white,new Color(2,2,2,.7f),new Color(4,4,4,.7f),
                new Color(2,.1f,.05f,.7f),new Color(.65f,.72f,.8f,.7f) };
            source.SetPixels(colors);source.Apply();source.filterMode=FilterMode.Point;
            Func<float,float,Color[]> render = (strength,depthValue) =>
            {
                depth.SetPixel(0,0,new Color(depthValue,0,0,1));depth.Apply();
                Shader.SetGlobalTexture("_CameraDepthTexture",depth);
                material.SetFloat("_Strength",strength);
                Graphics.Blit(source,target,material);
                RenderTexture.active=target;readback.ReadPixels(new Rect(0,0,8,1),0,0);readback.Apply();
                return readback.GetPixels();
            };
            Action<Color[],Color[]> equal = (a,b) =>
            { for(int i=0;i<a.Length;i++) if((a[i]-b[i]).maxColorComponent>.002f || (b[i]-a[i]).maxColorComponent>.002f)
                throw new Exception("Snow glare changed excluded pixels: "+i); };
            try
            {
                equal(colors,render(0,.5f));
                equal(colors,render(1,SystemInfo.usesReversedZBuffer?0:1));
                var reduced=render(2,.5f);
                equal(new[]{colors[0],colors[1],colors[6]},new[]{reduced[0],reduced[1],reduced[6]});
                for(int i=2;i<=5;i++)
                {
                    if(reduced[i].r>=colors[i].r || (i>2 && reduced[i].r<=reduced[i-1].r))
                        throw new Exception("Snow highlight shoulder lost brightness ordering");
                    if(Mathf.Abs(colors[i].a-reduced[i].a)>.002f) throw new Exception("Scene alpha changed");
                }
                if(reduced[5].r>=1 || reduced[7].b>=colors[7].b) throw new Exception("HDR/cool snow remained overbright");
                Debug.Log("SNOW_GLARE_GPU_OK: HDR highlight detail, cool snow, sky exclusion, shadows, saturated colours, alpha and zero-strength identity.");
            }
            finally
            {
                Shader.SetGlobalTexture("_CameraDepthTexture",previousDepth);RenderTexture.active=previous;
                UnityEngine.Object.DestroyImmediate(material);UnityEngine.Object.DestroyImmediate(source);
                UnityEngine.Object.DestroyImmediate(depth);UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(readback);
            }
        }
    }
}
