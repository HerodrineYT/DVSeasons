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
        private IEnumerator<T> walk;
        private float nextScan;

        public IncrementalSceneScan(float interval,int nodeBudget,Action<T> visit)
        { this.interval=interval;this.nodeBudget=nodeBudget;this.visit=visit; }

        public void Step()
        {
            if(walk==null)
            {
                if(Time.realtimeSinceStartup<nextScan) return;
                walk=Walk().GetEnumerator();
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

        private static IEnumerable<T> Walk()
        {
            var pending=new Stack<Transform>();
            var roots=new List<GameObject>();
            for(int sceneIndex=0;sceneIndex<SceneManager.sceneCount;sceneIndex++)
            {
                var scene=SceneManager.GetSceneAt(sceneIndex);
                if(!scene.IsValid() || !scene.isLoaded) continue;
                roots.Clear();scene.GetRootGameObjects(roots);
                foreach(var root in roots)
                {
                    if(root==null) continue;
                    pending.Push(root.transform);
                    while(pending.Count>0)
                    {
                        var node=pending.Pop();
                        if(node==null) {yield return null;continue;}
                        for(int i=node.childCount-1;i>=0;i--) pending.Push(node.GetChild(i));
                        yield return node.GetComponent<T>();
                    }
                }
            }
        }

        public void Dispose()
        { if(walk!=null) walk.Dispose();walk=null;nextScan=0; }
    }
}
