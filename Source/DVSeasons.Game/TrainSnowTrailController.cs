using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    /// <summary>A single bounded powder emitter following the visible train tail.</summary>
    internal sealed class TrainSnowTrailController : IDisposable
    {
        private const float TargetRefreshSeconds=0.35f;
        private const float MaximumCameraDistance=450f;
        private ParticleSystem particles;
        private GameObject owner;
        private Material material;
        private Texture2D texture;
        private TrainCar target;
        private float nextTargetRefresh;

        public void Apply(float coverage,bool enabled,float lightFactor,Vector3 windVelocity)
        {
            var camera=Camera.main;
            if(!enabled || coverage<=0.01f || camera==null)
            {Stop();return;}

            if(Time.realtimeSinceStartup>=nextTargetRefresh || !Usable(target,camera))
            {
                nextTargetRefresh=Time.realtimeSinceStartup+TargetRefreshSeconds;
                target=FindTarget(camera);
            }
            if(!Usable(target,camera)) {Stop();return;}

            var velocity=target.GetVelocity();
            var horizontal=Vector3.ProjectOnPlane(velocity,Vector3.up);
            var speedKmh=horizontal.magnitude*3.6f;
            var intensity=SnowTrailProfile.Intensity(speedKmh,coverage);
            if(intensity<=0.001f || horizontal.sqrMagnitude<0.01f) {Stop();return;}

            EnsureParticles();
            if(particles==null) return;
            var direction=horizontal.normalized;
            owner.transform.position=FindTail(target,direction);
            owner.transform.rotation=Quaternion.LookRotation(-direction,Vector3.up);

            var emission=particles.emission;
            var strength=Mathf.Sqrt(intensity);
            // Distance-based emission keeps the powder continuous when frame rate
            // or train speed changes. A small time component fills slow threshold
            // transitions without turning the trail into an opaque white wall.
            emission.rateOverTimeMultiplier=Mathf.Lerp(18f,95f,strength)*coverage;
            emission.rateOverDistanceMultiplier=Mathf.Lerp(1.8f,11.5f,strength)*coverage;
            var main=particles.main;
            main.startSpeed=new ParticleSystem.MinMaxCurve(
                Mathf.Lerp(0.8f,1.5f,intensity),Mathf.Lerp(2.0f,4.2f,intensity));
            main.startSize=new ParticleSystem.MinMaxCurve(
                Mathf.Lerp(0.24f,0.36f,intensity),Mathf.Lerp(0.72f,1.18f,intensity));
            var velocityOverLifetime=particles.velocityOverLifetime;
            velocityOverLifetime.x=new ParticleSystem.MinMaxCurve(windVelocity.x*.3f-.45f,
                windVelocity.x*.3f+.45f);
            velocityOverLifetime.y=new ParticleSystem.MinMaxCurve(.35f,
                Mathf.Lerp(.8f,1.55f,intensity));
            velocityOverLifetime.z=new ParticleSystem.MinMaxCurve(windVelocity.z*.3f-.45f,
                windVelocity.z*.3f+.45f);
            ApplyLighting(lightFactor);
            if(!particles.isPlaying) particles.Play(false);
        }

        private static bool Usable(TrainCar car,Camera camera)
        {
            return car!=null && camera!=null && car.gameObject.activeInHierarchy &&
                !car.derailed && car.rb!=null &&
                (car.transform.position-camera.transform.position).sqrMagnitude<=
                    MaximumCameraDistance*MaximumCameraDistance;
        }

        private static TrainCar FindTarget(Camera camera)
        {
            var player=PlayerManager.Car;
            if(Usable(player,camera)) return player;
            TrainCar nearest=null;
            var nearestDistance=MaximumCameraDistance*MaximumCameraDistance;
            foreach(var car in RailSnowGameSource.GetCars())
            {
                if(!Usable(car,camera) || car.GetVelocity().sqrMagnitude<190f) continue;
                var distance=(car.transform.position-camera.transform.position).sqrMagnitude;
                if(distance>=nearestDistance) continue;
                nearestDistance=distance;nearest=car;
            }
            return nearest;
        }

        private static Vector3 FindTail(TrainCar seed,Vector3 direction)
        {
            var cars=seed.trainset!=null ? seed.trainset.cars : null;
            TrainCar tail=seed;
            var lowest=TailProjection(seed,direction);
            if(cars!=null)
                foreach(var car in cars)
                {
                    if(car==null || !car.gameObject.activeInHierarchy) continue;
                    var projection=TailProjection(car,direction);
                    if(projection>=lowest) continue;
                    lowest=projection;tail=car;
                }
            var bounds=tail.Bounds;
            var center=tail.transform.TransformPoint(bounds.center);
            var extent=ProjectedExtent(tail.transform,bounds.extents,direction);
            var localBottom=new Vector3(bounds.center.x,bounds.min.y+0.18f,bounds.center.z);
            var bottom=tail.transform.TransformPoint(localBottom).y;
            var result=center-direction*(extent+0.7f);
            result.y=bottom+0.22f;
            return result;
        }

        private static float TailProjection(TrainCar car,Vector3 direction)
        {
            var bounds=car.Bounds;
            var center=car.transform.TransformPoint(bounds.center);
            return Vector3.Dot(center,direction)-ProjectedExtent(car.transform,bounds.extents,direction);
        }

        private static float ProjectedExtent(Transform transform,Vector3 extents,Vector3 direction)
        {
            return Mathf.Abs(Vector3.Dot(transform.TransformVector(Vector3.right*extents.x),direction))+
                Mathf.Abs(Vector3.Dot(transform.TransformVector(Vector3.up*extents.y),direction))+
                Mathf.Abs(Vector3.Dot(transform.TransformVector(Vector3.forward*extents.z),direction));
        }

        private void EnsureParticles()
        {
            if(particles!=null) return;
            owner=new GameObject("DVSeasons Train Snow Trail") {hideFlags=HideFlags.HideAndDontSave};
            particles=owner.AddComponent<ParticleSystem>();
            var main=particles.main;
            main.loop=true;main.playOnAwake=false;main.prewarm=false;
            main.simulationSpace=ParticleSystemSimulationSpace.World;
            main.cullingMode=ParticleSystemCullingMode.Automatic;
            main.startLifetime=new ParticleSystem.MinMaxCurve(1.4f,3.1f);
            main.startSpeed=new ParticleSystem.MinMaxCurve(.8f,2.4f);
            main.startSize=new ParticleSystem.MinMaxCurve(.18f,.62f);
            main.startColor=new ParticleSystem.MinMaxGradient(
                new Color(.94f,.95f,.96f,.78f),new Color(1f,1f,1f,.94f));
            main.startRotation=new ParticleSystem.MinMaxCurve(0,Mathf.PI*2);
            main.gravityModifier=-0.015f;main.maxParticles=900;

            var emission=particles.emission;
            emission.rateOverTimeMultiplier=0;emission.rateOverDistanceMultiplier=0;
            var shape=particles.shape;
            shape.shapeType=ParticleSystemShapeType.Cone;
            shape.angle=14f;shape.radius=1.45f;shape.length=.35f;
            var velocity=particles.velocityOverLifetime;
            velocity.enabled=true;velocity.space=ParticleSystemSimulationSpace.World;
            velocity.x=new ParticleSystem.MinMaxCurve(-.45f,.45f);
            velocity.y=new ParticleSystem.MinMaxCurve(.35f,1.1f);
            velocity.z=new ParticleSystem.MinMaxCurve(-.45f,.45f);
            var noise=particles.noise;
            noise.enabled=true;noise.quality=ParticleSystemNoiseQuality.Low;
            noise.strength=.42f;noise.frequency=.28f;noise.scrollSpeed=.38f;
            var color=particles.colorOverLifetime;color.enabled=true;
            var gradient=new Gradient();
            gradient.SetKeys(new[]{new GradientColorKey(Color.white,0),
                    new GradientColorKey(new Color(.91f,.92f,.93f),1)},
                new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(.78f,.08f),
                    new GradientAlphaKey(.48f,.55f),new GradientAlphaKey(.12f,.86f),
                    new GradientAlphaKey(0,1)});
            color.color=new ParticleSystem.MinMaxGradient(gradient);
            var size=particles.sizeOverLifetime;size.enabled=true;
            size.size=new ParticleSystem.MinMaxCurve(1,new AnimationCurve(
                new Keyframe(0,.35f),new Keyframe(.22f,1f),new Keyframe(1,1.85f)));
            var rotation=particles.rotationOverLifetime;rotation.enabled=true;
            rotation.z=new ParticleSystem.MinMaxCurve(-.22f,.22f);
            var collision=particles.collision;collision.enabled=false;

            var renderer=owner.GetComponent<ParticleSystemRenderer>();
            var shader=Shader.Find("Legacy Shaders/Particles/Alpha Blended") ??
                Shader.Find("Particles/Standard Unlit") ?? Shader.Find("Sprites/Default");
            if(shader==null) {Dispose();return;}
            texture=CreatePowderTexture();
            material=new Material(shader) {name="DVSeasons Train Snow Trail Material",mainTexture=texture,
                hideFlags=HideFlags.HideAndDontSave};
            renderer.material=material;renderer.renderMode=ParticleSystemRenderMode.Billboard;
            renderer.cameraVelocityScale=0.05f;renderer.maxParticleSize=0.08f;
            var sheet=particles.textureSheetAnimation;
            sheet.enabled=true;sheet.mode=ParticleSystemAnimationMode.Grid;
            sheet.numTilesX=2;sheet.numTilesY=2;
            sheet.animation=ParticleSystemAnimationType.WholeSheet;
            sheet.frameOverTime=new ParticleSystem.MinMaxCurve(0);
            sheet.startFrame=new ParticleSystem.MinMaxCurve(0,.999f);sheet.cycleCount=1;
            Debug.Log("[DVSeasons] Dense train snow trail ready (50 km/h threshold, 900 particle cap, four powder shapes).");
        }

        private void ApplyLighting(float lightFactor)
        {
            if(material==null) return;
            var light=Mathf.Lerp(.16f,1f,Mathf.SmoothStep(0,1,Mathf.Clamp01(lightFactor)));
            var tint=new Color(light*.97f,light*.985f,light,1);
            if(material.HasProperty("_TintColor")) material.SetColor("_TintColor",tint);
            if(material.HasProperty("_Color")) material.SetColor("_Color",tint);
        }

        private static Texture2D CreatePowderTexture()
        {
            const int cellSize=64,columns=2,size=cellSize*columns;
            var result=new Texture2D(size,size,TextureFormat.RGBA32,false,true)
            {name="DVSeasons Snow Powder",filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp,
                hideFlags=HideFlags.HideAndDontSave};
            var pixels=new Color32[size*size];
            for(var y=0;y<size;y++) for(var x=0;x<size;x++)
            {
                var variant=(y/cellSize)*columns+x/cellSize;
                var dx=((x%cellSize)+.5f)/cellSize*2-1;
                var dy=((y%cellSize)+.5f)/cellSize*2-1;
                var density=0f;
                var blobCount=7+variant*2;
                for(var blob=0;blob<blobCount;blob++)
                {
                    var seed=(uint)(variant*97+blob*19+11);
                    var cx=Mathf.Lerp(-.48f,.48f,Hash01(seed));
                    var cy=Mathf.Lerp(-.34f,.34f,Hash01(seed+1));
                    var sx=Mathf.Lerp(.20f,.48f,Hash01(seed+2));
                    var sy=Mathf.Lerp(.16f,.38f,Hash01(seed+3));
                    var px=(dx-cx)/sx;var py=(dy-cy)/sy;
                    density+=Mathf.Exp(-(px*px+py*py)*1.45f);
                }
                var edge=Mathf.Clamp01((1-Mathf.Abs(dx))*(1-Mathf.Abs(dy))*7f);
                var detail=.86f+.14f*Mathf.Sin(x*1.731f+y*2.417f+variant*5.13f);
                var opacity=Mathf.Pow(Mathf.Clamp01((density-.10f)*.72f),1.16f)*edge*detail*.90f;
                pixels[y*size+x]=new Color32(250,251,252,
                    (byte)Mathf.RoundToInt(255*Mathf.Clamp01(opacity)));
            }
            result.SetPixels32(pixels);result.Apply(false,true);return result;
        }

        private static float Hash01(uint value)
        {
            value^=value>>16;value*=0x7feb352du;value^=value>>15;
            value*=0x846ca68bu;value^=value>>16;
            return (value&0x00ffffffu)/16777215f;
        }

        private void Stop()
        {
            if(particles==null) return;
            var emission=particles.emission;
            emission.rateOverTimeMultiplier=0;emission.rateOverDistanceMultiplier=0;
            if(particles.isPlaying) particles.Stop(false,ParticleSystemStopBehavior.StopEmitting);
        }

        public void Dispose()
        {
            if(owner!=null) UnityEngine.Object.Destroy(owner);
            if(material!=null) UnityEngine.Object.Destroy(material);
            if(texture!=null) UnityEngine.Object.Destroy(texture);
            owner=null;particles=null;material=null;texture=null;target=null;nextTargetRefresh=0;
        }
    }
}
