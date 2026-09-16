using System;
using DV.WeatherSystem;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed partial class WeatherAdapter : IDisposable
    {
        private readonly SeasonalClimateController climate = new SeasonalClimateController();
        private readonly SeasonalWeatherIsolation isolation = new SeasonalWeatherIsolation();

        public void ApplySeasonalClimate(SeasonState state, bool daylight, bool weather)
        {
            TickProbe();
            climate.Apply(driver, state, daylight, weather);
        }

        private WeatherDriver driver;
        private float nextProbeTime;
        private readonly WetnessOverrideOwnership wetnessOwnership=new WetnessOverrideOwnership();
        private readonly WeatherOverrideOwnership thunderOwnership = new WeatherOverrideOwnership();
        private int adhesionStatus=-1;
        private bool manualWetness;
        private bool manualThunder;
        internal void ManualWetness(bool enabled)
        {
            ReleaseWetnessOverride();
            manualWetness = enabled;
        }
        internal void ManualThunder(bool enabled)
        {
            ReleaseThunderOverride();
            manualThunder = enabled;
        }
        private bool capturedPrecipitation;
        private bool appliedPrecipitation;
        private Vector2 originalRainRangeStart;
        private Vector2 originalRainRangeMax;
        private Vector2 lastAppliedRainRangeStart;
        private Vector2 lastAppliedRainRangeMax;

        public bool IsReady { get { return driver != null; } }
        public SeasonState WithAirTemperature(SeasonState state)
        {
            DateTime date;
            if (state == null || driver == null || !TryGetGameDateTime(out date)) return state;
            var snapshot = driver.CurrentChungusState.currentLow;
            var clouds = Mathf.Clamp01(Mathf.Max(snapshot.OverallFogginess, snapshot.cloudCoverage * snapshot.cloudOpacity));
            float temperature = AirTemperatureProfile.Evaluate(state, date.Ticks / (double)TimeSpan.TicksPerDay,
                driver.TimeOfDayHours.CurrentValue, clouds, RainIntensity,
                driver.WindSpeed.CurrentValue / 7f, driver.ThunderValue.CurrentValue);
            return new SeasonState(state.Phase, state.Current, state.Next, state.Transition,
                state.SnowAmount, temperature, state.WinterWetnessEquivalent);
        }
        public float RainIntensity { get { return driver == null ? 0f : Mathf.Clamp01(driver.RainValue.CurrentValue); } }
        public float SnowLightFactor
        {
            get { return driver == null ? 1f : Mathf.Clamp01(driver.GlobalSunIntensityFactor); }
        }
        public Vector3 SnowWindVelocity
        {
            get
            {
                if (driver == null) return Vector3.zero;
                var degrees = driver.WindDirection.CurrentValue * Mathf.Deg2Rad;
                var speed = Mathf.Clamp(driver.WindSpeed.CurrentValue, 0f, 7f) * 2.1f;
                return new Vector3(-Mathf.Sin(degrees), 0f, -Mathf.Cos(degrees)) * speed;
            }
        }

        public void TickProbe()
        {
            if (driver != null || Time.realtimeSinceStartup < nextProbeTime) return;
            nextProbeTime = Time.realtimeSinceStartup + 2f;
            driver = UnityEngine.Object.FindObjectOfType<WeatherDriver>();
            if (driver != null) isolation.Enable(this);
        }

        internal SeasonalWeatherIsolation.SuspendedValue SuspendWetness(WeatherDriver target)
        {
            if (target != driver || !wetnessOwnership.Owns(target.WetnessValue.IsOverridden,
                target.WetnessValue.OverriddenValue)) return null;
            return new SeasonalWeatherIsolation.SuspendedValue(target.WetnessValue);
        }

        internal SeasonalWeatherIsolation.SuspendedValue SuspendThunder(WeatherDriver target)
        {
            if (target != driver || !thunderOwnership.Owns(target.ThunderValue.IsOverridden,
                target.ThunderValue.OverriddenValue)) return null;
            return new SeasonalWeatherIsolation.SuspendedValue(target.ThunderValue);
        }

        public void ResetSeasonEffects()
        {
            TickProbe();
            ReleaseWetnessOverride(); ReleaseThunderOverride(); ReleaseSeasonalPrecipitation();
            if (driver == null) return;
            // Season selection also cancels stale weather-editor overrides (and
            // ones restored from an older save), before applying the new season.
            driver.WetnessValue.ClearOverride();
            driver.ThunderValue.ClearOverride();
            float normalDay = DV.Globals.G.GameParams.DayLengthInMinutes;
            var day = driver.DayLengthInMinutes;
            Debug.Log("[DVSeasons] Season change: reset wetness/thunder; weather day " +
                day.CurrentValue + " min (override=" + day.IsOverridden + ") -> " + normalDay + " min.");
            day.ClearOverride();
            day.RealValue = normalDay;
        }

        public void RefreshSeasonWeather()
        {
            if (driver == null) return;
            // Reconstruct actual rain wetness using the new season, rather than
            // preserving a winter value fed back by versions without isolation.
            // Calling only the driver's calculation does not jump the clock or
            // notify fauna/jobs/other time listeners.
            HarmonyLib.AccessTools.Method(typeof(WeatherDriver), "OnTimeJump").Invoke(driver, null);
        }

        public bool TryGetGameDateTime(out DateTime dateTime)
        {
            TickProbe();
            if (driver != null && driver.manager != null)
            {
                dateTime = driver.manager.DateTime;
                return true;
            }
            dateTime = default(DateTime);
            return false;
        }

        public void ApplyWinterAdhesion(SeasonState state, bool enabled, bool respectExternalOverride)
        {
            TickProbe();
            if (driver == null) return;
            if (!enabled || state == null || state.WinterWetnessEquivalent <= 0.0001f)
            {
                ReleaseWetnessOverride();
                return;
            }
            var wetness = driver.WetnessValue;
            if (manualWetness && wetness.IsOverridden) return;
            manualWetness = false;
            if(!wetnessOwnership.Acquire(wetness.IsOverridden,wetness.OverriddenValue,respectExternalOverride))
            {
                if(adhesionStatus!=1) Debug.Log("[DVSeasons] Seasonal adhesion is waiting for an external wetness override to end. Wetness="+wetness.CurrentValue);
                adhesionStatus=1;return;
            }
            float value=Mathf.Max(wetness.RealValue,state.WinterWetnessEquivalent);
            wetness.EngageOverride(value);
            wetnessOwnership.Applied(value);
            if(adhesionStatus!=2) Debug.Log("[DVSeasons] Seasonal adhesion active. Native wetness="+wetness.CurrentValue);
            adhesionStatus=2;
        }

        public void ApplySeasonalPrecipitation(SeasonState state, bool enabled)
        {
            TickProbe();
            if (driver == null || state == null) return;
            if (!enabled)
            {
                ReleaseSeasonalPrecipitation();
                return;
            }

            CapturePrecipitationSettings();
            if (appliedPrecipitation &&
                (!Approximately(driver.rainRangeStart, lastAppliedRainRangeStart) ||
                 !Approximately(driver.rainRangeMax, lastAppliedRainRangeMax)))
            {
                // Another mod changed the profile after us. Treat its values as the
                // new neutral baseline instead of fighting it or accumulating offsets.
                originalRainRangeStart = driver.rainRangeStart;
                originalRainRangeMax = driver.rainRangeMax;
            }

            var profile = SeasonalPrecipitationProfile.FromState(state);
            var start = Offset(originalRainRangeStart, profile.StartThresholdOffset);
            var maximum = Offset(originalRainRangeMax, profile.MaximumThresholdOffset);
            start.x = Mathf.Min(start.x, maximum.x - 0.05f);
            start.y = Mathf.Min(start.y, maximum.y - 0.05f);
            lastAppliedRainRangeStart = start;
            lastAppliedRainRangeMax = maximum;
            driver.rainRangeStart = start;
            driver.rainRangeMax = maximum;
            appliedPrecipitation = true;
        }

        public void ApplyWinterThunderSuppression(SeasonState state, bool enabled)
        {
            TickProbe();
            if (driver == null || state == null) return;
            if (!enabled || state.SnowAmount <= 0.001f)
            {
                ReleaseThunderOverride();
                return;
            }

            var thunder = driver.ThunderValue;
            if (manualThunder && thunder.IsOverridden) return;
            manualThunder = false;
            thunderOwnership.Acquire(thunder.IsOverridden, thunder.OverriddenValue, false);
            thunder.EngageOverride(0f);
            thunderOwnership.Applied(0f);
        }

        public void ReleaseWetnessOverride()
        {
            bool overridden;float value;
            if (driver != null && wetnessOwnership.Release(driver.WetnessValue.IsOverridden,
                driver.WetnessValue.OverriddenValue,out overridden,out value))
            {
                if (overridden) driver.WetnessValue.EngageOverride(value);
                else driver.WetnessValue.ClearOverride();
            }
            wetnessOwnership.Reset();
            adhesionStatus=-1;
        }

        public void ReleaseSeasonalPrecipitation()
        {
            if (driver != null && capturedPrecipitation && appliedPrecipitation &&
                Approximately(driver.rainRangeStart, lastAppliedRainRangeStart) &&
                Approximately(driver.rainRangeMax, lastAppliedRainRangeMax))
            {
                driver.rainRangeStart = originalRainRangeStart;
                driver.rainRangeMax = originalRainRangeMax;
            }
            capturedPrecipitation = false;
            appliedPrecipitation = false;
        }

        public void ReleaseThunderOverride()
        {
            bool overridden;
            float value;
            if (driver != null && thunderOwnership.Release(driver.ThunderValue.IsOverridden,
                driver.ThunderValue.OverriddenValue, out overridden, out value))
            {
                if (overridden) driver.ThunderValue.EngageOverride(value);
                else driver.ThunderValue.ClearOverride();
            }
            thunderOwnership.Reset();
        }

        private void CapturePrecipitationSettings()
        {
            if (capturedPrecipitation) return;
            originalRainRangeStart = driver.rainRangeStart;
            originalRainRangeMax = driver.rainRangeMax;
            capturedPrecipitation = true;
            appliedPrecipitation = false;
        }

        private static Vector2 Offset(Vector2 value, float offset)
        {
            return new Vector2(
                Mathf.Clamp(value.x + offset, 0.05f, 0.95f),
                Mathf.Clamp(value.y + offset, 0.05f, 0.95f));
        }

        private static bool Approximately(Vector2 a, Vector2 b)
        {
            return Mathf.Abs(a.x - b.x) < 0.0001f && Mathf.Abs(a.y - b.y) < 0.0001f;
        }

        public void ResetForSession()
        {
            ResetNetworkWeather();
            climate.Reset();
            ReleaseWetnessOverride();
            ReleaseSeasonalPrecipitation();
            ReleaseThunderOverride();
            isolation.Dispose();
            driver = null;
            nextProbeTime = 0f;
        }

        public void Dispose() { ResetForSession(); }
    }
}
