using System;
using System.IO;
using System.Reflection;
using System.Globalization;
using DVSeasons.Core;
using UnityModManagerNet;

namespace DVSeasons.Mod
{
    internal sealed class SeasonRuntime : IDisposable
    {
        private const float SettingsCheckpointSeconds = 30f;
        private readonly UnityModManager.ModEntry entry;
        private readonly SeasonModSettings settings;
        private readonly WeatherAdapter weather = new WeatherAdapter();
        private readonly WinterRainAudioController rainAudio = new WinterRainAudioController();
        private readonly MultiplayerLogSpamFilter multiplayerLogSpam = new MultiplayerLogSpamFilter();
        private readonly SeasonVisualController visuals;
        private readonly ISeasonNetworkBridge network;
        private readonly SeasonCycle cycle;
        private readonly SeasonGameClock gameClock = new SeasonGameClock();
        private readonly Random transitionRandom = new Random(unchecked(Environment.TickCount * 397));
        private SeasonState currentState;
        private SeasonSaveState localCalendar;
        private SaveGameManager saveManager;
        private SaveGameData sessionSaveData;
        private bool sessionReady;
        private bool sessionWasClient;
        private float nextSettingsCheckpoint;
        private bool started;
        private bool disposed;
        private bool receivedNetworkState;
        private float networkRainIntensity;
        private float networkWindVelocityX;
        private float networkWindVelocityZ;
        private float networkSnowLightFactor = 1f;

        public SeasonRuntime(UnityModManager.ModEntry entry, SeasonModSettings settings,
            ISeasonNetworkBridge networkBridge = null)
        {
            this.entry = entry ?? throw new ArgumentNullException(nameof(entry));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            visuals = new SeasonVisualController(entry.Path);
            settings.Clamp();
            var initialPhase = settings.HasSavedPhase ? settings.SavedPhase : settings.StartingSeason;
            EnsureTransitionDuration(initialPhase);
            cycle = new SeasonCycle(settings.ToSnapshot(), initialPhase);
            currentState = cycle.GetState();
            localCalendar = CaptureCalendar();
            network = networkBridge ?? CreateNetworkBridge(entry.Path);
            network.StateReceived += OnNetworkStateReceived;
            network.Initialize(entry.Info.Id);
        }

        public SeasonState CurrentState { get { return currentState; } }
        public string NetworkStatus { get { return network.Status; } }
        public string WeatherStatus { get { return weather.IsReady ? "Система погоды подключена" : "Ожидание загрузки мира и погоды"; } }
        public bool IsNetworkAvailable { get { return network.IsAvailable; } }
        public bool IsNetworkSessionActive { get { return network.IsSessionActive; } }
        public bool IsNetworkAuthority { get { return network.IsAuthority; } }
        public bool IsWeatherReady { get { return weather.IsReady; } }
        public float CurrentTransitionDays { get { return settings.TransitionDays; } }

        public void Start()
        {
            if (disposed || started) return;
            started = true;
            multiplayerLogSpam.Enable();
            receivedNetworkState = false;
            sessionWasClient = false;
            gameClock.Reset();
            WorldStreamingInit.LoadingFinished += OnWorldLoaded;
            UnloadWatcher.UnloadRequested += OnWorldUnloading;
            network.SetEnabled(true);
            // UMM may enable the mod after a world is already loaded. Do not use
            // SaveGameManager.Instance here: it auto-creates a manager in the menu.
            var manager = UnityEngine.Object.FindObjectOfType<SaveGameManager>();
            if (!UnloadWatcher.isUnloading && manager != null && manager.data != null &&
                WorldStreamingInit.IsLoaded)
                OnWorldLoaded();
            entry.Logger.Log("DV Seasons runtime started. " + network.Status);
        }

        public void Stop()
        {
            if (!started) return;
            // Toggling the mod off/on in the same live world must resume its
            // current calendar, not the phase from the last disk save.
            if (sessionReady) OnSaveDataUpdate(sessionSaveData);
            EndSession();
            SaveSettings();
            started = false;
            WorldStreamingInit.LoadingFinished -= OnWorldLoaded;
            UnloadWatcher.UnloadRequested -= OnWorldUnloading;
            network.SetEnabled(false);
            multiplayerLogSpam.Disable();
            weather.ResetForSession();
            rainAudio.Apply(0f, false, false);
            visuals.ResetForSession();
            gameClock.Reset();
        }

        public void OnSessionStart()
        {
            if (!started || disposed) return;
            // UMM fires this while the player is being created, before the saved
            // weather clock has finished loading. Bind save-data now, but wait for
            // WorldStreamingInit.LoadingFinished before advancing the calendar.
            gameClock.Reset();
            TryPrepareSession();
        }

        public void Tick(float deltaTime)
        {
            if (!started || disposed || !sessionReady) return;
            if (UnloadWatcher.isUnloading || saveManager == null)
            {
                OnWorldUnloading();
                return;
            }
            settings.Clamp();
            weather.TickProbe();
            if (HasLocalAuthority())
            {
                var transitionSelectionChanged = EnsureTransitionDuration(cycle.Phase);
                cycle.Configure(settings.ToSnapshot());
                AdvanceCycle(deltaTime);
                if (EnsureTransitionDuration(cycle.Phase))
                {
                    transitionSelectionChanged = true;
                    cycle.Configure(settings.ToSnapshot());
                }
                currentState = cycle.GetState();
                // A new random 1-5 day selection is authoritative configuration,
                // not merely visual state. Send it immediately instead of waiting
                // for the next periodic weather/season broadcast.
                network.Publish(CreateNetworkState(currentState), transitionSelectionChanged);
                if (UnityEngine.Time.realtimeSinceStartup >= nextSettingsCheckpoint)
                {
                    nextSettingsCheckpoint = UnityEngine.Time.realtimeSinceStartup + SettingsCheckpointSeconds;
                    SaveSettings();
                }
            }
            else if (!receivedNetworkState)
            {
                network.RequestState();
                return;
            }
            weather.ApplyWinterAdhesion(currentState, settings.WinterAdhesionEnabled,
                settings.RespectExternalWetnessOverride);
            weather.ApplySeasonalPrecipitation(currentState, settings.SeasonalPrecipitationEnabled);
            weather.ApplySeasonalClimate(currentState, settings.SeasonalDaylightEnabled,
                settings.SeasonalPrecipitationEnabled);
            weather.ApplyWinterThunderSuppression(currentState, settings.DisableWinterThunder);
            var useHostWeather = network.IsSessionActive && !network.IsAuthority && receivedNetworkState;
            var rainIntensity = useHostWeather ? networkRainIntensity : weather.RainIntensity;
            var windVelocity = useHostWeather
                ? new UnityEngine.Vector3(networkWindVelocityX, 0f, networkWindVelocityZ)
                : weather.SnowWindVelocity;
            var snowLightFactor = useHostWeather ? networkSnowLightFactor : weather.SnowLightFactor;
            var precipitationSnow = SnowCoverProfile.GetPrecipitationSnowAmount(currentState,
                settings.DaysPerSeason, settings.TransitionDays,
                settings.GroundSnowStrength * settings.TextureChangeStrength);
            rainAudio.Apply(precipitationSnow, settings.ReplaceRainWithSnow,
                settings.MuteRainAudioDuringSnow);
            visuals.Apply(currentState, precipitationSnow, rainIntensity, windVelocity,
                snowLightFactor, settings);
        }

        public void SetSeason(SeasonKind season)
        {
            if (!HasLocalAuthority()) return;
            cycle.SetSeason(season);
            EnsureTransitionDuration(cycle.Phase, true);
            cycle.Configure(settings.ToSnapshot());
            PublishManualChange();
        }

        public void AdvanceToNextSeason()
        {
            if (!HasLocalAuthority()) return;
            cycle.AdvanceToNextSeason();
            EnsureTransitionDuration(cycle.Phase, true);
            cycle.Configure(settings.ToSnapshot());
            PublishManualChange();
        }

        public void SavePhaseIfAuthoritative()
        {
            if (disposed || !HasLocalAuthority()) return;
            settings.Clamp();
            EnsureTransitionDuration(cycle.Phase);
            cycle.Configure(settings.ToSnapshot());
            currentState = cycle.GetState();
            settings.SavedPhase = (float)cycle.Phase;
            settings.HasSavedPhase = true;
            localCalendar = CaptureCalendar();
        }

        public void SaveSettings()
        {
            if (disposed) return;
            SavePhaseIfAuthoritative();
            var displayedCalendar = CaptureCalendar();
            try
            {
                // Clients can save their visual options without persisting a host's
                // calendar over the local game's fallback settings.
                if (sessionWasClient) ApplyCalendarSettings(localCalendar);
                settings.Save(entry);
            }
            catch (Exception exception)
            {
                entry.Logger.Warning("Could not checkpoint season settings: " + exception.Message);
            }
            finally
            {
                if (sessionWasClient) ApplyCalendarSettings(displayedCalendar);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            network.StateReceived -= OnNetworkStateReceived;
            network.Dispose();
            weather.Dispose();
            rainAudio.Dispose();
            multiplayerLogSpam.Dispose();
            visuals.Dispose();
            disposed = true;
        }

        private void AdvanceCycle(float deltaTime)
        {
            DateTime gameTime;
            DateTime? sample = weather.TryGetGameDateTime(out gameTime) ? (DateTime?)gameTime : null;
            cycle.AdvanceGameDays(gameClock.GetElapsedDays(sample, deltaTime,
                settings.FallbackMinutesPerGameDay));
        }

        private void PublishManualChange()
        {
            currentState = cycle.GetState();
            network.Publish(CreateNetworkState(currentState), true);
            SaveSettings();
        }

        private bool HasLocalAuthority()
        {
            if (sessionWasClient) return false;
            try
            {
                if (network.IsSessionActive && !network.IsAuthority)
                {
                    sessionWasClient = true;
                    return false;
                }
            }
            catch
            {
                // The network API may already be gone during UnloadRequested.
                // The latched client flag prevents saving foreign state then.
            }
            return true;
        }

        private SeasonSaveState CaptureCalendar()
        {
            return new SeasonSaveState
            {
                Phase = cycle.Phase,
                AutomaticCycle = settings.AutomaticCycle,
                DaysPerSeason = settings.DaysPerSeason,
                RandomTransitionDuration = settings.RandomTransitionDuration,
                TransitionDays = settings.TransitionDays,
                TransitionSeason = settings.TransitionSeason
            };
        }

        private void ApplyCalendarSettings(SeasonSaveState state)
        {
            settings.AutomaticCycle = state.AutomaticCycle;
            settings.DaysPerSeason = state.DaysPerSeason;
            settings.RandomTransitionDuration = state.RandomTransitionDuration;
            settings.TransitionDays = state.TransitionDays;
            settings.TransitionSeason = state.TransitionSeason;
        }

        private void RestoreCalendar(SeasonSaveState state)
        {
            ApplyCalendarSettings(state);
            cycle.Configure(settings.ToSnapshot());
            cycle.SetPhase(state.Phase);
            currentState = cycle.GetState();
            gameClock.Reset();
        }

        private bool TryPrepareSession()
        {
            if (UnloadWatcher.isUnloading) return false;
            var manager = UnityEngine.Object.FindObjectOfType<SaveGameManager>();
            if (manager == null || manager.data == null) return false;
            if (manager == saveManager && ReferenceEquals(sessionSaveData, manager.data)) return true;
            if (sessionSaveData != null) EndSession();

            visuals.ResetForSession();
            weather.ResetForSession();
            rainAudio.Apply(0f, false, false);
            gameClock.Reset();
            if (HasLocalAuthority())
            {
                SeasonSaveState saved = null;
                try { SeasonSaveData.TryRead(manager.data, out saved); }
                catch (Exception exception)
                {
                    entry.Logger.Warning("Invalid season data in save; using the local checkpoint: " +
                        exception.Message);
                }
                if (saved != null)
                {
                    // Restoring the recorded choice must not roll another random
                    // transition or reinterpret the phase using other save settings.
                    RestoreCalendar(saved);
                }
                else if (manager.IsNewSession)
                {
                    cycle.SetSeason((SeasonKind)settings.StartingSeason);
                    EnsureTransitionDuration(cycle.Phase, true);
                    cycle.Configure(settings.ToSnapshot());
                    currentState = cycle.GetState();
                }
                else
                {
                    // One-time migration for saves made before calendar data was
                    // stored in the save itself. Preserve the old UMM winter phase.
                    RestoreCalendar(localCalendar);
                }
                SavePhaseIfAuthoritative();
                entry.Logger.Log("Season restored from " + (saved != null ? "game save" : "local settings") +
                    ": " + currentState.Current + ", phase " +
                    cycle.Phase.ToString("F6", CultureInfo.InvariantCulture) +
                    ", transition " + settings.TransitionDays.ToString("F0", CultureInfo.InvariantCulture) + " day(s).");
            }
            saveManager = manager;
            sessionSaveData = manager.data;
            saveManager.OnInternalDataUpdate += OnSaveDataUpdate;
            return true;
        }

        private void OnWorldLoaded()
        {
            if (!started || disposed) return;
            try
            {
                if (!TryPrepareSession()) return;
                sessionReady = true;
                // Multiplayer can finish connecting just after the game's world-loaded
                // event, so use bridge availability to reserve its control-hook window.
                visuals.BeginSession(network.IsAvailable);
                // Date/time restoration is now complete. The first sample anchors
                // the new session instead of advancing from the previous world's date.
                gameClock.Reset();
                nextSettingsCheckpoint = UnityEngine.Time.realtimeSinceStartup + SettingsCheckpointSeconds;
                weather.TickProbe();
                if (HasLocalAuthority()) network.Publish(CreateNetworkState(currentState), true);
                else network.RequestState();
            }
            catch (Exception exception)
            {
                entry.Logger.Warning("Season session initialization failed: " + exception.Message);
            }
        }

        private void OnSaveDataUpdate(SaveGameData data)
        {
            if (!started || disposed || !sessionReady || !ReferenceEquals(data, sessionSaveData) ||
                !HasLocalAuthority()) return;
            try
            {
                SavePhaseIfAuthoritative();
                SeasonSaveData.Write(data, localCalendar);
                SaveSettings();
            }
            catch (Exception exception)
            {
                entry.Logger.Warning("Could not add season data to the game save: " + exception.Message);
            }
        }

        private void OnWorldUnloading()
        {
            if (!started || disposed) return;
            try { EndSession(); }
            catch (Exception exception)
            {
                // Never prevent another mod's UnloadRequested handler from running.
                entry.Logger.Warning("Season session cleanup failed: " + exception.Message);
            }
        }

        private void EndSession()
        {
            gameClock.Reset();
            if (sessionSaveData == null && !sessionReady && !sessionWasClient) return;
            SaveSettings();
            if (saveManager != null) saveManager.OnInternalDataUpdate -= OnSaveDataUpdate;
            saveManager = null;
            sessionSaveData = null;
            sessionReady = false;
            receivedNetworkState = false;
            if (sessionWasClient) RestoreCalendar(localCalendar);
            sessionWasClient = false;
            weather.ResetForSession();
            rainAudio.Apply(0f, false, false);
            visuals.ResetForSession();
        }

        public void SetRandomTransitionDuration(bool enabled)
        {
            if (!HasLocalAuthority() || settings.RandomTransitionDuration == enabled) return;
            settings.RandomTransitionDuration = enabled;
            EnsureTransitionDuration(cycle.Phase, enabled);
            cycle.Configure(settings.ToSnapshot());
            PublishManualChange();
        }

        public void SetManualTransitionDays(float days)
        {
            if (!HasLocalAuthority() || settings.RandomTransitionDuration) return;
            settings.TransitionDays = NormalizeTransitionDays(days);
            settings.TransitionSeason = CurrentSeason(cycle.Phase);
            cycle.Configure(settings.ToSnapshot());
            PublishManualChange();
        }

        private bool EnsureTransitionDuration(double phase, bool force = false)
        {
            var season = CurrentSeason(phase);
            var maximumDays = Math.Max(1, Math.Min(5, (int)Math.Floor(settings.DaysPerSeason)));
            if (!settings.RandomTransitionDuration)
            {
                var manualDays = NormalizeTransitionDays(settings.TransitionDays);
                var changed = settings.TransitionSeason != season ||
                    Math.Abs(settings.TransitionDays - manualDays) > 0.001f;
                settings.TransitionDays = manualDays;
                settings.TransitionSeason = season;
                return changed;
            }
            if (!force && settings.TransitionSeason == season &&
                settings.TransitionDays >= 1f && settings.TransitionDays <= maximumDays)
                return false;

            settings.TransitionDays = transitionRandom.Next(1, maximumDays + 1);
            settings.TransitionSeason = season;
            return true;
        }

        private float NormalizeTransitionDays(float value)
        {
            var maximumDays = Math.Max(1, Math.Min(5, (int)Math.Floor(settings.DaysPerSeason)));
            var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
            return Math.Max(1, Math.Min(maximumDays, rounded));
        }

        private static int CurrentSeason(double phase)
        {
            var season = (int)Math.Floor(phase) % 4;
            return season < 0 ? season + 4 : season;
        }

        private void OnNetworkStateReceived(SeasonNetworkState state)
        {
            if (!started || disposed || state == null || !state.IsValid() || network.IsAuthority) return;
            sessionWasClient = true;
            receivedNetworkState = true;
            settings.DaysPerSeason = state.DaysPerSeason;
            settings.RandomTransitionDuration = state.RandomTransitionDuration;
            settings.TransitionDays = state.TransitionDays;
            settings.TransitionSeason = state.TransitionSeason;
            networkRainIntensity = state.RainIntensity;
            networkWindVelocityX = state.WindVelocityX;
            networkWindVelocityZ = state.WindVelocityZ;
            networkSnowLightFactor = state.SnowLightFactor;
            currentState = SeasonState.FromNetwork(state);
        }

        private SeasonNetworkState CreateNetworkState(SeasonState state)
        {
            var wind = weather.SnowWindVelocity;
            return SeasonNetworkState.FromState(state, settings.DaysPerSeason,
                settings.TransitionDays, weather.RainIntensity, wind.x, wind.z,
                weather.SnowLightFactor, settings.TransitionSeason,
                settings.RandomTransitionDuration);
        }

        private ISeasonNetworkBridge CreateNetworkBridge(string modPath)
        {
            try
            {
                if (Type.GetType("MPAPI.MultiplayerAPI, MultiplayerAPI", false) == null) return new OfflineSeasonBridge();
                var adapterPath = Path.Combine(modPath, "DVSeasons.Multiplayer.dll");
                if (!File.Exists(adapterPath))
                {
                    entry.Logger.Warning("DVSeasons.Multiplayer.dll not found; using local mode.");
                    return new OfflineSeasonBridge();
                }
                var assembly = Assembly.LoadFrom(adapterPath);
                var type = assembly.GetType("DVSeasons.Multiplayer.MultiplayerSeasonBridge", true);
                return (ISeasonNetworkBridge)Activator.CreateInstance(type);
            }
            catch (Exception exception)
            {
                entry.Logger.Warning("Multiplayer adapter unavailable; using local mode: " + exception.Message);
                return new OfflineSeasonBridge();
            }
        }
    }
}
