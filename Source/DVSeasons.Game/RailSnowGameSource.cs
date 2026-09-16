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
                var live=new HashSet<int>();foreach(var car in cars) if(car!=null) live.Add(car.GetInstanceID());
                var expired=new List<int>();foreach(var id in motion.Keys) if(!live.Contains(id)) expired.Add(id);
                foreach(var id in expired) motion.Remove(id);
            }
            var camera=PlayerManager.ActiveCamera!=null ? PlayerManager.ActiveCamera : Camera.main;
            foreach(var car in cars)
            {
                if(car==null || !car.gameObject.activeInHierarchy || !car.AreBogiesFullyInitialized()) continue;
                var pose=car.transform;
                var position=pose.position-tracks.WorldOffset; var rotation=pose.rotation;
                CarMotion previous;
                if(motion.TryGetValue(car.GetInstanceID(),out previous))
                {
                    if((position-previous.Position).sqrMagnitude<0.000001f &&
                        Mathf.Abs(Quaternion.Dot(rotation,previous.Rotation))>0.9999999f &&
                        tracks.SnowClock-previous.SnowClock<0.015f) continue;
                }
                else {previous=new CarMotion();motion[car.GetInstanceID()]=previous;}
                previous.Position=position;previous.Rotation=rotation;previous.SnowClock=tracks.SnowClock;
                var velocity=car.GetVelocity();
                Vector3 contactSum=Vector3.zero,rightSum=Vector3.zero,upSum=Vector3.zero;int contactCount=0;
                foreach(var bogie in car.Bogies)
                {
                    if(bogie==null || bogie.HasDerailed || bogie.track==null) continue;
                    // Axle transforms rotate around X; use bogie up/right for contact.
                    foreach(var axle in bogie.Axles)
                    {
                        var t=axle.transform;
                        if(t==null) continue;
                        var contact=ContactPoint(t,bogie.transform);
                        contactSum+=contact;rightSum+=bogie.transform.right;upSum+=bogie.transform.up;contactCount++;
                        if(wheelSnow!=null && camera!=null && (contact-camera.transform.position).sqrMagnitude<=90f*90f)
                        {
                            // Sample the leading edge of the contact patch before
                            // this axle clears it. Each rail has its own history.
                            var sample=contact+velocity.normalized*.06f;
                            var side=bogie.transform.right*.75f;
                            var remaining=new Vector2(tracks.RemainingAt(sample-side),tracks.RemainingAt(sample+side));
                            wheelSnow(t.GetInstanceID(),contact,bogie.transform.right,bogie.transform.up,velocity,remaining);
                        }
                        tracks.WheelAt(t.GetInstanceID(),contact,bogie.transform.right,bogie.transform.forward);
                    }
                }
                if(contactCount>0)reportContact?.Invoke(car,contactSum/contactCount,rightSum.normalized,upSum.normalized);
            }
        }
        public void Reset() { cars=null; nextScan=0; motion.Clear(); }
    }
}
