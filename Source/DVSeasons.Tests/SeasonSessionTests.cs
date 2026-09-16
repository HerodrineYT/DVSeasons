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
                Assert.Same(winter,SeasonVisualController.SnowRestoredFrom);
                session.Manager.Save();
                Assert.Same(winter,SeasonVisualController.SnowWrittenTo);
                UnloadWatcher.RequestUnload();
                session.Enter(summer);
                Assert.Same(summer,SeasonVisualController.SnowRestoredFrom);
                session.Manager.Save();
                Assert.Same(summer,SeasonVisualController.SnowWrittenTo);
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
        public void ManualSeasonSelectionClearsStaleEffectsAndUpdatesSunImmediately()
        {
            using (var session = new Session(3d))
            {
                session.Enter(SavedCalendar(3d, 14f, 3f));
                WeatherAdapter.ResetAppliedOverrides();
                WeatherAdapter.DayMinutes = 3;
                WeatherAdapter.DayOverridden = WeatherAdapter.WetnessOverridden = WeatherAdapter.ThunderOverridden = true;
                session.Runtime.SetSeason(SeasonKind.Summer);
                Assert.Equal(60, WeatherAdapter.DayMinutes);
                Assert.False(WeatherAdapter.DayOverridden);
                Assert.False(WeatherAdapter.WetnessOverridden);
                Assert.False(WeatherAdapter.ThunderOverridden);
                Assert.Equal(1, WeatherAdapter.SeasonResetCount);
                Assert.Equal(1, WeatherAdapter.SeasonRefreshCount);
                Assert.Equal(SeasonKind.Summer, WeatherAdapter.LastClimateState.Current);

                session.Runtime.Tick(.02f);
                Assert.Equal(1, WeatherAdapter.SeasonResetCount);
                session.Runtime.SetSeason(SeasonKind.Summer);
                Assert.Equal(2, WeatherAdapter.SeasonResetCount);
            }
        }

        [Fact]
        public void AutomaticBoundaryResetsOnceWithoutResettingCalendarClock()
        {
            using (var session = new Session(3.99d))
            {
                session.Enter(SavedCalendar(3.99d, 14f, 3f));
                WeatherAdapter.ResetAppliedOverrides();
                WeatherAdapter.Clock = WeatherAdapter.Clock.Value.AddDays(1);
                session.Runtime.Tick(.02f);
                Assert.Equal(1, WeatherAdapter.SeasonResetCount);
                Assert.Equal(SeasonKind.Spring, WeatherAdapter.LastClimateState.Current);
                var phase = session.Runtime.CurrentState.Phase;
                WeatherAdapter.Clock = WeatherAdapter.Clock.Value.AddDays(1);
                session.Runtime.Tick(.02f);
                Assert.Equal(1, WeatherAdapter.SeasonResetCount);
                Assert.Equal(phase + 1d / 14d, session.Runtime.CurrentState.Phase, 8);
            }
        }

        [Fact]
        public void DirectSeasonSelectionUpdatesOwnedWeatherOverridesImmediately()
        {
            using (var session = new Session(3d))
            {
                session.Enter(SavedCalendar(3d, 14f, 3f));
                WeatherAdapter.ResetAppliedOverrides();

                session.Runtime.SetSeason(SeasonKind.Spring);
                Assert.Equal(1, WeatherAdapter.AdhesionApplyCount);
                Assert.Equal(1, WeatherAdapter.ThunderApplyCount);
                Assert.Equal(0f, WeatherAdapter.LastAdhesionState.WinterWetnessEquivalent);
                Assert.Equal(0f, WeatherAdapter.LastThunderState.SnowAmount);

                session.Runtime.SetSeason(SeasonKind.Winter);
                Assert.Equal(2, WeatherAdapter.AdhesionApplyCount);
                Assert.Equal(2, WeatherAdapter.ThunderApplyCount);
                Assert.True(WeatherAdapter.LastAdhesionState.WinterWetnessEquivalent > 0f);
                Assert.True(WeatherAdapter.LastThunderState.SnowAmount > 0f);
            }
        }

        [Fact]
        public void AdvancingSeasonUpdatesOwnedWeatherOverridesImmediately()
        {
            using (var session = new Session(2d))
            {
                session.Enter(SavedCalendar(2d, 14f, 3f));
                WeatherAdapter.ResetAppliedOverrides();

                session.Runtime.AdvanceToNextSeason();
                Assert.Equal(SeasonKind.Winter, WeatherAdapter.LastAdhesionState.Current);
                Assert.True(WeatherAdapter.LastAdhesionState.WinterWetnessEquivalent > 0f);
                Assert.True(WeatherAdapter.LastThunderState.SnowAmount > 0f);

                session.Runtime.AdvanceToNextSeason();
                Assert.Equal(SeasonKind.Spring, WeatherAdapter.LastAdhesionState.Current);
                Assert.Equal(0f, WeatherAdapter.LastAdhesionState.WinterWetnessEquivalent);
                Assert.Equal(0f, WeatherAdapter.LastThunderState.SnowAmount);
                Assert.Equal(2, WeatherAdapter.AdhesionApplyCount);
                Assert.Equal(2, WeatherAdapter.ThunderApplyCount);
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
                Assert.Null(SeasonVisualController.SnowWrittenTo);
                Assert.Null(SeasonVisualController.SnowRestoredFrom);
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

        [Fact]
        public void SeasonResetWaitsForWeatherDriverAfterLoading()
        {
            using (var session = new Session(3d))
            {
                WeatherAdapter.Clock = null;
                WeatherAdapter.ResetAppliedOverrides();
                session.Enter(new SaveGameData());
                session.Runtime.SetSeason(SeasonKind.Summer);
                Assert.Equal(0, WeatherAdapter.SeasonResetCount);

                WeatherAdapter.DayMinutes = 3f;
                WeatherAdapter.DayOverridden = true;
                WeatherAdapter.Clock = new DateTime(2026, 1, 1);
                session.Runtime.Tick(0.02f);
                Assert.Equal(1, WeatherAdapter.SeasonResetCount);
                Assert.Equal(60f, WeatherAdapter.DayMinutes);
                Assert.False(WeatherAdapter.DayOverridden);
                Assert.Equal(SeasonKind.Summer, WeatherAdapter.LastClimateState.Current);
            }
        }

        [Fact]
        public void ClientKeepsHostSnowThroughLoadingAndReappliesRepeatedSeasonSelectionOnce()
        {
            var net = new TestNetwork { IsSessionActive = true, IsAuthority = false };
            using (var session = new Session(1d, net))
            {
                session.Runtime.Start();
                var state = SeasonNetworkState.FromState(new SeasonState(3d, SeasonKind.Winter,
                    SeasonKind.Spring, 0f, 1f, -20f, .4f), 14f, 3f);
                state.SeasonSelectionRevision = 5;
                state.HasSurfaceSnowCoverage = true;
                state.SurfaceSnowCoverage = .42f;
                int initial = SeasonVisualController.SelectionCount;
                net.Receive(state); // Before native world/visuals exist.
                session.Prepare(new SaveGameData());
                session.Runtime.Tick(.02f);
                Assert.Equal(initial, SeasonVisualController.SelectionCount);
                WorldStreamingInit.FinishLoading();
                session.Runtime.Tick(.02f);
                Assert.Equal(initial + 1, SeasonVisualController.SelectionCount);
                Assert.Equal(.42f, SeasonVisualController.LastHostCoverage);
                Assert.False(SeasonalThermalController.SimulateLocally);
                net.Receive(state);
                session.Runtime.Tick(.02f);
                Assert.Equal(initial + 1, SeasonVisualController.SelectionCount);
                WeatherAdapter.ResetAppliedOverrides();
                state.SeasonSelectionRevision++;
                state.SurfaceSnowCoverage = 1f;
                net.Receive(state);
                session.Runtime.Tick(.02f);
                Assert.Equal(initial + 2, SeasonVisualController.SelectionCount);
                Assert.Equal(1, WeatherAdapter.SeasonResetCount);
                Assert.Equal(1f, SeasonVisualController.LastHostCoverage);
                net.Receive(state);
                session.Runtime.Tick(.02f);
                Assert.Equal(1, WeatherAdapter.SeasonResetCount);
            }
        }

        [Fact]
        public void ClientUsesHostWeatherSettingsWithoutPersistingThemOverLocalPreferences()
        {
            var net = new TestNetwork { IsSessionActive = true, IsAuthority = false };
            using (var session = new Session(1d, net))
            {
                session.Settings.SeasonalDaylightEnabled = true;
                session.Settings.SeasonalPrecipitationEnabled = true;
                session.Settings.WinterAdhesionEnabled = true;
                session.Settings.DisableWinterThunder = true;
                session.Settings.RespectExternalWetnessOverride = false;
                session.Enter(new SaveGameData());
                var state = HostWeather();
                state.Weather.RespectExternalWetnessOverride = true;
                net.Receive(state);
                session.Runtime.Tick(.02f);
                Assert.False(WeatherAdapter.LastDaylightEnabled);
                Assert.False(WeatherAdapter.LastPrecipitationEnabled);
                Assert.False(WeatherAdapter.LastAdhesionEnabled);
                Assert.False(WeatherAdapter.LastThunderEnabled);
                Assert.True(WeatherAdapter.LastRespectWetness);
                session.Runtime.SaveSettings();
                var saved = session.ReadCheckpoint();
                Assert.True(saved.SeasonalDaylightEnabled);
                Assert.True(saved.SeasonalPrecipitationEnabled);
                Assert.True(saved.WinterAdhesionEnabled);
                Assert.True(saved.DisableWinterThunder);
                Assert.False(saved.RespectExternalWetnessOverride);
                Assert.True(session.Settings.SeasonalDaylightEnabled);
                Assert.False(session.Settings.RespectExternalWetnessOverride);
            }
        }

        [Fact]
        public void WeatherMenuEditForcesImmediateHostPublishAndClientsCannotPublish()
        {
            var net = new TestNetwork { IsSessionActive = true };
            using (var session = new Session(3d, net))
            {
                session.Enter(new SaveGameData());
                var adapter = WeatherAdapter.LastCreated;
                adapter.OutgoingWeather.Overrides = 1;
                adapter.OutgoingWeather.Values[0] = .85f;
                int before = net.PublishCount;
                adapter.WeatherEdited(); // No runtime tick or broadcast timer advance.
                Assert.Equal(before + 1, net.PublishCount);
                Assert.True(net.LastPublishForced);
                Assert.Equal(.85f, net.LastPublished.Weather.Values[0]);
                Assert.Equal((ushort)1, net.LastPublished.Weather.Overrides);
                net.IsAuthority = false;
                adapter.WeatherEdited();
                Assert.Equal(before + 1, net.PublishCount);
                session.Runtime.Stop();
                adapter.WeatherEdited();
                Assert.Equal(before + 1, net.PublishCount);
            }
        }

        [Fact]
        public void HostWeatherReceivedDuringLoadingSurvivesPreparationAndSeasonResets()
        {
            var net = new TestNetwork { IsSessionActive = true, IsAuthority = false };
            using (var session = new Session(1d, net))
            {
                session.Runtime.Start();
                var state = HostWeather();
                state.SeasonSelectionRevision = 5;
                var adapter = WeatherAdapter.LastCreated;
                net.Receive(state);
                Assert.Equal(1, adapter.NetworkReceiveCount);
                Assert.Equal(0, adapter.NetworkApplyCount);
                session.Prepare(new SaveGameData());
                WorldStreamingInit.FinishLoading();
                session.Runtime.Tick(.02f);
                Assert.NotNull(adapter.LastAppliedNetworkWeather);
                Assert.Equal(.8f, adapter.LastAppliedNetworkWeather.Values[0]);
                int beforeReset = adapter.NetworkApplyCount;
                state.SeasonSelectionRevision++;
                net.Receive(state);
                session.Runtime.Tick(.02f);
                Assert.True(adapter.NetworkApplyCount > beforeReset);
                Assert.True(adapter.LastNetworkApplyForced);
                Assert.Equal((ushort)1, adapter.LastAppliedNetworkWeather.Overrides);
                UnloadWatcher.RequestUnload();
                Assert.Null(adapter.NetworkWeather);
            }
        }

        [Fact]
        public void NativeWeatherRestoreCallbackReappliesHostOwnedModifiers()
        {
            var net = new TestNetwork { IsSessionActive = true, IsAuthority = false };
            using (var session = new Session(1d, net))
            {
                session.Enter(new SaveGameData());
                var state = HostWeather();
                state.Weather.WinterAdhesion = false;
                state.Weather.DisableWinterThunder = false;
                state.Weather.RespectExternalWetnessOverride = true;
                net.Receive(state);
                session.Runtime.Tick(.02f);
                WeatherAdapter.ResetAppliedOverrides();
                WeatherAdapter.LastCreated.NetworkWeatherRestored();
                Assert.Equal(1, WeatherAdapter.AdhesionApplyCount);
                Assert.Equal(1, WeatherAdapter.ThunderApplyCount);
                Assert.False(WeatherAdapter.LastAdhesionEnabled);
                Assert.False(WeatherAdapter.LastThunderEnabled);
                Assert.True(WeatherAdapter.LastRespectWetness);
            }
        }

        private static SeasonNetworkState HostWeather()
        {
            var state = SeasonNetworkState.FromState(new SeasonState(3d, SeasonKind.Winter,
                SeasonKind.Spring, 0f, 1f, -20f, .4f), 14f, 3f);
            state.Weather = new WeatherNetworkState
            {
                Available = true, Overrides = 1,
                Values = new[] { .8f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },
                RealDateTimeTicks = new DateTime(2026, 1, 1).Ticks
            };
            return state;
        }

        [Fact]
        public void HostAloneRunsSeasonalPowertrainSimulation()
        {
            var net = new TestNetwork();
            using (var session = new Session(3d, net))
            {
                session.Enter(new SaveGameData());
                Assert.True(SeasonalThermalController.SimulateLocally);
                uint revision = net.LastPublished.SeasonSelectionRevision;
                session.Runtime.SetSeason(SeasonKind.Summer);
                Assert.True(SeasonalThermalController.SimulateLocally);
                Assert.Equal(revision + 1, net.LastPublished.SeasonSelectionRevision);
                session.Runtime.SetSeason(SeasonKind.Summer);
                Assert.Equal(revision + 2, net.LastPublished.SeasonSelectionRevision);
            }
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
                SeasonVisualController.SnowRestoredFrom=SeasonVisualController.SnowWrittenTo=null;
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
            public SeasonNetworkState LastPublished;
            public int PublishCount;
            public bool LastPublishForced;
            public bool IsAvailable { get { return true; } }
            public bool IsSessionActive { get; set; }
            public bool IsAuthority { get; set; } = true;
            public string Status { get { return "Test network"; } }
            public event Action<SeasonNetworkState> StateReceived;
            public void Receive(SeasonNetworkState state) { StateReceived?.Invoke(state); }
            public void Initialize(string id) { }
            public void SetEnabled(bool enabled) { }
            public void Publish(SeasonNetworkState state, bool force)
            { LastPublished = state; PublishCount++; LastPublishForced = force; }
            public void RequestState() { }
            public void Dispose() { }
        }
    }
}
