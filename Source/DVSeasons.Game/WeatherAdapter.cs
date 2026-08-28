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

        public bool IsReady { get { return driver != null; } }
        public float RainIntensity { get { return driver == null ? 0f : Mathf.Clamp01(driver.RainValue.CurrentValue); } }

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

        public void ReleaseWetnessOverride()
        {
            if (driver == null || !ownedWetnessOverride) return;
            if (previousWasOverridden) driver.WetnessValue.EngageOverride(previousOverride);
            else driver.WetnessValue.ClearOverride();
            ownedWetnessOverride = false;
            capturedWetness = false;
        }

        public void Dispose() { ReleaseWetnessOverride(); driver = null; }
    }
}
