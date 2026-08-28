using System;
using DVSeasons.Core;

namespace DVSeasons.Mod
{
    internal sealed class OfflineSeasonBridge : ISeasonNetworkBridge
    {
        public bool IsAvailable { get { return false; } }
        public bool IsSessionActive { get { return false; } }
        public bool IsAuthority { get { return true; } }
        public string Status { get { return "Локальный режим"; } }
        public event Action<SeasonNetworkState> StateReceived { add { } remove { } }
        public void Initialize(string modId) { }
        public void SetEnabled(bool enabled) { }
        public void Publish(SeasonNetworkState state, bool force) { }
        public void RequestState() { }
        public void Dispose() { }
    }
}
