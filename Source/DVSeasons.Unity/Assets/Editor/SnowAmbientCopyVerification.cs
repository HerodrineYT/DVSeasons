using System;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowAmbientCopyVerification
    {
        public static void Verify(AssetBundle bundle)
        {
            var shader=bundle!=null ? bundle.LoadAsset<Shader>("assets/dvseasons/dv99/shaders/proceduralsnow.shader") :
                UnityEditor.AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/ProceduralSnow.shader");
            if(shader==null || !shader.isSupported) throw new Exception("Snow AO shader unavailable");
            var material=new Material(shader);
            var source=new Texture2D(32,16,TextureFormat.RGBA32,false,true) {filterMode=FilterMode.Bilinear};
            var original=new RenderTexture(16,8,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            var readback=new Texture2D(16,8,TextureFormat.RGBAFloat,false,true);
            var previous=RenderTexture.active;
            try
            {
                var colors=new Color[32*16];
                for(int y=0;y<16;y++) for(int x=0;x<32;x++)
                    colors[y*32+x]=new Color(x/31f,y/15f,((x+y)%9)/8f,((x*13+y*29)%256)/255f);
                source.SetPixels(colors);source.Apply();original.Create();
                Graphics.Blit(source,original);
                RenderTexture.active=original;
                readback.ReadPixels(new Rect(0,0,16,8),0,0);readback.Apply();
                var expected=readback.GetPixels();
                foreach(var format in new[]{RenderTextureFormat.R8,RenderTextureFormat.ARGB32})
                {
                    if(!SystemInfo.SupportsRenderTextureFormat(format)) continue;
                    var compact=new RenderTexture(16,8,0,format,RenderTextureReadWrite.Linear);
                    try
                    {
                        compact.Create();Graphics.Blit(source,compact,material,1);
                        RenderTexture.active=compact;
                        readback.ReadPixels(new Rect(0,0,16,8),0,0);readback.Apply();
                        var actual=readback.GetPixels();
                        for(int i=0;i<actual.Length;i++)
                            if(Mathf.Abs(actual[i].r-expected[i].a)>1.01f/255f)
                                throw new Exception("Snow AO copy changed filtering/orientation at pixel "+i+" ("+format+"): expected "+expected[i]+", actual "+actual[i]);
                    }
                    finally {RenderTexture.active=previous;UnityEngine.Object.DestroyImmediate(compact);}
                }
                Debug.Log("SNOW_AMBIENT_COPY_OK: half-size R8 AO matches prior RGBA alpha, including filtering/orientation and ARGB32 fallback.");
            }
            finally
            {
                RenderTexture.active=previous;
                foreach(var resource in new UnityEngine.Object[]{material,source,original,readback})
                    UnityEngine.Object.DestroyImmediate(resource);
            }
        }

        public static void Run()
        {
            int code=0;
            try {Verify(null);}
            catch(Exception e) {Debug.LogException(e);code=1;}
            UnityEditor.EditorApplication.Exit(code);
        }
    }
}
