using System;
using System.Collections.Generic;
using DV.ThingTypes;
using DVSeasons.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    internal sealed class TenderCoalSnowController : IDisposable
    {
        private sealed class Binding
        {public Renderer Renderer;public int Slot;public long Key;public Material Original,Winter;public TrainCar Car;public RenderTexture Texture;public int Step=-1;}
        private readonly List<Binding> bindings=new List<Binding>();
        private readonly HashSet<long> known=new HashSet<long>();
        private readonly SeasonAssetBundleRepository repository;
        private Texture2D snow;
        private Material blend;
        private float nextScan;
        private float nextLoad;
        public TenderCoalSnowController(SeasonAssetBundleRepository repository) {this.repository=repository;}
        private static long Key(Renderer renderer,int slot) {return ((long)renderer.GetInstanceID()<<32)|(uint)slot;}
        public bool HasSnowTexture(Renderer renderer,int slot) {return renderer!=null && known.Contains(Key(renderer,slot));}
        public void Apply(float coverage,Func<Component,float> remaining)
        {
            if(coverage<=0.001f) {Restore();return;}
            if((snow==null || blend==null) && Time.realtimeSinceStartup<nextLoad) return;
            if(snow==null || blend==null) nextLoad=Time.realtimeSinceStartup+5f;
            if(snow==null)
            {
                Color32[] pixels;
                if(!repository.TryLoadPixels("Coal_01d",SeasonKind.Winter,1024,1024,out pixels)) return;
                snow=new Texture2D(1024,1024,TextureFormat.RGBA32,true) {name="DVSeasons tender winter coal",wrapMode=TextureWrapMode.Repeat,hideFlags=HideFlags.HideAndDontSave};
                snow.SetPixels32(pixels);snow.Apply(true,true);
            }
            if(blend==null)
            {var shader=repository.LoadShader("SnowVehicle");if(shader==null) return;blend=new Material(shader) {hideFlags=HideFlags.HideAndDontSave};}
            if(Time.realtimeSinceStartup>=nextScan)
            {
                nextScan=Time.realtimeSinceStartup+3f;
                foreach(var car in RailSnowGameSource.GetCars())
                {
                    if(car==null || (car.carType!=TrainCarType.Tender && car.carType!=TrainCarType.LocoS060 && car.carType!=TrainCarType.LocoSteamHeavy)) continue;
                    Register(car,car.transform);
                    if(car.interior!=null) Register(car,car.interior.transform);
                }
            }
            for(int i=bindings.Count-1;i>=0;i--)
            {
                var b=bindings[i];
                if(b.Renderer==null || b.Car==null || b.Original==null) {known.Remove(b.Key);Release(b);bindings.RemoveAt(i);continue;}
                float amount=coverage*(remaining!=null?remaining(b.Car):1f);
                int step=Mathf.RoundToInt(Mathf.Clamp01(amount)*32);
                if(b.Step!=step)
                {
                    if(b.Texture==null)
                    {
                        b.Texture=new RenderTexture(1024,1024,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.sRGB)
                        {name="DVSeasons coal blend",useMipMap=true,autoGenerateMips=true,filterMode=FilterMode.Trilinear,wrapMode=TextureWrapMode.Repeat,anisoLevel=4,hideFlags=HideFlags.HideAndDontSave};
                        b.Texture.Create();
                    }
                    blend.SetTexture("_DVPSCoalSnow",snow);blend.SetFloat("_DVPSCoalAmount",step/32f);
                    var previous=RenderTexture.active;
                    try {Graphics.Blit(b.Original.GetTexture("_MainTex"),b.Texture,blend,4);} finally {RenderTexture.active=previous;}
                    b.Step=step;
                }
                // Keep native clipping/coal-consumption properties, including
                // changes made through a previously cached original material.
                b.Winter.CopyPropertiesFromMaterial(b.Original);b.Winter.SetTexture("_MainTex",b.Texture);
            }
        }
        private void Register(TrainCar car,Transform root)
        {
            foreach(var renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var materials=renderer.sharedMaterials;bool changed=false;
                for(int slot=0;slot<materials.Length;slot++)
                {
                    var original=materials[slot];
                    if(!HasTextureProperty(original,"_MainTex")) continue;
                    var mainTexture=original.GetTexture("_MainTex");
                    if(mainTexture==null || !string.Equals(mainTexture.name,"Coal_01d",StringComparison.OrdinalIgnoreCase)) continue;
                    long key=Key(renderer,slot);if(!known.Add(key)) continue;
                    var winter=new Material(original) {name=original.name+" [DVSeasons tender snow]",hideFlags=HideFlags.HideAndDontSave};
                    bindings.Add(new Binding {Renderer=renderer,Slot=slot,Key=key,Original=original,Winter=winter,Car=car});
                    materials[slot]=winter;changed=true;
                }
                if(changed) renderer.sharedMaterials=materials;
            }
        }
        private static bool HasTextureProperty(Material material,string property)
        {
            if(material==null || material.shader==null) return false;
            var index=material.shader.FindPropertyIndex(property);
            return index>=0 && material.shader.GetPropertyType(index)==ShaderPropertyType.Texture;
        }
        private static void Destroy(UnityEngine.Object value)
        {if(value==null)return;if(Application.isPlaying) UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value);}
        private static void Release(Binding b)
        {
            if(b.Renderer!=null)
            {
                var materials=b.Renderer.sharedMaterials;
                if(b.Slot<materials.Length && materials[b.Slot]==b.Winter) {materials[b.Slot]=b.Original;b.Renderer.sharedMaterials=materials;}
            }
            Destroy(b.Winter);Destroy(b.Texture);
        }
        private void Restore() {foreach(var b in bindings)Release(b);bindings.Clear();known.Clear();nextScan=0;}
        public void Dispose() {Restore();Destroy(snow);Destroy(blend);snow=null;blend=null;nextLoad=0;}
    }
}
