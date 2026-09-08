using System;
using System.Collections.Generic;
using System.Reflection;
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
        }
        private sealed class StaticVehicle
        {
            public Transform Root, Interior, InteriorLod;
        }
        internal sealed class Part
        {
            public Renderer Renderer;
            public Material[] Materials;
            public Mesh Mesh;
            public bool Interior;
            public LodSet Lod;
            public int LodIndex;
            public bool[] Opaque;
            public float[] Cutoff;
            public Texture[] Albedo;
            public Vector4[] ST;
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
            public int Slot;
            public bool Dirty = true;
            public bool Ready;
            public bool SnowReady;
            public bool SnowShapeDirty;
            public Vector4 SnowArea;
            public float LastSnowClock;
            public bool FrameVisible;
            public int RootId;
            public Vector4 Area;
            public Vector4 Vertical;
            public Bounds LocalBounds;
            public int Signature;
            public Component Source;
            public Action<GameObject> InteriorLoadedHandler;
            public bool PartsPending;
            public bool InteriorPending;
        }
        private const int Capacity = 32;
        private readonly List<Vehicle> vehicles = new List<Vehicle>();
        private readonly Dictionary<int, Vehicle> byId = new Dictionary<int, Vehicle>();
        private readonly Dictionary<int,StaticVehicle> staticVehicles=new Dictionary<int,StaticVehicle>();
        private readonly List<int> departed=new List<int>();
        private readonly bool[] usedSlots=new bool[Capacity];
        public int FrameDrawCount { get; private set; }
        private readonly List<Renderer> hidden = new List<Renderer>();
        private readonly Plane[] frustum = new Plane[6];
        private readonly Vector4[] areas = new Vector4[Capacity];
        private readonly Vector4[] snowAreas = new Vector4[Capacity];
        private readonly float[] snowRemaining = new float[Capacity];
        public Func<Component,float> SnowRemaining;
        public Func<Renderer,int,bool> HasNativeSnowTexture;
        private CommandBuffer capture;
        private Material material;
        private RenderTexture heights;
        private RenderTexture heightSlice;
        private RenderTexture snow, snowSlice;
        private Mesh snowQuad;
        private int nextSnowVehicle;
        private float nextScan;
        private Type trainType;
        public Func<IEnumerable<Component>> DiscoverVehicles;
        private static readonly int DataId = Shader.PropertyToID("_DVPSVehicleData");
        public int Revision { get; private set; }
        public int CaptureCount { get; private set; }

        public bool Initialize(SeasonAssetBundleRepository repository)
        {
            if (material != null) return true;
            var shader = repository.LoadShader("SnowVehicle");
            if (shader == null || !SystemInfo.supports2DArrayTextures) return false;
            capture = new CommandBuffer { name = "DVSeasons vehicle snow cache" };
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            heights = new RenderTexture(256,256,0,RenderTextureFormat.RFloat,RenderTextureReadWrite.Linear)
            { dimension = TextureDimension.Tex2DArray, volumeDepth = Capacity, filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            heights.Create();
            heightSlice = new RenderTexture(256,256,24,RenderTextureFormat.RFloat,RenderTextureReadWrite.Linear)
            { hideFlags = HideFlags.HideAndDontSave };
            heightSlice.Create();
            snow = new RenderTexture(256,256,0,RenderTextureFormat.RHalf,RenderTextureReadWrite.Linear)
            { dimension=TextureDimension.Tex2DArray,volumeDepth=Capacity,filterMode=FilterMode.Bilinear,
                wrapMode=TextureWrapMode.Clamp,hideFlags=HideFlags.HideAndDontSave };
            snow.Create();
            snowSlice = new RenderTexture(256,256,0,RenderTextureFormat.RHalf,RenderTextureReadWrite.Linear)
            { hideFlags=HideFlags.HideAndDontSave }; snowSlice.Create();
            snowQuad=new Mesh { vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)},
                uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up},triangles=new[]{0,1,2,0,2,3},hideFlags=HideFlags.HideAndDontSave };
            return true;
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
                usedSlots[slot]=true;
                vehicle = new Vehicle { Root = root, RootId=root.GetInstanceID(), Slot = slot };
                vehicles.Add(vehicle); byId.Add(root.GetInstanceID(),vehicle); Revision++;
            }
            bool changed=vehicle.Interior!=interior || vehicle.InteriorLod!=interiorLod || vehicle.Parts.Count==0;
            vehicle.Interior = interior; vehicle.InteriorLod = interiorLod;
            if(changed) vehicle.PartsPending=true;
            TrackStaticRoot(root,interior,interiorLod);
        }

        public void Update(Camera camera)
        {
            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan = Time.realtimeSinceStartup+5f;
                if (trainType == null)
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    { trainType = assembly.GetType("TrainCar",false); if (trainType != null) break; }
                if (trainType != null)
                {
                    var candidates = new List<Component>();
                    var allLive=new HashSet<int>();
                    foreach (var item in DiscoverVehicles != null ? DiscoverVehicles() : Array.Empty<Component>())
                    {
                        var component = item as Component;
                        if (component == null || !component.gameObject.scene.IsValid() ||
                            !component.gameObject.activeInHierarchy) continue;
                        allLive.Add(component.transform.GetInstanceID());
                        TrackStaticRoot(component.transform,ReadTransform(component,"interior"),ReadTransform(component,"interiorLOD"));
                        if ((component.transform.position-camera.transform.position).sqrMagnitude > 90000f) continue;
                        candidates.Add(component);
                    }
                    departed.Clear();
                    foreach(var pair in staticVehicles) if(!allLive.Contains(pair.Key)) departed.Add(pair.Key);
                    foreach(var id in departed) staticVehicles.Remove(id);
                    candidates.Sort((a,b) => (a.transform.position-camera.transform.position).sqrMagnitude.CompareTo(
                        (b.transform.position-camera.transform.position).sqrMagnitude));
                    var live = new HashSet<int>();
                    for(var i=0;i<Mathf.Min(Capacity,candidates.Count);i++) live.Add(candidates[i].GetInstanceID());
                    // Remove departed/deleted cars before assigning free array slices.
                    for (var i=vehicles.Count-1;i>=0;i--)
                    {
                        var v=vehicles[i];
                        var component=v.Root != null ? v.Root.gameObject.GetComponent(trainType) : null;
                        if (component != null && live.Contains(component.GetInstanceID())) continue;
                        Unsubscribe(v);
                        usedSlots[v.Slot]=false;
                        byId.Remove(v.RootId);
                        vehicles.RemoveAt(i); Revision++;
                    }
                    for(var i=0;i<Mathf.Min(Capacity,candidates.Count);i++)
                    {
                        var item=candidates[i];
                        Register(item.transform,ReadTransform(item,"interior"),ReadTransform(item,"interiorLOD"));
                        var v=byId[item.transform.GetInstanceID()];
                        if(v.Source != item)
                        {
                            Unsubscribe(v); v.Source=item;
                            v.CargoController=trainType.GetProperty("CargoModelController")?.GetValue(item,null);
                            v.CargoGetter=v.CargoController?.GetType().GetMethod("GetCurrentCargoModel");
                            v.InteriorLoadedHandler=ignored=>{v.PartsPending=true;v.InteriorPending=true;v.NextCargoCheck=0;};
                            trainType.GetEvent("InteriorLoaded")?.AddEventHandler(item,v.InteriorLoadedHandler);
                            trainType.GetEvent("ExternalInteractableLoaded")?.AddEventHandler(item,v.InteriorLoadedHandler);
                        }
                    }
                }
            }
            // Cache all newly visible cars once. Translating/rotating a car never
            // recaptures its height map: sampling is in that car's local frame.
            var budget=1;
            foreach (var v in vehicles)
            {
                CheckCargo(v);
                if(v.PartsPending && budget>0) { RefreshParts(v); v.PartsPending=v.InteriorPending=false; }
                if (v.Root != null && v.Dirty && budget>0) { CaptureHeight(v); budget--; }
            }
        }

        public void SetCargo(Transform root,Transform cargo)
        {
            Vehicle v;
            if(root==null || !byId.TryGetValue(root.GetInstanceID(),out v) || v.Cargo==cargo) return;
            v.Cargo=cargo;v.PartsPending=true;
        }
        private void CheckCargo(Vehicle v)
        {
            if(Time.realtimeSinceStartup<v.NextCargoCheck) return;
            v.NextCargoCheck=Time.realtimeSinceStartup+0.25f;
            if(v.Source!=null)
            {
                var external=trainType.GetProperty("loadedExternalInteractables")?.GetValue(v.Source,null) as GameObject;
                var dummy=trainType.GetProperty("loadedDummyExternalInteractables")?.GetValue(v.Source,null) as GameObject;
                SetExternalParts(v.Root,external!=null?external.transform:null,dummy!=null?dummy.transform:null);
            }
            if(v.CargoGetter==null && v.Source!=null)
            {
                v.CargoController=trainType.GetProperty("CargoModelController")?.GetValue(v.Source,null);
                v.CargoGetter=v.CargoController?.GetType().GetMethod("GetCurrentCargoModel");
            }
            if(v.CargoGetter==null) return;
            var cargo=v.CargoGetter.Invoke(v.CargoController,null) as GameObject;
            SetCargo(v.Root,cargo!=null?cargo.transform:null);
        }
        public void SetExternalParts(Transform root,Transform external,Transform dummy)
        {
            Vehicle v;
            if(root==null || !byId.TryGetValue(root.GetInstanceID(),out v) || (v.External==external && v.DummyExternal==dummy)) return;
            v.External=external;v.DummyExternal=dummy;v.PartsPending=true;
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
        { return component.GetType().GetField(field,BindingFlags.Public|BindingFlags.Instance)?.GetValue(component) as Transform; }

        private void RefreshParts(Vehicle v)
        {
            var parts = new List<Part>(); var seen = new HashSet<int>();
            var lodParts=new Dictionary<int,Tuple<LodSet,int>>();
            v.Lods.Clear();
            var seenGroups=new HashSet<int>();
            foreach(var root in new[]{v.Root,v.Interior,v.InteriorLod,v.Cargo,v.External,v.DummyExternal})
            {
                if(root==null) continue;
                foreach(var group in root.GetComponentsInChildren<LODGroup>(true))
                {
                    if(!seenGroups.Add(group.GetInstanceID())) continue;
                    var set=new LodSet{Group=group,Levels=group.GetLODs()};v.Lods.Add(set);
                    for(int index=0;index<set.Levels.Length;index++) foreach(var r in set.Levels[index].renderers)
                        if(r!=null) lodParts[r.GetInstanceID()]=Tuple.Create(set,index);
                }
            }
            foreach (var root in new[]{v.Root,v.Interior,v.InteriorLod,v.Cargo,v.External,v.DummyExternal})
            {
                if (root == null) continue;
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                    if (!seen.Add(renderer.GetInstanceID())) continue;
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
                    var p=new Part { Renderer=renderer,Materials=renderer.sharedMaterials,Mesh=mesh,Interior=interior };
                    Tuple<LodSet,int> lod;
                    if(lodParts.TryGetValue(renderer.GetInstanceID(),out lod)) {p.Lod=lod.Item1;p.LodIndex=lod.Item2;}
                    int count=Mathf.Min(p.Materials.Length,mesh.subMeshCount);
                    p.Opaque=new bool[count];p.Cutoff=new float[count];p.Albedo=new Texture[count];p.ST=new Vector4[count];
                    for(int i=0;i<count;i++)
                    {
                        var m=p.Materials[i]; if(m==null || m.renderQueue>2500) continue;
                        p.Opaque[i]=true;
                        p.Cutoff[i]=m.GetTag("RenderType",false)=="TransparentCutout" ? (m.HasProperty("_Cutoff")?m.GetFloat("_Cutoff"):0.5f) : 0;
                        p.Albedo[i]=m.HasProperty("_MainTex")?m.GetTexture("_MainTex"):null;
                        var scale=m.HasProperty("_MainTex")?m.GetTextureScale("_MainTex"):Vector2.one;
                        var offset=m.HasProperty("_MainTex")?m.GetTextureOffset("_MainTex"):Vector2.zero;
                        p.ST[i]=new Vector4(scale.x,scale.y,offset.x,offset.y);
                    }
                    parts.Add(p);
                }
            }
            var signature=17;
            foreach (var p in parts) signature=unchecked(signature*31+p.Renderer.GetInstanceID()+p.Mesh.GetInstanceID()+(p.Interior?1:0));
            // LOD membership and active cargo can change without new renderers.

            v.Parts.Clear(); v.Parts.AddRange(parts); v.Signature=signature; v.Dirty=true; Revision++;
        }

        public void HideForStaticCapture(Vector4 area)
        {
            // A train can spawn between periodic discovery and this capture.
            // Refresh the exclusion roots now so it cannot become static shelter.
            if(DiscoverVehicles!=null) foreach(var item in DiscoverVehicles())
            {
                var component=item as Component;
                if(component==null) continue;
                TrackStaticRoot(component.transform,ReadTransform(component,"interior"),ReadTransform(component,"interiorLOD"));
            }
            hidden.Clear();
            foreach(var sv in staticVehicles.Values)
            {
                if(sv.Root==null || Mathf.Abs(sv.Root.position.x-area.x)>area.z+40 ||
                    Mathf.Abs(sv.Root.position.z-area.y)>area.z+40) continue;
                // Refresh only on an actual capture: also catches streamed cargo
                // on cars outside the detailed 32-car cache.
                foreach(var root in new[]{sv.Root,sv.Interior,sv.InteriorLod})
                    if(root!=null) foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
                    {if(renderer==null || renderer.forceRenderingOff) continue;hidden.Add(renderer);renderer.forceRenderingOff=true;}
            }
            foreach (var v in vehicles) foreach (var part in v.Parts)
            {
                var renderer=part.Renderer;
                if (renderer == null || renderer.forceRenderingOff) continue;
                hidden.Add(renderer); renderer.forceRenderingOff=true;
            }
        }
        public void RestoreAfterStaticCapture()
        { foreach (var renderer in hidden) if (renderer != null) renderer.forceRenderingOff=false; hidden.Clear(); }

        private void CaptureHeight(Vehicle v)
        {
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
        { foreach(var v in vehicles) v.SnowReady=false; }

        public void Accumulate(RenderTexture nearHeight,Vector4 nearArea,float nearOffset,
            RenderTexture farHeight,Vector4 farArea,float farOffset,float snowClock)
        {
            // One local mask per frame. Dry motion only reuses the mask; shelter
            // controls new accumulation, never erases snow already on a vehicle.
            for(int n=0;n<vehicles.Count;n++)
            {
                nextSnowVehicle%=vehicles.Count;
                var v=vehicles[nextSnowVehicle++];
                if(v.Root==null || !v.Ready || (v.SnowReady && !v.SnowShapeDirty && snowClock-v.LastSnowClock<1f/180f)) continue;
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
                capture.DrawMesh(snowQuad,Matrix4x4.identity,material,0,3);
                Graphics.ExecuteCommandBuffer(capture);
                Graphics.CopyTexture(snowSlice,0,0,snow,v.Slot,0);
                v.SnowReady=true;v.SnowShapeDirty=false;v.LastSnowClock=snowClock;
                v.SnowArea=v.Area;snowAreas[v.Slot]=v.SnowArea;
                break;
            }
        }

        public void Record(CommandBuffer buffer,Camera camera,RailSnowTracks rails)
        {
            foreach(var v in vehicles) snowRemaining[v.Slot]=SnowRemaining!=null?Mathf.Clamp01(SnowRemaining(v.Source)):1f;
            buffer.SetGlobalFloatArray("_DVPSVehicleSnowRemaining",snowRemaining);
            FrameDrawCount=0;
            GeometryUtility.CalculateFrustumPlanes(camera,frustum);
            bool visible=rails.HasVisibleTracks(frustum);
            foreach(var v in vehicles)
            {
                v.FrameVisible=false;
                if(v.Root==null) continue;
                var center=v.Ready?v.Root.TransformPoint(v.LocalBounds.center):v.Root.position;
                var scale=v.Root.lossyScale;
                float radius=v.Ready?v.LocalBounds.extents.magnitude*Mathf.Max(Mathf.Abs(scale.x),
                    Mathf.Max(Mathf.Abs(scale.y),Mathf.Abs(scale.z)))+3f:40f;
                v.FrameVisible=GeometryUtility.TestPlanesAABB(frustum,new Bounds(center,Vector3.one*(radius*2)));
                visible|=v.FrameVisible;
            }
            var size=visible ? -1 : 1;
            buffer.GetTemporaryRT(DataId,size,size,0,FilterMode.Point,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
            if (size==1) buffer.SetRenderTarget(DataId);
            else buffer.SetRenderTarget(DataId,BuiltinRenderTextureType.CameraTarget);
            buffer.ClearRenderTarget(false,true,Color.clear);
            rails.Record(buffer,material,frustum);
            foreach (var v in vehicles)
            {
                if (v.Root == null || !v.FrameVisible) continue;
                // An interior can finish loading between Update and rendering.
                // Exclude its new renderers immediately; its geometry is never snow.
                if(v.InteriorPending) { CheckCargo(v); RefreshParts(v); v.PartsPending=v.InteriorPending=false; }
                foreach(var lod in v.Lods)
                {
                    if(lod.Group==null) continue;
                    var g=lod.Group;var scale=g.transform.lossyScale;
                    float lodSize=g.size*Mathf.Max(Mathf.Abs(scale.x),Mathf.Max(Mathf.Abs(scale.y),Mathf.Abs(scale.z)));
                    float height=camera.orthographic ? lodSize/(2*camera.orthographicSize) :
                        lodSize/(2*Mathf.Max(0.01f,Vector3.Distance(camera.transform.position,g.transform.TransformPoint(g.localReferencePoint)))*Mathf.Tan(camera.fieldOfView*Mathf.Deg2Rad*0.5f));
                    height*=QualitySettings.lodBias;lod.Current=lod.Levels.Length;
                    for(int level=0;level<lod.Levels.Length;level++)
                        if(height>=lod.Levels[level].screenRelativeTransitionHeight) {lod.Current=level;break;}
                }
                buffer.SetGlobalMatrix("_DVPSVehicleWorldToLocal",v.Root.worldToLocalMatrix);
                foreach (var part in v.Parts)
                {
                    var renderer=part.Renderer;
                    if(!part.Interior && part.Lod!=null && part.LodIndex!=part.Lod.Current) continue;
                    if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                        renderer.forceRenderingOff || (camera.cullingMask & (1<<renderer.gameObject.layer))==0 ||
                        !GeometryUtility.TestPlanesAABB(frustum,renderer.bounds)) continue;
                    DrawPart(buffer,part,0,part.Interior || !v.SnowReady ? -1f : v.Slot+1f);
                }
            }
            buffer.SetGlobalTexture(DataId,DataId);
            buffer.SetGlobalTexture("_DVPSVehicleHeights",heights);
            buffer.SetGlobalTexture("_DVPSVehicleSnow",snow);
            buffer.SetGlobalVectorArray("_DVPSVehicleAreas",areas);
            buffer.SetGlobalVectorArray("_DVPSVehicleSnowAreas",snowAreas);
        }

        private void DrawPart(CommandBuffer buffer,Part part,int pass,float vehicleIndex=-1)
        {
            if(part.Mesh == null) return;
            for (var slot=0;slot<Mathf.Min(part.Materials.Length,part.Mesh.subMeshCount);slot++)
            {
                if (!part.Opaque[slot]) continue;
                if(pass==0) buffer.SetGlobalFloat("_DVPSVehicleIndex",HasNativeSnowTexture!=null && HasNativeSnowTexture(part.Renderer,slot)?-1:vehicleIndex);
                buffer.SetGlobalFloat("_DVPSVehicleCutoff",part.Cutoff[slot]);
                if(part.Cutoff[slot]>0)
                {
                    buffer.SetGlobalTexture("_DVPSVehicleAlbedo",part.Albedo[slot]!=null?part.Albedo[slot]:Texture2D.whiteTexture);
                    buffer.SetGlobalVector("_DVPSVehicleST",part.ST[slot]);
                }
                buffer.DrawRenderer(part.Renderer,material,slot,pass);
                if(pass==0) FrameDrawCount++;
            }
        }
        public void ReleaseFrame(CommandBuffer buffer) { buffer.ReleaseTemporaryRT(DataId); }
        private void Unsubscribe(Vehicle v)
        {
            if(v.Source != null && v.InteriorLoadedHandler != null)
            {
                trainType?.GetEvent("InteriorLoaded")?.RemoveEventHandler(v.Source,v.InteriorLoadedHandler);
                trainType?.GetEvent("ExternalInteractableLoaded")?.RemoveEventHandler(v.Source,v.InteriorLoadedHandler);
            }
            v.Source=null; v.InteriorLoadedHandler=null;
        }
        public void Dispose()
        {
            RestoreAfterStaticCapture();
            if(capture != null) { capture.Dispose(); capture=null; }
            foreach(var v in vehicles) Unsubscribe(v);
            foreach(var resource in new UnityEngine.Object[]{material,heights,heightSlice,snow,snowSlice,snowQuad})
                if (resource != null) { if(Application.isPlaying) UnityEngine.Object.Destroy(resource); else UnityEngine.Object.DestroyImmediate(resource); }
            material=null; heights=heightSlice=null; vehicles.Clear(); byId.Clear(); staticVehicles.Clear();Array.Clear(usedSlots,0,usedSlots.Length); nextScan=0; Revision++;
            snow=snowSlice=null;snowQuad=null;nextSnowVehicle=0;
        }
    }
}
