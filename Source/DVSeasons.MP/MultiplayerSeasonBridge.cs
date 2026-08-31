using System;
using DVSeasons.Core;
using MPAPI;
using MPAPI.Interfaces;
using MPAPI.Types;
using UnityEngine;

namespace DVSeasons.Multiplayer
{
    public sealed class MultiplayerSeasonBridge : ISeasonNetworkBridge
    {
        private string modId;
        private bool initialized;
        private bool enabled;
        private bool disposed;
        private bool serverRegistered;
        private bool clientRegistered;
        private uint sequence;
        private uint lastReceivedSequence;
        private float nextBroadcastTime;
        private float nextRequestTime;
        private SeasonNetworkState lastState;
        private bool receivedStateLogged;
        private bool precipitationStateLogged;
        private bool lastPrecipitationActive;

        public bool IsAvailable { get { return MultiplayerAPI.IsMultiplayerLoaded; } }
        public bool IsSessionActive
        {
            get
            {
                var api = MultiplayerAPI.Instance;
                return api != null && api.IsConnected && !api.IsSinglePlayer;
            }
        }
        public bool IsAuthority { get { return !IsSessionActive || (MultiplayerAPI.Instance != null && MultiplayerAPI.Instance.IsHost); } }
        public string Status
        {
            get
            {
                if (!IsAvailable) return "Multiplayer не установлен — локальный режим";
                if (!IsSessionActive) return "Multiplayer доступен — одиночная сессия";
                return IsAuthority ? "Multiplayer: хост управляет сезонами и погодой" : "Multiplayer: сезон и погода синхронизированы с хостом";
            }
        }

        public event Action<SeasonNetworkState> StateReceived;

        public void Initialize(string id)
        {
            if (initialized) return;
            modId = string.IsNullOrEmpty(id) ? "DVSeasons" : id;
            initialized = true;
            MultiplayerAPI.ServerStarted += OnServerStarted;
            MultiplayerAPI.ServerStopped += OnServerStopped;
            MultiplayerAPI.ClientStarted += OnClientStarted;
            MultiplayerAPI.ClientStopped += OnClientStopped;
            ConfigureCompatibility();
            if (MultiplayerAPI.Server != null) RegisterServer(MultiplayerAPI.Server);
            if (MultiplayerAPI.Client != null) RegisterClient(MultiplayerAPI.Client);
        }

        public void SetEnabled(bool value)
        {
            enabled = value;
            if (enabled && IsSessionActive && !IsAuthority) RequestState();
        }

        public void Publish(SeasonNetworkState state, bool force)
        {
            if (!enabled || disposed || state == null || !IsSessionActive || !IsAuthority || MultiplayerAPI.Server == null) return;
            lastState = Copy(state);
            if (!force && Time.realtimeSinceStartup < nextBroadcastTime) return;
            // Weather-editor changes need to reach clients quickly enough that the
            // first visible flakes do not lag several seconds behind the host.
            nextBroadcastTime = Time.realtimeSinceStartup + 1f;
            SendToAll();
        }

        public void RequestState()
        {
            if (!enabled || disposed || !IsSessionActive || IsAuthority || MultiplayerAPI.Client == null) return;
            if (Time.realtimeSinceStartup < nextRequestTime) return;
            nextRequestTime = Time.realtimeSinceStartup + 2f;
            MultiplayerAPI.Client.SendSerializablePacketToServer(new SeasonStateRequestPacket(), true);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            enabled = false;
            if (initialized)
            {
                MultiplayerAPI.ServerStarted -= OnServerStarted;
                MultiplayerAPI.ServerStopped -= OnServerStopped;
                MultiplayerAPI.ClientStarted -= OnClientStarted;
                MultiplayerAPI.ClientStopped -= OnClientStopped;
            }
        }

        private void OnServerStarted(IServer server) { serverRegistered = false; ConfigureCompatibility(); RegisterServer(server); }
        private void OnClientStarted(IClient client) { clientRegistered = false; lastReceivedSequence = 0; nextRequestTime = 0f; receivedStateLogged = false; precipitationStateLogged = false; ConfigureCompatibility(); RegisterClient(client); if (enabled) RequestState(); }
        private void OnServerStopped() { serverRegistered = false; sequence = 0; lastState = null; }
        private void OnClientStopped() { clientRegistered = false; lastReceivedSequence = 0; }

        private void RegisterServer(IServer server)
        {
            if (serverRegistered || server == null) return;
            serverRegistered = true;
            server.RegisterSerializablePacket<SeasonStateRequestPacket>(OnStateRequested);
            server.OnPlayerReady += OnPlayerReady;
            Debug.Log("[DVSeasons MP] Server packet handler registered (protocol " + SeasonNetworkState.CurrentProtocol + ").");
        }

        private void RegisterClient(IClient client)
        {
            if (clientRegistered || client == null) return;
            clientRegistered = true;
            client.RegisterSerializablePacket<SeasonStatePacket>(OnStatePacket);
            Debug.Log("[DVSeasons MP] Client packet handler registered (protocol " + SeasonNetworkState.CurrentProtocol + ").");
        }

        private void ConfigureCompatibility()
        {
            if (MultiplayerAPI.Instance != null) MultiplayerAPI.Instance.SetModCompatibility(modId, MultiplayerCompatibility.All);
        }

        private void OnStateRequested(SeasonStateRequestPacket request, IPlayer player)
        {
            if (!enabled || request == null) return;
            if (request.Protocol != SeasonNetworkState.CurrentProtocol)
            {
                Debug.LogWarning("[DVSeasons MP] Ignored season request with incompatible protocol " + request.Protocol + ".");
                return;
            }
            SendToPlayer(player);
        }

        private void OnPlayerReady(IPlayer player) { if (enabled) SendToPlayer(player); }

        private void OnStatePacket(SeasonStatePacket packet)
        {
            if (!enabled || packet == null || packet.State == null) return;
            if (!packet.State.IsValid())
            {
                Debug.LogWarning("[DVSeasons MP] Ignored invalid season state (protocol " + packet.State.Protocol +
                    ", days " + packet.State.DaysPerSeason + ", transition season " +
                    packet.State.TransitionSeason + ", transition days " +
                    packet.State.TransitionDays + ").");
                return;
            }
            if (packet.State.Sequence <= lastReceivedSequence) return;
            lastReceivedSequence = packet.State.Sequence;
            if (!receivedStateLogged)
            {
                receivedStateLogged = true;
                Debug.Log("[DVSeasons MP] Host season state received: " + packet.State.Current + " -> " +
                    packet.State.Next + ", transition " + (packet.State.Transition * 100f).ToString("F0") +
                    "%, selected duration " + packet.State.TransitionDays.ToString("F0") +
                    " day(s) for season " + packet.State.TransitionSeason + ", rain " +
                    packet.State.RainIntensity.ToString("F2") + ".");
            }
            var precipitationActive = packet.State.RainIntensity > 0.01f;
            if (!precipitationStateLogged || precipitationActive != lastPrecipitationActive)
            {
                precipitationStateLogged = true;
                lastPrecipitationActive = precipitationActive;
                Debug.Log("[DVSeasons MP] Host precipitation " +
                    (precipitationActive ? "started" : "stopped") + " (rain " +
                    packet.State.RainIntensity.ToString("F2") + ").");
            }
            var handler = StateReceived;
            if (handler != null) handler(packet.State);
        }

        private void SendToAll()
        {
            if (lastState == null || MultiplayerAPI.Server == null) return;
            lastState.Sequence = ++sequence;
            MultiplayerAPI.Server.SendSerializablePacketToAll(new SeasonStatePacket { State = Copy(lastState) }, true, true, null);
        }

        private void SendToPlayer(IPlayer player)
        {
            if (lastState == null || player == null || player.IsHost || MultiplayerAPI.Server == null) return;
            var copy = Copy(lastState);
            copy.Sequence = ++sequence;
            MultiplayerAPI.Server.SendSerializablePacketToPlayer(new SeasonStatePacket { State = copy }, player, true);
        }

        private static SeasonNetworkState Copy(SeasonNetworkState source)
        {
            return new SeasonNetworkState
            {
                Protocol = source.Protocol,
                Sequence = source.Sequence,
                Phase = source.Phase,
                Current = source.Current,
                Next = source.Next,
                Transition = source.Transition,
                SnowAmount = source.SnowAmount,
                TemperatureCelsius = source.TemperatureCelsius,
                WinterWetnessEquivalent = source.WinterWetnessEquivalent,
                DaysPerSeason = source.DaysPerSeason,
                RandomTransitionDuration = source.RandomTransitionDuration,
                TransitionDays = source.TransitionDays,
                TransitionSeason = source.TransitionSeason,
                RainIntensity = source.RainIntensity,
                WindVelocityX = source.WindVelocityX,
                WindVelocityZ = source.WindVelocityZ,
                SnowLightFactor = source.SnowLightFactor
            };
        }
    }
}
