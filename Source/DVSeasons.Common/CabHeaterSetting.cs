using System;

namespace DVSeasons.Core
{
    public static class CabHeaterSetting
    {
        public static float Normalize(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return (float)Math.Round(Math.Max(0f, Math.Min(1f, value)) * 3f) / 3f;
        }

        // Old DM3 intermediate save values become on; malformed values stay off.
        public static float NormalizeSwitch(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f ? 1f : 0f;
        }

        public static bool IsAllowed(float value)
        {
            return value == 0f || value == 1f;
        }

        public static float FromControlValue(float value)
        {
            // Physical joints can report near-endpoint values while moving.
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0.5f ? 1f : 0f;
        }
    }
}
