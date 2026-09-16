using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    // One cached result per original landscape array; no CPU pixel readback.
    // Keep each source's own slice numbering, including distant/cluster arrays.
    internal sealed class SpringTerrainTint : IDisposable
    {
        private sealed class Entry
        {
            public Texture Source;
            public Texture Original;
            public RenderTexture Output;
            public int Step=-1, Revision=-1;
            public bool Failed;
        }
        private readonly Dictionary<int,Entry> entries=new Dictionary<int,Entry>();
        private readonly SeasonAssetBundleRepository repository;
        private Material material;
        public int BuildCount { get; private set; }
        public SpringTerrainTint(SeasonAssetBundleRepository repository) { this.repository=repository; }

        public Texture OriginalFor(Texture output)
        {
            foreach (var entry in entries.Values)
                if (entry.Output == output) return entry.Original;
            return null;
        }

        public Texture Get(Texture original,Texture source,int step,int revision)
        {
            if(step<=0 || source==null || original==null) return source;
            var array=source as Texture2DArray;
            var rt=source as RenderTexture;
            int depth=array!=null ? array.depth : rt!=null && rt.dimension==TextureDimension.Tex2DArray ? rt.volumeDepth : 0;
            if(depth==0) return source;
            if(material==null)
            {
                var shader=repository.LoadShader("SpringTerrain");
                if(shader==null) return source;
                material=new Material(shader) {hideFlags=HideFlags.HideAndDontSave};
            }
            Entry entry;int key=original.GetInstanceID();
            if(!entries.TryGetValue(key,out entry)) entries.Add(key,entry=new Entry());
            if(entry.Failed) return source;
            entry.Original = original;
            if(entry.Output!=null && entry.Output.IsCreated() && entry.Source==source && entry.Step==step && entry.Revision==revision) return entry.Output;
            var previous=RenderTexture.active;RenderTexture scratch=null;
            try
            {
                // Match the existing seasonal terrain budget (512px per slice).
                int width=Math.Min(512,source.width),height=Math.Min(512,source.height);
                if(entry.Output==null)
                {
                    entry.Output=new RenderTexture(width,height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Default)
                    {name="DVSeasons Spring Terrain "+key,dimension=TextureDimension.Tex2DArray,
                        volumeDepth=depth,useMipMap=true,autoGenerateMips=false,wrapMode=source.wrapMode,
                        filterMode=FilterMode.Trilinear,anisoLevel=source.anisoLevel,hideFlags=HideFlags.HideAndDontSave};
                    if(!entry.Output.Create()) throw new InvalidOperationException("Cannot allocate spring terrain array");
                }
                if (!entry.Output.IsCreated()) entry.Output.Create();
                scratch=RenderTexture.GetTemporary(width,height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Default);
                material.SetTexture("_SpringSource",source);material.SetFloat("_SpringWeight",step/32f);
                for(int slice=0;slice<depth;slice++)
                {
                    material.SetFloat("_SpringSlice",slice);
                    Graphics.Blit(null,scratch,material,0);
                    Graphics.CopyTexture(scratch,0,0,entry.Output,slice,0);
                }
                entry.Output.GenerateMips();entry.Source=source;entry.Step=step;entry.Revision=revision;BuildCount++;
                return entry.Output;
            }
            catch(Exception exception)
            {
                entry.Failed=true;
                DestroyResource(entry.Output);
                entry.Output=null;
                Debug.LogWarning("[DVSeasons] Spring terrain tint unavailable for '"+original.name+"': "+exception.Message);
                return source;
            }
            finally {RenderTexture.active=previous;if(scratch!=null) RenderTexture.ReleaseTemporary(scratch);}
        }
        public void Dispose()
        {
            foreach(var entry in entries.Values) DestroyResource(entry.Output);
            entries.Clear();DestroyResource(material);material=null;
        }
        private static void DestroyResource(UnityEngine.Object resource)
        {
            if(resource==null) return;
            if(Application.isPlaying) UnityEngine.Object.Destroy(resource);
            else UnityEngine.Object.DestroyImmediate(resource);
        }
    }
}
