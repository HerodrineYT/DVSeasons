using System;
using System.Collections.Generic;
using System.Reflection;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    // Only the game discovery boundary uses reflection; rendering also runs in the
    // standalone Unity regression scene without loading Assembly-CSharp from DV.
    internal sealed class SnowVehicleRegistry : IDisposable
    {
        internal sealed class LodSet
        {
            public LODGroup Group;
            public LOD[] Levels;
            public int Current;
            public bool FilterInterior;
        }
        private sealed class StaticVehicle
        {
            public Transform Root, Interior, InteriorLod;
        }
        private struct DiscoveryCandidate
        {
            public Component Source;
            public Transform Root, Interior, InteriorLod;
            public int Id;
            public float Distance;
        }
        private sealed class InteriorFields
        {
            public FieldInfo Interior, InteriorLod;
        }
        private static readonly Dictionary<Type, InteriorFields> interiorFields = new Dictionary<Type, InteriorFields>();
        private static readonly Comparison<DiscoveryCandidate> nearestCandidate = (a,b) => a.Distance.CompareTo(b.Distance);
        internal sealed class Part
        {
            public Renderer Renderer;
            public Material[] Materials;
            public Mesh Mesh;
            public MeshFilter Filter;
            public bool Interior;
            public LodSet Lod;
            public int LodIndex;
            public int LodMask;
            public bool UsesLod(int index) { return index>=0 && index<32 && (LodMask&(1<<index))!=0; }
            public bool[] Opaque;
            public float[] Cutoff;
            public Texture[] Albedo;
            public Vector4[] ST;
            public SnowMovingVehiclePartFrame MovingFrame;
            public bool HasOpaque;
            public bool NativeComplete;
            public bool[] NativeSlots;
            internal SnowVehicleDrawScheduler.DrawMetadata[] DrawMetadata;
            internal Bounds DrawEnvelope;
            internal bool DrawEnvelopeKnown;
            internal Bounds DrawStreamBounds;
            internal bool DrawStreamBoundsKnown;
            internal Bounds VisibleBounds;
            internal ExclusionBatch[] ExclusionBatches;
            internal int ExclusionFrame;
            internal bool ExclusionAllowed, ExclusionMatrixKnown, ExclusionPositive;
            internal Matrix4x4 ExclusionMatrix;
            internal int ObjectMatrixFrame;
            internal Matrix4x4 ObjectMatrix;
            internal int InstanceReason;
            internal int MeshValidationFrame;
            internal bool MeshCurrent;
            internal int GeometryValidationFrame, GeometryReason;
            internal bool OrientedChecked;
            internal int OrientedRevision;
            internal Bounds OrientedSource;
            internal Matrix4x4 OrientedMatrix;
            internal float OrientedPadding;
            internal bool OrientedGuardRejected;
            internal int OrientedGuardRevision;
            internal float OrientedGuardPadding;
        }
        internal struct ExclusionKey : IEquatable<ExclusionKey>
        {
            public Mesh Mesh;
            public int Submesh;
            public float Cutoff;
            public Texture Albedo;
            public Vector4 ST;
            public bool Equals(ExclusionKey other)
            { return Mesh == other.Mesh && Submesh == other.Submesh && Cutoff == other.Cutoff && Albedo == other.Albedo && ST.Equals(other.ST); }
            public override bool Equals(object other) { return other is ExclusionKey && Equals((ExclusionKey)other); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Mesh.GetInstanceID() * 397 ^ Submesh;
                    hash = hash * 397 ^ Cutoff.GetHashCode();
                    hash = hash * 397 ^ (Albedo != null ? Albedo.GetInstanceID() : 0);
                    return hash * 397 ^ ST.GetHashCode();
                }
            }
        }
        internal sealed class ExclusionBatch
        {
            public ExclusionKey Key;
            public Matrix4x4[] Matrices = new Matrix4x4[32];
            public Renderer FirstRenderer;
            public int Count;
            public bool Active;
        }
        internal sealed class Vehicle
        {
            public Transform Root;
            public Transform Interior;
            public Transform InteriorLod;
            public Transform Cargo;
            public Transform External, DummyExternal;
            public readonly List<LodSet> Lods=new List<LodSet>();
            public object CargoController;
            public MethodInfo CargoGetter;
            public float NextCargoCheck;
            public readonly List<Part> Parts = new List<Part>();
            public readonly List<Part> FrameParts = new List<Part>();
            public readonly SnowVehiclePartCache PartCache = new SnowVehiclePartCache();
            public readonly SnowVehicleMeshCache MeshCache = new SnowVehicleMeshCache();
            public bool NativeComplete;
            public Bounds FrameBounds;
            public readonly SnowVehicleTopologyBounds Topology = new SnowVehicleTopologyBounds();
            public readonly SnowVehicleOrientedEnvelope Oriented = new SnowVehicleOrientedEnvelope();
            public readonly List<Renderer> CaptureRenderers = new List<Renderer>();
            public int Slot;
            public bool Dirty = true;
            public bool Ready;
            public bool SnowReady;
            public bool SnowShapeDirty;
            public Vector4 SnowArea;
            public float LastSnowClock;
            public bool FrameVisible;
            public bool FrameSnow;
            public bool FrameVolume;
            public bool FrameDistant;
            public bool HasIndependentSnowTexture;
            public int RootId;
            public Vector4 Area;
            public Vector4 Vertical;
            public Bounds LocalBounds;
            public int Signature;
            public Component Source;
            public Action<GameObject> InteriorLoadedHandler;
            public bool RollingStock;
            public bool PartsPending;
            public bool InteriorPending;
            public bool PartsExploded;
        }
        // Metadata has a fixed shader ceiling; GPU slices grow only for actual
        // loaded cars. Camera movement never evicts a car's accumulated snow.
        private const int Capacity = 1023; // Unity 2019's maximum shader property array length.
        private int textureCapacity;
        public Action StaticGeometryChanged;
        private readonly List<Vehicle> vehicles = new List<Vehicle>();
        private readonly Dictionary<int, Vehicle> byId = new Dictionary<int, Vehicle>();
        private readonly Dictionary<int,StaticVehicle> staticVehicles=new Dictionary<int,StaticVehicle>();
        private readonly List<int> departed=new List<int>();
        private readonly List<DiscoveryCandidate> discoveryCars = new List<DiscoveryCandidate>();
        private readonly List<DiscoveryCandidate> discoveryMoving = new List<DiscoveryCandidate>();
        private readonly HashSet<int> discoveryAllLive = new HashSet<int>();
        private readonly HashSet<int> discoveryLive = new HashSet<int>();
        private struct PartLodMembership { public LodSet Set; public int Mask; }
        private readonly List<Transform> partRootsScratch = new List<Transform>(6);
        private readonly List<Part> partsScratch = new List<Part>();
        private readonly List<Renderer> partRenderersScratch = new List<Renderer>();
        private readonly List<LODGroup> partGroupsScratch = new List<LODGroup>();
        private readonly HashSet<int> partSeenScratch = new HashSet<int>(), partGroupsSeenScratch = new HashSet<int>();
        private readonly Dictionary<int,PartLodMembership> partLodsScratch = new Dictionary<int,PartLodMembership>();
        internal int LastPartsHierarchyQueries { get; private set; }
        private readonly bool[] usedSlots=new bool[Capacity];
        public int FrameDrawCount { get; private set; }
        public int FrameSnowDrawCount { get; private set; }
        public int FrameExclusionDrawCount { get; private set; }
        public int FrameExclusionBatchCount { get; private set; }
        public int FrameInstancedExclusionCount { get; private set; }
        public int FrameFullBatchCount { get; private set; }
        public int FrameInstancedFullCount { get; private set; }
        internal int FrameOrientedFastCount { get; private set; }
        internal int FrameOrientedDetailedCount { get; private set; }
        internal int FrameOrientedMeshBoundsReadCount { get; private set; }
        private readonly Dictionary<Mesh,Bounds> orientedMeshBounds = new Dictionary<Mesh,Bounds>();
        private readonly SnowVehicleDrawScheduler vehicleDraws = new SnowVehicleDrawScheduler();
        private readonly SnowVehicleDrawScheduler partDraws = new SnowVehicleDrawScheduler();
        private SnowVehicleDrawScheduler orderedDraws;
        public SnowVehicleRegistry() {orderedDraws=vehicleDraws;}
        private readonly List<Part> stablePartLayout = new List<Part>();
        private int stablePartSubmissions,lastVehicleCommands,lastVehicleRequests;
        private bool rejectedPartLayout;
        private int eligibleFull,uniqueMeshFull,skinnedFull,staticFull,mirroredFull,streamsFull,otherFull;
        private readonly List<Vehicle> frameVehicles = new List<Vehicle>();
        internal bool OrderedVehicleBatchesEnabled = true;
        internal bool PartVehicleBatchesEnabled = true;
        internal bool PreparedVehicleRequestsEnabled = true;
        internal bool PartCacheEnabled = true;
        internal bool NativeMaterialsEnabled = true;
        internal bool VehicleSnowEnabled = true;
        internal bool IsSnowSelected(Vehicle vehicle)
        {
            return (!vehicle.RollingStock || VehicleSnowEnabled) && ObjectLimiter.IsSelected(vehicle.Root);
        }
        internal bool CombinedMeshesEnabled = true;
        private readonly SnowVehicleNativeMaterials nativeMaterials = new SnowVehicleNativeMaterials();
        public int NativeMaterialSlots => nativeMaterials.BoundSlots;
        internal bool ExclusionVolumesEnabled = true;
        private readonly SnowVehicleExclusionVolumes exclusionVolumes = new SnowVehicleExclusionVolumes();
        private readonly SnowVehicleDistantSurfaces distantSurfaces = new SnowVehicleDistantSurfaces();
        internal bool DistantSurfacesEnabled = true;
        public int FrameDistantCars => distantSurfaces.Cars;
        public int FrameDistantCommands => distantSurfaces.Commands;
        public int FrameCombinedCommands {get;private set;}
        internal int FrameExclusionVolumeCount => exclusionVolumes.Count;
        internal bool FrameUsesPartBatches {get;private set;}
        private const int MaximumPartStreams=2048;
        internal bool OrientedVehicleBatchesEnabled = true;
        private bool useOrderedDraws;
        private Matrix4x4 currentWorldToVehicle;
        // Reuse storage after the first frame. A full surface-data draw is an
        // ordering barrier: only exclusion draws (identical alpha writes) may
        // be regrouped. This preserves coincident body/interior silhouettes.
        private readonly Dictionary<ExclusionKey, ExclusionBatch> exclusionByKey = new Dictionary<ExclusionKey, ExclusionBatch>();
        private readonly List<ExclusionBatch> exclusionPool = new List<ExclusionBatch>();
        private readonly List<ExclusionBatch> activeExclusions = new List<ExclusionBatch>();
        private readonly Dictionary<Mesh, int> exclusionMeshUses = new Dictionary<Mesh, int>();
        private bool exclusionMeshUsesDirty = true;
        private bool hasSharedSnowMeshes;
        private int exclusionBatchCount;
        private int exclusionFrame;
        public int ExclusionCacheBuildCount { get; private set; }
        internal bool ExclusionInstancingEnabled = true;
        private readonly List<Renderer> hidden = new List<Renderer>();
        private readonly List<Renderer> captureScratch = new List<Renderer>();
        private readonly Plane[] frustum = new Plane[6];
        private readonly Plane[] rightFrustum = new Plane[6];
        private readonly JunctionSnowSource junctions = new JunctionSnowSource();
        public readonly SnowObjectLimiter ObjectLimiter = new SnowObjectLimiter();
        private readonly Vector4[] areas = new Vector4[Capacity];
        private readonly Vector4[] snowAreas = new Vector4[Capacity];
        private readonly float[] snowRemaining = new float[Capacity];
        private readonly Vector4[] sideSnowAmount = new Vector4[Capacity];
        private readonly Vector4[] vehicleRotation = new Vector4[Capacity];
        public Func<Component,float> SnowRemaining;
        public Func<Component,Vector4> SideSnowAmount;
        public Func<Component,string> StableVehicleId;
        private readonly Dictionary<string,VehicleSnowMask> savedMasks = new Dictionary<string,VehicleSnowMask>(StringComparer.OrdinalIgnoreCase);
        private int snapshotGeneration;
        private readonly List<AsyncGPUReadbackRequest> pendingSnapshots = new List<AsyncGPUReadbackRequest>();
        public void SaveMasks(List<VehicleSnowMask> destination)
        {
            // Readbacks are requested when a mask changes, not every frame.
            // Complete pending copies before serializing the game's save snapshot.
            foreach(var request in pendingSnapshots) if(!request.done) request.WaitForCompletion();
            pendingSnapshots.Clear();
            foreach(var mask in savedMasks.Values) { mask.Pack(); destination.Add(mask); }
        }
        public void RestoreMasks(List<VehicleSnowMask> records)
        {
            savedMasks.Clear(); snapshotGeneration++;
            if(records==null) return;
            foreach(var record in records)
                if(record!=null && !string.IsNullOrEmpty(record.Id) && record.Id.Length<=80 &&
                    !string.IsNullOrEmpty(record.Packed) && record.Packed.Length<=200000 &&
                    Math.Abs(record.Area.x)<1000 && Math.Abs(record.Area.y)<1000 &&
                    record.Area.z>0 && record.Area.z<1000 && record.Area.w>0 && record.Area.w<1000)
                    savedMasks[record.Id]=record;
        }
        private bool RestoreMask(Vehicle vehicle,float snowClock)
        {
            string id=StableVehicleId?.Invoke(vehicle.Source);
            VehicleSnowMask state;
            if(string.IsNullOrEmpty(id) || !savedMasks.TryGetValue(id,out state)) return false;
            Texture2D texture=null;
            try
            {
                var bytes=state.Unpack(); if(bytes==null) return false;
                texture=new Texture2D(256,256,TextureFormat.RHalf,false,true);
                texture.LoadRawTextureData(bytes); texture.Apply(false,false);
                Graphics.CopyTexture(texture,0,0,snow,vehicle.Slot,0);
                vehicle.SnowArea=state.Area; snowAreas[vehicle.Slot]=state.Area;
                vehicle.SnowReady=true;vehicle.LastSnowClock=snowClock;
                vehicle.SnowShapeDirty=state.Area!=vehicle.Area;
                return true;
            }
            catch(Exception e) { savedMasks.Remove(id); Debug.LogWarning("[DVSeasons] Invalid vehicle snow mask: "+e.Message); return false; }
            finally { if(texture!=null) UnityEngine.Object.Destroy(texture); }
        }
        private void SnapshotMask(Vehicle vehicle)
        {
            string id=StableVehicleId?.Invoke(vehicle.Source);
            if(string.IsNullOrEmpty(id) || !SystemInfo.supportsAsyncGPUReadback) return;
            int generation=snapshotGeneration; var area=vehicle.SnowArea;
            for(int i=pendingSnapshots.Count-1;i>=0;i--) if(pendingSnapshots[i].done) pendingSnapshots.RemoveAt(i);
            pendingSnapshots.Add(AsyncGPUReadback.Request(snow,0,0,256,0,256,vehicle.Slot,1,request=>
            {
                if(generation!=snapshotGeneration || request.hasError) return;
                var data = request.GetData<byte>();
                VehicleSnowMask state;
                if (!savedMasks.TryGetValue(id, out state))
                    savedMasks[id] = state = new VehicleSnowMask { Id = id };
                if (state.Raw == null || state.Raw.Length != data.Length) state.Raw = new byte[data.Length];
                data.CopyTo(state.Raw);
                state.Area = area;
                state.Packed = null; // The previous compressed save no longer describes this mask.
            }));
        }
        public Func<Renderer,int,bool> HasNativeSnowTexture;
        private CommandBuffer capture;
        private Material material;
        private RenderTexture heights;
        private RenderTexture heightSlice;
        private RenderTexture snow, snowSlice;
        private Mesh snowQuad;
        private int nextSnowVehicle;
        private int nextCargoVehicle;
        private float nextScan;
        private Vector3 lastCameraPosition;
        private bool cameraPositionKnown;
        private Type trainType;
        private PropertyInfo externalGetter, dummyExternalGetter, cargoControllerGetter;
        private FieldInfo explodedField;
        public Func<IEnumerable<Component>> DiscoverVehicles;
        public Func<IEnumerable<Transform>> DiscoverMovingRoots;
        private static readonly int DataId = Shader.PropertyToID("_DVPSVehicleData");
        private static readonly int SlopeId = Shader.PropertyToID("_DVPSVehicleSlope");
        private static readonly int WorldToLocalId = Shader.PropertyToID("_DVPSVehicleWorldToLocal");
        private static readonly int IndexId = Shader.PropertyToID("_DVPSVehicleIndex");
        private static readonly int CutoffId = Shader.PropertyToID("_DVPSVehicleCutoff");
        private static readonly int AlbedoId = Shader.PropertyToID("_DVPSVehicleAlbedo");
        private static readonly int STId = Shader.PropertyToID("_DVPSVehicleST");
        private readonly RenderTargetIdentifier[] dataTargets =
            { new RenderTargetIdentifier(DataId), new RenderTargetIdentifier(SlopeId) };
        private RenderTextureFormat slopeFormat;
        private Texture2D emptySurfaceData;
        private bool frameTargetsAllocated;
        private float recordedIndex = float.NaN, recordedCutoff = float.NaN;
        public int Revision { get; private set; }
        public int CaptureCount { get; private set; }

        public bool Initialize(SeasonAssetBundleRepository repository)
        {
            nativeMaterials.Enabled=NativeMaterialsEnabled;
            bool nativeWasReady=nativeMaterials.IsReady;
            if(nativeMaterials.Initialize(repository) && !nativeWasReady)
                foreach(var vehicle in vehicles)vehicle.PartsPending=true;
            if (material != null) return true;
            var shader = repository.LoadShader("SnowVehicle");
            if (shader == null || !SystemInfo.supports2DArrayTextures) return false;
            capture = new CommandBuffer { name = "DVSeasons vehicle snow cache" };
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, enableInstancing = true };
            emptySurfaceData=new Texture2D(1,1,TextureFormat.RGBA32,false,true)
                {name="DVSeasons empty surface data",hideFlags=HideFlags.HideAndDontSave};
            emptySurfaceData.SetPixel(0,0,Color.clear);emptySurfaceData.Apply(false,true);
            slopeFormat=SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8)?RenderTextureFormat.R8:RenderTextureFormat.RHalf;
            ResizeTextures(Mathf.Max(32,vehicles.Count));
            heightSlice = new RenderTexture(256,256,24,RenderTextureFormat.RFloat,RenderTextureReadWrite.Linear)
            { hideFlags = HideFlags.HideAndDontSave };
            heightSlice.Create();
            snowSlice = new RenderTexture(256,256,0,RenderTextureFormat.RHalf,RenderTextureReadWrite.Linear)
            { hideFlags=HideFlags.HideAndDontSave }; snowSlice.Create();
            snowQuad=new Mesh { vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)},
                uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up},triangles=new[]{0,1,2,0,2,3},hideFlags=HideFlags.HideAndDontSave };
            return true;
        }

        private void ResizeTextures(int required)
        {
            int size=Mathf.Min(Capacity,Mathf.NextPowerOfTwo(Mathf.Max(32,required)));
            if(size<=textureCapacity) return;
            using(SnowPerformance.Measure("vehicle-array-resize")) ResizeTexturesCore(size);
        }
        private void ResizeTexturesCore(int size)
        {
            var oldHeights=heights; var oldSnow=snow;
            heights = new RenderTexture(256,256,0,RenderTextureFormat.RFloat,RenderTextureReadWrite.Linear)
            { dimension = TextureDimension.Tex2DArray, volumeDepth = size, filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            heights.Create();
            snow = new RenderTexture(256,256,0,RenderTextureFormat.RHalf,RenderTextureReadWrite.Linear)
            { dimension=TextureDimension.Tex2DArray,volumeDepth=size,filterMode=FilterMode.Bilinear,
                wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave };
            snow.Create();
            if(oldHeights!=null)
            {
                foreach(var v in vehicles)
                {
                    if(v.Slot>=textureCapacity) continue;
                    if(v.Ready) Graphics.CopyTexture(oldHeights,v.Slot,0,heights,v.Slot,0);
                    if(v.SnowReady) Graphics.CopyTexture(oldSnow,v.Slot,0,snow,v.Slot,0);
                }
                if(Application.isPlaying)
                { UnityEngine.Object.Destroy(oldHeights); UnityEngine.Object.Destroy(oldSnow); }
                else
                { UnityEngine.Object.DestroyImmediate(oldHeights); UnityEngine.Object.DestroyImmediate(oldSnow); }
            }
            textureCapacity=size;
        }

        // Also used by the rendering regression with a moving, detached interior.
        public void Register(Transform root, Transform interior, Transform interiorLod = null)
        {
            if (root == null) return;
            Vehicle vehicle;
            if (!byId.TryGetValue(root.GetInstanceID(),out vehicle))
            {
                if (vehicles.Count >= Capacity) return;
                int slot=0; while(slot<Capacity && usedSlots[slot]) slot++;
                if(slot==Capacity) return;
                if(material!=null && slot>=textureCapacity) ResizeTextures(slot+1);
                usedSlots[slot]=true;
                vehicle = new Vehicle { Root = root, RootId=root.GetInstanceID(), Slot = slot };
                vehicles.Add(vehicle); byId.Add(root.GetInstanceID(),vehicle); Revision++; exclusionMeshUsesDirty=true;
                ObjectLimiter.InvalidateMembership();
            }
            bool branchesChanged=!SameTransform(vehicle.Interior,interior) || !SameTransform(vehicle.InteriorLod,interiorLod);
            bool changed=branchesChanged || vehicle.Parts.Count==0;
            vehicle.Interior = interior; vehicle.InteriorLod = interiorLod;
            if(branchesChanged) ObjectLimiter.InvalidateMembership();
            if(changed) vehicle.PartsPending=true;
            TrackStaticRoot(root,interior,interiorLod);
        }

        public void Update(Camera camera)
        {
            junctions.Update();
            if(junctions.ConsumeGeometryChanged()) StaticGeometryChanged?.Invoke();
            // Advance bounded bridge discovery every frame, even between fleet polls.
            var movingRoots=DiscoverMovingRoots?.Invoke();
            // A teleport/fast relocation invalidates the nearest-car ordering;
            // request an immediate discovery instead of waiting for the normal
            // half-second polling interval.
            if (camera != null)
            {
                if (!cameraPositionKnown || (camera.transform.position-lastCameraPosition).sqrMagnitude > 64f)
                    nextScan = 0f;
                lastCameraPosition = camera.transform.position;
                cameraPositionKnown = true;
            }
            if (Time.realtimeSinceStartup >= nextScan)
            using(SnowPerformance.Measure("vehicle-poll"))
            {
                // Cars can be streamed in or become visible immediately after
                // a fast relocation.  A five-second discovery interval left
                // their snow overlay blank even when the same cars were already
                // rendered with snow before the move.
                nextScan = Time.realtimeSinceStartup+0.5f;
                if (trainType == null)
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    { trainType = assembly.GetType("TrainCar",false); if (trainType != null) break; }
                if(trainType!=null && externalGetter==null)
                {
                    externalGetter=trainType.GetProperty("loadedExternalInteractables");
                    dummyExternalGetter=trainType.GetProperty("loadedDummyExternalInteractables");
                    cargoControllerGetter=trainType.GetProperty("CargoModelController");
                    explodedField=trainType.GetField("isExploded",BindingFlags.Public|BindingFlags.Instance);
                    if(explodedField!=null && explodedField.FieldType!=typeof(bool)) explodedField=null;
                }
                if (trainType != null || DiscoverMovingRoots != null)
                {
                    var candidates = discoveryCars; candidates.Clear();
                    var moving = discoveryMoving; moving.Clear();
                    var allLive = discoveryAllLive; allLive.Clear();
                    if (movingRoots != null) foreach (var root in movingRoots)
                    {
                        if (root == null || !root.gameObject.scene.IsValid() || !root.gameObject.activeInHierarchy) continue;
                        if (!allLive.Add(root.GetInstanceID())) continue;
                        TrackStaticRoot(root, null, null);
                        moving.Add(new DiscoveryCandidate {Root=root,Id=root.GetInstanceID(),Distance=(root.position-lastCameraPosition).sqrMagnitude});
                    }
                    foreach (var item in DiscoverVehicles != null ? DiscoverVehicles() : Array.Empty<Component>())
                    {
                        var component = item as Component;
                        if (component == null || !component.gameObject.scene.IsValid() ||
                            !component.gameObject.activeInHierarchy) continue;
                        var root=component.transform;
                        int id=root.GetInstanceID();
                        if(!allLive.Add(id)) continue;
                        var interior=ReadTransform(component,"interior");
                        var interiorLod=ReadTransform(component,"interiorLOD");
                        TrackStaticRoot(root,interior,interiorLod);
                        candidates.Add(new DiscoveryCandidate {Source=component,Root=root,Id=id,Interior=interior,
                            InteriorLod=interiorLod,Distance=(root.position-lastCameraPosition).sqrMagnitude});
                    }
                    departed.Clear();
                    foreach(var pair in staticVehicles) if(!allLive.Contains(pair.Key)) departed.Add(pair.Key);
                    foreach(var id in departed) staticVehicles.Remove(id);
                    // Bridges and every loaded car keep their own local masks;
                    // nearest-pending preparation below already prioritizes them.
                    // Sort only if the shader ceiling actually requires a choice,
                    // using positions fetched once, rather than native Transform
                    // lookups in every comparison twice per second.
                    if(moving.Count>Capacity) moving.Sort(nearestCandidate);
                    int movingCount=Mathf.Min(Capacity,moving.Count);
                    int carCount=Mathf.Min(Capacity-movingCount,candidates.Count);
                    if(carCount<candidates.Count) candidates.Sort(nearestCandidate);
                    ResizeTextures(movingCount+carCount);
                    var live=discoveryLive; live.Clear();
                    for(var i=0;i<movingCount;i++) live.Add(moving[i].Id);
                    for(var i=0;i<carCount;i++) live.Add(candidates[i].Id);
                    // Remove departed/deleted cars before assigning free array slices.
                    for (var i=vehicles.Count-1;i>=0;i--)
                    {
                        var v=vehicles[i];
                        if (v.Root != null && live.Contains(v.RootId)) continue;
                        Unsubscribe(v);
                        usedSlots[v.Slot]=false;
                        byId.Remove(v.RootId);
                        vehicles.RemoveAt(i); Revision++; exclusionMeshUsesDirty=true;
                        ObjectLimiter.InvalidateMembership();
                    }
                    for(var i=0;i<movingCount;i++) Register(moving[i].Root,null);
                    for(var i=0;i<carCount;i++)
                    {
                        var candidate=candidates[i];
                        var item=candidate.Source;
                        Register(candidate.Root,candidate.Interior,candidate.InteriorLod);
                        var v=byId[candidate.Id];
                        if(!v.RollingStock) { v.RollingStock=true; ObjectLimiter.InvalidateMembership(); }
                        if(v.Source != item)
                        {
                            Unsubscribe(v); v.Source=item;
                            v.CargoController=cargoControllerGetter?.GetValue(item,null);
                            v.CargoGetter=v.CargoController?.GetType().GetMethod("GetCurrentCargoModel");
                            v.InteriorLoadedHandler=ignored=>{v.PartsPending=true;v.InteriorPending=true;v.NextCargoCheck=0;};
                            trainType.GetEvent("InteriorLoaded")?.AddEventHandler(item,v.InteriorLoadedHandler);
                            trainType.GetEvent("ExternalInteractableLoaded")?.AddEventHandler(item,v.InteriorLoadedHandler);
                        }
                    }
                }
            }
            using(SnowPerformance.Measure("vehicle-limiter")) ObjectLimiter.Update(camera, vehicles);
            // Cache all newly visible cars once. Translating/rotating a car never
            // recaptures its height map: sampling is in that car's local frame.
            // Spread native cargo/interior polling across frames in large yards.
            for(int i=0;i<Mathf.Min(8,vehicles.Count);i++)
            { nextCargoVehicle%=vehicles.Count; CheckCargo(vehicles[nextCargoVehicle++]); }
            Vehicle pending=null; float nearest=float.MaxValue;
            foreach (var v in vehicles)
            {
                if(v.Root==null || (!v.Dirty && !v.PartsPending) ||
                    (!ObjectLimiter.IsSelected(v.Root) && !v.PartsPending)) continue;
                float distance=(v.Root.position-lastCameraPosition).sqrMagnitude;
                if(distance<nearest) {nearest=distance;pending=v;}
            }
            if(pending!=null)
            {
                CheckCargo(pending);
                if(pending.PartsPending)
                { using(SnowPerformance.Measure("vehicle-parts")) RefreshParts(pending); pending.PartsPending=pending.InteriorPending=false; }
                if(pending.Dirty && ObjectLimiter.IsSelected(pending.Root))
                    using(SnowPerformance.Measure("vehicle-height")) CaptureHeight(pending);
            }
        }

        public void SetCargo(Transform root,Transform cargo)
        {
            Vehicle v;
            if(root==null || !byId.TryGetValue(root.GetInstanceID(),out v) || SameTransform(v.Cargo,cargo)) return;
            v.Cargo=cargo;v.PartsPending=true;
            ObjectLimiter.InvalidateMembership();
        }
        private static bool SameTransform(Transform left,Transform right)
        {
            // Unity treats a destroyed transform as null. The cached reference
            // still represents geometry that must be removed from the height map.
            return ReferenceEquals(left,right) || (left!=null && right!=null && left==right);
        }
        private void CheckCargo(Vehicle v,bool force=false)
        {
            if(!force && Time.realtimeSinceStartup<v.NextCargoCheck) return;
            v.NextCargoCheck=Time.realtimeSinceStartup+0.25f;
            if(v.Source!=null)
            {
                var external=externalGetter?.GetValue(v.Source,null) as GameObject;
                var dummy=dummyExternalGetter?.GetValue(v.Source,null) as GameObject;
                SetExternalParts(v.Root,external!=null?external.transform:null,dummy!=null?dummy.transform:null);
                // An explosion can replace descendants without replacing their
                // cargo/interior roots or raising an interior-loaded event.
                if(explodedField!=null && (bool)explodedField.GetValue(v.Source)!=v.PartsExploded)
                    v.PartsPending=true;
            }
            if(v.CargoGetter==null && v.Source!=null)
            {
                v.CargoController=cargoControllerGetter?.GetValue(v.Source,null);
                v.CargoGetter=v.CargoController?.GetType().GetMethod("GetCurrentCargoModel");
            }
            if(v.CargoGetter==null) return;
            var cargo=v.CargoGetter.Invoke(v.CargoController,null) as GameObject;
            SetCargo(v.Root,cargo!=null?cargo.transform:null);
        }
        public void SetExternalParts(Transform root,Transform external,Transform dummy)
        {
            Vehicle v;
            if(root==null || !byId.TryGetValue(root.GetInstanceID(),out v) ||
                (SameTransform(v.External,external) && SameTransform(v.DummyExternal,dummy))) return;
            v.External=external;v.DummyExternal=dummy;v.PartsPending=true;
            ObjectLimiter.InvalidateMembership();
        }
        // Tracks every live car independently of the limited detailed height cache.
        public void TrackStaticRoot(Transform root,Transform interior,Transform interiorLod)
        {
            StaticVehicle v;
            if(!staticVehicles.TryGetValue(root.GetInstanceID(),out v))
            {v=new StaticVehicle{Root=root};staticVehicles.Add(root.GetInstanceID(),v);}
            if(v.Interior!=interior || v.InteriorLod!=interiorLod)
            {v.Interior=interior;v.InteriorLod=interiorLod;}
        }
        private static Transform ReadTransform(Component component,string field)
        {
            var type=component.GetType(); InteriorFields fields;
            if(!interiorFields.TryGetValue(type,out fields))
            {
                fields=new InteriorFields {Interior=type.GetField("interior",BindingFlags.Public|BindingFlags.Instance),
                    InteriorLod=type.GetField("interiorLOD",BindingFlags.Public|BindingFlags.Instance)};
                interiorFields.Add(type,fields);
            }
            return (field=="interior"?fields.Interior:fields.InteriorLod)?.GetValue(component) as Transform;
        }

        private void RefreshParts(Vehicle v)
        {
            nativeMaterials.Release(v);
            ClearPartsScratch();
            LastPartsHierarchyQueries=0;
            try
            {
            AddPartRoot(v.Root);AddPartRoot(v.Interior);AddPartRoot(v.InteriorLod);
            AddPartRoot(v.Cargo);AddPartRoot(v.External);AddPartRoot(v.DummyExternal);
            var parts=partsScratch;var seen=partSeenScratch;
            v.CaptureRenderers.Clear();
            var handlebar=SnowMovingVehiclePartFrame.FindHandlebar(v.Root);
            var lodParts=partLodsScratch;
            v.Lods.Clear();
            var seenGroups=partGroupsSeenScratch;
            foreach(var root in partRootsScratch)
            {
                partGroupsScratch.Clear();root.GetComponentsInChildren(true,partGroupsScratch);LastPartsHierarchyQueries++;
                foreach(var group in partGroupsScratch)
                {
                    if(!seenGroups.Add(group.GetInstanceID())) continue;
                    var set=new LodSet{Group=group,Levels=group.GetLODs()};v.Lods.Add(set);
                    for(int index=0;index<set.Levels.Length;index++) foreach(var r in set.Levels[index].renderers)
                    {
                        if(r==null) continue;
                        int id=r.GetInstanceID();PartLodMembership membership;
                        // A renderer may be shared by several levels (native
                        // brake pads and cab labels do this). Keep every level.
                        // Ambiguous membership in different groups stays unculled.
                        if(lodParts.TryGetValue(id,out membership))
                            lodParts[id]=membership.Set==set && index<32?
                                new PartLodMembership {Set=set,Mask=membership.Mask|(1<<index)}:default(PartLodMembership);
                        else lodParts[id]=index<32?new PartLodMembership {Set=set,Mask=1<<index}:default(PartLodMembership);
                    }
                }
            }
            foreach (var root in partRootsScratch)
            {
                partRenderersScratch.Clear();root.GetComponentsInChildren(true,partRenderersScratch);LastPartsHierarchyQueries++;
                foreach (var renderer in partRenderersScratch)
                {
                    if(renderer==null || !seen.Add(renderer.GetInstanceID())) continue;
                    v.CaptureRenderers.Add(renderer);
                    if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                    if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                    var t=renderer.transform;
                    var interior=(v.Interior != null && t.IsChildOf(v.Interior)) ||
                        (v.InteriorLod != null && t.IsChildOf(v.InteriorLod));
                    for (var ancestor=t;ancestor != null && ancestor != v.Root;ancestor=ancestor.parent)
                        if (ancestor.name.IndexOf("interior",StringComparison.OrdinalIgnoreCase)>=0 ||
                            ancestor.name.StartsWith("[axle]",StringComparison.OrdinalIgnoreCase)) interior=true;
                    // DV parents visible freight (including cars) beneath interior.
                    // Only the cab stays excluded; freight has its own exposed shell.
                    if(v.Cargo!=null && t.IsChildOf(v.Cargo)) interior=false;
                    if((v.External!=null && t.IsChildOf(v.External)) ||
                        (v.DummyExternal!=null && t.IsChildOf(v.DummyExternal))) interior=false;
                    var filter=renderer.GetComponent<MeshFilter>(); var skinned=renderer as SkinnedMeshRenderer;
                    var mesh=filter != null ? filter.sharedMesh : skinned != null ? skinned.sharedMesh : null;
                    if(mesh == null) continue;
                    var p=new Part { Renderer=renderer,Materials=renderer.sharedMaterials,Mesh=mesh,Filter=filter,Interior=interior };
                    p.MovingFrame=SnowMovingVehiclePartFrame.TryCreate(v.Root,handlebar,renderer);
                    PartLodMembership lod;
                    if(lodParts.TryGetValue(renderer.GetInstanceID(),out lod))
                    {
                        p.Lod=lod.Set;p.LodMask=lod.Mask;
                        // Retain the finest membership for capture metadata.
                        while(p.LodIndex<31 && !p.UsesLod(p.LodIndex)) p.LodIndex++;
                    }
                    int count=Mathf.Min(p.Materials.Length,mesh.subMeshCount);
                    p.Opaque=new bool[count];p.Cutoff=new float[count];p.Albedo=new Texture[count];p.ST=new Vector4[count];
                    p.DrawMetadata=new SnowVehicleDrawScheduler.DrawMetadata[count];
                    for(int i=0;i<count;i++)
                    {
                        var m=p.Materials[i]; if(m==null || m.renderQueue>2500) continue;
                        p.Opaque[i]=true; p.HasOpaque=true;
                        p.Cutoff[i]=m.GetTag("RenderType",false)=="TransparentCutout" ? (m.HasProperty("_Cutoff")?m.GetFloat("_Cutoff"):0.5f) : 0;
                        p.Albedo[i]=m.HasProperty("_MainTex")?m.GetTexture("_MainTex"):null;
                        var scale=m.HasProperty("_MainTex")?m.GetTextureScale("_MainTex"):Vector2.one;
                        var offset=m.HasProperty("_MainTex")?m.GetTextureOffset("_MainTex"):Vector2.zero;
                        p.ST[i]=new Vector4(scale.x,scale.y,offset.x,offset.y);
                        p.DrawMetadata[i]=new SnowVehicleDrawScheduler.DrawMetadata(renderer,mesh,i,p.Cutoff[i],p.Albedo[i],p.ST[i]);
                    }
                    parts.Add(p);
                }
            }
            var signature=17;
            foreach (var p in parts) signature=unchecked(signature*31+p.Renderer.GetInstanceID()+p.Mesh.GetInstanceID()+(p.Interior?1:0));
            // LOD membership and active cargo can change without new renderers.

            v.Parts.Clear(); v.Parts.AddRange(parts); v.Signature=signature; v.Dirty=true;
            v.Topology.Reset();
            v.Oriented.Reset();
            PrepareTopology(v);
            exclusionMeshUsesDirty=true;
            v.PartsExploded=v.Source!=null && explodedField!=null && (bool)explodedField.GetValue(v.Source);
            nativeMaterials.Bind(v);
            v.NativeComplete=v.Parts.Count>0;
            v.HasIndependentSnowTexture=false;
            foreach(var part in v.Parts)foreach(var texture in part.Albedo)
                if(texture!=null && texture.name.IndexOf("coal",StringComparison.OrdinalIgnoreCase)>=0)v.HasIndependentSnowTexture=true;
            foreach(var part in v.Parts)if(part.HasOpaque && !part.NativeComplete){v.NativeComplete=false;break;}
            v.PartCache.Build(v.Parts);
            if(CombinedMeshesEnabled)v.MeshCache.Build(v);else v.MeshCache.Dispose();
            Revision++;
            }
            finally {ClearPartsScratch();}
        }

        private void AddPartRoot(Transform root)
        {
            if(root==null)return;
            // Preserve first-seen traversal order. An earlier ancestor already
            // discovers all inactive descendants too. If a later root instead
            // contains an earlier one, keep both and let the existing ID sets
            // remove duplicates after discovery, as before.
            foreach(var earlier in partRootsScratch)
                if(root==earlier || root.IsChildOf(earlier))return;
            partRootsScratch.Add(root);
        }

        private void ClearPartsScratch()
        {
            partRootsScratch.Clear();partsScratch.Clear();partRenderersScratch.Clear();partGroupsScratch.Clear();
            partSeenScratch.Clear();partGroupsSeenScratch.Clear();partLodsScratch.Clear();
        }

        public void HideForStaticCapture(Vector4 area)
        {
            junctions.PrepareForStaticCapture(area);
            // A train can spawn between periodic discovery and this capture.
            // Refresh the exclusion roots now so it cannot become static shelter.
            if(DiscoverVehicles!=null) foreach(var item in DiscoverVehicles())
            {
                var component=item as Component;
                if(component==null) continue;
                TrackStaticRoot(component.transform,ReadTransform(component,"interior"),ReadTransform(component,"interiorLOD"));
            }
            // Do not bake the rotating bridge into the stationary world's height
            // map: otherwise its old position leaves a snow-shaped hole in the pit.
            if (DiscoverMovingRoots != null) foreach (var root in DiscoverMovingRoots())
                if (root != null) TrackStaticRoot(root,null,null);
            hidden.Clear();
            foreach(var sv in staticVehicles.Values)
            {
                if(sv.Root==null || Mathf.Abs(sv.Root.position.x-area.x)>area.z+40 ||
                    Mathf.Abs(sv.Root.position.z-area.y)>area.z+40) continue;
                Vehicle v;
                byId.TryGetValue(sv.Root.GetInstanceID(),out v);
                // Cargo and dummy externals may stream beneath an unchanged
                // interior between the bounded fleet polls. Check their cheap
                // native references before trusting the renderer cache.
                if(v!=null) CheckCargo(v,true);
                if(v!=null && !v.PartsPending && !v.InteriorPending && v.CaptureRenderers.Count>0 &&
                    v.Interior==sv.Interior && v.InteriorLod==sv.InteriorLod)
                {
                    foreach(var renderer in v.CaptureRenderers) HideRenderer(renderer);
                    continue;
                }
                // Only an uncached or newly streamed hierarchy needs discovery.
                // Reuse the list; ready cars never allocate/scan during captures.
                HideHierarchy(sv.Root); HideHierarchy(sv.Interior); HideHierarchy(sv.InteriorLod);
                if(v!=null) { HideHierarchy(v.Cargo); HideHierarchy(v.External); HideHierarchy(v.DummyExternal); }
            }
        }
        private void HideRenderer(Renderer renderer)
        {
            if(renderer==null || renderer.forceRenderingOff) return;
            hidden.Add(renderer); renderer.forceRenderingOff=true;
        }
        private void HideHierarchy(Transform root)
        {
            if(root==null) return;
            root.GetComponentsInChildren(true,captureScratch);
            foreach(var renderer in captureScratch) HideRenderer(renderer);
            captureScratch.Clear();
        }
        public void RestoreAfterStaticCapture()
        {
            junctions.RestoreAfterStaticCapture();
            foreach (var renderer in hidden) if (renderer != null) renderer.forceRenderingOff=false;
            hidden.Clear();
        }

        private void CaptureHeight(Vehicle v)
        {
            foreach(var part in v.Parts) part.MovingFrame?.CaptureReference();
            var bounds=new Bounds(Vector3.zero,Vector3.one);
            var first=true;
            foreach (var part in v.Parts)
            {
                if (part.Interior || part.Renderer == null || (part.Lod!=null && part.LodIndex!=0)) continue;
                var mesh=part.Mesh;
                if (mesh == null) continue;
                var b=mesh.bounds; var matrix=v.Root.worldToLocalMatrix*part.Renderer.localToWorldMatrix;
                for(var corner=0;corner<8;corner++)
                {
                    var point=matrix.MultiplyPoint3x4(b.center+Vector3.Scale(b.extents,
                        new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1)));
                    if (first) { bounds=new Bounds(point,Vector3.zero); first=false; } else bounds.Encapsulate(point);
                }
            }
            v.LocalBounds=bounds;
            v.Area=new Vector4(bounds.center.x,bounds.center.z,Mathf.Max(0.5f,bounds.extents.x+0.1f),Mathf.Max(0.5f,bounds.extents.z+0.1f));
            v.Vertical=new Vector4(bounds.min.y-0.1f,Mathf.Max(1f,bounds.size.y+0.2f),0,0);
            areas[v.Slot]=v.Area;
            capture.Clear(); capture.SetRenderTarget(heightSlice); capture.ClearRenderTarget(true,true,new Color(-100000,0,0,0));
            recordedIndex=recordedCutoff=float.NaN;
            capture.SetGlobalMatrix("_DVPSVehicleWorldToLocal",v.Root.worldToLocalMatrix);
            capture.SetGlobalVector("_DVPSVehicleArea",v.Area); capture.SetGlobalVector("_DVPSVehicleVertical",v.Vertical);
            foreach (var part in v.Parts)
                if (!part.Interior && (part.Lod==null || part.LodIndex==0) && part.Renderer != null && part.Renderer.gameObject.activeInHierarchy)
                    DrawPart(capture,part,1);
            Graphics.ExecuteCommandBuffer(capture);
            Graphics.CopyTexture(heightSlice,0,0,heights,v.Slot,0);
            v.Dirty=false; v.Ready=true; CaptureCount++;
            v.SnowShapeDirty=true;
        }

        public void ResetSnow()
        { foreach(var v in vehicles) v.SnowReady=false; savedMasks.Clear(); snapshotGeneration++; }

        public void Accumulate(RenderTexture nearHeight,Vector4 nearArea,float nearOffset,
            RenderTexture farHeight,Vector4 farArea,float farOffset,float snowClock,
            RenderTexture distantHeight,Vector4 distantArea,float distantOffset)
        {
            // One local mask per frame. Dry motion only reuses the mask; shelter
            // controls new accumulation, never erases snow already on a vehicle.
            for(int n=0;n<vehicles.Count;n++)
            {
                nextSnowVehicle%=vehicles.Count;
                var v=vehicles[nextSnowVehicle++];
                if(!ObjectLimiter.IsSelected(v.Root)) continue;
                if(v.Root!=null && v.Ready && !v.SnowReady && RestoreMask(v,snowClock)) break;
                if(v.Root==null || !v.Ready || (v.SnowReady && !v.SnowShapeDirty && snowClock-v.LastSnowClock<1f/180f)) continue;
                // Never initialize a distant car from a clamped edge pixel of
                // an unrelated shelter map. Preserve its existing mask instead.
                if(!FitsExposure(v,farArea) && !(distantHeight!=null && FitsExposure(v,distantArea))) continue;
                capture.Clear();capture.SetRenderTarget(snowSlice);
                capture.SetGlobalTexture("_DVPSVehicleHeights",heights);
                capture.SetGlobalTexture("_DVPSVehicleSnow",snow);
                capture.SetGlobalFloat("_DVPSVehicleIndex",v.Slot);
                capture.SetGlobalMatrix("_DVPSVehicleLocalToWorld",v.Root.localToWorldMatrix);
                capture.SetGlobalVector("_DVPSVehicleArea",v.Area);
                capture.SetGlobalVector("_DVPSPreviousSnowArea",v.SnowReady?v.SnowArea:v.Area);
                capture.SetGlobalVector("_DVPSAccumulation",new Vector4(v.SnowReady?0:1,Mathf.Max(0,snowClock-v.LastSnowClock),nearOffset,farOffset));
                capture.SetGlobalTexture("_DVPSNearHeight",nearHeight);capture.SetGlobalTexture("_DVPSFarHeight",farHeight);
                capture.SetGlobalVector("_DVPSNearArea",nearArea);capture.SetGlobalVector("_DVPSFarArea",farArea);
                capture.SetGlobalTexture("_DVPSDistantHeight",distantHeight!=null?distantHeight:farHeight);
                capture.SetGlobalVector("_DVPSDistantArea",distantHeight!=null?distantArea:farArea);
                capture.SetGlobalFloat("_DVPSDistantOffset",distantHeight!=null?distantOffset:farOffset);
                capture.DrawMesh(snowQuad,Matrix4x4.identity,material,0,3);
                Graphics.ExecuteCommandBuffer(capture);
                Graphics.CopyTexture(snowSlice,0,0,snow,v.Slot,0);
                v.SnowReady=true;v.SnowShapeDirty=false;v.LastSnowClock=snowClock;
                v.SnowArea=v.Area;snowAreas[v.Slot]=v.SnowArea;
                SnapshotMask(v);
                break;
            }
        }

        private static bool FitsExposure(Vehicle vehicle,Vector4 area)
        {
            var center=vehicle.Root.TransformPoint(vehicle.LocalBounds.center);
            var scale=vehicle.Root.lossyScale;
            float radius=vehicle.LocalBounds.extents.magnitude*Mathf.Max(Mathf.Abs(scale.x),
                Mathf.Max(Mathf.Abs(scale.y),Mathf.Abs(scale.z)));
            return area.z>0 && Mathf.Max(Mathf.Abs(center.x-area.x),Mathf.Abs(center.z-area.y))+radius<area.z*.9f;
        }

        internal void BindEmptyFrame(CommandBuffer buffer)
        {
            // Texture2D.blackTexture has alpha one, which means vehicle ID 1.
            // Reuse the explicit zero-alpha pixel used for an empty frustum.
            buffer.SetGlobalTexture(DataId,emptySurfaceData);
            buffer.SetGlobalTexture(SlopeId,Texture2D.blackTexture);
        }
        public void Record(CommandBuffer buffer,Camera camera,RailSnowTracks rails,bool extraVisible=false)
        {
            using(SnowPerformance.MeasureScheduler(vehicleDraws))
            using(SnowPerformance.MeasureScheduler(partDraws)) RecordCore(buffer,camera,rails,extraVisible);
        }
        public void PrepareNativeSnow(float amount,float glare,Texture noise,Color ambient)
        {
            using(SnowPerformance.Measure("snow-native-materials"))
                nativeMaterials.Prepare(vehicles,this,heights,snow,amount,glare,noise,ambient);
        }
        private static void PrepareTopology(Vehicle vehicle)
        {
            if(vehicle.Topology.Initialized)return;
            var worldToLocal=vehicle.Root.worldToLocalMatrix;
            foreach(var part in vehicle.Parts)
                if(part.HasOpaque && part.Renderer!=null)
                    IncludeTopologyPart(vehicle,part,worldToLocal);
            vehicle.Topology.Finish();
        }
        private static void IncludeTopologyPart(Vehicle vehicle,Part part,Matrix4x4 worldToLocal)
        {
            var renderer=part.Renderer;
            if(renderer==null)return;
            var skin=renderer as SkinnedMeshRenderer;
            if(skin!=null)
                vehicle.Topology.Include(skin.localBounds,worldToLocal*renderer.localToWorldMatrix);
            else if(part.Mesh!=null && !renderer.isPartOfStaticBatch)
                vehicle.Topology.Include(part.Mesh.bounds,worldToLocal*renderer.localToWorldMatrix);
            else
                // Static batching can replace sharedMesh with a combined mesh
                // covering other objects. Keep the native per-renderer bound.
                vehicle.Topology.Include(renderer.bounds,worldToLocal);
        }
        private void RecordCore(CommandBuffer buffer,Camera camera,RailSnowTracks rails,bool extraVisible)
        {
            // The preparation/culling phase includes its nested RT, junction
            // and rail surface setup. Do not sum nested timings twice.
            using(SnowPerformance.Measure("snow-vehicle-culling"))
            {
            eligibleFull=uniqueMeshFull=skinnedFull=staticFull=mirroredFull=streamsFull=otherFull=0;
            FrameDrawCount=FrameSnowDrawCount=FrameExclusionDrawCount=0;
            FrameCombinedCommands=0;
            FrameExclusionBatchCount=FrameInstancedExclusionCount=0;
            FrameFullBatchCount=FrameInstancedFullCount=0;
            FrameOrientedFastCount=FrameOrientedDetailedCount=FrameOrientedMeshBoundsReadCount=0;
            FrameUsesPartBatches=false;
            orderedDraws=vehicleDraws;
            // Cache mesh/material grouping until topology changes. Only transforms,
            // visibility and the order of active runs change during ordinary frames.
            if(exclusionMeshUsesDirty)
                using(SnowPerformance.Measure("snow-fallback-index")) RefreshExclusionMeshUses();
            unchecked { exclusionFrame++; }
            foreach(var batch in activeExclusions) { batch.Count=0; batch.Active=false; }
            activeExclusions.Clear();
            long coarseStart=Stopwatch.GetTimestamp();
            long lodTicks=0,partTicks=0,topologyTicks=0;
            int partsVisited=0,partsLodSkipped=0,partsStateRejected=0,partsFrustumTested=0,lodGroups=0;
            var secondaryFrustum=SnowCameraFrustum.Prepare(camera,frustum,rightFrustum);
            bool visible=junctions.PrepareVisible(camera,frustum,secondaryFrustum) || extraVisible || rails.HasVisibleTracks(frustum,secondaryFrustum);
            int visibleVehicles=0, snowVehicles=0;
            exclusionVolumes.Begin();
            distantSurfaces.Begin();
            bool useVolumes=ExclusionVolumesEnabled && (ObjectLimiter.Active || !VehicleSnowEnabled) && !camera.stereoEnabled &&
                SystemInfo.supportsInstancing && material.passCount>7;
            foreach(var v in vehicles)
            {
                v.FrameVisible=false;v.FrameSnow=false;v.FrameVolume=false;v.FrameDistant=false;
                if(v.Root==null || v.NativeComplete) continue;
                var center=v.Ready?v.Root.TransformPoint(v.LocalBounds.center):v.Root.position;
                var scale=v.Root.lossyScale;
                float radius=v.Ready?v.LocalBounds.extents.magnitude*Mathf.Max(Mathf.Abs(scale.x),
                    Mathf.Max(Mathf.Abs(scale.y),Mathf.Abs(scale.z)))+3f:40f;
                v.FrameVisible=SnowCameraFrustum.Intersects(frustum,secondaryFrustum,new Bounds(center,Vector3.one*(radius*2)));
                visible|=v.FrameVisible;
                if(v.FrameVisible)
                {
                    visibleVehicles++;
                    v.FrameSnow=v.SnowReady && IsSnowSelected(v);
                    if(DistantSurfacesEnabled && v.FrameSnow && material.passCount>8 && SystemInfo.supportsInstancing)
                        v.FrameDistant=distantSurfaces.Eligible(v,camera);
                    if(v.FrameSnow)snowVehicles++;
                    else if(useVolumes && !IsSnowSelected(v))
                        v.FrameVolume=exclusionVolumes.TryAdd(v,camera);
                }
            }
            SnowPerformance.Elapsed("snow-vehicle-coarse",Stopwatch.GetTimestamp()-coarseStart);
            frameTargetsAllocated=visible;
            if(!visible)
            {
                // Unity 2019 can retain the camera depth attachment even when
                // None is supplied for a 1x1 MRT. No visible surface needs a
                // render target at all: bind an immutable zero pixel instead.
                buffer.SetGlobalTexture(DataId,emptySurfaceData);
                buffer.SetGlobalTexture(SlopeId,Texture2D.blackTexture);
                SnowPerformance.VehicleCulling(0,0,0,0,0);
                return;
            }
            // Keep integer IDs exact in half precision. A separate byte carries
            // slope instead of doubling the entire screen buffer for large yards.
            using(SnowPerformance.Measure("snow-vehicle-surface-prep-record"))
            {
            StereoRenderSupport.GetTemporaryRT(buffer,DataId,camera,RenderTextureFormat.ARGBHalf,FilterMode.Point);
            StereoRenderSupport.GetTemporaryRT(buffer,SlopeId,camera,slopeFormat,FilterMode.Point);
            buffer.SetRenderTarget(dataTargets,BuiltinRenderTextureType.CameraTarget);
            buffer.ClearRenderTarget(false,true,Color.clear);
            // Exact selected-car, rail and junction draws win over approximate
            // exclusions wherever their actual visible geometry overlaps a box.
            exclusionVolumes.Record(buffer,material,camera);
            foreach(var v in vehicles)if(v.FrameDistant)
            {
                snowRemaining[v.Slot]=SnowRemaining!=null?Mathf.Clamp01(SnowRemaining(v.Source)):1;
                sideSnowAmount[v.Slot]=SideSnowAmount!=null?SideSnowAmount(v.Source):Vector4.zero;
                var r=v.Root.rotation;vehicleRotation[v.Slot]=new Vector4(r.x,r.y,r.z,r.w);
                distantSurfaces.Add(buffer,material,v,camera);
            }
            distantSurfaces.Flush(buffer,material,camera);
            junctions.Record(buffer,material);
            rails.Record(buffer,material,frustum,secondaryFrustum);
            }
            useOrderedDraws=OrderedVehicleBatchesEnabled && ExclusionInstancingEnabled &&
                hasSharedSnowMeshes && SystemInfo.supportsInstancing && material.passCount>6 && visibleVehicles>1 &&
                // Exclusion runs already batch cheaply on the native path.
                // When most cars have no full snow, collecting full ordered
                // streams costs more than it saves (verified at limits 0/96/64).
                snowVehicles*2>visibleVehicles-exclusionVolumes.Count;
            frameVehicles.Clear();
            recordedIndex=recordedCutoff=float.NaN;
            var cameraPosition=camera.transform.position;
            bool orthographic=camera.orthographic;
            float lodFactor=QualitySettings.lodBias/(2f*(orthographic?camera.orthographicSize:Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f)));
            float tangent=orthographic?0f:Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*.5f);
            float rayLength=orthographic?1f:Mathf.Sqrt(1f+tangent*tangent*(1f+camera.aspect*camera.aspect));
            if(secondaryFrustum!=null && !orthographic) rayLength=SnowCameraFrustum.MaximumRayLength(camera);
            int cullingMask=camera.cullingMask;
            foreach (var v in vehicles)
            {
                v.FrameParts.Clear();
                if (v.Root == null || v.NativeComplete || v.FrameVolume || v.FrameDistant) continue;
                if(useOrderedDraws)
                {
                    long phaseStart=Stopwatch.GetTimestamp();
                    // Keep the complete registered fleet in stable native order,
                    // including empty streams outside the camera. Only real car
                    // poses/geometry can then change the overlap graph.
                    PrepareTopology(v);
                    v.Topology.Update(v.Root.localToWorldMatrix);
                    v.FrameBounds=v.Topology.World;
                    frameVehicles.Add(v);
                    topologyTicks+=Stopwatch.GetTimestamp()-phaseStart;
                }
                if(!v.FrameVisible)continue;
                // An interior can finish loading between Update and rendering.
                // Exclude its new renderers immediately; its geometry is never snow.
                // Preparation is budgeted in Update; never rebuild every newly
                // streamed interior synchronously from the render callback.
                long lodStart=Stopwatch.GetTimestamp();
                foreach(var lod in PartCacheEnabled?v.PartCache.Lods:v.Lods)
                {
                    lod.FilterInterior=false;
                    if(lod.Group==null) continue;
                    if(PartCacheEnabled && !lod.Group.gameObject.activeInHierarchy)
                    {lod.Current=-1;lod.FilterInterior=true;continue;}
                    lodGroups++;
                    var g=lod.Group;var scale=g.transform.lossyScale;
                    // Native axles and simplified interiors have ordinary,
                    // non-fading groups. Disabled/fading groups retain the old
                    // conservative exclusion path for all their representations.
                    lod.FilterInterior=g.enabled && g.fadeMode==LODFadeMode.None;
                    float lodSize=g.size*Mathf.Max(Mathf.Abs(scale.x),Mathf.Max(Mathf.Abs(scale.y),Mathf.Abs(scale.z)));
                    float height=lodSize*lodFactor;
                    if(!orthographic) height/=Mathf.Max(.01f,Vector3.Distance(cameraPosition,g.transform.TransformPoint(g.localReferencePoint)));
                    lod.Current=lod.Levels.Length;
                    for(int level=0;level<lod.Levels.Length;level++)
                        if(height>=lod.Levels[level].screenRelativeTransitionHeight) {lod.Current=level;break;}
                }
                lodTicks+=Stopwatch.GetTimestamp()-lodStart;
                long partsStart=Stopwatch.GetTimestamp();
                var visibleBounds=new Bounds();bool haveBounds=false;
                float minX=float.PositiveInfinity,minY=float.PositiveInfinity,minZ=float.PositiveInfinity;
                float maxX=float.NegativeInfinity,maxY=float.NegativeInfinity,maxZ=float.NegativeInfinity;
                foreach(var part in PartCacheEnabled?v.PartCache.Select():v.Parts)
                {
                    partsVisited++;
                    var renderer=part.Renderer;
                    if(!part.HasOpaque || part.NativeComplete) continue;
                    if(part.Lod!=null && (!part.Interior || part.Lod.FilterInterior) && !part.UsesLod(part.Lod.Current))
                    {partsLodSkipped++;continue;}
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                        renderer.forceRenderingOff || (cullingMask & (1<<renderer.gameObject.layer))==0)
                    {partsStateRejected++;continue;}
                    partsFrustumTested++;
                    var partBounds=renderer.bounds;
                    if(!SnowCameraFrustum.Intersects(frustum,secondaryFrustum,partBounds)) continue;
                    part.VisibleBounds=partBounds;
                    v.FrameParts.Add(part);
                    if(useOrderedDraws)
                    {
                        // Bounds.Encapsulate constructs several temporary
                        // vectors twice per part in Unity 2019. Accumulate the
                        // six extrema directly, then construct one car bound.
                        var center=partBounds.center;var extents=partBounds.extents;
                        float low=center.x-extents.x,high=center.x+extents.x;
                        if(low<minX)minX=low;if(high>maxX)maxX=high;
                        low=center.y-extents.y;high=center.y+extents.y;
                        if(low<minY)minY=low;if(high>maxY)maxY=high;
                        low=center.z-extents.z;high=center.z+extents.z;
                        if(low<minZ)minZ=low;if(high>maxZ)maxZ=high;
                        haveBounds=true;
                    }
                }
                partTicks+=Stopwatch.GetTimestamp()-partsStart;
                if(useOrderedDraws && haveBounds)
                {
                    long phaseStart=Stopwatch.GetTimestamp();
                    // Validate the stable envelope against actual visible geometry
                    // (including animated/detached parts), but do not replace it
                    // with a camera-dependent subset of the vehicle.
                    visibleBounds.SetMinMax(new Vector3(minX,minY,minZ),new Vector3(maxX,maxY,maxZ));
                    float distance=Vector3.Distance(cameraPosition,visibleBounds.center)+visibleBounds.extents.magnitude;
                    float depthStep=orthographic?camera.farClipPlane-camera.nearClipPlane:
                        Mathf.Abs(1f/camera.nearClipPlane-1f/camera.farClipPlane)*distance*distance;
                    float padding=rayLength*(Mathf.Max(.015f,distance*.00005f)+2f/16777215f*depthStep);
                    if(!v.Topology.ContainsGeometry(visibleBounds))
                    {
                        var worldToLocal=v.Root.worldToLocalMatrix;
                        foreach(var part in v.FrameParts)IncludeTopologyPart(v,part,worldToLocal);
                        v.Topology.Update(v.Root.localToWorldMatrix);
                    }
                    v.Topology.EnsureContains(visibleBounds,padding);
                    v.FrameBounds=v.Topology.World;
                    topologyTicks+=Stopwatch.GetTimestamp()-phaseStart;
                }
                if(!useOrderedDraws && v.FrameParts.Count>0) frameVehicles.Add(v);
            }
            SnowPerformance.Elapsed("snow-vehicle-lod",lodTicks);
            SnowPerformance.Elapsed("snow-vehicle-parts",partTicks);
            SnowPerformance.Elapsed("snow-vehicle-topology",topologyTicks);
            SnowPerformance.VehicleCulling(partsVisited,partsLodSkipped,partsStateRejected,partsFrustumTested,lodGroups);
            }
            // The scheduler retains native order on actual overlap edges and
            // batches ready independent streams, including streams belonging
            // to the same connected component.
            using(SnowPerformance.Measure("snow-part-batch-selection"))
                FrameUsesPartBatches=SelectPartBatching();
            orderedDraws=FrameUsesPartBatches?partDraws:vehicleDraws;
            if(useOrderedDraws && !FrameUsesPartBatches && OrientedVehicleBatchesEnabled)
                using(SnowPerformance.Measure("snow-obb-validation")) ValidateOrientedVehicles();
            using(SnowPerformance.Measure("snow-vehicle-request-build"))
            {
            if(useOrderedDraws) orderedDraws.Begin(buffer,material);
            foreach(var v in frameVehicles)
            {
                if(useOrderedDraws && !FrameUsesPartBatches) orderedDraws.BeginOrientedVehicle(v.FrameBounds,
                    OrientedVehicleBatchesEnabled?v.Oriented.World:default(SnowOrientedBounds));
                if(v.FrameParts.Count==0)continue;
                bool allowSnow=v.FrameSnow;
                if(allowSnow)
                {
                    snowRemaining[v.Slot]=SnowRemaining!=null?Mathf.Clamp01(SnowRemaining(v.Source)):1f;
                    // Logical accumulation belongs to the car, independently of
                    // camera distance, LOD changes and the visible-object limit.
                    sideSnowAmount[v.Slot]=SideSnowAmount!=null?SideSnowAmount(v.Source):Vector4.zero;
                    var rotation=v.Root.rotation;
                    vehicleRotation[v.Slot]=new Vector4(rotation.x,rotation.y,rotation.z,rotation.w);
                }
                var vehicleMatrix=allowSnow?v.Root.worldToLocalMatrix:Matrix4x4.identity;
                if(!useOrderedDraws && allowSnow) buffer.SetGlobalMatrix(WorldToLocalId,vehicleMatrix);
                bool customMatrix=false;
                for(int partIndex=0;partIndex<v.FrameParts.Count;partIndex++)
                {
                    var part=v.FrameParts[partIndex];
                    var combined=CombinedMeshesEnabled && !FrameUsesPartBatches?v.MeshCache.Find(part):null;
                    if(combined!=null && combined.Matches(v.FrameParts,partIndex,v.Root.worldToLocalMatrix))
                    {
                        bool snowPart=allowSnow && !part.Interior;
                        if(useOrderedDraws)
                        {
                            var rootMatrix=v.Root.localToWorldMatrix;
                            orderedDraws.AddPrepared(combined.Metadata,snowPart?0:5,ref rootMatrix,ref vehicleMatrix,snowPart?v.Slot+1f:-1f,true);
                        }
                        else
                        {
                            FlushExclusions(buffer);
                            buffer.SetGlobalMatrix(WorldToLocalId,vehicleMatrix);
                            buffer.SetGlobalFloat(IndexId,snowPart?v.Slot+1f:-1f);
                            buffer.SetGlobalFloat(CutoffId,0);
                            buffer.DrawMesh(combined.Mesh,v.Root.localToWorldMatrix,material,0,snowPart?0:5);
                        }
                        FrameCombinedCommands++;
                        recordedCutoff=0;recordedIndex=snowPart?v.Slot+1f:-1f;customMatrix=false;
                        FrameDrawCount++;if(snowPart)FrameSnowDrawCount++;else FrameExclusionDrawCount++;
                        partIndex+=combined.Parts.Length-1;continue;
                    }
                    if(FrameUsesPartBatches)
                        orderedDraws.BeginVehicle(part.DrawStreamBounds);
                    bool partSnow=allowSnow && !part.Interior;
                    currentWorldToVehicle=vehicleMatrix;
                    if(partSnow)
                    {
                        if(part.MovingFrame!=null)
                        {
                            currentWorldToVehicle=part.MovingFrame.WorldToCapturedVehicle;
                            if(!useOrderedDraws) buffer.SetGlobalMatrix(WorldToLocalId,currentWorldToVehicle);
                            customMatrix=true;
                        }
                        else if(customMatrix && !useOrderedDraws)
                        { buffer.SetGlobalMatrix(WorldToLocalId,vehicleMatrix); customMatrix=false; }
                    }
                    DrawPart(buffer,part,0,partSnow ? v.Slot+1f : -1f);
                }
            }
            }
            // End owns snow-scheduler-plan-record (and its nested uploads).
            // Keep it outside both request construction and post-record work.
            if(useOrderedDraws)
            {
                orderedDraws.End();
                if(!FrameUsesPartBatches)
                {lastVehicleCommands=vehicleDraws.CommandCount;lastVehicleRequests=FrameDrawCount;}
                else if(partDraws.CommandCount*4>=lastVehicleCommands*3)
                    rejectedPartLayout=true;
            }
            using(SnowPerformance.Measure("snow-vehicle-post-record"))
            {
            if(useOrderedDraws)
            {
                SnowPerformance.SurfaceGranularity(FrameUsesPartBatches);
                FrameInstancedExclusionCount=orderedDraws.ExclusionInstancedObjectCount;
                FrameExclusionBatchCount=orderedDraws.ExclusionBatchCount;
                FrameInstancedFullCount=orderedDraws.FullInstancedObjectCount;
                FrameFullBatchCount=orderedDraws.FullBatchCount;
                SnowPerformance.VehicleBatching(orderedDraws.ComponentCount,orderedDraws.LargestComponent,
                    orderedDraws.ComponentVehicleCount,orderedDraws.MultiVehicleComponentCount,
                    eligibleFull,uniqueMeshFull,skinnedFull,staticFull,mirroredFull,streamsFull,otherFull,orderedDraws.UniqueKeyDraws);
                SnowPerformance.SchedulerBatching(orderedDraws.ReadyStreamSamples,orderedDraws.ReadyStreamTotal,
                    orderedDraws.ReadyStreamMaximum,orderedDraws.FullBatchMaximum);
                SnowPerformance.ActiveGraph(orderedDraws.ActiveStreamCount,orderedDraws.ActiveEdgeCount,
                    orderedDraws.EmptyStreamCount,orderedDraws.EmptyBridgeEdgeCount);
            }
            useOrderedDraws=false;
            FlushExclusions(buffer);
            // Only positive surface IDs sample this array. Off-screen and capped
            // cars do not need thermal lookups or a per-frame array upload.
            if(FrameSnowDrawCount>0 || distantSurfaces.Cars>0)
            {
                buffer.SetGlobalFloatArray("_DVPSVehicleSnowRemaining",snowRemaining);
                buffer.SetGlobalVectorArray("_DVPSVehicleSideSnow",sideSnowAmount);
                buffer.SetGlobalVectorArray("_DVPSVehicleRotation",vehicleRotation);
            }
            buffer.SetGlobalTexture(DataId,DataId);
            buffer.SetGlobalTexture(SlopeId,SlopeId);
            buffer.SetGlobalTexture("_DVPSVehicleHeights",heights);
            buffer.SetGlobalTexture("_DVPSVehicleSnow",snow);
            buffer.SetGlobalVectorArray("_DVPSVehicleAreas",areas);
            buffer.SetGlobalVectorArray("_DVPSVehicleSnowAreas",snowAreas);
            }
        }

        private void DrawPart(CommandBuffer buffer,Part part,int pass,float vehicleIndex=-1)
        {
            if(part.Mesh == null) return;
            for (var slot=0;slot<part.Opaque.Length;slot++)
            {
                if (!part.Opaque[slot]) continue;
                if(pass==0 && part.NativeSlots!=null && part.NativeSlots[slot])continue;
                int drawPass=pass;
                float index=vehicleIndex;
                if(pass==0)
                {
                    index=vehicleIndex>0 && HasNativeSnowTexture!=null && HasNativeSnowTexture(part.Renderer,slot)?-1:vehicleIndex;
                    if(index==-1f) drawPass=5;
                    else if(!useOrderedDraws && index!=recordedIndex) {buffer.SetGlobalFloat(IndexId,index);recordedIndex=index;}
                }
                if(pass==0 && useOrderedDraws)
                {
                    bool canInstance=PrepareInstance(part) && part.ExclusionBatches!=null;
                    if(drawPass==0)
                    {
                        if(canInstance)eligibleFull++;
                        else if(part.InstanceReason==1)skinnedFull++;
                        else if(part.InstanceReason==2)staticFull++;
                        else if(part.InstanceReason==3)streamsFull++;
                        else if(part.InstanceReason==4)mirroredFull++;
                        else if(part.InstanceReason==0)uniqueMeshFull++;
                        else otherFull++;
                    }
                    if(PreparedVehicleRequestsEnabled && part.DrawMetadata!=null && part.DrawMetadata[slot]!=null)
                        orderedDraws.AddPrepared(part.DrawMetadata[slot],drawPass,ref part.ExclusionMatrix,ref currentWorldToVehicle,index,canInstance);
                    else orderedDraws.Add(part.Renderer,part.Mesh,slot,drawPass,part.Cutoff[slot],part.Albedo[slot],part.ST[slot],
                        part.ExclusionMatrix,currentWorldToVehicle,index,canInstance);
                    FrameDrawCount++;
                    if(drawPass==5) FrameExclusionDrawCount++;else FrameSnowDrawCount++;
                    continue;
                }
                if(drawPass==5 && QueueExclusion(buffer,part,slot))
                {
                    FrameDrawCount++; FrameExclusionDrawCount++;
                    continue;
                }
                // A detailed surface draw or an unsupported exclusion renderer
                // must retain its position relative to all preceding writes.
                if(pass==0) FlushExclusions(buffer);
                if(recordedCutoff!=part.Cutoff[slot])
                {buffer.SetGlobalFloat(CutoffId,part.Cutoff[slot]);recordedCutoff=part.Cutoff[slot];}
                if(part.Cutoff[slot]>0)
                {
                    buffer.SetGlobalTexture(AlbedoId,part.Albedo[slot]!=null?part.Albedo[slot]:Texture2D.whiteTexture);
                    buffer.SetGlobalVector(STId,part.ST[slot]);
                }
                buffer.DrawRenderer(part.Renderer,material,slot,drawPass);
                if(pass==0)
                {
                    FrameDrawCount++;
                    if(drawPass==5) FrameExclusionDrawCount++; else FrameSnowDrawCount++;
                }
            }
        }
        private bool SelectPartBatching()
        {
            // Merged meshes already reduce each rigid run. Retain the vehicle
            // DAG/cache so other model parts still instance across the yard.
            if(CombinedMeshesEnabled)
                foreach(var vehicle in frameVehicles)if(vehicle.MeshCache.Count>0)return false;
            int count=0;
            foreach(var vehicle in frameVehicles)count+=vehicle.FrameParts.Count;
            // Fine ordering only pays when the existing vehicle plan still emits
            // many commands. Keep its independent cache warm through movement,
            // LOD/interior changes and exceptionally dense views.
            if(!PartVehicleBatchesEnabled || !useOrderedDraws || count<2 || count>MaximumPartStreams ||
                lastVehicleCommands<=64 || lastVehicleCommands*16<=lastVehicleRequests)
            {stablePartSubmissions=0;stablePartLayout.Clear();rejectedPartLayout=false;return false;}
            bool changed=stablePartLayout.Count!=count;
            int index=0;
            foreach(var vehicle in frameVehicles)foreach(var part in vehicle.FrameParts)
            {
                if(index>=stablePartLayout.Count)stablePartLayout.Add(part);
                else if(!ReferenceEquals(stablePartLayout[index],part))
                {changed=true;stablePartLayout[index]=part;}
                index++;
                var bounds=PartDrawEnvelope(part,vehicle);
                if(!part.DrawStreamBoundsKnown || !part.DrawStreamBounds.Equals(bounds))
                {changed=true;part.DrawStreamBounds=bounds;part.DrawStreamBoundsKnown=true;}
            }
            if(index<stablePartLayout.Count)stablePartLayout.RemoveRange(index,stablePartLayout.Count-index);
            if(changed) {stablePartSubmissions=0;rejectedPartLayout=false;return false;}
            if(rejectedPartLayout)return false;
            if(stablePartSubmissions<8)stablePartSubmissions++;
            return stablePartSubmissions>=8;
        }
        private Bounds PartDrawEnvelope(Part part,Vehicle vehicle)
        {
            // Native fallbacks may submit geometry outside the ordinary mesh
            // transform (skinning, static batches or extra vertex streams).
            // Preserve the proven vehicle barrier for these exceptional parts.
            if(GeometryReason(part)!=0)return vehicle.FrameBounds;
            var required=part.VisibleBounds;
            required.Expand(2f*vehicle.Topology.Padding);
            var c=required.center;var e=required.extents;
            var old=part.DrawEnvelope.center;var margin=part.DrawEnvelope.extents;
            if(!part.DrawEnvelopeKnown ||
                (double)c.x-e.x<(double)old.x-margin.x || (double)c.x+e.x>(double)old.x+margin.x ||
                (double)c.y-e.y<(double)old.y-margin.y || (double)c.y+e.y>(double)old.y+margin.y ||
                (double)c.z-e.z<(double)old.z-margin.z || (double)c.z+e.z>(double)old.z+margin.z)
            {
                required.Expand(.5f);
                part.DrawEnvelope=required;part.DrawEnvelopeKnown=true;
            }
            return part.DrawEnvelope;
        }
        private bool QueueExclusion(CommandBuffer buffer,Part part,int slot)
        {
            if(!ExclusionInstancingEnabled || !SystemInfo.supportsInstancing) return false;
            var batch=part.ExclusionBatches!=null?part.ExclusionBatches[slot]:null;
            if(batch==null) return false;
            if(!PrepareInstance(part)) return false;
            if(!batch.Active) { batch.Active=true;activeExclusions.Add(batch); }
            if(batch.Count==batch.Matrices.Length)
                Array.Resize(ref batch.Matrices,Math.Min(1023,batch.Matrices.Length*2));
            if(batch.Count==0) batch.FirstRenderer=part.Renderer;
            batch.Matrices[batch.Count++]=part.ExclusionMatrix;
            if(batch.Count==1023) RecordExclusionBatch(buffer,batch);
            return true;
        }
        private bool PrepareInstance(Part part)
        {
            if(part.ExclusionFrame!=exclusionFrame)
            {
                part.ExclusionFrame=exclusionFrame;
                part.InstanceReason=GeometryReason(part);
                part.ExclusionAllowed=part.InstanceReason==0;
                if(part.ExclusionAllowed)
                {
                    var matrix=ObjectMatrix(part);
                    if(!part.ExclusionMatrixKnown || !SnowVehicleDrawScheduler.SameMatrix(ref part.ExclusionMatrix,ref matrix))
                    {
                        part.ExclusionMatrix=matrix;part.ExclusionMatrixKnown=true;
                        // Keep native renderer culling for mirrored transforms.
                        part.ExclusionPositive=matrix.determinant>0f;
                    }
                    part.ExclusionAllowed=part.ExclusionPositive;
                    if(!part.ExclusionPositive)part.InstanceReason=4;
                }
            }
            return part.ExclusionAllowed;
        }
        private Matrix4x4 ObjectMatrix(Part part)
        {
            if(part.ObjectMatrixFrame!=exclusionFrame)
            {part.ObjectMatrixFrame=exclusionFrame;part.ObjectMatrix=part.Renderer.localToWorldMatrix;}
            return part.ObjectMatrix;
        }
        private bool IsCurrentMesh(Part part)
        {
            if(part.MeshValidationFrame!=exclusionFrame)
            {part.MeshValidationFrame=exclusionFrame;part.MeshCurrent=part.Filter!=null && part.Filter.sharedMesh==part.Mesh;}
            return part.MeshCurrent;
        }
        private int GeometryReason(Part part)
        {
            if(part.GeometryValidationFrame!=exclusionFrame)
            {
                part.GeometryValidationFrame=exclusionFrame;
                var renderer=part.Renderer as MeshRenderer;
                part.GeometryReason=part.Renderer is SkinnedMeshRenderer?1:renderer==null?5:
                    renderer.isPartOfStaticBatch?2:renderer.additionalVertexStreams!=null?3:!IsCurrentMesh(part)?5:0;
            }
            return part.GeometryReason;
        }
        private void ValidateOrientedVehicles()
        {
            // Native world bounds were already read for visibility. When the
            // complete AABB plus shader padding fits, all its geometry is safe
            // without consulting parents, animation, meshes or vertex streams.
            // A large rotated body may not fit this test; retain the exact
            // mesh/localBounds path for it instead of widening its OBB.
            orientedMeshBounds.Clear();
            try
            {
                foreach(var vehicle in frameVehicles)
                {
                    vehicle.Oriented.Update(vehicle.Topology.Local,vehicle.Root.localToWorldMatrix,vehicle.Topology.Padding);
                    var box=vehicle.Oriented.World;
                    if(!box.Valid)continue;
                    foreach(var part in vehicle.FrameParts)
                    {
                        int revision=vehicle.Oriented.Revision;float padding=vehicle.Topology.Padding;
                        // A failed guard only loses an optimization. Keep that
                        // result until the box changes, even if the part moves;
                        // its exact geometry is still validated below. Successful
                        // guards always check this submit's current world bounds.
                        bool rejected=part.OrientedGuardRejected && part.OrientedGuardRevision==revision && part.OrientedGuardPadding==padding;
                        if(!rejected && ContainsOrientedWorldBounds(ref box,ref part.VisibleBounds,padding))
                            FrameOrientedFastCount++;
                        else
                        {
                            part.OrientedGuardRejected=true;part.OrientedGuardRevision=revision;part.OrientedGuardPadding=padding;
                            FrameOrientedDetailedCount++;
                            EnsureOrientedPart(vehicle,part);
                            box=vehicle.Oriented.World;
                        }
                    }
                }
            }
            finally {orientedMeshBounds.Clear();}
        }
        private static bool ContainsOrientedWorldBounds(ref SnowOrientedBounds box,ref Bounds bounds,float padding)
        {
            if(!box.Valid)return false;
            var center=bounds.center;var extent=bounds.extents;
            // Compute the three AABB projection intervals in double precision.
            // No epsilon accepts an escaping corner, including distant yards.
            double x=(double)center.x-box.Center.x,y=(double)center.y-box.Center.y,z=(double)center.z-box.Center.z;
            var axis=box.AxisX;
            if(!(Math.Abs(x*axis.x+y*axis.y+z*axis.z)+(double)Math.Abs(axis.x)*extent.x+
                (double)Math.Abs(axis.y)*extent.y+(double)Math.Abs(axis.z)*extent.z+padding<=box.Extents.x))return false;
            axis=box.AxisY;
            if(!(Math.Abs(x*axis.x+y*axis.y+z*axis.z)+(double)Math.Abs(axis.x)*extent.x+
                (double)Math.Abs(axis.y)*extent.y+(double)Math.Abs(axis.z)*extent.z+padding<=box.Extents.y))return false;
            axis=box.AxisZ;
            return Math.Abs(x*axis.x+y*axis.y+z*axis.z)+(double)Math.Abs(axis.x)*extent.x+
                (double)Math.Abs(axis.y)*extent.y+(double)Math.Abs(axis.z)*extent.z+padding<=box.Extents.z;
        }
        private void EnsureOrientedPart(Vehicle vehicle,Part part)
        {
            var renderer=part.Renderer;
            var skin=renderer as SkinnedMeshRenderer;
            Bounds source;Matrix4x4 matrix;
            if(skin!=null)
            {source=skin.localBounds;matrix=ObjectMatrix(part);}
            else if(part.Mesh!=null && GeometryReason(part)==0)
            {
                // A shared mesh cannot mutate while this synchronous camera
                // submission runs. Re-read next submit to catch bounds changes
                // even when the mesh reference itself stays the same.
                if(!orientedMeshBounds.TryGetValue(part.Mesh,out source))
                {source=part.Mesh.bounds;orientedMeshBounds.Add(part.Mesh,source);FrameOrientedMeshBoundsReadCount++;}
                matrix=ObjectMatrix(part);
            }
            else
            {
                // Combined meshes and custom vertex streams can exceed the base
                // mesh. Native bounds stay conservative for these rare parts.
                source=part.VisibleBounds;matrix=Matrix4x4.identity;
            }
            float padding=vehicle.Topology.Padding;
            if(part.OrientedChecked && part.OrientedRevision==vehicle.Oriented.Revision && part.OrientedPadding==padding &&
                part.OrientedSource.Equals(source) && SnowVehicleDrawScheduler.SameMatrix(ref part.OrientedMatrix,ref matrix))return;
            vehicle.Oriented.EnsureContains(source,matrix,padding);
            part.OrientedChecked=true;part.OrientedRevision=vehicle.Oriented.Revision;part.OrientedPadding=padding;
            part.OrientedSource=source;part.OrientedMatrix=matrix;
        }
        private void RefreshExclusionMeshUses()
        {
            // Unique meshes cannot join an instance batch. Topology changes
            // are the only reason to rebuild this index; ordinary camera and
            // vehicle motion need neither matrix reads nor grouping for them.
            exclusionMeshUses.Clear();
            foreach(var vehicle in vehicles)
                foreach(var part in vehicle.PartCache.FallbackParts)
                {
                    if(part.Renderer==null || part.Mesh==null) continue;
                    int count;
                    exclusionMeshUses.TryGetValue(part.Mesh,out count);
                    if(count<2) exclusionMeshUses[part.Mesh]=count+1;
                }
            // Each part points directly to its reusable group. Per-frame draws
            // no longer construct/hash keys or look up groups for every submesh.
            exclusionByKey.Clear();exclusionBatchCount=0;activeExclusions.Clear();hasSharedSnowMeshes=false;
            foreach(var vehicle in vehicles)
                foreach(var part in vehicle.PartCache.FallbackParts)
                {
                    part.ExclusionFrame=exclusionFrame;
                    int uses;
                    // Unity keeps the managed MeshRenderer wrapper after its
                    // native object is destroyed during car/interior streaming.
                    // A CLR `is` check alone still succeeds on that dead wrapper.
                    if(part.Renderer==null || part.Mesh==null || !(part.Renderer is MeshRenderer) ||
                        !exclusionMeshUses.TryGetValue(part.Mesh,out uses) || uses<2)
                    {part.ExclusionBatches=null;continue;}
                    if(!part.Interior && !part.Renderer.isPartOfStaticBatch) hasSharedSnowMeshes=true;
                    if(part.ExclusionBatches==null || part.ExclusionBatches.Length!=part.Opaque.Length)
                        part.ExclusionBatches=new ExclusionBatch[part.Opaque.Length];
                    else Array.Clear(part.ExclusionBatches,0,part.ExclusionBatches.Length);
                    for(int slot=0;slot<part.Opaque.Length;slot++)
                    {
                        if(!part.Opaque[slot] || (part.NativeSlots!=null && part.NativeSlots[slot])) continue;
                        bool cutout=part.Cutoff[slot]>0f;
                        var key=new ExclusionKey {Mesh=part.Mesh,Submesh=slot,Cutoff=part.Cutoff[slot],
                            Albedo=cutout?part.Albedo[slot]:null,ST=cutout?part.ST[slot]:Vector4.zero};
                        ExclusionBatch batch;
                        if(!exclusionByKey.TryGetValue(key,out batch))
                        {
                            if(exclusionBatchCount==exclusionPool.Count) exclusionPool.Add(new ExclusionBatch());
                            batch=exclusionPool[exclusionBatchCount++];batch.Key=key;batch.Count=0;batch.Active=false;batch.FirstRenderer=null;
                            exclusionByKey.Add(key,batch);
                        }
                        part.ExclusionBatches[slot]=batch;
                    }
                }
            // Do not retain removed cargo/materials in unused pooled entries.
            for(int i=exclusionBatchCount;i<exclusionPool.Count;i++)
            {var batch=exclusionPool[i];batch.Key=default(ExclusionKey);batch.FirstRenderer=null;batch.Count=0;batch.Active=false;}
            ExclusionCacheBuildCount++;
            exclusionMeshUsesDirty=false;
        }
        private void RecordExclusionBatch(CommandBuffer buffer,ExclusionBatch batch)
        {
            if(batch.Count==0) return;
            var key=batch.Key;
            buffer.SetGlobalFloat(CutoffId,key.Cutoff);
            if(key.Cutoff>0f)
            {
                buffer.SetGlobalTexture(AlbedoId,key.Albedo!=null?key.Albedo:Texture2D.whiteTexture);
                buffer.SetGlobalVector(STId,key.ST);
            }
            if(batch.Count==1) buffer.DrawRenderer(batch.FirstRenderer,material,key.Submesh,5);
            else
            {
                buffer.DrawMeshInstanced(key.Mesh,key.Submesh,material,5,batch.Matrices,batch.Count);
                FrameExclusionBatchCount++; FrameInstancedExclusionCount+=batch.Count;
            }
            recordedCutoff=key.Cutoff;
            batch.Count=0;
        }
        private void FlushExclusions(CommandBuffer buffer)
        {
            foreach(var batch in activeExclusions) { RecordExclusionBatch(buffer,batch);batch.Active=false; }
            activeExclusions.Clear();
        }
        public void ReleaseFrame(CommandBuffer buffer)
        { if(frameTargetsAllocated) {buffer.ReleaseTemporaryRT(DataId);buffer.ReleaseTemporaryRT(SlopeId);} }
        private void Unsubscribe(Vehicle v)
        {
            nativeMaterials.Release(v);
            v.MeshCache.Dispose();
            if(v.Source != null && v.InteriorLoadedHandler != null)
            {
                trainType?.GetEvent("InteriorLoaded")?.RemoveEventHandler(v.Source,v.InteriorLoadedHandler);
                trainType?.GetEvent("ExternalInteractableLoaded")?.RemoveEventHandler(v.Source,v.InteriorLoadedHandler);
            }
            v.Source=null; v.InteriorLoadedHandler=null;
        }
        public void Dispose()
        {
            snapshotGeneration++; savedMasks.Clear(); pendingSnapshots.Clear();
            RestoreAfterStaticCapture();
            junctions.Dispose();
            ObjectLimiter.Dispose();
            vehicleDraws.Dispose();partDraws.Dispose();frameVehicles.Clear();useOrderedDraws=false;
            stablePartLayout.Clear();stablePartSubmissions=lastVehicleCommands=lastVehicleRequests=0;
            FrameUsesPartBatches=rejectedPartLayout=false;
            exclusionVolumes.Dispose();
            distantSurfaces.Dispose();
            nativeMaterials.Dispose();
            if(capture != null) { capture.Dispose(); capture=null; }
            foreach(var v in vehicles) Unsubscribe(v);
            foreach(var resource in new UnityEngine.Object[]{material,heights,heightSlice,snow,snowSlice,snowQuad,emptySurfaceData})
                if (resource != null) { if(Application.isPlaying) UnityEngine.Object.Destroy(resource); else UnityEngine.Object.DestroyImmediate(resource); }
            material=null; heights=heightSlice=null; vehicles.Clear(); byId.Clear(); staticVehicles.Clear();Array.Clear(usedSlots,0,usedSlots.Length); nextScan=0; cameraPositionKnown=false; lastCameraPosition=Vector3.zero; Revision++;
            snow=snowSlice=null;snowQuad=null;nextSnowVehicle=0;
            emptySurfaceData=null;frameTargetsAllocated=false;
            FrameDrawCount=FrameSnowDrawCount=FrameExclusionDrawCount=0;
            FrameExclusionBatchCount=FrameInstancedExclusionCount=0;
            FrameFullBatchCount=FrameInstancedFullCount=0;
            FrameOrientedFastCount=FrameOrientedDetailedCount=FrameOrientedMeshBoundsReadCount=0;
            orientedMeshBounds.Clear();
            exclusionByKey.Clear(); exclusionPool.Clear(); exclusionBatchCount=0;
            activeExclusions.Clear();exclusionFrame=0;ExclusionCacheBuildCount=0;
            exclusionMeshUses.Clear(); exclusionMeshUsesDirty=true;
            textureCapacity=0;nextCargoVehicle=0;
            discoveryCars.Clear();discoveryMoving.Clear();discoveryAllLive.Clear();discoveryLive.Clear();
        }
    }
}
