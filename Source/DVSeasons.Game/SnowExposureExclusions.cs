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
        private readonly IncrementalSceneScan<SkinnedMeshRenderer> skinnedScan;
        private readonly Dictionary<int,MeshRenderer> proxies=new Dictionary<int,MeshRenderer>();
        private readonly Dictionary<int,Renderer> animals=new Dictionary<int,Renderer>();
        private readonly List<int> expired=new List<int>();
        private readonly List<Material> materials=new List<Material>();
        private readonly List<Renderer> hidden=new List<Renderer>();
        private bool changed;

        public SnowExposureExclusions()
        { scan=new IncrementalSceneScan<MeshRenderer>(5f,192,Visit); skinnedScan=new IncrementalSceneScan<SkinnedMeshRenderer>(5f,192,VisitSkinned); }

        public bool Update()
        {
            changed=false;
            using(SnowPerformance.Measure("snow-proxy-discovery")) scan.Step();
            skinnedScan.Step();
            return changed;
        }

        private void Visit(MeshRenderer renderer)
        {
            if (IsAnimal(renderer))
            {
                var id=renderer.GetInstanceID();
                if (!animals.ContainsKey(id)) changed=true;
                animals[id] = renderer;
                return;
            }
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

        private void VisitSkinned(SkinnedMeshRenderer renderer)
        {
            if (IsAnimal(renderer))
            {
                var id=renderer.GetInstanceID();
                if (!animals.ContainsKey(id)) changed=true;
                animals[id] = renderer;
            }
        }

        public void HideForCapture()
        {
            expired.Clear();
            foreach (var pair in animals)
            {
                var renderer=pair.Value;
                if (renderer==null) { expired.Add(pair.Key); continue; }
                if (renderer.forceRenderingOff) continue;
                renderer.forceRenderingOff=true; hidden.Add(renderer);
            }
            foreach(var pair in proxies)
            {
                var renderer=pair.Value;
                if(renderer==null) { expired.Add(pair.Key);continue; }
                // Preserve externally hidden renderers and renderer.enabled.
                if(renderer.forceRenderingOff) continue;
                renderer.forceRenderingOff=true;hidden.Add(renderer);
            }
            foreach(int id in expired) { proxies.Remove(id); animals.Remove(id); }
        }

        public void Restore()
        {
            foreach(var renderer in hidden) if(renderer!=null) renderer.forceRenderingOff=false;
            hidden.Clear();
        }

        private static bool IsAnimal(Renderer renderer)
        {
            if (renderer == null) return false;
            // CompareTag logs an engine error for undefined tags even inside a
            // try/catch. Reading the assigned tag does not require that Animal
            // exists in the game's TagManager.
            if (string.Equals(renderer.tag, "Animal", StringComparison.Ordinal)) return true;
            for (var t=renderer.transform; t!=null; t=t.parent)
            {
                var n=t.name;
                if (string.IsNullOrEmpty(n)) continue;
                var lower=n.ToLowerInvariant();
                if (lower.Contains("animal") || lower.Contains("deer") || lower.Contains("moose") ||
                    lower.Contains("elk") || lower.Contains("horse") || lower.Contains("cow") ||
                    lower.Contains("sheep") || lower.Contains("goat") || lower.Contains("chicken") ||
                    lower.Contains("boar") || lower.Contains("pig")) return true;
            }
            return false;
        }

        public void Dispose()
        { Restore();scan.Dispose();skinnedScan.Dispose();proxies.Clear();animals.Clear();materials.Clear();expired.Clear();changed=false; }
    }
}
