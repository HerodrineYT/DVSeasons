using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    // Actual overlapping pairs impose native-order dependencies. Independent
    // vehicles within the same connected component can share a batch.
    internal sealed class SnowVehicleDrawScheduler : IDisposable
    {
        private const int FullBatchCapacity = 128;
        private const int ExclusionBatchCapacity = 1023;
        private const int MaximumPlanRequests = 16384, MaximumPlanFullRequests = 8192, MaximumPlanStreams = 2048;
        private const int MaximumPlanDependencies = 131072;
        private static readonly int WorldToLocalId = Shader.PropertyToID("_DVPSVehicleWorldToLocal");
        private static readonly int IndexId = Shader.PropertyToID("_DVPSVehicleIndex");
        private static readonly int CutoffId = Shader.PropertyToID("_DVPSVehicleCutoff");
        private static readonly int AlbedoId = Shader.PropertyToID("_DVPSVehicleAlbedo");
        private static readonly int STId = Shader.PropertyToID("_DVPSVehicleST");
        private static readonly int InstanceIndexId = Shader.PropertyToID("_DVPSInstanceVehicleIndex");
        private static readonly int InstanceWorldToLocalId = Shader.PropertyToID("_DVPSInstanceWorldToLocal");

        internal struct DrawKey : IEquatable<DrawKey>
        {
            public Mesh Mesh;
            public Texture Albedo;
            public int MeshId, AlbedoId, Slot, Pass;
            public float Cutoff;
            public Vector4 ST;
            public bool CanInstance;

            public bool Equals(DrawKey other)
            {
                return MeshId == other.MeshId && AlbedoId == other.AlbedoId && Slot == other.Slot &&
                    Pass == other.Pass && Cutoff == other.Cutoff && ST.Equals(other.ST) && CanInstance == other.CanInstance;
            }
            public override bool Equals(object value) { return value is DrawKey && Equals((DrawKey)value); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = MeshId * 397 ^ AlbedoId;
                    hash = hash * 397 ^ Slot;
                    hash = hash * 397 ^ Pass;
                    hash = hash * 397 ^ Cutoff.GetHashCode();
                    hash = hash * 397 ^ ST.GetHashCode();
                    return hash * 397 ^ (CanInstance ? 1 : 0);
                }
            }
        }

        private sealed class Request
        {
            public DrawKey Key;
            public Renderer Renderer;
            public DrawMetadata Metadata;
            public int Variant;
            public Matrix4x4 ObjectToWorld, WorldToVehicle;
            public float VehicleIndex;
        }

        // A renderer/submesh's discovery metadata is immutable until the part
        // is rediscovered. Pass and instancing eligibility remain frame-local.
        internal sealed class DrawMetadata
        {
            internal readonly Renderer Renderer;
            internal readonly bool MeshOnly;
            private readonly DrawKey[] keys = new DrawKey[4];
            internal DrawMetadata(Renderer renderer, Mesh mesh, int slot, float cutoff, Texture albedo, Vector4 st,bool meshOnly=false)
            {
                if(renderer==null && !meshOnly)throw new ArgumentNullException(nameof(renderer));
                if(mesh==null)throw new ArgumentNullException(nameof(mesh));
                Renderer=renderer;MeshOnly=meshOnly;bool cutout=cutoff>0f;
                var key=new DrawKey {Mesh=mesh,MeshId=mesh.GetInstanceID(),Slot=slot,Cutoff=cutoff,
                    Albedo=cutout?albedo:null,AlbedoId=cutout&&albedo!=null?albedo.GetInstanceID():0,
                    ST=cutout?st:Vector4.zero};
                for(int variant=0;variant<4;variant++)
                {key.Pass=(variant&2)!=0?5:0;key.CanInstance=(variant&1)!=0;keys[variant]=key;}
            }
            internal DrawKey GetKey(int variant) { return keys[variant]; }
        }

        private sealed class VehicleStream
        {
            public Bounds Bounds;
            public SnowOrientedBounds OrientedBounds;
            public readonly List<Request> Requests = new List<Request>(32);
            public int Count, Next, Outstanding;
            public List<int> Successors = new List<int>(), BuildingSuccessors = new List<int>();
            public int PredecessorCount, RemainingPredecessors;
            public bool Activated, Completed;
        }

        private struct SweepInterval
        {
            public int Index;
            public float Minimum, Maximum;
            public float MinX, MaxX, MinY, MaxY, MinZ, MaxZ;

            public bool Intersects(ref SweepInterval other)
            {
                // Same inclusive comparisons/order as Unity 2019 Bounds.Intersects.
                // Avoid repeated Bounds.min/max Vector3 construction per pair.
                // Written positively so NaNs reject, just like the Unity API.
                return MinX<=other.MaxX && MaxX>=other.MinX &&
                    MinY<=other.MaxY && MaxY>=other.MinY && MinZ<=other.MaxZ && MaxZ>=other.MinZ;
            }
        }

        private sealed class SweepComparer : IComparer<SweepInterval>
        {
            public int Compare(SweepInterval a, SweepInterval b)
            {
                int order = a.Minimum.CompareTo(b.Minimum);
                return order != 0 ? order : a.Index.CompareTo(b.Index);
            }
        }
        private static readonly SweepComparer sweepComparer = new SweepComparer();

        private struct ReadyDraw
        {
            public VehicleStream Stream;
            public int Index;
        }

        private sealed class ReadyGroup
        {
            public DrawKey Key;
            public readonly Queue<ReadyDraw> Draws = new Queue<ReadyDraw>(8);
            public bool Queued;
        }

        private sealed class PlannedBatch
        {
            public DrawKey Key;
            public ReadyDraw[] Draws = new ReadyDraw[1];
            public int Count;
            public MaterialPropertyBlock Properties;
            public Matrix4x4[] ObjectMatrices, VehicleMatrices;
            public float[] VehicleIndices;
            public bool PropertiesReady;
        }

        private struct RequestSignature
        {
            public Renderer Renderer;
            public DrawKey Key;
            public DrawMetadata Metadata;
            public int Variant;
        }

        private sealed class CachedPlan
        {
            public readonly List<PlannedBatch> Batches = new List<PlannedBatch>();
            public readonly List<int> StreamCounts = new List<int>(), SuccessorCounts = new List<int>(), Successors = new List<int>();
            public readonly List<RequestSignature> Requests = new List<RequestSignature>();
            public int Count;
            public int UniqueKeyDraws;
            public long ReadySamples, ReadyTotal;
            public int ReadyMaximum;
            public int ActiveStreams, ActiveEdges, EmptyStreams, EmptyBridgeEdges;
            public bool Valid;
        }

        private readonly List<VehicleStream> streamPool = new List<VehicleStream>();
        private readonly List<ReadyGroup> groupPool = new List<ReadyGroup>();
        private readonly Dictionary<DrawKey, ReadyGroup> groups = new Dictionary<DrawKey, ReadyGroup>();
        private readonly Queue<ReadyGroup> readyGroups = new Queue<ReadyGroup>();
        private readonly Queue<VehicleStream> readyStreams = new Queue<VehicleStream>();
        private readonly Dictionary<DrawKey,int> eligibleKeys = new Dictionary<DrawKey,int>();
        private readonly ReadyDraw[] batchDraws = new ReadyDraw[ExclusionBatchCapacity];
        private readonly Matrix4x4[] fullMatrices = new Matrix4x4[FullBatchCapacity];
        private readonly Matrix4x4[] exclusionMatrices = new Matrix4x4[ExclusionBatchCapacity];
        private readonly Matrix4x4[] vehicleMatrices = new Matrix4x4[FullBatchCapacity];
        private readonly float[] vehicleIndices = new float[FullBatchCapacity];
        private readonly MaterialPropertyBlock fullProperties = new MaterialPropertyBlock();
        private readonly CachedPlan[] cachedPlans = {new CachedPlan(), new CachedPlan()};
        private CachedPlan activePlan;
        private List<PlannedBatch> plan { get { return activePlan.Batches; } }
        private List<int> plannedStreamCounts { get { return activePlan.StreamCounts; } }
        // A plan caches only grouping/order. Add still supplies the current
        // object matrices, captured vehicle frame and snow ID on every render.
        // Exact renderer/key sequences and counts validate it before reuse.
        private int planCount;
        private bool planValid, frameFlushed, recordingPlan, previousBoundsUnchanged;
        private int topologyCount = -1;
        private int[] componentParents = new int[32], componentSizes = new int[32];
        private int activeStreams, dependencyCount;
        private long uploadTicks;
        private SweepInterval[] sweepIntervals = new SweepInterval[32];
        private int streamCount, groupCount;
        private VehicleStream currentStream;
        private CommandBuffer buffer;
        private Material material;
        private bool stateKnown, matrixKnown, albedoKnown;
        private float recordedCutoff, recordedIndex;
        private Matrix4x4 recordedMatrix;
        private Texture recordedAlbedo;
        private Vector4 recordedST;

        public int CommandCount { get; private set; }
        public int FullDrawCount { get; private set; }
        public int ExclusionDrawCount { get; private set; }
        public int FullInstancedObjectCount { get; private set; }
        public int FullBatchCount { get; private set; }
        public int ExclusionInstancedObjectCount { get; private set; }
        public int ExclusionBatchCount { get; private set; }
        internal int PlanCacheHits { get; private set; }
        internal int PlanBuilds { get; private set; }
        internal int ComponentBuilds { get; private set; }
        internal long BoundsPairTests { get; private set; }
        internal long ActualOverlaps { get; private set; }
        internal long ComponentUnions { get; private set; }
        internal long OrientedPairTests { get; private set; }
        internal long OrientedPairsRejected { get; private set; }
        internal int ComponentCount { get; private set; }
        internal int LargestComponent { get; private set; }
        internal int ComponentVehicleCount { get; private set; }
        internal int MultiVehicleComponentCount { get; private set; }
        internal int UniqueKeyDraws { get; private set; }
        internal int FullMatrixUploads { get; private set; }
        internal int FullMatrixElementsUploaded { get; private set; }
        internal long ReadyStreamSamples {get;private set;}
        internal long ReadyStreamTotal {get;private set;}
        internal int ReadyStreamMaximum {get;private set;}
        internal int FullBatchMaximum {get;private set;}
        internal int ActiveStreamCount {get;private set;}
        internal int ActiveEdgeCount {get;private set;}
        internal int EmptyStreamCount {get;private set;}
        internal int EmptyBridgeEdgeCount {get;private set;}

        public void Begin(CommandBuffer commands, Material snowMaterial)
        {
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            if (snowMaterial == null) throw new ArgumentNullException(nameof(snowMaterial));
            ClearPending();
            buffer = commands;
            material = snowMaterial;
            if (activePlan == null) activePlan = cachedPlans[0];
            frameFlushed = false;
            previousBoundsUnchanged = topologyCount >= 0;
            CommandCount = FullDrawCount = ExclusionDrawCount = 0;
            FullInstancedObjectCount = FullBatchCount = ExclusionInstancedObjectCount = ExclusionBatchCount = 0;
            ReadyStreamSamples=ReadyStreamTotal=0;ReadyStreamMaximum=FullBatchMaximum=0;uploadTicks=0;
            ActiveStreamCount=ActiveEdgeCount=EmptyStreamCount=EmptyBridgeEdgeCount=0;
        }

        public void BeginVehicle(Bounds worldBounds)
        { BeginOrientedVehicle(worldBounds,default(SnowOrientedBounds)); }

        public void BeginOrientedVehicle(Bounds worldBounds,SnowOrientedBounds orientedBounds)
        {
            RequireActive();
            // Bounds include detached interiors and the shader's depth padding.
            // Collect the entire fleet before finding overlap components; one
            // conflicting pair must not flush unrelated vehicle streams.
            bool reuseBounds = previousBoundsUnchanged && streamCount < topologyCount &&
                streamCount < streamPool.Count && streamPool[streamCount].Bounds.Equals(worldBounds) &&
                streamPool[streamCount].OrientedBounds.Same(orientedBounds);
            if (!reuseBounds) previousBoundsUnchanged = false;
            if (streamCount == streamPool.Count) streamPool.Add(new VehicleStream());
            currentStream = streamPool[streamCount++];
            currentStream.Bounds = worldBounds;
            currentStream.OrientedBounds = orientedBounds;
            currentStream.Count = currentStream.Next = currentStream.Outstanding = 0;
        }

        public void Add(Renderer renderer, Mesh mesh, int slot, int pass, float cutoff, Texture albedo,
            Vector4 st, Matrix4x4 objectToWorld, Matrix4x4 worldToVehicle, float vehicleIndex, bool canInstance)
        {
            if (currentStream == null) throw new InvalidOperationException("BeginVehicle must precede snow draw requests.");
            if (pass != 0 && pass != 5) throw new ArgumentOutOfRangeException(nameof(pass));
            if (renderer == null || mesh == null) return;
            bool cutout = cutoff > 0f;
            int position = currentStream.Count++;
            if (position == currentStream.Requests.Count) currentStream.Requests.Add(new Request());
            var request = currentStream.Requests[position];
            request.Metadata=null;
            var oldKey = request.Key;
            var texture = cutout ? albedo : null;
            var uv = cutout ? st : Vector4.zero;
            if (!ReferenceEquals(request.Renderer, renderer) || !ReferenceEquals(oldKey.Mesh, mesh) ||
                oldKey.Slot != slot || oldKey.Pass != pass || oldKey.Cutoff != cutoff ||
                !ReferenceEquals(oldKey.Albedo, texture) || oldKey.ST.x!=uv.x || oldKey.ST.y!=uv.y ||
                oldKey.ST.z!=uv.z || oldKey.ST.w!=uv.w || oldKey.CanInstance != canInstance)
            {
                planValid = false;
                request.Key = new DrawKey
                {
                    Mesh = mesh, MeshId = mesh.GetInstanceID(), Slot = slot, Pass = pass, Cutoff = cutoff,
                    Albedo = texture, AlbedoId = cutout && albedo != null ? albedo.GetInstanceID() : 0,
                    ST = uv, CanInstance = canInstance
                };
                request.Renderer = renderer;
            }
            request.ObjectToWorld = objectToWorld;
            request.WorldToVehicle = worldToVehicle;
            request.VehicleIndex = vehicleIndex;
        }

        // The registry already validates the live renderer and mesh while
        // collecting FrameParts. Do not repeat Unity native null checks here.
        internal void AddPrepared(DrawMetadata metadata, int pass, ref Matrix4x4 objectToWorld,
            ref Matrix4x4 worldToVehicle, float vehicleIndex, bool canInstance)
        {
            if(currentStream==null)throw new InvalidOperationException("BeginVehicle must precede snow draw requests.");
            if(metadata==null)throw new ArgumentNullException(nameof(metadata));
            if(pass!=0&&pass!=5)throw new ArgumentOutOfRangeException(nameof(pass));
            int position=currentStream.Count++;
            if(position==currentStream.Requests.Count)currentStream.Requests.Add(new Request());
            var request=currentStream.Requests[position];
            int variant=(pass==5?2:0)|(canInstance?1:0);
            if(!ReferenceEquals(request.Metadata,metadata)||request.Variant!=variant)
            {
                var key=metadata.GetKey(variant);
                // Recreated metadata with the same native objects/key may still
                // use the old plan. Identity alone only selects the fast path.
                if(!ReferenceEquals(request.Renderer,metadata.Renderer)||
                    !ReferenceEquals(request.Key.Mesh,key.Mesh)||!ReferenceEquals(request.Key.Albedo,key.Albedo)||!request.Key.Equals(key))
                    planValid=false;
                request.Metadata=metadata;request.Variant=variant;
                request.Renderer=metadata.Renderer;request.Key=key;
            }
            request.ObjectToWorld=objectToWorld;request.WorldToVehicle=worldToVehicle;request.VehicleIndex=vehicleIndex;
        }

        public void End()
        {
            FlushCore(true);
            buffer = null;
            material = null;
        }

        public void Flush() { FlushCore(false); }

        private void FlushCore(bool endFrame)
        {
            long before=uploadTicks;
            using(SnowPerformance.Measure("snow-scheduler-plan-record"))FlushRecorded(endFrame);
            SnowPerformance.Elapsed("snow-full-batch-upload",uploadTicks-before);
        }

        private void FlushRecorded(bool endFrame)
        {
            if (streamCount == 0)
            {
                ComponentCount=LargestComponent=ComponentVehicleCount=MultiVehicleComponentCount=UniqueKeyDraws=0;
                topologyCount=-1;previousBoundsUnchanged=false;
                ReadyStreamSamples=ReadyStreamTotal=0;ReadyStreamMaximum=FullBatchMaximum=0;
                ActiveStreamCount=ActiveEdgeCount=EmptyStreamCount=EmptyBridgeEdgeCount=0;
                return;
            }
            RequireActive();
            // The caller can record other globals between groups. Never assume
            // that command-buffer global state survives an ordering barrier.
            stateKnown = matrixKnown = albedoKnown = false;
            recordedIndex = float.NaN;
            if (!endFrame) { frameFlushed = true; planValid = false; }
            bool cacheable = endFrame && !frameFlushed;
            PrepareComponents();
            if (cacheable && (MatchesPlan() || RestorePlan()))
            {
                UniqueKeyDraws=activePlan.UniqueKeyDraws;
                ReadyStreamSamples=activePlan.ReadySamples;ReadyStreamTotal=activePlan.ReadyTotal;ReadyStreamMaximum=activePlan.ReadyMaximum;
                ActiveStreamCount=activePlan.ActiveStreams;ActiveEdgeCount=activePlan.ActiveEdges;
                EmptyStreamCount=activePlan.EmptyStreams;EmptyBridgeEdgeCount=activePlan.EmptyBridgeEdges;
                for (int i = 0; i < planCount; i++)
                {
                    var batch = plan[i];
                    RecordBatch(batch.Key, batch.Count, batch.Draws, batch);
                }
                PlanCacheHits++;
                ClearPending();
                return;
            }
            recordingPlan = cacheable && WithinPlanBudget();
            CountUniqueEligibleKeys();
            planValid = false;
            if (recordingPlan)
            {
                // With two entries, the entry other than the most recently
                // selected one is exactly the LRU victim. Never retain more
                // than a bounded pair of camera/LOD draw patterns.
                activePlan = ReferenceEquals(activePlan, cachedPlans[0]) ? cachedPlans[1] : cachedPlans[0];
                ClearPlan(activePlan, false);
                planCount = 0;
            }
            activeStreams=0;
            PrepareActiveGraph();
            for(int i=0;i<streamCount;i++)
                if(streamPool[i].Count>0 && streamPool[i].RemainingPredecessors==0)readyStreams.Enqueue(streamPool[i]);
            DrainReadyStreams();
            while (readyGroups.Count != 0)
            {
                var group = readyGroups.Dequeue();
                group.Queued = false;
                // New heads released while consuming this group belong to its
                // next visit. Full draws from one stream never share a batch.
                int remaining = group.Draws.Count;
                while (remaining > 0)
                {
                    int capacity = !group.Key.CanInstance ? 1 :
                        group.Key.Pass == 0 ? FullBatchCapacity : ExclusionBatchCapacity;
                    int take = Math.Min(remaining, capacity);
                    int count = 0;
                    for (int i = 0; i < take; i++)
                    {
                        var ready = group.Draws.Dequeue();
                        var request = ready.Stream.Requests[ready.Index];
                        if ((request.Renderer == null && (request.Metadata==null || !request.Metadata.MeshOnly)) || request.Key.Mesh == null) Complete(ready);
                        else batchDraws[count++] = ready;
                    }
                    remaining -= take;
                    if (count == 0) {DrainReadyStreams();continue;}
                    ReadyStreamSamples++;ReadyStreamTotal+=activeStreams;
                    if(activeStreams>ReadyStreamMaximum)ReadyStreamMaximum=activeStreams;
                    var planned = recordingPlan ? RememberBatch(group.Key, count) : null;
                    RecordBatch(group.Key, count, batchDraws, planned);
                    // Completing an exclusion run may release a full draw, but
                    // only after all its exclusion writes were actually recorded.
                    for (int i = 0; i < count; i++)
                    {
                        var ready = batchDraws[i];
                        batchDraws[i] = default(ReadyDraw);
                        Complete(ready);
                    }
                    DrainReadyStreams();
                }
            }
            if (recordingPlan)
            {
                CapturePlanSignature();
                planValid = true;
                activePlan.Valid = true; activePlan.Count = planCount;
                activePlan.UniqueKeyDraws=UniqueKeyDraws;
                activePlan.ReadySamples=ReadyStreamSamples;activePlan.ReadyTotal=ReadyStreamTotal;activePlan.ReadyMaximum=ReadyStreamMaximum;
                activePlan.ActiveStreams=ActiveStreamCount;activePlan.ActiveEdges=ActiveEdgeCount;
                activePlan.EmptyStreams=EmptyStreamCount;activePlan.EmptyBridgeEdges=EmptyBridgeEdgeCount;
                PlanBuilds++;
                for (int i = planCount; i < plan.Count; i++) ClearBatch(plan[i]);
            }
            recordingPlan = false;
            ClearPending();
        }

        private void PrepareActiveGraph()
        {
            // Geometry and its direct edges stay camera-independent. A stream
            // without writes imposes no framebuffer ordering, including when
            // it would otherwise connect two non-overlapping active vehicles.
            ActiveStreamCount=ActiveEdgeCount=0;
            for(int i=0;i<streamCount;i++)
            {
                var stream=streamPool[i];stream.RemainingPredecessors=0;
                stream.Activated=stream.Completed=stream.Count==0;
                if(stream.Count>0)ActiveStreamCount++;
            }
            for(int i=0;i<streamCount;i++)
            {
                var stream=streamPool[i];if(stream.Count==0)continue;
                foreach(int index in stream.Successors)
                {
                    var next=streamPool[index];if(next.Count==0)continue;
                    next.RemainingPredecessors++;ActiveEdgeCount++;
                }
            }
            EmptyStreamCount=streamCount-ActiveStreamCount;
            // Counts every removed direct edge once, including empty->empty;
            // this is not a count of distinct transitive bridge paths.
            EmptyBridgeEdgeCount=dependencyCount-ActiveEdgeCount;
        }

        private void ExposeNextRun(VehicleStream stream)
        {
            if (stream.Outstanding != 0) return;
            if(stream.Next==stream.Count)
            {
                if(stream.Completed)return;
                stream.Completed=true;
                if(stream.Count>0)activeStreams--;
                foreach(int index in stream.Successors)
                {
                    var next=streamPool[index];
                    if(next.Count==0)continue;
                    if(--next.RemainingPredecessors==0)readyStreams.Enqueue(next);
                }
                return;
            }
            int end = stream.Next + 1;
            if (stream.Requests[stream.Next].Key.Pass == 5)
                while (end < stream.Count && stream.Requests[end].Key.Pass == 5) end++;
            stream.Outstanding = end - stream.Next;
            while (stream.Next < end)
            {
                int index = stream.Next++;
                var key = stream.Requests[index].Key;
                ReadyGroup group;
                if (!groups.TryGetValue(key, out group))
                {
                    if (groupCount == groupPool.Count) groupPool.Add(new ReadyGroup());
                    group = groupPool[groupCount++];
                    group.Key = key;
                    group.Draws.Clear();
                    group.Queued = false;
                    groups.Add(key, group);
                }
                group.Draws.Enqueue(new ReadyDraw { Stream = stream, Index = index });
                if (!group.Queued) { group.Queued = true; readyGroups.Enqueue(group); }
            }
        }

        private void Complete(ReadyDraw ready)
        {
            if (--ready.Stream.Outstanding == 0) ExposeNextRun(ready.Stream);
        }

        private void DrainReadyStreams()
        {
            // Release active successors iteratively and never activate twice.
            while(readyStreams.Count>0)
            {
                var stream=readyStreams.Dequeue();if(stream.Activated)continue;
                stream.Activated=true;if(stream.Count>0)activeStreams++;
                ExposeNextRun(stream);
            }
        }

        private void PrepareComponents()
        {
            if (previousBoundsUnchanged && topologyCount == streamCount) return;
            using(SnowPerformance.Measure("snow-scheduler-topology-build"))
                BuildComponents();
        }

        private void BuildComponents()
        {
            if (componentParents.Length < streamCount)
            {
                int capacity = Math.Max(streamCount, componentParents.Length * 2);
                Array.Resize(ref componentParents, capacity);
                Array.Resize(ref componentSizes, capacity);
                Array.Resize(ref sweepIntervals, capacity);
            }
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
            float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
            double totalExtentX = 0, totalExtentZ = 0;
            dependencyCount=0;
            for (int i = 0; i < streamCount; i++)
            {
                componentParents[i] = i;componentSizes[i]=0;
                streamPool[i].BuildingSuccessors.Clear();streamPool[i].PredecessorCount=0;
                var bounds = streamPool[i].Bounds;
                var center = bounds.center; var extents = bounds.extents;
                if (center.x < minX) minX = center.x; if (center.x > maxX) maxX = center.x;
                if (center.z < minZ) minZ = center.z; if (center.z > maxZ) maxZ = center.z;
                totalExtentX += extents.x; totalExtentZ += extents.z;
                sweepIntervals[i] = new SweepInterval {Index = i, MinX = center.x - extents.x,
                    MaxX = center.x + extents.x, MinY = center.y - extents.y, MaxY = center.y + extents.y,
                    MinZ = center.z - extents.z, MaxZ = center.z + extents.z};
            }
            // Normalize centre spread by object length: a row of long cars
            // should not choose its long axis merely because its bounds are
            // large. Only candidate count changes; exact 3D bounds decide every
            // connection. Reuse both interval storage and the comparer.
            bool sweepX = ((double)maxX - minX) * Math.Max(.000001d, totalExtentZ) >=
                ((double)maxZ - minZ) * Math.Max(.000001d, totalExtentX);
            for (int i = 0; i < streamCount; i++)
            {
                var interval = sweepIntervals[i];
                interval.Minimum = sweepX ? interval.MinX : interval.MinZ;
                interval.Maximum = sweepX ? interval.MaxX : interval.MaxZ;
                sweepIntervals[i] = interval;
            }
            SortIntervals();
            for (int i = 0; i < streamCount; i++)
            {
                var interval = sweepIntervals[i];
                for (int j = i + 1; j < streamCount; j++)
                {
                    var candidate = sweepIntervals[j];
                    // Equality is an overlap too: coupled/touching bounds must
                    // retain the same component and native predecessor order.
                    if (candidate.Minimum > interval.Maximum) break;
                    BoundsPairTests++;
                    if (interval.Intersects(ref candidate))
                    {
                        var first=streamPool[interval.Index].OrientedBounds;
                        var second=streamPool[candidate.Index].OrientedBounds;
                        if(first.Valid && second.Valid)
                        {
                            OrientedPairTests++;
                            if(!SnowOrientedBounds.Intersects(first,second)){OrientedPairsRejected++;continue;}
                        }
                        ActualOverlaps++;
                        int earlier=Math.Min(interval.Index,candidate.Index),later=Math.Max(interval.Index,candidate.Index);
                        streamPool[earlier].BuildingSuccessors.Add(later);streamPool[later].PredecessorCount++;dependencyCount++;
                        int a = ComponentRoot(interval.Index), b = ComponentRoot(candidate.Index);
                        if (a != b) { componentParents[a] = b; ComponentUnions++; }
                    }
                }
            }
            ComponentCount=LargestComponent=MultiVehicleComponentCount=0;
            ComponentVehicleCount=streamCount;
            for (int i = 0; i < streamCount; i++)
            {
                int component = ComponentRoot(i);
                componentSizes[component]++;
                var stream = streamPool[i];
                stream.BuildingSuccessors.Sort();
                if(!SameDependencies(stream.Successors,stream.BuildingSuccessors))planValid=false;
                var old=stream.Successors;stream.Successors=stream.BuildingSuccessors;stream.BuildingSuccessors=old;
            }
            for(int i=0;i<streamCount;i++)
            {
                int size=componentSizes[i];if(size==0)continue;
                ComponentCount++;if(size>LargestComponent)LargestComponent=size;
                if(size>1)MultiVehicleComponentCount++;
            }
            topologyCount = streamCount;
            previousBoundsUnchanged = true;
            ComponentBuilds++;
        }

        private void SortIntervals()
        {
            // Array.Sort's generic comparer adapter can allocate a delegate on
            // every call, even with a cached comparer. In-place heapsort has a
            // bounded O(N log N) worst case and no temporary storage or boxing.
            for (int root = streamCount / 2 - 1; root >= 0; root--) SiftInterval(root, streamCount);
            for (int end = streamCount - 1; end > 0; end--)
            {
                var swap = sweepIntervals[end]; sweepIntervals[end] = sweepIntervals[0]; sweepIntervals[0] = swap;
                SiftInterval(0, end);
            }
        }

        private static bool SameDependencies(List<int> a,List<int> b)
        {
            if(a.Count!=b.Count)return false;
            for(int i=0;i<a.Count;i++)if(a[i]!=b[i])return false;
            return true;
        }

        private void CountUniqueEligibleKeys()
        {
            eligibleKeys.Clear();UniqueKeyDraws=0;
            for(int i=0;i<streamCount;i++)
            {
                var stream=streamPool[i];
                for(int j=0;j<stream.Count;j++)
                {
                    var key=stream.Requests[j].Key;
                    if(key.Pass!=0 || !key.CanInstance)continue;
                    int count;eligibleKeys.TryGetValue(key,out count);eligibleKeys[key]=count+1;
                }
            }
            foreach(var pair in eligibleKeys)if(pair.Value==1)UniqueKeyDraws++;
            // Only the integer result is cached with a plan; avoid retaining
            // unloaded meshes/materials in the temporary diagnostic index.
            eligibleKeys.Clear();
        }

        private void SiftInterval(int root, int count)
        {
            var value = sweepIntervals[root];
            for (int child = root * 2 + 1; child < count; child = root * 2 + 1)
            {
                if (child + 1 < count && sweepComparer.Compare(sweepIntervals[child], sweepIntervals[child + 1]) < 0) child++;
                if (sweepComparer.Compare(value, sweepIntervals[child]) >= 0) break;
                sweepIntervals[root] = sweepIntervals[child]; root = child;
            }
            sweepIntervals[root] = value;
        }

        private int ComponentRoot(int index)
        {
            while (componentParents[index] != index)
            {
                componentParents[index] = componentParents[componentParents[index]];
                index = componentParents[index];
            }
            return index;
        }

        private bool MatchesPlan()
        {
            if (!planValid || !activePlan.Valid || plannedStreamCounts.Count != streamCount) return false;
            for (int i = 0; i < streamCount; i++)
                if (plannedStreamCounts[i] != streamPool[i].Count) return false;
            return true;
        }

        private bool RestorePlan()
        {
            foreach (var candidate in cachedPlans)
            {
                if (!candidate.Valid || candidate.StreamCounts.Count != streamCount) continue;
                bool matches = true;
                int position = 0,dependencyPosition=0;
                for (int i = 0; matches && i < streamCount; i++)
                {
                    var stream = streamPool[i];
                    if (candidate.StreamCounts[i] != stream.Count || candidate.SuccessorCounts[i] != stream.Successors.Count)
                    { matches = false; break; }
                    for(int edge=0;edge<stream.Successors.Count;edge++)
                        if(candidate.Successors[dependencyPosition++]!=stream.Successors[edge]){matches=false;break;}
                    if(!matches)break;
                    for (int requestIndex = 0; requestIndex < stream.Count; requestIndex++)
                    {
                        var request = stream.Requests[requestIndex];
                        var signature = candidate.Requests[position++];
                        if(request.Metadata!=null && ReferenceEquals(signature.Metadata,request.Metadata) && signature.Variant==request.Variant)continue;
                        // Instance IDs alone are not object identity after a
                        // Unity object has been destroyed and its ID reused.
                        if (!ReferenceEquals(signature.Renderer, request.Renderer) ||
                            !ReferenceEquals(signature.Key.Mesh, request.Key.Mesh) ||
                            !ReferenceEquals(signature.Key.Albedo, request.Key.Albedo) || !signature.Key.Equals(request.Key))
                        { matches = false; break; }
                    }
                }
                if (!matches || position != candidate.Requests.Count || dependencyPosition!=candidate.Successors.Count) continue;
                activePlan = candidate; planCount = candidate.Count; planValid = true;
                return true;
            }
            return false;
        }

        private bool WithinPlanBudget()
        {
            if (streamCount > MaximumPlanStreams || dependencyCount>MaximumPlanDependencies) return false;
            int requests = 0, full = 0;
            for (int i = 0; i < streamCount; i++)
            {
                var stream = streamPool[i]; requests += stream.Count;
                if (requests > MaximumPlanRequests) return false;
                for (int j = 0; j < stream.Count; j++) if (stream.Requests[j].Key.Pass == 0) full++;
                if (full > MaximumPlanFullRequests) return false;
            }
            return true;
        }

        private void CapturePlanSignature()
        {
            for (int i = 0; i < streamCount; i++)
            {
                var stream = streamPool[i];
                activePlan.StreamCounts.Add(stream.Count);activePlan.SuccessorCounts.Add(stream.Successors.Count);
                activePlan.Successors.AddRange(stream.Successors);
                for (int j = 0; j < stream.Count; j++)
                {
                    var request = stream.Requests[j];
                    activePlan.Requests.Add(new RequestSignature {Renderer = request.Renderer, Key = request.Key,
                        Metadata=request.Metadata,Variant=request.Variant});
                }
            }
        }

        private static void ClearBatch(PlannedBatch batch)
        {
            batch.Key = default(DrawKey); batch.Draws = Array.Empty<ReadyDraw>(); batch.Count = 0;
            // Release old exact-sized matrix blocks instead of keeping peak
            // arrays in inactive batches across many different camera patterns.
            ReleaseMatrices(batch);
        }

        private static void ReleaseMatrices(PlannedBatch batch)
        {
            batch.Properties = null; batch.ObjectMatrices = batch.VehicleMatrices = null;
            batch.VehicleIndices = null; batch.PropertiesReady = false;
        }

        private static void ClearPlan(CachedPlan cached, bool releaseBatches)
        {
            cached.Valid = false; cached.Count = 0;
            cached.StreamCounts.Clear();cached.SuccessorCounts.Clear();cached.Successors.Clear();cached.Requests.Clear();
            cached.ReadySamples=cached.ReadyTotal=0;cached.ReadyMaximum=0;
            cached.ActiveStreams=cached.ActiveEdges=cached.EmptyStreams=cached.EmptyBridgeEdges=0;
            foreach (var batch in cached.Batches)
            {
                batch.Key = default(DrawKey); Array.Clear(batch.Draws, 0, batch.Draws.Length);
                if (releaseBatches) ClearBatch(batch);
            }
            if (releaseBatches) cached.Batches.Clear();
        }

        private PlannedBatch RememberBatch(DrawKey key, int count)
        {
            if (planCount == plan.Count) plan.Add(new PlannedBatch());
            var batch = plan[planCount++];
            batch.Key = key;
            if (key.Pass != 0 || count < 2 || !key.CanInstance) ReleaseMatrices(batch);
            if (batch.Draws.Length != count) batch.Draws = new ReadyDraw[count];
            Array.Copy(batchDraws, batch.Draws, count);
            batch.Count = count;
            return batch;
        }

        private void RecordBatch(DrawKey key, int count, ReadyDraw[] draws, PlannedBatch planned)
        {
            SetCutout(key);
            if (count == 1 || !key.CanInstance)
            {
                var ready = draws[0];
                var request = ready.Stream.Requests[ready.Index];
                if (key.Pass == 0)
                {
                    if (!matrixKnown || !SameMatrix(ref recordedMatrix,ref request.WorldToVehicle))
                    {
                        buffer.SetGlobalMatrix(WorldToLocalId, request.WorldToVehicle);
                        recordedMatrix = request.WorldToVehicle;
                        matrixKnown = true;
                    }
                    if (recordedIndex != request.VehicleIndex)
                    { buffer.SetGlobalFloat(IndexId, request.VehicleIndex); recordedIndex = request.VehicleIndex; }
                }
                if(request.Metadata!=null && request.Metadata.MeshOnly)
                    buffer.DrawMesh(key.Mesh,request.ObjectToWorld,material,key.Slot,key.Pass);
                else buffer.DrawRenderer(request.Renderer, material, key.Slot, key.Pass);
            }
            else
            {
                // Unity copies the complete MaterialPropertyBlock arrays, not
                // just the active instance count. Keep exact-size blocks with
                // each cached batch; a stationary train needs no array uploads.
                bool cachedFull = key.Pass == 0 && planned != null;
                if (cachedFull && (planned.ObjectMatrices == null || planned.ObjectMatrices.Length != count))
                {
                    planned.ObjectMatrices = new Matrix4x4[count];
                    planned.VehicleMatrices = new Matrix4x4[count];
                    planned.VehicleIndices = new float[count];
                    planned.Properties = new MaterialPropertyBlock();
                    planned.PropertiesReady = false;
                }
                var matrices = key.Pass == 0 ? (cachedFull ? planned.ObjectMatrices : fullMatrices) : exclusionMatrices;
                var localMatrices = cachedFull ? planned.VehicleMatrices : vehicleMatrices;
                var indices = cachedFull ? planned.VehicleIndices : vehicleIndices;
                bool upload = !cachedFull || !planned.PropertiesReady;
                for (int i = 0; i < count; i++)
                {
                    var ready = draws[i];
                    var request = ready.Stream.Requests[ready.Index];
                    matrices[i] = request.ObjectToWorld;
                    if (key.Pass == 0)
                    {
                        upload |= !SameMatrix(ref localMatrices[i],ref request.WorldToVehicle) || indices[i] != request.VehicleIndex;
                        localMatrices[i] = request.WorldToVehicle;
                        indices[i] = request.VehicleIndex;
                    }
                }
                if (key.Pass == 0)
                {
                    var properties = cachedFull ? planned.Properties : fullProperties;
                    if (upload)
                    {
                        long uploadStart=System.Diagnostics.Stopwatch.GetTimestamp();
                        properties.SetFloatArray(InstanceIndexId, indices);
                        properties.SetMatrixArray(InstanceWorldToLocalId, localMatrices);
                        uploadTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-uploadStart;
                        if (cachedFull) planned.PropertiesReady = true;
                        FullMatrixUploads++;
                        FullMatrixElementsUploaded += localMatrices.Length;
                    }
                    buffer.DrawMeshInstanced(key.Mesh, key.Slot, material, 6, matrices, count, properties);
                    FullInstancedObjectCount += count;
                    FullBatchCount++;
                    if(count>FullBatchMaximum)FullBatchMaximum=count;
                }
                else
                {
                    buffer.DrawMeshInstanced(key.Mesh, key.Slot, material, 5, matrices, count);
                    ExclusionInstancedObjectCount += count;
                    ExclusionBatchCount++;
                }
            }
            CommandCount++;
            if (key.Pass == 0) FullDrawCount += count;
            else ExclusionDrawCount += count;
        }

        private void SetCutout(DrawKey key)
        {
            if (!stateKnown || recordedCutoff != key.Cutoff)
            { buffer.SetGlobalFloat(CutoffId, key.Cutoff); recordedCutoff = key.Cutoff; stateKnown = true; }
            if (key.Cutoff <= 0f) return;
            var texture = key.Albedo != null ? key.Albedo : Texture2D.whiteTexture;
            if (!albedoKnown || recordedAlbedo != texture)
            { buffer.SetGlobalTexture(AlbedoId, texture); recordedAlbedo = texture; }
            if (!albedoKnown || !recordedST.Equals(key.ST))
            { buffer.SetGlobalVector(STId, key.ST); recordedST = key.ST; }
            albedoKnown = true;
        }

        private void ClearPending()
        {
            if (streamCount > 0)
                for (int i = streamCount; i < streamPool.Count; i++)
                {
                    var unused = streamPool[i];
                    foreach (var request in unused.Requests)
                    { request.Renderer = null; request.Key = default(DrawKey); request.Metadata=null; }
                    unused.Count = unused.Next = unused.Outstanding = 0;
                }
            for (int i = 0; i < streamCount; i++)
            {
                var stream = streamPool[i];
                for (int j = stream.Count; j < stream.Requests.Count; j++)
                { stream.Requests[j].Renderer = null; stream.Requests[j].Key = default(DrawKey); stream.Requests[j].Metadata=null; }
                stream.Count = stream.Next = stream.Outstanding = 0;
            }
            for (int i = 0; i < groupCount; i++)
            {
                groupPool[i].Draws.Clear();
                groupPool[i].Key = default(DrawKey);
                groupPool[i].Queued = false;
            }
            groups.Clear();
            readyGroups.Clear();
            readyStreams.Clear();activeStreams=0;
            currentStream = null;
            streamCount = groupCount = 0;
            recordedAlbedo = null;
        }

        private void RequireActive()
        { if (buffer == null || material == null) throw new InvalidOperationException("Begin must precede snow scheduling."); }

        // Matrix4x4.Equals in Unity 2019 copies eight Vector4 columns. These
        // exact scalar comparisons avoid that hot-path work without a tolerance
        // that could leave moving snow in an outdated coordinate frame.
        internal static bool SameMatrix(ref Matrix4x4 a,ref Matrix4x4 b)
        {
            return a.m00==b.m00 && a.m01==b.m01 && a.m02==b.m02 && a.m03==b.m03 &&
                a.m10==b.m10 && a.m11==b.m11 && a.m12==b.m12 && a.m13==b.m13 &&
                a.m20==b.m20 && a.m21==b.m21 && a.m22==b.m22 && a.m23==b.m23 &&
                a.m30==b.m30 && a.m31==b.m31 && a.m32==b.m32 && a.m33==b.m33;
        }

        public void Dispose()
        {
            ClearPending();
            streamPool.Clear();
            groupPool.Clear();
            foreach (var cached in cachedPlans) ClearPlan(cached, true);
            activePlan = null;
            planCount = 0;
            planValid = frameFlushed = recordingPlan = previousBoundsUnchanged = false;
            topologyCount = -1;
            PlanCacheHits = PlanBuilds = ComponentBuilds = FullMatrixUploads = FullMatrixElementsUploaded = 0;
            BoundsPairTests = ActualOverlaps = ComponentUnions = 0;
            OrientedPairTests=OrientedPairsRejected=0;
            ComponentCount=LargestComponent=ComponentVehicleCount=MultiVehicleComponentCount=UniqueKeyDraws=0;
            ActiveStreamCount=ActiveEdgeCount=EmptyStreamCount=EmptyBridgeEdgeCount=0;
            ReadyStreamSamples=ReadyStreamTotal=0;ReadyStreamMaximum=FullBatchMaximum=0;uploadTicks=0;
            eligibleKeys.Clear();
            Array.Clear(batchDraws, 0, batchDraws.Length);
            fullProperties.Clear();
            buffer = null;
            material = null;
        }
    }
}
