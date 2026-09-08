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
                settings = UnityModManager.ModSettings.Load<SeasonModSettings>(entry) ?? new SeasonModSettings();
                settings.Clamp();
                runtime = new SeasonRuntime(entry, settings);
                entry.OnToggle = OnToggle;
                entry.OnUnload = OnUnload;
                entry.OnGUI = OnGui;
                entry.OnSaveGUI = OnSaveGui;
                entry.OnUpdate = OnUpdate;
                entry.OnSessionStart = OnSessionStart;
                entry.Logger.Log("Dynamic Seasons 0.2.40 loaded with automatic Russian/English localization.");
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
            var russian = ModLocalization.IsRussian;
            var state = runtime == null ? null : runtime.CurrentState;
            GUILayout.Label(ModLocalization.Text(russian,
                "Dynamic Seasons — плавные времена года",
                "Dynamic Seasons — smooth seasonal transitions"));
            if (state != null)
            {
                GUILayout.Label(ModLocalization.Text(russian, "Сейчас: ", "Current: ") +
                    SeasonName(state.Current, russian) + " → " + SeasonName(state.Next, russian) +
                    ModLocalization.Text(russian, ", переход ", ", transition ") +
                    ModLocalization.Number(russian, state.Transition * 100f, "F0") + "%");
                GUILayout.Label(ModLocalization.Text(russian, "Снег: ", "Snow: ") +
                    ModLocalization.Number(russian, state.SnowAmount * 100f, "F0") +
                    ModLocalization.Text(russian, "%, температура: ", "%, temperature: ") +
                    ModLocalization.Number(russian, state.TemperatureCelsius, "F1") + " °C");
            }
            GUILayout.Label(NetworkStatus(russian));
            GUILayout.Label(WeatherStatus(russian));
            GUILayout.Space(8f);

            settings.MuteRainAudioDuringSnow = GUILayout.Toggle(settings.MuteRainAudioDuringSnow,
                ModLocalization.Text(russian,
                    "Без звука дождя во время зимнего снегопада",
                    "Mute rain audio during winter snowfall"));
            settings.SeasonalPrecipitationEnabled = GUILayout.Toggle(settings.SeasonalPrecipitationEnabled,
                ModLocalization.Text(russian,
                    "Сезонные облачность, туман и осадки",
                    "Seasonal clouds, fog and precipitation"));
            settings.SeasonalDaylightEnabled = GUILayout.Toggle(settings.SeasonalDaylightEnabled,
                ModLocalization.Text(russian, "Сезонная длина светового дня (6–18 часов)",
                    "Seasonal daylight duration (6–18 hours)"));
            settings.DisableWinterThunder = GUILayout.Toggle(settings.DisableWinterThunder,
                ModLocalization.Text(russian,
                    "Отключать гром и молнии зимой",
                    "Disable thunder and lightning in winter"));
            settings.ProceduralSnowEnabled = GUILayout.Toggle(settings.ProceduralSnowEnabled,
                ModLocalization.Text(russian,
                    "Новая система снега",
                    "New snow system"));
            GUILayout.Label(ModLocalization.Text(russian,
                "Включено — динамический снег. Выключено — сезонные текстуры без расчёта снежного покрова.",
                "On: dynamic snow. Off: seasonal textures without snow-cover simulation."));
            settings.NativeWinterVegetationLod = GUILayout.Toggle(settings.NativeWinterVegetationLod,
                ModLocalization.Text(russian,
                    "Штатная детализация деревьев зимой (быстрее)",
                    "Native tree detail in winter (faster)"));
            settings.WinterWaterIceEnabled = GUILayout.Toggle(settings.WinterWaterIceEnabled,
                ModLocalization.Text(russian,
                    "Замерзание воды зимой",
                    "Freeze world water in winter"));
            settings.FreezeWinterPuddles = GUILayout.Toggle(settings.FreezeWinterPuddles,
                ModLocalization.Text(russian,
                    "Замораживать лужи зимой, сохраняя влажность",
                    "Freeze winter puddles while keeping surface wetness"));
            GUILayout.Space(8f);

            settings.AutomaticCycle = GUILayout.Toggle(settings.AutomaticCycle,
                ModLocalization.Text(russian, "Автоматическая смена сезонов", "Automatic season cycle"));
            GUILayout.Label(ModLocalization.Text(russian, "Дней на сезон: ", "Days per season: ") +
                ModLocalization.Number(russian, settings.DaysPerSeason, "F0"));
            settings.DaysPerSeason = GUILayout.HorizontalSlider(settings.DaysPerSeason, 1f, 90f);
            var requestedRandomDuration = GUILayout.Toggle(settings.RandomTransitionDuration,
                ModLocalization.Text(russian,
                    "Случайная длительность перехода (1–5 игровых дней)",
                    "Random transition duration (1–5 game days)"));
            if (requestedRandomDuration != settings.RandomTransitionDuration)
            {
                if (runtime != null) runtime.SetRandomTransitionDuration(requestedRandomDuration);
                else settings.RandomTransitionDuration = requestedRandomDuration;
            }
            var transitionDays = runtime == null ? settings.TransitionDays : runtime.CurrentTransitionDays;
            GUILayout.Label(ModLocalization.Text(russian,
                    settings.RandomTransitionDuration ? "Текущая длительность перехода: " :
                        "Длительность перехода: ",
                    settings.RandomTransitionDuration ? "Current transition duration: " :
                        "Transition duration: ") +
                ModLocalization.Number(russian, transitionDays, "F0") +
                ModLocalization.Text(russian, " дн.", " days"));
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
            if (GUILayout.Button(SeasonName(SeasonKind.Spring, russian))) runtime.SetSeason(SeasonKind.Spring);
            if (GUILayout.Button(SeasonName(SeasonKind.Summer, russian))) runtime.SetSeason(SeasonKind.Summer);
            if (GUILayout.Button(SeasonName(SeasonKind.Autumn, russian))) runtime.SetSeason(SeasonKind.Autumn);
            if (GUILayout.Button(SeasonName(SeasonKind.Winter, russian))) runtime.SetSeason(SeasonKind.Winter);
            if (GUILayout.Button(ModLocalization.Text(russian, "Следующий", "Next"))) runtime.AdvanceToNextSeason();
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label(ModLocalization.Text(russian, "Плотность снегопада: ", "Snowfall density: ") +
                ModLocalization.Number(russian, settings.SnowfallDensity, "F2"));
            settings.SnowfallDensity = GUILayout.HorizontalSlider(settings.SnowfallDensity, 0f, 2f);
            GUILayout.Space(8f);
            GUILayout.Label(ModLocalization.Text(russian, "Эквивалент мокрого рельса: ",
                    "Wet-rail equivalent: ") +
                ModLocalization.Number(russian, settings.WinterWetnessEquivalent, "F2") +
                ModLocalization.Text(russian, " (0,50 = штатный минимум сцепления)",
                    " (0.50 = vanilla minimum adhesion)"));
            settings.WinterWetnessEquivalent = GUILayout.HorizontalSlider(settings.WinterWetnessEquivalent, 0f, 0.5f);
            settings.RespectExternalWetnessOverride = GUILayout.Toggle(settings.RespectExternalWetnessOverride,
                ModLocalization.Text(russian,
                    "Не перехватывать уже установленный другим модом override мокроты",
                    "Do not replace a wetness override already set by another mod"));
        }

        private static string NetworkStatus(bool russian)
        {
            if (runtime == null) return ModLocalization.Text(russian, "Runtime не создан", "Runtime not created");
            if (!runtime.IsNetworkAvailable)
                return ModLocalization.Text(russian, "Локальный режим", "Local mode");
            if (!runtime.IsNetworkSessionActive)
                return ModLocalization.Text(russian, "Multiplayer доступен — одиночная сессия",
                    "Multiplayer available — single-player session");
            return runtime.IsNetworkAuthority
                ? ModLocalization.Text(russian, "Multiplayer: хост управляет сезонами и погодой",
                    "Multiplayer: the host controls seasons and weather")
                : ModLocalization.Text(russian, "Multiplayer: сезон и погода синхронизированы с хостом",
                    "Multiplayer: season and weather synchronized with the host");
        }

        private static string WeatherStatus(bool russian)
        {
            if (runtime == null) return ModLocalization.Text(russian, "Погода недоступна", "Weather unavailable");
            return runtime.IsWeatherReady
                ? ModLocalization.Text(russian, "Система погоды подключена", "Weather system connected")
                : ModLocalization.Text(russian, "Ожидание загрузки мира и погоды",
                    "Waiting for the world and weather to load");
        }

        private static string SeasonName(SeasonKind season, bool russian)
        {
            switch (season)
            {
                case SeasonKind.Spring: return ModLocalization.Text(russian, "Весна", "Spring");
                case SeasonKind.Summer: return ModLocalization.Text(russian, "Лето", "Summer");
                case SeasonKind.Autumn: return ModLocalization.Text(russian, "Осень", "Autumn");
                case SeasonKind.Winter: return ModLocalization.Text(russian, "Зима", "Winter");
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
