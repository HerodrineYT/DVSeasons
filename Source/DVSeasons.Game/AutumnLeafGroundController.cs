using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    /// <summary>
    /// Maintains a bounded, camera-local carpet of individual autumn leaves.
    /// Every leaf comes from a real deciduous Terrain tree, settles on an upward
    /// facing world surface, and can be lifted again by a moving train's wake.
    /// </summary>
    internal sealed class AutumnLeafGroundController : IDisposable
    {
        private int leafLimit = 2200;
        private const int InitialCapacity = 2200;
        private const int MaximumSources = 96;
        private const int MaximumTrainCars = 24;
        private const int MaximumSurfaceCars = 16;
        private const int TreeProbeBudget = 320;
        private const int PopulationRaycastsPerFrame = 5;
        private const int FallingLeafReserve = 120;
        private const int MaximumHiddenWindSpawnsPerFrame = 2;
        private const float GroundLeavesPerSource = 16f;
        private const float InitialCoverCreditPerSource = 10f;
        private const float MinimumCoverGrowthPerSecond = 8f;
        private const float MaximumCoverGrowthPerSecond = 72f;
        private const float CoverDeficitResponse = 2.4f;
        private const float LeafVisibilityRadius = 105f;
        private const float TreeSearchRadius = 115f;
        private const float GroundLitterSpreadRadius = 11f;
        private const float HiddenSourceScanInterval = 0.35f;
        private const float HiddenIngressRevealDelay = 0.18f;
        private const float HiddenIngressRevealDuration = 0.62f;
        private static readonly string[] EvergreenNames =
            { "fir", "pine", "spruce", "conifer", "evergreen", "abies", "picea" };
        private static readonly string[] NonTreeNames =
            { "bush", "shrub", "grass", "fern", "stump", "dead", "log" };

        private sealed class LeafBody
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public Vector3 LandingPosition;
            public Vector3 GroundNormal;
            public Vector3 Rotation;
            public Vector3 AngularVelocity;
            public Color32 Color;
            public uint AppearanceSeed;
            public float Size;
            public float Age;
            public float FlutterPhase;
            public float RestUntil;
            public bool LandingRefreshed;
            public bool Settled;
            public bool HiddenWindIngress;
            public Transform SurfaceTransform;
            public Vector3 SurfaceLocalPosition;
            public Vector3 SurfaceLocalNormal;
            public Quaternion SurfaceLocalRotation;
            public Vector3 SurfaceVelocity;
            public Vector3 SurfaceVelocitySamplePosition;
            public bool SurfacePoseReady;
            public TrainCar ReleasedFromCar;
        }

        private struct LeafSurface
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Transform Anchor;
            public Vector3 AnchorLocalPosition;
            public Vector3 AnchorLocalNormal;
            public Vector3 AnchorVelocity;
        }

        private struct TreeSource
        {
            public long Key;
            public Terrain Terrain;
            public bool FromVegetationStudio;
            public Vector3 BasePosition;
            public Vector3 CanopyPosition;
            public float CrownRadius;
        }

        private struct TrainWake
        {
            public TrainCar Car;
            public float CameraDistance;
            public Vector3 Centre;
            public Vector3 Direction;
            public Vector3 Right;
            public float HalfLength;
            public float HalfWidth;
            public float SpeedMetresPerSecond;
        }

        private struct SurfaceCar
        {
            public TrainCar Car;
            public float CameraDistance;
            public float SpeedMetresPerSecond;
        }

        private sealed class CollisionCar
        {
            public TrainCar Car;
            public Transform PoseTransform;
            public Matrix4x4 PreviousWorldToLocal, CurrentLocalToWorld;
            public Bounds SweptBounds;
            public bool Moved;
        }

        private LeafBody[] leaves = new LeafBody[InitialCapacity];
        private ParticleSystem.Particle[] particleBuffer =
            new ParticleSystem.Particle[InitialCapacity];
        private readonly List<TreeSource> sources = new List<TreeSource>(MaximumSources);
        private readonly HashSet<long> sourceKeys = new HashSet<long>();
        private readonly TreeSource[] hiddenWindSources = new TreeSource[MaximumSources];
        private AutumnTreeSourceProvider treeSourceProvider;
        private readonly List<AutumnTreeSourceSnapshot> vegetationStudioSnapshots =
            new List<AutumnTreeSourceSnapshot>(MaximumSources * 2);
        private readonly TrainWake[] trainWakes = new TrainWake[MaximumTrainCars];
        private readonly SurfaceCar[] surfaceCars = new SurfaceCar[MaximumSurfaceCars];
        private readonly List<CollisionCar> collisionCars = new List<CollisionCar>();
        private int leafCount;
        private int trainWakeCount;
        private int surfaceCarCount;
        private float nextTreeScan;
        private float nextTrainScan;
        private float fallingAccumulator;
        private float hiddenWindAccumulator;
        private float coverAccumulator;
        private float windLiftAccumulator;
        private int windLiftCursor;
        private int surfaceQueriesRemaining;
        private int vegetationStudioSnapshotVersion = -1;
        private int terrainRevision = -1;
        private bool usingVegetationStudioSources;
        private int hiddenWindSourceCount;
        private float nextHiddenSourceScan;
        private Vector3 hiddenSourceCameraPosition;
        private Vector3 hiddenSourceCameraForward;
        private Vector3 hiddenSourceWindDirection;
        private bool hiddenSourceCacheReady;
        private uint randomState = 0x8f31a6d5u;
        private GameObject owner;
        private ParticleSystem particles;
        private Material material;
        private Texture2D texture;
        private Mesh leafMesh;
        private float renderWeight;
        private SeasonState pendingState;
        private Vector3 pendingWind;
        private float pendingDeltaTime;
        private bool tickPending;
        private static readonly Vector3[] AmbientDirections =
            { Vector3.up, Vector3.right, Vector3.left, Vector3.forward, Vector3.back, Vector3.down };
        private readonly Color[] ambientColours = new Color[6];

        public void Apply(SeasonState state, Vector3 windVelocity, int configuredLimit)
        {
            var weight = AutumnEffectsProfile.GetWeight(state);
            var camera = Camera.main;
            if (weight <= 0.001f || camera == null)
            {
                if (treeSourceProvider != null) treeSourceProvider.SetEnabled(false);
                ClearLeaves();
                return;
            }

            leafLimit = configuredLimit <= 0 ? int.MaxValue : Math.Max(100, configuredLimit);
            while (leafCount > leafLimit) RemoveLeafAt(leafCount - 1);
            EnsureTreeSourceProvider();
            EnsureRenderer();
            if (particles == null) return;

            pendingState = state;
            pendingWind = windVelocity;
            pendingDeltaTime = Mathf.Min(Time.deltaTime, 0.05f);
            tickPending = true;
            renderWeight = weight;
        }

        private void AdvanceLeaves(Camera camera, SeasonState state, Vector3 windVelocity, float deltaTime)
        {
            var weight = AutumnEffectsProfile.GetWeight(state);
            var worldOffset = DV.OriginShift.OriginShift.currentMove;
            var cameraPosition = camera.transform.position - worldOffset;
            treeSourceProvider.UpdateInterest(camera.transform.position, TreeSearchRadius);
            surfaceQueriesRemaining = 10;
            RefreshTreeSources(cameraPosition, worldOffset);
            RefreshTrainCars(cameraPosition);
            UpdateTrainWakes(worldOffset);
            UpdateCollisionCars(worldOffset, cameraPosition);
            // WALKABLE colliders are parented to the detached interior, whose
            // final pose is assigned after UMM.Update. Query that render pose.
            if (collisionCars.Count > 0) Physics.SyncTransforms();
            UpdateAnchoredLeaves(worldOffset, deltaTime);
            PruneDistantLeaves(cameraPosition);
            SimulateLeaves(weight, cameraPosition, worldOffset, windVelocity, deltaTime);
            GrowGroundCover(weight, cameraPosition, worldOffset, windVelocity, deltaTime);
            EmitFallingLeaves(state, cameraPosition, worldOffset, windVelocity, deltaTime);
            EmitHiddenWindIngress(state, camera, cameraPosition, worldOffset,
                windVelocity, deltaTime);
        }

        private void BeforeCameraCull(Camera camera)
        {
            if (camera != Camera.main || particles == null) return;
            if (tickPending)
            {
                tickPending = false;
                AdvanceLeaves(camera, pendingState, pendingWind, pendingDeltaTime);
            }
            if (leafCount == 0) return;
            // The detached WALKABLE/interior hierarchy and rigidbody interpolation
            // can move after UMM.Update. Resolve the stored local pose again after
            // those updates, immediately before the camera consumes the particles.
            var worldOffset = DV.OriginShift.OriginShift.currentMove;
            UpdateAnchoredLeaves(worldOffset, 0f);
            UpdateLighting();
            RenderLeaves(renderWeight, camera.transform.position - worldOffset, worldOffset);
        }

        private void EnsureCapacity(int required)
        {
            if (required <= leaves.Length) return;
            // Unlimited means no configured population ceiling, not an up-front
            // allocation. Keep the existing per-frame spawning/query budgets.
            int capacity = (int)Math.Min(leafLimit, Math.Max((long)required, leaves.Length + Math.Max(512L, leaves.Length / 2L)));
            Array.Resize(ref leaves, capacity);
            Array.Resize(ref particleBuffer, capacity);
            if (particles != null) { var main = particles.main; main.maxParticles = capacity; }
        }

        private void RefreshTreeSources(Vector3 cameraPosition, Vector3 worldOffset)
        {
            RefreshVegetationStudioSources(cameraPosition, worldOffset);
            if (terrainRevision != treeSourceProvider.TerrainRevision)
            {
                terrainRevision = treeSourceProvider.TerrainRevision;
                RemoveSources(false);
                nextTreeScan = 0f;
            }

            if (Time.realtimeSinceStartup < nextTreeScan) return;
            nextTreeScan = Time.realtimeSinceStartup + 0.42f;

            for (var i = sources.Count - 1; i >= 0; i--)
            {
                if ((sources[i].FromVegetationStudio || sources[i].Terrain != null) &&
                    HorizontalSqrDistance(sources[i].BasePosition, cameraPosition) <=
                    (TreeSearchRadius + 20f) * (TreeSearchRadius + 20f)) continue;
                sourceKeys.Remove(sources[i].Key);
                sources.RemoveAt(i);
            }

            // VSP is the authoritative runtime forest. Unity Terrain trees are
            // retained only as a compatibility fallback for maps without VSP data.
            if (usingVegetationStudioSources) return;

            var terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0) return;
            var remainingBudget = TreeProbeBudget;
            for (var terrainIndex = 0; terrainIndex < terrains.Length && remainingBudget > 0; terrainIndex++)
            {
                var terrain = terrains[terrainIndex];
                if (!TerrainNear(terrain, cameraPosition + worldOffset, TreeSearchRadius + 30f)) continue;
                var data = terrain.terrainData;
                if (data == null || data.treeInstanceCount <= 0) continue;
                var prototypes = data.treePrototypes;
                var probes = Mathf.Min(data.treeInstanceCount,
                    Mathf.Min(remainingBudget, Mathf.Max(32, TreeProbeBudget / terrains.Length)));
                for (var probe = 0; probe < probes; probe++)
                {
                    randomState = randomState * 1664525u + 1013904223u;
                    var treeIndex = (int)(randomState % (uint)data.treeInstanceCount);
                    var tree = data.GetTreeInstance(treeIndex);
                    if (!IsLeafBearingTree(prototypes, tree.prototypeIndex)) continue;
                    var baseWorld = terrain.transform.position + Vector3.Scale(tree.position, data.size);
                    var baseStable = baseWorld - worldOffset;
                    if (HorizontalSqrDistance(baseStable, cameraPosition) >
                        TreeSearchRadius * TreeSearchRadius) continue;
                    var key = ((long)(uint)terrain.GetInstanceID() << 32) | (uint)treeIndex;
                    if (sourceKeys.Contains(key)) continue;
                    var height = Mathf.Clamp(8f * tree.heightScale, 4.5f, 15f);
                    var candidate = new TreeSource
                    {
                        Key = key,
                        Terrain = terrain,
                        FromVegetationStudio = false,
                        BasePosition = baseStable,
                        CanopyPosition = baseStable + Vector3.up * height,
                        CrownRadius = Mathf.Clamp(3.2f * tree.widthScale, 1.8f, 5.8f)
                    };
                    AddNearestSource(candidate, cameraPosition);
                }
                remainingBudget -= probes;
            }
        }

        private void EnsureTreeSourceProvider()
        {
            if (treeSourceProvider != null) return;
            treeSourceProvider = new AutumnTreeSourceProvider();
            vegetationStudioSnapshotVersion = -1;
            terrainRevision = -1;
            usingVegetationStudioSources = false;
            nextTreeScan = 0f;
        }

        private void RefreshVegetationStudioSources(Vector3 cameraPosition, Vector3 worldOffset)
        {
            if (vegetationStudioSnapshotVersion == treeSourceProvider.SnapshotVersion) return;
            vegetationStudioSnapshotVersion = treeSourceProvider.SnapshotVersion;
            treeSourceProvider.CopySnapshots(vegetationStudioSnapshots);
            var haveSnapshots = vegetationStudioSnapshots.Count > 0;
            if (haveSnapshots != usingVegetationStudioSources)
            {
                sources.Clear();
                sourceKeys.Clear();
                usingVegetationStudioSources = haveSnapshots;
                nextTreeScan = 0f;
            }
            else
            {
                RemoveSources(true);
            }

            if (!haveSnapshots) return;
            for (var i = 0; i < vegetationStudioSnapshots.Count; i++)
            {
                var snapshot = vegetationStudioSnapshots[i];
                // The provider has already matched VSP rendering exactly with
                // raw matrix translation + FloatingOriginOffset. Convert that
                // current world position to this controller's stable space once.
                var baseStable = snapshot.BaseWorldPosition - worldOffset;
                if (HorizontalSqrDistance(baseStable, cameraPosition) >
                    TreeSearchRadius * TreeSearchRadius) continue;
                AddNearestSource(new TreeSource
                {
                    Key = snapshot.Key,
                    Terrain = null,
                    FromVegetationStudio = true,
                    BasePosition = baseStable,
                    CanopyPosition = snapshot.CanopyWorldPosition - worldOffset,
                    CrownRadius = snapshot.CrownRadius
                }, cameraPosition);
            }
        }

        private void RemoveSources(bool vegetationStudioSources)
        {
            for (var i = sources.Count - 1; i >= 0; i--)
            {
                if (sources[i].FromVegetationStudio != vegetationStudioSources) continue;
                sourceKeys.Remove(sources[i].Key);
                sources.RemoveAt(i);
            }
        }

        private void AddNearestSource(TreeSource candidate, Vector3 cameraPosition)
        {
            if (sources.Count < MaximumSources)
            {
                sources.Add(candidate);
                sourceKeys.Add(candidate.Key);
                coverAccumulator += InitialCoverCreditPerSource;
                return;
            }
            var farthestIndex = -1;
            var farthestDistance = HorizontalSqrDistance(candidate.BasePosition, cameraPosition);
            for (var i = 0; i < sources.Count; i++)
            {
                var distance = HorizontalSqrDistance(sources[i].BasePosition, cameraPosition);
                if (distance <= farthestDistance) continue;
                farthestDistance = distance;
                farthestIndex = i;
            }
            if (farthestIndex < 0) return;
            sourceKeys.Remove(sources[farthestIndex].Key);
            sources[farthestIndex] = candidate;
            sourceKeys.Add(candidate.Key);
            coverAccumulator += InitialCoverCreditPerSource;
        }

        private void RefreshTrainCars(Vector3 cameraPosition)
        {
            if (Time.realtimeSinceStartup < nextTrainScan) return;
            nextTrainScan = Time.realtimeSinceStartup + 0.35f;
            trainWakeCount = 0;
            surfaceCarCount = 0;
            var worldOffset = DV.OriginShift.OriginShift.currentMove;
            foreach (var car in RailSnowGameSource.GetCars())
            {
                if (car == null || car.carType == DV.ThingTypes.TrainCarType.HandCar || !car.gameObject.activeInHierarchy || car.rb == null) continue;
                var distance = HorizontalSqrDistance(car.transform.position - worldOffset, cameraPosition);
                if (distance > (LeafVisibilityRadius + 45f) * (LeafVisibilityRadius + 45f)) continue;
                var horizontalVelocity = Vector3.ProjectOnPlane(car.GetVelocity(), Vector3.up);
                InsertSurfaceCar(new SurfaceCar
                {
                    Car = car,
                    CameraDistance = distance,
                    SpeedMetresPerSecond = horizontalVelocity.magnitude
                });
                if (horizontalVelocity.magnitude * 3.6f <= AutumnEffectsProfile.TrainLiftStartKmh) continue;
                InsertTrainWake(new TrainWake { Car = car, CameraDistance = distance });
            }
        }

        private void InsertTrainWake(TrainWake wake)
        {
            var index = 0;
            while (index < trainWakeCount &&
                trainWakes[index].CameraDistance <= wake.CameraDistance) index++;
            if (index >= MaximumTrainCars) return;
            var newCount = Mathf.Min(MaximumTrainCars, trainWakeCount + 1);
            for (var i = newCount - 1; i > index; i--)
                trainWakes[i] = trainWakes[i - 1];
            trainWakes[index] = wake;
            trainWakeCount = newCount;
        }

        private void InsertSurfaceCar(SurfaceCar candidate)
        {
            var index = 0;
            while (index < surfaceCarCount &&
                surfaceCars[index].CameraDistance <= candidate.CameraDistance) index++;
            if (index >= MaximumSurfaceCars) return;
            var newCount = Mathf.Min(MaximumSurfaceCars, surfaceCarCount + 1);
            for (var i = newCount - 1; i > index; i--)
                surfaceCars[i] = surfaceCars[i - 1];
            surfaceCars[index] = candidate;
            surfaceCarCount = newCount;
        }

        private void UpdateTrainWakes(Vector3 worldOffset)
        {
            for (var i = trainWakeCount - 1; i >= 0; i--)
            {
                var wake = trainWakes[i];
                var car = wake.Car;
                if (car == null || !car.gameObject.activeInHierarchy || car.rb == null)
                {
                    RemoveTrainWakeAt(i);
                    continue;
                }
                var horizontalVelocity = Vector3.ProjectOnPlane(car.GetVelocity(), Vector3.up);
                wake.SpeedMetresPerSecond = horizontalVelocity.magnitude;
                if (wake.SpeedMetresPerSecond * 3.6f <= AutumnEffectsProfile.TrainLiftStartKmh)
                {
                    RemoveTrainWakeAt(i);
                    continue;
                }
                wake.Direction = horizontalVelocity / wake.SpeedMetresPerSecond;
                wake.Right = Vector3.Cross(Vector3.up, wake.Direction).normalized;
                var bounds = car.Bounds;
                wake.Centre = car.transform.TransformPoint(bounds.center) - worldOffset;
                wake.HalfLength = ProjectedExtent(car.transform, bounds.extents, wake.Direction);
                wake.HalfWidth = Mathf.Max(1.1f,
                    ProjectedExtent(car.transform, bounds.extents, wake.Right));
                trainWakes[i] = wake;
            }
        }

        private void RemoveTrainWakeAt(int index)
        {
            trainWakeCount--;
            for (var i = index; i < trainWakeCount; i++)
                trainWakes[i] = trainWakes[i + 1];
            trainWakes[trainWakeCount] = default(TrainWake);
        }

        private void GrowGroundCover(float weight, Vector3 cameraPosition, Vector3 worldOffset,
            Vector3 windVelocity, float deltaTime)
        {
            if (sources.Count == 0 || leafCount >= leafLimit) return;
            var desired = leafLimit == int.MaxValue ? leafCount + PopulationRaycastsPerFrame :
                Mathf.Min(Math.Max(0, leafLimit - Math.Min(FallingLeafReserve, leafLimit / 10)),
                    Mathf.RoundToInt(sources.Count * GroundLeavesPerSource * weight * Math.Max(1f, leafLimit / 2200f)));
            if (leafCount >= desired) return;
            coverAccumulator += Mathf.Min(MaximumCoverGrowthPerSecond,
                Mathf.Max(MinimumCoverGrowthPerSecond,
                    (desired - leafCount) * CoverDeficitResponse)) * deltaTime;
            var attempts = Mathf.Min(PopulationRaycastsPerFrame, Mathf.FloorToInt(coverAccumulator));
            coverAccumulator -= attempts;
            for (var attempt = 0; attempt < attempts && leafCount < desired; attempt++)
                SpawnFromTree(true, cameraPosition, worldOffset, windVelocity);
        }

        private void EmitFallingLeaves(SeasonState state, Vector3 cameraPosition, Vector3 worldOffset,
            Vector3 windVelocity, float deltaTime)
        {
            if (sources.Count == 0 || leafCount >= leafLimit) return;
            fallingAccumulator += AutumnEffectsProfile.GetLeafEmissionRate(state,
                windVelocity.magnitude) * deltaTime;
            var count = Mathf.Min(3, Mathf.FloorToInt(fallingAccumulator));
            fallingAccumulator -= count;
            for (var i = 0; i < count && leafCount < leafLimit; i++)
                SpawnFromTree(false, cameraPosition, worldOffset, windVelocity);
        }

        private void EmitHiddenWindIngress(SeasonState state, Camera camera,
            Vector3 cameraPosition, Vector3 worldOffset, Vector3 windVelocity,
            float deltaTime)
        {
            var horizontalWind = Vector3.ProjectOnPlane(windVelocity, Vector3.up);
            var windSpeed = horizontalWind.magnitude;
            var windStrength = AutumnEffectsProfile.GetHiddenLeafIngressStrength(windSpeed);
            var autumnWeight = AutumnEffectsProfile.GetWeight(state);
            if (sources.Count == 0 || windStrength <= 0f || autumnWeight <= 0f)
            {
                hiddenWindAccumulator = 0f;
                hiddenWindSourceCount = 0;
                hiddenSourceCacheReady = false;
                return;
            }

            var populationLimit = leafLimit;
            if (leafCount >= populationLimit)
            {
                hiddenWindAccumulator = Mathf.Min(hiddenWindAccumulator, 0.99f);
                return;
            }

            hiddenWindAccumulator = Mathf.Min(2.99f, hiddenWindAccumulator +
                AutumnEffectsProfile.GetHiddenLeafIngressRate(state, windSpeed) * deltaTime);
            var attempts = Mathf.Min(MaximumHiddenWindSpawnsPerFrame,
                Mathf.FloorToInt(hiddenWindAccumulator));
            if (attempts <= 0) return;

            var windDirection = horizontalWind / windSpeed;
            RefreshHiddenWindSources(camera, cameraPosition, worldOffset, windDirection);
            if (hiddenWindSourceCount == 0)
            {
                // Do not bank a burst while the player is away from a suitable
                // real tree. Extra leaves must never fall from an arbitrary sky
                // volume merely because a source later streams in.
                hiddenWindAccumulator = Mathf.Min(hiddenWindAccumulator, 0.99f);
                return;
            }

            hiddenWindAccumulator -= attempts;
            for (var i = 0; i < attempts && leafCount < populationLimit; i++)
            {
                var source = hiddenWindSources[RandomIndex(hiddenWindSourceCount)];
                SpawnFromSource(source, false, cameraPosition, worldOffset,
                    windVelocity, true);
            }
        }

        private void RefreshHiddenWindSources(Camera camera, Vector3 cameraPosition,
            Vector3 worldOffset, Vector3 windDirection)
        {
            var cameraForward = camera.transform.forward;
            var cacheStillValid = hiddenSourceCacheReady &&
                Time.realtimeSinceStartup < nextHiddenSourceScan &&
                HorizontalSqrDistance(hiddenSourceCameraPosition, cameraPosition) <= 4f &&
                Vector3.Dot(hiddenSourceCameraForward, cameraForward) >= 0.998f &&
                Vector3.Dot(hiddenSourceWindDirection, windDirection) >= 0.985f;
            if (cacheStillValid) return;

            hiddenWindSourceCount = 0;
            var windSide = Vector3.Cross(Vector3.up, windDirection).normalized;
            for (var i = 0; i < sources.Count && hiddenWindSourceCount < MaximumSources; i++)
            {
                var source = sources[i];
                var relative = Vector3.ProjectOnPlane(
                    source.CanopyPosition - cameraPosition, Vector3.up);
                var distance = relative.magnitude;
                if (distance <= 0.001f) continue;
                var toCamera = -relative;
                var upwindAlignment = Vector3.Dot(toCamera / distance, windDirection);
                var crosswindDistance = Mathf.Abs(Vector3.Dot(toCamera, windSide));
                var viewport = camera.WorldToViewportPoint(source.CanopyPosition + worldOffset);
                var projectedRadius = ProjectedViewportRadius(camera,
                    source.CrownRadius, viewport.z);
                if (!AutumnEffectsProfile.IsHiddenLeafIngressSource(viewport.x, viewport.y,
                    viewport.z, projectedRadius, distance, upwindAlignment,
                    crosswindDistance)) continue;
                hiddenWindSources[hiddenWindSourceCount++] = source;
            }

            hiddenSourceCameraPosition = cameraPosition;
            hiddenSourceCameraForward = cameraForward;
            hiddenSourceWindDirection = windDirection;
            hiddenSourceCacheReady = true;
            nextHiddenSourceScan = Time.realtimeSinceStartup + HiddenSourceScanInterval;
        }

        private static float ProjectedViewportRadius(Camera camera, float worldRadius,
            float viewportDepth)
        {
            if (viewportDepth <= 0.001f) return float.PositiveInfinity;
            var aspect = Mathf.Max(0.1f, camera.aspect);
            if (camera.orthographic)
                return worldRadius / Mathf.Max(0.01f, camera.orthographicSize * 2f * aspect);
            var nearestDepth = Mathf.Max(0.01f, viewportDepth - worldRadius);
            var halfWidth = nearestDepth * Mathf.Tan(camera.fieldOfView *
                Mathf.Deg2Rad * 0.5f) * aspect;
            return worldRadius / Mathf.Max(0.01f, halfWidth * 2f);
        }

        private void SpawnFromTree(bool alreadySettled, Vector3 cameraPosition, Vector3 worldOffset,
            Vector3 windVelocity)
        {
            if (sources.Count == 0) return;
            var source = sources[RandomIndex(sources.Count)];
            SpawnFromSource(source, alreadySettled, cameraPosition, worldOffset,
                windVelocity, false);
        }

        private void SpawnFromSource(TreeSource source, bool alreadySettled,
            Vector3 cameraPosition, Vector3 worldOffset, Vector3 windVelocity,
            bool hiddenWindIngress)
        {
            var angle = Next01() * Mathf.PI * 2f;
            var radius = Mathf.Sqrt(Next01()) * source.CrownRadius;
            var canopyOffset = new Vector3(Mathf.Cos(angle) * radius, 0f,
                Mathf.Sin(angle) * radius);
            var windScatter = Vector3.ClampMagnitude(Vector3.ProjectOnPlane(windVelocity, Vector3.up) *
                (Next01() * 0.55f), 7f);
            var surfaceOffset = canopyOffset + (alreadySettled ? windScatter : Vector3.zero);
            if (alreadySettled)
            {
                // Existing litter may have been carried beyond the crown before
                // the player arrived. This also lets real nearby trees seed roofs,
                // track, sleepers and parked rolling stock without sky emitters.
                var litterAngle = Next01() * Mathf.PI * 2f;
                var litterRadius = Mathf.Sqrt(Next01()) * GroundLitterSpreadRadius;
                surfaceOffset += new Vector3(Mathf.Cos(litterAngle) * litterRadius, 0f,
                    Mathf.Sin(litterAngle) * litterRadius);
            }
            Vector3 surfaceProbe;
            if (!alreadySettled || !TryGetRollingStockSurfaceProbe(source, worldOffset,
                out surfaceProbe))
                surfaceProbe = source.CanopyPosition + surfaceOffset;
            LeafSurface surface;
            if (!TryFindSurface(surfaceProbe, worldOffset, out surface)) return;
            var maximumSpawnRadius = hiddenWindIngress
                ? LeafVisibilityRadius + 15f
                : LeafVisibilityRadius;
            if (HorizontalSqrDistance(surface.Position, cameraPosition) >
                maximumSpawnRadius * maximumSpawnRadius) return;

            // Species/pigment are in the atlas; stable seeds survive uploads,
            // array compaction, wind lift-off and reattachment to rolling stock.
            var tone = (byte)Mathf.Lerp(224f, 255f, Next01());
            var colour = new Color32(tone, tone, tone, 255);
            EnsureCapacity(leafCount + 1);
            var leaf = leaves[leafCount] ?? new LeafBody();
            leaf.Position = Vector3.zero;
            leaf.Velocity = Vector3.zero;
            AssignSurface(leaf, surface, alreadySettled);
            leaf.Rotation = Vector3.zero;
            leaf.AngularVelocity = new Vector3(
                Mathf.Lerp(-240f, 240f, Next01()),
                Mathf.Lerp(-180f, 180f, Next01()),
                Mathf.Lerp(-300f, 300f, Next01()));
            leaf.Color = colour;
            leaf.AppearanceSeed = (uint)(Next01() * 16777215f) + 1;
            leaf.Size = Mathf.Lerp(0.13f, 0.27f, Next01());
            leaf.Age = 0f;
            leaf.FlutterPhase = Next01() * Mathf.PI * 2f;
            leaf.RestUntil = 0f;
            leaf.LandingRefreshed = alreadySettled;
            leaf.Settled = alreadySettled;
            leaf.HiddenWindIngress = hiddenWindIngress;
            if (alreadySettled)
            {
                leaf.Position = surface.Position + surface.Normal * 0.018f;
                leaf.Rotation = GroundRotation(surface.Normal, Next01() * 360f);
                CaptureSurfaceRotation(leaf);
                leaf.RestUntil = Time.realtimeSinceStartup + Next01() * 0.8f;
            }
            else
            {
                // The visible leaf always starts inside its actual tree crown;
                // wind affects its travel and landing point, not its origin.
                var fallingPosition = source.CanopyPosition + canopyOffset +
                    Vector3.up * Mathf.Lerp(-0.6f, 0.8f, Next01());
                if (fallingPosition.y <= surface.Position.y + 0.08f)
                {
                    AssignSurface(leaf, surface, true);
                    leaf.Position = surface.Position + surface.Normal * 0.018f;
                    leaf.Rotation = GroundRotation(surface.Normal, Next01() * 360f);
                    CaptureSurfaceRotation(leaf);
                    leaf.RestUntil = Time.realtimeSinceStartup + Next01() * 0.8f;
                    leaf.LandingRefreshed = true;
                    leaf.Settled = true;
                }
                else
                {
                    leaf.Position = fallingPosition;
                    var horizontalWind = Vector3.ProjectOnPlane(windVelocity, Vector3.up);
                    if (hiddenWindIngress && horizontalWind.sqrMagnitude > 0.001f)
                    {
                        var windDirection = horizontalWind.normalized;
                        var windSide = Vector3.Cross(Vector3.up, windDirection);
                        leaf.Velocity = horizontalWind * Mathf.Lerp(0.48f, 0.62f, Next01()) +
                            windSide * Mathf.Lerp(-0.35f, 0.35f, Next01()) +
                            Vector3.up * Mathf.Lerp(-0.15f, 0.12f, Next01());
                    }
                    else
                    {
                        leaf.Velocity = horizontalWind * Mathf.Lerp(0.22f, 0.48f, Next01()) +
                            new Vector3(Mathf.Lerp(-0.3f, 0.3f, Next01()),
                                Mathf.Lerp(-0.35f, 0.05f, Next01()),
                                Mathf.Lerp(-0.3f, 0.3f, Next01()));
                    }
                    leaf.Rotation = new Vector3(Next01() * 360f, Next01() * 360f, Next01() * 360f);
                }
            }
            leaves[leafCount++] = leaf;
        }

        private bool TryGetRollingStockSurfaceProbe(TreeSource source, Vector3 worldOffset,
            out Vector3 surfaceProbe)
        {
            surfaceProbe = Vector3.zero;
            // A minority of existing litter is aimed at nearby parked rolling
            // stock. The one shared downward raycast still decides the actual
            // physical surface and remains inside the global per-frame budget.
            if (surfaceCarCount == 0 || Next01() >= 0.24f) return false;
            var first = RandomIndex(surfaceCarCount);
            var transportRadius = GroundLitterSpreadRadius + source.CrownRadius + 8f;
            for (var offset = 0; offset < surfaceCarCount; offset++)
            {
                var candidate = surfaceCars[(first + offset) % surfaceCarCount];
                var car = candidate.Car;
                if (car == null || !car.gameObject.activeInHierarchy || car.rb == null ||
                    candidate.SpeedMetresPerSecond * 3.6f > AutumnEffectsProfile.TrainLiftStartKmh)
                    continue;
                var centre = car.transform.TransformPoint(car.Bounds.center) - worldOffset;
                if (HorizontalSqrDistance(centre, source.BasePosition) >
                    transportRadius * transportRadius) continue;
                var bounds = car.Bounds;
                if (bounds.extents.x < 0.1f || bounds.extents.z < 0.1f) continue;
                var localProbe = bounds.center + new Vector3(
                    Mathf.Lerp(-0.82f, 0.82f, Next01()) * bounds.extents.x,
                    bounds.extents.y + 1.25f,
                    Mathf.Lerp(-0.82f, 0.82f, Next01()) * bounds.extents.z);
                surfaceProbe = car.transform.TransformPoint(localProbe) - worldOffset;
                return true;
            }
            return false;
        }

        private void SimulateLeaves(float weight, Vector3 cameraPosition, Vector3 worldOffset,
            Vector3 windVelocity, float deltaTime)
        {
            var horizontalWind = Vector3.ProjectOnPlane(windVelocity, Vector3.up);
            LiftSettledLeavesByWind(weight, horizontalWind, deltaTime);
            for (var i = 0; i < leafCount; i++)
            {
                var leaf = leaves[i];
                leaf.Age += deltaTime;
                TrainWake wake;
                float wakeStrength;
                var hasWake = FindStrongestWake(leaf.Position, weight, out wake, out wakeStrength);
                if (leaf.Settled)
                {
                    if (hasWake && wakeStrength > 0f &&
                        Time.realtimeSinceStartup >= leaf.RestUntil &&
                        Next01() < Mathf.Clamp01(wakeStrength * deltaTime * 9f))
                    {
                        LiftLeaf(leaf, wake, wakeStrength, horizontalWind);
                    }
                }
                if (!leaf.Settled)
                {
                    var previousPosition = leaf.Position;
                    var targetWind = horizontalWind * 0.55f;
                    leaf.Velocity.x = Mathf.MoveTowards(leaf.Velocity.x, targetWind.x, deltaTime * 0.8f);
                    leaf.Velocity.z = Mathf.MoveTowards(leaf.Velocity.z, targetWind.z, deltaTime * 0.8f);
                    leaf.Velocity.y = Mathf.Max(-3.4f, leaf.Velocity.y - 1.65f * deltaTime);
                    var flutter = Mathf.Sin(leaf.FlutterPhase + leaf.Age * 7.5f);
                    var flutterSide = new Vector3(-leaf.Velocity.z, 0f, leaf.Velocity.x).normalized;
                    leaf.Position += (leaf.Velocity + flutterSide * flutter * 0.34f) * deltaTime;
                    leaf.Rotation += leaf.AngularVelocity * deltaTime;

                    if (MoveWithCollisions(leaf, previousPosition, worldOffset)) continue;

                    if (hasWake && wakeStrength > 0f)
                    {
                        leaf.Velocity += (wake.Direction * wake.SpeedMetresPerSecond * 0.18f +
                            Vector3.up * 1.5f) * (wakeStrength * deltaTime);
                    }
                    if (leaf.Position.y <= leaf.LandingPosition.y + 0.025f)
                    {
                        LeafSurface surface;
                        if (TryFindSurface(leaf.Position + Vector3.up, worldOffset, out surface) &&
                            surface.Position.y <= previousPosition.y + .025f)
                        {
                            AssignSurface(leaf, surface, true);
                            leaf.LandingRefreshed = true;
                        }
                        // A stale roof height or an exhausted spawn-ray budget
                        // cannot freeze a flying leaf. Continuous sweeps above
                        // decide collisions; keep falling until a real landing.
                    }
                    if (leaf.Position.y <= leaf.LandingPosition.y + 0.025f &&
                        leaf.LandingRefreshed)
                        SettleLeaf(leaf);
                }
            }
        }

        private static Matrix4x4 StableMatrix(Transform transform, Vector3 worldOffset)
        {
            var matrix = transform.localToWorldMatrix;
            matrix.m03 -= worldOffset.x;
            matrix.m13 -= worldOffset.y;
            matrix.m23 -= worldOffset.z;
            return matrix;
        }

        private void UpdateCollisionCars(Vector3 worldOffset, Vector3 cameraPosition)
        {
            for (int i = collisionCars.Count - 1; i >= 0; i--)
                if (collisionCars[i].Car == null ||
                    !collisionCars[i].Car.gameObject.activeInHierarchy) collisionCars.RemoveAt(i);
            for (int i = 0; i < surfaceCarCount; i++)
            {
                var car = surfaceCars[i].Car;
                if (car == null) continue;
                bool known = false;
                foreach (var entry in collisionCars) if (entry.Car == car) { known = true; break; }
                var pose = CollisionPose(car);
                if (!known) collisionCars.Add(new CollisionCar { Car = car, PoseTransform = pose,
                    CurrentLocalToWorld = StableMatrix(pose, worldOffset) });
            }
            for (int i = collisionCars.Count - 1; i >= 0; i--)
            {
                var entry = collisionCars[i];
                if ((entry.Car.transform.position - worldOffset - cameraPosition).sqrMagnitude > 40000f)
                { collisionCars.RemoveAt(i); continue; }
                var pose = CollisionPose(entry.Car);
                if (entry.PoseTransform != pose)
                {
                    entry.PoseTransform = pose;
                    entry.CurrentLocalToWorld = StableMatrix(pose, worldOffset);
                }
                entry.PreviousWorldToLocal = entry.CurrentLocalToWorld.inverse;
                var previous = entry.CurrentLocalToWorld;
                entry.CurrentLocalToWorld = StableMatrix(pose, worldOffset);
                entry.Moved = previous != entry.CurrentLocalToWorld;
                var b = entry.Car.Bounds;
                var currentCentre = entry.CurrentLocalToWorld.MultiplyPoint3x4(b.center);
                var previousCentre = previous.MultiplyPoint3x4(b.center);
                entry.SweptBounds = new Bounds(currentCentre, Vector3.one * (b.extents.magnitude * 2f + 1f));
                entry.SweptBounds.Encapsulate(new Bounds(previousCentre, entry.SweptBounds.size));
            }
        }

        private static Transform CollisionPose(TrainCar car)
        {
            return car.interior != null ? car.interior : car.transform;
        }

        private bool MoveWithCollisions(LeafBody leaf, Vector3 previous, Vector3 worldOffset)
        {
            var start = previous + worldOffset;
            var end = leaf.Position + worldOffset;
            RaycastHit hit;
            // Resting litter uses its local anchor and adds no physics queries.
            var radius = FlightRadius(leaf);
            var releasedFrom = leaf.ReleasedFromCar;
            leaf.ReleasedFromCar = null;
            // A moving train can cross a nearly stationary leaf. Test the old
            // point in the train's current frame as well as the leaf's own path.
            foreach (var car in collisionCars)
            {
                if (!car.Moved || (!car.SweptBounds.Contains(previous) && !car.SweptBounds.Contains(leaf.Position))) continue;
                // The attachment was already carried to this frame's pose
                // before release. Do not apply its carrier movement twice.
                if (releasedFrom != null && car.Car == releasedFrom) continue;
                var relativeStart = car.CurrentLocalToWorld.MultiplyPoint3x4(
                    car.PreviousWorldToLocal.MultiplyPoint3x4(previous)) + worldOffset;
                if (Sweep(relativeStart, end, radius, out hit))
                { Contact(leaf, hit, worldOffset, radius); return true; }
            }
            if (Sweep(start, end, radius, out hit))
            { Contact(leaf, hit, worldOffset, radius); return true; }
            return false;
        }

        private static float FlightRadius(LeafBody leaf)
        {
            // Enclose the whole curved mesh, including its rotating tips.
            return Mathf.Max(.035f, leaf.Size * 1.02f);
        }

        private static bool Sweep(Vector3 start, Vector3 end, float radius, out RaycastHit hit)
        {
            hit = default(RaycastHit);
            var delta = end - start;float length = delta.magnitude;
            if (length < .0001f) return false;
            bool swept = Physics.SphereCast(start, radius, delta / length, out hit, length,
                SeasonSurfaceLayers.Mask, QueryTriggerInteraction.Ignore);
            // SphereCast ignores a collider already intersecting the starting
            // sphere. A centre ray still catches a crossing from just outside
            // that surface (roof/wall corners when the train starts moving).
            RaycastHit centreHit;
            if (Physics.Raycast(start, delta / length, out centreHit, length,
                SeasonSurfaceLayers.Mask, QueryTriggerInteraction.Ignore) &&
                (!swept || centreHit.distance < hit.distance))
            { hit = centreHit; swept = true; }
            return swept;
        }

        private static void Contact(LeafBody leaf, RaycastHit hit, Vector3 worldOffset, float radius)
        {
            leaf.Position = hit.point - worldOffset + hit.normal * (radius + .01f);
            var car = TrainCar.Resolve(hit.collider.transform);
            var body = car != null ? car.rb : hit.rigidbody;
            var velocity = body != null ? body.GetPointVelocity(hit.point) : Vector3.zero;
            var relative = leaf.Velocity - velocity;
            leaf.Velocity = velocity + Vector3.ProjectOnPlane(relative, hit.normal) * .45f;
            if (hit.normal.y < .28f)
            {
                leaf.Velocity += hit.normal * .15f;
                leaf.LandingRefreshed = false;
                return;
            }
            if (car == null && body != null) car = TrainCar.Resolve(body.transform);
            if (car != null && car.carType == DV.ThingTypes.TrainCarType.HandCar) return;
            var surface = SurfaceFromHit(hit, car, worldOffset);
            AssignSurface(leaf, surface, true);leaf.LandingRefreshed = true;SettleLeaf(leaf);
        }

        private void LiftSettledLeavesByWind(float autumnWeight, Vector3 horizontalWind,
            float deltaTime)
        {
            if (leafCount == 0)
            {
                windLiftAccumulator = 0f;
                windLiftCursor = 0;
                return;
            }
            var rate = AutumnEffectsProfile.GetGroundWindLiftRate(autumnWeight,
                horizontalWind.magnitude);
            if (rate <= 0f)
            {
                windLiftAccumulator = 0f;
                return;
            }

            windLiftAccumulator = Mathf.Min(2.99f, windLiftAccumulator + rate * deltaTime);
            var requested = Mathf.Min(2, Mathf.FloorToInt(windLiftAccumulator));
            if (requested <= 0) return;

            var lifted = 0;
            var checkedLeaves = 0;
            var maximumChecks = Mathf.Min(48, leafCount);
            while (checkedLeaves < maximumChecks && lifted < requested)
            {
                if (windLiftCursor >= leafCount) windLiftCursor = 0;
                var leaf = leaves[windLiftCursor++];
                checkedLeaves++;
                if (!leaf.Settled || Time.realtimeSinceStartup < leaf.RestUntil) continue;
                LiftLeafByWind(leaf, horizontalWind, rate /
                    AutumnEffectsProfile.GroundWindLiftMaximumLeavesPerSecond);
                lifted++;
            }
            windLiftAccumulator -= lifted;
            if (lifted < requested) windLiftAccumulator = Mathf.Min(windLiftAccumulator, 0.99f);
        }

        private void LiftLeafByWind(LeafBody leaf, Vector3 horizontalWind, float strength)
        {
            var speed = horizontalWind.magnitude;
            if (speed <= 0.001f) return;
            strength = Mathf.Clamp01(strength);
            var direction = horizontalWind / speed;
            var side = Vector3.Cross(Vector3.up, direction) * Mathf.Lerp(-0.45f, 0.45f, Next01());
            var inheritedVelocity = ReleaseLeaf(leaf);
            leaf.Velocity = inheritedVelocity +
                direction * Mathf.Lerp(0.45f, 3.2f, strength) + side +
                Vector3.up * Mathf.Lerp(0.12f, 1.1f, strength);
            leaf.AngularVelocity = new Vector3(
                Mathf.Lerp(-360f, 360f, Next01()),
                Mathf.Lerp(-300f, 300f, Next01()),
                Mathf.Lerp(-460f, 460f, Next01()));
        }

        private bool FindStrongestWake(Vector3 position, float autumnWeight,
            out TrainWake strongest, out float strongestValue)
        {
            strongest = default(TrainWake);
            strongestValue = 0f;
            var found = false;
            for (var i = 0; i < trainWakeCount; i++)
            {
                var wake = trainWakes[i];
                var relative = position - wake.Centre;
                if (Mathf.Abs(relative.y) > 6.5f) continue;
                var longitudinal = Vector3.Dot(relative, wake.Direction);
                var lateral = Vector3.Dot(relative, wake.Right);
                var value = AutumnEffectsProfile.GetTrainWakeStrength(autumnWeight,
                    wake.SpeedMetresPerSecond * 3.6f, longitudinal, lateral,
                    wake.HalfLength, wake.HalfWidth);
                if (value <= strongestValue) continue;
                strongestValue = value;
                strongest = wake;
                found = true;
            }
            return found;
        }

        private void LiftLeaf(LeafBody leaf, TrainWake wake, float strength, Vector3 wind)
        {
            var relative = leaf.Position - wake.Centre;
            var sideSign = Mathf.Sign(Vector3.Dot(relative, wake.Right));
            if (Mathf.Abs(sideSign) < 0.5f) sideSign = Next01() < 0.5f ? -1f : 1f;
            var inheritedVelocity = ReleaseLeaf(leaf);
            leaf.Velocity = inheritedVelocity +
                wake.Direction * Mathf.Lerp(1.2f,
                    Mathf.Min(9f, wake.SpeedMetresPerSecond * 0.38f), strength) +
                wake.Right * sideSign * Mathf.Lerp(0.35f, 2.2f, strength) +
                Vector3.up * Mathf.Lerp(0.9f, 3.8f, strength) + wind * 0.12f;
            leaf.AngularVelocity = new Vector3(
                Mathf.Lerp(-420f, 420f, Next01()),
                Mathf.Lerp(-360f, 360f, Next01()),
                Mathf.Lerp(-540f, 540f, Next01()));
        }

        private static Vector3 ReleaseLeaf(LeafBody leaf)
        {
            var car = leaf.SurfaceTransform != null ? TrainCar.Resolve(leaf.SurfaceTransform) : null;
            var offset = DV.OriginShift.OriginShift.currentMove;
            var inherited = car != null && car.rb != null
                ? car.rb.GetPointVelocity(leaf.Position + offset) : leaf.SurfaceVelocity;
            leaf.Settled = false;
            leaf.Age = 0f;
            leaf.LandingRefreshed = false;
            var radius = FlightRadius(leaf);
            var departure = leaf.Position + leaf.GroundNormal * (radius + .012f);
            RaycastHit hit;
            if (Sweep(leaf.Position + offset, departure + offset, radius, out hit))
                departure = hit.point - offset + hit.normal * (radius + .01f);
            ClearSurfaceAnchor(leaf);
            leaf.ReleasedFromCar = car;
            leaf.Position = departure;
            return inherited;
        }

        private static void SettleLeaf(LeafBody leaf)
        {
            leaf.Position = leaf.LandingPosition + leaf.GroundNormal * 0.018f;
            leaf.Velocity = Vector3.zero;
            leaf.Rotation = GroundRotation(leaf.GroundNormal, leaf.Rotation.y);
            leaf.Settled = true;
            leaf.RestUntil = Time.realtimeSinceStartup + 0.65f;
            CaptureSurfaceRotation(leaf);
        }

        private bool TryFindSurface(Vector3 stableProbe, Vector3 worldOffset,
            out LeafSurface surface)
        {
            surface = new LeafSurface { Normal = Vector3.up };
            if (surfaceQueriesRemaining <= 0) return false;
            surfaceQueriesRemaining--;
            // Begin above roofs, even for low tree crowns/old landing heights.
            // The first obstruction is authoritative: skipping a steep roof
            // must not seed the interior floor beneath it.
            var worldProbe = stableProbe + worldOffset + Vector3.up * 32f;
            RaycastHit hit;
            if (Physics.Raycast(worldProbe, Vector3.down, out hit, 75f,
                SeasonSurfaceLayers.Mask, QueryTriggerInteraction.Ignore))
            {
                if (hit.normal.y < .28f) return false;
                var hitCar = TrainCar.Resolve(hit.collider.transform);
                if (hitCar == null && hit.rigidbody != null) hitCar = TrainCar.Resolve(hit.rigidbody.transform);
                if (hitCar != null && hitCar.carType == DV.ThingTypes.TrainCarType.HandCar) return false;
                surface = SurfaceFromHit(hit, hitCar, worldOffset);
                return true;
            }

            var terrains = Terrain.activeTerrains;
            if (terrains != null)
            {
                for (var i = 0; i < terrains.Length; i++)
                {
                    var terrain = terrains[i];
                    if (terrain == null || terrain.terrainData == null) continue;
                    var origin = terrain.transform.position;
                    var size = terrain.terrainData.size;
                    if (worldProbe.x < origin.x || worldProbe.x > origin.x + size.x ||
                        worldProbe.z < origin.z || worldProbe.z > origin.z + size.z) continue;
                    var point = new Vector3(worldProbe.x,
                        terrain.SampleHeight(worldProbe) + origin.y, worldProbe.z);
                    var normalizedX = Mathf.Clamp01((worldProbe.x - origin.x) / size.x);
                    var normalizedZ = Mathf.Clamp01((worldProbe.z - origin.z) / size.z);
                    surface.Position = point - worldOffset;
                    surface.Normal = terrain.terrainData.GetInterpolatedNormal(
                        normalizedX, normalizedZ);
                    return true;
                }
            }
            return false;
        }

        private static LeafSurface SurfaceFromHit(RaycastHit hit, TrainCar car, Vector3 worldOffset)
        {
            var surface = new LeafSurface { Position = hit.point - worldOffset,
                Normal = hit.normal.normalized,
                Anchor = car != null || hit.rigidbody != null ? hit.collider.transform : null };
            if (surface.Anchor != null)
            {
                surface.AnchorLocalPosition = surface.Anchor.InverseTransformPoint(hit.point);
                surface.AnchorLocalNormal = surface.Anchor.InverseTransformDirection(surface.Normal).normalized;
                var body = car != null ? car.rb : hit.rigidbody;
                surface.AnchorVelocity = body == null ? Vector3.zero :
                    Vector3.ClampMagnitude(body.GetPointVelocity(hit.point), 40f);
            }
            return surface;
        }

        private static void AssignSurface(LeafBody leaf, LeafSurface surface, bool attach)
        {
            leaf.LandingPosition = surface.Position;
            leaf.GroundNormal = surface.Normal;
            if (attach && surface.Anchor != null)
            {
                leaf.SurfaceTransform = surface.Anchor;
                leaf.SurfaceLocalPosition = surface.AnchorLocalPosition;
                leaf.SurfaceLocalNormal = surface.AnchorLocalNormal;
                leaf.SurfaceVelocity = surface.AnchorVelocity;
                leaf.SurfaceVelocitySamplePosition = surface.Position;
                leaf.SurfacePoseReady = true;
            }
            else
            {
                ClearSurfaceAnchor(leaf);
            }
        }

        private static void CaptureSurfaceRotation(LeafBody leaf)
        {
            if (leaf.SurfaceTransform == null) return;
            leaf.SurfaceLocalRotation = Quaternion.Inverse(leaf.SurfaceTransform.rotation) *
                Quaternion.Euler(leaf.Rotation);
        }

        private static void ClearSurfaceAnchor(LeafBody leaf)
        {
            leaf.SurfaceTransform = null;
            leaf.SurfaceLocalPosition = Vector3.zero;
            leaf.SurfaceLocalNormal = Vector3.up;
            leaf.SurfaceLocalRotation = Quaternion.identity;
            leaf.SurfaceVelocity = Vector3.zero;
            leaf.SurfaceVelocitySamplePosition = Vector3.zero;
            leaf.SurfacePoseReady = false;
            leaf.ReleasedFromCar = null;
        }

        private void UpdateAnchoredLeaves(Vector3 worldOffset, float deltaTime)
        {
            for (var i = 0; i < leafCount; i++)
            {
                var leaf = leaves[i];
                var anchor = leaf.SurfaceTransform;
                if (ReferenceEquals(anchor, null)) continue;
                if (anchor == null)
                {
                    var inheritedVelocity = leaf.SurfaceVelocity;
                    ClearSurfaceAnchor(leaf);
                    if (leaf.Settled)
                    {
                        leaf.Settled = false;
                        leaf.Age = 0f;
                        leaf.LandingRefreshed = false;
                        leaf.Velocity = inheritedVelocity + Vector3.down * 0.2f;
                        leaf.LandingPosition = leaf.Position + Vector3.down;
                        leaf.GroundNormal = Vector3.up;
                    }
                    continue;
                }

                var nextLandingPosition = anchor.TransformPoint(leaf.SurfaceLocalPosition) - worldOffset;
                if (deltaTime > 0.0001f)
                {
                    if (leaf.SurfacePoseReady)
                        leaf.SurfaceVelocity = Vector3.ClampMagnitude(
                            (nextLandingPosition - leaf.SurfaceVelocitySamplePosition) / deltaTime, 100f);
                    leaf.SurfaceVelocitySamplePosition = nextLandingPosition;
                    leaf.SurfacePoseReady = true;
                }
                leaf.LandingPosition = nextLandingPosition;
                leaf.GroundNormal = anchor.TransformDirection(leaf.SurfaceLocalNormal).normalized;
                if (!leaf.Settled) continue;
                leaf.Position = leaf.LandingPosition + leaf.GroundNormal * 0.018f;
                leaf.Rotation = (anchor.rotation * leaf.SurfaceLocalRotation).eulerAngles;
            }
        }

        private void PruneDistantLeaves(Vector3 cameraPosition)
        {
            var limit = (LeafVisibilityRadius + 15f) * (LeafVisibilityRadius + 15f);
            for (var i = leafCount - 1; i >= 0; i--)
                if (HorizontalSqrDistance(leaves[i].Position, cameraPosition) > limit)
                    RemoveLeafAt(i);
        }

        private void RenderLeaves(float weight, Vector3 cameraPosition, Vector3 worldOffset)
        {
            var rendered = 0;
            for (var i = 0; i < leafCount; i++)
            {
                var leaf = leaves[i];
                var distance = Mathf.Sqrt(HorizontalSqrDistance(leaf.Position, cameraPosition));
                var distanceAlpha = Mathf.Clamp01((LeafVisibilityRadius - distance) / 14f);
                if (distanceAlpha <= 0f) continue;
                var colour = leaf.Color;
                var ingressAlpha = leaf.HiddenWindIngress
                    ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(
                        (leaf.Age - HiddenIngressRevealDelay) /
                        HiddenIngressRevealDuration))
                    : 1f;
                colour.a = (byte)Mathf.RoundToInt(colour.a * weight *
                    distanceAlpha * ingressAlpha);
                particleBuffer[rendered] = new ParticleSystem.Particle
                {
                    position = leaf.Position + worldOffset,
                    rotation3D = leaf.Rotation,
                    startSize = leaf.Size,
                    startColor = colour,
                    randomSeed = leaf.AppearanceSeed,
                    startLifetime = 1000f,
                    remainingLifetime = 999f
                };
                rendered++;
            }
            particles.SetParticles(particleBuffer, rendered);
        }

        private readonly SeasonAssetBundleRepository repository;
        public AutumnLeafGroundController(SeasonAssetBundleRepository repository) { this.repository = repository; }

        private void EnsureRenderer()
        {
            if (particles != null) return;
            var shader = repository.LoadShader("AutumnLeaf");
            if (shader == null) return;
            owner = new GameObject("DVSeasons Physical Autumn Leaf Cover")
            { hideFlags = HideFlags.HideAndDontSave };
            particles = owner.AddComponent<ParticleSystem>();
            var main = particles.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            main.startRotation3D = true;
            main.maxParticles = leaves.Length;
            main.simulationSpeed = 0f;
            var emission = particles.emission;
            emission.enabled = false;
            var sheet = particles.textureSheetAnimation;
            sheet.enabled = true;sheet.numTilesX = 4;sheet.numTilesY = 4;
            sheet.animation = ParticleSystemAnimationType.WholeSheet;
            sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f);
            sheet.startFrame = new ParticleSystem.MinMaxCurve(0f, 1f);

            // The game's quality presets enable soft particles. Their depth fade
            // makes a leaf resting 18 mm above the surface almost transparent,
            // so use a vertex-coloured shader without soft-particle fading.
            texture = AutumnLeafParticleTexture.Create("DVSeasons Physical Autumn Leaf");
            material = new Material(shader)
            {
                name = "DVSeasons Physical Autumn Leaves",
                mainTexture = texture,
                hideFlags = HideFlags.HideAndDontSave
            };
            leafMesh = CreateLeafMesh();
            var renderer = owner.GetComponent<ParticleSystemRenderer>();
            renderer.material = material;
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            // Mesh mode alone still defaults to View alignment. That rotates a
            // resting leaf into/through its support as the camera moves around it.
            renderer.alignment = ParticleSystemRenderSpace.World;
            renderer.mesh = leafMesh;
            renderer.maxParticleSize = 0.5f;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            UpdateLighting();
            particles.Play(false);
            Camera.onPreCull += BeforeCameraCull;
            Debug.Log("[DVSeasons] Physical autumn leaf cover ready: real-tree sources, " +
                "configurable population, hidden strong-wind ingress and per-car wake volumes.");
        }

        private void UpdateLighting()
        {
            if (material == null) return;
            Color sky, equator, ground;
            if (RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Flat)
            {
                sky = equator = ground = LinearAmbient(RenderSettings.ambientLight);
            }
            else if (RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Trilight)
            {
                sky = LinearAmbient(RenderSettings.ambientSkyColor);
                equator = LinearAmbient(RenderSettings.ambientEquatorColor);
                ground = LinearAmbient(RenderSettings.ambientGroundColor);
            }
            else
            {
                // Particle renderers have no reliable local light probe here.
                // Read the weather-updated sky probe explicitly, without a fixed
                // brightness floor that would make leaves glow at night.
                var probe = RenderSettings.ambientProbe;
                probe.Evaluate(AmbientDirections, ambientColours);
                sky = ambientColours[0];
                equator = (ambientColours[1] + ambientColours[2] +
                    ambientColours[3] + ambientColours[4]) * .25f;
                ground = ambientColours[5];
                if (QualitySettings.activeColorSpace == ColorSpace.Gamma)
                { sky = sky.gamma; equator = equator.gamma; ground = ground.gamma; }
            }
            // Vectors are already in the shader's working colour space.
            material.SetVector("_LeafAmbientSky", sky);
            material.SetVector("_LeafAmbientEquator", equator);
            material.SetVector("_LeafAmbientGround", ground);
        }

        private static Color LinearAmbient(Color colour)
        {
            return QualitySettings.activeColorSpace == ColorSpace.Linear ? colour.linear : colour;
        }

        private static Mesh CreateLeafMesh()
        {
            var mesh = new Mesh
            {
                name = "DVSeasons curved autumn leaf",
                hideFlags = HideFlags.HideAndDontSave,
                vertices = new[]
                {
                    new Vector3(-0.55f, -0.85f, 0.010f), new Vector3(0f, -0.85f, 0.050f),
                    new Vector3(0.55f, -0.85f, 0.000f),
                    new Vector3(-0.55f, -0.425f, 0.020f), new Vector3(0f, -0.425f, 0.085f),
                    new Vector3(0.55f, -0.425f, 0.012f),
                    new Vector3(-0.55f, 0f, 0.015f), new Vector3(0f, 0f, 0.105f),
                    new Vector3(0.55f, 0f, 0.020f),
                    new Vector3(-0.55f, 0.425f, 0.025f), new Vector3(0f, 0.425f, 0.075f),
                    new Vector3(0.55f, 0.425f, 0.010f),
                    new Vector3(-0.55f, 0.85f, 0.005f), new Vector3(0f, 0.85f, 0.045f),
                    new Vector3(0.55f, 0.85f, 0.015f)
                },
                uv = new[]
                {
                    new Vector2(0f, 0f), new Vector2(0.5f, 0f), new Vector2(1f, 0f),
                    new Vector2(0f, 0.25f), new Vector2(0.5f, 0.25f), new Vector2(1f, 0.25f),
                    new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(1f, 0.5f),
                    new Vector2(0f, 0.75f), new Vector2(0.5f, 0.75f), new Vector2(1f, 0.75f),
                    new Vector2(0f, 1f), new Vector2(0.5f, 1f), new Vector2(1f, 1f)
                },
                triangles = new[]
                {
                    0, 1, 4, 0, 4, 3, 1, 2, 5, 1, 5, 4,
                    3, 4, 7, 3, 7, 6, 4, 5, 8, 4, 8, 7,
                    6, 7, 10, 6, 10, 9, 7, 8, 11, 7, 11, 10,
                    9, 10, 13, 9, 13, 12, 10, 11, 14, 10, 14, 13
                }
            };
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            // ParticleSystemRenderer reads the assigned mesh on the CPU when it
            // prepares mesh particles. UploadMeshData(true) discards that copy
            // and Unity then renders no leaves with "No mesh data available".
            // Keep this tiny, shared 15-vertex mesh readable for the lifetime of
            // the controller; the memory cost is negligible and deterministic.
            mesh.UploadMeshData(false);
            return mesh;
        }

        private static Vector3 GroundRotation(Vector3 normal, float yaw)
        {
            return (Quaternion.FromToRotation(Vector3.forward, normal) *
                Quaternion.AngleAxis(yaw, Vector3.forward)).eulerAngles;
        }

        private static bool TerrainNear(Terrain terrain, Vector3 point, float radius)
        {
            if (terrain == null || terrain.terrainData == null) return false;
            var origin = terrain.transform.position;
            var size = terrain.terrainData.size;
            var closestX = Mathf.Clamp(point.x, origin.x, origin.x + size.x);
            var closestZ = Mathf.Clamp(point.z, origin.z, origin.z + size.z);
            var x = point.x - closestX;
            var z = point.z - closestZ;
            return x * x + z * z <= radius * radius;
        }

        private static bool IsLeafBearingTree(TreePrototype[] prototypes, int prototypeIndex)
        {
            if (prototypes == null || prototypeIndex < 0 || prototypeIndex >= prototypes.Length ||
                prototypes[prototypeIndex] == null || prototypes[prototypeIndex].prefab == null)
                return false;
            var name = prototypes[prototypeIndex].prefab.name ?? string.Empty;
            if (ContainsAny(name, EvergreenNames))
                return false;
            return !ContainsAny(name, NonTreeNames);
        }

        private static bool ContainsAny(string value, params string[] candidates)
        {
            for (var i = 0; i < candidates.Length; i++)
                if (value.IndexOf(candidates[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static float ProjectedExtent(Transform transform, Vector3 extents, Vector3 direction)
        {
            return Mathf.Abs(Vector3.Dot(transform.TransformVector(Vector3.right * extents.x), direction)) +
                Mathf.Abs(Vector3.Dot(transform.TransformVector(Vector3.up * extents.y), direction)) +
                Mathf.Abs(Vector3.Dot(transform.TransformVector(Vector3.forward * extents.z), direction));
        }

        private static float HorizontalSqrDistance(Vector3 first, Vector3 second)
        {
            var x = first.x - second.x;
            var z = first.z - second.z;
            return x * x + z * z;
        }

        private int RandomIndex(int count)
        {
            return Mathf.Min(count - 1, Mathf.FloorToInt(Next01() * count));
        }

        private float Next01()
        {
            randomState = randomState * 1664525u + 1013904223u;
            return (randomState >> 8) * (1f / 16777216f);
        }

        private void RemoveLeafAt(int index)
        {
            var removed = leaves[index];
            leafCount--;
            leaves[index] = leaves[leafCount];
            leaves[leafCount] = removed;
            ClearSurfaceAnchor(removed);
        }

        private void ClearLeaves()
        {
            tickPending = false;
            pendingState = null;
            collisionCars.Clear();
            if (leafCount == 0 && sources.Count == 0 && trainWakeCount == 0 &&
                surfaceCarCount == 0)
            {
                fallingAccumulator = 0f;
                hiddenWindAccumulator = 0f;
                coverAccumulator = 0f;
                windLiftAccumulator = 0f;
                windLiftCursor = 0;
                hiddenWindSourceCount = 0;
                nextHiddenSourceScan = 0f;
                hiddenSourceCacheReady = false;
                return;
            }
            for (var i = 0; i < leafCount; i++) ClearSurfaceAnchor(leaves[i]);
            leafCount = 0;
            sources.Clear();
            sourceKeys.Clear();
            trainWakeCount = 0;
            surfaceCarCount = 0;
            fallingAccumulator = 0f;
            hiddenWindAccumulator = 0f;
            coverAccumulator = 0f;
            windLiftAccumulator = 0f;
            windLiftCursor = 0;
            nextTreeScan = 0f;
            nextTrainScan = 0f;
            hiddenWindSourceCount = 0;
            nextHiddenSourceScan = 0f;
            hiddenSourceCacheReady = false;
            if (particles != null) particles.SetParticles(particleBuffer, 0);
        }

        public void Dispose()
        {
            Camera.onPreCull -= BeforeCameraCull;
            ClearLeaves();
            if (treeSourceProvider != null) treeSourceProvider.Dispose();
            treeSourceProvider = null;
            vegetationStudioSnapshotVersion = -1;
            terrainRevision = -1;
            usingVegetationStudioSources = false;
            if (owner != null) UnityEngine.Object.Destroy(owner);
            if (material != null) UnityEngine.Object.Destroy(material);
            if (texture != null) UnityEngine.Object.Destroy(texture);
            if (leafMesh != null) UnityEngine.Object.Destroy(leafMesh);
            owner = null;
            particles = null;
            material = null;
            texture = null;
            leafMesh = null;
            // Release an expanded unlimited population when leaving the session.
            if (leaves.Length > InitialCapacity)
            {
                leaves = new LeafBody[InitialCapacity];
                particleBuffer = new ParticleSystem.Particle[InitialCapacity];
            }
        }
    }
}
