using System;

namespace DVSeasons.Core
{
    public static class LocomotiveSnowHeat
    {
        public const float SteamMeltSeconds = 30f;
        public const float DieselMeltSeconds = 45f;
        // Spawned S282 boilers can report a warm default temperature before their
        // own fire is lit. The fire flag belongs to this exact locomotive and does
        // not make neighbouring, idle steam locomotives appear hot.
        public static bool SteamIsHot(bool fireOn,float boilerTemperature) {return fireOn;}
        // Steam heat clears the exposed cover quickly; diesel heat removes a
        // smaller amount more gradually. Running engines retain their heat during
        // snowfall; after shutdown only falling snow restores the lost cover.
        public static float Advance(float melted,bool running,bool steam,bool battery,float snowfall,float seconds)
        {
            if(battery) return 0;
            if(float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds<=0) return melted;
            float maximum=steam?1f:0.30f;
            float meltSeconds=steam?SteamMeltSeconds:DieselMeltSeconds;
            if(running) return Math.Min(maximum,Math.Max(0,melted)+maximum*seconds/meltSeconds);
            float fallingSnow=Math.Max(0,Math.Min(1,snowfall));
            if(fallingSnow<=0.001f) return Math.Max(0,melted);
            // Once snow is visibly falling, restore heat-cleared cover in about
            // 60-120 seconds depending on intensity. The old proportional 180 s
            // rule could take tens of minutes during light snow.
            float recovery=0.5f+0.5f*fallingSnow;
            return Math.Max(0,melted-recovery*seconds/60f);
        }

        // The deferred overlay is blended over a bright snow base. A literal 30%
        // alpha reduction was almost imperceptible on the broad, pale roofs of the
        // DM1U, DH4 and DE6. Preserve the requested partial diesel melt internally,
        // but map it to a clearly readable visual reduction. Steam still reaches
        // zero cover and BE2 still remains at full cover.
        public static float VisibleRemaining(float melted)
        {
            if(float.IsNaN(melted) || float.IsInfinity(melted)) return 1f;
            return Math.Max(0,Math.Min(1,1-melted*(5f/3f)));
        }
    }
}
