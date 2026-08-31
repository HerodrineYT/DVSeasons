using System;
using System.Reflection;
using DV.Audio;
using DV.ModularAudioCar;
using DV.Rain;
using HarmonyLib;
using PlaceholderSoftware.WetStuff;
using PlaceholderSoftware.WetStuff.Weather;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class WinterRainAudioController : IDisposable
    {
        private const string HarmonyId = "Herodrine.DVSeasons.WinterRainAudio";
        private readonly Harmony harmony;
        private bool disposed;
        private bool surfaceEffectsCleared;

        internal static bool SuppressRainAudio { get; private set; }
        internal static bool SnowReplacesRain { get; private set; }

        public WinterRainAudioController()
        {
            harmony = new Harmony(HarmonyId);
            harmony.PatchAll(typeof(WinterRainAudioController).Assembly);
        }

        public void Apply(float snowAmount, bool replaceRainWithSnow, bool muteRainAudio)
        {
            SnowReplacesRain = replaceRainWithSnow && snowAmount > 0.001f;
            // Keep suppression active for the entire snowy part of the season. If it
            // depended on the current rain sample, a vanilla smoothed loop could leak
            // through for a frame at the beginning or end of a weather episode.
            SuppressRainAudio = SnowReplacesRain && muteRainAudio;
            if (SnowReplacesRain && !surfaceEffectsCleared)
            {
                surfaceEffectsCleared = true;
                ClearSurfaceDropEffects();
            }
            else if (!SnowReplacesRain)
            {
                surfaceEffectsCleared = false;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            SuppressRainAudio = false;
            SnowReplacesRain = false;
            harmony.UnpatchAll(HarmonyId);
            disposed = true;
        }

        private static void ClearSurfaceDropEffects()
        {
            var splatters = UnityEngine.Object.FindObjectsOfType<ParticleWetSplatter>();
            for (var i = 0; i < splatters.Length; i++)
                if (splatters[i] != null) splatters[i].Clear();
        }

        [HarmonyPatch(typeof(RainParticles), "Update")]
        private static class NativeRainParticlesPatch
        {
            private static bool Prefix(RainParticles __instance)
            {
                if (!SnowReplacesRain || __instance == null || __instance.particleSystems == null)
                    return true;
                for (var i = 0; i < __instance.particleSystems.Length; i++)
                {
                    var system = __instance.particleSystems[i].system;
                    if (system == null) continue;
                    var emission = system.emission;
                    emission.rateOverTimeMultiplier = 0f;
                    if (!system.isStopped)
                        system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                // Avoid vanilla restarting the cleared emitters later in this Update.
                return false;
            }
        }

        [HarmonyPatch(typeof(ParticleWetSplatter), "OnParticleCollision")]
        private static class SurfaceSplatterPatch
        {
            private static bool Prefix() { return !SnowReplacesRain; }
        }

        [HarmonyPatch(typeof(DripLine), "Update")]
        private static class SurfaceDripLinePatch
        {
            private static readonly FieldInfo ParticlesField = AccessTools.Field(typeof(DripLine), "_particles");

            private static bool Prefix(DripLine __instance)
            {
                if (!SnowReplacesRain || __instance == null) return true;
                var particles = ParticlesField == null ? null : ParticlesField.GetValue(__instance) as ParticleSystem;
                if (particles != null && !particles.isStopped)
                    particles.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                return false;
            }
        }

        [HarmonyPatch(typeof(RainRipples), "UpdateShaderVariables")]
        private static class SurfaceRainRipplesPatch
        {
            private struct RippleState
            {
                public bool Suppressed;
                public float RainAmount;
                public float RippleMultiplier;
                public float FlowMultiplier;
            }

            private static void Prefix(out RippleState __state)
            {
                __state = new RippleState();
                if (!SnowReplacesRain) return;
                __state.Suppressed = true;
                __state.RainAmount = RainRipples.rainAmount;
                __state.RippleMultiplier = RainRipples.rippleMultiplier;
                __state.FlowMultiplier = RainRipples.flowMultiplier;
                RainRipples.rainAmount = 0f;
                RainRipples.rippleMultiplier = 0f;
                RainRipples.flowMultiplier = 0f;
            }

            private static void Postfix(RippleState __state)
            {
                if (!__state.Suppressed) return;
                RainRipples.rainAmount = __state.RainAmount;
                RainRipples.rippleMultiplier = __state.RippleMultiplier;
                RainRipples.flowMultiplier = __state.FlowMultiplier;
            }
        }

        [HarmonyPatch(typeof(EnvironmentRainManager), "Update")]
        private static class EnvironmentRainPatch
        {
            private static bool Prefix(EnvironmentRainManager __instance)
            {
                if (!SuppressRainAudio) return true;
                Silence(__instance.rainSource);
                Silence(__instance.depotSource);
                return false;
            }

            private static void Silence(AudioSource source)
            {
                if (source == null) return;
                source.volume = 0f;
                source.enabled = false;
            }
        }

        [HarmonyPatch(typeof(EnvironmentSoundSystem), "LateUpdate")]
        private static class WetBiomeAmbiencePatch
        {
            private static readonly FieldInfo AmbianceLoopsField =
                AccessTools.Field(typeof(EnvironmentSoundSystem), "ambianceLoops");
            private static readonly FieldInfo SingleLoopBiomeField =
                AccessTools.Field(typeof(EnvironmentSoundSystem), "singleLoopBiome");

            private static void Postfix(EnvironmentSoundSystem __instance)
            {
                if (!SuppressRainAudio || __instance == null || AmbianceLoopsField == null ||
                    SingleLoopBiomeField == null) return;
                var loops = AmbianceLoopsField.GetValue(__instance) as AudioSource[][];
                var singleLoop = SingleLoopBiomeField.GetValue(__instance) as bool[];
                if (loops == null || singleLoop == null) return;

                for (var biome = 0; biome < loops.Length && biome < singleLoop.Length; biome++)
                {
                    // Four-source biomes use: day dry, day wet, night dry, night wet.
                    // Only the two wet variants contain the remaining rain ambience.
                    if (singleLoop[biome] || loops[biome] == null || loops[biome].Length < 4)
                        continue;
                    SilenceWetLoop(loops[biome][1]);
                    SilenceWetLoop(loops[biome][3]);
                }
            }

            private static void SilenceWetLoop(AudioSource source)
            {
                if (source != null) source.volume = 0f;
            }
        }

        [HarmonyPatch(typeof(RainAudioModule), nameof(RainAudioModule.UpdateModule))]
        private static class LocomotiveRainPatch
        {
            private static void Postfix(RainAudioModule __instance)
            {
                // Let the vanilla module continue updating its private smoothing and
                // roof detection, then silence only its final layered rain output.
                // This prevents a stale rain tail when snowfall ends.
                if (!SuppressRainAudio) return;
                if (__instance.rainAudio != null) __instance.rainAudio.Set(0f);
            }
        }
    }
}
