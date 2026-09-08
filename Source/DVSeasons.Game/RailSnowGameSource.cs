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
        public static List<TrainCar> GetCars()
        {
            var spawner=CarSpawner.Instance;
            return spawner!=null && spawner.AllCars!=null ? spawner.AllCars : emptyCars;
        }
        private float nextScan;
        public void Update(RailSnowTracks tracks)
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
                foreach(var bogie in car.Bogies)
                {
                    if(bogie==null || bogie.HasDerailed || bogie.track==null) continue;
                    // Axle transforms rotate around X; use bogie up/right for contact.
                    foreach(var axle in bogie.Axles)
                    {
                        var t=axle.transform;
                        if(t==null) continue;
                        var contact=t.position-bogie.transform.up*car.carLivery.parentType.wheelRadius;
                        tracks.WheelAt(t.GetInstanceID(),contact,bogie.transform.right,bogie.transform.forward);
                    }
                }
            }
        }
        public void Reset() { cars=null; nextScan=0; motion.Clear(); }
    }
}
