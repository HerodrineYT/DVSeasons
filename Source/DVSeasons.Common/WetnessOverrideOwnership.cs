using System;

namespace DVSeasons.Core
{
    /// <summary>
    /// Tracks ownership of a single floating-point weather override. The caller
    /// only restores the captured value while its last applied value is still
    /// present, so a later override from another system is left untouched.
    /// </summary>
    public class WeatherOverrideOwnership
    {
        private bool owned;
        private bool previousOverridden;
        private float previous;
        private float applied;

        public bool Acquire(bool overridden, float value, bool respectExternal)
        {
            if (owned && overridden && Same(value, applied)) return true;

            // An external override may end later. Do not latch a permanent refusal.
            // When this effect has priority, a replacement override becomes the new
            // baseline and is restored when the effect releases ownership.
            owned = false;
            if (respectExternal && overridden) return false;
            previousOverridden = overridden;
            previous = value;
            owned = true;
            return true;
        }

        public void Applied(float value) { applied = value; }

        public bool Owns(bool overridden, float value)
        { return owned && overridden && Same(value, applied); }

        public bool Release(bool overridden, float value, out bool restoreOverride, out float restoreValue)
        {
            restoreOverride = previousOverridden;
            restoreValue = previous;
            var release = owned && overridden && Same(value, applied);
            owned = false;
            return release;
        }

        public void Reset() { owned = false; }

        private static bool Same(float a, float b) { return Math.Abs(a - b) < 0.0001f; }
    }

    // Retain the original public type for source and binary consumers of the core assembly.
    public sealed class WetnessOverrideOwnership : WeatherOverrideOwnership { }
}
