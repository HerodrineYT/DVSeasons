using System;
using System.Collections.Generic;
using AwesomeTechnologies.VegetationStudio;
using AwesomeTechnologies.VegetationSystem;
using DV.TerrainSystem;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal struct AutumnTreeSourceSnapshot
    {
        public long Key;
        public Vector3 BaseWorldPosition;
        public Vector3 CanopyWorldPosition;
        public float CrownRadius;
    }

    /// <summary>
    /// Copies real tree positions out of Vegetation Studio Pro after its jobs have
    /// completed. Native containers are inspected only inside render-complete and
    /// only managed value copies leave that callback.
    /// </summary>
    internal sealed class AutumnTreeSourceProvider : IDisposable
    {
        private const int MaximumSnapshotsPerSystem = 192;
        private const float SubscriptionRefreshInterval = 0.8f;
        private const float MinimumCaptureInterval = 0.2f;
        private const float InterestMoveThreshold = 12f;

        private static readonly string[] EvergreenNames =
            { "fir", "pine", "spruce", "conifer", "evergreen", "abies", "picea" };
        private static readonly string[] NonTreeNames =
            { "bush", "shrub", "grass", "fern", "stump", "dead", "log" };

        private struct Candidate
        {
            public AutumnTreeSourceSnapshot Snapshot;
            public float DistanceSquared;
        }

        private sealed class SystemState
        {
            public VegetationSystemPro System;
            public readonly List<AutumnTreeSourceSnapshot> Snapshots =
                new List<AutumnTreeSourceSnapshot>(MaximumSnapshotsPerSystem);
            public readonly Candidate[] Heap = new Candidate[MaximumSnapshotsPerSystem];
            public int HeapCount;
            public bool Dirty = true;
            public bool CaptureFailureReported;
            public bool CaptureReadyReported;
            public float NextCaptureTime;
            public Vector3 LastInterestCentre;
            public float LastInterestRadius;

            public VegetationSystemPro.MultOnRenderCompleteDelegate RenderCompleteHandler;
            public VegetationSystemPro.MultiOnVegetationCellSpawnedDelegate CellLoadedHandler;
            public VegetationSystemPro.MultiOnClearCacheDelegate ClearCacheHandler;
            public VegetationSystemPro.MultiOnClearCacheVegetationItemDelegate
                ClearCacheItemHandler;
            public VegetationSystemPro.MultiOnClearCacheVegetationCellDelegate
                ClearCacheCellHandler;
            public VegetationSystemPro.MultiOnClearCacheVegetationCellVegetationItemDelegate
                ClearCacheCellItemHandler;
            public VegetationSystemPro.MultiOnVegetationStudioRefreshDelegate RefreshHandler;
        }

        private readonly List<SystemState> systems = new List<SystemState>(2);
        private VegetationStudioManager manager;
        private TerrainGrid terrainGrid;
        private Vector3 interestCentreWorld;
        private float interestRadius;
        private float nextSubscriptionRefresh;
        private bool enabled;
        private bool disposed;
        private readonly bool floweringPlants;

        public int SnapshotVersion { get; private set; }
        public int TerrainRevision { get; private set; }

        public AutumnTreeSourceProvider(bool floweringPlants = false)
        {
            this.floweringPlants = floweringPlants;
            TerrainGrid.TerrainDataLoaded += OnTerrainDataChanged;
            TerrainGrid.TerrainDataAboutToBeUnloaded += OnTerrainDataChanged;
            TerrainGrid.Initialized += OnTerrainGridInitialized;
        }

        public void SetEnabled(bool value)
        {
            if (disposed || enabled == value) return;
            enabled = value;
            if (enabled)
            {
                nextSubscriptionRefresh = 0f;
                for (var i = 0; i < systems.Count; i++) systems[i].Dirty = true;
                return;
            }

            var changed = false;
            for (var i = 0; i < systems.Count; i++)
            {
                if (systems[i].Snapshots.Count > 0) changed = true;
                systems[i].Snapshots.Clear();
                systems[i].Dirty = true;
            }
            if (changed) SnapshotVersion++;
        }

        public void UpdateInterest(Vector3 centreWorld, float radius)
        {
            if (disposed) return;
            SetEnabled(true);
            interestCentreWorld = centreWorld;
            interestRadius = Mathf.Max(1f, radius);
            RefreshSubscriptions();

            var moveLimitSquared = InterestMoveThreshold * InterestMoveThreshold;
            for (var i = 0; i < systems.Count; i++)
            {
                var state = systems[i];
                if (HorizontalSqrDistance(state.LastInterestCentre, centreWorld) > moveLimitSquared ||
                    Mathf.Abs(state.LastInterestRadius - interestRadius) > 1f)
                    state.Dirty = true;
            }
        }

        public int CopySnapshots(List<AutumnTreeSourceSnapshot> destination)
        {
            destination.Clear();
            var radiusSquared = interestRadius * interestRadius;
            for (var systemIndex = 0; systemIndex < systems.Count; systemIndex++)
            {
                var snapshots = systems[systemIndex].Snapshots;
                for (var i = 0; i < snapshots.Count; i++)
                {
                    var snapshot = snapshots[i];
                    if (HorizontalSqrDistance(snapshot.BaseWorldPosition, interestCentreWorld) <=
                        radiusSquared)
                        destination.Add(snapshot);
                }
            }
            return destination.Count;
        }

        private void RefreshSubscriptions()
        {
            if (Time.realtimeSinceStartup < nextSubscriptionRefresh) return;
            nextSubscriptionRefresh = Time.realtimeSinceStartup + SubscriptionRefreshInterval;

            var currentManager = VegetationStudioManager.Instance;
            if (currentManager == null)
                currentManager = UnityEngine.Object.FindObjectOfType<VegetationStudioManager>();
            if (currentManager != manager)
            {
                DetachManager();
                manager = currentManager;
                if (manager != null)
                {
                    manager.OnAddVegetationSystemDelegate += OnVegetationSystemAdded;
                    manager.OnRemoveVegetationSystemDelegate += OnVegetationSystemRemoved;
                }
            }

            if (manager != null)
            {
                var registered = manager.VegetationSystemList;
                for (var i = systems.Count - 1; i >= 0; i--)
                    if (registered == null || !registered.Contains(systems[i].System))
                        RemoveSystemAt(i);
                if (registered != null)
                    for (var i = 0; i < registered.Count; i++) AddSystem(registered[i]);
            }

            var currentGrid = TerrainGrid.Instance;
            if (currentGrid != terrainGrid)
            {
                if (terrainGrid != null) terrainGrid.TerrainsMoved -= OnTerrainsMoved;
                terrainGrid = currentGrid;
                if (terrainGrid != null) terrainGrid.TerrainsMoved += OnTerrainsMoved;
                InvalidateTerrainAndVegetation();
            }
        }

        private void OnVegetationSystemAdded(VegetationSystemPro system)
        {
            AddSystem(system);
        }

        private void OnVegetationSystemRemoved(VegetationSystemPro system)
        {
            for (var i = systems.Count - 1; i >= 0; i--)
                if (systems[i].System == system) RemoveSystemAt(i);
        }

        private void AddSystem(VegetationSystemPro system)
        {
            if (system == null) return;
            for (var i = 0; i < systems.Count; i++)
                if (systems[i].System == system) return;

            var state = new SystemState { System = system };
            state.RenderCompleteHandler = renderedSystem => OnRenderComplete(state, renderedSystem);
            state.CellLoadedHandler = cell => OnCellLoaded(state, cell);
            state.ClearCacheHandler = clearedSystem => OnCacheCleared(state, clearedSystem);
            state.ClearCacheItemHandler = (clearedSystem, packageIndex, itemIndex) =>
                OnCacheCleared(state, clearedSystem);
            state.ClearCacheCellHandler = (clearedSystem, cell) =>
                OnCacheCleared(state, clearedSystem);
            state.ClearCacheCellItemHandler = (clearedSystem, cell, packageIndex, itemIndex) =>
                OnCacheCleared(state, clearedSystem);
            state.RefreshHandler = refreshedSystem => OnCacheCleared(state, refreshedSystem);

            system.OnRenderCompleteDelegate += state.RenderCompleteHandler;
            system.OnVegetationCellLoaded += state.CellLoadedHandler;
            system.OnClearCacheDelegate += state.ClearCacheHandler;
            system.OnClearCacheVegetationItemDelegate += state.ClearCacheItemHandler;
            system.OnClearCacheVegetationCellDelegate += state.ClearCacheCellHandler;
            system.OnClearCacheVegetationCellVegetatonItemDelegate += state.ClearCacheCellItemHandler;
            system.OnRefreshVegetationSystemDelegate += state.RefreshHandler;
            systems.Add(state);
        }

        private void RemoveSystemAt(int index)
        {
            var state = systems[index];
            DetachSystem(state);
            systems.RemoveAt(index);
            if (state.Snapshots.Count > 0) SnapshotVersion++;
        }

        private static void DetachSystem(SystemState state)
        {
            var system = state.System;
            if (system == null) return;
            system.OnRenderCompleteDelegate -= state.RenderCompleteHandler;
            system.OnVegetationCellLoaded -= state.CellLoadedHandler;
            system.OnClearCacheDelegate -= state.ClearCacheHandler;
            system.OnClearCacheVegetationItemDelegate -= state.ClearCacheItemHandler;
            system.OnClearCacheVegetationCellDelegate -= state.ClearCacheCellHandler;
            system.OnClearCacheVegetationCellVegetatonItemDelegate -= state.ClearCacheCellItemHandler;
            system.OnRefreshVegetationSystemDelegate -= state.RefreshHandler;
        }

        private void OnCellLoaded(SystemState state, VegetationCell cell)
        {
            // VSP invokes this immediately after scheduling its spawn jobs. Only
            // mark the managed snapshot dirty; the actual read waits for render complete.
            state.Dirty = true;
        }

        private void OnCacheCleared(SystemState state, VegetationSystemPro system)
        {
            ClearSystemSnapshot(state);
        }

        private void OnRenderComplete(SystemState state, VegetationSystemPro renderedSystem)
        {
            if (!enabled || disposed || renderedSystem == null || renderedSystem != state.System ||
                !state.Dirty || Time.realtimeSinceStartup < state.NextCaptureTime) return;
            CaptureSystem(state);
        }

        private void CaptureSystem(SystemState state)
        {
            state.NextCaptureTime = Time.realtimeSinceStartup + MinimumCaptureInterval;
            state.HeapCount = 0;
            var system = state.System;
            try
            {
                if (system == null || system.LoadedVegetationCellList == null ||
                    system.VegetationPackageProList == null)
                {
                    CommitCapture(state);
                    return;
                }

                var offset = system.FloatingOriginOffset;
                var radiusSquared = interestRadius * interestRadius;
                var cells = system.LoadedVegetationCellList;
                for (var cellIndex = 0; cellIndex < cells.Count; cellIndex++)
                {
                    var cell = cells[cellIndex];
                    if (cell == null || !cell.Prepared || cell.LoadedDistanceBand == 99 ||
                        cell.VegetationPackageInstancesList == null) continue;
                    var cellBounds = cell.VegetationCellBounds;
                    cellBounds.center += offset;
                    if (!BoundsNear(cellBounds, interestCentreWorld, interestRadius)) continue;

                    var packageCount = Mathf.Min(cell.VegetationPackageInstancesList.Count,
                        system.VegetationPackageProList.Count);
                    for (var packageIndex = 0; packageIndex < packageCount; packageIndex++)
                    {
                        var packageInstances = cell.VegetationPackageInstancesList[packageIndex];
                        var package = system.VegetationPackageProList[packageIndex];
                        if (packageInstances == null || package == null ||
                            package.VegetationInfoList == null ||
                            !packageInstances.LoadStateList.IsCreated) continue;
                        var itemCount = Mathf.Min(packageInstances.VegetationItemMatrixList.Count,
                            package.VegetationInfoList.Count);
                        itemCount = Mathf.Min(itemCount, packageInstances.LoadStateList.Length);
                        for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
                        {
                            if (packageInstances.LoadStateList[itemIndex] != 1) continue;
                            var item = package.VegetationInfoList[itemIndex];
                            if (floweringPlants ? !IsFlower(item) : !IsLeafBearingTree(item)) continue;
                            var matrixList = packageInstances.VegetationItemMatrixList[itemIndex];
                            if (!matrixList.IsCreated) continue;
                            var itemSeed = StableStringHash(item.VegetationItemID ?? item.Name);
                            for (var matrixIndex = 0; matrixIndex < matrixList.Length; matrixIndex++)
                            {
                                var matrix = matrixList[matrixIndex].Matrix;
                                var baseWorld = new Vector3(matrix.m03 + offset.x,
                                    matrix.m13 + offset.y, matrix.m23 + offset.z);
                                var distanceSquared = HorizontalSqrDistance(baseWorld,
                                    interestCentreWorld);
                                if (distanceSquared > radiusSquared) continue;

                                var scale = matrix.lossyScale;
                                var height = Mathf.Abs(item.Bounds.size.y * scale.y);
                                if (!floweringPlants && height < 2f)
                                    height = Mathf.Max(5f, Mathf.Abs(scale.y) * 8f);
                                height = floweringPlants ? Mathf.Clamp(height, .1f, 1.5f) : Mathf.Clamp(height, 4.5f, 22f);
                                var crownRadius = Mathf.Max(
                                    Mathf.Abs(item.Bounds.extents.x * scale.x),
                                    Mathf.Abs(item.Bounds.extents.z * scale.z));
                                if (crownRadius < 0.8f)
                                    crownRadius = Mathf.Max(Mathf.Abs(scale.x),
                                        Mathf.Abs(scale.z)) * 3.2f;

                                var snapshot = new AutumnTreeSourceSnapshot
                                {
                                    Key = BuildKey(system.GetInstanceID(), cell.Index, packageIndex,
                                        itemSeed, matrixIndex),
                                    BaseWorldPosition = baseWorld,
                                    CanopyWorldPosition = baseWorld + Vector3.up * (height * 0.72f),
                                    CrownRadius = Mathf.Clamp(crownRadius, 1.8f, 7f)
                                };
                                AddCandidate(state, snapshot, distanceSquared);
                            }
                        }
                    }
                }
                CommitCapture(state);
            }
            catch (Exception exception)
            {
                // Streaming can dispose a NativeList during a game-version-specific
                // lifecycle edge. Keep the previous managed snapshot and retry only
                // after the capture throttle; report the incompatibility once.
                state.Dirty = true;
                if (!state.CaptureFailureReported)
                {
                    state.CaptureFailureReported = true;
                    Debug.LogWarning("[DVSeasons] Could not read Vegetation Studio tree instances " +
                        "at its render-complete boundary; Terrain tree fallback remains active. " +
                        exception.GetType().Name + ": " + exception.Message);
                }
            }
        }

        private void CommitCapture(SystemState state)
        {
            state.Snapshots.Clear();
            for (var i = 0; i < state.HeapCount; i++)
                state.Snapshots.Add(state.Heap[i].Snapshot);
            state.Dirty = false;
            state.LastInterestCentre = interestCentreWorld;
            state.LastInterestRadius = interestRadius;
            SnapshotVersion++;
            if (!state.CaptureReadyReported && state.Snapshots.Count > 0)
            {
                state.CaptureReadyReported = true;
                Debug.Log("[DVSeasons] Vegetation Studio " + (floweringPlants ? "flower" : "tree") + " snapshot ready: " +
                    state.Snapshots.Count +
                    " nearby sources copied after render jobs completed.");
            }
        }

        private static void AddCandidate(SystemState state, AutumnTreeSourceSnapshot snapshot,
            float distanceSquared)
        {
            var candidate = new Candidate
                { Snapshot = snapshot, DistanceSquared = distanceSquared };
            if (state.HeapCount < state.Heap.Length)
            {
                var index = state.HeapCount++;
                state.Heap[index] = candidate;
                while (index > 0)
                {
                    var parent = (index - 1) / 2;
                    if (state.Heap[parent].DistanceSquared >= distanceSquared) break;
                    state.Heap[index] = state.Heap[parent];
                    index = parent;
                }
                state.Heap[index] = candidate;
                return;
            }
            if (distanceSquared >= state.Heap[0].DistanceSquared) return;

            state.Heap[0] = candidate;
            var current = 0;
            while (true)
            {
                var left = current * 2 + 1;
                if (left >= state.HeapCount) break;
                var right = left + 1;
                var largest = right < state.HeapCount &&
                    state.Heap[right].DistanceSquared > state.Heap[left].DistanceSquared
                    ? right : left;
                if (state.Heap[largest].DistanceSquared <=
                    state.Heap[current].DistanceSquared) break;
                var swap = state.Heap[current];
                state.Heap[current] = state.Heap[largest];
                state.Heap[largest] = swap;
                current = largest;
            }
        }

        private void OnTerrainDataChanged(TerrainData data, Vector2Int coordinates)
        {
            InvalidateTerrainAndVegetation();
        }

        private void OnTerrainGridInitialized()
        {
            nextSubscriptionRefresh = 0f;
            InvalidateTerrainAndVegetation();
        }

        private void OnTerrainsMoved()
        {
            InvalidateTerrainAndVegetation();
        }

        private void InvalidateTerrainAndVegetation()
        {
            TerrainRevision++;
            var changed = false;
            for (var i = 0; i < systems.Count; i++)
            {
                var state = systems[i];
                if (state.Snapshots.Count > 0) changed = true;
                state.Snapshots.Clear();
                state.Dirty = true;
                state.NextCaptureTime = 0f;
            }
            if (changed) SnapshotVersion++;
        }

        private void ClearSystemSnapshot(SystemState state)
        {
            var changed = state.Snapshots.Count > 0;
            state.Snapshots.Clear();
            state.Dirty = true;
            state.NextCaptureTime = 0f;
            if (changed) SnapshotVersion++;
        }

        private void DetachManager()
        {
            if (manager != null)
            {
                manager.OnAddVegetationSystemDelegate -= OnVegetationSystemAdded;
                manager.OnRemoveVegetationSystemDelegate -= OnVegetationSystemRemoved;
            }
            for (var i = systems.Count - 1; i >= 0; i--) DetachSystem(systems[i]);
            if (systems.Count > 0) SnapshotVersion++;
            systems.Clear();
            manager = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            TerrainGrid.TerrainDataLoaded -= OnTerrainDataChanged;
            TerrainGrid.TerrainDataAboutToBeUnloaded -= OnTerrainDataChanged;
            TerrainGrid.Initialized -= OnTerrainGridInitialized;
            if (terrainGrid != null) terrainGrid.TerrainsMoved -= OnTerrainsMoved;
            terrainGrid = null;
            DetachManager();
        }

        private static bool IsLeafBearingTree(VegetationItemInfoPro item)
        {
            if (item == null || item.VegetationType != VegetationType.Tree) return false;
            var name = (item.Name ?? string.Empty) + " " +
                (item.VegetationPrefab != null ? item.VegetationPrefab.name : string.Empty);
            return !ContainsAny(name, EvergreenNames) && !ContainsAny(name, NonTreeNames);
        }

        private static bool IsFlower(VegetationItemInfoPro item)
        {
            if (item == null || item.VegetationType == VegetationType.Tree) return false;
            var name = (item.Name ?? "") + " " + (item.VegetationPrefab != null ? item.VegetationPrefab.name : "");
            return name.IndexOf("flower", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ContainsAny(string value, string[] candidates)
        {
            for (var i = 0; i < candidates.Length; i++)
                if (value.IndexOf(candidates[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static bool BoundsNear(Bounds bounds, Vector3 point, float radius)
        {
            var closestX = Mathf.Clamp(point.x, bounds.min.x, bounds.max.x);
            var closestZ = Mathf.Clamp(point.z, bounds.min.z, bounds.max.z);
            var x = point.x - closestX;
            var z = point.z - closestZ;
            return x * x + z * z <= radius * radius;
        }

        private static float HorizontalSqrDistance(Vector3 first, Vector3 second)
        {
            var x = first.x - second.x;
            var z = first.z - second.z;
            return x * x + z * z;
        }

        private static uint StableStringHash(string value)
        {
            unchecked
            {
                var hash = 2166136261u;
                if (value == null) return hash;
                for (var i = 0; i < value.Length; i++)
                    hash = (hash ^ value[i]) * 16777619u;
                return hash;
            }
        }

        private static long BuildKey(int systemId, int cellIndex, int packageIndex,
            uint itemSeed, int matrixIndex)
        {
            unchecked
            {
                var hash = 1469598103934665603UL;
                hash = (hash ^ (uint)systemId) * 1099511628211UL;
                hash = (hash ^ (uint)cellIndex) * 1099511628211UL;
                hash = (hash ^ (uint)packageIndex) * 1099511628211UL;
                hash = (hash ^ itemSeed) * 1099511628211UL;
                hash = (hash ^ (uint)matrixIndex) * 1099511628211UL;
                return (long)(hash | 0x8000000000000000UL);
            }
        }
    }
}
