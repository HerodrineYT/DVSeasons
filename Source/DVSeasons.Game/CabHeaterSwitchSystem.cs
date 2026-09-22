using System;
using System.Collections;
using System.Collections.Generic;
using DV;
using DV.CabControls;
using DV.CabControls.Spec;
using DV.HUD;
using DV.ThingTypes;
using DV.Utils;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    /// <summary>
    /// Activates native heater geometry in build-99 DE2, DM3, DH4 and DE6
    /// interiors. Ensure is intentionally called only when the cached car/interior context
    /// changes; this class never scans the scene from Update.
    /// </summary>
    internal sealed class CabHeaterSwitchSystem : IDisposable
    {
        private const string GeneratedRootName = "DVSeasons_CabHeaterSwitch";
        private const string TemplatePath = "PanelCluster/CabHeater";
        private const string TemplateControlPath = "C_CabHeater";
        private const string TemplateModelPath = "switch_cooler model";
        private const int MaximumSetupAttempts = 3;

        private readonly Action<string, float> onUserToggle;
        private readonly Dictionary<string, float> states =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SwitchBinding> bindings =
            new Dictionary<string, SwitchBinding>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, int> failedInteriorAttempts =
            new Dictionary<int, int>();
        private readonly Dictionary<Mesh, NativeHeaterGeometry> geometryCache =
            new Dictionary<Mesh, NativeHeaterGeometry>();
        private bool disposed;

        public CabHeaterSwitchSystem(Action<string, float> onUserToggle)
        {
            this.onUserToggle = onUserToggle;
        }

        public bool Ensure(TrainCar car)
        {
            if (disposed || !IsSupported(car) || car.carType == TrainCarType.LocoDM1U || car.loadedInterior == null) return false;
            var carId = GetCarId(car);
            if (string.IsNullOrEmpty(carId)) return false;

            var interior = car.loadedInterior;
            // The instantiator is created by the game during control bootstrap. Treat its brief
            // absence as transient so the next context sample may retry this interior.
            if (SingletonBehaviour<ControlsInstantiatorBase>.Instance == null) return false;
            SwitchBinding current;
            if (bindings.TryGetValue(carId, out current))
            {
                if (current.IsAlive && current.Interior == interior) return true;
                current.Detach(true);
                bindings.Remove(carId);
            }

            var interiorId = interior.GetInstanceID();
            int previousFailures;
            if (failedInteriorAttempts.TryGetValue(interiorId, out previousFailures) &&
                previousFailures >= MaximumSetupAttempts)
                return false;

            try
            {
                var profile = SwitchProfile.For(car.carType);
                CreatedSwitch created;
                if (!TryCreateSwitch(interior.transform, profile, out created))
                {
                    RecordSetupFailure(interiorId);
                    Debug.LogWarning("[DVSeasons] Cab heater switch could not be created for " +
                        car.carType + ".");
                    return false;
                }

                created.Marker.Bind(this, carId);
                var lampTransform = car.carType == TrainCarType.LocoDH4 ?
                    FindRelative(interior.transform, "RightCluster/CabHeater/L_CabHeater") : null;
                var binding = new SwitchBinding(this, carId, interior, created.Container,
                    created.Marker, created.Control, created.HiddenNativeRenderer,
                    created.BodyFilter, created.OriginalBodyMesh,
                    lampTransform == null ? null : lampTransform.GetComponent<Renderer>());
                bindings[carId] = binding;
                failedInteriorAttempts.Remove(interiorId);

                float level;
                if (!states.TryGetValue(carId, out level)) states[carId] = 0f;
                binding.SetValue(level);
                return true;
            }
            catch (Exception exception)
            {
                RecordSetupFailure(interiorId);
                Debug.LogWarning("[DVSeasons] Cab heater switch setup failed for " + car.carType +
                    ": " + exception.Message);
                return false;
            }
        }

        /// <summary>
        /// Returns true after setup succeeded or after a build-mismatch failure was retried a
        /// bounded number of times. EnvironmentSampler uses this to stop rescanning an
        /// incompatible interior while still allowing transient bootstrap failures to recover.
        /// </summary>
        public bool IsSetupSettled(TrainCar car)
        {
            if (disposed || !IsSupported(car) || car.loadedInterior == null) return false;
            var carId = GetCarId(car);
            SwitchBinding binding;
            if (!string.IsNullOrEmpty(carId) && bindings.TryGetValue(carId, out binding) &&
                binding.IsAlive && binding.Interior == car.loadedInterior)
                return true;

            int failures;
            return failedInteriorAttempts.TryGetValue(car.loadedInterior.GetInstanceID(),
                out failures) && failures >= MaximumSetupAttempts;
        }

        public bool IsOn(TrainCar car) { return GetLevel(car) > 0f; }

        public float GetLevel(TrainCar car)
        {
            return IsSupported(car) ? GetLevel(GetCarId(car)) : 0f;
        }

        public float GetLevel(string carId)
        {
            if (string.IsNullOrEmpty(carId)) return 0f;
            float value;
            return states.TryGetValue(carId, out value) ? value : 0f;
        }

        public void ApplyConfirmed(string carId, float level)
        {
            if (disposed || string.IsNullOrEmpty(carId)) return;
            level = CabHeaterSetting.NormalizeSwitch(level);
            states[carId] = level;
            SwitchBinding binding;
            if (bindings.TryGetValue(carId, out binding) && binding.IsAlive)
                binding.SetValue(level);
        }

        public void Reset()
        {
            var snapshot = new List<SwitchBinding>(bindings.Values);
            bindings.Clear();
            failedInteriorAttempts.Clear();
            states.Clear();
            for (int i = 0; i < snapshot.Count; i++)
            {
                snapshot[i].SetValue(0f);
                snapshot[i].Detach(true);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            Reset();
            foreach (var geometry in geometryCache.Values) geometry.Dispose();
            geometryCache.Clear();
            disposed = true;
        }

        public static bool IsSupported(TrainCar car)
        {
            if (car == null || CabEngineHeating.IsCustomLocomotive(car)) return false;
            return car.carType == TrainCarType.LocoShunter ||
                car.carType == TrainCarType.LocoDM1U ||
                car.carType == TrainCarType.LocoDH4 ||
                car.carType == TrainCarType.LocoDM3 ||
                car.carType == TrainCarType.LocoDiesel;
        }

        private static string GetCarId(TrainCar car)
        {
            if (car == null) return string.Empty;
            try { return car.CarGUID ?? string.Empty; }
            catch { return string.Empty; }
        }

        private void RecordSetupFailure(int interiorId)
        {
            int failures;
            failedInteriorAttempts.TryGetValue(interiorId, out failures);
            failedInteriorAttempts[interiorId] = Mathf.Min(failures + 1,
                MaximumSetupAttempts);
        }

        private void HandleUserToggle(SwitchBinding binding, float level)
        {
            if (disposed || binding == null || !binding.IsAlive) return;
            level = CabHeaterSetting.FromControlValue(level);
            if (GetLevel(binding.CarId) == level) return;
            states[binding.CarId] = level;
            if (onUserToggle == null) return;
            try { onUserToggle(binding.CarId, level); }
            catch (Exception exception)
            {
                Debug.LogWarning("[DVSeasons] Cab heater switch callback failed: " +
                    exception.Message);
            }
        }

        internal void HandleMarkerDestroyed(CabHeaterSwitchMarker marker, string carId)
        {
            if (marker == null || string.IsNullOrEmpty(carId)) return;
            SwitchBinding binding;
            if (!bindings.TryGetValue(carId, out binding) || binding.Marker != marker) return;
            bindings.Remove(carId);
            binding.Detach(false);
        }

        internal void CompleteControlInitialization(CabHeaterSwitchMarker marker, string carId)
        {
            SwitchBinding binding;
            if (disposed || string.IsNullOrEmpty(carId) ||
                !bindings.TryGetValue(carId, out binding) || binding.Marker != marker) return;
            binding.SetValue(GetLevel(carId));
            binding.Ready = true;
        }

        private bool TryCreateSwitch(Transform interior, SwitchProfile profile,
            out CreatedSwitch created)
        {
            created = default(CreatedSwitch);
            var nativeVisual = FindRelative(interior, profile.NativeVisualPath);
            if (nativeVisual == null) return false;
            var sourceFilter = nativeVisual.GetComponent<MeshFilter>();
            var sourceRenderer = nativeVisual.GetComponent<MeshRenderer>();
            if (sourceFilter == null || sourceFilter.sharedMesh == null || sourceRenderer == null)
                return false;
            var originalMesh = sourceFilter.sharedMesh;
            NativeHeaterGeometry geometry = null;
            if (profile.SplitNativeDetail && !geometryCache.TryGetValue(originalMesh, out geometry))
            {
                geometry = NativeHeaterGeometry.Create(originalMesh, profile.Rotary);
                geometryCache.Add(originalMesh, geometry);
            }

            var template = GetControlTemplate(profile.Rotary);
            if (template == null) return false;
            var container = new GameObject(GeneratedRootName + "_" + profile.Suffix);
            container.SetActive(false);
            container.transform.SetParent(interior, false);
            MeshRenderer hiddenRenderer = null;
            MeshFilter changedBody = null;
            CabHeaterSwitchMarker materialOwner = null;
            try
            {
                var clone = UnityEngine.Object.Instantiate(template, container.transform, false);
                clone.name = "CabHeater_Control";
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;
                clone.transform.localScale = Vector3.one;
                clone.SetActive(true);
                var controlRoot = FindRelative(clone.transform,
                    profile.Rotary ? "C_Heating Rotary" : TemplateControlPath);
                var modelRoot = controlRoot == null ? null : FindRelative(controlRoot,
                    profile.Rotary ? "Heating Model" : TemplateModelPath);
                if (controlRoot == null || modelRoot == null)
                    throw new InvalidOperationException("Stock heater control template is incomplete.");
                RemoveSimulationFeeders(clone);
                foreach (var holder in controlRoot.GetComponents<ControlNameHolderBase>())
                    UnityEngine.Object.DestroyImmediate(holder);
                controlRoot.gameObject.AddComponent<CabHeaterControlNameHolder>();
                var marker = controlRoot.gameObject.AddComponent<CabHeaterSwitchMarker>();
                materialOwner = marker;
                var modelFilter = modelRoot.GetComponent<MeshFilter>();
                var modelRenderer = modelRoot.GetComponent<MeshRenderer>();
                if (modelFilter == null || modelRenderer == null)
                    throw new InvalidOperationException("Stock heater model template is incomplete.");

                if (profile.Rotary)
                {
                    var rotary = controlRoot.GetComponent<Rotary>();
                    if (rotary == null || modelFilter.sharedMesh == null)
                        throw new InvalidOperationException("DM1U Heating Rotary template is missing.");
                    rotary.notches = 2;
                    rotary.useSteppedJoint = true;
                    rotary.useLimits = true;
                    rotary.jointLimitMin = 0f;
                    rotary.jointLimitMax = 30f;
                    rotary.jointStartingPos = 0f;
                    rotary.invertDirection = false;
                    rotary.jointAxis = Vector3.forward;
                    rotary.scrollWheelUseSpringRotation = true;
                    rotary.scrollWheelHoverScroll = 1f;
                    // Match the existing DM3 knob's 41 mm diameter; scale its stock interaction
                    // geometry together with the DM1U model rather than leave an oversized hitbox.
                    var width = modelFilter.sharedMesh.bounds.size.x;
                    if (width < 0.001f) throw new InvalidOperationException("Invalid DM1U knob bounds.");
                    container.transform.localScale = Vector3.one * (0.041416f / width);
                }
                else
                {
                    var toggle = controlRoot.GetComponent<ToggleSwitch>();
                    if (toggle == null) throw new InvalidOperationException("Missing stock ToggleSwitch.");
                    modelFilter.sharedMesh = geometry != null ? geometry.Detail : originalMesh;
                    modelRenderer.sharedMaterials = sourceRenderer.sharedMaterials;
                    modelRenderer.enabled = true;
                    modelRoot.localPosition = Vector3.zero;
                    modelRoot.localRotation = Quaternion.identity;
                    modelRoot.localScale = Vector3.one;
                    toggle.jointAxis = profile.JointAxis;
                    toggle.jointLimitMin = profile.JointLimitMin;
                    toggle.jointLimitMax = profile.JointLimitMax;
                    toggle.autoOffTimer = 0f;
                    toggle.disableTouchUse = false;
                    toggle.touchInteractionAxis = profile.SplitNativeDetail ? Vector3.forward : Vector3.up;
                    FitToggleCollider(toggle, controlRoot, modelFilter.sharedMesh.bounds);
                }

                // The loaded cab may already use temporary winter materials. The
                // new renderer is not in that subsystem's current binding list,
                // so borrowing those materials would leave it pink on Release.
                marker.OwnVisualMaterials(modelRenderer);

                var desiredPosition = geometry == null ? nativeVisual.position :
                    nativeVisual.TransformPoint(geometry.Pivot);
                var desiredRotation = nativeVisual.rotation;
                var controlLocalPosition = container.transform.InverseTransformPoint(controlRoot.position);
                var controlLocalRotation = Quaternion.Inverse(container.transform.rotation) * controlRoot.rotation;
                container.transform.rotation = desiredRotation * Quaternion.Inverse(controlLocalRotation);
                container.transform.position = desiredPosition -
                    container.transform.TransformVector(controlLocalPosition);

                if (geometry != null)
                {
                    changedBody = sourceFilter;
                    sourceFilter.sharedMesh = geometry.Body;
                }
                else
                {
                    hiddenRenderer = sourceRenderer;
                    hiddenRenderer.enabled = false;
                }
                // Awake chooses the game's own desktop or VR implementation after the complete
                // spec, collider, stock visual and name holder have been prepared.
                container.SetActive(true);
                var control = controlRoot.GetComponent<ControlImplBase>();
                if (control == null) throw new InvalidOperationException("Heater control failed to initialize.");
                created = new CreatedSwitch(container, marker, control, hiddenRenderer,
                    changedBody, originalMesh);
                return true;
            }
            catch
            {
                if (hiddenRenderer != null) hiddenRenderer.enabled = true;
                if (changedBody != null) changedBody.sharedMesh = originalMesh;
                // OnDestroy is not guaranteed for a never-activated hierarchy.
                if (materialOwner != null) materialOwner.ReleaseVisualMaterials();
                if (container != null) UnityEngine.Object.Destroy(container);
                throw;
            }
        }

        private static void FitToggleCollider(ToggleSwitch spec, Transform root, Bounds bounds)
        {
            // Keep serialized collider GameObjects/VR references, but fit the collision shape
            // to the actual stock rocker instead of the differently shaped DM3 template.
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.DestroyImmediate(collider);
            var hit = new GameObject("Stock heater rocker collider");
            hit.layer = root.gameObject.layer;
            hit.transform.SetParent(root, false);
            var box = hit.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = bounds.size;
            spec.colliderGameObjects = new[] { hit };
            // The inherited sibling area is much larger than these small rocker switches.
            // Keep the native deferred setup and fit its child collider to the same profile.
            if (spec.nonVrStaticInteractionArea != null)
            {
                var area = spec.nonVrStaticInteractionArea.transform;
                area.localPosition = Vector3.zero;
                area.localRotation = Quaternion.identity;
                foreach (var collider in area.GetComponentsInChildren<Collider>(true))
                    UnityEngine.Object.DestroyImmediate(collider);
                var areaBox = area.gameObject.AddComponent<BoxCollider>();
                areaBox.center = bounds.center;
                areaBox.size = bounds.size + Vector3.one * 0.006f;
                areaBox.isTrigger = true;
            }
        }

        private static GameObject GetControlTemplate(bool rotary)
        {
            if (Globals.G == null || Globals.G.Types == null) return null;
            TrainCarLivery livery;
            if (!Globals.G.Types.TrainCarType_to_v2.TryGetValue(
                rotary ? TrainCarType.LocoDM1U : TrainCarType.LocoDM3, out livery) ||
                livery == null || livery.interiorPrefab == null) return null;
            var template = FindRelative(livery.interiorPrefab.transform,
                rotary ? "RightCluster/Heating Rotary" : TemplatePath);
            return template == null ? null : template.gameObject;
        }

        private static void RemoveSimulationFeeders(GameObject root)
        {
            var behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                var behaviour = behaviours[i];
                if (behaviour != null && string.Equals(behaviour.GetType().FullName,
                    "DV.Simulation.Ports.InteractablePortFeeder", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(behaviour);
            }
        }

        private static Transform FindRelative(Transform root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return null;
            return root.Find(path);
        }

        private sealed class SwitchBinding
        {
            private readonly CabHeaterSwitchSystem owner;
            private readonly ControlImplBase control;
            private readonly MeshRenderer hiddenNativeRenderer;
            private readonly MeshFilter bodyFilter;
            private readonly Mesh originalBodyMesh;
            private readonly CabHeaterLamp lamp;
            private bool suppress;
            private bool attached;
            public bool Ready { get; set; }

            public SwitchBinding(CabHeaterSwitchSystem owner, string carId, GameObject interior,
                GameObject container, CabHeaterSwitchMarker marker, ControlImplBase control,
                MeshRenderer hiddenNativeRenderer, MeshFilter bodyFilter, Mesh originalBodyMesh, Renderer lampRenderer)
            {
                this.owner = owner;
                CarId = carId;
                Interior = interior;
                Container = container;
                Marker = marker;
                this.control = control;
                this.hiddenNativeRenderer = hiddenNativeRenderer;
                this.bodyFilter = bodyFilter;
                this.originalBodyMesh = originalBodyMesh;
                lamp = new CabHeaterLamp(lampRenderer);
                control.ValueChanged += OnValueChanged;
                attached = true;
            }

            public string CarId { get; private set; }
            public GameObject Interior { get; private set; }
            public GameObject Container { get; private set; }
            public CabHeaterSwitchMarker Marker { get; private set; }
            public bool IsAlive
            {
                get { return Interior != null && Container != null && control != null; }
            }

            public void SetValue(float level)
            {
                if (!IsAlive) return;
                suppress = true;
                try { control.SetValue(level); lamp.Set(level); }
                finally { suppress = false; }
            }

            public void Detach(bool destroyContainer)
            {
                if (attached)
                {
                    try { control.ValueChanged -= OnValueChanged; }
                    catch { }
                    attached = false;
                }
                if (Marker != null) Marker.Unbind(owner);
                lamp.Dispose();
                if (hiddenNativeRenderer != null) hiddenNativeRenderer.enabled = true;
                if (bodyFilter != null && originalBodyMesh != null) bodyFilter.sharedMesh = originalBodyMesh;
                if (destroyContainer && Container != null)
                    UnityEngine.Object.Destroy(Container);
            }

            private void OnValueChanged(ValueChangedEventArgs args)
            {
                if (Ready && !suppress) owner.HandleUserToggle(this, args.newValue);
            }
        }

        private struct CreatedSwitch
        {
            public readonly GameObject Container;
            public readonly CabHeaterSwitchMarker Marker;
            public readonly ControlImplBase Control;
            public readonly MeshRenderer HiddenNativeRenderer;
            public readonly MeshFilter BodyFilter;
            public readonly Mesh OriginalBodyMesh;

            public CreatedSwitch(GameObject container, CabHeaterSwitchMarker marker,
                ControlImplBase control, MeshRenderer hiddenNativeRenderer,
                MeshFilter bodyFilter, Mesh originalBodyMesh)
            {
                Container = container;
                Marker = marker;
                Control = control;
                HiddenNativeRenderer = hiddenNativeRenderer;
                BodyFilter = bodyFilter;
                OriginalBodyMesh = originalBodyMesh;
            }
        }

        private struct SwitchProfile
        {
            public string Suffix;
            public string NativeVisualPath;
            public Vector3 JointAxis;
            public float JointLimitMin;
            public float JointLimitMax;
            public bool SplitNativeDetail;
            public bool Rotary;

            public static SwitchProfile For(TrainCarType type)
            {
                if (type == TrainCarType.LocoDH4)
                {
                    return new SwitchProfile
                    {
                        Suffix = "DH4",
                        NativeVisualPath = "RightCluster/CabHeater/C_CabHeater",
                        JointAxis = Vector3.right,
                        JointLimitMin = 0f,
                        JointLimitMax = 20f
                    };
                }
                if (type == TrainCarType.LocoDiesel)
                {
                    return new SwitchProfile
                    {
                        Suffix = "DE6",
                        NativeVisualPath = "CabHeater",
                        JointAxis = Vector3.up,
                        JointLimitMin = -10f,
                        JointLimitMax = 10f
                    };
                }
                if (type == TrainCarType.LocoDM3)
                    return new SwitchProfile
                    {
                        Suffix = "DM3", NativeVisualPath = "Cab",
                        SplitNativeDetail = true, Rotary = true
                    };
                return new SwitchProfile
                {
                    Suffix = "DE2", NativeVisualPath = "Deck",
                    JointAxis = Vector3.right,
                    JointLimitMin = 0f, JointLimitMax = 20f,
                    SplitNativeDetail = true
                };
            }
        }
    }

    internal sealed class CabHeaterSwitchMarker : MonoBehaviour
    {
        private CabHeaterSwitchSystem owner;
        private string carId;
        private Material[] visualMaterials;

        internal void OwnVisualMaterials(Renderer renderer)
        {
            var sources = renderer.sharedMaterials;
            visualMaterials = new Material[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                var source = sources[i];
                if (source == null) throw new InvalidOperationException("Heater visual material is unavailable.");
                var material = new Material(source)
                {
                    name = source.name + " [DVSeasons cab control]",
                    hideFlags = HideFlags.HideAndDontSave
                };
                visualMaterials[i] = material;
                // This variant is exclusively a replacement for Standard. Keep
                // the current paint/texture values, but not its temporary car ID
                // or dependence on the seasonal GPU buffers. Later snow discovery
                // can register this renderer normally and restore this owned copy.
                if (source.shader != null && source.shader.name == "Hidden/DVSeasons/SnowVehicleStandard")
                {
                    var shader = Shader.Find("Standard");
                    if (shader == null) throw new InvalidOperationException("Stock Standard shader is unavailable.");
                    material.shader = shader;
                    material.shaderKeywords = source.shaderKeywords;
                    material.renderQueue = source.renderQueue;
                }
            }
            renderer.sharedMaterials = visualMaterials;
        }

        internal void ReleaseVisualMaterials()
        {
            if (visualMaterials != null) foreach (var material in visualMaterials)
                if (material != null)
                {
                    if (Application.isPlaying) Destroy(material);
                    else DestroyImmediate(material);
                }
            visualMaterials = null;
        }

        internal void Bind(CabHeaterSwitchSystem newOwner, string newCarId)
        {
            owner = newOwner;
            carId = newCarId;
        }

        internal void Unbind(CabHeaterSwitchSystem expectedOwner)
        {
            if (owner != expectedOwner) return;
            owner = null;
            carId = null;
        }

        private IEnumerator Start()
        {
            // Rotary/HingeJoint and SteppedJoint finish their stock initialization in Start.
            // Reapply the confirmed setting afterward, before accepting physical changes.
            yield return null;
            yield return new WaitForEndOfFrame();
            if (owner != null) owner.CompleteControlInitialization(this, carId);
        }

        private void OnDestroy()
        {
            var currentOwner = owner;
            owner = null;
            try { if (currentOwner != null) currentOwner.HandleMarkerDestroyed(this, carId); }
            finally
            {
                // Also runs when setup failed before Bind, or the streamed cab
                // disappears without the service getting another Ensure call.
                ReleaseVisualMaterials();
            }
        }
    }

    internal sealed class CabHeaterControlNameHolder : ControlNameHolderBase
    {
        private ControlImplBase control;

        public override (string value, string unit) GetName()
        {
            if (control == null) control = GetComponent<ControlImplBase>();
            var level = control == null ? 0f : CabHeaterSetting.FromControlValue(control.Value);
            if (level <= 0f) return (ModLocalization.Text("Heater.Off"), string.Empty);
            return (ModLocalization.Text("Heater.On"), string.Empty);
        }
    }
}
