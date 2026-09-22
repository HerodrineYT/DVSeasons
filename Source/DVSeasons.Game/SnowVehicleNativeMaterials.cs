using System;
using System.Collections.Generic;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Standard materials keep the game's geometry pass. Instance-enabled model
    // materials remain fleet-shared; the renderer carries only an instanced ID.
    internal sealed class SnowVehicleNativeMaterials : IDisposable
    {
        internal struct Frame
        {
            public Matrix4x4 WorldToVehicle;
            public Vector4 Area, SnowArea, Sides, Rotation, State;
        }
        private sealed class Binding
        {
            public Renderer Renderer;
            public int Slot;
            public Variant Variant;
        }
        private sealed class Variant
        {
            public Source Source;
            public Material Material;
            public bool Exterior, Shared;
            public int CarSlot, SyncedCrc, SurfaceKey;
            public readonly HashSet<SnowVehicleRegistry.Vehicle> Owners=new HashSet<SnowVehicleRegistry.Vehicle>();
        }
        private sealed class Source
        {
            public Material Material;
            public int Crc, SurfaceKey;
            public Variant SharedExterior, SharedInterior;
            public readonly List<Variant> Variants=new List<Variant>();
        }
        private static readonly int SlotId=Shader.PropertyToID("_DVPSNativeSlot");
        private static readonly int CarId=Shader.PropertyToID("_DVPSNativeCar");
        private readonly Dictionary<SnowVehicleRegistry.Vehicle,List<Binding>> bindings=new Dictionary<SnowVehicleRegistry.Vehicle,List<Binding>>();
        private readonly Dictionary<Material,Source> sources=new Dictionary<Material,Source>();
        private readonly List<Source> sourceQueue=new List<Source>();
        private readonly Frame[] frames=new Frame[1023];
        private readonly List<Material> materials=new List<Material>();
        private readonly MaterialPropertyBlock properties=new MaterialPropertyBlock();
        private ComputeBuffer data;
        private Shader shader;
        private int poll,sourcePoll;
        public int BoundSlots {get;private set;}
        public int SharedSlots {get;private set;}
        public int VariantCount {get;private set;}
        public bool IsReady => data!=null;
        internal bool Enabled=true;

        private static int SurfaceKey(Material material)
        {
            unchecked
            {
                int key=material.GetFloat("_Mode").GetHashCode();
                if(!material.IsKeywordEnabled("_ALPHATEST_ON"))return key;
                var texture=material.mainTexture;
                key=key*397^(texture!=null?texture.GetInstanceID():0);
                key=key*397^material.GetFloat("_Cutoff").GetHashCode();
                key=key*397^material.mainTextureScale.GetHashCode();
                return key*397^material.mainTextureOffset.GetHashCode();
            }
        }
        private static bool Supported(Material material)
        {
            return material!=null && material.shader!=null && material.shader.name=="Standard" &&
                material.GetFloat("_Mode")<2 && material.renderQueue<=2500 &&
                !material.IsKeywordEnabled("_PARALLAXMAP") && !material.IsKeywordEnabled("_SPECULARHIGHLIGHTS_OFF") &&
                !material.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A") &&
                // Lamps/controllers animate retained originals every frame.
                !material.IsKeywordEnabled("_EMISSION") &&
                (material.mainTexture==null || material.mainTexture.name.IndexOf("coal",StringComparison.OrdinalIgnoreCase)<0);
        }
        public bool Initialize(SeasonAssetBundleRepository repository)
        {
            if(!Enabled || !SystemInfo.supportsComputeShaders || !SystemInfo.supports2DArrayTextures)return false;
            if(shader==null)shader=repository.LoadShader("SnowVehicleStandard");
            if(shader==null || !shader.isSupported)return false;
            if(data==null)data=new ComputeBuffer(1023,144);
            return true;
        }
        public void Bind(SnowVehicleRegistry.Vehicle vehicle)
        {
            if(!Enabled || shader==null || data==null || vehicle.PartsExploded)return;
            var entries=new List<Binding>();
            var exterior=new Dictionary<Material,Variant>();var interior=new Dictionary<Material,Variant>();
            foreach(var part in vehicle.Parts)
            {
                if(part.Renderer==null || part.MovingFrame!=null)continue;
                var renderer=part.Renderer;var shared=renderer.sharedMaterials;bool changed=false;
                renderer.GetPropertyBlock(properties);
                // Existing controllers with their own property blocks retain the
                // private material path. An inert negative ID is ours from a
                // previous winter; it does not mask any Standard property.
                bool mayShare=shared.Length==1 && renderer is MeshRenderer && !renderer.isPartOfStaticBatch &&
                    (properties.isEmpty || properties.GetFloat(SlotId)<0);
                if(mayShare)
                {
                    // An indexed block shadows the renderer-level car ID.
                    // Keep these controlled materials private to their car.
                    renderer.GetPropertyBlock(properties,0);
                    mayShare=properties.isEmpty && ((MeshRenderer)renderer).additionalVertexStreams==null;
                }
                part.NativeSlots=new bool[part.Opaque.Length];bool complete=part.HasOpaque;
                for(int slot=0;slot<part.Opaque.Length;slot++)
                {
                    if(!part.Opaque[slot])continue;
                    var original=shared[slot];
                    if(!Supported(original)){complete=false;continue;}
                    Source source;
                    if(!sources.TryGetValue(original,out source))
                    {
                        source=new Source{Material=original,Crc=original.ComputeCRC(),SurfaceKey=SurfaceKey(original)};
                        sources.Add(original,source);sourceQueue.Add(source);
                    }
                    // Our shader supports the per-car ID as an instanced value.
                    // The original Standard material need not opt into instancing:
                    // requiring that flag used to create one material per car.
                    bool sharedInstance=mayShare && SystemInfo.supportsInstancing;
                    Variant variant=null;
                    var cache=part.Interior?interior:exterior;
                    if(sharedInstance)variant=part.Interior?source.SharedInterior:source.SharedExterior;
                    else cache.TryGetValue(original,out variant);
                    if(variant==null)
                    {
                        var snow=new Material(original){shader=shader,name=original.name+" [DVSeasons native snow]",hideFlags=HideFlags.HideAndDontSave};
                        variant=new Variant{Source=source,Material=snow,Exterior=!part.Interior,Shared=sharedInstance,CarSlot=vehicle.Slot};
                        CopySource(variant);source.Variants.Add(variant);VariantCount++;
                        if(sharedInstance){if(part.Interior)source.SharedInterior=variant;else source.SharedExterior=variant;}
                        else cache.Add(original,variant);
                    }
                    variant.Owners.Add(vehicle);
                    entries.Add(new Binding{Renderer=renderer,Slot=slot,Variant=variant});
                    shared[slot]=variant.Material;part.NativeSlots[slot]=true;changed=true;BoundSlots++;
                    if(sharedInstance){SetRendererSlot(renderer,vehicle.Slot+1);SharedSlots++;}
                }
                part.NativeComplete=complete;
                if(changed)renderer.sharedMaterials=shared;
            }
            if(entries.Count>0)bindings.Add(vehicle,entries);
        }
        private void SetRendererSlot(Renderer renderer,float slot)
        {
            renderer.GetPropertyBlock(properties);properties.SetFloat(SlotId,slot);renderer.SetPropertyBlock(properties);
        }
        private void CopySource(Variant variant)
        {
            var original=variant.Source.Material;var material=variant.Material;
            material.CopyPropertiesFromMaterial(original);material.shaderKeywords=original.shaderKeywords;
            material.renderQueue=original.renderQueue;
            material.enableInstancing=variant.Shared || original.enableInstancing;
            material.SetFloat(CarId,variant.Shared?0:variant.CarSlot+1);
            material.SetFloat(SlotId,0);material.SetFloat("_DVPSNativeExterior",variant.Exterior?1:0);
            variant.SyncedCrc=material.ComputeCRC();variant.SurfaceKey=SurfaceKey(material);
        }
        private void RestoreShader(Material material,Material original)
        {
            if(material==null || material.shader!=shader)return;
            var keywords=material.shaderKeywords;int queue=material.renderQueue;
            material.shader=original!=null?original.shader:Shader.Find("Standard");
            material.shaderKeywords=keywords;material.renderQueue=queue;
        }
        public void Release(SnowVehicleRegistry.Vehicle vehicle)
        {
            List<Binding> entries;if(!bindings.TryGetValue(vehicle,out entries))return;
            var restored=new Dictionary<Variant,Material>();
            foreach(var entry in entries)
            {
                var variant=entry.Variant;var original=variant.Source.Material;Material replacement;
                if(!restored.TryGetValue(variant,out replacement))
                {
                    replacement=original;
                    if(variant.Material!=null && (original==null || variant.Material.ComputeCRC()!=variant.SyncedCrc))
                    {
                        // A live repaint must not write into the original shared
                        // material or lose its per-car values on season changes.
                        replacement=new Material(variant.Material){hideFlags=HideFlags.None,name=original!=null?original.name:"Vehicle material"};
                        RestoreShader(replacement,original);
                    }
                    restored.Add(variant,replacement);
                }
                if(entry.Renderer!=null)
                {
                    materials.Clear();entry.Renderer.GetSharedMaterials(materials);
                    if(entry.Slot<materials.Count)
                    {
                        if(materials[entry.Slot]==variant.Material)
                        {materials[entry.Slot]=replacement;entry.Renderer.sharedMaterials=materials.ToArray();}
                        else RestoreShader(materials[entry.Slot],original);
                    }
                    // Preserve unrelated changes made to this block by other
                    // controllers. Standard ignores this private, inert ID; it
                    // does not disable native instancing (covered by GPU test).
                    if(variant.Shared)SetRendererSlot(entry.Renderer,-1);
                }
                BoundSlots--;if(variant.Shared)SharedSlots--;
            }
            foreach(var item in restored)
            {
                var variant=item.Key;variant.Owners.Remove(vehicle);
                if(variant.Owners.Count>0)continue;
                var source=variant.Source;source.Variants.Remove(variant);
                if(source.SharedExterior==variant)source.SharedExterior=null;
                if(source.SharedInterior==variant)source.SharedInterior=null;
                Destroy(variant.Material);
                VariantCount--;
                if(source.Variants.Count==0){sources.Remove(source.Material);sourceQueue.Remove(source);}
            }
            bindings.Remove(vehicle);vehicle.NativeComplete=false;
            foreach(var part in vehicle.Parts){part.NativeComplete=false;part.NativeSlots=null;}
        }
        private void SynchronizeSources()
        {
            int count=Math.Min(8,sourceQueue.Count);
            while(count-->0)
            {
                sourcePoll%=sourceQueue.Count;var source=sourceQueue[sourcePoll++];
                if(source.Material==null)continue;
                int crc=source.Material.ComputeCRC();if(crc==source.Crc)continue;
                source.Crc=crc;bool supported=Supported(source.Material);
                int surface=supported?SurfaceKey(source.Material):source.SurfaceKey;
                bool recapture=surface!=source.SurfaceKey;source.SurfaceKey=surface;
                foreach(var variant in source.Variants)
                {
                    if(!supported){foreach(var owner in variant.Owners)owner.PartsPending=true;continue;}
                    CopySource(variant);
                    if(recapture)foreach(var owner in variant.Owners)owner.PartsPending=true;
                }
            }
        }
        public void Prepare(IList<SnowVehicleRegistry.Vehicle> vehicles,SnowVehicleRegistry registry,
            RenderTexture heights,RenderTexture snow,float amount,float glare,Texture noise,Color ambient)
        {
            if(data==null)return;
            SnowPerformance.NativeSharing(SharedSlots,BoundSlots-SharedSlots,VariantCount);
            if(BoundSlots==0){Shader.SetGlobalFloat("_DVPSNativeMaterialsActive",0);Shader.SetGlobalFloat("_DVPSNativeAmount",0);return;}
            SynchronizeSources();int end=0;
            foreach(var vehicle in vehicles)
            {
                if(vehicle.Root==null)continue;
                int slot=vehicle.Slot;end=Math.Max(end,slot+1);var rotation=vehicle.Root.rotation;
                frames[slot]=new Frame{WorldToVehicle=vehicle.Root.worldToLocalMatrix,Area=vehicle.Area,SnowArea=vehicle.SnowArea,
                    Sides=registry.SideSnowAmount!=null?registry.SideSnowAmount(vehicle.Source):Vector4.zero,
                    Rotation=new Vector4(rotation.x,rotation.y,rotation.z,rotation.w),
                    State=new Vector4(vehicle.SnowReady && registry.IsSnowSelected(vehicle)?1:0,
                        registry.SnowRemaining!=null?Mathf.Clamp01(registry.SnowRemaining(vehicle.Source)):1,0,0)};
            }
            if(end>0)data.SetData(frames,0,0,end);
            Shader.SetGlobalBuffer("_DVPSNativeFrames",data);
            Shader.SetGlobalTexture("_DVPSNativeHeights",heights);Shader.SetGlobalTexture("_DVPSNativeSnow",snow);
            Shader.SetGlobalTexture("_DVPSNativeNoise",noise);
            Shader.SetGlobalFloat("_DVPSNativeAmount",amount);Shader.SetGlobalFloat("_DVPSNativeGlare",glare);
            Shader.SetGlobalFloat("_DVPSNativeMaterialsActive",1);Shader.SetGlobalColor("_DVPSNativeAmbient",ambient);
            // One car per frame; new cargo/interior events already invalidate
            // their owner immediately. No all-renderer scan during rendering.
            if(vehicles.Count>0)
            {
                poll%=vehicles.Count;var vehicle=vehicles[poll++];List<Binding> entries;
                if(bindings.TryGetValue(vehicle,out entries))foreach(var entry in entries)
                {
                    if(entry.Renderer==null)continue;
                    var variant=entry.Variant;materials.Clear();entry.Renderer.GetSharedMaterials(materials);
                    if(entry.Slot>=materials.Count || materials[entry.Slot]!=variant.Material)
                    {if(entry.Slot<materials.Count)RestoreShader(materials[entry.Slot],variant.Source.Material);vehicle.PartsPending=true;break;}
                    if(SurfaceKey(variant.Material)!=variant.SurfaceKey){vehicle.PartsPending=true;break;}
                    if(variant.Shared)
                    {
                        entry.Renderer.GetPropertyBlock(properties,entry.Slot);
                        if(!properties.isEmpty){vehicle.PartsPending=true;break;}
                        entry.Renderer.GetPropertyBlock(properties);
                        if(properties.GetFloat(SlotId)!=vehicle.Slot+1)
                        {properties.SetFloat(SlotId,vehicle.Slot+1);entry.Renderer.SetPropertyBlock(properties);}
                    }
                }
            }
        }
        public void Dispose()
        {
            foreach(var vehicle in new List<SnowVehicleRegistry.Vehicle>(bindings.Keys))Release(vehicle);
            if(data!=null)data.Release();data=null;shader=null;poll=sourcePoll=0;
            Shader.SetGlobalFloat("_DVPSNativeAmount",0);Shader.SetGlobalFloat("_DVPSNativeMaterialsActive",0);
        }
        private static void Destroy(UnityEngine.Object obj)
        {if(obj!=null){if(Application.isPlaying)UnityEngine.Object.Destroy(obj);else UnityEngine.Object.DestroyImmediate(obj);}}
    }
}
