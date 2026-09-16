using System;
using System.IO;
using System.Reflection;
using DV;
using DV.WeatherSystem;
using DV.UI.LocoHUD;
using DVSeasons.Core;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

// Run inside Unity Mono against DV99's real weather slots, editor provider,
// save/load methods and the compiled Seasons Harmony patches. No fake slots.
public static class VerifyWeatherNetwork
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const string FixtureId = "DVSeasons.VerifyWeatherNetwork.Fixture";
    static readonly PhotoModeWeatherController.WeatherSettingType[] Types = {
        PhotoModeWeatherController.WeatherSettingType.RainValue,
        PhotoModeWeatherController.WeatherSettingType.ThunderValue,
        PhotoModeWeatherController.WeatherSettingType.WetnessValue,
        PhotoModeWeatherController.WeatherSettingType.WindSpeed,
        PhotoModeWeatherController.WeatherSettingType.WindDirection,
        PhotoModeWeatherController.WeatherSettingType.WeatherPointX,
        PhotoModeWeatherController.WeatherSettingType.WeatherPointY,
        PhotoModeWeatherController.WeatherSettingType.TimeOfDayHours,
        PhotoModeWeatherController.WeatherSettingType.DayLengthInMinutes
    };
    static readonly float[] ManualValues = { .67f, .73f, .23f, 8.1f, 241f, .8f, .2f, 6.25f, 7.5f };
    static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    static object Get(object target, string name) { return target.GetType().GetField(name, All).GetValue(target); }
    static void Set(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, All);
        if (field != null) field.SetValue(target, value);
        else target.GetType().GetProperty(name, All).SetValue(target, value, null);
    }
    static object Call(object target, string name, params object[] args)
    { return target.GetType().GetMethod(name, All).Invoke(target, args); }
    static bool SkipValidate() { return false; }

    sealed class World : IDisposable
    {
        readonly GameObject root;
        readonly FieldInfo singleton;
        readonly object previousDriver;
        public readonly WeatherDriver Driver;
        public readonly WeatherPresetManager Manager;
        public readonly PhotoModeWeatherSettingsProvider Provider;
        public readonly object Adapter;
        public readonly object Settings;
        public int TimeJumps;
        public int WeatherEdits;
        public World(Assembly mod, bool authority, string label)
        {
            root = new GameObject("weather network " + label); root.SetActive(false);
            Driver = root.AddComponent<WeatherDriver>();
            Manager = root.AddComponent<WeatherPresetManager>();
            Manager.todSky = root.AddComponent<TOD_Sky>();
            Manager.todSky.Cycle = new TOD_CycleParameters();
            Manager.todSky.Cycle.RealDateTime = new DateTime(2026, 9, 16, 13, 30, 0, DateTimeKind.Utc);
            Manager.todTime = root.AddComponent<TOD_Time>();
            Driver.manager = Manager; Driver.todAnimation = root.AddComponent<TOD_Animation>();
            Set(Driver, "s_", new WeatherStateChungus(0f));
            Driver.DayLengthInMinutes.RealValue = 120f;
            Driver.WindSpeed.RealValue = 1f;
            Driver.RainValue.RealValue = .1f;
            Driver.WetnessValue.RealValue = .1f;
            Adapter = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.WeatherAdapter", true), true);
            Settings = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SeasonModSettings", true), true);
            Set(Adapter, "driver", Driver);
            Set(Adapter, "WeatherAuthority", new Func<bool>(() => authority));
            Set(Adapter, "WeatherEdited", new Action(() => WeatherEdits++));
            singleton = typeof(DV.Utils.SingletonBehaviour<WeatherDriver>).GetField("_instance", All);
            previousDriver = singleton.GetValue(null); singleton.SetValue(null, Driver);
            Provider = root.AddComponent<PhotoModeWeatherSettingsProvider>();
            Call(Get(Adapter, "isolation"), "Enable", Adapter);
            Manager.TimeJump += () => TimeJumps++;
        }
        public OverridableValue<float>[] Slots
        {
            get { return new[] { Driver.RainValue, Driver.ThunderValue, Driver.WetnessValue, Driver.WindSpeed,
                Driver.WindDirection, Driver.WeatherPointX, Driver.WeatherPointY, Driver.TimeOfDayHours, Driver.DayLengthInMinutes }; }
        }
        public WeatherNetworkState Capture() { return (WeatherNetworkState)Call(Adapter, "CaptureNetworkWeather", Settings); }
        public void Receive(WeatherNetworkState state)
        {
            Call(Adapter, "ReceiveNetworkWeather", state);
            Call(Adapter, "ApplyNetworkWeather", false);
        }
        public void Dispose()
        {
            ((IDisposable)Adapter).Dispose(); singleton.SetValue(null, previousDriver);
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    public static void Run(string modDirectory)
    {
        var mod = Assembly.LoadFrom(Path.Combine(modDirectory, "DVSeasons.dll"));
        var fixture = new Harmony(FixtureId);
        fixture.Patch(AccessTools.Method(typeof(TOD_Sky), "OnValidate"),
            prefix: new HarmonyMethod(typeof(VerifyWeatherNetwork), "SkipValidate"));
        try
        {
            WeatherNetworkState manual, cleared;
            JObject nativePacket;
            using (var host = new World(mod, true, "host"))
            {
                var winter = new SeasonCycle(new SeasonSettingsSnapshot(true, 14, 3, SeasonKind.Winter, true, .5f), 3).GetState();
                Call(host.Adapter, "ApplyWinterAdhesion", winter, true, false);
                Call(host.Adapter, "ApplyWinterThunderSuppression", winter, true);
                var automatic = host.Capture();
                Require(automatic.Available && (automatic.Overrides & 6) == 0,
                    "Owned snow adhesion/thunder suppression leaked into manual host overrides");
                Require(host.Driver.WetnessValue.IsOverridden && host.Driver.WetnessValue.CurrentValue == .5f &&
                    host.Driver.ThunderValue.IsOverridden, "Network capture removed live seasonal effects");
                Require(host.Provider.IsSliderInteractable(Types[0]), "Host weather editor was locked");
                for (int i = 0; i < Types.Length; i++) host.Provider.SetWeatherOverride(Types[i], ManualValues[i]);
                // A manually chosen wetness/thunder must survive normal seasonal ticks.
                Call(host.Adapter, "ApplyWinterAdhesion", winter, true, false);
                Call(host.Adapter, "ApplyWinterThunderSuppression", winter, true);
                CheckManual(host, "host after seasonal tick");
                uint revision = host.Capture().TimeRevision;
                host.Provider.SetTime(new DateTime(2026, 10, 3, 6, 15, 0, DateTimeKind.Utc));
                Set(host.Settings, "SeasonalDaylightEnabled", false);
                Set(host.Settings, "SeasonalPrecipitationEnabled", false);
                Set(host.Settings, "DisableWinterThunder", true);
                Set(host.Settings, "WinterAdhesionEnabled", true);
                Set(host.Settings, "RespectExternalWetnessOverride", true);
                manual = WireRoundTrip(host.Capture());
                Require(manual.Overrides == WeatherNetworkState.AllOverridesMask && manual.TimeRevision != revision,
                    "Host manual state or SetTime revision not captured");
                Require(!manual.SeasonalDaylight && !manual.SeasonalPrecipitation && manual.WinterAdhesion &&
                    manual.DisableWinterThunder && manual.RespectExternalWetnessOverride,
                    "Host weather-affecting settings not captured");
                nativePacket = host.Driver.GetSaveData(false);
                for (int i = 0; i < Types.Length; i++) host.Provider.ClearWeatherOverride(Types[i]);
                cleared = WireRoundTrip(host.Capture());
                Require(cleared.Overrides == 0, "Host reset buttons did not clear overrides in snapshot");
                Require(host.WeatherEdits == Types.Length * 2 + 1,
                    "Host editor changes did not notify the network publisher immediately");
                Debug.Log("WEATHER_NETWORK_HOST_OK: actual menu edits for all nine slots; seasonal override isolation; explicit clock revision; reset buttons; host weather settings.");
            }
            // Harmony has one active game adapter, as in a real process. Execute two
            // independent fresh client worlds sequentially to cover join/snapshot state.
            VerifyClient(mod, manual, cleared, nativePacket, "client A");
            VerifyClient(mod, manual, cleared, nativePacket, "late-joining client B");
            Debug.Log("WEATHER_NETWORK_OK: real DV99 menu/provider/overridable slots and save-load path; one host plus two independent client worlds. This is not a live two-process MP play test.");
        }
        finally { fixture.UnpatchAll(FixtureId); }
    }

    static WeatherNetworkState WireRoundTrip(WeatherNetworkState state)
    {
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) state.WriteTo(writer);
            stream.Position = 0;
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true)) return WeatherNetworkState.ReadFrom(reader);
        }
    }

    static void CheckManual(World world, string phase)
    {
        var slots = world.Slots;
        for (int i = 0; i < slots.Length; i++)
            Require(slots[i].IsOverridden && Mathf.Abs(slots[i].CurrentValue - ManualValues[i]) < .0001f,
                phase + ": wrong override " + Types[i] + " = " + slots[i].CurrentValue + ", overridden=" + slots[i].IsOverridden);
    }

    static void VerifyClient(Assembly mod, WeatherNetworkState manual, WeatherNetworkState cleared, JObject nativePacket, string label)
    {
        using (var client = new World(mod, false, label))
        {
            client.Receive(manual.Clone()); CheckManual(client, label + " first packet");
            Require(Math.Abs((client.Manager.RealDateTime - new DateTime(manual.RealDateTimeTicks)).TotalSeconds) < .01,
                label + ": initial host date/clock was not applied");
            int initialJumps = client.TimeJumps;
            for (int i = 0; i < 5; i++) client.Receive(manual.Clone());
            Require(client.TimeJumps == initialJumps, label + ": duplicate state repeatedly jumped the clock");
            var heartbeat = manual.Clone(); heartbeat.RealDateTimeTicks += TimeSpan.FromMinutes(10).Ticks;
            var beforeHeartbeat = client.Manager.RealDateTime;
            client.Receive(heartbeat);
            Require(client.TimeJumps == initialJumps && client.Manager.RealDateTime == beforeHeartbeat,
                label + ": ordinary clock heartbeat jumped time without an explicit time edit");
            var slots = client.Slots;
            for (int i = 0; i < Types.Length; i++)
            {
                Require(!client.Provider.IsSliderInteractable(Types[i]), label + ": local editor slider still enabled: " + Types[i]);
                client.Provider.SetWeatherOverride(Types[i], ManualValues[i] * .25f);
                client.Provider.ClearWeatherOverride(Types[i]);
            }
            CheckManual(client, label + " rejected local changes");
            DateTime beforeLocalTime = client.Manager.DateTime;
            client.Provider.SetTime(beforeLocalTime.AddDays(3));
            Require(client.Manager.DateTime == beforeLocalTime, label + ": local SetTime changed authoritative clock");
            Require(client.WeatherEdits == 0, label + ": rejected local changes notified the host publisher");

            // Multiplayer 0.1.16 loads ordinary weather packets with useOverrides=false.
            // That native call clears every override: Seasons must restore cached state
            // in the same call, rather than leaving weather flicker until its next tick.
            initialJumps = client.TimeJumps;
            client.Driver.LoadSaveData((JObject)nativePacket.DeepClone(), false);
            CheckManual(client, label + " native weather packet");
            Require(client.TimeJumps == initialJumps,
                label + ": periodic native weather restore broadcast global clock jumps");
            initialJumps = client.TimeJumps;
            client.Receive(manual.Clone());
            Require(client.TimeJumps == initialJumps, label + ": native reload made duplicate state jump the clock");
            client.Receive(cleared.Clone());
            foreach (var slot in client.Slots) Require(!slot.IsOverridden, label + ": host reset left an override active");
            Require(Mathf.Abs(client.Driver.DayLengthInMinutes.RealValue - cleared.BaseDayLengthInMinutes) < .0001f,
                label + ": host reset did not restore normal day length");
            client.Driver.LoadSaveData((JObject)nativePacket.DeepClone(), false);
            foreach (var slot in client.Slots) Require(!slot.IsOverridden, label + ": native reload resurrected cleared override");
            var winter = new SeasonCycle(new SeasonSettingsSnapshot(true, 14, 3, SeasonKind.Winter, true, .5f), 3).GetState();
            Set(client.Adapter, "NetworkWeatherRestored", new Action(() => {
                Call(client.Adapter, "ApplyWinterAdhesion", winter, true, false);
                Call(client.Adapter, "ApplyWinterThunderSuppression", winter, true);
            }));
            for (int i = 0; i < 3; i++)
            {
                client.Driver.LoadSaveData((JObject)nativePacket.DeepClone(), false);
                Require(client.Driver.WetnessValue.IsOverridden && client.Driver.WetnessValue.CurrentValue == .5f &&
                    client.Driver.ThunderValue.IsOverridden && client.Driver.ThunderValue.CurrentValue == 0f,
                    label + ": native weather packet dropped seasonal effects after host reset");
            }
            Debug.Log("WEATHER_NETWORK_CLIENT_OK: " + label + "; all nine values, local edit rejection, native packet immediate restoration, no duplicate clock jump, complete host reset.");
        }
    }
}
