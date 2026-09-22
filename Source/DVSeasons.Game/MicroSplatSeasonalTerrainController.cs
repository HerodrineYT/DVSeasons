using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DVSeasons.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DVSeasons.Mod
{
    internal sealed class MicroSplatSeasonalTerrainController : IDisposable
    {
        private sealed class Binding
        {
            public Material Material;
            public Shader Shader;
            public string Property;
            public Texture Original;
            public Texture Applied;
            public bool IsDistantTerrain;
            public bool WaitingForArray;
        }

        private sealed class LayeredArrayState
        {
            public Texture2DArray Clear;
            public RenderTexture Output;
            public int CoverageStep = -1;
            public int AppliedLayerCount;
            public bool Failed;
            public Texture2DArray PendingWinter;
            public int PendingCoverageStep = -1, PendingLayerCount, NextLayer;
            public bool Queued;
        }

        private sealed class TerrainLayerBinding
        {
            public TerrainLayer Layer;
            public Texture2D Original;
            public Texture2D Winter;
            public Texture2D Applied;
            public int WinterRank;
        }

        private static readonly string[] DiffuseProperties =
        {
            // All four arrays participate at different viewing distances. Each is
            // now given its own layered copy based on that property's original
            // array; sharing one global clear array was what produced the large
            // neon-green mosaic patches around industrial terrain.
            "_Diffuse",
            "_ClusterDiffuse2",
            "_ClusterDiffuse3",
            "_DistanceResampleHackDiff"
        };
        // The order is deliberately spread across the 16 MicroSplat slots. Nearby
        // slot numbers often describe related ground materials, so replacing them
        // sequentially would turn one surface family winter-white all at once.
        private static readonly int[] WinterLayerOrder =
        {
            // Slots 13 and 12 are the two broad grass/yard ground families seen on
            // the screenshots. Both must be covered first: leaving slot 12 until
            // the final step kept a large brown/green surface snow-free through
            // almost the whole winter transition.
            13, 12, 1, 6, 11, 0, 5, 10, 15, 4, 9, 14, 3, 8, 2, 7
        };
        private readonly Dictionary<string, Binding> bindings = new Dictionary<string, Binding>();
        private readonly SeasonAssetBundleRepository texturePack;
        private readonly Dictionary<string, Texture> canonicalSummerArrays =
            new Dictionary<string, Texture>(StringComparer.Ordinal);
        private readonly Dictionary<int, LayeredArrayState> layeredArrays =
            new Dictionary<int, LayeredArrayState>();
        private readonly Dictionary<int, TerrainLayerBinding> terrainLayerBindings =
            new Dictionary<int, TerrainLayerBinding>();
        private readonly Queue<LayeredArrayState> pendingArrays = new Queue<LayeredArrayState>();
        private int arrayWorkFrame = -1;
        private float nextScanTime;
        private float nextReapplyTime;
        private int lastSeasonKey = int.MinValue;
        private bool discoveryLogged;
        private bool restored=true;
        public int RestorePassCount { get; private set; }
        private bool missingWinterArrayLogged;
        private readonly SpringTerrainTint springTint;
        private int springStep;
        private sealed class SceneScan
        {
            public Scene Scene;
            public IEnumerator<int> Work;
        }
        private struct SceneNode { public Transform Transform; public bool Distant; }
        private sealed class SceneScanBuffers
        {
            public readonly List<GameObject> Roots = new List<GameObject>();
            public readonly Queue<SceneNode> Pending = new Queue<SceneNode>();
            public readonly HashSet<int> SeenMaterials = new HashSet<int>();
            public void Clear()
            {
                Roots.Clear();Pending.Clear();SeenMaterials.Clear();
            }
        }
        // A priority scene can suspend another iterator. Each live walk owns its
        // buffers exclusively; completed walks reuse storage without world refs.
        private readonly Stack<SceneScanBuffers> sceneScanBuffers = new Stack<SceneScanBuffers>();
        private readonly LinkedList<SceneScan> pendingScenes = new LinkedList<SceneScan>();
        private readonly HashSet<int> queuedScenes = new HashSet<int>();
        private readonly List<Material> rendererMaterials = new List<Material>();
        private bool sceneEventsSubscribed;
        private bool includeDistantMaterials;
        // The native landscape must not wait behind unrelated scene transforms.
        // DV caches distant materials and visibility-swapped MicroSplat instances;
        // use those small lists before the bounded general scene fallback.
        private static readonly Type DistantTerrainType = Type.GetType("DV.WorldTools.DistantTerrain, DV.DistantTerrain", false);
        private static readonly Type MicroSplatTerrainType = Type.GetType("JBooth.MicroSplat.MicroSplatTerrain, JBooth.MicroSplat.Core", false);
        private static readonly FieldInfo DistantMaterials = DistantTerrainType == null ? null :
            DistantTerrainType.GetField("materials", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MicroSplatInstances = MicroSplatTerrainType == null ? null :
            MicroSplatTerrainType.GetField("sInstances", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo MicroSplatTemplate = MicroSplatTerrainType == null ? null :
            MicroSplatTerrainType.GetField("templateMaterial", BindingFlags.Instance | BindingFlags.Public);
        private static readonly FieldInfo MicroSplatInstanceMaterial = MicroSplatTerrainType == null ? null :
            MicroSplatTerrainType.GetField("matInstance", BindingFlags.Instance | BindingFlags.Public);
        private readonly List<Component> distantTerrainSources = new List<Component>();
        private readonly List<MeshRenderer> distantRenderers = new List<MeshRenderer>();
        private readonly HashSet<int> distantSourceIds = new HashSet<int>();
        private readonly HashSet<int> distantRendererIds = new HashSet<int>();
        private readonly HashSet<int> nativeMaterialsVisited = new HashSet<int>();
        private bool nativeSourcesDirty = true;
        private float nextNativeScanTime;
        internal int NativeSourceSnapshotCount { get; private set; }
        internal int NativeSceneScanCount { get; private set; }
        internal int LastDiscoverySteps { get; private set; }
        internal int LastMaterialVisits { get; private set; }
        internal int LayerBlitCount { get; private set; }
        internal int LayerMipGenerationCount { get; private set; }
        internal int LastLayerBuildCopies { get; private set; }

        public MicroSplatSeasonalTerrainController(SeasonAssetBundleRepository texturePack)
        {
            this.texturePack = texturePack;
            springTint=new SpringTerrainTint(texturePack);
        }

        public void Apply(SeasonState state, SeasonModSettings settings, bool proceduralSnow = false, float? coverageOverride = null)
        {
            if (state == null || settings == null) return;
            if (!settings.SeasonalTexturesEnabled || !settings.TerrainTextureChanges ||
                settings.TextureChangeStrength <= 0.001f)
            {
                Restore();
                return;
            }

            restored=false;
            includeDistantMaterials=settings.DistantTerrainSeasonal;
            if(!sceneEventsSubscribed)
            {
                SceneManager.sceneLoaded+=SceneLoaded;
                SceneManager.sceneUnloaded+=SceneUnloaded;
                DV.TerrainSystem.TerrainGrid.Initialized+=NativeTerrainInitialized;
                sceneEventsSubscribed=true;
                nextScanTime=0f;
            }
            if (nativeSourcesDirty || Time.realtimeSinceStartup >= nextNativeScanTime)
            {
                nextNativeScanTime = Time.realtimeSinceStartup + .5f;
                using(SnowPerformance.Measure("terrain-native-discovery"))
                    if(ScanNativeLandscapeMaterials()>0) nextReapplyTime=0f;
            }
            if (Time.realtimeSinceStartup >= nextScanTime)
            {
                nextScanTime = Time.realtimeSinceStartup + 10f;
                ScanTerrains();
                for(int i=0;i<SceneManager.sceneCount;i++) QueueScene(SceneManager.GetSceneAt(i),false);
            }
            using(SnowPerformance.Measure("terrain-material-discovery")) AdvanceDiscovery();

            // Native landscape arrays cover every terrain LOD, including native
            // distant terrain beyond the screen-space snow exposure maps.
            var coverage = coverageOverride ?? (settings.GroundSnowEnabled
                ? Mathf.Clamp01(state.SnowAmount * settings.GroundSnowStrength * settings.TextureChangeStrength)
                : 0f);
            var coverageStep = SnowCoverProfile.GetGroundTextureStep(coverage);
            springStep=Mathf.RoundToInt(Mathf.Clamp01(SpringAppearance.Weight(state)*settings.TextureChangeStrength)*32);
            var seasonKey = coverageStep |
                (settings.DistantTerrainSeasonal ? 0x100 : 0) | (springStep << 10);
            if (seasonKey != lastSeasonKey)
            {
                lastSeasonKey = seasonKey;
                ApplyCoverage(coverageStep, settings.DistantTerrainSeasonal);
                // Streamed material clones can still reference these outputs.
                // Keep them alive until their originals have been restored.
            }
            else if (Time.realtimeSinceStartup >= nextReapplyTime)
            {
                nextReapplyTime = Time.realtimeSinceStartup + 1f;
                ReapplyToNewBindings(coverageStep, settings.DistantTerrainSeasonal);
            }
            using(SnowPerformance.Measure("terrain-array-build"))
                AdvanceLayeredArrays(coverageStep);
        }

        public void Dispose()
        {
            Restore();
            StopDiscovery();
            bindings.Clear();
            terrainLayerBindings.Clear();
            canonicalSummerArrays.Clear();
            foreach (var state in layeredArrays.Values)
                if (state.Output != null) UnityEngine.Object.Destroy(state.Output);
            layeredArrays.Clear();pendingArrays.Clear();
            springTint.Dispose();
            nextScanTime = 0f;
            nextReapplyTime = 0f;
            discoveryLogged = false;
            missingWinterArrayLogged = false;
        }

        private void SceneLoaded(Scene scene,LoadSceneMode mode)
        {
            // An unrelated streamed scene must not discard every cached distant
            // ring and repeat a global Resources snapshot. Inspect only this
            // scene, before the general bounded material-discovery backlog.
            QueueScene(scene,true);
        }

        private void SceneUnloaded(Scene scene)
        {
            for(var node=pendingScenes.First;node!=null;)
            {
                var next=node.Next;
                if(node.Value.Scene.handle==scene.handle)
                {node.Value.Work.Dispose();pendingScenes.Remove(node);queuedScenes.Remove(scene.handle);}
                node=next;
            }
            // Unity null checks remove destroyed sources on the next cheap poll.
            nextNativeScanTime=0f;
        }

        private void NativeTerrainInitialized() { nativeSourcesDirty=true; }

        private int ScanNativeLandscapeMaterials()
        {
            if(nativeSourcesDirty)
            {
                nativeSourcesDirty=false;
                // No global Resources snapshot or recursive subtree query on
                // startup/grid changes. Discover loaded, inactive and late roots
                // through the same bounded breadth-first scene walk.
                for(int i=0;i<SceneManager.sceneCount;i++)
                {
                    var scene=SceneManager.GetSceneAt(i);
                    for(var node=pendingScenes.First;node!=null;node=node.Next)
                    {
                        if(node.Value.Scene.handle!=scene.handle) continue;
                        node.Value.Work.Dispose();pendingScenes.Remove(node);queuedScenes.Remove(scene.handle);break;
                    }
                    QueueScene(scene,true);
                }
            }
            distantTerrainSources.RemoveAll(source=>source==null);
            distantRenderers.RemoveAll(renderer=>renderer==null);
            int added=0;
            nativeMaterialsVisited.Clear();
            if(includeDistantMaterials)
            {
                // Start() may populate the cache after our first probe. Re-read
                // these few materials; also catch renderer material replacements.
                foreach(var source in distantTerrainSources)
                {
                    if(source==null || DistantMaterials==null) continue;
                    var materials=DistantMaterials.GetValue(source) as IList;
                    if(materials!=null) foreach(var value in materials)
                        added+=ScanNativeMaterial(value as Material);
                }
                foreach(var renderer in distantRenderers)
                {
                    if(renderer==null) continue;
                    renderer.GetSharedMaterials(rendererMaterials);
                    foreach(var material in rendererMaterials) added+=ScanNativeMaterial(material);
                }
            }
            var instances=MicroSplatInstances==null ? null : MicroSplatInstances.GetValue(null) as IList;
            if(instances!=null) foreach(var instance in instances)
            {
                var component=instance as Component;
                if(component==null) continue;
                // MicroSplatVisibilityHack replaces Terrain.materialTemplate with
                // a shadow material out of view. Prepare its real matInstance so
                // turning the camera never reveals an unconverted terrain tile.
                if(MicroSplatTemplate!=null) added+=ScanNativeMaterial(MicroSplatTemplate.GetValue(instance) as Material);
                if(MicroSplatInstanceMaterial!=null) added+=ScanNativeMaterial(MicroSplatInstanceMaterial.GetValue(instance) as Material);
            }
            return added;
        }

        private void AddNativeDistantSource(Component source)
        {
            if(source==null || !distantSourceIds.Add(source.GetInstanceID())) return;
            distantTerrainSources.Add(source);
            nextNativeScanTime=0f;
        }

        private int ScanNativeMaterial(Material material)
        {
            return material!=null && nativeMaterialsVisited.Add(material.GetInstanceID())
                ? ScanRendererMaterial(material,includeDistantMaterials) : 0;
        }

        private void QueueScene(Scene scene,bool priority)
        {
            if(!scene.IsValid() || !scene.isLoaded) return;
            if(!queuedScenes.Add(scene.handle))
            {
                if(priority) for(var node=pendingScenes.First;node!=null;node=node.Next)
                    if(node.Value.Scene.handle==scene.handle)
                    {pendingScenes.Remove(node);pendingScenes.AddFirst(node);break;}
                return;
            }
            var scan=new SceneScan {Scene=scene,Work=ScanScene(scene).GetEnumerator()};
            if(priority) pendingScenes.AddFirst(scan); else pendingScenes.AddLast(scan);
        }

        private void AdvanceDiscovery()
        {
            LastDiscoverySteps=0; LastMaterialVisits=0;
            long start=System.Diagnostics.Stopwatch.GetTimestamp();
            for(int i=0;i<256 && pendingScenes.Count>0;i++)
            {
                var work=pendingScenes.First.Value;
                LastDiscoverySteps++;
                if(!work.Scene.IsValid() || !work.Scene.isLoaded || !work.Work.MoveNext())
                {
                    work.Work.Dispose();pendingScenes.RemoveFirst();queuedScenes.Remove(work.Scene.handle);
                    if(pendingScenes.Count==0) nextScanTime=Time.realtimeSinceStartup+10f;
                }
                if((System.Diagnostics.Stopwatch.GetTimestamp()-start)*1000d/System.Diagnostics.Stopwatch.Frequency>=.25d) break;
            }
        }

        private IEnumerable<int> ScanScene(Scene scene)
        {
            NativeSceneScanCount++;
            SceneScanBuffers buffers=null;
            try
            {
                using(SnowPerformance.Measure("terrain-scene-roots"))
                {
                    buffers=sceneScanBuffers.Count>0 ? sceneScanBuffers.Pop() : new SceneScanBuffers();
                    scene.GetRootGameObjects(buffers.Roots);
                }
                foreach(var root in buffers.Roots)
                {
                    if(root!=null) buffers.Pending.Enqueue(new SceneNode {Transform=root.transform});
                    yield return 0;
                }
                while(buffers.Pending.Count>0)
                {
                    var entry=buffers.Pending.Dequeue();var node=entry.Transform;
                    if(node==null) {yield return 0;continue;}
                    using(SnowPerformance.Measure("terrain-node-materials"))
                    {
                        // A mod can add a native source after its scene loaded.
                        // Preserve the private-cache and inactive-source discovery.
                        var native=DistantTerrainType!=null ? node.GetComponent(DistantTerrainType) : null;
                        if(native!=null) {AddNativeDistantSource(native);entry.Distant=true;}
                        var renderer=node.GetComponent<Renderer>();
                        if(renderer!=null)
                        {
                            var meshRenderer=renderer as MeshRenderer;
                            if(entry.Distant && meshRenderer!=null && distantRendererIds.Add(renderer.GetInstanceID()))
                                distantRenderers.Add(meshRenderer);
                            renderer.GetSharedMaterials(rendererMaterials);
                            foreach(var material in rendererMaterials)
                            {
                                if(material==null || !buffers.SeenMaterials.Add(material.GetInstanceID())) continue;
                                LastMaterialVisits++;
                                if(ScanRendererMaterial(material,includeDistantMaterials)>0) nextReapplyTime=0f;
                            }
                        }
                    }
                    using(SnowPerformance.Measure("terrain-node-layers"))
                    {
                        var terrain=node.GetComponent<Terrain>();
                        if(terrain!=null) ScanTerrain(terrain);
                    }
                    yield return 0;
                    for(int child=0;node!=null && child<node.childCount;child++)
                    {
                        buffers.Pending.Enqueue(new SceneNode {Transform=node.GetChild(child),Distant=entry.Distant});
                        yield return 0;
                    }
                }
            }
            finally
            {
                if(buffers!=null)
                {
                    // Runs on completion, scene unload, grid restart and Dispose.
                    buffers.Clear();
                    if(sceneScanBuffers.Count<4) sceneScanBuffers.Push(buffers);
                }
            }
        }

        private void StopDiscovery()
        {
            if(sceneEventsSubscribed) SceneManager.sceneLoaded-=SceneLoaded;
            if(sceneEventsSubscribed) SceneManager.sceneUnloaded-=SceneUnloaded;
            if(sceneEventsSubscribed) DV.TerrainSystem.TerrainGrid.Initialized-=NativeTerrainInitialized;
            sceneEventsSubscribed=false;
            foreach(var scan in pendingScenes) scan.Work.Dispose();
            sceneScanBuffers.Clear();
            pendingScenes.Clear();queuedScenes.Clear();rendererMaterials.Clear();
            distantTerrainSources.Clear();distantRenderers.Clear();distantSourceIds.Clear();distantRendererIds.Clear();
            nativeMaterialsVisited.Clear();nativeSourcesDirty=true;nextNativeScanTime=0f;
        }

        private int ScanTerrains()
        {
            int added=0;
            foreach(var terrain in Terrain.activeTerrains) if(terrain!=null) added+=ScanTerrain(terrain);
            return added;
        }

        private int ScanTerrain(Terrain terrain)
        {
            int added=0;
            var material=terrain.materialTemplate;
            if(material!=null) added+=ScanMaterial(material,false);
            return added+ScanTerrainLayers(terrain);
        }

        private int ScanRendererMaterial(Material material,bool includeDistantTerrain)
        {
            // Preserve the exact vanilla signatures: broad terrain-name matches
            // previously changed unrelated custom-map impostor materials.
            if(IsBuiltInMicroSplatMaterial(material)) return ScanMaterial(material,false);
            return includeDistantTerrain && IsDistantTerrainMaterial(material) ? ScanDistantTerrainMaterial(material) : 0;
        }

        private void Scan(bool includeDistantTerrain)
        {
            // Restore is exceptional: reconcile even unbound material assets that
            // cloned our render texture before releasing it. Normal gameplay uses
            // bounded scene discovery, never a global material snapshot.
            var terrains = Terrain.activeTerrains;
            var added = 0;
            var rendererMaterialAdded = 0;
            var distantAdded = 0;
            for (var i = 0; i < terrains.Length; i++)
            {
                var terrain = terrains[i];
                if (terrain == null) continue;
                var material = terrain.materialTemplate;
                if (material != null) added += ScanMaterial(material, false);
                added += ScanTerrainLayers(terrain);
            }

            // DV99 does not expose every streamed landscape tile through
            // Terrain.activeTerrains. Some near tiles are ordinary renderers which
            // share the built-in MicroSplat material from sharedassets5. Looking for
            // that exact material signature catches those green islands without
            // reviving the old broad "terrain-like" scan that also modified custom
            // map impostors and produced giant white/blurred surfaces.
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            for (var i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (IsBuiltInMicroSplatMaterial(material))
                {
                    rendererMaterialAdded += ScanMaterial(material, false);
                    continue;
                }
                if (includeDistantTerrain && IsDistantTerrainMaterial(material))
                    distantAdded += ScanDistantTerrainMaterial(material);
            }
            added += rendererMaterialAdded + distantAdded;

            if (!discoveryLogged && (added > 0 || terrains.Length > 0))
            {
                discoveryLogged = true;
                Debug.Log("[DVSeasons] Terrain scan: " + bindings.Count +
                    " compatible array binding(s), " + terrainLayerBindings.Count +
                    " direct built-in TerrainLayer binding(s); renderer MicroSplat is matched " +
                    "by its built-in 16-layer signature and DistantTerrain only by the exact " +
                    "DV/DistantTerrain shader.");
            }
            if (rendererMaterialAdded > 0)
                Debug.Log("[DVSeasons] Bound " + rendererMaterialAdded +
                    " streamed renderer MicroSplat array(s).");
            if (distantAdded > 0)
                Debug.Log("[DVSeasons] Bound " + distantAdded +
                    " exact DistantTerrain _Splats material array(s).");
        }

        private int ScanMaterial(Material material, bool isDistantTerrain)
        {
            var added = 0;
            for (var propertyIndex = 0; propertyIndex < DiffuseProperties.Length; propertyIndex++)
            {
                var property = DiffuseProperties[propertyIndex];
                if (!HasTextureProperty(material, property)) continue;
                var observed = material.GetTexture(property);
                if (!IsLandscapeArray(observed)) continue;
                var key = material.GetInstanceID() + "|" + property;
                Binding existing;
                if (bindings.TryGetValue(key, out existing) && IsCurrentTextureBinding(existing))
                    continue;
                Texture original;
                if (IsSeasonalTerrainTexture(observed))
                {
                    original = OriginalFor(observed);
                    if (original == null && !canonicalSummerArrays.TryGetValue(property, out original)) continue;
                }
                else
                {
                    original = observed;
                    if (!canonicalSummerArrays.ContainsKey(property))
                        canonicalSummerArrays.Add(property, observed);
                }
                // A material can keep its instance ID while its shader is replaced.
                // Replace such a stale binding instead of retaining assumptions
                // about the old shader's property layout.
                bindings[key] = new Binding
                {
                    Material = material,
                    Shader = material.shader,
                    Property = property,
                    Original = original,
                    IsDistantTerrain = isDistantTerrain
                };
                added++;
            }
            return added;
        }

        private int ScanTerrainLayers(Terrain terrain)
        {
            var data = terrain == null ? null : terrain.terrainData;
            var layers = data == null ? null : data.terrainLayers;
            // DV99's material-free near terrain uses the same sixteen logical slots
            // as the MicroSplat arrays. Do not guess on custom terrains: a different
            // layer count has no reliable mapping to the bundled winter set.
            if (layers == null || layers.Length != WinterLayerOrder.Length) return 0;

            var added = 0;
            for (var layerIndex = 0; layerIndex < layers.Length; layerIndex++)
            {
                var layer = layers[layerIndex];
                if (layer == null || layer.diffuseTexture == null ||
                    terrainLayerBindings.ContainsKey(layer.GetInstanceID()))
                    continue;
                Texture2D winter;
                if (!texturePack.TryGetTerrainLayer(SeasonKind.Winter, layerIndex, out winter) ||
                    winter == null)
                    continue;
                terrainLayerBindings.Add(layer.GetInstanceID(), new TerrainLayerBinding
                {
                    Layer = layer,
                    Original = layer.diffuseTexture,
                    Winter = winter,
                    WinterRank = GetWinterRank(layerIndex)
                });
                added++;
            }
            return added;
        }

        private static int GetWinterRank(int layerIndex)
        {
            for (var rank = 0; rank < WinterLayerOrder.Length; rank++)
                if (WinterLayerOrder[rank] == layerIndex) return rank;
            return WinterLayerOrder.Length;
        }

        private int ScanDistantTerrainMaterial(Material material)
        {
            const string property = "_Splats";
            if (!HasTextureProperty(material, property)) return 0;
            var observed = material.GetTexture(property);
            if (!IsLandscapeArray(observed)) return 0;

            var key = material.GetInstanceID() + "|" + property;
            Binding existing;
            if (bindings.TryGetValue(key, out existing) && IsCurrentTextureBinding(existing))
                return 0;
            Texture original;
            if (IsSeasonalTerrainTexture(observed))
            {
                original = OriginalFor(observed);
                if (original == null && !canonicalSummerArrays.TryGetValue(property, out original)) return 0;
            }
            else
            {
                original = observed;
                if (!canonicalSummerArrays.ContainsKey(property))
                    canonicalSummerArrays.Add(property, observed);
            }
            bindings[key] = new Binding
            {
                Material = material,
                Shader = material.shader,
                Property = property,
                Original = original,
                IsDistantTerrain = true
            };
            return 1;
        }

        private static bool IsDistantTerrainMaterial(Material material)
        {
            if (material == null || material.shader == null) return false;
            return string.Equals(material.shader.name, "DV/DistantTerrain",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasTextureProperty(Material material, string property)
        {
            if (material == null || string.IsNullOrEmpty(property)) return false;
            var shader = material.shader;
            if (shader == null) return false;

            // Material.HasProperty is not sufficient here. Unity can retain a saved
            // material value with the same name after a shader swap even when the
            // new shader exposes that name as a Color/Float. Calling GetTexture or
            // SetTexture in that state emits "doesn't have a texture property".
            // Unity 2019 exposes the declared type through Shader's property API.
            var propertyIndex = shader.FindPropertyIndex(property);
            return propertyIndex >= 0 &&
                shader.GetPropertyType(propertyIndex) == ShaderPropertyType.Texture;
        }

        private static bool IsCurrentTextureBinding(Binding binding)
        {
            return binding != null && binding.Material != null &&
                binding.Shader != null && binding.Material.shader == binding.Shader &&
                HasTextureProperty(binding.Material, binding.Property);
        }

        private bool IsBuiltInMicroSplatMaterial(Material material)
        {
            if (!HasTextureProperty(material, "_Diffuse")) return false;
            var diffuse = material.GetTexture("_Diffuse");
            if (!IsLandscapeArray(diffuse)) return false;

            var materialName = material.name ?? string.Empty;
            var shaderName = material.shader == null ? string.Empty : material.shader.name;
            var textureName = diffuse.name ?? string.Empty;
            var materialSignature =
                materialName.StartsWith("MicroSplat", StringComparison.OrdinalIgnoreCase) ||
                shaderName.IndexOf("MicroSplat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                HasBuiltInMicroSplatCompanions(material);
            var arraySignature =
                string.Equals(textureName, "MicroSplatConfig_diff_tarray",
                    StringComparison.OrdinalIgnoreCase) ||
                IsSeasonalTerrainTexture(diffuse);
            return materialSignature && arraySignature;
        }

        private static bool HasBuiltInMicroSplatCompanions(Material material)
        {
            return HasTextureProperty(material, "_ClusterDiffuse2") &&
                HasTextureProperty(material, "_ClusterDiffuse3") &&
                HasTextureProperty(material, "_DistanceResampleHackDiff");
        }

        private void ApplyCoverage(int coverageStep, bool distantTerrainEnabled)
        {
            foreach (var binding in bindings.Values)
            {
                if (!IsCurrentTextureBinding(binding)) continue;
                Texture target;
                if (binding.IsDistantTerrain && !distantTerrainEnabled)
                    target = binding.Original;
                else if (!TryGetCoverageTexture(binding, coverageStep, out target))
                    target = binding.Original;
                target=target ?? binding.Original;
                if(!binding.WaitingForArray && (!binding.IsDistantTerrain || distantTerrainEnabled))
                    target=springTint.Get(binding.Original,target,springStep,coverageStep);
                Apply(binding, target);
            }
            ApplyTerrainLayers(coverageStep);
            Debug.Log("[DVSeasons] MicroSplat ground coverage step " + coverageStep + "/" +
                SnowCoverProfile.GroundTextureSteps + " applied (" + bindings.Count +
                " array binding(s), " + terrainLayerBindings.Count +
                " direct TerrainLayer binding(s)).");
        }

        private void ReapplyToNewBindings(int coverageStep, bool distantTerrainEnabled)
        {
            foreach (var binding in bindings.Values)
            {
                if (!IsCurrentTextureBinding(binding)) continue;
                Texture target;
                if (binding.IsDistantTerrain && !distantTerrainEnabled)
                    target = binding.Original;
                else if (!TryGetCoverageTexture(binding, coverageStep, out target))
                    target = binding.Original;
                target=target ?? binding.Original;
                if(!binding.WaitingForArray && (!binding.IsDistantTerrain || distantTerrainEnabled))
                    target=springTint.Get(binding.Original,target,springStep,coverageStep);
                var current = binding.Material.GetTexture(binding.Property);
                // Reassert our intended state when a streamed terrain material was
                // repopulated with an older DVSeasons array after the cover-mode key
                // had already changed. Preserve unrelated non-seasonal replacements
                // made by other mods.
                if (binding.Applied == null || current == binding.Applied ||
                    IsSeasonalTerrainTexture(current))
                {
                    if (current != target || binding.Applied != target)
                        Apply(binding, target);
                }
            }
            ApplyTerrainLayers(coverageStep);
        }

        private void ApplyTerrainLayers(int coverageStep)
        {
            var desiredLayerCount = Mathf.Clamp(Mathf.CeilToInt(
                coverageStep / (float)SnowCoverProfile.GroundTextureSteps *
                WinterLayerOrder.Length), 0, WinterLayerOrder.Length);
            var changed = false;
            foreach (var binding in terrainLayerBindings.Values)
            {
                if (binding.Layer == null) continue;
                var target = binding.WinterRank < desiredLayerCount
                    ? binding.Winter
                    : binding.Original;
                var current = binding.Layer.diffuseTexture;
                if (current == target)
                {
                    binding.Applied = target;
                    continue;
                }
                // Reassert a streamed/reloaded layer only while it still contains
                // our prior value or its original. Preserve unrelated third-party
                // replacements instead of taking ownership of arbitrary textures.
                if (binding.Applied == null || current == binding.Applied ||
                    current == binding.Original || current == binding.Winter)
                {
                    binding.Layer.diffuseTexture = target;
                    binding.Applied = target;
                    changed = true;
                }
            }
            if (changed) FlushTerrains();
        }

        private static void FlushTerrains()
        {
            var terrains = Terrain.activeTerrains;
            for (var i = 0; i < terrains.Length; i++)
                if (terrains[i] != null) terrains[i].Flush();
        }

        private static void Apply(Binding binding, Texture target)
        {
            if (!IsCurrentTextureBinding(binding) || target == null) return;
            if(binding.Material.GetTexture(binding.Property)!=target) binding.Material.SetTexture(binding.Property, target);
            binding.Applied = target;
        }

        private static bool IsLandscapeArray(Texture texture)
        {
            var array = texture as Texture2DArray;
            var rt = texture as RenderTexture;
            return array != null ? array.depth == 16 : rt != null &&
                rt.dimension == TextureDimension.Tex2DArray && rt.volumeDepth == 16;
        }

        private Texture OriginalFor(Texture texture)
        {
            var original = springTint.OriginalFor(texture);
            if (original != null) return original;
            foreach (var entry in layeredArrays.Values)
                if (entry.Output == texture) return entry.Clear;
            return null;
        }

        private bool IsSeasonalTerrainTexture(Texture texture)
        {
            if (texture == null) return false;
            if (texturePack.IsTerrainArray(texture)) return true;
            foreach (var state in layeredArrays.Values)
                if (texture == state.Output) return true;
            var name = texture.name;
            return !string.IsNullOrEmpty(name) &&
                name.IndexOf("DVSeasons", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool TryGetCoverageTexture(Binding binding, int coverageStep, out Texture texture)
        {
            texture = null;
            binding.WaitingForArray=false;
            if (coverageStep <= 0)
                return true;

            Texture2DArray winter;
            if (!texturePack.TryGetTerrainArray(SeasonKind.Winter, out winter))
            {
                if (!missingWinterArrayLogged)
                {
                    missingWinterArrayLogged = true;
                    Debug.LogWarning("[DVSeasons] Winter MicroSplat terrain array is unavailable; will retry loading it.");
                }
                return false;
            }
            missingWinterArrayLogged = false;

            if (coverageStep >= SnowCoverProfile.GroundTextureSteps)
            {
                texture = winter;
                return true;
            }

            var clear = binding.Original as Texture2DArray;
            if (!CanBuildLayeredArray(clear, winter))
            {
                Debug.LogWarning("[DVSeasons] MicroSplat array " + binding.Property +
                    " on material '" + binding.Material.name + "' does not expose the " +
                    "16 source layers required for a layered transition.");
                texture = winter;
                return true;
            }

            LayeredArrayState state;
            if (!layeredArrays.TryGetValue(clear.GetInstanceID(), out state))
            {
                state = new LayeredArrayState { Clear = clear };
                layeredArrays.Add(clear.GetInstanceID(), state);
            }
            if (!UpdateLayeredArray(state, coverageStep, winter))
                texture = winter;
            else
            {
                // An array is published only after all slices and mip levels
                // exist. Keep the previous valid season until that atomic swap.
                binding.WaitingForArray=state.CoverageStep<0;
                texture = state.CoverageStep>=0 ? state.Output : binding.Applied ?? binding.Original;
            }
            return true;
        }

        private bool UpdateLayeredArray(
            LayeredArrayState state,
            int coverageStep,
            Texture2DArray winter)
        {
            if (state.Failed) return false;
            if (state.CoverageStep == coverageStep && state.Output != null) return true;
            try
            {
                ValidateLayerSources(state.Clear, winter);

                var desiredLayerCount = Mathf.Clamp(Mathf.CeilToInt(
                    coverageStep / (float)SnowCoverProfile.GroundTextureSteps * winter.depth),
                    0,
                    winter.depth);
                bool changed=state.Output==null || desiredLayerCount!=state.AppliedLayerCount;

                if(state.CoverageStep<0 || state.Output==null)
                {
                    state.CoverageStep=-1;
                    QueueLayeredArray(state,coverageStep,desiredLayerCount,winter);
                    return true;
                }

                if (desiredLayerCount > state.AppliedLayerCount)
                {
                    for (var rank = state.AppliedLayerCount; rank < desiredLayerCount; rank++)
                        BlitLayer(winter, WinterLayerOrder[rank], state.Output);
                }
                else if (desiredLayerCount < state.AppliedLayerCount)
                {
                    for (var rank = state.AppliedLayerCount - 1; rank >= desiredLayerCount; rank--)
                        BlitLayer(state.Clear, WinterLayerOrder[rank], state.Output);
                }

                if(changed) {state.Output.GenerateMips();LayerMipGenerationCount++;}
                state.AppliedLayerCount = desiredLayerCount;
                state.CoverageStep = coverageStep;
                Debug.Log("[DVSeasons] Layered terrain snow step " + coverageStep + "/" +
                    SnowCoverProfile.GroundTextureSteps + ": " + state.AppliedLayerCount + "/" +
                    winter.depth + " MicroSplat layers use their winter texture.");
                return true;
            }
            catch (Exception exception)
            {
                state.Failed = true;
                if (state.Output != null) UnityEngine.Object.Destroy(state.Output);
                state.Output = null;
                Debug.LogWarning("[DVSeasons] Layer-by-layer terrain snow is unavailable for " +
                    "this source array; leaving it unchanged: " + exception.Message);
                return false;
            }
        }

        private void QueueLayeredArray(LayeredArrayState state,int coverageStep,int layerCount,Texture2DArray winter)
        {
            if(state.PendingWinter!=winter || state.PendingLayerCount!=layerCount) state.NextLayer=0;
            state.PendingWinter=winter;state.PendingCoverageStep=coverageStep;state.PendingLayerCount=layerCount;
            if(!state.Queued) {state.Queued=true;pendingArrays.Enqueue(state);}
        }

        private static void CreateLayeredOutput(LayeredArrayState state,Texture2DArray winter)
        {
            state.Output=new RenderTexture(winter.width,winter.height,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Default)
            {
                name="DVSeasons Layered Winter Terrain "+state.Clear.GetInstanceID(),dimension=TextureDimension.Tex2DArray,
                volumeDepth=winter.depth,useMipMap=true,autoGenerateMips=false,wrapMode=winter.wrapMode,
                filterMode=winter.filterMode,anisoLevel=Mathf.Max(state.Clear.anisoLevel,winter.anisoLevel),
                hideFlags=HideFlags.HideAndDontSave
            };
            if(!state.Output.Create()) throw new InvalidOperationException("GPU render array for the terrain transition could not be created");
        }

        private void AdvanceLayeredArrays(int coverageStep)
        {
            LastLayerBuildCopies=0;
            if(coverageStep<=0 || coverageStep>=SnowCoverProfile.GroundTextureSteps || arrayWorkFrame==Time.frameCount) return;
            arrayWorkFrame=Time.frameCount;
            var previous=RenderTexture.active;
            try
            {
                for(int budget=0;budget<2 && pendingArrays.Count>0;)
                {
                    var state=pendingArrays.Peek();var winter=state.PendingWinter;
                    if(state.Failed || winter==null || state.Clear==null)
                    {pendingArrays.Dequeue();state.Queued=false;continue;}
                    int desired=SnowCoverProfile.GetWinterTerrainLayerCount(coverageStep,winter.depth);
                    QueueLayeredArray(state,coverageStep,desired,winter);
                    try
                    {
                        if(state.Output==null) CreateLayeredOutput(state,winter);
                        int rank=state.NextLayer;
                        BlitLayer(rank<desired?winter:state.Clear,WinterLayerOrder[rank],state.Output);
                        budget++;LastLayerBuildCopies++;state.NextLayer++;
                        if(state.NextLayer<winter.depth) continue;
                        state.Output.GenerateMips();LayerMipGenerationCount++;
                        state.AppliedLayerCount=desired;state.CoverageStep=coverageStep;
                        pendingArrays.Dequeue();state.Queued=false;nextReapplyTime=0f;
                        Debug.Log("[DVSeasons] Completed staged terrain array '"+state.Clear.name+"': "+desired+"/"+winter.depth+
                            " winter layers, published after all slices and mip levels were ready.");
                    }
                    catch(Exception exception)
                    {
                        state.Failed=true;state.Queued=false;pendingArrays.Dequeue();
                        UnityEngine.Object.Destroy(state.Output);state.Output=null;nextReapplyTime=0f;
                        Debug.LogWarning("[DVSeasons] Staged terrain array failed: "+exception.Message);
                    }
                }
            }
            finally {RenderTexture.active=previous;}
        }

        private static bool CanBuildLayeredArray(Texture2DArray clear, Texture2DArray winter)
        {
            return clear != null && winter != null &&
                clear.depth == winter.depth &&
                clear.depth == WinterLayerOrder.Length;
        }

        private static void ValidateLayerSources(Texture2DArray clear, Texture2DArray winter)
        {
            if (!CanBuildLayeredArray(clear, winter))
                throw new InvalidOperationException(
                    "clear and winter terrain arrays have incompatible layer counts");
        }

        private void BlitLayer(
            Texture2DArray source,
            int slice,
            RenderTexture destination)
        {
            // DV's arrays are 1024px while the stable 0.1.34 seasonal bundle is
            // 512px. This Texture2DArray-aware Blit selects the real source slice,
            // scales it on the GPU and writes it into the matching destination
            // slice. It keeps each map's material numbering, unlike the old spring
            // fallback that produced the bright green industrial rectangle.
            Graphics.Blit(source, destination, slice, slice);
            LayerBlitCount++;
        }

        private void Restore()
        {
            if(restored) return;
            StopDiscovery();
            // Find clones created since the last scheduled scan before releasing
            // their shared spring render textures (otherwise they become black).
            Scan(true);
            restored=true;RestorePassCount++;
            foreach (var binding in bindings.Values)
            {
                if (!IsCurrentTextureBinding(binding))
                {
                    // The material may survive a streamed-scene shader swap. Its old
                    // property layout is no longer safe to query; a later Scan can
                    // replace this binding if the new shader is compatible.
                    binding.Applied = null;
                    continue;
                }
                var current = binding.Material.GetTexture(binding.Property);
                if (binding.Applied == null || current == binding.Applied ||
                    IsSeasonalTerrainTexture(current))
                    if(current!=binding.Original) binding.Material.SetTexture(binding.Property, binding.Original);
                binding.Applied = null;
            }
            var terrainLayersChanged = false;
            foreach (var binding in terrainLayerBindings.Values)
            {
                if (binding.Layer == null) continue;
                var current = binding.Layer.diffuseTexture;
                if (binding.Applied == null || current == binding.Applied ||
                    current == binding.Winter)
                {
                    if (current != binding.Original)
                    {
                        binding.Layer.diffuseTexture = binding.Original;
                        terrainLayersChanged = true;
                    }
                }
                binding.Applied = null;
            }
            if (terrainLayersChanged) FlushTerrains();
            lastSeasonKey = int.MinValue;
            nextReapplyTime = 0f;
            springStep=0;springTint.Dispose();
        }
    }
}
