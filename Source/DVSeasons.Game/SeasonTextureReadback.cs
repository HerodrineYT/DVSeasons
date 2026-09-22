using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    // A mip lease and its GPU surface must outlive the asynchronous request,
    // including when the owning season/session is reset in the meantime.
    internal sealed class SeasonTextureReadback : IDisposable
    {
        private sealed class Pending
        {
            public RenderTexture Target;
            public StreamingTextureReadiness.ReadLease Lease;
            public Color32[] Pixels;
            public bool Done, Cancelled, Error;
        }
        private static int activeRequests;
        private Pending pending;
        private bool synchronousFallback;
        internal static int ActiveRequests => activeRequests;
        internal static int AsyncRequestCount { get; private set; }
        internal static int SynchronousReadCount { get; private set; }

        public bool TryRead(Texture2D source,int width,int height,out Color32[] pixels)
        {
            pixels=null;
            if(pending!=null)
            {
                if(!pending.Done) return false;
                pixels=pending.Pixels;
                synchronousFallback=pending.Error;
                pending=null;
                if(pixels!=null) return true;
            }
            if(source==null || activeRequests>=2) return false;
            StreamingTextureReadiness.ReadLease lease;
            if(!StreamingTextureReadiness.TryAcquire(source,width,height,out lease)) return false;
            var previous=RenderTexture.active;
            RenderTexture target=null;
            try
            {
                target=RenderTexture.GetTemporary(width,height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Default);
                Graphics.Blit(source,target);
                if(SystemInfo.supportsAsyncGPUReadback && !synchronousFallback)
                {
                    var work=new Pending {Target=target,Lease=lease};
                    pending=work;
                    activeRequests++;
                    try
                    {
                        AsyncGPUReadback.Request(target,0,TextureFormat.RGBA32,request=>
                        {
                            try
                            {
                                work.Error=request.hasError;
                                if(!work.Cancelled && !work.Error)
                                {
                                    var data=request.GetData<Color32>();
                                    work.Pixels=new Color32[data.Length];data.CopyTo(work.Pixels);
                                }
                            }
                            catch(Exception e)
                            {work.Error=true;Debug.LogWarning("[DVSeasons] Async seasonal texture copy failed: "+e.Message);}
                            finally
                            {
                                work.Lease?.Dispose();
                                RenderTexture.ReleaseTemporary(work.Target);
                                work.Target=null;work.Lease=null;work.Done=true;activeRequests--;
                            }
                        });
                    }
                    catch
                    {activeRequests--;pending=null;throw;}
                    AsyncRequestCount++;
                    target=null;lease=null;
                    return false;
                }
                // Compatibility only: this path is never used on supported GPUs
                // unless Unity reports an actual asynchronous readback failure.
                Texture2D readable=null;
                try
                {
                    RenderTexture.active=target;
                    readable=new Texture2D(width,height,TextureFormat.RGBA32,false);
                    readable.ReadPixels(new Rect(0,0,width,height),0,0,false);
                    pixels=readable.GetPixels32();SynchronousReadCount++;
                    return true;
                }
                finally {Destroy(readable);}
            }
            finally
            {
                RenderTexture.active=previous;
                lease?.Dispose();
                if(target!=null) RenderTexture.ReleaseTemporary(target);
            }
        }

        public void Dispose()
        {
            if(pending==null) return;
            pending.Cancelled=true;pending.Pixels=null;pending=null;
            // The completion callback owns pending GPU resources; releasing them
            // here could let another texture overwrite an in-flight readback.
        }
        private static void Destroy(UnityEngine.Object resource)
        {if(resource==null)return;if(Application.isPlaying)UnityEngine.Object.Destroy(resource);else UnityEngine.Object.DestroyImmediate(resource);}
    }
}
