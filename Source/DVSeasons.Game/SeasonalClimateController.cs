using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using DV.WeatherSystem;
using DVSeasons.Core;
using HarmonyLib;
using UnityEngine;

namespace DVSeasons.Mod
{
    // Patches calculations, not the clock or persistent weather overrides.
    internal sealed class SeasonalClimateController : IDisposable
    {
        private const string Id = "Herodrine.DVSeasons.Climate";
        private static SeasonalClimateController active;
        private readonly Harmony harmony = new Harmony(Id);
        private WeatherDriver driver;
        private SeasonState state;
        private bool daylightEnabled, weatherEnabled, installed;
        private static bool celestialScope;
        private static float nativeDawn, nativeDusk, utc;
        private static float seasonalDawn, seasonalDusk;

        public void Apply(WeatherDriver target, SeasonState value, bool daylight, bool weather)
        {
            driver = target; state = value;
            daylightEnabled = daylight; weatherEnabled = weather;
            if (target == null || value == null || (!daylight && !weather)) { Reset(); return; }
            active = this;
            Install();
        }

        private void Install()
        {
            if (installed) return;
            try
            {
                Patch(typeof(TOD_Sky), "UpdateCelestials", "BeginCelestials", null, "EndCelestials");
                Patch(typeof(TOD_Sky), "ComputeSunsetAndSunrise", null, "SunTimes");
                Patch(typeof(TOD_Sky), "ComputeSunPosition", "SolarPosition");
                Patch(typeof(WeatherDriver), "ComputeGlobalSunIntensityFactor", "SunIntensity");
                harmony.Patch(AccessTools.Method(typeof(WeatherDriver), "SimulateWeatherToTime"),
                    transpiler: new HarmonyMethod(typeof(SeasonalClimateController), nameof(WeatherNoise)));
                installed = true;
            }
            catch
            {
                Reset();
                throw;
            }
        }

        private void Patch(Type type, string method, string prefix = null, string postfix = null, string finalizer = null)
        {
            var target = AccessTools.Method(type, method);
            if (target == null) throw new MissingMethodException(type.FullName, method);
            harmony.Patch(target,
                prefix == null ? null : new HarmonyMethod(typeof(SeasonalClimateController), prefix),
                postfix == null ? null : new HarmonyMethod(typeof(SeasonalClimateController), postfix),
                finalizer: finalizer == null ? null : new HarmonyMethod(typeof(SeasonalClimateController), finalizer));
        }

        private static void BeginCelestials(TOD_Sky __instance)
        {
            celestialScope = active != null && active.daylightEnabled && active.driver != null &&
                active.driver.manager != null && active.driver.manager.todSky == __instance;
        }

        private static void EndCelestials() { celestialScope = false; }

        private static void SunTimes(float worldUTC, ref float SunriseTime, ref float SunsetTime)
        {
            if (!celestialScope) return;
            nativeDawn = SunriseTime; nativeDusk = SunsetTime; utc = worldUTC;
            var nativeLength = SeasonalClimateProfile.WrapHour(nativeDusk - nativeDawn);
            var length = SeasonalClimateProfile.DaylightHours(active.state.Phase);
            seasonalDawn = (float)SeasonalClimateProfile.WrapHour(nativeDawn + (nativeLength - length) / 2);
            seasonalDusk = (float)SeasonalClimateProfile.WrapHour(seasonalDawn + length);
            SunriseTime = seasonalDawn; SunsetTime = seasonalDusk;
        }

        private static void SolarPosition(ref float d, ref float hour)
        {
            if (!celestialScope) return;
            var localHour = hour + utc;
            var mapped = (float)SeasonalClimateProfile.MapSolarHour(localHour, nativeDawn, nativeDusk,
                SeasonalClimateProfile.DaylightHours(active.state.Phase));
            // Keep the astronomical fractional date consistent with the visual hour.
            var delta = Mathf.DeltaAngle(localHour * 15, mapped * 15) / 15;
            hour += delta;
            d += delta / 24;
        }

        private static bool SunIntensity(WeatherDriver __instance, float timeOfDay, float fogginess, ref float __result)
        {
            if (active == null || !active.daylightEnabled || active.driver != __instance ||
                __instance.manager == null || __instance.manager.todSky == null) return true;
            var sky = __instance.manager.todSky;
            var length = (float)SeasonalClimateProfile.WrapHour(sky.SunsetTime - sky.SunriseTime);
            if (length < 1) return true;
            var elapsed = (float)SeasonalClimateProfile.WrapHour(timeOfDay * 24 - sky.SunriseTime);
            __result = elapsed >= length ? 0 : Mathf.Sin(elapsed / length * Mathf.PI) * (1 - fogginess * 0.7f);
            return false;
        }

        private static IEnumerable<CodeInstruction> WeatherNoise(IEnumerable<CodeInstruction> instructions)
        {
            var result = new List<CodeInstruction>();
            var noise = AccessTools.Method(typeof(Mathf), nameof(Mathf.PerlinNoise));
            var adjust = AccessTools.Method(typeof(SeasonalClimateController), nameof(AdjustNoise));
            var count = 0;
            foreach (var instruction in instructions)
            {
                result.Add(instruction);
                if (!instruction.Calls(noise)) continue;
                if (count < 2)
                {
                    // DV99's first two samples select fog/cloud presets BEFORE
                    // rain, wind and snapshots are derived. Override branches skip them.
                    result.Add(new CodeInstruction(OpCodes.Ldc_I4, count));
                    result.Add(new CodeInstruction(OpCodes.Ldarg_0));
                    result.Add(new CodeInstruction(OpCodes.Call, adjust));
                }
                count++;
            }
            if (count != 5) throw new InvalidOperationException("Unsupported WeatherDriver noise layout: " + count);
            return result;
        }

        private static float AdjustNoise(float value, int channel, WeatherDriver target)
        {
            if (active == null || !active.weatherEnabled || active.driver != target) return value;
            return Mathf.Clamp01(value + SeasonalClimateProfile.NoiseOffset(active.state, channel == 1));
        }

        public void Reset()
        {
            harmony.UnpatchAll(Id);
            installed = false;
            if (active == this) { active = null; celestialScope = false; }
            driver = null; state = null;
        }

        public void Dispose() { Reset(); }
    }
}
