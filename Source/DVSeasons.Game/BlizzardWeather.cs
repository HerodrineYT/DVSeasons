using System;
using DV.WeatherSystem;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed partial class WeatherAdapter
    {
        private bool blizzardOwned;
        private readonly bool[] preBlizzardOverrides = new bool[6];
        private readonly float[] preBlizzardValues = new float[6];
        private static readonly int[] BlizzardSlots = { 0, 1, 2, 3, 5, 6 };
        private static readonly float[] BlizzardValues = { 1f, 0f, 1f, 10f, 1f, 1f };

        internal void ApplyBlizzard(bool enabled)
        {
            if (driver == null) return;
            if (enabled)
            {
                if (!blizzardOwned)
                    for (int i = 0; i < BlizzardSlots.Length; i++)
                    {
                        var slot = WeatherSlot(BlizzardSlots[i]);
                        preBlizzardOverrides[i] = slot.IsOverridden;
                        preBlizzardValues[i] = slot.OverriddenValue;
                    }
                blizzardOwned = true;
                for (int i = 0; i < BlizzardSlots.Length; i++) WeatherSlot(BlizzardSlots[i]).EngageOverride(BlizzardValues[i]);
            }
            else if (blizzardOwned)
            {
                RestoreBlizzardInputs(); blizzardOwned = false;
            }
        }

        private void RestoreBlizzardInputs()
        {
            for (int i = 0; i < BlizzardSlots.Length; i++)
            {
                var slot = WeatherSlot(BlizzardSlots[i]);
                if (preBlizzardOverrides[i]) slot.EngageOverride(preBlizzardValues[i]);
                else slot.ClearOverride();
            }
        }

        internal IDisposable SuspendBlizzard()
        {
            if (!blizzardOwned || driver == null) return null;
            RestoreBlizzardInputs();
            return new ResumeBlizzard(this);
        }
        private sealed class ResumeBlizzard : IDisposable
        {
            private readonly WeatherAdapter owner;
            internal ResumeBlizzard(WeatherAdapter owner) { this.owner = owner; }
            public void Dispose() { owner.ApplyBlizzard(true); }
        }

        internal void ApplyBlizzardSky(WeatherDriver target)
        {
            if (!blizzardOwned || driver != target) return;
            SetStormSnapshot(target.CurrentChungusState.currentLow);
            SetStormSnapshot(target.CurrentChungusState.currentHigh);
        }
        private static void SetStormSnapshot(WeatherSnapshot s)
        {
            if (s == null) return;
            s.cloudCoverage = 1f; s.cloudOpacity = 1f; s.fogginess = 1f;
            // DisplayFogDensity feeds RenderSettings directly: 1 is an extinction
            // coefficient, not a normalized "100% weather" slider. It obscures
            // even nearby cab geometry. Keep the storm in the distance; use only
            // distance fog so the underlying preset's height layer cannot turn
            // valleys/interiors into a second, much denser blanket.
            s.DisplayFogDensity = .021f; // 1.75 times the previous .012 extinction.
            s.DisplayFogDistanceDensity = 1f;
            s.DisplayFogHeightDensity = 0f;
            s.rainStrength = 1f; s.wetness = 1f;
        }
    }
}
