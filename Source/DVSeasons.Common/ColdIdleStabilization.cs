namespace DVSeasons.Core
{
    // DE6's local engine-room throttle must be opened promptly and held through
    // the first three seconds of cold idle. No accumulated taps are accepted.
    public sealed class ColdIdleStabilization
    {
        private float elapsed, held;
        private bool opened;
        public bool Active { get; private set; }
        public void Begin() { elapsed = held = 0; opened = false; Active = true; }
        public void Reset() { Active = false; }
        public bool Advance(float delta, bool leverOpen)
        {
            if (!Active || delta <= 0) return false;
            elapsed += delta;
            if (!opened && leverOpen && elapsed <= 2.0001f) opened = true;
            if (!opened && elapsed >= 2.0001f || opened && !leverOpen)
            { Active = false; return true; }
            if (opened)
            {
                held += delta;
                if (held >= 2.9999f) Active = false;
            }
            return false;
        }
    }
}
