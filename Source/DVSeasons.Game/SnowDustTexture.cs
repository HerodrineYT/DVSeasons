using UnityEngine;

namespace DVSeasons.Mod
{
    internal static class SnowDustTexture
    {
        // Copy the native dust silhouette once. Never recolour the shared game
        // material/texture: derailment dust must keep its original appearance.
        public static Texture2D Create(Texture source)
        {
            if (source == null) source = Texture2D.whiteTexture;
            int width = Mathf.Min(512, source.width), height = Mathf.Min(512, source.height);
            StreamingTextureReadiness.ReadLease lease = null;
            var streamed = source as Texture2D;
            if (streamed != null && !StreamingTextureReadiness.TryAcquire(streamed,width,height,out lease)) return null;
            var previous = RenderTexture.active;
            var target = RenderTexture.GetTemporary(width,height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            Texture2D result = null;
            try
            {
                Graphics.Blit(source,target);
                RenderTexture.active=target;
                result=new Texture2D(width,height,TextureFormat.RGBA32,true,false)
                {name="DVSeasons snow-coloured dust",hideFlags=HideFlags.HideAndDontSave,
                    wrapMode=source.wrapMode,filterMode=FilterMode.Trilinear};
                result.ReadPixels(new Rect(0,0,width,height),0,0,false);
                var pixels=result.GetPixels32();
                for(int i=0;i<pixels.Length;i++)
                {
                    var pixel=pixels[i];
                    // Preserve alpha and faint internal detail; remove earth hues.
                    float detail=.94f+.06f*(pixel.r*.2126f+pixel.g*.7152f+pixel.b*.0722f)/255f;
                    byte value=(byte)(255*detail);
                    pixels[i]=new Color32(value,value,value,pixel.a);
                }
                result.SetPixels32(pixels);result.Apply(true,false);
                return result;
            }
            catch {if(result!=null)Object.Destroy(result);throw;}
            finally{RenderTexture.active=previous;RenderTexture.ReleaseTemporary(target);if(lease!=null)lease.Dispose();}
        }
    }
}
