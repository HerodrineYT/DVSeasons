using System;
using System.Collections.Generic;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Raw vertices of the shader-displaced distant landscape create phantom roofs.
    // It keeps its native seasonal textures; exposure uses detailed scene geometry.
    internal sealed class SnowExposureExclusions : IDisposable
    {
        private readonly IncrementalSceneScan<MeshRenderer> scan;
        private readonly Dictionary<int,MeshRenderer> proxies=new Dictionary<int,MeshRenderer>();
        private readonly List<int> expired=new List<int>();
        private readonly List<Material> materials=new List<Material>();
        private readonly List<MeshRenderer> hidden=new List<MeshRenderer>();
        private bool changed;

        public SnowExposureExclusions()
        { scan=new IncrementalSceneScan<MeshRenderer>(5f,192,Visit); }

        public bool Update()
        {
            changed=false;
            using(SnowPerformance.Measure("snow-proxy-discovery")) scan.Step();
            return changed;
        }

        private void Visit(MeshRenderer renderer)
        {
            MeshRenderer existing;
            if(proxies.TryGetValue(renderer.GetInstanceID(),out existing) && existing==renderer) return;
            renderer.GetSharedMaterials(materials);
            foreach(var material in materials)
            {
                if(material==null || material.shader==null ||
                    !string.Equals(material.shader.name,"DV/DistantTerrain",StringComparison.Ordinal)) continue;
                proxies[renderer.GetInstanceID()]=renderer;changed=true;break;
            }
        }

        public void HideForCapture()
        {
            expired.Clear();
            foreach(var pair in proxies)
            {
                var renderer=pair.Value;
                if(renderer==null) { expired.Add(pair.Key);continue; }
                // Preserve externally hidden renderers and renderer.enabled.
                if(renderer.forceRenderingOff) continue;
                renderer.forceRenderingOff=true;hidden.Add(renderer);
            }
            foreach(int id in expired) proxies.Remove(id);
        }

        public void Restore()
        {
            foreach(var renderer in hidden) if(renderer!=null) renderer.forceRenderingOff=false;
            hidden.Clear();
        }

        public void Dispose()
        { Restore();scan.Dispose();proxies.Clear();materials.Clear();expired.Clear();changed=false; }
    }
}
