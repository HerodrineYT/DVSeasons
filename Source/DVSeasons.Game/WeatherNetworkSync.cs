using System;
using DV;
using DV.WeatherSystem;
using DVSeasons.Core;

namespace DVSeasons.Mod
{
    internal sealed partial class WeatherAdapter
    {
        internal Func<bool> WeatherAuthority;
        internal Action WeatherEdited;
        internal Action NetworkWeatherRestored;
        private WeatherNetworkState networkWeather;
        private bool networkWeatherDirty;
        private uint timeRevision;
        private uint? appliedTimeRevision;

        internal bool CanEditWeather { get { return WeatherAuthority == null || WeatherAuthority(); } }
        internal WeatherNetworkState NetworkWeather { get { return !CanEditWeather ? networkWeather : null; } }

        internal void MenuWeatherChanged(bool timeChanged)
        {
            if (!CanEditWeather) return;
            if (timeChanged) timeRevision++;
            WeatherEdited?.Invoke();
        }

        // The order is the protocol's fixed order, independent of the game's UI enum.
        private OverridableValue<float> WeatherSlot(int index)
        {
            switch (index)
            {
                case 0: return driver.RainValue;
                case 1: return driver.ThunderValue;
                case 2: return driver.WetnessValue;
                case 3: return driver.WindSpeed;
                case 4: return driver.WindDirection;
                case 5: return driver.WeatherPointX;
                case 6: return driver.WeatherPointY;
                case 7: return driver.TimeOfDayHours;
                case 8: return driver.DayLengthInMinutes;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }

        internal WeatherNetworkState CaptureNetworkWeather(SeasonModSettings settings)
        {
            if (driver == null || driver.manager == null || driver.manager.todSky == null)
                return new WeatherNetworkState();
            var state = new WeatherNetworkState
            {
                Available = true,
                RealDateTimeTicks = driver.manager.RealDateTime.Ticks,
                TimeRevision = timeRevision,
                BaseDayLengthInMinutes = driver.DayLengthInMinutes.RealValue,
                SeasonalDaylight = settings.SeasonalDaylightEnabled,
                SeasonalPrecipitation = settings.SeasonalPrecipitationEnabled,
                WinterAdhesion = settings.WinterAdhesionEnabled,
                DisableWinterThunder = settings.DisableWinterThunder,
                RespectExternalWetnessOverride = settings.RespectExternalWetnessOverride
            };
            var blizzard = SuspendBlizzard();
            var wetness = SuspendWetness(driver);
            var thunder = SuspendThunder(driver);
            try
            {
                for (int i = 0; i < WeatherNetworkState.ValueCount; i++)
                {
                    var slot = WeatherSlot(i);
                    if (!slot.IsOverridden) continue;
                    state.Overrides |= (ushort)(1 << i);
                    state.Values[i] = slot.OverriddenValue;
                }
            }
            finally { thunder?.Restore(); wetness?.Restore(); blizzard?.Dispose(); }
            return state;
        }

        internal void ReceiveNetworkWeather(WeatherNetworkState state)
        {
            if (CanEditWeather || state == null || !state.Available || !state.IsValid()) return;
            // Time advances in every heartbeat. Only a deliberate time edit (or
            // first synchronization) should jump the client's clock.
            bool changed = networkWeather == null || state.TimeRevision != networkWeather.TimeRevision ||
                state.Overrides != networkWeather.Overrides ||
                state.BaseDayLengthInMinutes != networkWeather.BaseDayLengthInMinutes ||
                state.SeasonalDaylight != networkWeather.SeasonalDaylight ||
                state.SeasonalPrecipitation != networkWeather.SeasonalPrecipitation ||
                state.WinterAdhesion != networkWeather.WinterAdhesion ||
                state.DisableWinterThunder != networkWeather.DisableWinterThunder ||
                state.RespectExternalWetnessOverride != networkWeather.RespectExternalWetnessOverride;
            if (!changed)
                for (int i = 0; i < WeatherNetworkState.ValueCount; i++)
                    if (state.Values[i] != networkWeather.Values[i]) { changed = true; break; }
            networkWeather = state.Clone();
            networkWeatherDirty |= changed;
        }

        internal void ApplyNetworkWeather(bool force = false)
        {
            if (CanEditWeather || networkWeather == null || !networkWeather.Available ||
                (!force && !networkWeatherDirty) || driver == null || driver.manager == null ||
                driver.manager.todSky == null) return;
            // Release our own modifiers before replacing the manual input. They
            // are reapplied by the runtime with the host's gameplay settings.
            ApplyBlizzard(false);
            ReleaseWetnessOverride();
            ReleaseThunderOverride();
            bool refresh = false;
            for (int i = 0; i < WeatherNetworkState.ValueCount; i++)
            {
                var slot = WeatherSlot(i);
                bool enabled = (networkWeather.Overrides & (1 << i)) != 0;
                bool changed = slot.IsOverridden != enabled ||
                    (enabled && slot.OverriddenValue != networkWeather.Values[i]);
                if (!changed) continue;
                if (enabled) slot.EngageOverride(networkWeather.Values[i]);
                else slot.ClearOverride();
                // WeatherDriver simulates cloud/rain/wind every frame. Do not
                // notify jobs/fauna of a time jump for a cloud edit, or for the
                // native MP packet merely clearing and restoring the same hour.
                if (i == 7 && !force) refresh = true;
            }
            manualWetness = (networkWeather.Overrides & (1 << 2)) != 0;
            manualThunder = (networkWeather.Overrides & (1 << 1)) != 0;
            driver.DayLengthInMinutes.RealValue = networkWeather.BaseDayLengthInMinutes;
            if (appliedTimeRevision != networkWeather.TimeRevision)
            {
                driver.manager.todSky.Cycle.RealDateTime = new DateTime(networkWeather.RealDateTimeTicks);
                appliedTimeRevision = networkWeather.TimeRevision;
                refresh = true;
            }
            networkWeatherDirty = false;
            if (refresh) driver.manager.RefreshTimeOfDay();
        }

        internal void NativeWeatherLoaded(WeatherDriver target)
        {
            if (target != driver || CanEditWeather || networkWeather == null) return;
            // MP 0.1.16's periodic native packet omits Overrides, so LoadSaveData
            // clears them. Restore in the same call, before the next game frame.
            ApplyNetworkWeather(true);
            NetworkWeatherRestored?.Invoke();
        }

        private void ResetNetworkWeather()
        {
            networkWeather = null;
            networkWeatherDirty = false;
            appliedTimeRevision = null;
            timeRevision = 0;
            manualWetness = manualThunder = false;
        }
    }
}
