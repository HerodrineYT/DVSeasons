using System;
using DVSeasons.Core;

namespace DVSeasons.Mod
{
    internal static class SeasonSaveData
    {
        private const string Prefix = "DVSeasons.";

        public static bool TryRead(SaveGameData data, out SeasonSaveState state)
        {
            state = null;
            if (data == null) return false;
            var version = data.GetInt(Prefix + "Version");
            var phase = data.GetDouble(Prefix + "Phase");
            var automatic = data.GetBool(Prefix + "AutomaticCycle");
            var days = data.GetFloat(Prefix + "DaysPerSeason");
            var transitionDays = data.GetFloat(Prefix + "TransitionDays");
            var transitionSeason = data.GetInt(Prefix + "TransitionSeason");
            if (!version.HasValue || !phase.HasValue || !automatic.HasValue ||
                !days.HasValue || !transitionDays.HasValue || !transitionSeason.HasValue)
                return false;
            if (version.Value < 1 || version.Value > SeasonSaveState.CurrentVersion) return false;
            var randomTransitionDuration = version.Value >= 2
                ? data.GetBool(Prefix + "RandomTransitionDuration")
                : (bool?)true;
            if (!randomTransitionDuration.HasValue) return false;

            var candidate = new SeasonSaveState
            {
                // Version 1 did not store the mode and always used random duration.
                // Promote it in memory; the next normal game save writes version 2.
                Version = SeasonSaveState.CurrentVersion,
                Phase = phase.Value,
                AutomaticCycle = automatic.Value,
                DaysPerSeason = days.Value,
                RandomTransitionDuration = randomTransitionDuration.Value,
                TransitionDays = transitionDays.Value,
                TransitionSeason = transitionSeason.Value
            };
            if (!candidate.IsValid()) return false;
            state = candidate;
            return true;
        }

        public static void Write(SaveGameData data, SeasonSaveState state)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (state == null || !state.IsValid())
                throw new ArgumentException("Cannot save an invalid season calendar.", nameof(state));
            data.SetDouble(Prefix + "Phase", state.Phase);
            data.SetBool(Prefix + "AutomaticCycle", state.AutomaticCycle);
            data.SetFloat(Prefix + "DaysPerSeason", state.DaysPerSeason);
            data.SetBool(Prefix + "RandomTransitionDuration", state.RandomTransitionDuration);
            data.SetFloat(Prefix + "TransitionDays", state.TransitionDays);
            data.SetInt(Prefix + "TransitionSeason", state.TransitionSeason);
            data.SetInt(Prefix + "Version", state.Version);
        }
    }
}
