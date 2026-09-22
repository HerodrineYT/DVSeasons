using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DVSeasons.Mod
{
    // Raw vertices of the shader-displaced distant landscape create phantom roofs.
    // It keeps its native seasonal textures; exposure uses detailed scene geometry.
    internal sealed class SnowExposureExclusions : IDisposable
    {
        private readonly Dictionary<int,MeshRenderer> proxies=new Dictionary<int,MeshRenderer>();
        private readonly Dictionary<int,Renderer> animals=new Dictionary<int,Renderer>();
        private readonly List<int> expired=new List<int>();
        private readonly List<Material> materials=new List<Material>();
        private readonly List<Renderer> hidden=new List<Renderer>();
        private readonly List<Renderer> visibleAnimals=new List<Renderer>();
        private readonly Plane[] frustum=new Plane[6];
        private readonly Plane[] rightFrustum=new Plane[6];
        private Material exclusionMaterial;
        private sealed class AnimalDraw
        {
            public Renderer Renderer;
            public int[] Slots;
            public float[] Cutoffs;
            public Texture[] Textures;
            public Vector4[] ST;
            public float NextRefresh;
        }
        private readonly Dictionary<int,AnimalDraw> animalDraws=new Dictionary<int,AnimalDraw>();
        private readonly List<AnimalDraw> drawRefresh=new List<AnimalDraw>();
        private int nextDrawRefresh;
        private sealed class AnimalSceneScan
        { public Scene Scene; public IEnumerator<Transform> Walk; }
        private readonly LinkedList<AnimalSceneScan> pendingAnimalScenes=new LinkedList<AnimalSceneScan>();
        private readonly HashSet<int> queuedAnimalScenes=new HashSet<int>();
        private bool subscribed;
        private bool changed;
        private float nextBackgroundScan;
        internal int LastAnimalDiscoverySteps {get;private set;}
        internal int LastRendererDiscoveryCount {get;private set;}
        internal int MaterialRefreshCount {get;private set;}

        public bool Update()
        {
            changed=false;
            if(!subscribed)
            {
                SceneManager.sceneLoaded+=SceneLoaded;
                subscribed=true;
                for(int i=0;i<SceneManager.sceneCount;i++) QueueAnimalScene(SceneManager.GetSceneAt(i),false);
            }
            if(pendingAnimalScenes.Count==0 && Time.realtimeSinceStartup>=nextBackgroundScan)
            {
                for(int i=0;i<SceneManager.sceneCount;i++) QueueAnimalScene(SceneManager.GetSceneAt(i),false);
                nextBackgroundScan=Time.realtimeSinceStartup+5f;
            }
            // Give newly streamed scenes a separate bounded pass. Restarting a
            // world-wide Resources search on every load produced large stalls.
            // Named animal branches take priority over unrelated scenery; found
            // renderers are excluded immediately, before this frame's capture.
            // Animals and terrain proxies share one walk and one total budget.
            // Previously three independent walks permanently spent >1 ms/frame.
            using(SnowPerformance.Measure("snow-exclusion-discovery")) DiscoverSceneAnimals();
            using(SnowPerformance.Measure("animal-material-refresh")) RefreshOneAnimalDraw();
            return changed;
        }

        private void SceneLoaded(Scene scene,LoadSceneMode mode) { QueueAnimalScene(scene,true); }

        private void QueueAnimalScene(Scene scene,bool priority)
        {
            if(!scene.IsValid() || !scene.isLoaded) return;
            if(!queuedAnimalScenes.Add(scene.handle))
            {
                if(priority) for(var node=pendingAnimalScenes.First;node!=null;node=node.Next)
                    if(node.Value.Scene.handle==scene.handle)
                    {pendingAnimalScenes.Remove(node);pendingAnimalScenes.AddFirst(node);break;}
                return;
            }
            var work=new AnimalSceneScan {Scene=scene,Walk=WalkAnimalScene(scene).GetEnumerator()};
            if(priority) pendingAnimalScenes.AddFirst(work); else pendingAnimalScenes.AddLast(work);
        }

        private void DiscoverSceneAnimals()
        {
            LastAnimalDiscoverySteps=0;
            LastRendererDiscoveryCount=0;
            long started=System.Diagnostics.Stopwatch.GetTimestamp();
            for(int count=0;count<96;count++)
            {
                if(pendingAnimalScenes.Count==0) break;
                var work=pendingAnimalScenes.First.Value;
                LastAnimalDiscoverySteps++;
                if(!work.Scene.IsValid() || !work.Scene.isLoaded || !work.Walk.MoveNext())
                {
                    work.Walk.Dispose();pendingAnimalScenes.RemoveFirst();
                    queuedAnimalScenes.Remove(work.Scene.handle);
                    if(pendingAnimalScenes.Count==0) nextBackgroundScan=Time.realtimeSinceStartup+5f;
                }
                else if(work.Walk.Current!=null)
                {
                    var node=work.Walk.Current;
                    // One native component lookup per node, including empty
                    // transforms. Bones need no name or Animator inspection.
                    var renderer=node.GetComponent<Renderer>();
                    if(renderer!=null)
                    {
                        LastRendererDiscoveryCount++;
                        var skin=renderer as SkinnedMeshRenderer;
                        if(skin!=null) VisitSkinned(skin);
                        else {var mesh=renderer as MeshRenderer;if(mesh!=null) Visit(mesh);}
                    }
                }
                if((System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000d/System.Diagnostics.Stopwatch.Frequency>=.25d) break;
            }
        }

        private static IEnumerable<Transform> WalkAnimalScene(Scene scene)
        {
            var roots=new List<GameObject>();scene.GetRootGameObjects(roots);
            var priority=new Queue<Transform>();var pending=new Queue<Transform>();
            // Enqueue siblings before descending a dense scenery hierarchy, so
            // an animal root placed late in a scene is not hidden behind all
            // children of the first root. Enumeration itself consumes budget.
            foreach(var root in roots)
            {
                if(root!=null)
                {
                    if(SnowAnimalExclusion.IsAnimalName(root.name)) priority.Enqueue(root.transform);
                    else pending.Enqueue(root.transform);
                }
                yield return null;
            }
            while(priority.Count>0 || pending.Count>0)
            {
                bool priorityBranch=priority.Count>0;
                var node=priorityBranch?priority.Dequeue():pending.Dequeue();
                if(node==null) {yield return null;continue;}
                // Keep breadth-first traversal; only the few scene roots need
                // name-based priority. Classification belongs to renderers,
                // rather than every bone/scenery transform in the whole map.
                yield return node;
                for(int child=0;node!=null && child<node.childCount;child++)
                {
                    var next=node.GetChild(child);
                    if(priorityBranch) priority.Enqueue(next); else pending.Enqueue(next);
                    yield return null;
                }
            }
        }

        public void SetExclusionShader(Shader shader)
        {
            if(exclusionMaterial==null && shader!=null)
                exclusionMaterial=new Material(shader) {hideFlags=HideFlags.HideAndDontSave};
        }

        public bool PrepareVisibleAnimals(Camera camera)
        {
            visibleAnimals.Clear();expired.Clear();
            if(exclusionMaterial==null || camera==null) return false;
            var secondaryFrustum=SnowCameraFrustum.Prepare(camera,frustum,rightFrustum);
            foreach(var pair in animals)
            {
                var renderer=pair.Value;
                if(renderer==null) {expired.Add(pair.Key);continue;}
                if(!renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.forceRenderingOff ||
                    renderer.shadowCastingMode==ShadowCastingMode.ShadowsOnly ||
                    (camera.cullingMask & (1<<renderer.gameObject.layer))==0 ||
                    !SnowCameraFrustum.Intersects(frustum,secondaryFrustum,renderer.bounds)) continue;
                visibleAnimals.Add(renderer);
            }
            foreach(var id in expired) {animals.Remove(id);animalDraws.Remove(id);}
            return visibleAnimals.Count>0;
        }

        public void RecordAnimalExclusions(CommandBuffer buffer)
        {
            if(exclusionMaterial==null || visibleAnimals.Count==0) return;
            // Hiding animals in the height map alone does not exclude their
            // feet/bellies: the snow height tolerance can still match ground.
            // Mark their actual visible pixels with the existing exclusion ID.
            // DrawRenderer follows each skinned pose and the native depth test
            // prevents exclusions from removing snow behind an animal.
            buffer.SetGlobalMatrix("_DVPSVehicleWorldToLocal",Matrix4x4.identity);
            buffer.SetGlobalFloat("_DVPSVehicleIndex",-1f);
            foreach(var renderer in visibleAnimals)
            {
                if(renderer==null) continue;
                AnimalDraw draw;
                if(!animalDraws.TryGetValue(renderer.GetInstanceID(),out draw))
                    draw=CacheAnimalDraw(renderer);
                for(var index=0;index<draw.Slots.Length;index++)
                {
                    float cutoff=draw.Cutoffs[index];
                    buffer.SetGlobalFloat("_DVPSVehicleCutoff",cutoff);
                    if(cutoff>0)
                    {
                        buffer.SetGlobalTexture("_DVPSVehicleAlbedo",draw.Textures[index]!=null?draw.Textures[index]:Texture2D.whiteTexture);
                        buffer.SetGlobalVector("_DVPSVehicleST",draw.ST[index]);
                    }
                    buffer.DrawRenderer(renderer,exclusionMaterial,draw.Slots[index],0);
                }
            }
        }

        private void Visit(MeshRenderer renderer)
        {
            if (IsAnimal(renderer))
            {
                RegisterAnimal(renderer);
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
                RegisterAnimal(renderer);
            }
        }

        private void RegisterAnimal(Renderer renderer)
        {
            int id=renderer.GetInstanceID();
            Renderer previous;
            if(!animals.TryGetValue(id,out previous) || previous!=renderer) changed=true;
            animals[id]=renderer;
            AnimalDraw draw;
            if(!animalDraws.TryGetValue(id,out draw) || draw.Renderer!=renderer) CacheAnimalDraw(renderer);
        }

        private AnimalDraw CacheAnimalDraw(Renderer renderer)
        {
            AnimalDraw draw;
            int id=renderer.GetInstanceID();
            // A streamed-out native object may release its ID before the next
            // cleanup pass. Never reuse its material slots for a new renderer.
            if(animalDraws.TryGetValue(id,out draw) && draw.Renderer!=renderer)
            {drawRefresh.Remove(draw);animalDraws.Remove(id);}
            if(!animalDraws.TryGetValue(id,out draw))
            {draw=new AnimalDraw {Renderer=renderer};animalDraws.Add(id,draw);drawRefresh.Add(draw);}
            var skinned=renderer as SkinnedMeshRenderer;
            var filter=skinned==null?renderer.GetComponent<MeshFilter>():null;
            var mesh=skinned!=null?skinned.sharedMesh:filter!=null?filter.sharedMesh:null;
            renderer.GetSharedMaterials(materials);
            var slots=new List<int>();var cutoffs=new List<float>();var textures=new List<Texture>();var transforms=new List<Vector4>();
            for(int slot=0;mesh!=null && slot<Mathf.Min(mesh.subMeshCount,materials.Count);slot++)
            {
                var source=materials[slot];
                if(source==null || source.renderQueue>2500) continue;
                float cutoff=source.IsKeywordEnabled("_ALPHATEST_ON") ||
                    string.Equals(source.GetTag("RenderType",false),"TransparentCutout",StringComparison.Ordinal)
                    ? (source.HasProperty("_Cutoff")?source.GetFloat("_Cutoff"):0.5f) : 0f;
                slots.Add(slot);cutoffs.Add(cutoff);
                textures.Add(cutoff>0?source.mainTexture:null);
                var scale=cutoff>0?source.mainTextureScale:Vector2.one;
                var offset=cutoff>0?source.mainTextureOffset:Vector2.zero;
                transforms.Add(new Vector4(scale.x,scale.y,offset.x,offset.y));
            }
            draw.Slots=slots.ToArray();draw.Cutoffs=cutoffs.ToArray();draw.Textures=textures.ToArray();draw.ST=transforms.ToArray();
            draw.NextRefresh=Time.realtimeSinceStartup+2f;MaterialRefreshCount++;
            return draw;
        }

        private void RefreshOneAnimalDraw()
        {
            if(drawRefresh.Count==0) return;
            nextDrawRefresh%=drawRefresh.Count;
            var draw=drawRefresh[nextDrawRefresh];
            if(draw.Renderer==null)
            {drawRefresh.RemoveAt(nextDrawRefresh);return;}
            nextDrawRefresh++;
            if(Time.realtimeSinceStartup>=draw.NextRefresh) CacheAnimalDraw(draw.Renderer);
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
            foreach(int id in expired) { proxies.Remove(id); animals.Remove(id);animalDraws.Remove(id); }
        }

        public void Restore()
        {
            foreach(var renderer in hidden) if(renderer!=null) renderer.forceRenderingOff=false;
            hidden.Clear();
        }

        private static bool IsAnimal(Renderer renderer)
        { return SnowAnimalExclusion.IsAnimal(renderer); }

        public void Dispose()
        {
            Restore();proxies.Clear();animals.Clear();materials.Clear();expired.Clear();visibleAnimals.Clear();changed=false;
            animalDraws.Clear();drawRefresh.Clear();nextDrawRefresh=0;nextBackgroundScan=0;MaterialRefreshCount=0;
            if(subscribed) SceneManager.sceneLoaded-=SceneLoaded;
            subscribed=false;
            foreach(var work in pendingAnimalScenes) work.Walk.Dispose();
            pendingAnimalScenes.Clear();queuedAnimalScenes.Clear();LastAnimalDiscoverySteps=0;
            LastRendererDiscoveryCount=0;
            if(exclusionMaterial!=null)
            {
                if(Application.isPlaying) UnityEngine.Object.Destroy(exclusionMaterial);
                else UnityEngine.Object.DestroyImmediate(exclusionMaterial);
            }
            exclusionMaterial=null;
        }
    }
}
