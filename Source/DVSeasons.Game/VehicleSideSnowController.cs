using System;
using System.Collections.Generic;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Four persistent car-local faces: +X, -X, +Z, -Z. No geometry scans or GPU
    // resources are needed; the existing vehicle snow pass consumes these values.
    internal sealed class VehicleSideSnowController
    {
        private sealed class Entry
        {
            public TrainCar Car;
            public string Id;
            public Vector4 Amount;
            public Vector3 ProbePosition;
            public float NextProbe;
            public int Seen;
            public bool ShelterReady, Sheltered, Queued;
        }

        private readonly Dictionary<int,Entry> entries = new Dictionary<int,Entry>();
        private readonly Dictionary<string,Vector4> saved = new Dictionary<string,Vector4>(StringComparer.OrdinalIgnoreCase);
        private readonly List<int> expired = new List<int>();
        private readonly Queue<Entry> shelterQueue = new Queue<Entry>();
        private readonly RaycastHit[] shelterHits = new RaycastHit[16];
        private bool authority = true;
        private float elapsed, nextSnapshot;
        private int revision, probeFrame = -1, probeBudget;
        private VehicleSideSnowNetworkState[] networkSnapshot = VehicleSideSnowNetworkState.Empty;

        public void SetNetworkAuthority(bool value)
        {
            if(authority==value) return;
            // A joining client must wait for the host rather than importing the
            // previous local world's layer. Host migration can retain its snapshot.
            if(!value) Reset();
            authority=value;
        }

        public Vector4 Amount(Component source)
        {
            var car=source as TrainCar;
            if(car==null) return Vector4.zero;
            Entry entry;
            if(authority && entries.TryGetValue(car.GetInstanceID(),out entry)) return entry.Amount;
            Vector4 amount;
            return !string.IsNullOrEmpty(car.CarGUID) && saved.TryGetValue(car.CarGUID,out amount) ? amount : Vector4.zero;
        }

        public void Update(float snowfall,float coverage,float temperatureCelsius,float seconds,
            Func<Component,float> heat,Vector3 windVelocity)
        {
            if(!authority) return;
            if(coverage<=.001f) {if(entries.Count>0 || saved.Count>0) Reset();return;}
            if(float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds<=0f) return;
            // A loading stall is not a period of driving through the new scene.
            elapsed+=Mathf.Min(seconds,1f);
            if(elapsed>=.25f)
            {
                float step=elapsed;elapsed=0f;unchecked {revision++;}
                var offset=DV.OriginShift.OriginShift.currentMove;
                int tracked=0,depositing=0,sheltered=0;float maximum=0f;
                foreach(var car in RailSnowGameSource.GetCars())
                {
                    if(car==null || !car.gameObject.activeInHierarchy) continue;
                    tracked++;
                    int key=car.GetInstanceID();Entry entry;
                    if(!entries.TryGetValue(key,out entry))
                    {
                        Vector4 restored=Vector4.zero;
                        if(!string.IsNullOrEmpty(car.CarGUID)) saved.TryGetValue(car.CarGUID,out restored);
                        entries.Add(key,entry=new Entry {Car=car,Id=car.CarGUID,Amount=restored});
                    }
                    entry.Seen=revision;
                    var velocity=car.GetVelocity();
                    float speed=velocity.magnitude*3.6f;
                    var localAir=car.transform.InverseTransformDirection(windVelocity-velocity);
                    bool canGrow=snowfall>.001f && temperatureCelsius<0f &&
                        (localAir.x!=0f || localAir.z!=0f);
                    bool exposed=false;
                    if(canGrow)
                    {
                        var roof=Roof(car)-offset;
                        float distance=Mathf.Max(3f,speed/3.6f*.65f);
                        bool fresh=entry.ShelterReady && Time.time<entry.NextProbe &&
                            (roof-entry.ProbePosition).sqrMagnitude<=distance*distance;
                        if(!fresh && !entry.Queued) {entry.Queued=true;shelterQueue.Enqueue(entry);}
                        exposed=fresh && !entry.Sheltered;
                        if(fresh && entry.Sheltered) sheltered++;
                    }
                    if(exposed && canGrow) depositing++;
                    float heating=heat!=null?heat(car):0f;
                    var amount=entry.Amount;
                    amount.x=Advance(amount.x,speed,snowfall,temperatureCelsius,heating,exposed,step,localAir,1f,0f);
                    amount.y=Advance(amount.y,speed,snowfall,temperatureCelsius,heating,exposed,step,localAir,-1f,0f);
                    amount.z=Advance(amount.z,speed,snowfall,temperatureCelsius,heating,exposed,step,localAir,0f,1f);
                    amount.w=Advance(amount.w,speed,snowfall,temperatureCelsius,heating,exposed,step,localAir,0f,-1f);
                    entry.Amount=amount;
                    maximum=Mathf.Max(maximum,Maximum(amount));
                    if(ValidId(entry.Id))
                    {
                        if(amount.sqrMagnitude<=0f) saved.Remove(entry.Id);
                        else if(saved.Count<16384 || saved.ContainsKey(entry.Id)) saved[entry.Id]=amount;
                    }
                }
                expired.Clear();
                foreach(var pair in entries) if(pair.Value.Car==null || pair.Value.Seen!=revision) expired.Add(pair.Key);
                foreach(var key in expired) entries.Remove(key);
                SnowPerformance.SideSnow(tracked,depositing,sheltered,maximum,snowfall);
            }
            if(snowfall>.001f && temperatureCelsius<0f) ProcessShelter();
        }

        private static float Advance(float amount,float speed,float snowfall,float temperature,float heat,
            bool exposed,float seconds,Vector3 air,float nx,float nz)
        {
            return VehicleSideSnowProfile.Advance(amount,speed,snowfall,temperature,heat,exposed,seconds,
                VehicleSideSnowProfile.ImpactFactor(speed,air.x,air.z,nx,nz));
        }

        private static Vector3 Roof(TrainCar car)
        {
            var bounds=car.Bounds;
            return car.transform.TransformPoint(bounds.center+Vector3.up*(bounds.extents.y+.2f));
        }

        private void ProcessShelter()
        {
            if(probeFrame!=Time.frameCount) {probeFrame=Time.frameCount;probeBudget=4;}
            while(probeBudget>0 && shelterQueue.Count>0)
            {
                var entry=shelterQueue.Dequeue();entry.Queued=false;probeBudget--;
                var car=entry.Car;Entry current;
                if(car==null || !car.gameObject.activeInHierarchy ||
                    !entries.TryGetValue(car.GetInstanceID(),out current) || !ReferenceEquals(current,entry)) continue;
                var point=Roof(car);
                int count=Physics.RaycastNonAlloc(point,Vector3.up,shelterHits,150f,SeasonSurfaceLayers.Mask,QueryTriggerInteraction.Ignore);
                bool blocked=count==shelterHits.Length;
                for(int i=0;i<count && !blocked;i++)
                {
                    var hit=shelterHits[i].collider;
                    if(hit!=null && !OwnPart(hit.transform,car)) blocked=true;
                }
                entry.ProbePosition=point-DV.OriginShift.OriginShift.currentMove;
                entry.NextProbe=Time.time+1f;entry.ShelterReady=true;entry.Sheltered=blocked;
            }
        }

        private static bool OwnPart(Transform child,TrainCar car)
        {
            return Below(child,car.transform) || Below(child,car.interior) || Below(child,car.interiorLOD) ||
                Below(child,car.loadedInterior!=null?car.loadedInterior.transform:null) ||
                Below(child,car.loadedExternalInteractables!=null?car.loadedExternalInteractables.transform:null) ||
                Below(child,car.loadedDummyExternalInteractables!=null?car.loadedDummyExternalInteractables.transform:null);
        }
        private static bool Below(Transform child,Transform root)
        {return root!=null && (child==root || child.IsChildOf(root));}

        public void Save(List<VehicleSideSnowState> destination)
        {
            foreach(var pair in saved) destination.Add(new VehicleSideSnowState {Id=pair.Key,Amount=pair.Value});
        }
        public void Restore(List<VehicleSideSnowState> records)
        {
            Reset();if(records==null) return;
            foreach(var record in records)
                if(record!=null && ValidId(record.Id) && ValidAmount(record.Amount) && saved.Count<16384)
                    saved[record.Id]=record.Amount;
        }

        public VehicleSideSnowNetworkState[] CaptureNetworkState()
        {
            // SeasonRuntime may prepare network state every frame; allocate only
            // at the bridge's one-second transmission cadence.
            if(Time.realtimeSinceStartup<nextSnapshot) return networkSnapshot;
            nextSnapshot=Time.realtimeSinceStartup+1f;
            var snapshot=new VehicleSideSnowNetworkState[Math.Min(saved.Count,VehicleSideSnowNetworkState.MaxVehicleCount)];
            int count=0;
            foreach(var pair in saved)
            {
                if(count==snapshot.Length) break;
                var state=new VehicleSideSnowNetworkState {CarId=pair.Key,PositiveX=pair.Value.x,NegativeX=pair.Value.y,
                    PositiveZ=pair.Value.z,NegativeZ=pair.Value.w};
                if(!state.IsValid()) continue;
                snapshot[count++]=state;
            }
            if(count!=snapshot.Length) Array.Resize(ref snapshot,count);
            return networkSnapshot=snapshot;
        }
        public void ApplyNetworkState(VehicleSideSnowNetworkState[] states)
        {
            if(authority) return;
            saved.Clear();
            if(states==null) {SnowPerformance.SideSnow(0,0,0,0f,0f);return;}
            float maximum=0f;
            for(int i=0;i<Math.Min(states.Length,VehicleSideSnowNetworkState.MaxVehicleCount);i++)
            {
                var state=states[i];
                if(state.IsValid())
                {
                    var amount=new Vector4(state.PositiveX,state.NegativeX,state.PositiveZ,state.NegativeZ);
                    saved[state.CarId]=amount;maximum=Mathf.Max(maximum,Maximum(amount));
                }
            }
            SnowPerformance.SideSnow(saved.Count,0,0,maximum,0f);
        }

        private static float Maximum(Vector4 amount) {return Mathf.Max(Mathf.Max(amount.x,amount.y),Mathf.Max(amount.z,amount.w));}
        private static bool ValidId(string id) {return !string.IsNullOrEmpty(id) && id.Length<=80;}
        private static bool ValidAmount(Vector4 value)
        {return SnowWorldSave.Unit(value.x) && SnowWorldSave.Unit(value.y) && SnowWorldSave.Unit(value.z) && SnowWorldSave.Unit(value.w);}
        public void Reset()
        {
            entries.Clear();saved.Clear();expired.Clear();shelterQueue.Clear();elapsed=0f;
            nextSnapshot=0f;networkSnapshot=VehicleSideSnowNetworkState.Empty;
            SnowPerformance.SideSnow(0,0,0,0f,0f);
        }
    }
}
