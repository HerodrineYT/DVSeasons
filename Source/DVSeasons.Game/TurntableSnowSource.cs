using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace DVSeasons.Mod
{
    internal sealed class TurntableSnowSource : IDisposable
    {
        private const int NodeBudget=192;
        private const string HarmonyId="Herodrine.DVSeasons.TurntableDiscovery";
        private static readonly List<TurntableSnowSource> listeners=new List<TurntableSnowSource>();
        private static readonly Harmony harmony=new Harmony(HarmonyId);
        private struct ChildCursor { public Transform Parent; public int Index; }
        private sealed class SceneWalk
        {
            public Scene Scene;
            public bool Started,Finished;
            public int RootIndex;
            public readonly List<GameObject> Roots=new List<GameObject>();
            public readonly Queue<ChildCursor> Children=new Queue<ChildCursor>();
            public void Clear() {Finished=true;Roots.Clear();Children.Clear();}
        }
        private readonly List<Transform> roots=new List<Transform>();
        private readonly Dictionary<int,TurntableRailTrack> tracks=new Dictionary<int,TurntableRailTrack>();
        private readonly List<int> expired=new List<int>();
        private readonly Dictionary<int,SceneWalk> scenes=new Dictionary<int,SceneWalk>();
        private readonly Queue<SceneWalk> pending=new Queue<SceneWalk>();
        private bool disposed;
        internal int SceneSnapshotCount {get;private set;}
        internal int VisitedNodeCount {get;private set;}
        internal int LastStepVisitedCount {get;private set;}
        internal int PendingSceneCount => pending.Count;

        public TurntableSnowSource()
        {
            if(listeners.Count==0) InstallHooks();
            listeners.Add(this);
            SceneManager.sceneLoaded+=OnSceneLoaded;
            SceneManager.sceneUnloaded+=OnSceneUnloaded;
            // One initial snapshot, then only newly loaded scenes. Completed
            // scene walks stay marked so unrelated scene events cannot restart
            // a large terrain hierarchy that has already been examined.
            for(int index=0;index<SceneManager.sceneCount;index++) QueueScene(SceneManager.GetSceneAt(index));
        }

        public IEnumerable<Transform> GetRoots()
        {
            if(disposed) return roots;
            using(SnowPerformance.Measure("turntable-discovery")) Step();
            roots.Clear();expired.Clear();
            foreach(var pair in tracks)
            {
                var track=pair.Value;
                if(track==null) {expired.Add(pair.Key);continue;}
                // Custom maps can assign or replace the bridge after native
                // component initialization. Recheck only known turntables, not
                // every object in every loaded scene. Rotation changes no cache.
                var root=track.visuals;
                if(root!=null && !roots.Contains(root)) roots.Add(root);
            }
            foreach(var id in expired) tracks.Remove(id);
            return roots;
        }

        private void QueueScene(Scene scene)
        {
            if(!scene.IsValid() || !scene.isLoaded || scenes.ContainsKey(scene.handle)) return;
            var walk=new SceneWalk {Scene=scene};scenes.Add(scene.handle,walk);pending.Enqueue(walk);
        }

        private void Step()
        {
            LastStepVisitedCount=0;
            if(pending.Count==0)return;
            long start=Stopwatch.GetTimestamp();
            for(int work=0;work<NodeBudget && pending.Count>0;work++)
            {
                var walk=pending.Dequeue();
                if(walk.Finished || !walk.Scene.IsValid() || !walk.Scene.isLoaded) {walk.Clear();continue;}
                if(!walk.Started)
                {
                    walk.Started=true;
                    // Unity exposes the root list as one native snapshot; it is
                    // taken once per scene, never once per frame or interval.
                    walk.Roots.Capacity=Math.Max(walk.Roots.Capacity,walk.Scene.rootCount);
                    walk.Scene.GetRootGameObjects(walk.Roots);SceneSnapshotCount++;
                    pending.Enqueue(walk);
                }
                else
                {
                    Transform node=null;
                    if(walk.RootIndex<walk.Roots.Count)
                    {
                        var root=walk.Roots[walk.RootIndex++];
                        if(root!=null)node=root.transform;
                    }
                    else if(walk.Children.Count>0)
                    {
                        var cursor=walk.Children.Dequeue();
                        if(cursor.Parent!=null && cursor.Index<cursor.Parent.childCount)
                        {
                            node=cursor.Parent.GetChild(cursor.Index++);
                            if(cursor.Index<cursor.Parent.childCount)walk.Children.Enqueue(cursor);
                        }
                    }
                    else {walk.Clear();continue;}
                    LastStepVisitedCount++;VisitedNodeCount++;
                    if(node!=null && node.gameObject.scene.handle==walk.Scene.handle)
                    {
                        // Advance one edge at a time. A node with 100,000 direct
                        // children cannot enqueue them all in one GetRoots call.
                        if(node.childCount>0)walk.Children.Enqueue(new ChildCursor {Parent=node});
                        Visit(node.GetComponent<TurntableRailTrack>());
                    }
                    pending.Enqueue(walk);
                }
                if((Stopwatch.GetTimestamp()-start)*1000d/Stopwatch.Frequency>=.5d)return;
            }
        }

        private void Visit(TurntableRailTrack track)
        {
            // The bridge rotates; the native track root and pit do not.
            if(track!=null && track.gameObject.scene.IsValid())tracks[track.GetInstanceID()]=track;
        }
        private static void Register(TurntableRailTrack track)
        {if(track!=null)foreach(var source in listeners)source.Visit(track);}

        private static void InstallHooks()
        {
            // TurntableRailTrack has no Awake/OnEnable/Start in DV99. Its
            // required RailTrack initializes when a prefab becomes active;
            // controllers supply their explicit track reference in Awake.
            // These narrow native events cover turntables instantiated after
            // the scene scan without polling the full streamed world again.
            // A custom mod that adds an inactive, controller-less component to
            // an already scanned scene has no native lifecycle event yet; it is
            // registered when its RailTrack initializes or the bridge is used.
            // Inactive components already present on scene load use the walker.
            harmony.Patch(AccessTools.Method(typeof(RailTrack),"Init"),
                postfix:new HarmonyMethod(typeof(TurntableSnowSource),nameof(RailInitialized)));
            harmony.Patch(AccessTools.Method(typeof(TurntableController),"Awake"),
                prefix:new HarmonyMethod(typeof(TurntableSnowSource),nameof(ControllerCreated)));
            harmony.Patch(AccessTools.Method(typeof(TurntableRailTrack),"RotateToTargetRotation"),
                prefix:new HarmonyMethod(typeof(TurntableSnowSource),nameof(TrackUsed)));
        }
        private static void RailInitialized(RailTrack __instance)
        {if(__instance!=null)Register(__instance.GetComponent<TurntableRailTrack>());}
        private static void ControllerCreated(TurntableController __instance)
        {if(__instance!=null)Register(__instance.turntable);}
        private static void TrackUsed(TurntableRailTrack __instance) {Register(__instance);}

        private void OnSceneLoaded(Scene scene,LoadSceneMode mode) {QueueScene(scene);}
        private void OnSceneUnloaded(Scene scene)
        {
            SceneWalk walk;
            if(scenes.TryGetValue(scene.handle,out walk)) {walk.Clear();scenes.Remove(scene.handle);}
            expired.Clear();
            foreach(var pair in tracks)
                if(pair.Value==null || pair.Value.gameObject.scene.handle==scene.handle)expired.Add(pair.Key);
            foreach(var id in expired)tracks.Remove(id);
            roots.Clear();
        }

        public void Dispose()
        {
            if(disposed)return;
            disposed=true;
            SceneManager.sceneLoaded-=OnSceneLoaded;
            SceneManager.sceneUnloaded-=OnSceneUnloaded;
            listeners.Remove(this);
            if(listeners.Count==0)harmony.UnpatchAll(HarmonyId);
            foreach(var walk in scenes.Values)walk.Clear();
            pending.Clear();scenes.Clear();roots.Clear();tracks.Clear();expired.Clear();
        }
    }
}
