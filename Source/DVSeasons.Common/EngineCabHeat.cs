using System;

namespace DVSeasons.Core
{
    // Available heat for a cab supplied directly by its engine. This is not
    // an electrical heater setting, so cold/stopped engines supply no heat.
    public sealed class EngineCabHeat
    {
        private float warmth;
        private float delivery = 1;
        private bool hasDelivery;
        public float Advance(float seconds, bool running, float engineTemperature)
        { return AdvanceAtRpm(seconds, running, engineTemperature, 1, .25f); }

        public float AdvanceAtRpm(float seconds, bool running, float engineTemperature,
            float rpmNormalized, float idleRpmNormalized)
        {
            if (float.IsNaN(seconds) || float.IsInfinity(seconds)) seconds = 0;
            seconds = Math.Max(0, Math.Min(5, seconds));
            idleRpmNormalized = Finite(idleRpmNormalized, .25f);
            idleRpmNormalized = Math.Max(.05f, Math.Min(.9f, idleRpmNormalized));
            rpmNormalized = Finite(rpmNormalized, idleRpmNormalized);
            float effort = Clamp((rpmNormalized - idleRpmNormalized) / (1 - idleRpmNormalized));
            float targetDelivery = .8f + .2f * effort;
            if (!hasDelivery) { delivery = targetDelivery; hasDelivery = true; }
            // Pump flow changes with RPM, but does not make cabin temperature jump.
            // Keep the last heat-delivery factor after shutdown while stored heat cools.
            if (running) delivery += (targetDelivery - delivery) * (1 - (float)Math.Exp(-seconds / 10));
            float warmupSeconds = 75 - 57 * effort;
            warmth += ((running ? 1 : 0) - warmth) * (1 - (float)Math.Exp(-seconds / (running ? warmupSeconds : 240)));
            float measured = float.IsNaN(engineTemperature) || float.IsInfinity(engineTemperature)
                ? 0 : Clamp((engineTemperature - 20) / 60);
            return Math.Max(warmth, measured) * delivery;
        }
        private static float Clamp(float value) { return Math.Max(0, Math.Min(1, value)); }
        private static float Finite(float value, float fallback)
        { return float.IsNaN(value) || float.IsInfinity(value) ? fallback : value; }
    }
}
