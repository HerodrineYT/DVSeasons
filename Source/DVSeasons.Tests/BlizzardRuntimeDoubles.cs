using System;
using DVSeasons.Core;

namespace DVSeasons.Mod
{
    // Engine/audio boundary for the existing linked SeasonRuntime lifecycle suite.
    // The actual Unity controller and hooks are exercised by VerifyBlizzard.
    internal sealed class BlizzardController : IDisposable
    {
        internal BlizzardState State = new BlizzardState();
        private readonly Random random = new Random(79);
        internal BlizzardController(string path, SeasonModSettings settings) { }
        internal bool Advance(double hours, SeasonState season, bool enabled) =>
            State.Advance(hours, season.Current == SeasonKind.Winter, enabled, random, DateTime.UtcNow.Ticks);
        internal void Schedule() => State.Schedule(random, DateTime.UtcNow.Ticks);
        internal void Cancel() => State.Cancel();
        internal void Receive(BlizzardState state) { State = state.Clone(); }
        internal BlizzardState Capture() => State.Clone();
        internal SeasonState Temperature(SeasonState state) => state;
        internal void Apply(bool authority) { }
        internal void Restore(SaveGameData data) { }
        internal void Save(SaveGameData data) { }
        public void Dispose() { State = new BlizzardState(); }
    }
    internal static class ModLocalization
    {
        internal static string Text(string key) => key;
        internal static string Format(string key, params object[] args) => key;
    }
}
