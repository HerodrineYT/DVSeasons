using System;

namespace DVSeasons.Core
{
    public sealed class WetnessOverrideOwnership
    {
        private bool owned, previousOverridden;
        private float previous, applied;
        private static bool Same(float a,float b) { return Math.Abs(a-b)<0.0001f; }
        public bool Acquire(bool overridden,float value,bool respectExternal)
        {
            if(owned && overridden && Same(value,applied)) return true;
            // An external override may end later. Do not latch a permanent refusal.
            owned=false;
            if(respectExternal && overridden) return false;
            previousOverridden=overridden;previous=value;owned=true;return true;
        }
        public void Applied(float value) { applied=value; }
        public bool Release(bool overridden,float value,out bool restoreOverride,out float restoreValue)
        {
            restoreOverride=previousOverridden;restoreValue=previous;
            bool release=owned && overridden && Same(value,applied);
            owned=false;return release;
        }
        public void Reset() { owned=false; }
    }
}
