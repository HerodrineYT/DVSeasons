using System;
using System.Collections.Generic;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Local rolling-stock rendering budget. Keep all cached masks; static
    // scenery, junctions and moving infrastructure never consume this limit.
    internal sealed class SnowObjectLimiter : IDisposable
    {
        private struct Candidate { public int Id; public float Distance; }
        private struct Branch { public int Owner; public Transform Transform, OwnerRoot; }
        private readonly List<Candidate> heap = new List<Candidate>();
        private readonly HashSet<int> selected = new HashSet<int>();
        private readonly Dictionary<int,Branch> branches = new Dictionary<int,Branch>();
        private readonly List<Transform> selectedRoots = new List<Transform>();
        private readonly Plane[] frustum = new Plane[6];
        private readonly Plane[] rightFrustum = new Plane[6];
        private int limit;
        private bool dirty = true, membershipDirty = true, cameraKnown;
        private float nextSelection;
        private Vector3 previousPosition;
        private Quaternion previousRotation;

        public int Limit
        {
            get { return limit; }
            set
            {
                int next = Math.Max(0, value);
                if (limit == next) return;
                limit = next; dirty = true;
                if (next == 0)
                { selected.Clear(); selectedRoots.Clear(); heap.Clear(); branches.Clear(); cameraKnown = false; membershipDirty = true; }
            }
        }
        public bool Active { get { return limit > 0; } }
        public int SelectedCount { get { return selected.Count; } }
        public int MembershipRebuildCount { get; private set; }
        public void InvalidateMembership() { membershipDirty = true; dirty = true; }
        public void ShiftWorld(Vector3 delta) { previousPosition += delta; dirty = true; }

        public void Update(Camera camera, IList<SnowVehicleRegistry.Vehicle> vehicles)
        {
            if (!Active || camera == null) return;
            // Native roots can disappear before the registry's next fleet poll.
            // Only a removed selected car can change the winning set; validate
            // that small set without rebuilding every branch in the whole yard.
            for (int i = 0; i < selectedRoots.Count; i++)
                if (selectedRoots[i] == null) { InvalidateMembership(); break; }
            if (membershipDirty)
            {
                branches.Clear();
                for (int i = 0; i < vehicles.Count; i++)
                {
                    var v = vehicles[i];
                    if (!v.RollingStock || v.Root == null) continue;
                    int id = v.RootId;
                    AddBranch(v.Root, id, v.Root); AddBranch(v.Interior, id, v.Root);
                    AddBranch(v.InteriorLod, id, v.Root); AddBranch(v.Cargo, id, v.Root);
                    AddBranch(v.External, id, v.Root); AddBranch(v.DummyExternal, id, v.Root);
                }
                membershipDirty = false; MembershipRebuildCount++;
            }
            var position = camera.transform.position;
            if (!dirty && cameraKnown && Time.realtimeSinceStartup < nextSelection &&
                (position - previousPosition).sqrMagnitude <= 16f &&
                Quaternion.Angle(camera.transform.rotation, previousRotation) <= 8f) return;

            heap.Clear(); var secondaryFrustum = SnowCameraFrustum.Prepare(camera, frustum, rightFrustum);
            for (int i = 0; i < vehicles.Count; i++)
            {
                var v = vehicles[i];
                if (!v.RollingStock || v.Root == null || !v.Root.gameObject.activeInHierarchy) continue;
                var scale = v.Root.lossyScale;
                float radius = v.Ready ? v.LocalBounds.extents.magnitude *
                    Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z))) : 40f;
                var bounds = new Bounds(v.Ready ? v.Root.TransformPoint(v.LocalBounds.center) : v.Root.position,
                    Vector3.one * Mathf.Max(1f, radius * 2f));
                if (SnowCameraFrustum.Intersects(frustum, secondaryFrustum, bounds))
                    // The broad 40 m culling bound of an unprepared car is not
                    // its real body. Do not let it outrank a closer ready car.
                    AddCandidate(new Candidate { Id = v.RootId, Distance = v.Ready
                        ? bounds.SqrDistance(position) : (v.Root.position - position).sqrMagnitude });
            }
            selected.Clear();
            foreach (var candidate in heap) selected.Add(candidate.Id);
            selectedRoots.Clear();
            foreach (var vehicle in vehicles)
                if (vehicle.Root != null && selected.Contains(vehicle.RootId)) selectedRoots.Add(vehicle.Root);
            previousPosition = position; previousRotation = camera.transform.rotation;
            nextSelection = Time.realtimeSinceStartup + .25f; cameraKnown = true; dirty = false;
        }

        private void AddBranch(Transform branch, int owner, Transform ownerRoot)
        {
            if (branch == null) return;
            branches[branch.GetInstanceID()] = new Branch { Owner = owner, Transform = branch, OwnerRoot = ownerRoot };
        }
        public bool IsSelected(Transform root)
        {
            if (!Active) return true;
            for (var node = root; node != null; node = node.parent)
            {
                Branch branch;
                // A destroyed cached branch may have its native instance ID
                // reused before the next fleet poll. It must not classify an
                // unrelated live object as rolling stock.
                if (branches.TryGetValue(node.GetInstanceID(), out branch) && branch.OwnerRoot != null &&
                    branch.Transform != null && branch.Transform == node)
                    return selected.Contains(branch.Owner);
            }
            return true; // Every non-train object keeps its normal snow.
        }
        public bool IsSelected(Renderer renderer)
        { return renderer != null && IsSelected(renderer.transform); }
        private static bool Farther(Candidate a, Candidate b)
        { return a.Distance > b.Distance || (a.Distance == b.Distance && a.Id > b.Id); }
        private void AddCandidate(Candidate candidate)
        {
            if (heap.Count < limit)
            {
                int index = heap.Count; heap.Add(candidate);
                while (index > 0)
                {
                    int parent = (index - 1) / 2;
                    if (!Farther(heap[index], heap[parent])) break;
                    var swap = heap[parent]; heap[parent] = heap[index]; heap[index] = swap; index = parent;
                }
                return;
            }
            if (!Farther(heap[0], candidate)) return;
            heap[0] = candidate; int cursor = 0;
            while (true)
            {
                int child = cursor * 2 + 1;
                if (child >= heap.Count) break;
                if (child + 1 < heap.Count && Farther(heap[child + 1], heap[child])) child++;
                if (!Farther(heap[child], heap[cursor])) break;
                var swap = heap[cursor]; heap[cursor] = heap[child]; heap[child] = swap; cursor = child;
            }
        }
        public void Dispose()
        {
            heap.Clear(); selected.Clear(); selectedRoots.Clear(); branches.Clear();
            dirty = membershipDirty = true; cameraKnown = false; nextSelection = 0; MembershipRebuildCount = 0;
        }
    }
}
