using System;
using System.Collections.Generic;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Small camera-independent physics cache. Only nearby flakes need collision:
    // distant flakes are hidden by normal depth testing. AABB rejection avoids
    // asking PhysX about every flake against every scene collider.
    internal sealed class SnowfallWorldCollision
    {
        internal const float ParticleRadius = 20f;
        private const float QueryRadius = 36f;
        private const float CellSize = 6f;
        private const int GridSize = 12;
        private Collider[] colliders = new Collider[128];
        private Vector3[] minima = new Vector3[128], maxima = new Vector3[128];
        private int[] visited = new int[128];
        private readonly List<int>[] cells = new List<int>[GridSize * GridSize];
        private int visit;
        private int count;
        private float nextQuery;
        private Vector3 queryPosition;
        private bool queried;

        internal void Prepare(Vector3 position)
        {
            if (!queried || Time.unscaledTime >= nextQuery || (queryPosition - position).sqrMagnitude > 4f)
            {
                // No renderer visibility, frustum or camera rotation participates.
                // Grow only when needed, retaining the buffers between storms.
                do
                {
                    count = Physics.OverlapSphereNonAlloc(position, QueryRadius, colliders,
                        SeasonSurfaceLayers.Mask, QueryTriggerInteraction.Ignore);
                    if (count < colliders.Length) break;
                    Array.Resize(ref colliders, colliders.Length * 2);
                    Array.Resize(ref minima, colliders.Length);
                    Array.Resize(ref maxima, colliders.Length);
                    Array.Resize(ref visited, colliders.Length);
                } while (true);
                queryPosition = position; queried = true; nextQuery = Time.unscaledTime + .2f;
            }
            // Trains/doors may move between discoveries; cache their current AABB
            // once per particle update, not once per snowflake.
            foreach (var cell in cells) cell?.Clear();
            for (int i = 0; i < count; i++)
            {
                if (colliders[i] == null) continue;
                var bounds = colliders[i].bounds;
                var min = minima[i] = bounds.min; var max = maxima[i] = bounds.max;
                int firstX = CellX(min.x), lastX = CellX(max.x), firstZ = CellZ(min.z), lastZ = CellZ(max.z);
                for (int z = firstZ; z <= lastZ; z++)
                    for (int x = firstX; x <= lastX; x++)
                    {
                        int cell = z * GridSize + x;
                        if (cells[cell] == null) cells[cell] = new List<int>(8);
                        cells[cell].Add(i);
                    }
            }
        }

        private int CellX(float x) => Mathf.Clamp(Mathf.FloorToInt((x - queryPosition.x + QueryRadius) / CellSize), 0, GridSize - 1);
        private int CellZ(float z) => Mathf.Clamp(Mathf.FloorToInt((z - queryPosition.z + QueryRadius) / CellSize), 0, GridSize - 1);

        internal bool Blocked(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance < .0001f) return false;
            var ray = new Ray(from, delta / distance);
            var min = Vector3.Min(from, to) - Vector3.one * .01f;
            var max = Vector3.Max(from, to) + Vector3.one * .01f;
            if (++visit == int.MaxValue) { Array.Clear(visited, 0, visited.Length); visit = 1; }
            int firstX = CellX(min.x), lastX = CellX(max.x), firstZ = CellZ(min.z), lastZ = CellZ(max.z);
            for (int z = firstZ; z <= lastZ; z++)
                for (int x = firstX; x <= lastX; x++)
                {
                    var cell = cells[z * GridSize + x];
                    if (cell == null) continue;
                    for (int k = 0; k < cell.Count; k++)
                    {
                        int i = cell[k];
                        if (visited[i] == visit) continue;
                        visited[i] = visit;
                        // Cache min/max once. Bounds.Intersects recomputes them
                        // repeatedly through Vector3 accessors in Unity's Mono.
                        var lo = minima[i]; var hi = maxima[i];
                        if (lo.x > max.x || hi.x < min.x || lo.y > max.y || hi.y < min.y || lo.z > max.z || hi.z < min.z) continue;
                        var collider = colliders[i];
                        if (collider == null || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                        if (collider.Raycast(ray, out var hit, distance + .01f)) return true;
                    }
                }
            return false;
        }

        internal void Clear()
        {
            Array.Clear(colliders, 0, colliders.Length); count = 0; queried = false; nextQuery = 0;
            foreach (var cell in cells) cell?.Clear();
        }
    }
}
