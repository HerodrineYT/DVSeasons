using System;

namespace DVSeasons.Core
{
    /// <summary>
    /// Pure temperature-dependent parts of the native locomotive thermal model.
    /// Derail Valley uses 25 C as its fixed outside temperature; these helpers
    /// preserve the original result at 25 C and replace that reference point with
    /// the current seasonal air temperature.
    /// </summary>
    public static class SeasonalThermalProfile
    {
        public const float VanillaAmbientCelsius = 25f;
        public const float MinimumAmbientCelsius = -40f;
        public const float MaximumAmbientCelsius = 45f;
        public const float MinimumFireboxTemperature = 500f;
        public const float FireboxAmbientCoupling = 0.35f;
        public const float FireboxResponseChangePerDegree = 0.004f;
        public const float BrakeTemperatureGainRate = 1.5f;
        public const float BrakeCoolingRate = 0.004f;
        public const float BrakeMaximumTemperature = 1000f;
        public const float BrakeOverheatStart = 600f;
        public const float BrakeOverheatEnd = 1000f;
        public const float BrakeMinimumForceFactor = 0.12f;

        public static float NormalizeAmbient(float ambientCelsius)
        {
            if (float.IsNaN(ambientCelsius) || float.IsInfinity(ambientCelsius))
                return VanillaAmbientCelsius;
            return Clamp(ambientCelsius, MinimumAmbientCelsius, MaximumAmbientCelsius);
        }

        public static float AmbientOffset(float ambientCelsius)
        {
            return NormalizeAmbient(ambientCelsius) - VanillaAmbientCelsius;
        }

        public static float CoolerHeatCorrection(float ambientCelsius, float conductance,
            float coolerEffect = 1f)
        {
            if (conductance <= 0f || coolerEffect <= 0f) return 0f;
            return AmbientOffset(ambientCelsius) * conductance * Clamp(coolerEffect, 0f, 1f);
        }

        public static float AdjustFireboxCombustionTarget(float vanillaTarget,
            float ambientCelsius)
        {
            // Intake-air temperature only shifts a fraction of an established
            // coal fire's equilibrium temperature. Retain DV's 500 C combustion
            // floor so cold weather cannot extinguish a valid fire numerically.
            return Math.Max(MinimumFireboxTemperature,
                vanillaTarget + AmbientOffset(ambientCelsius) * FireboxAmbientCoupling);
        }

        public static float AdjustFireboxResponseTime(float vanillaSeconds,
            float ambientCelsius)
        {
            if (vanillaSeconds <= 0f) return vanillaSeconds;
            // Cold iron and cold intake air slow establishment of a fire; hot air
            // does the reverse. Limit the multiplier for compatibility with
            // unusual climates supplied over multiplayer or by other mods.
            var multiplier = 1f + (VanillaAmbientCelsius - NormalizeAmbient(ambientCelsius)) *
                FireboxResponseChangePerDegree;
            return vanillaSeconds * Clamp(multiplier, 0.8f, 1.3f);
        }

        public static float StepBrakeTemperature(float temperature, float absoluteSpeed,
            float brakingFactor, float deltaTime, float ambientCelsius)
        {
            var ambient = NormalizeAmbient(ambientCelsius);
            if (float.IsNaN(temperature) || float.IsInfinity(temperature)) temperature = ambient;
            if (float.IsNaN(deltaTime) || float.IsInfinity(deltaTime)) deltaTime = 0;
            if (float.IsNaN(absoluteSpeed) || float.IsInfinity(absoluteSpeed)) absoluteSpeed = 0;
            if (float.IsNaN(brakingFactor) || float.IsInfinity(brakingFactor)) brakingFactor = 0;
            if (deltaTime <= 0f) return Clamp(temperature, ambient, BrakeMaximumTemperature);

            var heatRate = 0f;
            if (absoluteSpeed > 0f && brakingFactor > 0.001f)
                heatRate += absoluteSpeed * brakingFactor * BrakeTemperatureGainRate;

            if (temperature > ambient)
            {
                // Native DV cools toward one degree below its 25 C lower clamp
                // (24 C). Preserve that one-degree gradient at seasonal ambient.
                heatRate -= (temperature - (ambient - 1f)) * BrakeCoolingRate;
            }

            return Clamp(temperature + heatRate * deltaTime, ambient, BrakeMaximumTemperature);
        }

        public static float BrakeOverheatFraction(float temperature)
        {
            return Clamp((temperature - BrakeOverheatStart) /
                (BrakeOverheatEnd - BrakeOverheatStart), 0f, 1f);
        }

        public static float BrakeForceFactor(float temperature)
        {
            var overheat = BrakeOverheatFraction(temperature);
            return 1f + (BrakeMinimumForceFactor - 1f) * overheat;
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            return value < minimum ? minimum : value > maximum ? maximum : value;
        }
    }
}
