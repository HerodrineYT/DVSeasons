using System;
using System.IO;
using System.Reflection;
using DVSeasons.Core;
using UnityModManagerNet;

namespace DVSeasons.Mod
{
    internal sealed class SeasonRuntime : IDisposable
    {
        private readonly UnityModManager.ModEntry entry;
        private readonly SeasonModSettings settings;
        private readonly WeatherAdapter weather = new WeatherAdapter();
        private readonly SeasonVisualController visuals;
        private readonly ISeasonNetworkBridge network;
        private readonly SeasonCycle cycle;
        private SeasonState currentState;
        private DateTime? lastGameDateTime;
        private bool started;
        private bool disposed;
        private bool receivedNetworkState;

        public SeasonRuntime(UnityModManager.ModEntry entry, SeasonModSettings settings)
        {
            this.entry = entry ?? throw new ArgumentNullException(nameof(entry));
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            visuals = new SeasonVisualController(entry.Path);
            settings.Clamp();
            var initialPhase = settings.HasSavedPhase ? settings.SavedPhase : settings.StartingSeason;
            cycle = new SeasonCycle(settings.ToSnapshot(), initialPhase);
            currentState = cycle.GetState();
            network = CreateNetworkBridge(entry.Path);
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

        public void Start()
        {
            if (disposed || started) return;
            started = true;
            receivedNetworkState = false;
            lastGameDateTime = null;
            network.SetEnabled(true);
            entry.Logger.Log("DV Seasons runtime started. " + network.Status);
        }

        public void Stop()
        {
            if (!started) return;
            started = false;
            network.SetEnabled(false);
            weather.ReleaseWetnessOverride();
            visuals.Dispose();
            lastGameDateTime = null;
            SavePhaseIfAuthoritative();
        }

        public void Tick(float deltaTime)
        {
            if (!started || disposed) return;
            settings.Clamp();
            weather.TickProbe();
            if (!network.IsSessionActive || network.IsAuthority)
            {
                cycle.Configure(settings.ToSnapshot());
                AdvanceCycle(deltaTime);
                currentState = cycle.GetState();
                network.Publish(SeasonNetworkState.FromState(currentState), false);
            }
            else if (!receivedNetworkState)
            {
                network.RequestState();
            }
            weather.ApplyWinterAdhesion(currentState, settings.WinterAdhesionEnabled,
                settings.RespectExternalWetnessOverride);
            visuals.Apply(currentState, weather.RainIntensity, settings);
        }

        public void SetSeason(SeasonKind season)
        {
            if (network.IsSessionActive && !network.IsAuthority) return;
            cycle.SetSeason(season);
            PublishManualChange();
        }

        public void AdvanceToNextSeason()
        {
            if (network.IsSessionActive && !network.IsAuthority) return;
            cycle.AdvanceToNextSeason();
            PublishManualChange();
        }

        public void SavePhaseIfAuthoritative()
        {
            if (network.IsSessionActive && !network.IsAuthority) return;
            settings.SavedPhase = (float)cycle.Phase;
            settings.HasSavedPhase = true;
        }

        public void Dispose()
        {
            if (disposed) return;
            Stop();
            network.StateReceived -= OnNetworkStateReceived;
            network.Dispose();
            weather.Dispose();
            visuals.Dispose();
            disposed = true;
        }

        private void AdvanceCycle(float deltaTime)
        {
            DateTime gameTime;
            if (weather.TryGetGameDateTime(out gameTime))
            {
                if (lastGameDateTime.HasValue)
                {
                    var days = (gameTime - lastGameDateTime.Value).TotalDays;
                    if (days >= 0d && days <= 31d) cycle.AdvanceGameDays(days);
                }
                lastGameDateTime = gameTime;
                return;
            }
            var fallbackDays = deltaTime / (Math.Max(1f, settings.FallbackMinutesPerGameDay) * 60f);
            cycle.AdvanceGameDays(fallbackDays);
        }

        private void PublishManualChange()
        {
            currentState = cycle.GetState();
            network.Publish(SeasonNetworkState.FromState(currentState), true);
            SavePhaseIfAuthoritative();
        }

        private void OnNetworkStateReceived(SeasonNetworkState state)
        {
            if (state == null || !state.IsValid()) return;
            receivedNetworkState = true;
            currentState = SeasonState.FromNetwork(state);
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
