using System;

namespace DVSeasons.Core
{
    // Gameplay tuning for the existing starter and lead-acid battery models.
    // Does not change damage limits, charge snapshots or a running engine's power.
    public static class ColdPowertrainProfile
    {
        private static float Cold(float temperature, float onset)
        {
            temperature = SeasonalThermalProfile.NormalizeAmbient(temperature);
            return Math.Max(0, Math.Min(1, (onset-temperature)/(onset+30)));
        }

        public static float DieselStartMultiplier(float engineTemperature)
        {
            var cold = Cold(engineTemperature, 5);
            return 1 + 2.5f*cold*cold;
        }

        // Absolute continuous starter-hold time, not a multiplier on the native
        // random ignition delay. A hot engine keeps its normal restart.
        public static float StarterHoldSeconds(float ambient, float engineTemperature, float nativeSeconds)
        {
            ambient = SeasonalThermalProfile.NormalizeAmbient(ambient);
            if (ambient >= 0 || engineTemperature >= 40) return nativeSeconds;
            float coldSeconds = ambient <= -25 ? 12 : ambient <= -10
                ? 5 + (-ambient - 10) * (7f / 15f)
                : nativeSeconds + (5 - nativeSeconds) * (-ambient / 10);
            if (float.IsNaN(engineTemperature) || float.IsInfinity(engineTemperature)) engineTemperature = ambient;
            float warmth = Math.Max(0, Math.Min(1, (engineTemperature - ambient) / (40 - ambient)));
            warmth = warmth * warmth * (3 - 2 * warmth);
            return coldSeconds + (nativeSeconds - coldSeconds) * warmth;
        }

        // The engine block retains combustion heat even on DE6, whose native
        // temperature input is unconnected. Coolant gauges keep their own model.
        public static float StepEngineBlockTemperature(float current, float ambient, bool running, float delta)
        {
            ambient = SeasonalThermalProfile.NormalizeAmbient(ambient);
            if (float.IsNaN(current) || float.IsInfinity(current)) current = ambient;
            if (float.IsNaN(delta) || float.IsInfinity(delta) || delta <= 0) return current;
            float target = running ? 65 : ambient;
            return current + (target-current)*(float)(1-Math.Exp(-delta/(running ? 150 : 600)));
        }

        public static float BatteryResistanceMultiplier(float packTemperature)
        { return 1 + 1.2f*Cold(packTemperature, 10); }

        public static float BatteryConsumptionMultiplier(float packTemperature)
        { return 1/(1-.4f*Cold(packTemperature, 10)); }

        public static float StepBatteryTemperature(float current, float ambient, float watts, float delta)
        {
            ambient = SeasonalThermalProfile.NormalizeAmbient(ambient);
            if(float.IsNaN(current) || float.IsInfinity(current)) current = ambient;
            if(float.IsNaN(watts) || float.IsInfinity(watts)) watts = 0;
            if(float.IsNaN(delta) || float.IsInfinity(delta) || delta <= 0) return current;
            var load = Math.Max(0, Math.Min(1, watts/150000f));
            var target = ambient + 12*load;
            return current + (target-current)*(float)(1-Math.Exp(-delta/900));
        }
    }
}
