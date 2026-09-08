using System;
using System.IO;
using System.Xml.Serialization;
using DVSeasons.Core;
using DVSeasons.Mod;
using UnityModManagerNet;
using Xunit;

namespace DVSeasons.Tests
{
    [CollectionDefinition("Season sessions", DisableParallelization = true)]
    public sealed class SeasonSessionCollection { }

    [Collection("Season sessions")]
    public sealed class SeasonSessionTests
    {
        [Fact]
        public void MenuAndLoadingDoNotAdvanceTheSavedWinter()
        {
            using (var session = new Session(3.2d))
            {
                session.Runtime.Start();
                session.Runtime.Tick(86400f);
                Assert.Equal(3.2d, session.Runtime.CurrentState.Phase, 6);
                session.Prepare(new SaveGameData());
                WeatherAdapter.Clock = new DateTime(2026, 1, 1);
                session.Runtime.Tick(86400f);
                WeatherAdapter.Clock = new DateTime(2026, 1, 9);
                session.Runtime.Tick(86400f);
                Assert.Equal(3.2d, session.Runtime.CurrentState.Phase, 6);
                WorldStreamingInit.FinishLoading();
                session.Runtime.Tick(0.02f);
                Assert.Equal(3.2d, session.Runtime.CurrentState.Phase, 6);
                WeatherAdapter.Clock = WeatherAdapter.Clock.Value.AddDays(1);
                session.Runtime.Tick(0.02f);
                Assert.Equal(3.2d + (1d / 14d), session.Runtime.CurrentState.Phase, 6);
            }
        }

        [Fact]
        public void NativeSaveRestoresExactPhaseAndRandomTransitionAfterRestart()
        {
            var save = new SaveGameData();
            SeasonSaveState expected;
            float expectedSnow;
            using (var first = new Session(2.9d))
            {
                first.Enter(save);
                first.Runtime.SetSeason(SeasonKind.Winter);
                WeatherAdapter.Clock = WeatherAdapter.Clock.Value.AddDays(12.1d);
                first.Runtime.Tick(0.02f);
                expectedSnow = first.Runtime.CurrentState.SnowAmount;
                first.Manager.Save();
                Assert.True(SeasonSaveData.TryRead(save, out expected));
                Assert.Equal(first.Runtime.CurrentState.Phase, expected.Phase);
                Assert.Equal(first.Settings.TransitionDays, expected.TransitionDays);
                UnloadWatcher.RequestUnload();
            }
            using (var second = new Session(0d))
            {
                second.Settings.DaysPerSeason = 90f;
                second.Enter(save);
                Assert.Equal(expected.Phase, second.Runtime.CurrentState.Phase);
                Assert.Equal(expectedSnow, second.Runtime.CurrentState.SnowAmount);
                Assert.Equal(expected.TransitionDays, second.Runtime.CurrentTransitionDays);
                Assert.Equal(expected.DaysPerSeason, second.Settings.DaysPerSeason);
                Assert.Equal(expected.TransitionSeason, second.Settings.TransitionSeason);
                Assert.True(second.Settings.RandomTransitionDuration);
            }
        }

        [Fact]
        public void ManualTransitionDurationIsKeptAcrossSeasonsAndSaveReloads()
        {
            var save = SavedCalendar(2.25d, 14f, 4f, true, false);
            using (var first = new Session(0d))
            {
                first.Enter(save);
                Assert.False(first.Settings.RandomTransitionDuration);
                first.Runtime.SetManualTransitionDays(5f);
                first.Runtime.AdvanceToNextSeason();
                Assert.Equal(SeasonKind.Winter, first.Runtime.CurrentState.Current);
                Assert.Equal(5f, first.Runtime.CurrentTransitionDays);
                first.Manager.Save();
            }
            using (var second = new Session(0d))
            {
                second.Enter(save);
                Assert.False(second.Settings.RandomTransitionDuration);
                Assert.Equal(5f, second.Runtime.CurrentTransitionDays);
                second.Runtime.SetRandomTransitionDuration(true);
                Assert.True(second.Settings.RandomTransitionDuration);
                Assert.InRange(second.Runtime.CurrentTransitionDays, 1f, 5f);
            }
        }

        [Fact]
        public void ReenteringInSameProcessRebasesClockAndRebindsSaveEvents()
        {
            var save = SavedCalendar(3.4d, 14f, 3f);
            using (var session = new Session(0d))
            {
                session.Enter(save);
                var phase = session.Runtime.CurrentState.Phase;
                var oldManager = session.Manager;
                var resetCount = SeasonVisualController.ResetCount;
                UnloadWatcher.RequestUnload();
                Assert.Equal(0, oldManager.SubscriberCount);
                Assert.True(SeasonVisualController.ResetCount > resetCount);
                session.Runtime.Tick(100000f);
                WeatherAdapter.Clock = WeatherAdapter.Clock.Value.AddDays(20);
                session.Enter(save);
                session.Runtime.OnSessionStart(); // duplicate UMM notification
                session.Runtime.Tick(0.02f);
                Assert.Equal(phase, session.Runtime.CurrentState.Phase);
                Assert.Equal(1, session.Manager.SubscriberCount);
                Assert.Equal(SeasonKind.Winter, SeasonVisualController.LastApplied.Current);
            }
        }

        [Fact]
        public void DifferentSavesKeepIndependentCalendars()
        {
            var winter = SavedCalendar(3.8d, 21f, 4f);
            var summer = SavedCalendar(1.25d, 7f, 2f, false);
            using (var session = new Session(0d))
            {
                session.Enter(winter);
                Assert.Equal(3.8d, session.Runtime.CurrentState.Phase);
                UnloadWatcher.RequestUnload();
                session.Enter(summer);
                Assert.Equal(1.25d, session.Runtime.CurrentState.Phase);
                Assert.Equal(7f, session.Settings.DaysPerSeason);
                Assert.False(session.Settings.AutomaticCycle);
                UnloadWatcher.RequestUnload();
                session.Enter(winter);
                Assert.Equal(3.8d, session.Runtime.CurrentState.Phase);
                Assert.Equal(21f, session.Settings.DaysPerSeason);
                Assert.Equal(4f, session.Runtime.CurrentTransitionDays);
                Assert.True(session.Settings.AutomaticCycle);
            }
        }

        [Fact]
        public void LegacySaveMigratesWinterFromUmmAndManualChangeCheckpointsImmediately()
        {
            var save = new SaveGameData();
            using (var session = new Session(3.0358305d))
            {
                session.Enter(save);
                Assert.Equal(SeasonKind.Winter, session.Runtime.CurrentState.Current);
                session.Runtime.SetSeason(SeasonKind.Autumn);
                var checkpoint = session.ReadCheckpoint();
                Assert.True(checkpoint.HasSavedPhase);
                Assert.Equal(2f, checkpoint.SavedPhase);
                session.Manager.Save();
                SeasonSaveState stored;
                Assert.True(SeasonSaveData.TryRead(save, out stored));
                Assert.Equal(2d, stored.Phase);
            }
        }

        [Fact]
        public void AutomaticCyclePeriodicallyCheckpointsWithoutUmmSaveButton()
        {
            using (var session = new Session(3d))
            {
                session.Enter(new SaveGameData());
                WeatherAdapter.Clock = WeatherAdapter.Clock.Value.AddDays(0.5d);
                UnityEngine.Time.realtimeSinceStartup = 31f;
                session.Runtime.Tick(0.02f);
                var checkpoint = session.ReadCheckpoint();
                Assert.Equal(session.Runtime.CurrentState.Phase, checkpoint.SavedPhase, 6);
                Assert.Equal(session.Runtime.CurrentTransitionDays, checkpoint.TransitionDays);
            }
        }

        [Fact]
        public void ClientNeverWritesHostCalendarOverItsLocalSaveOrSettings()
        {
            var localSave = SavedCalendar(1.25d, 14f, 3f);
            var network = new TestNetwork { IsSessionActive = true, IsAuthority = false };
            using (var session = new Session(1.25d, network))
            {
                session.Enter(localSave);
                var host = SeasonNetworkState.FromState(new SeasonState(3.9d, SeasonKind.Winter,
                    SeasonKind.Spring, 0.5f, 0.5f, 0f, 0.2f), 30f, 5f,
                    1f, 0f, 0f, 1f, 3, false);
                network.Receive(host);
                session.Runtime.Tick(0.02f);
                Assert.Equal(host.Phase, session.Runtime.CurrentState.Phase);
                session.Manager.Save();
                SeasonSaveState unchanged;
                Assert.True(SeasonSaveData.TryRead(localSave, out unchanged));
                Assert.Equal(1.25d, unchanged.Phase);
                session.Runtime.SaveSettings();
                var checkpoint = session.ReadCheckpoint();
                Assert.Equal(1.25f, checkpoint.SavedPhase);
                Assert.Equal(14f, checkpoint.DaysPerSeason);
                Assert.Equal(3f, checkpoint.TransitionDays);
                Assert.Equal(30f, session.Settings.DaysPerSeason); // display still uses host
                Assert.False(session.Settings.RandomTransitionDuration);
                Assert.True(checkpoint.RandomTransitionDuration);

                // Multiplayer tears down its singleton before the unload event.
                network.IsSessionActive = false;
                network.IsAuthority = true;
                UnloadWatcher.RequestUnload();
                Assert.Equal(1.25d, session.Runtime.CurrentState.Phase);
                Assert.Equal(14f, session.Settings.DaysPerSeason);
                Assert.Equal(1.25f, session.ReadCheckpoint().SavedPhase);
            }
        }

        [Fact]
        public void ToggleOffAndOnKeepsLivePhaseAndUsableVisualController()
        {
            using (var session = new Session(0d))
            {
                session.Enter(SavedCalendar(0d, 14f, 3f));
                session.Runtime.SetSeason(SeasonKind.Winter);
                session.Runtime.Stop();
                Assert.Equal(0, session.Manager.SubscriberCount);
                session.Runtime.Start();
                session.Runtime.Tick(0.02f);
                Assert.Equal(3d, session.Runtime.CurrentState.Phase);
                Assert.Equal(SeasonKind.Winter, SeasonVisualController.LastApplied.Current);
                Assert.Equal(1, session.Manager.SubscriberCount);
            }
        }

        [Fact]
        public void InvalidSaveCalendarFallsBackWithoutStoppingWorldLoad()
        {
            var save = SavedCalendar(2d, 14f, 3f);
            save.SetDouble("DVSeasons.Phase", double.NaN);
            using (var session = new Session(3d))
            {
                session.Enter(save);
                Assert.Equal(SeasonKind.Winter, session.Runtime.CurrentState.Current);
                session.Manager.Save();
                SeasonSaveState repaired;
                Assert.True(SeasonSaveData.TryRead(save, out repaired));
                Assert.Equal(3d, repaired.Phase);
            }
        }

        [Fact]
        public void SavePayloadPreservesPrecisionAndDoesNotTouchOtherMods()
        {
            var save = SavedCalendar(3.918237492817d, 30f, 5f, false, false);
            save.SetInt("OtherMod.Counter", 51);
            SeasonSaveState restored;
            Assert.True(SeasonSaveData.TryRead(save, out restored));
            SeasonSaveData.Write(save, restored);
            Assert.Equal(3.918237492817d, restored.Phase);
            Assert.Equal(51, save.GetInt("OtherMod.Counter"));
            Assert.False(restored.AutomaticCycle);
            Assert.False(restored.RandomTransitionDuration);
            save.SetInt("DVSeasons.Version", 999);
            Assert.False(SeasonSaveData.TryRead(save, out restored));
        }

        [Fact]
        public void VersionOneSaveMigratesToRandomTransitionMode()
        {
            var save = new SaveGameData();
            save.SetInt("DVSeasons.Version", 1);
            save.SetDouble("DVSeasons.Phase", 3.25d);
            save.SetBool("DVSeasons.AutomaticCycle", true);
            save.SetFloat("DVSeasons.DaysPerSeason", 14f);
            save.SetFloat("DVSeasons.TransitionDays", 3f);
            save.SetInt("DVSeasons.TransitionSeason", 3);
            SeasonSaveState restored;
            Assert.True(SeasonSaveData.TryRead(save, out restored));
            Assert.Equal(SeasonSaveState.CurrentVersion, restored.Version);
            Assert.True(restored.RandomTransitionDuration);
        }

        [Fact]
        public void LegacyUmmSettingsDefaultRandomTransitionModeToEnabled()
        {
            const string legacyXml = "<SeasonModSettings><TransitionDays>4</TransitionDays></SeasonModSettings>";
            using (var reader = new StringReader(legacyXml))
            {
                var restored = (SeasonModSettings)new XmlSerializer(typeof(SeasonModSettings)).Deserialize(reader);
                Assert.True(restored.RandomTransitionDuration);
                Assert.False(restored.SurfaceSnowEnabled);
                Assert.True(restored.WinterWaterIceEnabled);
                Assert.True(restored.FreezeWinterPuddles);
                Assert.False(restored.HideWinterPuddles);
                Assert.Equal(1f, restored.SurfaceSnowStrength);
                Assert.Equal(4f, restored.TransitionDays);
            }
        }

        [Fact]
        public void SurfaceSnowStrengthIsClampedToSupportedRange()
        {
            var settings = new SeasonModSettings { SurfaceSnowStrength = 20f };
            settings.Clamp();
            Assert.Equal(1.5f, settings.SurfaceSnowStrength);

            settings.SurfaceSnowStrength = -2f;
            settings.Clamp();
            Assert.Equal(0f, settings.SurfaceSnowStrength);
        }

        private static SaveGameData SavedCalendar(double phase, float days, float transition,
            bool automatic = true, bool randomTransitionDuration = true)
        {
            var save = new SaveGameData();
            SeasonSaveData.Write(save, new SeasonSaveState
            {
                Phase = phase, DaysPerSeason = days, TransitionDays = transition,
                TransitionSeason = (int)Math.Floor(phase), AutomaticCycle = automatic,
                RandomTransitionDuration = randomTransitionDuration
            });
            return save;
        }

        private sealed class Session : IDisposable
        {
            public readonly UnityModManager.ModEntry Entry = new UnityModManager.ModEntry();
            public readonly SeasonModSettings Settings;
            public readonly SeasonRuntime Runtime;
            public SaveGameManager Manager;

            public Session(double phase, TestNetwork network = null)
            {
                UnityEngine.Object.Manager = null;
                WorldStreamingInit.IsLoaded = false;
                UnloadWatcher.isUnloading = false;
                WeatherAdapter.Clock = new DateTime(2026, 1, 1);
                UnityEngine.Time.realtimeSinceStartup = 0f;
                SeasonVisualController.ResetCount = 0;
                SeasonVisualController.LastApplied = null;
                Settings = new SeasonModSettings
                {
                    HasSavedPhase = true, SavedPhase = (float)phase,
                    DaysPerSeason = 14f, TransitionDays = 3f, TransitionSeason = (int)Math.Floor(phase)
                };
                Runtime = new SeasonRuntime(Entry, Settings, network ?? new TestNetwork());
            }

            public void Prepare(SaveGameData data)
            {
                UnloadWatcher.isUnloading = false;
                WorldStreamingInit.IsLoaded = false;
                Manager = new SaveGameManager { data = data, IsNewSession = false };
                UnityEngine.Object.Manager = Manager;
                Runtime.OnSessionStart();
            }

            public void Enter(SaveGameData data)
            {
                Runtime.Start();
                Prepare(data);
                WorldStreamingInit.FinishLoading();
                Runtime.Tick(0.02f);
            }

            public SeasonModSettings ReadCheckpoint()
            {
                Assert.NotNull(Entry.SavedSettingsXml);
                using (var reader = new StringReader(Entry.SavedSettingsXml))
                    return (SeasonModSettings)new XmlSerializer(typeof(SeasonModSettings)).Deserialize(reader);
            }

            public void Dispose() { Runtime.Dispose(); UnityEngine.Object.Manager = null; }
        }

        private sealed class TestNetwork : ISeasonNetworkBridge
        {
            public bool IsAvailable { get { return true; } }
            public bool IsSessionActive { get; set; }
            public bool IsAuthority { get; set; } = true;
            public string Status { get { return "Test network"; } }
            public event Action<SeasonNetworkState> StateReceived;
            public void Receive(SeasonNetworkState state) { StateReceived?.Invoke(state); }
            public void Initialize(string id) { }
            public void SetEnabled(bool enabled) { }
            public void Publish(SeasonNetworkState state, bool force) { }
            public void RequestState() { }
            public void Dispose() { }
        }
    }
}
