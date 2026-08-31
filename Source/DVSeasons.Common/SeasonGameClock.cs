using System;

namespace DVSeasons.Core
{
    public sealed class SeasonGameClock
    {
        private DateTime? previousTime;

        public void Reset() { previousTime = null; }

        public double GetElapsedDays(DateTime? gameTime, float deltaSeconds, float fallbackMinutesPerDay)
        {
            if (gameTime.HasValue)
            {
                var days = previousTime.HasValue ? (gameTime.Value - previousTime.Value).TotalDays : 0d;
                previousTime = gameTime;
                // Rewinding a save or a discontinuous clock must never reverse or
                // fast-forward the calendar. Normal sleep/time acceleration still works.
                return days >= 0d && days <= 31d ? days : 0d;
            }

            // Never count the same interval twice when the native clock returns.
            Reset();
            if (float.IsNaN(deltaSeconds) || float.IsInfinity(deltaSeconds) || deltaSeconds <= 0f)
                return 0d;
            if (float.IsNaN(fallbackMinutesPerDay) || float.IsInfinity(fallbackMinutesPerDay))
                return 0d;
            return deltaSeconds / (Math.Max(1f, fallbackMinutesPerDay) * 60d);
        }
    }
}
