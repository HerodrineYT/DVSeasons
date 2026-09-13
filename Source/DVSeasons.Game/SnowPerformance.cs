using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Local aggregate timings; no readback, network traffic, or per-frame log writes.
    internal static class SnowPerformance
    {
        private sealed class Counter { public long Ticks, Maximum; public int Count; }
        private static readonly Dictionary<string,Counter> counters=new Dictionary<string,Counter>();
        private static float nextReport;
        private static int frames;
        private static double frameSeconds;
        private static float maximumFrame;
        private static int slowFrames, lastCollections;
        public struct Scope : IDisposable
        {
            private readonly string name;
            private readonly long start;
            public Scope(string name) {this.name=name;start=Stopwatch.GetTimestamp();}
            public void Dispose()
            {
                var ticks=Stopwatch.GetTimestamp()-start;Counter counter;
                if(!counters.TryGetValue(name,out counter)) {counter=new Counter();counters.Add(name,counter);}
                counter.Ticks+=ticks;counter.Maximum=Math.Max(counter.Maximum,ticks);counter.Count++;
            }
        }
        public static Scope Measure(string name) {return new Scope(name);}
        public static void Frame()
        {
            var dt=Time.unscaledDeltaTime;
            // Do not discard stalls >= 1 second: those are exactly the frames
            // this diagnostic needs to expose when a client reports freezes.
            if(dt>0) {frames++;frameSeconds+=dt;maximumFrame=Math.Max(maximumFrame,dt);if(dt>=.1f)slowFrames++;}
            if(nextReport<=0) {nextReport=Time.realtimeSinceStartup+20f;lastCollections=GC.CollectionCount(0);}
            if(Time.realtimeSinceStartup<nextReport) return;
            var report=new StringBuilder("[DVSeasons] Performance: CPU submission ms mean/max; ");
            foreach(var pair in counters)
            {
                var c=pair.Value;double factor=1000d/Stopwatch.Frequency;
                report.Append(pair.Key).Append('=')
                    .Append((c.Count>0?c.Ticks*factor/c.Count:0).ToString("F2",CultureInfo.InvariantCulture)).Append('/')
                    .Append((c.Maximum*factor).ToString("F2",CultureInfo.InvariantCulture)).Append("; ");
                c.Ticks=c.Maximum=0;c.Count=0;
            }
            report.Append("whole-frame FPS=").Append((frameSeconds>0?frames/frameSeconds:0).ToString("F1",CultureInfo.InvariantCulture))
                .Append("; max-frame-ms=").Append((maximumFrame*1000).ToString("F1",CultureInfo.InvariantCulture))
                .Append("; frames>=100ms=").Append(slowFrames)
                .Append("; GC0=").Append(GC.CollectionCount(0)-lastCollections)
                .Append(". CPU scopes exclude GPU execution; FPS includes the whole game.");
            UnityEngine.Debug.Log(report.ToString());nextReport=Time.realtimeSinceStartup+20f;frames=0;frameSeconds=0;
            maximumFrame=0;slowFrames=0;lastCollections=GC.CollectionCount(0);
        }
        public static void Reset() {counters.Clear();nextReport=0;frames=0;frameSeconds=0;maximumFrame=0;slowFrames=0;lastCollections=0;}
    }
}
