using DV.OriginShift;
using UnityEngine;
using System.Collections.Generic;
namespace DVSeasons.Mod
{
    internal sealed class RailSnowGameSource
    {
        private sealed class CarMotion
        { public Vector3 Position; public Quaternion Rotation; public float SnowClock; }
        private readonly Dictionary<int,CarMotion> motion=new Dictionary<int,CarMotion>();
        private readonly HashSet<int> liveCars=new HashSet<int>();
        private readonly List<int> expiredCars=new List<int>();
        private List<TrainCar> cars;
        private static readonly List<TrainCar> emptyCars=new List<TrainCar>();
        internal static Vector3 ContactPoint(Transform axle,Transform bogie)
        {
            // Bogie origin follows the rail plane. An engine-wide driving-wheel
            // radius puts S282's smaller leading/trailing wheels below the track.
            // Use each axle's actual height; axle rotation must not change up.
            var up=bogie.up;
            return axle.position-up*Vector3.Dot(axle.position-bogie.position,up);
        }
        public static List<TrainCar> GetCars()
        {
            var spawner=CarSpawner.Instance;
            return spawner!=null && spawner.AllCars!=null ? spawner.AllCars : emptyCars;
        }
        private float nextScan;
        public void Update(RailSnowTracks tracks, System.Action<int,Vector3,Vector3,Vector3,Vector3,Vector2> wheelSnow = null,
            System.Action<TrainCar,Vector3,Vector3,Vector3> reportContact = null)
        {
            tracks.WorldOffset=OriginShift.currentMove;
            if(cars==null || Time.realtimeSinceStartup>=nextScan)
            {
                cars=GetCars();
                nextScan=Time.realtimeSinceStartup+5f;
                liveCars.Clear();foreach(var car in cars) if(car!=null) liveCars.Add(car.GetInstanceID());
                expiredCars.Clear();foreach(var id in motion.Keys) if(!liveCars.Contains(id)) expiredCars.Add(id);
                foreach(var id in expiredCars) motion.Remove(id);
            }
            var camera=PlayerManager.ActiveCamera!=null ? PlayerManager.ActiveCamera : Camera.main;
            bool sampleWheels=wheelSnow!=null && camera!=null;
            var cameraPosition=sampleWheels ? camera.transform.position : Vector3.zero;
            foreach(var car in cars)
            {
                if(car==null || !car.gameObject.activeInHierarchy || !car.AreBogiesFullyInitialized()) continue;
                var pose=car.transform;
                int carId=car.GetInstanceID();
                var position=pose.position-tracks.WorldOffset; var rotation=pose.rotation;
                CarMotion previous;
                if(motion.TryGetValue(carId,out previous))
                {
                    if((position-previous.Position).sqrMagnitude<0.000001f &&
                        Mathf.Abs(Quaternion.Dot(rotation,previous.Rotation))>0.9999999f &&
                        tracks.SnowClock-previous.SnowClock<0.015f) continue;
                }
                else {previous=new CarMotion();motion[carId]=previous;}
                previous.Position=position;previous.Rotation=rotation;previous.SnowClock=tracks.SnowClock;
                var velocity=car.GetVelocity();
                var leadingOffset=sampleWheels ? velocity.normalized*.06f : Vector3.zero;
                Vector3 contactSum=Vector3.zero,rightSum=Vector3.zero,upSum=Vector3.zero;int contactCount=0;
                foreach(var bogie in car.Bogies)
                {
                    if(bogie==null || bogie.HasDerailed || bogie.track==null) continue;
                    var bogiePose=bogie.transform;
                    var bogiePosition=bogiePose.position;
                    var up=bogiePose.up;var right=bogiePose.right;var forward=bogiePose.forward;
                    var side=right*.75f;
                    // Axle transforms rotate around X; use bogie up/right for contact.
                    foreach(var axle in bogie.Axles)
                    {
                        var t=axle.transform;
                        if(t==null) continue;
                        var axlePosition=t.position;
                        var contact=axlePosition-up*Vector3.Dot(axlePosition-bogiePosition,up);
                        contactSum+=contact;rightSum+=right;upSum+=up;contactCount++;
                        if(sampleWheels && (contact-cameraPosition).sqrMagnitude<=90f*90f)
                        {
                            // Sample the leading edge of the contact patch before
                            // this axle clears it. Each rail has its own history.
                            var sample=contact+leadingOffset;
                            var remaining=new Vector2(tracks.RemainingAt(sample-side),tracks.RemainingAt(sample+side));
                            wheelSnow(t.GetInstanceID(),contact,right,up,velocity,remaining);
                        }
                        tracks.WheelAt(t.GetInstanceID(),contact,right,forward);
                    }
                }
                if(contactCount>0)reportContact?.Invoke(car,contactSum/contactCount,rightSum.normalized,upSum.normalized);
            }
        }
        public void Reset() { cars=null; nextScan=0; motion.Clear(); liveCars.Clear(); expiredCars.Clear(); }
    }
}
