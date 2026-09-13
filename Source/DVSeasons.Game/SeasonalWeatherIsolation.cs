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
        }

        public void Enable(WeatherAdapter adapter)
        {
            active = adapter;
            if (installed) return;
            try
            {
                harmony.Patch(AccessTools.Method(typeof(WeatherDriver), "UpdateWetnessHours"),
                    prefix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(BeginDrying)),
                    finalizer: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(EndDrying)));
                harmony.Patch(AccessTools.Method(typeof(WeatherDriver), "GetSaveData"),
                    prefix: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(BeginSnapshot)),
                    finalizer: new HarmonyMethod(typeof(SeasonalWeatherIsolation), nameof(EndSnapshot)));
                installed = true;
            }
            catch { Dispose(); throw; }
        }

        private static void BeginDrying(WeatherDriver __instance, out SuspendedValue __state)
        { __state = active == null ? null : active.SuspendWetness(__instance); }

        private static void EndDrying(SuspendedValue __state) { __state?.Restore(); }

        private static void BeginSnapshot(WeatherDriver __instance, out SnapshotScope __state)
        {
            __state = new SnapshotScope();
            if (active == null) return;
            __state.Wetness = active.SuspendWetness(__instance);
            __state.Thunder = active.SuspendThunder(__instance);
        }

        private static void EndSnapshot(SnapshotScope __state)
        {
            if (__state == null) return;
            __state.Thunder?.Restore(); __state.Wetness?.Restore();
        }

        public void Dispose()
        {
            harmony.UnpatchAll(Id); installed = false; active = null;
        }
    }
}
