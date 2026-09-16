using UnityEngine;

namespace DVSeasons.Mod
{
    internal static class SnowCoveragePattern
    {
        private static readonly Color32[] pixels=CreatePixels();
        private static Color32[] CreatePixels()
        {
            var result=new Color32[128*128];uint seed=0x71623u;
            for(int i=0;i<result.Length;i++){seed=unchecked(1664525u*seed+1013904223u);byte v=(byte)(seed>>24);result[i]=new Color32(v,v,v,255);}
            return result;
        }
        public static Texture2D CreateTexture()
        {
            var texture=new Texture2D(128,128,TextureFormat.RGBA32,false,true)
            {wrapMode=TextureWrapMode.Repeat,filterMode=FilterMode.Bilinear,hideFlags=HideFlags.HideAndDontSave};
            texture.SetPixels32(pixels);texture.Apply(false,true);return texture;
        }
        private static float Noise(Vector3 p)
        {
            float x=p.x+p.y*.37f,z=p.z+p.y*.61f;
            int ix=Mathf.FloorToInt(x),iz=Mathf.FloorToInt(z);float fx=x-ix,fz=z-iz;
            float a=pixels[(iz&127)*128+(ix&127)].r,b=pixels[(iz&127)*128+((ix+1)&127)].r;
            float c=pixels[((iz+1)&127)*128+(ix&127)].r,d=pixels[((iz+1)&127)*128+((ix+1)&127)].r;
            return Mathf.Lerp(Mathf.Lerp(a,b,fx),Mathf.Lerp(c,d,fx),fz)/255f;
        }
        public static float At(Vector3 stablePoint,float amount)
        {
            if(amount<=.001f)return 0;
            float rank=Noise(stablePoint*.8f)*.7f+Noise(stablePoint*3.2f)*.3f;
            return Mathf.SmoothStep(0,1,Mathf.InverseLerp(rank-.10f,rank+.10f,amount*1.24f-.12f));
        }
    }
}
