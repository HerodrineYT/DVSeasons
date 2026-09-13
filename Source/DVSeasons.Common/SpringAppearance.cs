using System;

namespace DVSeasons.Core
{
    public static class SpringAppearance
    {
        public static float Weight(SeasonState state)
        {
            if (state == null) return 0;
            return (state.Current == SeasonKind.Spring ? 1-state.Transition : 0)
                + (state.Next == SeasonKind.Spring ? state.Transition : 0);
        }

        // Preserve bark, petals and the original light/shadow detail. Only
        // vegetation-coloured pixels receive the young, lighter yellow-green.
        // SpringTerrain.shader uses the same palette in gamma colour space.
        public static void Recolor(ref float r, ref float g, ref float b, bool evergreen)
        {
            float mask=Clamp((g-r*.90f-b*.12f)/.075f)*Clamp((g-b*.8f)/.08f);
            float luma=r*.2126f+g*.7152f+b*.0722f;
            float amount=mask*(evergreen ? .30f : .85f);
            r=Clamp(r+(luma*.95f+.035f-r)*amount);
            g=Clamp(g+(luma*1.40f+.045f-g)*amount);
            b=Clamp(b+(luma*.46f+.018f-b)*amount);
        }
        private static float Clamp(float value) { return Math.Max(0,Math.Min(1,value)); }
    }
}
