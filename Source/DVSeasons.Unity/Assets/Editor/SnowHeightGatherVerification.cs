using System;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowHeightGatherVerification
    {
        public static void Run()
        {
            int code=0;
            try {Verify();}
            catch(Exception error){Debug.LogException(error);code=1;}
            EditorApplication.Exit(code);
        }
        public static void Verify()
        {
            var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/SnowHeightGatherVerification.shader");
            if(shader==null || !shader.isSupported) throw new Exception("Height gather test shader unsupported");
            var source=new Texture2D(1024,1024,TextureFormat.RFloat,false,true)
                {filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
            var pixels=new Color[1024*1024];
            for(int y=0;y<1024;y++) for(int x=0;x<1024;x++)
                pixels[y*1024+x]=new Color(((x*127+y*71)%1031)*.003f+(x<512?0:12),0,0,0);
            source.SetPixels(pixels);source.Apply();
            var array=new Texture2DArray(1024,1024,1,TextureFormat.RFloat,false,true)
                {filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};array.SetPixels(pixels,0);array.Apply();
            var material=new Material(shader);material.SetTexture("_Heights",source);material.SetTexture("_ReferenceHeights",source);
            material.SetTexture("_ArrayHeights",array);
            var output=new RenderTexture(256,256,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);output.Create();
            var read=new Texture2D(256,256,TextureFormat.RGBAFloat,false,true);var old=RenderTexture.active;
            try
            {
                foreach(var uv in new[]{new Vector4(1,1,0,0),new Vector4(1.2f,1.2f,-.1f,-.1f),
                    new Vector4(.002f,.002f,.4991f,.6131f),new Vector4(.77f,.65f,.00137f,.00091f)})
                {
                    material.SetVector("_TestUV",uv);Graphics.Blit(null,output,material);RenderTexture.active=output;
                    read.ReadPixels(new Rect(0,0,256,256),0,0);read.Apply();
                    float worst=0;foreach(var value in read.GetPixels()) worst=Mathf.Max(worst,value.maxColorComponent);
                    if(worst>.00001f) throw new Exception("Gather changed an exposure height: maximum error="+worst+", UV="+uv);
                }
                Debug.Log("SNOW_HEIGHT_GATHER_OK: four height samples preserved at irregular UVs, shelter discontinuities and clamped map edges; comparison remains before interpolation.");
            }
            finally
            {
                RenderTexture.active=old;foreach(var value in new UnityEngine.Object[]{source,array,material,output,read}) UnityEngine.Object.DestroyImmediate(value);
            }
        }
    }
}
