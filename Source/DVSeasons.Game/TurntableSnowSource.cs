using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DVSeasons.Mod
{
    internal sealed class TurntableSnowSource : IDisposable
    {
        private readonly List<Transform> roots = new List<Transform>();
        private float nextScan;
        private bool dirty = true;
        private bool disposed;

        public TurntableSnowSource()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        public IEnumerable<Transform> GetRoots()
        {
            if (disposed) return roots;
            if (dirty || Time.realtimeSinceStartup >= nextScan)
            {
                dirty = false;
                nextScan = Time.realtimeSinceStartup + 5f;
                roots.Clear();
                foreach (var track in UnityEngine.Object.FindObjectsOfType<TurntableRailTrack>())
                {
                    // The game rotates this reference, while the track transform,
                    // pit and control house remain stationary. Custom maps use
                    // the same component contract without requiring prefab names.
                    var root = track != null ? track.visuals : null;
                    if (root != null && !roots.Contains(root)) roots.Add(root);
                }
            }
            for (int i = roots.Count - 1; i >= 0; i--)
                if (roots[i] == null) roots.RemoveAt(i);
            return roots;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) { dirty = true; }
        private void OnSceneUnloaded(Scene scene) { dirty = true; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            roots.Clear();
        }
    }
}
