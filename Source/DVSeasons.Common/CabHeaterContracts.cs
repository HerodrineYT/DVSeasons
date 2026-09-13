using System;
using System.Collections.Generic;

namespace DVSeasons.Core
{
    public static class CabHeaterAccess
    {
        public static bool CanChange(string requestedId,string occupiedId,bool loaded,bool onCar,bool locomotive,float level)
        {
            return loaded && onCar && locomotive && !string.IsNullOrEmpty(requestedId) && requestedId.Length<=80 &&
                string.Equals(requestedId,occupiedId,StringComparison.OrdinalIgnoreCase) && CabHeaterSetting.IsAllowed(level);
        }
    }
    public interface ICabHeaterNetworkBridge
    {
        event Action<string, float> HeaterChanged;
        event Action<Dictionary<string, float>> HeatersReceived;
        void SetHeaters(Dictionary<string, float> states, bool broadcast);
        void RequestHeaterChange(string carId, float level);
    }
}
