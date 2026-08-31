using System;
using DV.WeatherSystem;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class WeatherAdapter : IDisposable
    {
        private WeatherDriver driver;
        private float nextProbeTime;
        private bool capturedWetness;
        private bool ownedWetnessOverride;
        private bool previousWasOverridden;
        private float previousOverride;
        private bool capturedPrecipitation;
        private bool appliedPrecipitation;
        private Vector2 originalRainRangeStart;
        private Vector2 originalRainRangeMax;
        private Vector2 lastAppliedRainRangeStart;
        private Vector2 lastAppliedRainRangeMax;
        private bool capturedThunder;
        private bool previousThunderWasOverridden;
        private float previousThunderOverride;

        public bool IsReady { get { return driver != null; } }
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
            if (!enabled || state.WinterWetnessEquivalent <= 0.0001f)
            {
                ReleaseWetnessOverride();
                return;
            }
            var wetness = driver.WetnessValue;
            if (!capturedWetness)
            {
                capturedWetness = true;
                previousWasOverridden = wetness.IsOverridden;
                previousOverride = wetness.OverriddenValue;
                if (respectExternalOverride && previousWasOverridden) return;
                ownedWetnessOverride = true;
            }
            if (!ownedWetnessOverride) return;
            wetness.EngageOverride(Mathf.Max(wetness.RealValue, state.WinterWetnessEquivalent));
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

            if (!capturedThunder)
            {
                capturedThunder = true;
                previousThunderWasOverridden = driver.ThunderValue.IsOverridden;
                previousThunderOverride = driver.ThunderValue.OverriddenValue;
            }
            driver.ThunderValue.EngageOverride(0f);
        }

        public void ReleaseWetnessOverride()
        {
            if (driver != null && ownedWetnessOverride)
            {
                if (previousWasOverridden) driver.WetnessValue.EngageOverride(previousOverride);
                else driver.WetnessValue.ClearOverride();
            }
            ownedWetnessOverride = false;
            capturedWetness = false;
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
            if (!capturedThunder) return;
            if (driver == null)
            {
                capturedThunder = false;
                return;
            }
            if (!driver.ThunderValue.IsOverridden ||
                Mathf.Abs(driver.ThunderValue.OverriddenValue) > 0.0001f)
            {
                capturedThunder = false;
                return;
            }
            if (previousThunderWasOverridden)
                driver.ThunderValue.EngageOverride(previousThunderOverride);
            else
                driver.ThunderValue.ClearOverride();
            capturedThunder = false;
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
            ReleaseWetnessOverride();
            ReleaseSeasonalPrecipitation();
            ReleaseThunderOverride();
            driver = null;
            nextProbeTime = 0f;
        }

        public void Dispose() { ResetForSession(); }
    }
}
