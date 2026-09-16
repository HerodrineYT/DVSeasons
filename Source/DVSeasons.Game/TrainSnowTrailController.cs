using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    // A shared renderer batches independent dust sources from every nearby car.
    internal sealed class TrainSnowTrailController : IDisposable
    {
        internal const float MainOpacity = .35f;
        internal const float MainLifetimeMin = 3f, MainLifetimeMax = 5.2f;
        internal const float WheelLifetimeMin = .7f, WheelLifetimeMax = 1.2f;
        private sealed class Emitter
        {
            public TrainCar Car;
            public float Credit, Rate, Snow, NextSurfaceCheck;
            public Vector3 Previous, PreviousSource, LocalContact, LocalRight, LocalUp, SurfacePosition;
            public bool Ready, HasContact;
        }
        private readonly List<Emitter> emitters = new List<Emitter>();
        private readonly Dictionary<int,Emitter> cars = new Dictionary<int,Emitter>();
        private ParticleSystem particles;
        private GameObject owner;
        private Material material;
        private Texture2D snowTexture, pendingTexture;
        private sealed class WheelEmitter
        {
            public Vector3 Position; public bool Ready;
            public readonly float[] Credit=new float[2], NextShelter=new float[2], ShelterRequestedAt=new float[2];
            public readonly bool[] ShelterReady=new bool[2], Sheltered=new bool[2], ShelterQueued=new bool[2];
            public readonly Vector3[] ShelterPosition=new Vector3[2], ShelterRequestedPosition=new Vector3[2];
        }
        private struct WheelShelterProbe { public WheelEmitter Wheel; public int Rail; }
        private readonly Queue<WheelShelterProbe> wheelShelterQueries=new Queue<WheelShelterProbe>();
        private readonly Dictionary<int,WheelEmitter> wheels = new Dictionary<int,WheelEmitter>();
        private int wheelFrame=-1, wheelBudget, wheelShelterBudget;
        private float wheelTokens=24;
        private int surfaceBudget, surfaceCursor;
        private float temperature=-10, wetness;
        private float nextScan, nextTemplate, coverage, light;
        private Vector3 wind;
        private bool pending, subscribed;
        private readonly SeasonAssetBundleRepository repository;
        public TrainSnowTrailController(SeasonAssetBundleRepository repository) { this.repository=repository; }

        public void WheelAt(int id,Vector3 contact,Vector3 right,Vector3 up,Vector3 velocity,Vector2 remaining)
        {
            var camera=PlayerManager.ActiveCamera != null ? PlayerManager.ActiveCamera : Camera.main;
            if(particles==null || coverage<=.01f || camera==null ||
                (contact-camera.transform.position).sqrMagnitude>90f*90f) return;
            var offset=DV.OriginShift.OriginShift.currentMove;
            if(wheelFrame!=Time.frameCount)
            {
                wheelFrame=Time.frameCount;
                wheelShelterBudget=16;
                wheelTokens=Mathf.Min(24,wheelTokens+160f*Mathf.Min(Time.deltaTime,.1f));
                wheelBudget=Mathf.FloorToInt(wheelTokens);
                // Pending axles get first use of the new frame's query budget.
                ProcessShelterQueries(offset);
            }
            owner.transform.position=offset;
            var stable=contact-offset;
            WheelEmitter wheel;
            if(!wheels.TryGetValue(id,out wheel))
            {
                if(wheels.Count>=2048){wheels.Clear();wheelShelterQueries.Clear();}
                wheels.Add(id,wheel=new WheelEmitter());
            }
            float distance=wheel.Ready ? Vector3.Distance(stable,wheel.Position) : 0;
            wheel.Position=stable;wheel.Ready=true;
            float speed=velocity.magnitude;
            if(speed<.3f || distance<=0 || distance>Mathf.Max(1.5f,speed*Mathf.Min(Time.deltaTime,.1f)*3f)){wheel.Credit[0]=wheel.Credit[1]=0;return;}
            var direction=velocity.normalized;
            for(int rail=0;rail<2;rail++)
            {
                int side=rail*2-1;
                var point=stable+right*(side*.75f);
                float snow=remaining[rail]*SnowCoveragePattern.At(point+direction*.06f,coverage);
                if(snow<.12f){wheel.Credit[rail]=0;continue;}
                float powder=SnowTrailProfile.PowderFactor(temperature,wetness);
                float distanceFade=1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(45,90,Vector3.Distance(contact,camera.transform.position)));
                wheel.Credit[rail]=Mathf.Min(2,wheel.Credit[rail]+distance*2.5f*snow*Mathf.Lerp(.3f,1,powder)*distanceFade);
                if(wheel.Credit[rail]<1)continue;
                // Check the actual rail side, not the centre of the axle. Only
                // scenery blocks snowfall; the train's own hull is excluded.
                float shelterDistance=Mathf.Max(2f,speed*.2f);
                if(!wheel.ShelterReady[rail] || Time.time>=wheel.NextShelter[rail] ||
                    (point-wheel.ShelterPosition[rail]).sqrMagnitude>shelterDistance*shelterDistance)
                {
                    wheel.ShelterRequestedPosition[rail]=point;wheel.ShelterRequestedAt[rail]=Time.time;
                    if(!wheel.ShelterQueued[rail])
                    {
                        wheel.ShelterQueued[rail]=true;
                        wheelShelterQueries.Enqueue(new WheelShelterProbe {Wheel=wheel,Rail=rail});
                    }
                    ProcessShelterQueries(offset);
                    // A stale/unqueried position must not emit while waiting.
                    if(wheel.ShelterQueued[rail] || !wheel.ShelterReady[rail] ||
                        Time.time>=wheel.NextShelter[rail] ||
                        (point-wheel.ShelterPosition[rail]).sqrMagnitude>shelterDistance*shelterDistance)continue;
                }
                if(wheel.Sheltered[rail]){wheel.Credit[rail]=0;continue;}
                int count=Mathf.Min(wheelBudget,Mathf.FloorToInt(wheel.Credit[rail]));
                if(count==0)continue;
                wheel.Credit[rail]-=count;wheelBudget-=count;wheelTokens=Mathf.Max(0,wheelTokens-count);
                if(!particles.isPlaying)particles.Play(false);
                for(int i=0;i<count;i++)
                {
                var emit=new ParticleSystem.EmitParams {
                    position=point+up*.025f,
                    velocity=right*(side*UnityEngine.Random.Range(.25f,.7f))+up*UnityEngine.Random.Range(.25f,.65f)
                        -direction*Mathf.Min(.9f,speed*.08f)+velocity*.08f,
                    startSize=SampleWheelSize(speed*3.6f),
                    rotation=UnityEngine.Random.Range(0,360f),
                    startLifetime=UnityEngine.Random.Range(WheelLifetimeMin,WheelLifetimeMax)*Mathf.Lerp(.65f,1.1f,powder),
                    startColor=DustColour(.65f),applyShapeToPosition=false };
                particles.Emit(emit,1);
                }
            }
        }

        private void ProcessShelterQueries(Vector3 offset)
        {
            while(wheelShelterBudget>0 && wheelShelterQueries.Count>0)
            {
                var probe=wheelShelterQueries.Dequeue();
                var wheel=probe.Wheel;int rail=probe.Rail;
                wheelShelterBudget--;
                wheel.ShelterQueued[rail]=false;
                if(Time.time-wheel.ShelterRequestedAt[rail]>.5f)continue;
                var point=wheel.ShelterRequestedPosition[rail];
                wheel.Sheltered[rail]=Physics.Raycast(point+offset+Vector3.up*.15f,Vector3.up,100f,
                    (int)(DV.Layers.DVLayerMask.Default|DV.Layers.DVLayerMask.Terrain),QueryTriggerInteraction.Ignore);
                wheel.ShelterPosition[rail]=point;wheel.NextShelter[rail]=Time.time+.2f;wheel.ShelterReady[rail]=true;
            }
        }

        public void ReportTrackContact(TrainCar car,Vector3 contact,Vector3 right,Vector3 up)
        {
            Emitter emitter;
            if(car==null || !cars.TryGetValue(car.GetInstanceID(),out emitter))return;
            emitter.LocalContact=car.transform.InverseTransformPoint(contact);
            emitter.LocalRight=car.transform.InverseTransformDirection(right);
            emitter.LocalUp=car.transform.InverseTransformDirection(up);
            emitter.HasContact=true;
        }

        internal static float EmissionRate(float speed,float coverage)
            => SnowTrailProfile.EmissionRate(speed*3.6f,coverage,-10,0);

        internal float SampleTrailSize(float speedKmh)
        {
            var main=particles.main;
            float cycle=main.duration>0 ? Mathf.Repeat(particles.time/main.duration,1) : 0;
            return main.startSize.Evaluate(cycle,UnityEngine.Random.value)*SnowTrailProfile.MainSizeMultiplier(speedKmh);
        }

        internal static float SampleWheelSize(float speedKmh)
            => UnityEngine.Random.Range(.16f,.3f)*SnowTrailProfile.WheelSizeMultiplier(speedKmh);

        private void RefreshSurface(Emitter emitter,Vector3 contact,Vector3 right,Vector3 up,Vector3 offset)
        {
            var stable=contact-offset;
            if(Time.time<emitter.NextSurfaceCheck && (stable-emitter.SurfacePosition).sqrMagnitude<9)return;
            if(surfaceBudget<4)return;
            surfaceBudget-=4;
            emitter.SurfacePosition=stable;emitter.NextSurfaceCheck=Time.time+.18f;
            emitter.Snow=SampleSurfaceSnow(contact,right,up,coverage,offset);
        }

        internal static float SampleSurfaceSnow(Vector3 contact,Vector3 right,Vector3 up,float amount,Vector3 offset)
        {
            // Airflow lifts loose snow beside the rail head, even when preceding
            // axles have polished the rail itself. Missing ballast/bridge support
            // and covered depots must not produce an airborne plume.
            float snow=0;
            for(int side=-1;side<=1;side+=2)
            {
                var point=contact+right*(side*1.15f);
                RaycastHit hit;
                if(!Physics.Raycast(point+up*.3f,-up,out hit,1f,SeasonSurfaceLayers.Mask,QueryTriggerInteraction.Ignore) || hit.normal.y<.55f)continue;
                if(Physics.Raycast(hit.point+Vector3.up*.12f,Vector3.up,100f,
                    (int)(DV.Layers.DVLayerMask.Default|DV.Layers.DVLayerMask.Terrain),QueryTriggerInteraction.Ignore))continue;
                snow=Mathf.Max(snow,SnowCoveragePattern.At(hit.point-offset,amount));
            }
            return snow;
        }

        private Color DustColour(float alpha)
        {
            // Lighting and snow albedo are evaluated by the snow material.
            // An extra time-of-day multiplier would darken the particles twice.
            return new Color(1,1,1,alpha);
        }

        public void Apply(float amount, bool enabled, float lightFactor, Vector3 windVelocity, float airTemperature = -10, float liquidRain = 0)
        {
            coverage = enabled ? Mathf.Clamp01(amount) : 0;
            light = Mathf.Clamp01(lightFactor); wind = Vector3.ClampMagnitude(windVelocity,25);
            temperature=airTemperature;
            // Wet snow takes longer to dry than to become damp.
            wetness=Mathf.MoveTowards(wetness,Mathf.Clamp01(liquidRain),Mathf.Min(Time.deltaTime,.1f)*(liquidRain>wetness ? .4f : .015f));
            UpdateAirflow();
            if (coverage <= .01f) { Stop(); return; }
            if (!subscribed) { Camera.onPreCull += BeforeCamera; subscribed=true; }
            pending = true;
        }

        private void BeforeCamera(Camera camera)
        {
            var activeCamera = PlayerManager.ActiveCamera != null ? PlayerManager.ActiveCamera : Camera.main;
            if (camera != activeCamera || !pending) return;
            pending=false;
            camera.depthTextureMode |= DepthTextureMode.Depth;
            surfaceBudget=32;
            var offset = DV.OriginShift.OriginShift.currentMove;
            if (owner != null) owner.transform.position=offset;
            if (Time.realtimeSinceStartup >= nextScan)
            {
                nextScan=Time.realtimeSinceStartup+.35f;
                for(int i=emitters.Count-1;i>=0;i--)
                    if(!Usable(emitters[i].Car,camera))
                    { if(emitters[i].Car!=null)cars.Remove(emitters[i].Car.GetInstanceID());emitters.RemoveAt(i); }
                cars.Clear();foreach(var emitter in emitters)cars[emitter.Car.GetInstanceID()]=emitter;
                foreach(var car in RailSnowGameSource.GetCars())
                {
                    if(!Usable(car,camera)) continue;
                    if(!cars.ContainsKey(car.GetInstanceID())) {var added=new Emitter {Car=car};emitters.Add(added);cars[car.GetInstanceID()]=added;}
                }
            }
            if(particles==null && Time.realtimeSinceStartup>=nextTemplate)
            {
                nextTemplate=Time.realtimeSinceStartup+1f;
                var native=DV.VFX.DerailedParticleSystem.Instance;
                if(native!=null) EnsureParticles(native.GetComponentInChildren<ParticleSystem>());
            }
            if(particles==null) return;
            owner.transform.position=offset;
            if(!particles.isPlaying)particles.Play(false);
            // Rotate service order: early cars must not repeatedly consume all
            // ground queries while the tail of a long train starves at low FPS.
            for(int checkedCars=0;checkedCars<emitters.Count && surfaceBudget>=4;checkedCars++)
            {
                surfaceCursor%=emitters.Count;
                var emitter=emitters[surfaceCursor++];
                if(!emitter.HasContact || !Usable(emitter.Car,camera) || emitter.Car.GetVelocity().sqrMagnitude<48.2f)continue;
                var pose=emitter.Car.transform;
                RefreshSurface(emitter,pose.TransformPoint(emitter.LocalContact),pose.TransformDirection(emitter.LocalRight),
                    pose.TransformDirection(emitter.LocalUp),offset);
            }
            var colour=DustColour(MainOpacity);
            foreach(var emitter in emitters)
            {
                var car=emitter.Car;
                if(!Usable(car,camera)) continue;
                var velocity=Vector3.ProjectOnPlane(car.GetVelocity(),Vector3.up);
                float speed=velocity.magnitude;
                if(!emitter.HasContact || speed*3.6f<=SnowTrailProfile.StartSpeedKmh)
                {emitter.Ready=false;emitter.Rate=emitter.Credit=0;continue;}
                var contact=car.transform.TransformPoint(emitter.LocalContact);
                var right=car.transform.TransformDirection(emitter.LocalRight);
                var up=car.transform.TransformDirection(emitter.LocalUp);
                if(Time.time>emitter.NextSurfaceCheck+.5f || (contact-offset-emitter.SurfacePosition).sqrMagnitude>324)emitter.Snow=0;
                float distanceFade=1-Mathf.SmoothStep(0,1,Mathf.InverseLerp(100,320,Vector3.Distance(contact,camera.transform.position)));
                float target=SnowTrailProfile.EmissionRate(speed*3.6f,Mathf.Min(coverage,emitter.Snow),temperature,wetness)*distanceFade;
                float dt=Mathf.Min(Time.deltaTime,.05f);
                emitter.Rate=Mathf.Lerp(emitter.Rate,target,1-Mathf.Exp(-dt*3f));
                float rate=target>0 ? emitter.Rate : 0;
                var stable=car.transform.position-offset;
                bool teleported=!emitter.Ready || (stable-emitter.Previous).sqrMagnitude>Mathf.Pow(Mathf.Max(2,speed*dt*3),2);
                var previousSource=emitter.PreviousSource;
                var source=EmissionPoint(car.transform,emitter.LocalContact,up)-offset;
                emitter.PreviousSource=source;
                emitter.Previous=stable; emitter.Ready=true;
                if(rate<=0 || teleported) {emitter.Rate=emitter.Credit=0;continue;}
                var direction=velocity.normalized;
                var position=source;
                var side=Vector3.Cross(Vector3.up,direction);
                emitter.Credit+=rate*dt;
                int count=Mathf.Min(3,Mathf.FloorToInt(emitter.Credit)); emitter.Credit-=count;
                for(int i=0;i<count;i++)
                {
                    var emit=new ParticleSystem.EmitParams {
                        position=Vector3.Lerp(previousSource,position,(i+.5f)/count),
                        velocity=velocity*.10f-direction*UnityEngine.Random.Range(.2f,.7f)+
                            side*UnityEngine.Random.Range(-.35f,.35f)+up*UnityEngine.Random.Range(.15f,.5f),
                        rotation=UnityEngine.Random.Range(0,360f),
                        startSize=SampleTrailSize(speed*3.6f),
                        startLifetime=UnityEngine.Random.Range(MainLifetimeMin,MainLifetimeMax)*Mathf.Lerp(.5f,1,SnowTrailProfile.PowderFactor(temperature,wetness)),
                        startColor=colour, applyShapeToPosition=false };
                    particles.Emit(emit,1);
                }
            }
        }

        internal static bool Usable(TrainCar car, Camera camera)
        {
            return car!=null && camera!=null && car.gameObject.activeInHierarchy && !car.derailed &&
                car.rb!=null && (car.transform.position-camera.transform.position).sqrMagnitude<340f*340f;
        }

        internal static Vector3 EmissionPoint(Transform pose,Vector3 localContact,Vector3 up)
            => pose.TransformPoint(localContact)+up*.1f;

        private void UpdateAirflow()
        {
            if(particles==null)return;
            // Change the whole airborne cloud when wind changes, not only newly
            // emitted particles. Custom space has translation only, so axes are world-aligned.
            var flow=particles.velocityOverLifetime;flow.enabled=true;
            flow.space=ParticleSystemSimulationSpace.World;
            flow.x=wind.x*.55f;flow.y=0;flow.z=wind.z*.55f;
        }

        private void EnsureParticles(ParticleSystem source)
        {
            if(particles!=null || source==null) return;
            var sourceRenderer=source.GetComponent<ParticleSystemRenderer>();
            if(sourceRenderer==null || sourceRenderer.sharedMaterial==null) return;
            var shader=repository.LoadShader("SnowDust");if(shader==null)return;
            pendingTexture=sourceRenderer.sharedMaterial.mainTexture as Texture2D;
            snowTexture=SnowDustTexture.Create(sourceRenderer.sharedMaterial.mainTexture);
            if(snowTexture==null)return;
            pendingTexture=null;
            owner=new GameObject("DVSeasons per-car snow dust") {hideFlags=HideFlags.HideAndDontSave};
            particles=owner.AddComponent<ParticleSystem>();
            particles.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
            var main=particles.main;var native=source.main;
            main.loop=true; main.playOnAwake=false;
            main.simulationSpace=ParticleSystemSimulationSpace.Custom; main.customSimulationSpace=owner.transform;
            main.cullingMode=ParticleSystemCullingMode.AlwaysSimulate;
            main.startLifetime=new ParticleSystem.MinMaxCurve(MainLifetimeMin,MainLifetimeMax);
            // Quarter of the previous snow trail size (it was 25% of native dust).
            main.startSize=native.startSize; main.startSizeMultiplier=native.startSizeMultiplier*.0625f;
            main.startRotation=native.startRotation; main.startSpeed=0; main.maxParticles=1800;
            // Fine powder remains suspended longer; retain a slow downward drift.
            main.gravityModifier=.008f;
            var emission=particles.emission;emission.enabled=false;
            var shape=particles.shape;shape.enabled=false;
            var size=particles.sizeOverLifetime;size.enabled=true;
            size.size=new ParticleSystem.MinMaxCurve(1,new AnimationCurve(new Keyframe(0,.55f),new Keyframe(.4f,1),new Keyframe(1,1.7f)));
            var drag=particles.limitVelocityOverLifetime;drag.enabled=true;drag.limit=100;drag.dampen=0;
            drag.drag=.7f;drag.multiplyDragByParticleSize=false;drag.multiplyDragByParticleVelocity=false;
            var colour=particles.colorOverLifetime;colour.enabled=true;
            var fade=new Gradient();fade.SetKeys(new[]{new GradientColorKey(Color.white,0),new GradientColorKey(Color.white,1)},
                new[]{new GradientAlphaKey(0,0),new GradientAlphaKey(1,.12f),new GradientAlphaKey(.45f,.6f),new GradientAlphaKey(0,1)});
            colour.color=fade;
            var noise=particles.noise;noise.enabled=true;noise.quality=ParticleSystemNoiseQuality.Low;
            noise.strength=.10f;noise.frequency=.20f;noise.scrollSpeed=.12f;
            var sheet=particles.textureSheetAnimation;var sourceSheet=source.textureSheetAnimation;
            sheet.enabled=sourceSheet.enabled;
            if(sheet.enabled) {sheet.numTilesX=sourceSheet.numTilesX;sheet.numTilesY=sourceSheet.numTilesY;
                sheet.frameOverTime=sourceSheet.frameOverTime;sheet.startFrame=sourceSheet.startFrame;}
            material=new Material(shader) {name="DVSeasons snow powder",hideFlags=HideFlags.HideAndDontSave};
            material.mainTexture=snowTexture;
            if(material.HasProperty("_TintColor")) material.SetColor("_TintColor",Color.white);
            if(material.HasProperty("_Color")) material.SetColor("_Color",Color.white);
            var renderer=owner.GetComponent<ParticleSystemRenderer>();renderer.sharedMaterial=material;
            renderer.renderMode=ParticleSystemRenderMode.Billboard;renderer.maxParticleSize=.5f;
            renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;
            UpdateAirflow();
            Debug.Log("[DVSeasons] Per-car snow dust uses native derailment dust at 6.25% size (25 km/h threshold).");
        }

        private void Stop()
        {
            pending=false;emitters.Clear();cars.Clear();wheels.Clear();wheelShelterQueries.Clear();wheelTokens=24;wheelFrame=-1;
            if(particles!=null) particles.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);
        }
        public void Dispose()
        {
            if(subscribed) Camera.onPreCull-=BeforeCamera;subscribed=false;
            Stop();
            if(owner!=null) UnityEngine.Object.Destroy(owner);
            if(material!=null) UnityEngine.Object.Destroy(material);
            if(snowTexture!=null) UnityEngine.Object.Destroy(snowTexture);
            StreamingTextureReadiness.Cancel(pendingTexture);pendingTexture=null;snowTexture=null;
            owner=null;particles=null;material=null;nextScan=nextTemplate=0;
        }
    }
}
