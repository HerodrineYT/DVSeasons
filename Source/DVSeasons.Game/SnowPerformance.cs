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
            if(dt>0 && dt<1f) {frames++;frameSeconds+=dt;}
            if(nextReport<=0) nextReport=Time.realtimeSinceStartup+20f;
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
                .Append(". CPU scopes exclude GPU execution; FPS includes the whole game.");
            UnityEngine.Debug.Log(report.ToString());nextReport=Time.realtimeSinceStartup+20f;frames=0;frameSeconds=0;
        }
        public static void Reset() {counters.Clear();nextReport=0;frames=0;frameSeconds=0;}
    }
}
