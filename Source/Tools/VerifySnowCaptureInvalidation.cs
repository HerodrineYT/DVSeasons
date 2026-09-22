using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;

// Runs in Unity with actual native TrainCar/CargoModelController getters. The
// inactive source shell avoids unrelated train physics, audio and save loading.
public static class VerifySnowCaptureInvalidation
{
    private const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    private static Type registryType;
    private static object registry, vehicle;
    private static readonly Vector4 Area=new Vector4(0,0,64,0);
    private static object Get(object target,string name) {return target.GetType().GetField(name,All).GetValue(target);}
    private static void Set(object target,string name,object value) {target.GetType().GetField(name,All).SetValue(target,value);}
    private static object Call(string name,params object[] args) {return registryType.GetMethod(name,All).Invoke(registry,args);}
    private static void Require(bool value,string message) {if(!value) throw new Exception(message);}
    private static Renderer Cube(string name,Transform parent)
    {
        var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.name=name;go.transform.SetParent(parent,false);
        return go.GetComponent<Renderer>();
    }
    private static void Refresh()
    {
        Call("RefreshParts",vehicle);Set(vehicle,"PartsPending",false);Set(vehicle,"InteriorPending",false);
        Set(vehicle,"NextCargoCheck",float.PositiveInfinity);
    }
    private static void NormalPollMustWait(string label)
    {
        Call("CheckCargo",vehicle,false);
        Require(!(bool)Get(vehicle,"PartsPending"),label+" was not tested between scheduled polls");
    }
    private static void CheckCapture(Renderer added,string label)
    {
        Call("HideForStaticCapture",Area);
        try
        {
            Require(added.forceRenderingOff,label+" leaked into stationary exposure before the next fleet poll");
            Require((bool)Get(vehicle,"PartsPending"),label+" did not invalidate cached geometry");
        }
        finally {Call("RestoreAfterStaticCapture");}
        Require(!added.forceRenderingOff,label+" stayed hidden after capture");
        Refresh();
        Require(((IList)Get(vehicle,"CaptureRenderers")).Contains(added),label+" missing after cache refresh");
    }

    public static void Run(string modPath)
    {
        registryType=Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll")).GetType("DVSeasons.Mod.SnowVehicleRegistry",true);
        registry=Activator.CreateInstance(registryType,true);
        var root=new GameObject("Native train source for snow capture");root.SetActive(false);
        var interior=new GameObject("Detached native interior");interior.SetActive(false);
        try
        {
            var car=root.AddComponent<TrainCar>();car.interior=interior.transform;
            var cargoController=root.AddComponent<CargoModelController>();
            typeof(TrainCar).GetProperty("CargoModelController",All).SetValue(car,cargoController,null);
            var original=Cube("Original exterior",root.transform);
            var interiorBody=Cube("Original detached interior",interior.transform);
            Call("Register",root.transform,interior.transform,null);
            vehicle=((IList)Get(registry,"vehicles"))[0];
            Set(vehicle,"Source",car);Set(vehicle,"CargoController",cargoController);
            Set(vehicle,"CargoGetter",typeof(CargoModelController).GetMethod("GetCurrentCargoModel"));
            Set(registry,"trainType",typeof(TrainCar));
            Set(registry,"externalGetter",typeof(TrainCar).GetProperty("loadedExternalInteractables"));
            Set(registry,"dummyExternalGetter",typeof(TrainCar).GetProperty("loadedDummyExternalInteractables"));
            Set(registry,"cargoControllerGetter",typeof(TrainCar).GetProperty("CargoModelController"));
            Set(registry,"explodedField",typeof(TrainCar).GetField("isExploded"));
            Refresh();

            // Native cargo loading instantiates below the same interior root and
            // does not raise InteriorLoaded. No explicit registry SetCargo call.
            var cargo=Cube("Cargo arriving between polls",interior.transform);
            Set(cargoController,"currentCargoModel",cargo.gameObject);
            NormalPollMustWait("Late cargo");CheckCapture(cargo,"Late cargo");
            Require((Transform)Get(vehicle,"Cargo")==cargo.transform,"Actual native cargo getter was not sampled");

            // Native LoadDummyExternalInteractables has no loaded event at all.
            var dummy=Cube("Late dummy external",interior.transform);
            typeof(TrainCar).GetProperty("loadedDummyExternalInteractables",All).SetValue(car,dummy.gameObject,null);
            NormalPollMustWait("Late dummy external");CheckCapture(dummy,"Late dummy external");
            var external=Cube("Late full external",interior.transform);
            typeof(TrainCar).GetProperty("loadedExternalInteractables",All).SetValue(car,external.gameObject,null);
            NormalPollMustWait("Late full external");CheckCapture(external,"Late full external");

            // ExplosionModelHandler adds replacement descendants; a freight car
            // need not have an exploded interior prefab that produces an event.
            var wreck=Cube("New exploded descendant under unchanged cargo",cargo.transform);
            car.isExploded=true;
            NormalPollMustWait("Explosion");CheckCapture(wreck,"Explosion");
            Require((bool)Get(vehicle,"PartsExploded"),"Refreshed cache did not retain exploded state");
            car.isExploded=false;NormalPollMustWait("Repair");
            Call("HideForStaticCapture",Area);
            try {Require((bool)Get(vehicle,"PartsPending"),"Repair did not invalidate exploded geometry");}
            finally {Call("RestoreAfterStaticCapture");}
            Refresh();Require(!(bool)Get(vehicle,"PartsExploded"),"Repair did not refresh cached state");

            // Steady captures retain the fast path, including detached interiors,
            // and preserve visibility that another system owns.
            original.forceRenderingOff=true;
            int revision=(int)registryType.GetProperty("Revision",All).GetValue(registry,null);
            int cached=((IList)Get(vehicle,"CaptureRenderers")).Count;
            for(int i=0;i<64;i++)
            {
                Call("HideForStaticCapture",Area);
                try
                {
                    Require(interiorBody.forceRenderingOff && cargo.forceRenderingOff && dummy.forceRenderingOff && wreck.forceRenderingOff,
                        "Steady cached capture omitted a registered renderer");
                    Require(!(bool)Get(vehicle,"PartsPending"),"Unchanged native state repeatedly invalidated the cache");
                }
                finally {Call("RestoreAfterStaticCapture");}
                Require(original.forceRenderingOff,"Capture changed externally owned visibility");
                Require(!interiorBody.forceRenderingOff && !cargo.forceRenderingOff,"Capture left detached geometry hidden");
            }
            Require((int)registryType.GetProperty("Revision",All).GetValue(registry,null)==revision &&
                ((IList)Get(vehicle,"CaptureRenderers")).Count==cached,"Steady captures rebuilt geometry");
            // After Destroy, Unity's overloaded equality reports old Transform
            // == null. The old height geometry must nevertheless be invalidated.
            foreach(var field in new[]{"loadedExternalInteractables","loadedDummyExternalInteractables"})
            {
                Set(vehicle,"NextCargoCheck",float.PositiveInfinity);
                var property=typeof(TrainCar).GetProperty(field,All);
                UnityEngine.Object.DestroyImmediate((GameObject)property.GetValue(car,null));
                property.SetValue(car,null,null);
                NormalPollMustWait("Destroyed "+field);
                Call("HideForStaticCapture",Area);
                try {Require((bool)Get(vehicle,"PartsPending"),"Destroyed "+field+" retained cached geometry");}
                finally {Call("RestoreAfterStaticCapture");}
                Refresh();
                Require(ReferenceEquals(Get(vehicle,field=="loadedExternalInteractables"?"External":"DummyExternal"),null),
                    "Destroyed "+field+" retained its dead Transform reference");
            }
            Set(vehicle,"NextCargoCheck",float.PositiveInfinity);
            UnityEngine.Object.DestroyImmediate(cargo.gameObject);Set(cargoController,"currentCargoModel",null);
            NormalPollMustWait("Destroyed cargo");Call("HideForStaticCapture",Area);
            try {Require((bool)Get(vehicle,"PartsPending"),"Destroyed cargo retained its old height geometry");}
            finally {Call("RestoreAfterStaticCapture");}
            Refresh();Require(ReferenceEquals(Get(vehicle,"Cargo"),null),"Destroyed cargo retained its dead Transform reference");
            Require(((IList)Get(vehicle,"CaptureRenderers")).Count==2,"Unloaded descendants remained in the cache");
            Debug.Log("SNOW_CAPTURE_INVALIDATION_OK late cargo/dummy/external, explosion/repair, destroyed unloads, detached interior, 64 stable captures");
        }
        finally
        {
            ((IDisposable)registry).Dispose();
            UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(interior);
        }
    }
}
