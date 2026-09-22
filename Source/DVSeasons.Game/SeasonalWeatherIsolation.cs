using System;
using DV;
using DV.WeatherSystem;
using HarmonyLib;

namespace DVSeasons.Mod
{
    // Visual/adhesion overrides must not become the input to native drying or
    // get serialized as user weather-editor overrides in saves/MP snapshots.
    internal sealed class SeasonalWeatherIsolation : IDisposable
    {
        private const string Id = "Herodrine.DVSeasons.WeatherIsolation";
        private readonly Harmony harmony = new Harmony(Id);
        private static WeatherAdapter active;
        private bool installed;

        internal sealed class SuspendedValue
        {
            private readonly OverridableValue<float> target;
            private readonly float value;
            public SuspendedValue(OverridableValue<float> target)
            {
                this.target = target; value = target.OverriddenValue;
                target.ClearOverride();
            }
            public void Restore()
            {
                if (!target.IsOverridden) target.EngageOverride(value);
            }
        }

        private sealed class SnapshotScope
        {
            public SuspendedValue Wetness, Thunder;
            public IDisposable Blizzard;
        }

        public void Enable(WeatherAdapter adapter)
        {
            active = adapter;
            if (installed) return;
            try
            {
                harmony.Patch(AccessTools.Method(typeof(WeatherDriver), "PositionalUpdate"),
                    prefix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(StormSky)));
                harmony.Patch(AccessTools.Method(typeof(WeatherDriver), "UpdateWetnessHours"),
                    prefix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(BeginDrying)),
                    finalizer: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(EndDrying)));
                harmony.Patch(AccessTools.Method(typeof(WeatherDriver), "GetSaveData"),
                    prefix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(BeginSnapshot)),
                    finalizer: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(EndSnapshot)));
                installed = true;
                var provider = typeof(DV.UI.LocoHUD.PhotoModeWeatherSettingsProvider);
                harmony.Patch(AccessTools.Method(provider, "SetWeatherOverride"),
                    prefix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(ManualSet)),
                    postfix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(MenuChanged)));
                harmony.Patch(AccessTools.Method(provider, "ClearWeatherOverride"),
                    prefix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(ManualClear)),
                    postfix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(MenuChanged)));
                harmony.Patch(AccessTools.Method(provider, "SetTime"),
                    prefix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(CanEdit)),
                    postfix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(TimeChanged)));
                harmony.Patch(AccessTools.Method(provider, "IsSliderInteractable"),
                    prefix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(SliderInteractable)));
                var panel = typeof(DV.UI.LocoHUD.PhotoModeWeatherController);
                harmony.Patch(AccessTools.Method(panel, "UpdateInteractable"),
                    prefix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(UpdateClientControls)));
                foreach (var method in new[] { "ResetDefaultSettings", "SliderChanged" })
                    harmony.Patch(AccessTools.Method(panel, method),
                        prefix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(AllowPanelEdit)));
                foreach (var method in new[] { "ToggleOn", "OnEnable", "NotifyOverrideChanged" })
                    harmony.Patch(AccessTools.Method(panel, method),
                        postfix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(RefreshClientControls)));
                harmony.Patch(AccessTools.Method(typeof(WeatherDriver), "LoadSaveData"),
                    postfix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(WeatherLoaded)));
                harmony.Patch(AccessTools.Method(typeof(DV.UI.LocoHUD.HUDHint), "HUDHoverManagerOnHoveredChangedAll"),
                    postfix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(WeatherHintChanged)));
            }
            catch { Dispose(); throw; }
        }

        private static void BeginDrying(WeatherDriver __instance, out SuspendedValue __state)
        { __state = active == null ? null : active.SuspendWetness(__instance); }

        private static bool CanEdit() { return active == null || active.CanEditWeather; }

        private static bool ManualSet(DV.UI.LocoHUD.PhotoModeWeatherController.WeatherSettingType __0)
        {
            if (!CanEdit()) return false;
            if (__0 == DV.UI.LocoHUD.PhotoModeWeatherController.WeatherSettingType.WetnessValue) active?.ManualWetness(true);
            if (__0 == DV.UI.LocoHUD.PhotoModeWeatherController.WeatherSettingType.ThunderValue) active?.ManualThunder(true);
            return true;
        }
        private static bool ManualClear(DV.UI.LocoHUD.PhotoModeWeatherController.WeatherSettingType __0)
        {
            if (!CanEdit()) return false;
            if (__0 == DV.UI.LocoHUD.PhotoModeWeatherController.WeatherSettingType.WetnessValue) active?.ManualWetness(false);
            if (__0 == DV.UI.LocoHUD.PhotoModeWeatherController.WeatherSettingType.ThunderValue) active?.ManualThunder(false);
            return true;
        }

        private static void MenuChanged() { if (CanEdit()) active?.MenuWeatherChanged(false); }
        private static void TimeChanged() { if (CanEdit()) active?.MenuWeatherChanged(true); }
        private static bool SliderInteractable(ref bool __result)
        {
            if (CanEdit()) return true;
            __result = false;
            return false;
        }

        private static bool IsClientPanel(DV.UI.LocoHUD.PhotoModeWeatherController panel)
        { return !CanEdit() && panel.Provider is DV.UI.LocoHUD.PhotoModeWeatherSettingsProvider; }

        private static bool UpdateClientControls(DV.UI.LocoHUD.PhotoModeWeatherController __instance)
        {
            if (!IsClientPanel(__instance)) return true;
            // Native UpdateInteractable resets every disabled override and queues a
            // DelayedRefresh for each one. Client overrides belong to the host, so
            // ManualClear must reject that reset. Calling the native reset here
            // would therefore multiply refresh coroutines on every following frame.
            DisableClientControls(__instance);
            return false;
        }

        private static bool AllowPanelEdit(DV.UI.LocoHUD.PhotoModeWeatherController __instance)
        { return !IsClientPanel(__instance); }

        private static void RefreshClientControls(DV.UI.LocoHUD.PhotoModeWeatherController __instance)
        {
            // These native callbacks can re-enable reset buttons after refreshing
            // the values. Keep the whole client panel read-only, including VR.
            if (IsClientPanel(__instance)) DisableClientControls(__instance);
        }

        private static void DisableClientControls(DV.UI.LocoHUD.PhotoModeWeatherController panel)
        {
            if (panel.sliders == null) return;
            foreach (var slider in panel.sliders)
            {
                if (slider == null) continue;
                if (slider.slider != null) slider.slider.ToggleInteractable(false);
                if (slider.clearButton != null) slider.clearButton.ToggleInteractable(false);
            }
        }
        private static void WeatherLoaded(WeatherDriver __instance) { active?.NativeWeatherLoaded(__instance); }
        private static void WeatherHintChanged(DV.UI.LocoHUD.HUDHint __instance, DV.UI.LocoHUD.LocoHUDControlBase __0)
        { WeatherEditorHintLayout.HoverChanged(__instance, __0); }

        private static void EndDrying(SuspendedValue __state) { __state?.Restore(); }

        private static void BeginSnapshot(WeatherDriver __instance, out SnapshotScope __state)
        {
            __state = new SnapshotScope();
            if (active == null) return;
            __state.Blizzard = active.SuspendBlizzard();
            __state.Wetness = active.SuspendWetness(__instance);
            __state.Thunder = active.SuspendThunder(__instance);
        }

        private static void EndSnapshot(SnapshotScope __state)
        {
            if (__state == null) return;
            __state.Thunder?.Restore(); __state.Wetness?.Restore();
            __state.Blizzard?.Dispose();
        }

        private static void StormSky(WeatherDriver __instance) { active?.ApplyBlizzardSky(__instance); }

        public void Dispose()
        {
            WeatherEditorHintLayout.RestoreAll();
            harmony.UnpatchAll(Id); installed = false; active = null;
        }
    }
}
