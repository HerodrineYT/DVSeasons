using System;
using DVSeasons.Core;
using UnityEngine;
using UnityModManagerNet;

namespace DVSeasons.Mod
{
    public static class Main
    {
        private static UnityModManager.ModEntry entry;
        private static SeasonModSettings settings;
        private static SeasonRuntime runtime;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            if (modEntry == null) return false;
            entry = modEntry;
            try
            {
                ModLocalization.Initialize(entry.Path);
                settings = UnityModManager.ModSettings.Load<SeasonModSettings>(entry) ?? new SeasonModSettings();
                settings.Clamp();
                runtime = new SeasonRuntime(entry, settings);
                entry.OnToggle = OnToggle;
                entry.OnUnload = OnUnload;
                entry.OnGUI = OnGui;
                entry.OnSaveGUI = OnSaveGui;
                entry.OnUpdate = OnUpdate;
                entry.OnSessionStart = OnSessionStart;
                entry.Logger.Log("Dynamic Seasons " + entry.Info.Version + " loaded with Language Helper localization.");
                entry.Logger.Log("Cold-start hints build 2026-09-21: host vanilla-start rule, local tutorial hints, protocol " + SeasonNetworkState.CurrentProtocol + ".");
                entry.Logger.Log("Native snow sharing build 2026-09-21: shared compatible materials and fallback-only batching index.");
                return true;
            }
            catch (Exception exception)
            {
                entry.Logger.Error("Dynamic Seasons failed to load.");
                entry.Logger.LogException(exception);
                Cleanup();
                return false;
            }
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool enabled)
        {
            try
            {
                if (enabled) runtime.Start(); else runtime.Stop();
                return true;
            }
            catch (Exception exception)
            {
                modEntry.Logger.LogException(exception);
                return false;
            }
        }

        private static bool OnUnload(UnityModManager.ModEntry modEntry)
        {
            if (runtime != null) runtime.SaveSettings();
            else if (settings != null) settings.Save(modEntry);
            Cleanup();
            return true;
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float deltaTime)
        {
            if (runtime != null) runtime.Tick(deltaTime);
        }

        private static void OnSaveGui(UnityModManager.ModEntry modEntry)
        {
            if (runtime != null) runtime.SaveSettings();
            else if (settings != null) settings.Save(modEntry);
        }

        private static void OnSessionStart(UnityModManager.ModEntry modEntry)
        {
            try
            {
                if (runtime != null) runtime.OnSessionStart();
            }
            catch (Exception exception)
            {
                modEntry.Logger.LogException(exception);
            }
        }

        private static void OnGui(UnityModManager.ModEntry modEntry)
        {
            if (settings == null) return;
            settings.Clamp();
            var state = runtime == null ? null : runtime.CurrentState;
            GUILayout.Label(ModLocalization.Text("UI.Title"));
            if (state != null)
            {
                GUILayout.Label(ModLocalization.Format("Status.Season", SeasonName(state.Current),
                    SeasonName(state.Next), state.Transition * 100f));
                GUILayout.Label(ModLocalization.Format("Status.Snow", state.SnowAmount * 100f,
                    state.TemperatureCelsius));
            }
            GUILayout.Label(NetworkStatus());
            GUILayout.Label(WeatherStatus());
            GUILayout.Label(runtime == null ? "" : runtime.BlizzardStatus);
            var beforeBlizzardControls = GUI.enabled;
            GUI.enabled = beforeBlizzardControls && runtime != null && runtime.IsWeatherReady &&
                (!runtime.IsNetworkSessionActive || runtime.IsNetworkAuthority);
            settings.BlizzardsEnabled = GUILayout.Toggle(settings.BlizzardsEnabled, ModLocalization.Text("Blizzard.Enabled"));
            GUILayout.BeginHorizontal();
            GUI.enabled &= state != null && state.Current == SeasonKind.Winter;
            if (GUILayout.Button(ModLocalization.Text("Blizzard.Schedule"))) runtime.ScheduleBlizzard();
            if (GUILayout.Button(ModLocalization.Text("Blizzard.Stop"))) runtime.EndBlizzard();
            GUILayout.EndHorizontal();
            GUI.enabled = beforeBlizzardControls;
            GUILayout.Label(ModLocalization.Format("Settings.BlizzardRadioVolume", settings.BlizzardRadioVolume * 100f));
            settings.BlizzardRadioVolume = GUILayout.HorizontalSlider(settings.BlizzardRadioVolume, 0f, 3f);
            GUILayout.Space(8f);

            settings.MuteRainAudioDuringSnow = GUILayout.Toggle(settings.MuteRainAudioDuringSnow,
                ModLocalization.Text("Settings.MuteRain"));
            settings.SeasonalPrecipitationEnabled = GUILayout.Toggle(settings.SeasonalPrecipitationEnabled,
                ModLocalization.Text("Settings.SeasonalWeather"));
            settings.SeasonalDaylightEnabled = GUILayout.Toggle(settings.SeasonalDaylightEnabled,
                ModLocalization.Text("Settings.SeasonalDaylight"));
            settings.DisableWinterThunder = GUILayout.Toggle(settings.DisableWinterThunder,
                ModLocalization.Text("Settings.DisableWinterThunder"));
            settings.ProceduralSnowEnabled = GUILayout.Toggle(settings.ProceduralSnowEnabled,
                ModLocalization.Text("Settings.NewSnow"));
            GUILayout.Label(ModLocalization.Text("Settings.NewSnowHelp"));
            settings.VehicleSnowEnabled = !GUILayout.Toggle(!settings.VehicleSnowEnabled,
                ModLocalization.Text("Settings.DisableVehicleSnow"));
            GUILayout.Label(ModLocalization.Text("Settings.DisableVehicleSnowHelp"),new GUIStyle(GUI.skin.label){wordWrap=true});
            var snowLimitGuiEnabled = GUI.enabled;
            GUI.enabled = snowLimitGuiEnabled && settings.ProceduralSnowEnabled && settings.VehicleSnowEnabled;
            GUILayout.Label(ModLocalization.Format("Settings.SnowObjectLimit", settings.SnowObjectLimit == 0
                ? ModLocalization.Text("Settings.Unlimited") : settings.SnowObjectLimit.ToString()));
            var snowObjectSlider = Mathf.RoundToInt(GUILayout.HorizontalSlider(
                settings.SnowObjectLimit == 0 ? 1001 : Mathf.Sqrt(settings.SnowObjectLimit) * 10f, 10, 1001));
            settings.SnowObjectLimit = snowObjectSlider == 1001 ? 0 : Mathf.RoundToInt(snowObjectSlider * snowObjectSlider / 100f);
            GUI.enabled = snowLimitGuiEnabled;
            GUILayout.Label(ModLocalization.Text("Settings.SnowObjectLimitHelp"));
            if(SnowRenderBenchmark.Active)
            {
                GUILayout.Label(ModLocalization.Format("Diagnostics.SnowProgress",SnowRenderBenchmark.SecondsRemaining));
                if(GUILayout.Button(ModLocalization.Text("Diagnostics.SnowCancel")))SnowRenderBenchmark.Cancel();
            }
            else
            {
                var diagnosticEnabled=GUI.enabled;
                GUI.enabled=diagnosticEnabled && settings.VehicleSnowEnabled && SnowRenderBenchmark.CanStart;
                if(GUILayout.Button(ModLocalization.Text("Diagnostics.SnowStart")))SnowRenderBenchmark.Start();
                GUI.enabled=diagnosticEnabled;
            }
            GUILayout.Label(ModLocalization.Text("Diagnostics.SnowHelp"),new GUIStyle(GUI.skin.label){wordWrap=true});
            settings.WinterWindowsEnabled = GUILayout.Toggle(settings.WinterWindowsEnabled,
                ModLocalization.Text("Settings.WinterWindows"));
            GUILayout.BeginHorizontal();
            GUILayout.Label(ModLocalization.Text("Settings.HeaterKey"));
            UnityModManager.UI.DrawKeybindingSmart(settings.CabHeaterHotkey,
                ModLocalization.Text("Settings.HeaterKey"), key => { settings.CabHeaterHotkey = key; settings.Save(entry); });
            if (GUILayout.Button(ModLocalization.Text("Settings.ClearHeaterKey")))
            { settings.CabHeaterHotkey = new KeyBinding(); settings.Save(entry); }
            GUILayout.EndHorizontal();
            GUILayout.Label(ModLocalization.Text("Settings.HeaterKeyHelp"));
            settings.EngineHeatingWithoutSwitch = GUILayout.Toggle(settings.EngineHeatingWithoutSwitch,
                ModLocalization.Text("Settings.EngineHeatingWithoutSwitch"));
            GUILayout.Label(ModLocalization.Text("Settings.EngineHeatingWithoutSwitchHelp"));
            var startingGuiEnabled=GUI.enabled;
            GUI.enabled=startingGuiEnabled && runtime.CanConfigureColdStarts;
            var ignoreCold=GUILayout.Toggle(runtime.IgnoreVanillaColdStarts,ModLocalization.Text("Settings.IgnoreVanillaColdStarts"));
            if(runtime.CanConfigureColdStarts)settings.IgnoreVanillaColdStarts=ignoreCold;
            GUI.enabled=startingGuiEnabled;
            GUILayout.Label(ModLocalization.Text("Settings.IgnoreVanillaColdStartsHelp"));
            settings.ColdStartHintsEnabled=GUILayout.Toggle(settings.ColdStartHintsEnabled,
                ModLocalization.Text("Settings.ColdStartHints"));
            settings.AutumnLeavesEnabled = GUILayout.Toggle(settings.AutumnLeavesEnabled,
                ModLocalization.Text("Settings.AutumnLeaves"));
            GUILayout.Label(ModLocalization.Format("Settings.LeafLimit", settings.AutumnLeafLimit == 0
                ? ModLocalization.Text("Settings.Unlimited") : settings.AutumnLeafLimit.ToString()));
            var leafSlider = Mathf.RoundToInt(GUILayout.HorizontalSlider(
                settings.AutumnLeafLimit == 0 ? 201 : settings.AutumnLeafLimit / 100f, 1, 201));
            settings.AutumnLeafLimit = leafSlider == 201 ? 0 : leafSlider * 100;
            settings.InsectsEnabled = GUILayout.Toggle(settings.InsectsEnabled,
                ModLocalization.Text("Settings.Insects"));
            settings.NativeWinterVegetationLod = GUILayout.Toggle(settings.NativeWinterVegetationLod,
                ModLocalization.Text("Settings.NativeTreeLod"));
            settings.WinterWaterIceEnabled = GUILayout.Toggle(settings.WinterWaterIceEnabled,
                ModLocalization.Text("Settings.WaterIce"));
            settings.FreezeWinterPuddles = GUILayout.Toggle(settings.FreezeWinterPuddles,
                ModLocalization.Text("Settings.FreezePuddles"));
            GUILayout.Space(8f);

            settings.AutomaticCycle = GUILayout.Toggle(settings.AutomaticCycle,
                ModLocalization.Text("Settings.AutomaticCycle"));
            GUILayout.Label(ModLocalization.Format("Settings.DaysPerSeason", settings.DaysPerSeason));
            settings.DaysPerSeason = GUILayout.HorizontalSlider(settings.DaysPerSeason, 1f, 90f);
            var requestedRandomDuration = GUILayout.Toggle(settings.RandomTransitionDuration,
                ModLocalization.Text("Settings.RandomTransition"));
            if (requestedRandomDuration != settings.RandomTransitionDuration)
            {
                if (runtime != null) runtime.SetRandomTransitionDuration(requestedRandomDuration);
                else settings.RandomTransitionDuration = requestedRandomDuration;
            }
            var transitionDays = runtime == null ? settings.TransitionDays : runtime.CurrentTransitionDays;
            GUILayout.Label(ModLocalization.Format(settings.RandomTransitionDuration
                ? "Settings.CurrentTransitionDays" : "Settings.TransitionDays", transitionDays));
            var guiWasEnabled = GUI.enabled;
            GUI.enabled = guiWasEnabled && !settings.RandomTransitionDuration;
            var maximumTransitionDays = Mathf.Max(1f, Mathf.Min(5f, Mathf.Floor(settings.DaysPerSeason)));
            var requestedTransitionDays = Mathf.Round(GUILayout.HorizontalSlider(
                transitionDays, 1f, maximumTransitionDays));
            GUI.enabled = guiWasEnabled;
            if (!settings.RandomTransitionDuration &&
                Mathf.Abs(requestedTransitionDays - settings.TransitionDays) > 0.001f)
            {
                if (runtime != null) runtime.SetManualTransitionDays(requestedTransitionDays);
                else settings.TransitionDays = requestedTransitionDays;
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(SeasonName(SeasonKind.Spring))) runtime.SetSeason(SeasonKind.Spring);
            if (GUILayout.Button(SeasonName(SeasonKind.Summer))) runtime.SetSeason(SeasonKind.Summer);
            if (GUILayout.Button(SeasonName(SeasonKind.Autumn))) runtime.SetSeason(SeasonKind.Autumn);
            if (GUILayout.Button(SeasonName(SeasonKind.Winter))) runtime.SetSeason(SeasonKind.Winter);
            if (GUILayout.Button(ModLocalization.Text("Action.Next"))) runtime.AdvanceToNextSeason();
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label(ModLocalization.Format("Settings.SnowfallDensity", settings.SnowfallDensity));
            settings.SnowfallDensity = GUILayout.HorizontalSlider(settings.SnowfallDensity, 0f, 2f);
            GUILayout.Label(ModLocalization.Format("Settings.SnowGlare", settings.SnowGlareReduction * 100f));
            settings.SnowGlareReduction = GUILayout.HorizontalSlider(settings.SnowGlareReduction, 0f, 2f);
            GUILayout.Space(8f);
            GUILayout.Label(ModLocalization.Format("Settings.WetRailEquivalent", settings.WinterWetnessEquivalent));
            settings.WinterWetnessEquivalent = GUILayout.HorizontalSlider(settings.WinterWetnessEquivalent, 0f, 0.5f);
            settings.RespectExternalWetnessOverride = GUILayout.Toggle(settings.RespectExternalWetnessOverride,
                ModLocalization.Text("Settings.RespectWetness"));
        }

        private static string NetworkStatus()
        {
            if (runtime == null) return ModLocalization.Text("Status.NoRuntime");
            if (!runtime.IsNetworkAvailable)
                return ModLocalization.Text("Status.Local");
            if (!runtime.IsNetworkSessionActive)
                return ModLocalization.Text("Status.SinglePlayer");
            return runtime.IsNetworkAuthority
                ? ModLocalization.Text("Status.Host")
                : ModLocalization.Text("Status.Client");
        }

        private static string WeatherStatus()
        {
            if (runtime == null) return ModLocalization.Text("Status.NoWeather");
            return runtime.IsWeatherReady
                ? ModLocalization.Text("Status.WeatherReady")
                : ModLocalization.Text("Status.WaitingWeather");
        }

        private static string SeasonName(SeasonKind season)
        {
            switch (season)
            {
                case SeasonKind.Spring: return ModLocalization.Text("Season.Spring");
                case SeasonKind.Summer: return ModLocalization.Text("Season.Summer");
                case SeasonKind.Autumn: return ModLocalization.Text("Season.Autumn");
                case SeasonKind.Winter: return ModLocalization.Text("Season.Winter");
                default: return season.ToString();
            }
        }

        private static void Cleanup()
        {
            if (runtime != null) { runtime.Dispose(); runtime = null; }
            if (entry != null)
            {
                entry.OnToggle = null;
                entry.OnUnload = null;
                entry.OnGUI = null;
                entry.OnSaveGUI = null;
                entry.OnUpdate = null;
                entry.OnSessionStart = null;
            }
            entry = null;
        }
    }
}
