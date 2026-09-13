using System;

namespace DVSeasons.Core
{
    public static class AirTemperatureProfile
    {
        // Regional air temperature, not perceived wind chill. Cloud cover reduces
        // the day/night swing; wet, windy fronts add modest advective cooling.
        public static float Evaluate(SeasonState season, double days, float hour,
            float cloud, float rain, float wind, float thunder)
        {
            cloud = Unit(cloud); rain = Unit(rain); wind = Unit(wind); thunder = Unit(thunder);
            if (double.IsNaN(days) || double.IsInfinity(days)) days = 0;
            if (float.IsNaN(hour) || float.IsInfinity(hour)) hour = 12;
            var summer = Weight(season, SeasonKind.Summer);
            var winter = Weight(season, SeasonKind.Winter);
            float mean = season.TemperatureCelsius + winter * 10 - summer * 6;
            float daily = (float)Math.Sin((hour - 9) * Math.PI / 12) * (5 - cloud * 3.5f);
            float front = (float)(Math.Sin(days * Math.PI / 2.7) * 2.5 + Math.Sin(days * Math.PI / 6.1) * 1.5);
            return Math.Max(-30, Math.Min(30, mean + daily + front - rain * 2.5f
                - wind * (.6f + rain * 1.4f) - thunder * 1.5f));
        }

        private static float Weight(SeasonState s, SeasonKind k)
        { return (s.Current == k ? 1 - s.Transition : 0) + (s.Next == k ? s.Transition : 0); }
        private static float Unit(float x)
        { return float.IsNaN(x) || float.IsInfinity(x) ? 0 : Math.Max(0, Math.Min(1, x)); }
    }
}
