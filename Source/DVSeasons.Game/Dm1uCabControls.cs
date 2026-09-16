using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DV.CabControls;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    [Serializable] internal sealed class Dm1uFanState { public string CarId; public float Level; }

    // These are existing native controls. Keep their values outside the streamed
    // interior and restore them after the native joints finish initializing.
    internal sealed class Dm1uCabControls
    {
        private readonly Action<string,float> heaterChanged;
        private readonly Dictionary<string,float> fans = new Dictionary<string,float>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,Dm1uControlMemory> heaters = new Dictionary<string,Dm1uControlMemory>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string,Dm1uControlMemory> fanControls = new Dictionary<string,Dm1uControlMemory>(StringComparer.OrdinalIgnoreCase);
        public Dm1uCabControls(Action<string,float> heaterChanged) {this.heaterChanged=heaterChanged;}
        public bool Ensure(TrainCar car,float heater)
        {
            if(car==null || car.loadedInterior==null || string.IsNullOrEmpty(car.CarGUID))return false;
            var root=car.loadedInterior.transform;var id=car.CarGUID;
            float fan; fans.TryGetValue(id,out fan);
            Bind(root,"RightCluster/Fan Switch/C_Fan Switch",id,fan,fanControls,
                value=>{if(fans.Count<1024 || fans.ContainsKey(id))fans[id]=value;});
            return Bind(root,"RightCluster/Heating Rotary/C_Heating Rotary",id,heater,heaters,
                value=>heaterChanged(id,CabHeaterSetting.FromControlValue(value)));
        }
        private static bool Bind(Transform root,string path,string id,float value,
            Dictionary<string,Dm1uControlMemory> bindings,Action<float> changed)
        {
            Dm1uControlMemory current;
            if(bindings.TryGetValue(id,out current) && current!=null && current.Interior==root)return current.Ready;
            if(current!=null){current.Detach();UnityEngine.Object.Destroy(current);}
            var node=root.Find(path);var control=node!=null ? node.GetComponent<ControlImplBase>() : null;
            if(control==null)return false;
            var memory=node.gameObject.AddComponent<Dm1uControlMemory>();
            memory.Bind(root,control,value,changed);bindings[id]=memory;
            return memory.Ready;
        }
        public void ApplyHeater(string id,float value)
        { Dm1uControlMemory memory;if(heaters.TryGetValue(id,out memory) && memory!=null)memory.SetDesired(value); }
        public List<Dm1uFanState> CaptureFans()
        {var result=new List<Dm1uFanState>();foreach(var pair in fans)result.Add(new Dm1uFanState{CarId=pair.Key,Level=pair.Value});return result;}
        public void RestoreFans(List<Dm1uFanState> records)
        {
            fans.Clear();if(records==null)return;
            foreach(var record in records)
                if(record!=null && !string.IsNullOrEmpty(record.CarId) && record.CarId.Length<=80 && fans.Count<1024 && !float.IsNaN(record.Level) && !float.IsInfinity(record.Level))
                    fans[record.CarId]=Mathf.Clamp01(record.Level);
        }
        public void Reset()
        {
            foreach(var dictionary in new[]{heaters,fanControls})
            {
                foreach(var memory in dictionary.Values)if(memory!=null){memory.Detach();UnityEngine.Object.Destroy(memory);}
                dictionary.Clear();
            }
            fans.Clear();
        }
    }

    internal sealed class Dm1uControlMemory : MonoBehaviour
    {
        private static readonly FieldInfo RotaryReady = typeof(RotaryBase).GetField("isInitialized",BindingFlags.Instance|BindingFlags.NonPublic);
        private ControlImplBase control;
        private Action<float> changed;
        private float desired;
        private bool suppress;
        public Transform Interior {get;private set;}
        public bool Ready {get;private set;}
        public void Bind(Transform interior,ControlImplBase target,float value,Action<float> callback)
        {
            Interior=interior;control=target;desired=value;changed=callback;
            control.ValueChanged+=OnValueChanged;
            if(isActiveAndEnabled)StartCoroutine(RestoreAfterInitialization());
        }
        private void OnEnable(){if(control!=null)StartCoroutine(RestoreAfterInitialization());}
        private void OnDisable(){Ready=false;StopAllCoroutines();}
        private IEnumerator RestoreAfterInitialization()
        {
            Ready=false;
            // Native rotary/stepped joints initialize in coroutines and Start.
            yield return null;
            while(control!=null && control is RotaryBase && RotaryReady!=null && !(bool)RotaryReady.GetValue(control))yield return null;
            yield return new WaitForEndOfFrame();
            if(control==null)yield break;
            Apply();Ready=true;
        }
        public void SetDesired(float value){desired=Mathf.Clamp01(value);if(Ready)Apply();}
        private void Apply()
        {
            suppress=true;
            try{control.SetValue(desired);}
            finally{suppress=false;}
        }
        private void OnValueChanged(ValueChangedEventArgs args)
        {
            if(this==null || suppress || !Ready || !isActiveAndEnabled)return;
            desired=Mathf.Clamp01(args.newValue);changed?.Invoke(desired);
        }
        public void Detach()
        {
            Ready=false;StopAllCoroutines();
            if(control!=null)control.ValueChanged-=OnValueChanged;
            changed=null;control=null;Interior=null;
        }
        private void OnDestroy(){Detach();}
    }
}
