using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using DVSeasons.Core;

// Narrow stand-ins for the native boundaries used by the linked production runtime.
// SaveGameData's nullable primitive getters match the build-99 game API.
internal sealed class SaveGameData
{
    private readonly Dictionary<string, object> values = new Dictionary<string, object>();
    public int? GetInt(string key) { return Get<int>(key); }
    public float? GetFloat(string key) { return Get<float>(key); }
    public double? GetDouble(string key) { return Get<double>(key); }
    public bool? GetBool(string key) { return Get<bool>(key); }
    public void SetInt(string key, int value) { values[key] = value; }
    public void SetFloat(string key, float value) { values[key] = value; }
    public void SetDouble(string key, double value) { values[key] = value; }
    public void SetBool(string key, bool value) { values[key] = value; }
    private T? Get<T>(string key) where T : struct
    {
        object value;
        return values.TryGetValue(key, out value) ? (T?)value : null;
    }
}

internal sealed class SaveGameManager
{
    public SaveGameData data;
    public bool IsNewSession;
    public event Action<SaveGameData> OnInternalDataUpdate;
    public int SubscriberCount { get { return OnInternalDataUpdate?.GetInvocationList().Length ?? 0; } }
    public void Save() { OnInternalDataUpdate?.Invoke(data); }
}

internal static class WorldStreamingInit
{
    public static bool IsLoaded;
    public static event Action LoadingFinished;
    public static void FinishLoading() { IsLoaded = true; LoadingFinished?.Invoke(); }
}

internal static class UnloadWatcher
{
    public static bool isUnloading;
    public static event Action UnloadRequested;
    public static void RequestUnload() { isUnloading = true; UnloadRequested?.Invoke(); }
}

namespace UnityEngine
{
    internal static class Object
    {
        public static SaveGameManager Manager;
        public static T FindObjectOfType<T>() where T : class { return Manager as T; }
    }
    internal static class Time { public static float realtimeSinceStartup; }
    internal struct Vector3
    {
        public float x; public float y; public float z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
}

namespace UnityModManagerNet
{
    public static class UnityModManager
    {
        public sealed class ModEntry
        {
            public string Path = "";
            public ModInfo Info = new ModInfo();
            public ModLogger Logger = new ModLogger();
            public string SavedSettingsXml;
            public int SaveCount;
        }
        public sealed class ModInfo { public string Id = "DVSeasons.Tests"; }
        public sealed class ModLogger
        {
            public readonly List<string> Messages = new List<string>();
            public void Log(string message) { Messages.Add(message); }
            public void Warning(string message) { Messages.Add(message); }
        }
        public class ModSettings
        {
            public virtual void Save(ModEntry entry) { }
            protected static void Save<T>(T settings, ModEntry entry)
            {
                using (var writer = new StringWriter())
                {
                    new XmlSerializer(typeof(T)).Serialize(writer, settings);
                    entry.SavedSettingsXml = writer.ToString();
                    entry.SaveCount++;
                }
            }
        }
    }
}

namespace DVSeasons.Mod
{
    internal sealed class WeatherAdapter : IDisposable
    {
        public static DateTime? Clock;
        public bool IsReady { get { return Clock.HasValue; } }
        public float RainIntensity { get { return 0f; } }
        public UnityEngine.Vector3 SnowWindVelocity { get { return new UnityEngine.Vector3 { x = 0, y = 0, z = 0 }; } }
        public float SnowLightFactor { get { return 1f; } }
        public void TickProbe() { }
        public bool TryGetGameDateTime(out DateTime time) { time = Clock.GetValueOrDefault(); return Clock.HasValue; }
        public void ResetForSession() { }
        public void ApplyWinterAdhesion(SeasonState state, bool enabled, bool respectOtherMods) { }
        public void ApplySeasonalPrecipitation(SeasonState state, bool enabled) { }
        public void ApplyWinterThunderSuppression(SeasonState state, bool enabled) { }
        public void Dispose() { }
    }

    internal sealed class WinterRainAudioController : IDisposable
    {
        public void Apply(float snow, bool replaceRain, bool muteRain) { }
        public void Dispose() { }
    }

    internal sealed class SeasonVisualController : IDisposable
    {
        public static int ResetCount;
        public static SeasonState LastApplied;
        private bool disposed;
        public SeasonVisualController(string path) { }
        public void ResetForSession() { ResetCount++; LastApplied = null; }
        public void Apply(SeasonState state, float snow, float rain, UnityEngine.Vector3 wind,
            float light, SeasonModSettings settings)
        {
            if (disposed) throw new ObjectDisposedException(nameof(SeasonVisualController));
            LastApplied = state;
        }
        public void Dispose() { disposed = true; }
    }
}
