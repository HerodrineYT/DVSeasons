using System;

namespace DVSeasons.Core
{
    // Logical coverage only; changing camera distance never changes this state.
    public static class VehicleSideSnowProfile
    {
        public const float StartSpeedKmh = 30f;
        public const float FullSpeedKmh = 70f;
        public const float BuildSeconds = 90f;

        public static float SpeedFactor(float speedKmh)
        {
            if (!Finite(speedKmh)) return 0f;
            var value = Unit((speedKmh - StartSpeedKmh) / (FullSpeedKmh - StartSpeedKmh));
            return value * value * (3f - 2f * value);
        }

        public static float Advance(float amount, float speedKmh, float snowfall,
            float temperatureCelsius, float heating, bool exposed, float seconds, float impact = 1f)
        {
            amount = Unit(amount);
            if (!Finite(seconds) || seconds <= 0f || !Finite(temperatureCelsius) || !Finite(speedKmh)) return amount;
            heating = Unit(heating);
            var cold = Unit(-temperatureCelsius / 2f);
            // Relative airflow already includes vehicle speed. A second speed
            // gate would prevent crosswind from coating parked rolling stock.
            var deposit = exposed ? Unit(snowfall) * cold *
                (Finite(impact) ? Math.Max(0f, Math.Min(2f, impact)) : 0f) *
                (1f - heating * .85f) / BuildSeconds : 0f;
            // Warm engines reduce sticking and gradually remove the layer already
            // present. A cold parked car keeps it, even after the snowfall ends.
            var melt = heating / 45f + Math.Max(0f, temperatureCelsius) / 240f;
            float result;
            if (melt <= 0f) result = amount + deposit * seconds;
            else
            {
                double equilibrium = (double)deposit / melt;
                result = (float)(equilibrium + (amount - equilibrium) * Math.Exp(-(double)melt * seconds));
            }
            // Preserve every positive increment. Resetting small values each
            // quarter-second prevented light snowfall from ever accumulating.
            return Unit(result);
        }

        // Airflow is wind minus vehicle velocity in the car's local frame.
        // Turbulence still dusts lateral panels; the windward face receives
        // substantially more snow and the sheltered leeward face receives less.
        public static float ImpactFactor(float speedKmh, float airX, float airZ, float normalX, float normalZ)
        {
            if (!Finite(speedKmh) || !Finite(airX) || !Finite(airZ)) return 0f;
            float strength = (float)Math.Sqrt((double)airX * airX + (double)airZ * airZ) / 12f;
            if (strength <= 0f) return 0f;
            float normalFlow = airX * normalX + airZ * normalZ;
            float turbulence = .08f * Math.Min(2f, strength) + .4f * SpeedFactor(speedKmh);
            return Math.Min(2f, turbulence * (normalFlow > 0f ? .35f : 1f) + Math.Max(0f, -normalFlow) / 12f);
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        private static float Unit(float value) { return Finite(value) ? Math.Max(0f, Math.Min(1f, value)) : 0f; }
    }
}
