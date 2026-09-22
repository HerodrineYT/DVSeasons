using System;

namespace DVSeasons.Core
{
    public enum WinterGlassStage { Frozen, Thawing, Fogged, Wet }

    // Per-car air and heater inertia, with a separate glass temperature. All
    // time constants are seconds; engine, heater and cabin are not one switch.
    public sealed class WindowWinterClimate
    {
        public const float ClearGlassTemperature = 5f;
        public const float IceFadeTemperature = 2f;
        public float EngineWarmth { get; private set; }
        public float HeaterTemperature { get; private set; }
        public float CabinTemperature { get; private set; }
        public float GlassTemperature { get; private set; }
        public float Frost { get; private set; }
        // Coverage controls where crystals remain; visibility controls their
        // final transparency. Do not shrink the pattern a second time as it warms.
        public float IceVisibility { get { return 1 - SmoothRange(IceFadeTemperature, ClearGlassTemperature, GlassTemperature); } }
        public float Fog { get; private set; }
        public WinterGlassStage Stage { get; private set; }
        private bool initialized;
        public bool IsInitialized { get { return initialized; } }

        public void Advance(float seconds, float outside, bool running, float engineTemperature,
            float heater, bool openings, float winterCoverage)
        { AdvanceCore(seconds, outside, running, engineTemperature, heater, openings, winterCoverage, false); }

        // Direct engine heat has already been limited by coolant/engine warmup.
        // Do not put it through another slow electrical-heater warmup sequence.
        public void AdvanceEngineHeated(float seconds, float outside, bool running, float engineTemperature,
            float heat, bool openings, float winterCoverage)
        { AdvanceCore(seconds, outside, running, engineTemperature,
            (float)Math.Sqrt(Clamp(Finite(heat, 0))), openings, winterCoverage, true); }

        // A catenary-fed resistance heater does not wait for a diesel engine to
        // warm up, and traction motor heat must not keep feeding it after a trip.
        // Heater, cabin and glass temperatures retain their own thermal inertia.
        public void AdvanceElectricHeated(float seconds, float outside, float power,
            bool openings, float winterCoverage)
        {
            EngineWarmth = 0;
            AdvanceCore(seconds, outside, false, 20f,
                (float)Math.Sqrt(Clamp(Finite(power, 0))), openings, winterCoverage, true);
        }

        private void AdvanceCore(float seconds, float outside, bool running, float engineTemperature,
            float heater, bool openings, float winterCoverage, bool directEngineHeat)
        {
            outside = Finite(outside, 0);
            heater = Clamp(Finite(heater, 0));
            winterCoverage = Clamp(Finite(winterCoverage, 0));
            if (!initialized)
            {
                initialized = true;
                CabinTemperature = GlassTemperature = HeaterTemperature = outside;
                Frost = ColdCoverage(outside, winterCoverage);
            }
            seconds = Math.Max(0, Math.Min(5, Finite(seconds, 0)));
            EngineWarmth = Approach(EngineWarmth, running ? 1 : 0, seconds, running ? 85 : 240);
            float engineHeat = Clamp((Finite(engineTemperature, outside) - 20) / 60);
            engineHeat = Math.Max(engineHeat, EngineWarmth);
            // A powered heater starts warming before the engine and cabin finish
            // heating. Opening the cab increases exchange with outdoor air.
            float heaterTarget = outside + heater * (Math.Max(35, outside + 42) - outside);
            HeaterTemperature = Approach(HeaterTemperature, heaterTarget, seconds, heater > 0 ? (directEngineHeat ? 6 : 18) : 65);
            float cabTarget = outside + heater * Math.Max(0, HeaterTemperature - outside) * .72f
                + engineHeat * (heater > 0 ? 10 : 2);
            // Keep heater power unchanged and add outdoor air exchange. Speeding
            // up the entire heating curve with an open door could warm a cold
            // cabin faster than with the door closed.
            const float sealedExchange = 1f / 65f;
            float ventilation = openings ? 1f / 12f : 0;
            float exchange = sealedExchange + ventilation;
            cabTarget = (cabTarget * sealedExchange + outside * ventilation) / exchange;
            // Faster heat delivery in a closed cab; retain outdoor exchange and
            // the original cooldown so opening a door still defeats the heater.
            float cabinSeconds = directEngineHeat && !openings && heater > 0 && cabTarget > CabinTemperature
                ? 22 : 1f / exchange;
            CabinTemperature = Approach(CabinTemperature, cabTarget, seconds, cabinSeconds);
            float glassTarget = outside * .22f + CabinTemperature * .78f
                + heater * Math.Max(0, HeaterTemperature - CabinTemperature) * .18f;
            float glassSeconds = heater > 0 ? 35 : 90;
            if (directEngineHeat && heater > 0 && glassTarget > GlassTemperature) glassSeconds = 18;
            GlassTemperature = Approach(GlassTemperature, glassTarget, seconds, glassSeconds);
            // A warmer season or a heater switch alone cannot melt a cold pane.
            // Frost cannot persist once the outside air is clearly above
            // freezing.  Previously a saved cold pane could carry its crystal
            // coverage into spring for the whole melt time, which made +11 C
            // windows look iced even though the thermal envelope was clear.
            float frostTarget = GlassTemperature <= 0 && outside < 1f
                ? Math.Max(Frost, ColdCoverage(GlassTemperature, winterCoverage)) : 0;
            Frost = Approach(Frost, frostTarget, seconds, frostTarget > Frost ? 100 : 28);
            // By this point the renderer's thermal opacity is already zero.
            // Until then preserve the actual crystal coverage as it melts.
            if (outside >= 1f || GlassTemperature >= ClearGlassTemperature || Frost < .0001f) Frost = 0;
            float fogTarget = GlassTemperature > -2 && GlassTemperature < 12 &&
                CabinTemperature > GlassTemperature + 1 ? (1 - Frost) * .65f : 0;
            Fog = Approach(Fog, fogTarget, seconds, fogTarget > Fog ? 14 : 35);
            Fog = Math.Min(Fog, .65f * (1 - SmoothRange(8, 12, GlassTemperature)));
            if (Fog < .0001f) Fog = 0;
            Stage = Frost > .1f ? (GlassTemperature > 0 ? WinterGlassStage.Thawing : WinterGlassStage.Frozen)
                : Fog > .07f ? WinterGlassStage.Fogged : WinterGlassStage.Wet;
        }
        private static float ColdCoverage(float temperature, float coverage)
        { return Clamp(-temperature / 12f) * Math.Max(.25f, coverage); }

        public WindowClimateState Capture()
        { return new WindowClimateState { Initialized=initialized, EngineWarmth=EngineWarmth,
            Heater=HeaterTemperature, Cabin=CabinTemperature, Glass=GlassTemperature, Frost=Frost, Fog=Fog }; }

        public void Restore(WindowClimateState state)
        {
            if (state == null || !state.IsValid()) return;
            initialized=state.Initialized; EngineWarmth=state.EngineWarmth;
            HeaterTemperature=state.Heater; CabinTemperature=state.Cabin;
            GlassTemperature=state.Glass; Frost=state.Frost; Fog=state.Fog;
            Stage = Frost > .1f ? (GlassTemperature > 0 ? WinterGlassStage.Thawing : WinterGlassStage.Frozen)
                : Fog > .07f ? WinterGlassStage.Fogged : WinterGlassStage.Wet;
        }
        private static float Clamp(float v) { return Math.Max(0, Math.Min(1, v)); }
        private static float SmoothRange(float low, float high, float value)
        { float t=Clamp((value-low)/(high-low)); return t*t*(3-2*t); }
        private static float Finite(float v, float fallback) { return float.IsNaN(v) || float.IsInfinity(v) ? fallback : v; }
        private static float Approach(float v, float target, float dt, float tau)
        { return v + (target - v) * (1 - (float)Math.Exp(-dt / tau)); }
    }

    [Serializable]
    public sealed class WindowClimateState
    {
        public bool Initialized;
        public float EngineWarmth, Heater, Cabin, Glass, Frost, Fog;
        public bool IsValid()
        {
            return Unit(EngineWarmth) && Unit(Frost) && Unit(Fog) &&
                Temperature(Heater) && Temperature(Cabin) && Temperature(Glass);
        }
        private static bool Unit(float v) { return v >= 0 && v <= 1; }
        private static bool Temperature(float v) { return v >= -60 && v <= 150; }
    }
}
