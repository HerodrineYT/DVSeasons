using System;
using System.Collections;
using System.IO;
using System.Reflection;
using DV.CabControls;
using DV.CabControls.Spec;
using DV.Interaction;
using DV.ThingTypes;
using HarmonyLib;
using UnityEngine;

// Uses the real switch factory and native snow material Bind/Release lifecycle.
// Only the desktop/VR input bootstrap is replaced: no input device is needed.
public static class VerifyHeaterMaterial
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly mod;
    static GameObject template;
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static object Make(string type) => Activator.CreateInstance(mod.GetType("DVSeasons.Mod." + type, true), true);
    static object Get(object value, string field) => value.GetType().GetField(field, All).GetValue(value);
    static void Set(object value, string field, object data) => value.GetType().GetField(field, All).SetValue(value, data);
    static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, All).Invoke(value, args);
    static bool Template(ref GameObject __result) { __result = template; return false; }
    static bool SkipAwake() => false;
    static GameObject Child(string name, Transform parent)
    { var go = new GameObject(name); go.transform.SetParent(parent, false); return go; }
    static object Part(MeshRenderer renderer)
    {
        var part = Make("SnowVehicleRegistry+Part");
        Set(part, "Renderer", renderer); Set(part, "Interior", true);
        Set(part, "Opaque", new[] { true }); Set(part, "HasOpaque", true);
        return part;
    }
    public static void Run(string path, Shader snowShader)
    {
        mod = Assembly.LoadFrom(Path.Combine(path, "DVSeasons.dll"));
        var harmony = new Harmony("DVSeasons.VerifyHeaterMaterial");
        var systemType = mod.GetType("DVSeasons.Mod.CabHeaterSwitchSystem", true);
        harmony.Patch(systemType.GetMethod("GetControlTemplate", All), prefix: new HarmonyMethod(typeof(VerifyHeaterMaterial), nameof(Template)));
        harmony.Patch(typeof(ControlSpec).GetMethod("Awake"), prefix: new HarmonyMethod(typeof(VerifyHeaterMaterial), nameof(SkipAwake)));
        try
        {
            foreach (var type in new[] { TrainCarType.LocoDiesel, TrainCarType.LocoDH4, TrainCarType.LocoShunter, TrainCarType.LocoDM3 })
                Verify(systemType, type, snowShader);
            Debug.Log("HEATER_MATERIAL_OK: DE6/DH4/DE2/DM3 factories; winter release, 12 rebinds per cab, appearance, input values and unload cleanup");
        }
        finally { harmony.UnpatchAll(harmony.Id); }
    }
    static void Verify(Type systemType, TrainCarType type, Shader snowShader)
    {
        var system = Activator.CreateInstance(systemType, new object[] { null });
        var native = Make("SnowVehicleNativeMaterials");
        var car = new GameObject(type + " interior");
        Material original = null;
        Mesh splitSource = null;
        try
        {
            bool rotary = type == TrainCarType.LocoDM3;
            bool split = rotary || type == TrainCarType.LocoShunter;
            string controlName = rotary ? "C_Heating Rotary" : "C_CabHeater";
            string modelName = rotary ? "Heating Model" : "switch_cooler model";
            template = new GameObject("Stock toggle fixture"); template.SetActive(false);
            var control = Child(controlName, template.transform);
            if (rotary) control.AddComponent<Rotary>(); else control.AddComponent<ToggleSwitch>();
            control.AddComponent<HeaterFixtureControl>();
            var templateModel = GameObject.CreatePrimitive(PrimitiveType.Cube);
            templateModel.name = modelName; templateModel.transform.SetParent(control.transform, false);
            Transform parent = car.transform;
            if(type == TrainCarType.LocoDH4) parent = Child("CabHeater", Child("RightCluster", parent).transform).transform;
            var nativeObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nativeObject.name = rotary ? "Cab" : split ? "Deck" : type == TrainCarType.LocoDH4 ? "C_CabHeater" : "CabHeater";
            nativeObject.transform.SetParent(parent, false);
            if(split)
            {
                splitSource = UnityEngine.Object.Instantiate(nativeObject.GetComponent<MeshFilter>().sharedMesh);
                var vertices = splitSource.vertices;
                var center = rotary ? new Vector3(-.6472235f,2.222754f,-3.599f) : new Vector3(1.1658912f,-.286f,2.0866545f);
                for(int i=0;i<vertices.Length;i++) vertices[i] = center + vertices[i] * .004f;
                splitSource.vertices = vertices; splitSource.RecalculateBounds();
                nativeObject.GetComponent<MeshFilter>().sharedMesh = splitSource;
            }
            var source = nativeObject.GetComponent<MeshRenderer>();
            original = new Material(Shader.Find("Standard")) { name = type + " cab material", color = new Color(.2f,.6f,.8f), enableInstancing = true };
            original.SetFloat("_Glossiness", .37f); source.sharedMaterial = original;
            templateModel.GetComponent<MeshRenderer>().sharedMaterial = original;
            Set(native, "shader", snowShader); Set(native, "data", new ComputeBuffer(1023,144));
            var vehicle = Make("SnowVehicleRegistry+Vehicle"); Set(vehicle, "Root", car.transform);
            var parts = (IList)Get(vehicle, "Parts"); parts.Add(Part(source));
            Call(native, "Bind", vehicle);
            var transient = source.sharedMaterial;
            Check(transient != original, "Fixture did not install the native snow variant");
            var profileType = systemType.GetNestedType("SwitchProfile", BindingFlags.NonPublic);
            var profile = profileType.GetMethod("For", All).Invoke(null, new object[] { type });
            var args = new object[] { car.transform, profile, null };
            Check((bool)systemType.GetMethod("TryCreateSwitch", All).Invoke(system, args), type + " switch creation failed");
            var container = (GameObject)Get(args[2], "Container");
            var rocker = container.transform.Find("CabHeater_Control/" + controlName + "/" + modelName).GetComponent<MeshRenderer>();
            Call(native, "Release", vehicle);
            Check(transient == null, "Fixture did not destroy the released temporary material");
            Check(rocker.sharedMaterial != null, type + " rocker retained a destroyed winter material (pink switch)");
            Check(rocker.sharedMaterial.shader.name == "Standard", "Cab rocker retained a seasonal shader outside its ownership");
            Check(rocker.sharedMaterial.color == original.color && Mathf.Abs(rocker.sharedMaterial.GetFloat("_Glossiness") - .37f) < .0001f,
                "Cab rocker appearance changed");
            var privateMaterial = rocker.sharedMaterial;
            parts.Add(Part(rocker));
            for (int i = 0; i < 12; i++)
            {
                Call(native, "Bind", vehicle); Call(native, "Release", vehicle);
                Check(rocker.sharedMaterial == privateMaterial, "Refresh/season switch lost the rocker material");
            }
            Check(source.enabled == split, "Material fix changed native visual visibility");
            var liveControl = (ControlImplBase)Get(args[2], "Control");
            liveControl.SetValue(1); Check(liveControl.Value == 1, "Heater could not be switched on");
            liveControl.SetValue(0); Check(liveControl.Value == 0, "Heater could not be switched off");
            // The fixture runs in edit mode, where a non-ExecuteAlways game
            // MonoBehaviour does not receive Unity's play-mode OnDestroy.
            Call(Get(args[2], "Marker"), "OnDestroy");
            UnityEngine.Object.DestroyImmediate(container);
            Check(privateMaterial == null, "Streamed-out control leaked its private material");
            Check(original != null && original.shader.name == "Standard", "Control destroyed or changed the game's material");
            Debug.Log("HEATER_MATERIAL_CAB_OK: " + type);
        }
        finally
        {
            ((IDisposable)native).Dispose();
            // Geometry cache disposal normally uses end-of-frame Destroy in
            // play mode; release fixture meshes synchronously in edit mode.
            var geometry = (IDictionary)Get(system, "geometryCache");
            foreach (var value in geometry.Values)
                foreach (var property in new[] { "Body", "Detail" })
                    UnityEngine.Object.DestroyImmediate((Mesh)value.GetType().GetProperty(property).GetValue(value, null));
            geometry.Clear(); ((IDisposable)system).Dispose();
            UnityEngine.Object.DestroyImmediate(car); UnityEngine.Object.DestroyImmediate(template);
            if (original != null) UnityEngine.Object.DestroyImmediate(original);
            if (splitSource != null) UnityEngine.Object.DestroyImmediate(splitSource);
        }
    }
}
public sealed class HeaterFixtureControl : ControlImplBase
{
    protected override InteractionHandPoses GenericHandPoses => default(InteractionHandPoses);
    protected override void AcceptSetValue(float value) { }
    public override bool IsGrabbed() => false;
    public override void ForceEndInteraction() { }
}
