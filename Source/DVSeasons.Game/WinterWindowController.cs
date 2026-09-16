using System;
using System.Collections.Generic;
using System.Reflection;
using DV.CabControls;
using DV.Openables;
using DV.Rain;
using DVSeasons.Core;
using LocoSim.Implementations;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    internal sealed class WinterWindowController : IDisposable
    {
        private const int Resolution = 128;
        private static WinterWindowController active;
        public static float CabinTemperature(TrainCar car)
        {
            Cab cab;
            return active != null && car != null && active.lastClimate > 0 &&
                active.cabs.TryGetValue(car, out cab) ? cab.Climate.CabinTemperature : float.NaN;
        }
        private sealed class Cab
        {
            public TrainCar Car;
            public WindowWinterClimate Climate = new WindowWinterClimate();
            public ControlImplBase Heater;
            public DoorsAndWindowsController[] Openings;
            public OpenableControl[] Openables;
            public ControlImplBase[] OpeningControls;
            public GameObject InteriorRoot, ExternalRoot, PersistentRoot;
            public bool BindingsReady, BindingsSettled;
            public SimulationFlow Flow;
            public Port EngineOn, EngineRpm, EngineTemperature;
            public float AmbientTemperature;
            public float NextBind;
        }
        private sealed class Pane
        {
            public Window Window;
            public MeshRenderer FallbackRenderer;
            public Transform Frame => Window != null ? Window.transform : FallbackRenderer != null ? FallbackRenderer.transform : null;
            public Vector2 FallbackSize;
            public Vector2 Size => Window != null ? Window.sizeInMeters : FallbackSize;
            public Cab Cab;
            public Texture2D Mask;
            public Color32[] Pixels = new Color32[Resolution * Resolution];
            public readonly List<MeshRenderer> Overlays = new List<MeshRenderer>();
            public readonly List<MeshRenderer> Originals = new List<MeshRenderer>();
            public readonly List<Window> VisualWindows = new List<Window>();
            public readonly List<Matrix4x4> VisualToPane = new List<Matrix4x4>();
            public readonly List<LODGroup> LodGroups = new List<LODGroup>();
            public Material Material;
            public readonly MaterialPropertyBlock Properties = new MaterialPropertyBlock();
            public float LastUpdate;
            public bool Dirty;
            public bool MaskChanged, HasWipes;
            public float MeltRemainder, RefreezeRemainder;
            public Vector3 Velocity;
            public Vector2[] LastStarts, LastEnds;
            public bool[] WiperReady;
            public float NextShelter;
            public bool Sheltered;
            public Matrix4x4 CollisionMatrix;
            public Vector3 CollisionCentre;
            public float NextProperties;
        }
        private readonly SeasonAssetBundleRepository repository;
        private readonly Dictionary<TrainCar, Cab> cabs = new Dictionary<TrainCar, Cab>();
        private readonly List<Pane> panes = new List<Pane>();
        private readonly List<Pane> collisionPanes = new List<Pane>();
        private Cab cameraCab;
        private RenderTexture frostPattern;
        private readonly HashSet<Window> known = new HashSet<Window>();
        private readonly HashSet<MeshRenderer> boundVisuals = new HashSet<MeshRenderer>();
        private readonly List<TrainCar> removed = new List<TrainCar>();
        private readonly Dictionary<string,WindowClimateState> savedClimates = new Dictionary<string,WindowClimateState>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,WindowSnowMask> savedMasks = new Dictionary<string,WindowSnowMask>();
        private static string MaskId(TrainCar car,Window window)
        {
            if(car==null || window==null || string.IsNullOrEmpty(car.CarGUID)) return null;
            string path=""; var transform=window.transform;
            for(int i=0;transform!=null && transform.parent!=null && transform!=car.transform && i<20;i++,transform=transform.parent)
            {
                if(transform==car.interior || transform==car.interiorLOD ||
                    (car.loadedInterior!=null && transform==car.loadedInterior.transform)) break;
                path=transform.name+"["+transform.GetSiblingIndex()+"]/"+path;
            }
            return car.CarGUID+"/"+path;
        }
        public void RestoreMasks(List<WindowSnowMask> records)
        {
            savedMasks.Clear();if(records==null) return;
            foreach(var record in records)
                if(record!=null && !string.IsNullOrEmpty(record.Id) && record.Id.Length<4096 &&
                    !string.IsNullOrEmpty(record.Packed) && record.Packed.Length<60000) savedMasks[record.Id]=record;
        }
        public void SaveMasks(List<WindowSnowMask> destination)
        {
            var bytes=new byte[Resolution*Resolution*2];
            foreach(var pane in panes)
            {
                string packed;
                for(int i=0;i<pane.Pixels.Length;i++) { bytes[i*2]=pane.Pixels[i].r; bytes[i*2+1]=pane.Pixels[i].g; }
                using(var stream=new System.IO.MemoryStream())
                {
                    using(var zip=new System.IO.Compression.DeflateStream(stream,System.IO.Compression.CompressionLevel.Fastest,true))
                        zip.Write(bytes,0,bytes.Length);
                    packed=Convert.ToBase64String(stream.ToArray());
                }
                foreach(var window in pane.VisualWindows)
                {
                    var id=MaskId(pane.Cab.Car,window);
                    if(id!=null) savedMasks[id]=new WindowSnowMask {Id=id,Packed=packed};
                }
            }
            destination.AddRange(savedMasks.Values);
        }
        private void RestoreMask(Pane pane)
        {
            string id=MaskId(pane.Cab.Car,pane.Window);WindowSnowMask record;
            if(id==null || !savedMasks.TryGetValue(id,out record)) return;
            try
            {
                using(var input=new System.IO.MemoryStream(Convert.FromBase64String(record.Packed)))
                using(var zip=new System.IO.Compression.DeflateStream(input,System.IO.Compression.CompressionMode.Decompress))
                    for(int i=0;i<pane.Pixels.Length;i++)
                    {
                        int r=zip.ReadByte(),g=zip.ReadByte(); if(r<0 || g<0) throw new System.IO.InvalidDataException();
                        pane.Pixels[i]=new Color32((byte)r,(byte)g,0,0);pane.Dirty|=r>0;pane.HasWipes|=g>0;
                    }
            }
            catch(Exception) { Array.Clear(pane.Pixels,0,pane.Pixels.Length);pane.Dirty=false;savedMasks.Remove(id); }
        }
        public void Restore(List<CabFrostState> records)
        {
            CabHeating.RestoreCustomClimates(records);
            savedClimates.Clear(); if(records==null) return;
            foreach(var record in records)
                if(record!=null && !string.IsNullOrEmpty(record.Id) && record.Id.Length<=80 && record.Climate!=null && record.Climate.IsValid())
                    savedClimates[record.Id]=record.Climate;
        }
        public void Save(List<CabFrostState> destination)
        {
            foreach(var cab in cabs.Values)
                if(cab.Car!=null && !string.IsNullOrEmpty(cab.Car.CarGUID)) savedClimates[cab.Car.CarGUID]=cab.Climate.Capture();
            CabHeating.SaveCustomClimates(savedClimates);
            foreach(var pair in savedClimates) destination.Add(new CabFrostState {Id=pair.Key,Climate=pair.Value});
        }
        private float nextScan, nextClimate, lastClimate;
        private IEnumerator<int> discovery;
        private float snowfall, lighting;
        private Shader shader;
        private int cursor;
        private bool reported;
        public WinterWindowController(SeasonAssetBundleRepository repository) { this.repository = repository; }

        private bool HasColdPanes()
        {
            foreach (var cab in cabs.Values)
                if (cab.Car != null && (cab.Climate.GlassTemperature < 12
                    || cab.Climate.Frost > .0001f || cab.Climate.Fog > .0001f)) return true;
            return false;
        }

        public void Apply(SeasonState state, float snow, float light, bool enabled)
        {
            var camera = PlayerManager.ActiveCamera;
            // Existing cold panes finish warming through their own thermal inertia.
            if (!enabled || state == null || (state.SnowAmount <= .001f && state.TemperatureCelsius > 5
                && !HasColdPanes()))
            { Dispose(); return; }
            if (camera == null) return;
            if (shader == null) shader = repository.LoadShader("WinterWindow");
            if (shader == null) return;
            EnsureFrostPattern();
            active = this;
            snowfall = Mathf.Clamp01(snow); lighting = Mathf.Clamp01(light);
            float now = Time.time;
            if (discovery == null && now >= nextScan)
            {
                nextScan = now + 2;
                discovery = Discover(camera, now).GetEnumerator();
            }
            using (SnowPerformance.Measure("window-discovery")) FrameDiscovery.Advance(ref discovery);
            if (now >= nextClimate)
            {
                float dt = lastClimate > 0 ? Mathf.Clamp(now - lastClimate, 0, 1) : .1f;
                lastClimate = now; nextClimate = now + .1f;
                foreach (var cab in cabs.Values)
                {
                    if (cab.Car == null) continue;
                    cab.AmbientTemperature = state.TemperatureCelsius;
                    // Custom-car climate lives independently of this visual
                    // controller and is also shared with Survival without panes.
                    var sharedClimate = CabHeating.CustomClimate(cab.Car);
                    if (sharedClimate != null) { cab.Climate = sharedClimate; continue; }
                    Bind(cab, now);
                    bool running = cab.EngineOn != null ? cab.EngineOn.Value > .5f
                        : cab.EngineRpm != null && cab.EngineRpm.Value > .05f;
                    var sim = cab.Car.SimController;
                    if (sim != null && sim.firebox != null && sim.firebox.IsFireOn) running = true;
                    bool open = AnythingOpen(cab);
                    float heater = cab.Heater != null ? Mathf.Clamp01(cab.Heater.Value) : 0;
                    if (CabHeating.IsActive && (CabHeaterSwitchSystem.IsSupported(cab.Car) || CabEngineHeating.IsCustomLocomotive(cab.Car)))
                        heater = CabHeating.GetLevel(cab.Car);
                    // Steam's firebox warms the cab without a separate electrical heater.
                    if (sim != null && sim.firebox != null && sim.firebox.IsFireOn) heater = Math.Max(heater, .85f);
                    cab.Climate.Advance(dt, state.TemperatureCelsius, running,
                        cab.EngineTemperature != null ? cab.EngineTemperature.Value : state.TemperatureCelsius,
                        heater, open, state.SnowAmount);
                }
            }
            // Reuse the actual glass geometry and follow streamed/moving windows.
            // Never change the native droplet materials or their property blocks.
            collisionPanes.Clear(); cameraCab = null;
            float nearestCab = 25f;
            foreach (var pane in panes)
            {
                if (pane.Frame == null) continue;
                float distance = (pane.Frame.position-camera.transform.position).sqrMagnitude;
                if (distance < 1600 && pane.Frame.gameObject.activeInHierarchy)
                {
                    pane.CollisionMatrix = MovingPaneMatrix(pane);
                    pane.CollisionCentre = pane.Frame.position;
                    collisionPanes.Add(pane);
                    if (pane.Window != null && distance < nearestCab)
                    { nearestCab = distance; cameraCab = pane.Cab; }
                }
                var body = pane.Window != null ? pane.Window.rb : pane.Cab.Car.rb;
                pane.Velocity = body != null ? body.GetPointVelocity(pane.Frame.position) : Vector3.zero;
                for (int i = 0; i < pane.Overlays.Count; i++)
                    if (pane.Overlays[i] != null)
                    {
                        var original = pane.Originals[i];
                        if (original == null) { pane.Overlays[i].enabled=false; continue; }
                        pane.Overlays[i].enabled = original != null && original.enabled && !original.forceRenderingOff
                            && (pane.Cab.Climate.Frost > .005f || pane.Cab.Climate.Fog > .005f || snowfall > 0 || pane.Dirty);
                        if (now >= pane.NextProperties)
                        { SetProperties(pane, i); pane.Overlays[i].SetPropertyBlock(pane.Properties); }
                    }
                if (now >= pane.NextProperties) pane.NextProperties = now + (distance < 1600 ? .1f : .5f);
                if (distance < 1600)
                    using (SnowPerformance.Measure("window-wipers")) Wipe(pane);
            }
            int budget = Math.Min(4, panes.Count);
            for (int i = 0; i < budget; i++)
            {
                cursor %= panes.Count;
                var pane = panes[cursor++];
                if (pane.Frame == null || now - pane.LastUpdate < .1f) continue;
                using (SnowPerformance.Measure("window-mask"))
                    UpdateMask(pane, Mathf.Min(1, now - pane.LastUpdate));
                pane.LastUpdate = now;
            }
        }

        private void EnsureFrostPattern()
        {
            if (frostPattern != null && frostPattern.IsCreated()) return;
            if (frostPattern == null)
                frostPattern = new RenderTexture(1024,1024,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear)
                { name="DVSeasons shared frost crystals", useMipMap=true, autoGenerateMips=false,
                    wrapMode=TextureWrapMode.Clamp, filterMode=FilterMode.Trilinear, hideFlags=HideFlags.HideAndDontSave };
            frostPattern.Create();
            var bake = new Material(shader); var previous = RenderTexture.active;
            try { Graphics.Blit(null, frostPattern, bake, 1); frostPattern.GenerateMips(); }
            finally { RenderTexture.active=previous; UnityEngine.Object.Destroy(bake); }
        }

        private IEnumerable<int> Discover(Camera camera, float now)
        {
            for (int i = panes.Count - 1; i >= 0; i--)
            {
                yield return 0;
                var p = panes[i];
                if (p.FallbackRenderer != null && p.Cab.Car != null) continue;
                if (p.Window == null)
                    foreach (var duplicate in p.VisualWindows)
                        if (duplicate != null) { p.Window = duplicate; break; }
                if (p.Window != null && p.Cab.Car != null)
                {
                    AddVisuals(p,p.Window);
                    if(p.Window.duplicates!=null) foreach(var duplicate in p.Window.duplicates)
                        if(duplicate!=null) {AddVisuals(p,duplicate);known.Add(duplicate);}
                    continue;
                }
                known.Remove(p.Window); Release(p); panes.RemoveAt(i);
            }
            // Search only locomotive hierarchies, including detached doors and
            // streamed interiors, rather than every object in the scene.
            foreach (var window in LoadedWindows(camera))
            {
                yield return 0;
                if (window == null || known.Contains(window) || !window.gameObject.activeInHierarchy) continue;
                var car = TrainCar.Resolve(window.transform);
                if (car == null || !car.IsLoco) continue;
                Pane existing=null;
                if(window.simulate && window.duplicates!=null)
                    foreach(var candidate in panes)
                        foreach(var duplicate in window.duplicates)
                            if(duplicate!=null && candidate.VisualWindows.Contains(duplicate)) { existing=candidate; break; }
                if(existing!=null)
                {
                    // The detailed interior can load after its exterior duplicate.
                    // Adopt its native wipers without replacing the existing mask.
                    existing.Window=window;known.Add(window);AddVisuals(existing,window);
                    foreach(var duplicate in window.duplicates)
                        if(duplicate!=null) {AddVisuals(existing,duplicate);known.Add(duplicate);}
                    InitializeWipers(existing);
                    continue;
                }
                Cab cab;
                if (!cabs.TryGetValue(car, out cab))
                {
                    cab = new Cab { Car = car, Climate = CabHeating.CustomClimate(car) ?? new WindowWinterClimate() };
                    WindowClimateState saved;
                    if (!CabEngineHeating.IsCustomLocomotive(car) && !string.IsNullOrEmpty(car.CarGUID) && savedClimates.TryGetValue(car.CarGUID, out saved)) cab.Climate.Restore(saved);
                    cabs.Add(car, cab);
                }
                var pane = new Pane { Window = window, Cab = cab, LastUpdate = now };
                RestoreMask(pane);
                pane.Mask = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false, true)
                    { name = "DVSeasons window snow and wiper mask", wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                pane.Mask.SetPixels32(pane.Pixels); pane.Mask.Apply(false, false);
                pane.Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                AddVisuals(pane, window);
                if (window.duplicates != null) foreach (var duplicate in window.duplicates)
                    if (duplicate != null && !known.Contains(duplicate)) { AddVisuals(pane, duplicate); known.Add(duplicate); }
                InitializeWipers(pane);
                panes.Add(pane); known.Add(window);
            }
            foreach (var car in RailSnowGameSource.GetCars().ToArray())
            {
                yield return 0;
                if (car != null && car.carType == DV.ThingTypes.TrainCarType.LocoDM1U)
                    DiscoverDm1uCarGlass(car, now);
            }
            removed.Clear();
            foreach (var pair in cabs)
                if (pair.Key == null) removed.Add(pair.Key);
            foreach (var car in removed) cabs.Remove(car);
            if (!reported && panes.Count > 0)
            {
                reported = true;
                Debug.Log("[DVSeasons] Winter windows ready: per-cab heater/air/glass temperatures, native wiper paths and snow impacts.");
            }
        }

        private static IEnumerable<Window> LoadedWindows(Camera camera)
        {
            var roots = new HashSet<GameObject>();
            var cars = RailSnowGameSource.GetCars().ToArray();
            var position = camera != null ? camera.transform.position : Vector3.zero;
            Array.Sort(cars,(a,b) =>
                (a != null ? (a.transform.position-position).sqrMagnitude : float.MaxValue).CompareTo(
                 b != null ? (b.transform.position-position).sqrMagnitude : float.MaxValue));
            foreach (var car in cars)
            {
                yield return null;
                if (car == null || !car.IsLoco || !car.gameObject.activeInHierarchy) continue;
                // Interior masters first, so exterior duplicates share their mask.
                foreach (var root in new[] { car.loadedInterior, car.gameObject,
                    car.loadedExternalInteractables, car.interior != null ? car.interior.gameObject : null })
                {
                    if (root == null || !roots.Add(root)) continue;
                    var windows = root.GetComponentsInChildren<Window>(true);
                    Array.Sort(windows, (a,b) => b.simulate.CompareTo(a.simulate));
                    foreach (var window in windows) yield return window;
                }
            }
        }

        private void AddVisuals(Pane pane, Window window)
        {
            // A few streamed cabs (notably DM1U's rear pane) expose the glass
            // renderer on the Window object but leave the visuals array empty.
            // Use that renderer as a conservative fallback so the rear glass
            // receives the same climate and thawing behaviour.
            var visuals = window.visuals;
            if (visuals == null || visuals.Length == 0)
            {
                var fallback = window.GetComponent<MeshRenderer>();
                if (fallback != null) visuals = new[] { fallback };
                else return;
            }
            foreach (var visual in visuals)
            {
                if (visual == null || boundVisuals.Contains(visual)) continue;
                var filter = visual.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                boundVisuals.Add(visual);
                var go = new GameObject("DVSeasons Winter Glass") { hideFlags = HideFlags.HideAndDontSave, layer = visual.gameObject.layer };
                go.transform.SetParent(visual.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = pane.Material;
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                pane.Overlays.Add(renderer); pane.Originals.Add(visual);
                pane.VisualWindows.Add(window);
                // Bake the projection in mesh coordinates once. Doors and sliding
                // panes can move independently of their native Window controller.
                pane.VisualToPane.Add(window.transform.worldToLocalMatrix * visual.transform.localToWorldMatrix);
                var group=visual.GetComponentInParent<LODGroup>();
                pane.LodGroups.Add(group);
                if(group!=null)
                {
                    var levels=group.GetLODs(); bool changed=false;
                    for(int i=0;i<levels.Length;i++)
                    {
                        var renderers=new List<Renderer>(levels[i].renderers);
                        if(!renderers.Contains(visual)) continue;
                        renderers.Add(renderer);levels[i].renderers=renderers.ToArray();changed=true;
                    }
                    if(changed) group.SetLODs(levels);
                }
            }
        }

        private void DiscoverUnregisteredDm1uGlass(float now)
        {
            // DV99's dm1u-150_window_RC renderer has no DV.Rain.Window at all.
            // Discover actual glass meshes, without creating/registering a fake
            // rain component. Native windows already bound above are excluded.
            foreach (var car in RailSnowGameSource.GetCars())
            {
                if (car == null || car.carType != DV.ThingTypes.TrainCarType.LocoDM1U) continue;
                DiscoverDm1uCarGlass(car, now);
            }
        }
        private void DiscoverDm1uCarGlass(TrainCar car, float now)
        {
            DiscoverDm1uGlassRoot(car, car.gameObject, now);
            // External door/window rigidbodies are detached from TrainCar.
            DiscoverDm1uGlassRoot(car, car.loadedExternalInteractables, now);
            DiscoverDm1uGlassRoot(car, car.loadedInterior, now);
        }
        private void DiscoverDm1uGlassRoot(TrainCar car, GameObject root, float now)
        {
            if (root == null) return;
            foreach (var visual in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (visual == null || boundVisuals.Contains(visual) ||
                    !visual.name.StartsWith("dm1u-150_window_", StringComparison.OrdinalIgnoreCase)) continue;
                var source = visual.sharedMaterial;
                if (source == null || !source.name.StartsWith("Glass", StringComparison.OrdinalIgnoreCase)) continue;
                var filter = visual.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                Cab cab;
                if (!cabs.TryGetValue(car, out cab))
                { cab = new Cab { Car = car, Climate = CabHeating.CustomClimate(car) ?? new WindowWinterClimate() }; cabs.Add(car, cab); }
                AddUnregisteredGlass(cab, visual, now);
            }
        }
        private void AddUnregisteredGlass(Cab cab, MeshRenderer visual, float now)
        {
            var filter = visual.GetComponent<MeshFilter>();
            var bounds = filter.sharedMesh.bounds;
            var rotation = bounds.size.x < bounds.size.z ? Quaternion.Euler(0,90,0) : Quaternion.identity;
            var size = bounds.size.x < bounds.size.z
                ? new Vector2(bounds.size.z, bounds.size.y) : new Vector2(bounds.size.x, bounds.size.y);
            var pane = new Pane { Cab = cab, FallbackRenderer = visual, FallbackSize = size, LastUpdate = now };
            pane.Mask = new Texture2D(Resolution, Resolution, TextureFormat.RGBA32, false, true)
                { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            pane.Mask.SetPixels32(pane.Pixels); pane.Mask.Apply();
            pane.Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var go = new GameObject("DVSeasons Winter Glass") { hideFlags = HideFlags.HideAndDontSave, layer = visual.gameObject.layer };
            go.transform.SetParent(visual.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            var overlay = go.AddComponent<MeshRenderer>(); overlay.sharedMaterial = pane.Material;
            overlay.shadowCastingMode = ShadowCastingMode.Off; overlay.receiveShadows = false;
            pane.Originals.Add(visual); pane.Overlays.Add(overlay); pane.VisualWindows.Add(null);
            pane.VisualToPane.Add(Matrix4x4.TRS(bounds.center, rotation, Vector3.one).inverse);
            var group=visual.GetComponentInParent<LODGroup>();
            pane.LodGroups.Add(group);
            if(group!=null)
            {
                var levels=group.GetLODs();
                for(int i=0;i<levels.Length;i++)
                {
                    var renderers=new List<Renderer>(levels[i].renderers);
                    if(!renderers.Contains(visual)) continue;
                    renderers.Add(overlay);levels[i].renderers=renderers.ToArray();
                }
                group.SetLODs(levels);
            }
            boundVisuals.Add(visual); panes.Add(pane);
        }
        private static bool AnythingOpen(Cab cab)
        {
            if (cab.Openings != null) foreach (var opening in cab.Openings)
                if (opening != null) { try { if (opening.AnythingOpen()) return true; } catch { } }
            if (cab.Openables != null) for (int i = 0; i < cab.Openables.Length; i++)
            {
                var opening = cab.Openables[i]; var control = cab.OpeningControls[i];
                if (opening != null && control != null &&
                    (opening.closedAtZero ? control.Value >= .1f : control.Value <= .9f)) return true;
            }
            return false;
        }
        private static void Bind(Cab cab, float now)
        {
            var sim = cab.Car.SimController;
            var flow = sim != null ? sim.simFlow : null;
            var interior = cab.Car.loadedInterior;
            var external = cab.Car.loadedExternalInteractables;
            var persistent = cab.Car.interior != null ? cab.Car.interior.gameObject : null;
            bool sameRoots = cab.BindingsReady && cab.InteriorRoot == interior &&
                cab.ExternalRoot == external && cab.PersistentRoot == persistent;
            if (sameRoots && cab.Flow == flow && (cab.BindingsSettled || now < cab.NextBind)) return;
            // One follow-up after Start initializes controller.entries; thereafter
            // only streaming/context changes rebuild the component bindings.
            cab.BindingsSettled = sameRoots;
            cab.BindingsReady = true;
            cab.InteriorRoot = interior; cab.ExternalRoot = external; cab.PersistentRoot = persistent;
            cab.NextBind = now + 2;
            if (cab.Flow != flow)
            {
                cab.Flow = flow; cab.EngineOn = cab.EngineRpm = cab.EngineTemperature = null;
                if (flow != null) foreach (var component in flow.OrderedSimComps)
                {
                    var field = component.GetType().GetField("engineOnReadOut",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    if (field != null) cab.EngineOn = field.GetValue(component) as Port;
                    if (cab.EngineOn != null) break;
                }
                if (flow != null) foreach (var port in flow.AllPorts)
                {
                    if (port == null || port.id == null) continue;
                    var name = port.id.Replace("_", "").Replace(".", "").Replace("-", "").ToLowerInvariant();
                    if (cab.EngineOn == null && name.Contains("engineon")) cab.EngineOn = port;
                    if (name.Contains("enginerpm")) cab.EngineRpm = port;
                    if ((name.Contains("engine") || name.Contains("diesel")) && name.Contains("temperature")) cab.EngineTemperature = port;
                }
            }
            var controllers = new HashSet<DoorsAndWindowsController>();
            var openings = new HashSet<OpenableControl>();
            foreach (var root in new[] { cab.Car.gameObject, interior, external, persistent })
            {
                if (root == null) continue;
                foreach (var controller in root.GetComponentsInChildren<DoorsAndWindowsController>(true))
                {
                    controllers.Add(controller);
                    if (controller.entries != null) foreach (var opening in controller.entries)
                        if (opening != null) openings.Add(opening);
                }
                foreach (var opening in root.GetComponentsInChildren<OpenableControl>(true)) openings.Add(opening);
            }
            cab.Openings = new DoorsAndWindowsController[controllers.Count]; controllers.CopyTo(cab.Openings);
            cab.Openables = new OpenableControl[openings.Count]; openings.CopyTo(cab.Openables);
            cab.OpeningControls = new ControlImplBase[cab.Openables.Length];
            for (int i = 0; i < cab.Openables.Length; i++)
                cab.OpeningControls[i] = cab.Openables[i].GetComponent<ControlImplBase>();
            cab.Heater = null;
            if (interior == null) return;
            foreach (var control in interior.GetComponentsInChildren<ControlImplBase>(true))
            {
                string name = control.name.ToLowerInvariant();
                string parent = control.transform.parent != null ? control.transform.parent.name.ToLowerInvariant() : "";
                if (name.Contains("cabheater") || name.Contains("heating") || parent.Contains("cabheater") || parent.Contains("heating"))
                { cab.Heater = control; break; }
            }
        }
        private static Matrix4x4 MovingPaneMatrix(Pane pane)
        {
            for(int i=0;i<pane.Originals.Count;i++)
                if(pane.Originals[i]!=null && pane.VisualWindows[i]==pane.Window)
                    return pane.VisualToPane[i]*pane.Originals[i].transform.worldToLocalMatrix;
            return pane.Frame.worldToLocalMatrix;
        }

        private static void InitializeWipers(Pane pane)
        {
            int count=pane.Window==null || pane.Window.wipers==null?0:pane.Window.wipers.Length;
            pane.LastStarts=new Vector2[count];pane.LastEnds=new Vector2[count];pane.WiperReady=new bool[count];
        }
        private static Vector2 UV(Pane pane, Vector3 world)
        {
            var window=pane.Window;
            Vector3 p = MovingPaneMatrix(pane).MultiplyPoint3x4(world);
            return new Vector2(p.x / Mathf.Max(.01f, window.sizeInMeters.x) * (window.mirrorX ? -1 : 1) + .5f,
                p.y / Mathf.Max(.01f, window.sizeInMeters.y) * (window.mirrorY ? -1 : 1) + .5f);
        }
        private void SetProperties(Pane pane, int index)
        {
            var window = pane.VisualWindows[index];
            if (window == null) window = pane.Window;
            var climate = pane.Cab.Climate;
            pane.Properties.SetTexture("_SnowMask", pane.Mask);
            pane.Properties.SetTexture("_FrostPattern", frostPattern);
            // This matrix is fixed in the mesh's local coordinates. Sampling a
            // world inverse here races late train/door animation and origin shifts.
            pane.Properties.SetMatrix("_MeshToPane", pane.VisualToPane[index]);
            pane.Properties.SetFloat("_UseBakedUVs", window != null && window.useBakedUVs ? 1 : 0);
            var size = window != null ? window.sizeInMeters : pane.FallbackSize;
            pane.Properties.SetVector("_PaneSize", new Vector4(size.x, size.y,
                window != null && window.mirrorX ? -1 : 1, window != null && window.mirrorY ? -1 : 1));
            pane.Properties.SetVector("_Climate", new Vector4(climate.Frost, climate.Fog,
                climate.Stage == WinterGlassStage.Thawing ? 1 : 0, climate.GlassTemperature));
            pane.Properties.SetFloat("_Daylight", lighting);
        }
        private static void UpdateMask(Pane pane, float dt)
        {
            // Quantized channels with time-based decay. No per-pixel physics and
            // no GPU readback: one small texture shared by a window's duplicates.
            pane.MeltRemainder += pane.Cab.Climate.GlassTemperature > 0 ? dt * 255 / 18 : 0;
            pane.RefreezeRemainder += pane.Cab.Climate.GlassTemperature < -1 ? dt * 255 / 120 : 0;
            int snowMelt = (int)pane.MeltRemainder, refreeze = (int)pane.RefreezeRemainder;
            pane.MeltRemainder -= snowMelt; pane.RefreezeRemainder -= refreeze;
            if (pane.Cab.Climate.GlassTemperature >= WindowWinterClimate.ClearGlassTemperature) snowMelt=255;
            // Seasonal transitions can leave a saved mask from a freezing cab
            // while the new outside air is already mild.  Clear that deposited
            // layer immediately above +5 C; the thermal crystal layer remains
            // governed by the climate model and can still thaw smoothly.
            if (pane.Cab.AmbientTemperature >= 5f) snowMelt=255;
            if(!pane.MaskChanged && (snowMelt==0 || !pane.Dirty) && (refreeze==0 || !pane.HasWipes)) return;
            bool changed = pane.MaskChanged;
            bool any = false, wiped=false;
            for (int i = 0; i < pane.Pixels.Length; i++)
            {
                var p = pane.Pixels[i];
                changed |= (p.r > 0 && snowMelt > 0) || (p.g > 0 && refreeze > 0);
                p.r = (byte)Math.Max(0, p.r - snowMelt); p.g = (byte)Math.Max(0, p.g - refreeze);
                pane.Pixels[i] = p; any |= p.r > 0; wiped |= p.g > 0;
            }
            pane.Dirty = any;
            pane.HasWipes=wiped;pane.MaskChanged=false;
            if (changed) { pane.Mask.SetPixels32(pane.Pixels); pane.Mask.Apply(false, false); }
        }
        private static void Wipe(Pane pane)
        {
            var wipers = pane.Window != null ? pane.Window.wipers : null;
            if (wipers == null) return;
            for (int w = 0; w < Math.Min(wipers.Length, pane.WiperReady.Length); w++)
            {
                var wiper = wipers[w]; if (wiper == null || wiper.start == null || wiper.end == null || wiper.disableCollision) continue;
                var a = UV(pane, wiper.start.position); var b = UV(pane, wiper.end.position);
                if (pane.WiperReady[w] && ((a - pane.LastStarts[w]).sqrMagnitude + (b - pane.LastEnds[w]).sqrMagnitude > .0000001f))
                {
                    var c = pane.LastStarts[w]; var d = pane.LastEnds[w];
                    WipeTriangle(pane,a,b,c);
                    WipeTriangle(pane,b,c,d);
                }
                pane.LastStarts[w]=a;pane.LastEnds[w]=b;pane.WiperReady[w]=true;
            }
        }
        private static void WipeTriangle(Pane pane, Vector2 a, Vector2 b, Vector2 c)
        {
            if (Mathf.Abs(Cross(b-a,c-a)) < .00000001f) return;
            int y0=Math.Max(0,Mathf.CeilToInt(Mathf.Min(a.y,Mathf.Min(b.y,c.y))*Resolution-.5f));
            int y1=Math.Min(Resolution-1,Mathf.FloorToInt(Mathf.Max(a.y,Mathf.Max(b.y,c.y))*Resolution-.5f));
            bool thawed=pane.Cab.Climate.GlassTemperature>0;
            // Intersect three edges once per row instead of testing two triangles
            // for every pixel in the sweep's bounding rectangle.
            for(int y=y0;y<=y1;y++)
            {
                float row=(y+.5f)/Resolution, left=float.PositiveInfinity,right=float.NegativeInfinity;
                WipeEdge(a,b,row,ref left,ref right); WipeEdge(b,c,row,ref left,ref right); WipeEdge(c,a,row,ref left,ref right);
                if(left>right) continue;
                int x0=Math.Max(0,Mathf.CeilToInt(left*Resolution-.5f));
                int x1=Math.Min(Resolution-1,Mathf.FloorToInt(right*Resolution-.5f));
                for(int x=x0;x<=x1;x++)
                {
                    int index=y*Resolution+x;var colour=pane.Pixels[index];
                    if(colour.r==0 && (!thawed || colour.g==255)) continue;
                    colour.r=0;if(thawed)colour.g=255;
                    pane.Pixels[index]=colour;pane.MaskChanged=true;
                }
            }
        }
        private static void WipeEdge(Vector2 a,Vector2 b,float y,ref float left,ref float right)
        {
            if(y<Mathf.Min(a.y,b.y) || y>Mathf.Max(a.y,b.y)) return;
            if(Mathf.Abs(b.y-a.y)<.00000001f)
            {left=Mathf.Min(left,Mathf.Min(a.x,b.x));right=Mathf.Max(right,Mathf.Max(a.x,b.x));return;}
            float x=a.x+(y-a.y)*(b.x-a.x)/(b.y-a.y);
            left=Mathf.Min(left,x);right=Mathf.Max(right,x);
        }
        private static float Cross(Vector2 a, Vector2 b) { return a.x*b.y-a.y*b.x; }
        private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float area=Cross(b-a,c-a); if (Mathf.Abs(area)<.00000001f) return false;
            float u=Cross(b-a,p-a),v=Cross(c-b,p-b),w=Cross(a-c,p-c);
            return (u>=0 && v>=0 && w>=0) || (u<=0 && v<=0 && w<=0);
        }
        public bool Collide(Vector3 from, Vector3 to, float seconds)
        {
            foreach (var pane in collisionPanes)
            {
                var window=pane.Window;
                if((to-pane.CollisionCentre).sqrMagnitude>400) continue;
                // Test relative movement so a moving locomotive intercepts flakes
                // even when the particle's own horizontal velocity is near zero.
                var matrix=pane.CollisionMatrix;
                var localTo=matrix.MultiplyPoint3x4(to);
                var localFrom=matrix.MultiplyPoint3x4(from+pane.Velocity*seconds);
                if(localFrom.z*localTo.z>0 || Mathf.Abs(localFrom.z-localTo.z)<.00001f) continue;
                float t=localFrom.z/(localFrom.z-localTo.z);
                var hit=Vector3.Lerp(localFrom,localTo,t);
                if(Mathf.Abs(hit.x)>pane.Size.x*.5f || Mathf.Abs(hit.y)>pane.Size.y*.5f) continue;
                var uv=new Vector2(hit.x/Mathf.Max(.01f,pane.Size.x)*(window!=null && window.mirrorX?-1:1)+.5f,
                    hit.y/Mathf.Max(.01f,pane.Size.y)*(window!=null && window.mirrorY?-1:1)+.5f);
                if(Time.time>=pane.NextShelter)
                { pane.NextShelter=Time.time+.5f;pane.Sheltered=Physics.Raycast(matrix.inverse.MultiplyPoint3x4(Vector3.zero)+Vector3.up*3.1f,Vector3.up,35,SeasonSurfaceLayers.Mask,QueryTriggerInteraction.Ignore); }
                if(!pane.Sheltered && pane.Cab.Climate.GlassTemperature<4 && snowfall>0)
                {
                    int cx=Mathf.Clamp((int)(uv.x*Resolution),0,Resolution-1),cy=Mathf.Clamp((int)(uv.y*Resolution),0,Resolution-1);
                    for(int y=Math.Max(0,cy-2);y<=Math.Min(Resolution-1,cy+2);y++)
                        for(int x=Math.Max(0,cx-2);x<=Math.Min(Resolution-1,cx+2);x++)
                        {
                            float d=Mathf.Sqrt((x-cx)*(x-cx)+(y-cy)*(y-cy)); if(d>2.3f) continue;
                            int i=y*Resolution+x;var p=pane.Pixels[i];p.r=(byte)Math.Min(255,p.r+(int)((1-d/2.5f)*170));pane.Pixels[i]=p;
                        }
                    pane.Dirty=true;pane.MaskChanged=true;
                }
                return true;
            }
            return false;
        }
        public bool IsOutsideCab(Vector3 camera, Vector3 particle)
        {
            // Cab clearing must not delete flakes approaching the outside of a
            // windshield. Use the nearby cab's window planes as its enclosure.
            Cab nearest = cameraCab;
            if (nearest == null) return false;
            foreach (var pane in collisionPanes)
            {
                if (pane.Cab != nearest || pane.Window == null) continue;
                var matrix = pane.CollisionMatrix;
                float cameraSide = matrix.MultiplyPoint3x4(camera).z;
                float particleSide = matrix.MultiplyPoint3x4(particle).z;
                if (Mathf.Abs(cameraSide) > .03f && cameraSide * particleSide < 0) return true;
            }
            return false;
        }
        private void Release(Pane pane)
        {
            foreach(var original in pane.Originals) boundVisuals.Remove(original);
            for(int n=0;n<pane.Overlays.Count;n++)
            {
                var group=pane.LodGroups[n]; if(group==null) continue;
                var levels=group.GetLODs(); bool changed=false;
                for(int i=0;i<levels.Length;i++)
                {
                    var renderers=new List<Renderer>(levels[i].renderers);
                    if(!renderers.Remove(pane.Overlays[n])) continue;
                    levels[i].renderers=renderers.ToArray();changed=true;
                }
                if(changed) group.SetLODs(levels);
            }
            foreach(var renderer in pane.Overlays) if(renderer!=null) UnityEngine.Object.Destroy(renderer.gameObject);
            if(pane.Mask!=null) UnityEngine.Object.Destroy(pane.Mask);
            if(pane.Material!=null) UnityEngine.Object.Destroy(pane.Material);
        }
        public void Dispose()
        {
            collisionPanes.Clear(); cameraCab=null;
            if(frostPattern!=null) UnityEngine.Object.Destroy(frostPattern); frostPattern=null;
            if(active == this) active = null;
            foreach(var pane in panes) Release(pane);
            if (discovery != null) discovery.Dispose();
            discovery = null;
            panes.Clear();known.Clear();boundVisuals.Clear();cabs.Clear();removed.Clear();savedClimates.Clear();savedMasks.Clear();shader=null;nextScan=nextClimate=lastClimate=0;cursor=0;reported=false;
        }
    }
}

