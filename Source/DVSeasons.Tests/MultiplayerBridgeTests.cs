using System;
using System.Collections.Generic;
using System.IO;
using DVSeasons.Core;
using DVSeasons.Multiplayer;
using MPAPI;
using MPAPI.Interfaces;
using MPAPI.Interfaces.Packets;
using Xunit;

// Engine/API boundaries only: all tested packets and bridge callbacks are the
// production sources. Signatures match the installed Multiplayer 0.1.16 API.
public sealed class TrainCar { public string CarGUID; public bool IsLoco; }
namespace UnityEngine { internal static class Debug { public static void Log(string text) { } public static void LogWarning(string text) { } } }
namespace MPAPI.Types { public enum MultiplayerCompatibility { All } }
namespace MPAPI.Interfaces
{
    public interface IPlayer { byte PlayerId { get; } bool IsLoaded { get; } bool IsHost { get; } bool IsOnCar { get; } TrainCar OccupiedCar { get; } }
    public interface IServer
    {
        IPlayer GetPlayer(byte id);
        event Action<IPlayer> OnPlayerReady;
        event Action<IPlayer> OnPlayerDisconnected;
        void RegisterSerializablePacket<T>(Action<T,IPlayer> handler) where T : class, ISerializablePacket, new();
        void SendSerializablePacketToAll<T>(T packet, bool reliable = true, bool excludeSelf = false, IPlayer excludePlayer = null) where T : class, ISerializablePacket, new();
        void SendSerializablePacketToPlayer<T>(T packet, IPlayer player, bool reliable = true) where T : class, ISerializablePacket, new();
    }
    public interface IClient
    {
        void RegisterSerializablePacket<T>(Action<T> handler) where T : class, ISerializablePacket, new();
        void SendSerializablePacketToServer<T>(T packet, bool reliable = true) where T : class, ISerializablePacket, new();
    }
}
namespace MPAPI
{
    public sealed class TestApi
    {
        public bool IsConnected = true, IsHost, IsSinglePlayer;
        public void SetModCompatibility(string id, Types.MultiplayerCompatibility compatibility) { }
    }
    internal sealed class ApiProcess
    {
        public TestApi Api = new TestApi();
        public IServer Server; public IClient Client;
        public Action<IServer> ServerStarted;
        public Action<IClient> ClientStarted;
        public Action ServerStopped, ClientStopped;
        public void Run(Action action)
        {
            var previous = MultiplayerAPI.Process;
            MultiplayerAPI.Process = this;
            try { action(); } finally { MultiplayerAPI.Process = previous; }
        }
    }
    internal static class MultiplayerAPI
    {
        public static ApiProcess Process;
        public static TestApi Instance => Process.Api;
        public static IServer Server => Process.Server;
        public static IClient Client => Process.Client;
        public static bool IsMultiplayerLoaded => true;
        public static event Action<IServer> ServerStarted { add { Process.ServerStarted += value; } remove { Process.ServerStarted -= value; } }
        public static event Action<IClient> ClientStarted { add { Process.ClientStarted += value; } remove { Process.ClientStarted -= value; } }
        public static event Action ServerStopped { add { Process.ServerStopped += value; } remove { Process.ServerStopped -= value; } }
        public static event Action ClientStopped { add { Process.ClientStopped += value; } remove { Process.ClientStopped -= value; } }
    }
}

namespace DVSeasons.Tests
{
    [Collection("Season sessions")]
    public sealed class MultiplayerBridgeTests
    {
        private sealed class Player : IPlayer
        {
            public byte PlayerId { get; set; }
            public bool IsLoaded { get; set; }
            public bool IsHost => false;
            public bool IsOnCar { get; set; } = true;
            public TrainCar OccupiedCar { get; set; } = new TrainCar { CarGUID = "de6-a", IsLoco = true };
        }
        private static T Wire<T>(T packet) where T : class, ISerializablePacket, new()
        {
            using (var stream = new MemoryStream())
            {
                packet.Serialize(new BinaryWriter(stream)); stream.Position = 0;
                var result = new T(); result.Deserialize(new BinaryReader(stream));
                return result;
            }
        }
        private sealed class Client : IClient
        {
            public readonly ApiProcess Process = new ApiProcess();
            public readonly Player Player = new Player();
            public readonly MultiplayerSeasonBridge Bridge = new MultiplayerSeasonBridge();
            public readonly List<SeasonNetworkState> Seasons = new List<SeasonNetworkState>();
            public readonly List<Dictionary<string,float>> Heaters = new List<Dictionary<string,float>>();
            private readonly Server server;
            private readonly Dictionary<Type,Delegate> handlers = new Dictionary<Type,Delegate>();
            public Client(Server server, byte id)
            {
                Player.PlayerId = id;
                this.server = server; Process.Client = this;
                Bridge.StateReceived += Seasons.Add;
                Bridge.HeatersReceived += states => Heaters.Add(new Dictionary<string,float>(states));
                Process.Run(() => { Bridge.Initialize("DVSeasons"); Bridge.SetEnabled(true); });
            }
            public void RegisterSerializablePacket<T>(Action<T> callback) where T : class, ISerializablePacket, new() { handlers[typeof(T)] = callback; }
            public void SendSerializablePacketToServer<T>(T packet, bool reliable = true) where T : class, ISerializablePacket, new()
            { Assert.True(reliable); server.Receive(Wire(packet), Player); }
            public void Receive<T>(T packet) where T : class, ISerializablePacket, new()
            { if (handlers.TryGetValue(typeof(T), out var handler)) Process.Run(() => ((Action<T>)handler)(Wire(packet))); }
        }
        private sealed class Server : IServer, IDisposable
        {
            public readonly ApiProcess Process = new ApiProcess { Api = new TestApi { IsHost = true } };
            public readonly MultiplayerSeasonBridge Bridge = new MultiplayerSeasonBridge();
            public readonly List<Client> Clients = new List<Client>();
            public readonly List<(string,float)> Changes = new List<(string,float)>();
            private readonly Dictionary<Type,Delegate> handlers = new Dictionary<Type,Delegate>();
            public event Action<IPlayer> OnPlayerReady;
            public event Action<IPlayer> OnPlayerDisconnected;
            public int ReadyListeners => OnPlayerReady?.GetInvocationList().Length ?? 0;
            public Server()
            {
                UnityEngine.Time.realtimeSinceStartup = 0;
                Process.Server = this;
                Bridge.HeaterChanged += (id, level) => Changes.Add((id, level));
                Process.Run(() => { Bridge.Initialize("DVSeasons"); Bridge.SetEnabled(true); });
            }
            public Client AddClient()
            {
                var client = new Client(this, (byte)(Clients.Count + 1)); Clients.Add(client);
                // Constructor's eager request precedes this transport mapping.
                Process.Run(() => Receive(new SeasonStateRequestPacket(), client.Player));
                return client;
            }
            public void RegisterSerializablePacket<T>(Action<T,IPlayer> callback) where T : class, ISerializablePacket, new() { handlers[typeof(T)] = callback; }
            public void Receive<T>(T packet, IPlayer player) where T : class, ISerializablePacket, new()
            {
                // Match NetworkServer.RegisterExternalSerializablePacket: fresh
                // wrapper each time, including requests from the same player.
                var wrapper = new Player { PlayerId = player.PlayerId, IsLoaded = player.IsLoaded,
                    IsOnCar = player.IsOnCar, OccupiedCar = player.OccupiedCar };
                Process.Run(() => ((Action<T,IPlayer>)handlers[typeof(T)])(Wire(packet), wrapper));
            }
            public IPlayer GetPlayer(byte id) => Clients.Find(c => c.Player.PlayerId == id)?.Player;
            public void SendSerializablePacketToAll<T>(T packet, bool reliable = true, bool excludeSelf = false, IPlayer excludePlayer = null) where T : class, ISerializablePacket, new()
            { Assert.True(reliable); Assert.True(excludeSelf); foreach (var c in Clients) if (c.Player != excludePlayer) c.Receive(packet); }
            public void SendSerializablePacketToPlayer<T>(T packet, IPlayer player, bool reliable = true) where T : class, ISerializablePacket, new()
            { Assert.True(reliable); Clients.Find(c => c.Player.PlayerId == player.PlayerId)?.Receive(packet); }
            public void Ready(Client client) { client.Player.IsLoaded = true; Process.Run(() => OnPlayerReady?.Invoke(client.Player)); }
            public void Disconnect(Client client) { Process.Run(() => OnPlayerDisconnected?.Invoke(client.Player)); }
            public void Publish(SeasonNetworkState state) { Process.Run(() => Bridge.Publish(state, true)); }
            public void Heaters(float level) { Process.Run(() => Bridge.SetHeaters(new Dictionary<string,float> { ["de6-a"] = level }, true)); }
            public void Dispose()
            {
                Process.Run(() => Bridge.Dispose());
                foreach (var c in Clients) c.Process.Run(() => c.Bridge.Dispose());
                MultiplayerAPI.Process = null;
            }
        }
        private static SeasonNetworkState Winter(uint revision = 0)
        {
            var state = SeasonNetworkState.FromState(new SeasonState(3d, SeasonKind.Winter,
                SeasonKind.Spring, 0f, 1f, -25f, .4f), 14f, 3f, .8f, 3f, -2f, .2f);
            state.SeasonSelectionRevision = revision;
            state.HasSurfaceSnowCoverage = true; state.SurfaceSnowCoverage = .6f;
            return state;
        }
        [Fact]
        public void ColdStartRuleAndHintsReachClientsAndLateJoinWithOwnedSnapshot()
        {
            using(var host=new Server())
            {
                var first=host.AddClient();var second=host.AddClient();var state=Winter();
                state.IgnoreVanillaColdStarts=true;
                state.ColdStarts=new[]{new ColdStartHintState{CarId="custom-diesel",Stage=ColdStartHintStage.Starter,RemainingSeconds=9}};
                host.Publish(state);var expected=state.ColdStarts[0];state.ColdStarts[0].RemainingSeconds=0;
                Assert.True(first.Seasons[first.Seasons.Count-1].IgnoreVanillaColdStarts);
                Assert.Equal(expected,second.Seasons[second.Seasons.Count-1].ColdStarts[0]);
                var late=host.AddClient();host.Ready(late);
                Assert.Equal(expected,late.Seasons[late.Seasons.Count-1].ColdStarts[0]);
                host.Publish(Winter());
                Assert.Empty(first.Seasons[first.Seasons.Count-1].ColdStarts);
                Assert.False(first.Seasons[first.Seasons.Count-1].IgnoreVanillaColdStarts);
            }
        }
        [Fact]
        public void ThermalSnapshotsReachTwoClientsAndLateJoinAndCannotBeMutatedAfterPublish()
        {
            using (var host = new Server())
            {
                var first = host.AddClient(); var second = host.AddClient();
                var packet = VehicleThermalNetworkTests.Packet();
                var expected = (VehicleThermalNetworkState[])packet.VehicleThermal.Clone();
                host.Publish(packet);
                Assert.Equal(expected, first.Seasons[first.Seasons.Count - 1].VehicleThermal);
                Assert.Equal(expected, second.Seasons[second.Seasons.Count - 1].VehicleThermal);
                packet.VehicleThermal[0].Cabin = 140;
                host.Publish(Winter());
                Assert.False(first.Seasons[first.Seasons.Count - 1].HasThermalSnapshot);
                var late = host.AddClient(); host.Ready(late);
                Assert.True(late.Seasons[late.Seasons.Count - 1].HasThermalSnapshot);
                Assert.Equal(expected, late.Seasons[late.Seasons.Count - 1].VehicleThermal);
                var cleared = Winter(); cleared.HasThermalSnapshot = true;
                host.Publish(cleared);
                var next = host.AddClient(); host.Ready(next);
                Assert.Empty(next.Seasons[next.Seasons.Count - 1].VehicleThermal);
            }
        }
        [Fact]
        public void LateJoinReceivesAllFourFacesAfterWeatherOnlyEditsAndSnapshotOwnsItsArray()
        {
            using (var host = new Server())
            {
                var first = host.AddClient();
                var state = Winter();
                state.HasSideSnowSnapshot = true;
                state.SideSnow = new[] { new VehicleSideSnowNetworkState
                {
                    CarId = "de6-a", PositiveX = .2f, NegativeX = .4f, PositiveZ = .8f, NegativeZ = 1f
                } };
                host.Publish(state);
                Assert.True(first.Seasons[first.Seasons.Count - 1].HasSideSnowSnapshot);
                state.SideSnow[0] = new VehicleSideSnowNetworkState { CarId = "mutated", PositiveX = 1f };
                host.Publish(Winter()); // A weather edit carries no new fleet history.
                Assert.False(first.Seasons[first.Seasons.Count - 1].HasSideSnowSnapshot);
                var late = host.AddClient();
                host.Ready(late);
                var received = late.Seasons[late.Seasons.Count - 1];
                Assert.True(received.HasSideSnowSnapshot);
                var snow = Assert.Single(received.SideSnow);
                Assert.Equal("de6-a", snow.CarId);
                Assert.InRange(snow.PositiveX, .1999f, .2001f);
                Assert.InRange(snow.NegativeX, .3999f, .4001f);
                Assert.InRange(snow.PositiveZ, .7999f, .8001f);
                Assert.Equal(1f, snow.NegativeZ);

                var cleared = Winter(); cleared.HasSideSnowSnapshot = true;
                host.Publish(cleared);
                var afterClear = host.AddClient(); host.Ready(afterClear);
                Assert.Empty(afterClear.Seasons[afterClear.Seasons.Count - 1].SideSnow);
            }
        }

        [Fact]
        public void HostAndTwoClientsReceiveWeatherSnowAndIndependentHeaterChanges()
        {
            using (var host = new Server())
            {
                host.Publish(Winter(9));
                var first = host.AddClient(); var second = host.AddClient();
                host.Heaters(0); host.Ready(first); host.Ready(second);
                foreach (var c in host.Clients)
                {
                    var state = c.Seasons[c.Seasons.Count - 1];
                    Assert.Equal(9u, state.SeasonSelectionRevision);
                    Assert.Equal(.6f, state.SurfaceSnowCoverage);
                    Assert.Equal(.8f, state.RainIntensity);
                    Assert.Equal(-25f, state.TemperatureCelsius);
                    Assert.Equal(-2f, state.WindVelocityZ);
                }
                first.Process.Run(() => first.Bridge.RequestHeaterChange("de6-a", 1));
                Assert.Single(host.Changes);
                host.Heaters(1); // Game service confirms and broadcasts the request.
                Assert.Equal(1f, first.Heaters[first.Heaters.Count - 1]["de6-a"]);
                Assert.Equal(1f, second.Heaters[second.Heaters.Count - 1]["de6-a"]);
                // A different client can switch the same occupied cab back off;
                // every request arrives through a new MP player wrapper.
                second.Process.Run(() => second.Bridge.RequestHeaterChange("de6-a", 0));
                Assert.Equal(2, host.Changes.Count);
                host.Heaters(0);
                Assert.Equal(0f, first.Heaters[first.Heaters.Count - 1]["de6-a"]);
                Assert.Equal(0f, second.Heaters[second.Heaters.Count - 1]["de6-a"]);
                second.Process.Run(() => second.Bridge.RequestHeaterChange("someone-elses-car", 0));
                Assert.Equal(2, host.Changes.Count);
            }
        }
        [Fact]
        public void ReadyResendsHeatersChangedDuringLoadingAndDisconnectStopsDelivery()
        {
            using (var host = new Server())
            {
                host.Publish(Winter()); host.Heaters(0);
                var client = host.AddClient();
                Assert.Equal(0f, client.Heaters[client.Heaters.Count - 1]["de6-a"]);
                int beforeReady = client.Heaters.Count;
                host.Heaters(1);
                Assert.Equal(beforeReady, client.Heaters.Count);
                host.Ready(client);
                Assert.Equal(1f, client.Heaters[client.Heaters.Count - 1]["de6-a"]);
                host.Disconnect(client);
                int beforeDisconnect = client.Heaters.Count;
                host.Heaters(0);
                Assert.Equal(beforeDisconnect, client.Heaters.Count);
                client.Process.Run(() => client.Bridge.RequestHeaterChange("de6-a", 0));
                Assert.Empty(host.Changes);
            }
        }
        [Fact]
        public void DuplicatePacketsAreIgnoredAndReconnectAcceptsRestartedHostSequence()
        {
            using (var host = new Server())
            {
                var client = host.AddClient(); host.Publish(Winter(2));
                var previous = client.Seasons[client.Seasons.Count - 1];
                client.Receive(new SeasonStatePacket { State = previous });
                Assert.Single(client.Seasons);
                var future = Winter(3); future.Sequence = previous.Sequence + 10;
                client.Receive(new SeasonStatePacket { State = future });
                client.Receive(new SeasonStatePacket { State = previous });
                Assert.Equal(2, client.Seasons.Count);
                client.Process.Run(() => { client.Process.ClientStopped?.Invoke(); client.Process.ClientStarted?.Invoke(client); });
                Assert.Equal(previous.Sequence + 1, client.Seasons[client.Seasons.Count - 1].Sequence);
                client.Process.Run(() => client.Bridge.Dispose());
                future.Sequence += 10;
                int count = client.Seasons.Count;
                client.Receive(new SeasonStatePacket { State = future });
                Assert.Equal(count, client.Seasons.Count);
            }
        }
        [Fact]
        public void WeatherIsCopiedForLateJoinersAndClearedMasksReachExistingClients()
        {
            using (var host = new Server())
            {
                var state = Winter();
                state.Weather = new WeatherNetworkState
                {
                    Available = true, Overrides = 1, Values = new[] { .8f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },
                    RealDateTimeTicks = new DateTime(2026, 9, 16).Ticks,
                    TimeRevision = 3, SeasonalPrecipitation = true
                };
                host.Publish(state);
                state.Weather.Values[0] = .2f;
                state.Weather.Overrides = 0;
                var first = host.AddClient();
                var received = first.Seasons[first.Seasons.Count - 1].Weather;
                Assert.Equal(.8f, received.Values[0]);
                Assert.Equal((ushort)1, received.Overrides);
                Assert.Equal(3u, received.TimeRevision);
                Assert.True(received.SeasonalPrecipitation);
                received.Values[0] = .4f;
                var second = host.AddClient();
                Assert.Equal(.8f, second.Seasons[second.Seasons.Count - 1].Weather.Values[0]);
                host.Publish(state);
                foreach (var client in host.Clients)
                    Assert.Equal((ushort)0, client.Seasons[client.Seasons.Count - 1].Weather.Overrides);
            }
        }

        [Fact]
        public void ServerHooksAreRemovedOnStopAndDispose()
        {
            using (var host = new Server())
            {
                Assert.Equal(1, host.ReadyListeners);
                host.Process.Run(() => host.Process.ServerStopped?.Invoke());
                Assert.Equal(0, host.ReadyListeners);
                host.Process.Run(() => host.Process.ServerStarted?.Invoke(host));
                Assert.Equal(1, host.ReadyListeners);
                host.Process.Run(() => host.Bridge.Dispose());
                Assert.Equal(0, host.ReadyListeners);
            }
        }
    }
}
