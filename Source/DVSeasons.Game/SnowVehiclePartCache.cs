using System.Collections.Generic;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Native order is retained. Only the active LOD lists are expanded, and only
    // when their membership changes; live renderer state is still checked later.
    internal sealed class SnowVehiclePartCache
    {
        private sealed class Run
        {
            public SnowVehicleRegistry.LodSet Lod;
            public int Mask;
            public bool Interior, Included;
            public Transform Parent;
            public readonly List<SnowVehicleRegistry.Part> Parts=new List<SnowVehicleRegistry.Part>();
        }
        private readonly List<Run> runs=new List<Run>();
        private readonly List<SnowVehicleRegistry.Part> active=new List<SnowVehicleRegistry.Part>();
        // All fallback LODs, in native order. Native-shader parts never issue
        // a surface/exclusion draw and must not rebuild that batching index.
        public readonly List<SnowVehicleRegistry.Part> FallbackParts=new List<SnowVehicleRegistry.Part>();
        public readonly List<SnowVehicleRegistry.LodSet> Lods=new List<SnowVehicleRegistry.LodSet>();
        private readonly HashSet<SnowVehicleRegistry.LodSet> seenLods=new HashSet<SnowVehicleRegistry.LodSet>();
        private bool dirty;
        public void Build(List<SnowVehicleRegistry.Part> parts)
        {
            runs.Clear();active.Clear();FallbackParts.Clear();Lods.Clear();seenLods.Clear();dirty=true;
            Run run=null;
            foreach(var part in parts)
            {
                if(!part.HasOpaque || part.NativeComplete)continue;
                FallbackParts.Add(part);
                if(part.Lod!=null && seenLods.Add(part.Lod))Lods.Add(part.Lod);
                var parent=part.Renderer.transform.parent;
                if(run==null || run.Lod!=part.Lod || run.Mask!=part.LodMask || run.Interior!=part.Interior || run.Parent!=parent)
                {run=new Run{Lod=part.Lod,Mask=part.LodMask,Interior=part.Interior,Parent=parent};runs.Add(run);}
                run.Parts.Add(part);
            }
        }
        public List<SnowVehicleRegistry.Part> Select()
        {
            bool changed=dirty;
            foreach(var run in runs)
            {
                var lod=run.Lod;
                bool include=(run.Parent==null || run.Parent.gameObject.activeInHierarchy) &&
                    (lod==null || (run.Interior && !lod.FilterInterior) ||
                    (lod.Current>=0 && lod.Current<32 && (run.Mask&(1<<lod.Current))!=0));
                if(include!=run.Included){run.Included=include;changed=true;}
            }
            if(changed)
            {
                active.Clear();foreach(var run in runs)if(run.Included)active.AddRange(run.Parts);
                dirty=false;
            }
            return active;
        }
    }
}
