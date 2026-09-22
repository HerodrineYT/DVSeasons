using System;
using DVSeasons.Core;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class BlizzardController : IDisposable
    {
        private const string SaveKey = "DVSeasons.Blizzard.v1";
        private readonly System.Random random = new System.Random();
        private readonly string modPath;
        private readonly SeasonModSettings settings;
        private readonly BlizzardBlackout blackout = new BlizzardBlackout();
        private BlizzardAudio audio;
        internal BlizzardState State { get; private set; } = new BlizzardState();
        internal BlizzardController(string path, SeasonModSettings settings)
        { modPath = path; this.settings = settings; }

        internal bool Advance(double gameHours, SeasonState season, bool enabled)
        { return State.Advance(gameHours, season.Current == SeasonKind.Winter, enabled, random, DateTime.UtcNow.Ticks); }
        internal void Schedule() { State.Schedule(random, DateTime.UtcNow.Ticks); }
        internal void Cancel() { State.Cancel(); }
        internal void Receive(BlizzardState state)
        {
            if (state == null || !state.IsValid()) return;
            State = state.Clone();
            if (State.Scheduled || audio != null) { EnsureAudio(); audio.Receive(State); }
        }
        internal BlizzardState Capture()
        { var copy = State.Clone(); copy.HostUtcTicks = DateTime.UtcNow.Ticks; return copy; }
        internal SeasonState Temperature(SeasonState source)
        {
            if (!State.Active) return source;
            float temp = Mathf.Lerp(source.TemperatureCelsius, -50f, State.Strength);
            return new SeasonState(source.Phase, source.Current, source.Next, source.Transition,
                source.SnowAmount, temp, source.WinterWetnessEquivalent);
        }
        internal void Apply(bool authority)
        {
            if (State.Scheduled || audio != null)
            {
                EnsureAudio();
                if (authority) { State.HostUtcTicks = DateTime.UtcNow.Ticks; audio.Receive(State); }
                audio.SetStorm(State.Active);
            }
            blackout.Apply(State.Blackout);
        }
        private void EnsureAudio()
        {
            if (audio != null) return;
            var root = new GameObject("DVSeasons Blizzard Audio");
            audio = root.AddComponent<BlizzardAudio>(); audio.Initialize(modPath, settings);
        }
        internal void Save(SaveGameData data)
        { data.SetString(SaveKey, Newtonsoft.Json.JsonConvert.SerializeObject(State)); }
        internal void Restore(SaveGameData data)
        {
            State = new BlizzardState();
            try
            {
                var json = data.GetString(SaveKey);
                if (string.IsNullOrEmpty(json)) return;
                var saved = Newtonsoft.Json.JsonConvert.DeserializeObject<BlizzardState>(json);
                if (saved != null && saved.IsValid())
                {
                    State = saved;
                    // Save reloads retain the timeline, not a recording that already aired.
                    State.Notice = BlizzardNotice.None; State.NoticeUtcTicks = 0;
                }
            }
            catch (Exception ex) { Debug.LogWarning("[DVSeasons] Blizzard save ignored: " + ex.Message); }
        }
        public void Dispose()
        {
            blackout.Dispose();
            if (audio != null) UnityEngine.Object.Destroy(audio.gameObject);
            audio = null; State = new BlizzardState();
        }
    }
}
