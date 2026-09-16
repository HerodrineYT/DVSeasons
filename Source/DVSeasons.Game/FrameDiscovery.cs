using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DVSeasons.Mod
{
    // Unity object access stays on the main thread. Yield between objects, with
    // both a time and an item limit; a single native operation cannot be preempted.
    internal static class FrameDiscovery
    {
        public static void Advance(ref IEnumerator<int> work)
        {
            if (work == null) return;
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000;
            try
            {
                for (int step = 0; step < 64; step++)
                {
                    if (!work.MoveNext()) { work.Dispose(); work = null; return; }
                    if (Stopwatch.GetTimestamp() >= deadline) return;
                }
            }
            catch { work.Dispose(); work = null; throw; }
        }
    }
}
