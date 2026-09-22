using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DVSeasons.Mod
{
    // Visits loaded scene objects in bounded batches, including inactive objects.
    // A node without T also consumes budget, so sparse scenes cannot block a frame.
    internal sealed class IncrementalSceneScan<T> : IDisposable where T : Component
    {
        private readonly float interval;
        private readonly int nodeBudget;
        private readonly Action<T> visit;
        private readonly bool prioritizeSceneRoots;
        private struct ChildCursor
        { public Transform Parent, Previous; public int Index, ChildCount; public bool HasPrevious; }
        private readonly Queue<ChildCursor> childCursors = new Queue<ChildCursor>();
        private readonly Stack<ChildCursor> depthCursors = new Stack<ChildCursor>();
        private readonly List<GameObject> rootBuffer = new List<GameObject>();
        private IEnumerator<T> walk;
        private float nextScan;

        public IncrementalSceneScan(float interval,int nodeBudget,Action<T> visit,bool prioritizeSceneRoots=false)
        { this.interval=interval;this.nodeBudget=nodeBudget;this.visit=visit;this.prioritizeSceneRoots=prioritizeSceneRoots; }

        public void Step()
        {
            if(walk==null)
            {
                if(Time.realtimeSinceStartup<nextScan) return;
                walk=(prioritizeSceneRoots ? WalkSceneRootsFirst() : Walk()).GetEnumerator();
            }
            long start=Stopwatch.GetTimestamp();
            for(int i=0;i<nodeBudget;i++)
            {
                if(!walk.MoveNext())
                {
                    walk.Dispose();walk=null;nextScan=Time.realtimeSinceStartup+interval;return;
                }
                if(walk.Current!=null) visit(walk.Current);
                if((Stopwatch.GetTimestamp()-start)*1000d/Stopwatch.Frequency>=0.5d) return;
            }
        }

        // World-water planes are near scene roots. A depth-first traversal can
        // spend a minute inside unrelated streamed scenery before visiting them.
        // Interleave child ranges rather than enqueueing all children in one
        // visit: even a root with 100,000 descendants cannot monopolize a frame.
        private IEnumerable<T> WalkSceneRootsFirst()
        {
            childCursors.Clear();
            for(int sceneIndex=0;sceneIndex<SceneManager.sceneCount;sceneIndex++)
            {
                var scene=SceneManager.GetSceneAt(sceneIndex);
                if(!scene.IsValid() || !scene.isLoaded) continue;
                ReadRoots(scene);
                foreach(var root in rootBuffer)
                {
                    if(root==null) {yield return null;continue;}
                    var node=root.transform;
                    if(node.childCount>0) childCursors.Enqueue(new ChildCursor {Parent=node});
                    yield return node.GetComponent<T>();
                }
            }
            while(childCursors.Count>0)
            {
                var cursor=childCursors.Dequeue();
                Transform node;
                if(!NextChild(ref cursor,out node)) {yield return null;continue;}
                if(cursor.Index<cursor.ChildCount) childCursors.Enqueue(cursor);
                if(node==null) {yield return null;continue;}
                if(node.childCount>0) childCursors.Enqueue(new ChildCursor {Parent=node});
                yield return node.GetComponent<T>();
            }
            rootBuffer.Clear();
        }

        private void ReadRoots(Scene scene)
        {
            rootBuffer.Clear();
            // Unity otherwise allocates a temporary native-to-managed root array.
            if(rootBuffer.Capacity<=scene.rootCount) rootBuffer.Capacity=scene.rootCount+1;
            scene.GetRootGameObjects(rootBuffer);
        }

        private static bool NextChild(ref ChildCursor cursor,out Transform node)
        {
            node=null;
            if(cursor.Parent==null)return false;
            int count=cursor.Parent.childCount;
            // Streaming can remove/reparent a previously visited sibling while
            // this cursor waits for another frame. The remaining indices shift;
            // replaying the parent is safe, skipping its next child is not.
            if(cursor.HasPrevious && (count!=cursor.ChildCount || cursor.Previous==null ||
                cursor.Previous.parent!=cursor.Parent || cursor.Previous.GetSiblingIndex()!=cursor.Index-1))
                cursor.Index=0;
            if(cursor.Index>=count)return false;
            node=cursor.Parent.GetChild(cursor.Index++);
            cursor.Previous=node;cursor.HasPrevious=true;cursor.ChildCount=count;
            return true;
        }

        private IEnumerable<T> Walk()
        {
            depthCursors.Clear();
            for(int sceneIndex=0;sceneIndex<SceneManager.sceneCount;sceneIndex++)
            {
                var scene=SceneManager.GetSceneAt(sceneIndex);
                if(!scene.IsValid() || !scene.isLoaded) continue;
                ReadRoots(scene);
                foreach(var root in rootBuffer)
                {
                    if(root==null) {yield return null;continue;}
                    var rootTransform=root.transform;
                    if(rootTransform.childCount>0)depthCursors.Push(new ChildCursor {Parent=rootTransform});
                    yield return rootTransform.GetComponent<T>();
                    while(depthCursors.Count>0)
                    {
                        var cursor=depthCursors.Pop();
                        Transform node;
                        if(!NextChild(ref cursor,out node)) {yield return null;continue;}
                        if(cursor.Index<cursor.ChildCount)depthCursors.Push(cursor);
                        if(node==null) {yield return null;continue;}
                        // Visit only one child per yield. Pushing a whole fan-out
                        // defeated Step's budget on roots with thousands of children.
                        if(node.childCount>0)depthCursors.Push(new ChildCursor {Parent=node});
                        yield return node.GetComponent<T>();
                    }
                }
            }
            rootBuffer.Clear();
        }

        public void Dispose()
        { if(walk!=null) walk.Dispose();walk=null;nextScan=0;childCursors.Clear();depthCursors.Clear();rootBuffer.Clear(); }
    }
}
