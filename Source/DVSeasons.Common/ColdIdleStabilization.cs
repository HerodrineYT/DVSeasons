namespace DVSeasons.Core
{
    // Within five seconds of ignition, open DE6's engine-room throttle or set
    // the cab throttle to maximum, then sustain either for three seconds.
    // No accumulated taps are accepted.
    public sealed class ColdIdleStabilization
    {
        public const float ResponseSeconds = 5f;
        public const float HoldSeconds = 3f;
        private float elapsed, held;
        private bool opened;
        public bool Active { get; private set; }
        public float RemainingSeconds => Active ? System.Math.Max(0,HoldSeconds-held) : 0;
        public void Begin() { elapsed = held = 0; opened = false; Active = true; }
        public void Reset() { Active = false; }
        public bool Advance(float delta, bool leverOpen)
        {
            if (!Active || delta <= 0) return false;
            elapsed += delta;
            if (!opened && leverOpen && elapsed <= ResponseSeconds + .0001f) opened = true;
            if (!opened && elapsed >= ResponseSeconds + .0001f || opened && !leverOpen)
            { Active = false; return true; }
            if (opened)
            {
                held += delta;
                if (held >= HoldSeconds - .0001f) Active = false;
            }
            return false;
        }
    }
}
