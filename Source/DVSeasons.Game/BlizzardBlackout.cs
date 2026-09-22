using System;
using System.Collections.Generic;
using DV.CashRegister;
using DV.Shops;
using DV.VFX;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class BlizzardBlackout : IDisposable
    {
        private const string Id = "Herodrine.DVSeasons.BlizzardPower";
        private static BlizzardBlackout current;
        private readonly Harmony harmony = new Harmony(Id);
        private bool installed, enabled;
        private readonly Dictionary<Light, bool> lights = new Dictionary<Light, bool>();
        private readonly Dictionary<TMP_Text, bool> displays = new Dictionary<TMP_Text, bool>();
        private readonly Dictionary<Component, bool> exemptions = new Dictionary<Component, bool>();
        private readonly List<WindowsLightEvent> windows = new List<WindowsLightEvent>();
        private readonly List<SpriteLightsEvent> sprites = new List<SpriteLightsEvent>();
        private readonly HashSet<GeneratedLightsController> generated = new HashSet<GeneratedLightsController>();
        private readonly HashSet<ShopScanner> scanners = new HashSet<ShopScanner>();
        private readonly IncrementalSceneScan<Light> scan;
        private readonly BlizzardShopLighting shopLighting;
        private static readonly System.Reflection.FieldInfo SpriteChanged = AccessTools.Field(typeof(SpriteLightsEvent), "MaterialUpdated");
        private static readonly System.Reflection.FieldInfo Initialized = AccessTools.Field(typeof(GeneratedLightsController), "initialized");
        private static readonly System.Reflection.FieldInfo Pairs = AccessTools.Field(typeof(GeneratedLightsController), "pairsPerType");
        private static readonly System.Reflection.FieldInfo FollowDay = AccessTools.Field(typeof(GeneratedLightsController), "followDayNightCycle");
        private static readonly System.Reflection.FieldInfo ManualState = AccessTools.Field(typeof(GeneratedLightsController), "manualState");
        private static readonly System.Reflection.MethodInfo UpdateLightType = AccessTools.Method(typeof(GeneratedLightsController), "UpdateLightType");
        private float nextLights;
        internal BlizzardBlackout()
        {
            scan = new IncrementalSceneScan<Light>(30f, 128, RegisterLight);
            shopLighting = new BlizzardShopLighting(RegisterLight);
        }
        internal static bool IsOut => current != null && current.enabled;

        internal void Apply(bool outage)
        {
            if (outage && !installed) Install();
            if (enabled != outage)
            {
                enabled = outage;
                if (enabled)
                {
                    Camera.onPreCull += BeforeCamera;
                    // Native event objects are few; their listeners cover already
                    // loaded street lights, including ordinary railway stations.
                    windows.Clear(); windows.AddRange(UnityEngine.Object.FindObjectsOfType<WindowsLightEvent>());
                    sprites.Clear(); sprites.AddRange(UnityEngine.Object.FindObjectsOfType<SpriteLightsEvent>());
                    foreach (var w in windows) if (w != null) w.UpdateTime(CurrentHour());
                    foreach (var s in sprites) if (s != null) s.UpdateTime(CurrentHour());
                    foreach (var controller in UnityEngine.Object.FindObjectsOfType<GeneratedLightsController>()) GeneratedRegistered(controller);
                    foreach (var register in UnityEngine.Object.FindObjectsOfType<CashRegisterWithModules>()) RegisterDisplay(register);
                    shopLighting.Begin();
                }
                else Restore();
                Debug.Log("[DVSeasons] Blizzard power " + (outage ? "outage (towns and stations; offices on backup power)." : "restored."));
            }
            if (!enabled) return;
            scan.Step();
            foreach (var text in displays) if (text.Key != null) text.Key.enabled = false;
            if (Time.realtimeSinceStartup < nextLights) return;
            nextLights = Time.realtimeSinceStartup + .25f;
            foreach (var light in lights) if (light.Key != null) light.Key.enabled = false;
        }

        private void Install()
        {
            current = this;
            try
            {
                Prefix(typeof(WindowsLightEvent), "SetMaterials", nameof(WindowPower));
                Prefix(typeof(ftLightmaps), "RefreshScene", nameof(ShopLightmaps));
                Postfix(typeof(ftLightmaps), "RefreshScene", nameof(ShopSurfaces));
                Postfix(typeof(SpriteLightsEvent), "UpdateTime", nameof(SpritePower));
                Prefix(typeof(GeneratedLightsController), "UpdateLightType", nameof(GeneratedPower));
                Prefix(typeof(GeneratedLightsController), "ShouldBeOn", nameof(InitialGeneratedPower));
                Postfix(typeof(SpriteLightsSystem), "RegisterLightsController", nameof(GeneratedRegistered));
                harmony.Patch(AccessTools.Method(typeof(CookeryLightVolumeRenderer), "LateUpdate"),
                    prefix: new HarmonyMethod(typeof(BlizzardBlackout), nameof(VolumePower)),
                    finalizer: new HarmonyMethod(typeof(BlizzardBlackout), nameof(VolumeRestore)));
                Prefix(typeof(CashRegisterWithModules), "Buy", nameof(Buy));
                Prefix(typeof(CashRegisterBase), "OnTriggerEnter", nameof(Deposit));
                Postfix(typeof(CashRegisterWithModules), "OnEnable", nameof(RegisterDisplay));
                Prefix(typeof(ShopScanner), "OnUse", nameof(ScannerUse));
                Prefix(typeof(ShopScanner), "Update", nameof(ScannerUpdate));
                Prefix(typeof(ScanItemCashRegisterModule), "AddItemsToBuy", nameof(ScanBuy));
                Postfix(typeof(LocoResourceModule), "Update", nameof(ResourceUpdate));
                Prefix(typeof(LocoResourceModule), "get_IsReady", nameof(ResourceReady));
                Prefix(typeof(LocoResourceModule), "GetBoughtResource", nameof(ResourceBuy));
                installed = true;
            }
            catch { harmony.UnpatchAll(Id); current = null; throw; }
        }
        private void Prefix(Type type, string name, string hook)
        { harmony.Patch(AccessTools.Method(type, name), prefix: new HarmonyMethod(typeof(BlizzardBlackout), hook)); }
        private void Postfix(Type type, string name, string hook)
        { harmony.Patch(AccessTools.Method(type, name), postfix: new HarmonyMethod(typeof(BlizzardBlackout), hook)); }

        // Authored office interior roots, shared with the climate room detection.
        internal static bool Office(Transform root)
        {
            for (var t = root; t != null; t = t.parent)
            {
                string n = t.name.Replace("(Clone)", "").TrimEnd();
                if (n == "MilitaryOffice_interior" || (n.Length == 17 && n.StartsWith("Office_", StringComparison.Ordinal) &&
                    n[7] >= '1' && n[7] <= '7' && n.EndsWith("_interior", StringComparison.Ordinal))) return true;
            }
            return false;
        }
        private bool Exempt(Component component)
        {
            if (component == null) return true;
            if (exemptions.TryGetValue(component, out bool value)) return value;
            value = Office(component.transform) || component.GetComponentInParent<TrainCar>() != null ||
                component.GetComponentInParent<DV.CabControls.ItemBase>() != null;
            exemptions[component] = value; return value;
        }
        private void RegisterLight(Light light)
        {
            if (!enabled || light == null || light.type == LightType.Directional || Exempt(light) || lights.ContainsKey(light)) return;
            lights.Add(light, light.enabled); light.enabled = false;
        }
        private void BeforeCamera(Camera camera)
        {
            if (!enabled || camera == null || camera.cameraType != CameraType.Game) return;
            // Native day/night controllers can re-enable a lamp in LateUpdate.
            // Reassert only cached lamps before rendering, without world searches.
            foreach (var lamp in lights) if (lamp.Key != null && lamp.Key.enabled) lamp.Key.enabled = false;
        }
        private static void WindowPower(ref bool enabled) { if (IsOut) enabled = false; }
        private static void ShopLightmaps(ftLightmapsStorage storage)
        { if (IsOut && storage != null) current.shopLighting.Prepare(storage, false); }
        private static void ShopSurfaces(ftLightmapsStorage storage)
        { if (IsOut && storage != null) current.shopLighting.DarkenSurfaces(storage); }
        private static void SpritePower(SpriteLightsEvent __instance)
        {
            if (!IsOut) return;
            foreach (var m in __instance.materials)
            {
                bool changed = m.isOn; m.isOn = false;
                __instance.LightTypeOn[(int)m.lightType] = false;
                if (m.renderers != null) foreach (var r in m.renderers) if (r != null) r.enabled = false;
                if (changed) (SpriteChanged.GetValue(__instance) as Action<SpriteLightsEvent.SpriteLightMaterial>)?.Invoke(m);
            }
        }
        private static void GeneratedPower(GeneratedLightsController __instance, int typeID, ref bool on)
        {
            if (!IsOut) return;
            if (Office(__instance.transform)) { on = true; return; }
            if (current.Exempt(__instance)) return;
            on = false;
        }
        private static bool InitialGeneratedPower(GeneratedLightsController __instance, SpriteLight spriteLight, ref bool __result)
        {
            if (IsOut && Office(__instance.transform)) { __result = true; return false; }
            if (!IsOut || current.Exempt(__instance)) return true;
            __result = false; return false;
        }
        private static void GeneratedRegistered(GeneratedLightsController controller)
        {
            // DV registers the controller immediately BEFORE setting initialized.
            // Its pair arrays are already complete; remember it for restoration.
            if (!IsOut || controller == null || Pairs.GetValue(controller) == null) return;
            current.generated.Add(controller);
            bool office = Office(controller.transform);
            if (!office && current.Exempt(controller)) return;
            for (int i = 0; i < 8; i++) UpdateLightType.Invoke(controller, new object[] { i, office });
        }
        private static void VolumePower(CookeryLightVolumeRenderer __instance, out float? __state)
        {
            __state = null;
            if (!IsOut || current.Exempt(__instance)) return;
            __state = __instance.localMultiplier; __instance.localMultiplier = 0;
        }
        private static void VolumeRestore(CookeryLightVolumeRenderer __instance, float? __state)
        { if (__state.HasValue && __instance != null) __instance.localMultiplier = __state.Value; }

        internal static bool Affected(CashRegisterWithModules register)
        {
            if (register == null) return false;
            for (var parent = register.transform; parent != null; parent = parent.parent)
                if (parent.GetComponent<Shop>() != null) return true;
            if (register.registerModules != null)
                foreach (var module in register.registerModules)
                    if (module is LocoResourceModule || module is ScanItemCashRegisterModule) return true;
            // Career/job terminals and vending machines are deliberately not blocked.
            return false;
        }
        private static bool Buy(CashRegisterWithModules __instance, ref bool __result)
        { if (!IsOut || !Affected(__instance)) return true; __result = false; return false; }
        private static bool Deposit(CashRegisterBase __instance)
        { return !IsOut || !Affected(__instance as CashRegisterWithModules); }
        private static void RegisterDisplay(CashRegisterWithModules __instance)
        {
            if (!IsOut || !Affected(__instance)) return;
            current.Dark(__instance.cashText); current.Dark(__instance.totalText); current.Dark(__instance.infoText);
        }
        private void Dark(TMP_Text text)
        { if (text == null) return; if (!displays.ContainsKey(text)) displays.Add(text, text.enabled); text.enabled = false; }
        private static bool ScannerUse() { return !IsOut; }
        private static bool ScanBuy(ref bool __result) { if (!IsOut) return true; __result = false; return false; }
        private static bool ScannerUpdate(ShopScanner __instance)
        {
            if (!IsOut) return true;
            current.scanners.Add(__instance);
            current.Dark(__instance.nameText); current.Dark(__instance.amountText); current.Dark(__instance.priceText);
            if (__instance.laserBeam != null) __instance.laserBeam.EnableBeam(false);
            return false;
        }
        private static bool ResourceBuy() { return !IsOut; }
        private static bool ResourceReady(ref bool __result)
        { if (!IsOut) return true; __result = false; return false; }
        private static void ResourceUpdate(LocoResourceModule __instance)
        {
            if (!IsOut) return;
            current.Dark(__instance.currentValueText); current.Dark(__instance.statusText);
            current.Dark(__instance.differenceValueStaticText); current.Dark(__instance.differenceValueText);
            current.Dark(__instance.resourceTypeText); current.Dark(__instance.pricePerUnitText); current.Dark(__instance.totalPriceText);
            if (__instance.lamp != null) __instance.lamp.SetLampState(LampControl.LampState.Off);
        }
        private static float CurrentHour()
        {
            var weather = UnityEngine.Object.FindObjectOfType<DV.WeatherSystem.WeatherDriver>();
            return weather == null ? 12f : weather.TimeOfDayHours.CurrentValue;
        }
        private void Restore()
        {
            Camera.onPreCull -= BeforeCamera;
            scan.Dispose();
            shopLighting.Restore();
            foreach (var light in lights) if (light.Key != null) light.Key.enabled = light.Value;
            foreach (var text in displays) if (text.Key != null) text.Key.enabled = text.Value;
            lights.Clear(); displays.Clear(); exemptions.Clear();
            float hour = CurrentHour();
            foreach (var w in windows) if (w != null) w.UpdateTime(hour);
            foreach (var s in sprites) if (s != null) s.UpdateTime(hour);
            foreach (var controller in generated)
            {
                if (controller == null || !(bool)Initialized.GetValue(controller)) continue;
                bool follows = (bool)FollowDay.GetValue(controller), manual = (bool)ManualState.GetValue(controller);
                var evt = AccessTools.Field(typeof(GeneratedLightsController), "spriteLights").GetValue(controller) as SpriteLightsEvent;
                for (int i = 0; i < 8; i++) UpdateLightType.Invoke(controller, new object[] { i, follows && evt != null ? evt.LightTypeOn[i] : manual });
            }
            generated.Clear();
            foreach (var scanner in scanners)
                if (scanner != null && scanner.laserBeam != null) scanner.laserBeam.EnableBeam(scanner.enabled && scanner.gameObject.activeInHierarchy);
            scanners.Clear();
            windows.Clear(); sprites.Clear();
        }
        public void Dispose()
        {
            enabled = false; Restore(); harmony.UnpatchAll(Id); installed = false;
            if (current == this) current = null;
        }
    }
}
