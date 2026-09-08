using System;
using System.Collections.Generic;
using System.Reflection;
using DV.ThingTypes;
using DVSeasons.Core;
using LocoSim.Implementations;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class LocomotiveSnowHeatController
    {
        private sealed class Entry
        {
            public TrainCar Car;
            public SimulationFlow Flow;
            public Port EngineOn,EngineRpm;
            public float Melted;
            public bool Running,StateKnown;
            public int ReportedStep=-1;
            public string Detector="none";
        }
        private readonly Dictionary<int,Entry> entries=new Dictionary<int,Entry>();
        private readonly List<int> expired=new List<int>();
        public void Update(float snowfall,float coverage,float seconds)
        {
            if(coverage<=0.001f) {entries.Clear();return;}
            foreach(var car in RailSnowGameSource.GetCars())
            {
                if(car==null || !car.IsLoco) continue;
                Entry entry;int id=car.GetInstanceID();
                if(!entries.TryGetValue(id,out entry)) {entry=new Entry {Car=car};entries.Add(id,entry);}
                bool battery=car.carType==TrainCarType.LocoMicroshunter;
                bool steam=car.carType==TrainCarType.LocoSteamHeavy || car.carType==TrainCarType.LocoS060;
                var sim=car.SimController;var flow=sim!=null?sim.simFlow:null;
                if(entry.Flow!=flow)
                {
                    entry.Flow=flow;entry.EngineOn=null;entry.EngineRpm=null;
                    entry.Detector=Bind(flow,steam,entry);
                    Debug.Log("[DVSeasons] Snow heat bound " + Label(car) + ": " +
                        (battery?"BE2 exception":entry.Detector) + ".");
                }
                bool running=flow!=null && (steam ? LocomotiveSnowHeat.SteamIsHot(
                    sim.firebox!=null && sim.firebox.IsFireOn,0f) :
                    (entry.EngineOn!=null && entry.EngineOn.Value>0.5f) ||
                    (entry.EngineOn==null && entry.EngineRpm!=null && entry.EngineRpm.Value>0.05f));
                if(!entry.StateKnown || entry.Running!=running)
                    Debug.Log("[DVSeasons] Snow heat " + Label(car) + " is " + (running?"active":"inactive") +
                        " (detector " + entry.Detector + ").");
                entry.StateKnown=true;
                entry.Running=running;
                entry.Melted=LocomotiveSnowHeat.Advance(entry.Melted,running,steam,battery,snowfall,seconds);
                Report(entry,steam,battery);
            }
            expired.Clear();foreach(var pair in entries) if(pair.Value.Car==null) expired.Add(pair.Key);
            foreach(int id in expired) entries.Remove(id);
        }
        public float Remaining(Component source)
        {
            var car=source as TrainCar;
            if(car==null && source!=null) car=source.GetComponentInParent<TrainCar>();
            if(car==null) return 1f;
            Entry entry;return entries.TryGetValue(car.GetInstanceID(),out entry)
                ? LocomotiveSnowHeat.VisibleRemaining(entry.Melted) : 1f;
        }

        // Interior and streamed exterior colliders can be detached from the
        // TrainCar root. Resolve those colliders against the exact car entry so
        // audio follows the same per-locomotive melt state as the snow shader.
        public float RemainingAt(Collider collider)
        {
            if(collider==null) return 1f;
            var direct=collider.GetComponentInParent<TrainCar>();
            if(direct!=null) return Remaining(direct);
            var transform=collider.transform;
            foreach(var pair in entries)
            {
                var car=pair.Value.Car;
                if(car==null || (!IsPartOf(transform,car.transform) &&
                    !IsPartOf(transform,car.interior) && !IsPartOf(transform,car.interiorLOD) &&
                    !IsPartOf(transform,car.loadedInterior!=null?car.loadedInterior.transform:null) &&
                    !IsPartOf(transform,car.loadedExternalInteractables!=null?car.loadedExternalInteractables.transform:null) &&
                    !IsPartOf(transform,car.loadedDummyExternalInteractables!=null?car.loadedDummyExternalInteractables.transform:null))) continue;
                return LocomotiveSnowHeat.VisibleRemaining(pair.Value.Melted);
            }
            return 1f;
        }

        private static bool IsPartOf(Transform child,Transform root)
        { return child!=null && root!=null && (child==root || child.IsChildOf(root)); }
        private static string Bind(SimulationFlow flow,bool steam,Entry entry)
        {
            if(flow==null) return "simulation unavailable";
            foreach(var component in flow.OrderedSimComps)
            {
                if(steam)
                {
                    if(component is Boiler) return "own firebox";
                    continue;
                }
                var field=component.GetType().GetField("engineOnReadOut",
                    BindingFlags.Public|BindingFlags.Instance|BindingFlags.FlattenHierarchy);
                if(field==null) continue;
                entry.EngineOn=field.GetValue(component) as Port;
                if(entry.EngineOn!=null) return component.GetType().Name+"."+field.Name;
            }
            if(!steam)
                foreach(var port in flow.AllPorts)
                {
                    if(port==null) continue;
                    var id=Normalize(port.id);
                    if(id.Contains("engineon")) {entry.EngineOn=port;return "port "+port.id;}
                    if(entry.EngineRpm==null && (id.Contains("enginerpmnormalized") || id.Contains("enginerpm")))
                        entry.EngineRpm=port;
                }
            if(entry.EngineRpm!=null) return "RPM port "+entry.EngineRpm.id;
            return steam?"own firebox":"engine port missing";
        }
        private static string Normalize(string value)
        {
            if(string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("_",string.Empty).Replace("-",string.Empty).Replace(".",string.Empty).ToLowerInvariant();
        }
        private static string Label(TrainCar car)
        {
            if(car==null) return "<destroyed car>";
            var id=car.logicCar!=null?car.ID:"instance "+car.GetInstanceID();
            return car.name+" ["+id+", "+car.carType+"]";
        }
        private static void Report(Entry entry,bool steam,bool battery)
        {
            if(entry==null || battery) return;
            float maximum=steam?1f:.30f;
            int step=Mathf.Clamp(Mathf.FloorToInt(entry.Melted/maximum*4.001f),0,4);
            if(step==entry.ReportedStep) return;
            entry.ReportedStep=step;
            if(step==0) return;
            Debug.Log("[DVSeasons] Snow heat " + Label(entry.Car) + " melted " +
                Mathf.RoundToInt(entry.Melted*100f) + "% of dynamic cover.");
        }
        public void Reset() {entries.Clear();expired.Clear();}
    }
}
