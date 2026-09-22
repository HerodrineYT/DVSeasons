using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DV;
using DV.CashRegister;
using DV.Shops;
using DV.VFX;
using DV.WeatherSystem;
using DVSeasons.Core;
using HarmonyLib;
using UnityEngine;

public static class VerifyBlizzard
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Assembly mod;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static object Make(string name, params object[] args) => Activator.CreateInstance(mod.GetType("DVSeasons.Mod." + name, true), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, args, null);
    static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method, All).Invoke(value, args);
    static void Set(object value, string field, object data) => value.GetType().GetField(field, All).SetValue(value, data);
    static GameObject Root(string name, Transform parent = null)
    { var go = new GameObject(name); go.SetActive(false); if (parent != null) go.transform.SetParent(parent); return go; }
    static bool SkipValidate() => false;
    public static void Run(string path)
    {
        mod = Assembly.LoadFrom(Path.Combine(path, "DVSeasons.dll"));
        var fixture = new Harmony("DVSeasons.VerifyBlizzard");
        fixture.Patch(AccessTools.Method(typeof(TOD_Sky), "OnValidate"), prefix: new HarmonyMethod(typeof(VerifyBlizzard), nameof(SkipValidate)));
        try { RadioVolume(); Weather(); Power(); ShopLighting(); ShopRealtimeRender(); SnowWalls(); SnowCollisionBudget(); Save(path); Debug.Log("BLIZZARD_RUNTIME_OK: radio gain, weather, ownership, packet, offices/stations, shop lightmaps, snow walls, restoration and save cooldown"); }
        finally { fixture.UnpatchAll("DVSeasons.VerifyBlizzard"); }
    }
    static void RadioVolume()
    {
        var root = Root("Radio volume fixture");
        var settings = Make("SeasonModSettings");
        var volume = settings.GetType().GetField("BlizzardRadioVolume");
        Check((float)volume.GetValue(settings) == 1f, "Radio default must be 100%");
        var audio = root.AddComponent(mod.GetType("DVSeasons.Mod.BlizzardAudio", true));
        Set(audio, "settings", settings); Call(audio, "CreateSources");
        var voice = (AudioSource)audio.GetType().GetField("voice", All).GetValue(audio);
        var wind = (AudioSource)audio.GetType().GetField("wind", All).GetValue(audio);
        var gain = voice.GetComponent(mod.GetType("DVSeasons.Mod.BlizzardRadioGain", true));
        var clip = AudioClip.Create("Radio gain seek fixture", 48000, 1, 48000, false);
        try
        {
            Check(gain != null && voice.gameObject != wind.gameObject && wind.GetComponent(gain.GetType()) == null,
                "Radio gain is not isolated from wind");
            voice.clip = clip; voice.timeSamples = 12000; wind.volume = .65f;
            foreach (float level in new[] { 0f, .5f, 1f, 2f, 3f, 1f })
            {
                volume.SetValue(settings, level); Call(settings, "Clamp"); Call(audio, "ApplyRadioVolume");
                Check((float)volume.GetValue(settings) == level, "Radio settings clamped a valid level");
                // Stereo PCM probes: check both channels and both polarities.
                var pcm = new[] { .1f, -.1f, .2f, -.2f };
                Call(gain, "OnAudioFilterRead", pcm, 2);
                for (int i = 0; i < pcm.Length; i++)
                {
                    float original = (i < 2 ? .1f : .2f) * (i % 2 == 0 ? 1 : -1);
                    Check(Math.Abs(pcm[i] * voice.volume - original * level) < .00001f,
                        "Effective radio amplitude mismatch at " + level);
                }
                Check(voice.clip == clip && voice.timeSamples == 12000, "Volume change reset broadcast position");
                Check(wind.volume == .65f, "Radio setting changed wind volume");
            }
            foreach (float invalid in new[] { -1f, 9f, float.NaN, float.PositiveInfinity })
            {
                volume.SetValue(settings, invalid); Call(settings, "Clamp");
                float value = (float)volume.GetValue(settings);
                Check(value >= 0 && value <= 3, "Invalid persisted radio setting");
            }
            Debug.Log("BLIZZARD_RADIO_VOLUME_OK: default 100%, 0/50/100/200/300%, stereo PCM gain, unchanged seek, isolated wind, settings bounds");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(clip); }
    }
    static void Weather()
    {
        var go = Root("Blizzard weather fixture");
        var driver = go.AddComponent<WeatherDriver>();
        driver.manager = go.AddComponent<WeatherPresetManager>();
        driver.manager.todSky = go.AddComponent<TOD_Sky>();
        driver.manager.todSky.Cycle = new TOD_CycleParameters();
        driver.manager.todSky.Cycle.RealDateTime = new DateTime(2026, 1, 1);
        driver.manager.todTime = go.AddComponent<TOD_Time>();
        driver.todAnimation = go.AddComponent<TOD_Animation>();
        var adapter = Make("WeatherAdapter"); Set(adapter, "driver", driver);
        var settings = Make("SeasonModSettings");
        driver.RainValue.EngageOverride(.17f); driver.WindSpeed.EngageOverride(2f);
        driver.WeatherPointX.EngageOverride(.3f); driver.WetnessValue.RealValue = .1f;
        var winter = new SeasonState(3, SeasonKind.Winter, SeasonKind.Spring, 0, 1, -20, .4f);
        Call(adapter, "ApplyWinterAdhesion", winter, true, false);
        Call(adapter, "ApplyWinterThunderSuppression", winter, true);
        Call(adapter, "ApplyBlizzard", true);
        Check(driver.RainValue.CurrentValue == 1 && driver.WetnessValue.CurrentValue == 1 && driver.WindSpeed.CurrentValue == 10, "Storm maxima");
        var snapshot = (WeatherNetworkState)Call(adapter, "CaptureNetworkWeather", settings);
        Check(snapshot.Values[0] == .17f && snapshot.Values[3] == 2 && (snapshot.Overrides & 6) == 0, "Storm/season leaked into underlying MP weather");
        Check(driver.RainValue.CurrentValue == 1 && driver.WetnessValue.CurrentValue == 1, "Capture removed storm");
        driver.CurrentChungusState.currentLow.DisplayFogHeightDensity = 1f;
        driver.CurrentChungusState.currentHigh.DisplayFogHeightDensity = 1f;
        Call(adapter, "ApplyBlizzardSky", driver);
        Check(driver.CurrentChungusState.currentLow.cloudCoverage == 1 && driver.CurrentChungusState.currentHigh.fogginess == 1, "Storm sky zones");
        foreach (var zone in new[] { driver.CurrentChungusState.currentLow, driver.CurrentChungusState.currentHigh })
        {
            Check(Math.Abs(zone.DisplayFogDensity / .012f - 1.75f) < .0001f, "Storm visibility multiplier");
            Check(zone.DisplayFogHeightDensity == 0f && zone.DisplayFogDistanceDensity > .99f,
                "Storm inherited a second height-fog blanket");
        }
        Call(adapter, "ApplyBlizzard", false);
        Check(driver.RainValue.CurrentValue == .17f && driver.WindSpeed.CurrentValue == 2 && driver.WeatherPointX.CurrentValue == .3f, "Manual weather not restored");
        Check(Math.Abs(driver.WetnessValue.CurrentValue - .4f) < .001, "Underlying winter adhesion not restored");
        ((IDisposable)adapter).Dispose(); UnityEngine.Object.DestroyImmediate(go);
        Debug.Log("BLIZZARD_WEATHER_OK");
    }
    static void Power()
    {
        var root = Root("Ordinary station");
        var office = Root("Office_1_interior", root.transform);
        var yard = Root("Station platform light", root.transform);
        var light = yard.AddComponent<Light>(); light.type = LightType.Point; light.enabled = true;
        var officeLight = office.AddComponent<Light>(); officeLight.enabled = true;
        var window = root.AddComponent<WindowsLightEvent>();
        var material = new Material(Shader.Find("Standard"));
        window.materials = new[] { material }; window.fromTime = 6; window.toTime = 18; window.Initialize(); window.UpdateTime(22);
        Check(material.IsKeywordEnabled("_EMISSION"), "Window fixture starts dark");
        var sprite = root.AddComponent<SpriteLightsEvent>();
        var render = yard.AddComponent<MeshRenderer>(); render.enabled = true;
        sprite.materials.Add(new SpriteLightsEvent.SpriteLightMaterial { fromTime = 6, toTime = 18, isOn = true,
            lightType = SpriteLightType.StreetSpriteLight, material = material, renderers = new List<MeshRenderer> { render } });
        var service = root.AddComponent<CashRegisterWithModules>();
        var resource = root.AddComponent<LocoResourceModule>(); service.registerModules = new CashRegisterModule[] { resource };
        var shopRoot = Root("Shop", root.transform); shopRoot.AddComponent<Shop>();
        var shop = shopRoot.AddComponent<CashRegisterWithModules>();
        var machine = office.AddComponent<CashRegisterWithModules>(); machine.registerModules = new CashRegisterModule[0];
        var power = Make("BlizzardBlackout");
        try
        {
            Call(power, "Apply", true);
            Call(power, "RegisterLight", light); Call(power, "RegisterLight", officeLight);
            window.UpdateTime(22); sprite.UpdateTime(22);
            Check(!light.enabled && officeLight.enabled, "Station/office power separation");
            Check(!material.IsKeywordEnabled("_EMISSION") && !render.enabled, "Windows/street sprites still lit");
            Check(!service.Buy(), "Service purchase not blocked before native cash handling");
            Check(!shop.Buy(), "Shop purchase not blocked before native cash handling");
            var affected = power.GetType().GetMethod("Affected", All);
            Check(!(bool)affected.Invoke(null, new object[] { machine }), "Office machine blocked");
            Check(!resource.IsReady, "Fuel flow still ready");
            var exterior = root.AddComponent<GeneratedLightsController>(); Set(exterior, "followDayNightCycle", false); Set(exterior, "manualState", true);
            var interior = office.AddComponent<GeneratedLightsController>(); Set(interior, "followDayNightCycle", false); Set(interior, "manualState", true);
            var street = yard.AddComponent<StreetSpriteLight>();
            Check(!(bool)Call(exterior, "ShouldBeOn", street) && (bool)Call(interior, "ShouldBeOn", street), "Generated office/outdoor lights");
            Call(power, "Apply", false);
            window.UpdateTime(22); sprite.UpdateTime(22);
            Check(light.enabled && officeLight.enabled && material.IsKeywordEnabled("_EMISSION") && render.enabled, "Power restoration");
            Check((bool)Call(exterior, "ShouldBeOn", street), "Generated light stayed off");
            Debug.Log("BLIZZARD_POWER_OK: normal station, office, window, street sprites, pump and shop");
        }
        finally { ((IDisposable)power).Dispose(); UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(material); }
    }
    static ftLightmapsStorage BakedRoom(string name, Transform parent, Texture2D texture, Material material)
    {
        var room = Root(name, parent);
        var renderer = room.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
        // Match the shipped shop: realtime lamps are NOT in bakedLights.
        var lamps = Root("CeilingLights", room.transform);
        for (int i = 0; i < 4; i++)
        {
            var lamp = Root("Point light " + i, lamps.transform).AddComponent<Light>();
            lamp.type = LightType.Point; lamp.enabled = i != 3;
        }
        var storage = Root("BakeryPrefabLightmapData", room.transform).AddComponent<ftLightmapsStorage>();
        storage.maps.Add(texture); storage.bakedRenderers.Add(renderer); storage.bakedIDs.Add(0);
        storage.bakedScaleOffset.Add(new Vector4(1, 1, 0, 0));
        ftLightmaps.RefreshScene(room.scene, storage);
        return storage;
    }
    static Texture2D BoundLightmap(ftLightmapsStorage storage)
    { return LightmapSettings.lightmaps[storage.bakedRenderers[0].lightmapIndex].lightmapColor; }
    static void ShopLighting()
    {
        var oldMaps = LightmapSettings.lightmaps;
        var root = Root("Shop lighting fixture");
        var texture = new Texture2D(2, 2); texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white }); texture.Apply();
        var material = new Material(Shader.Find("Standard")); material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", Color.white);
        var shop = BakedRoom("ItemShop_interior", root.transform, texture, material);
        var props = BakedRoom("PropsShop", root.transform, texture, material);
        var office = BakedRoom("Office_1_interior", root.transform, texture, material);
        var originalMaps = shop.maps;
        ftLightmapsStorage streamed = null;
        var power = Make("BlizzardBlackout");
        // Do not let the general world walker hide missing local registration.
        Set(power.GetType().GetField("scan", All).GetValue(power), "nextScan", float.MaxValue);
        try
        {
            Call(power, "Apply", true);
            foreach (var lamp in shop.transform.parent.GetComponentsInChildren<Light>(true)) Check(!lamp.enabled, "Realtime shop lamp missed by local discovery");
            foreach (var lamp in office.transform.parent.GetComponentsInChildren<Light>(true)) Check(lamp.enabled == !lamp.name.EndsWith("3"), "Office lamp changed");
            Check(BoundLightmap(shop) == Texture2D.blackTexture && BoundLightmap(props) == Texture2D.blackTexture, "Shop baked lighting still on");
            Check(BoundLightmap(office) == texture && material.IsKeywordEnabled("_EMISSION"), "Shop outage modified office/shared material");
            Check(!shop.bakedRenderers[0].sharedMaterial.IsKeywordEnabled("_EMISSION"), "Shop ceiling still glows");
            streamed = BakedRoom("ItemShop_interior", root.transform, texture, material);
            ftLightmaps.RefreshScene2(root.scene, streamed);
            Check(BoundLightmap(streamed) == Texture2D.blackTexture, "Streamed shop/Start restored baked light during outage");
            foreach (var lamp in streamed.transform.parent.GetComponentsInChildren<Light>(true)) Check(!lamp.enabled, "Streamed realtime shop lamp stayed on");
            // Reassert cached lights if a native distance/day-night controller
            // switches one on after Update but before the camera renders.
            var relit = shop.transform.parent.GetComponentInChildren<Light>(true); relit.enabled = true;
            var cameraObject = new GameObject("Shop camera reassertion fixture");
            try { Call(power, "BeforeCamera", cameraObject.AddComponent<Camera>()); Check(!relit.enabled, "Late native lamp activation leaked into the frame"); }
            finally { UnityEngine.Object.DestroyImmediate(cameraObject); }
            Call(power, "Apply", false);
            Check(ReferenceEquals(shop.maps, originalMaps) && BoundLightmap(shop) == texture && BoundLightmap(props) == texture &&
                BoundLightmap(streamed) == texture, "Shop lightmap restoration");
            Check(shop.bakedRenderers[0].sharedMaterial == material && material.IsKeywordEnabled("_EMISSION"), "Lamp restoration");
            foreach (var storage in new[] { shop, streamed })
                foreach (var lamp in storage.transform.parent.GetComponentsInChildren<Light>(true))
                    Check(lamp.enabled == !lamp.name.EndsWith("3"), "Realtime lamp original state not restored");
            Call(power, "Apply", true); Call(power, "Apply", false);
            Check(BoundLightmap(shop) == texture && BoundLightmap(office) == texture, "Repeated blackout changed maps");
            Debug.Log("BLIZZARD_SHOP_LIGHTING_OK: four realtime lights with EMPTY bakedLights/world scan suspended, inactive fixtures, pre-cull reassertion, streamed lights, baked room/furniture, emission, office isolation, two restoration cycles");
        }
        finally
        {
            ((IDisposable)power).Dispose(); UnityEngine.Object.DestroyImmediate(root);
            LightmapSettings.lightmaps = oldMaps;
            UnityEngine.Object.DestroyImmediate(texture); UnityEngine.Object.DestroyImmediate(material);
        }
    }
    static float CaptureShop(Camera camera, string label)
    {
        camera.Render(); var previous = RenderTexture.active; RenderTexture.active = camera.targetTexture;
        var pixels = new Texture2D(64, 64, TextureFormat.RGBA32, false, true);
        try
        {
            pixels.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); pixels.Apply();
            double brightness = 0;
            foreach (var pixel in pixels.GetPixels32()) brightness += (pixel.r + pixel.g + pixel.b) / (3d * 255);
            string file = Path.GetFullPath(Path.Combine(Application.dataPath, "../../artifacts/verification/shop-power-" + label + ".png"));
            File.WriteAllBytes(file, pixels.EncodeToPNG());
            return (float)(brightness / (64 * 64));
        }
        finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(pixels); }
    }
    static void ShopRealtimeRender()
    {
        var oldMaps = LightmapSettings.lightmaps;
        var oldAmbient = RenderSettings.ambientMode; var oldColor = RenderSettings.ambientLight;
        float oldReflection = RenderSettings.reflectionIntensity; bool oldFog = RenderSettings.fog;
        var root = Root("Shop realtime GPU fixture");
        var material = new Material(Shader.Find("Standard")); material.SetFloat("_Glossiness", 0); material.SetFloat("_Metallic", 0);
        var storage = BakedRoom("ItemShop_interior", root.transform, Texture2D.blackTexture, material);
        var room = storage.transform.parent.gameObject; room.layer = 30; room.transform.localPosition = new Vector3(0, 0, 3);
        var mesh = new Mesh { vertices = new[] { new Vector3(-1,-1,0), new Vector3(1,-1,0), new Vector3(1,1,0), new Vector3(-1,1,0) },
            triangles = new[] { 0,2,1,0,3,2 }, normals = new[] { Vector3.back,Vector3.back,Vector3.back,Vector3.back },
            uv = new[] { Vector2.zero,Vector2.right,Vector2.one,Vector2.up }, uv2 = new[] { Vector2.zero,Vector2.right,Vector2.one,Vector2.up } };
        room.AddComponent<MeshFilter>().sharedMesh = mesh; mesh.RecalculateBounds();
        foreach (var lamp in room.GetComponentsInChildren<Light>(true))
        {
            lamp.transform.localPosition = new Vector3(0,0,-1); lamp.range = 8; lamp.intensity = 1;
            lamp.cullingMask = 1 << 30; lamp.renderMode = LightRenderMode.ForcePixel;
            lamp.gameObject.SetActive(true); lamp.transform.parent.gameObject.SetActive(true);
        }
        root.SetActive(true); room.SetActive(true);
        var cameraObject = new GameObject("Shop GPU camera"); var camera = cameraObject.AddComponent<Camera>();
        camera.cullingMask = 1 << 30; camera.orthographic = true; camera.orthographicSize = 1.3f;
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
        camera.renderingPath = RenderingPath.DeferredShading; camera.allowHDR = true;
        var target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGBHalf); target.Create(); camera.targetTexture = target;
        var flashlightObject = new GameObject("Independent portable-light fixture");
        var flashlight = flashlightObject.AddComponent<Light>(); flashlight.type = LightType.Spot;
        flashlight.spotAngle = 80; flashlight.range = 8; flashlight.intensity = 3; flashlight.cullingMask = 1 << 30;
        flashlight.renderMode = LightRenderMode.ForcePixel; flashlight.enabled = false;
        var power = Make("BlizzardBlackout");
        Set(power.GetType().GetField("scan", All).GetValue(power), "nextScan", float.MaxValue);
        try
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat; RenderSettings.ambientLight = Color.black;
            RenderSettings.reflectionIntensity = 0; RenderSettings.fog = false;
            float lit = CaptureShop(camera, "lit");
            Check(lit > .03f, "GPU shop fixture has no visible realtime illumination");
            Call(power, "Apply", true);
            float dark = CaptureShop(camera, "outage");
            Check(dark < lit * .02f + .003f, "Shop realtime lighting still reaches framebuffer during blackout");
            flashlight.enabled = true;
            float portable = CaptureShop(camera, "flashlight");
            Check(portable > .03f, "Blackout prevented independent flashlight rendering");
            flashlight.enabled = false; Call(power, "Apply", false);
            float restored = CaptureShop(camera, "restored");
            Check(Math.Abs(restored - lit) < .01f, "GPU shop illumination not restored");
            Debug.Log("BLIZZARD_SHOP_GPU_OK: deferred mean luminance lit=" + lit + " outage=" + dark + " flashlight=" + portable + " restored=" + restored);
        }
        finally
        {
            ((IDisposable)power).Dispose(); camera.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(cameraObject); UnityEngine.Object.DestroyImmediate(flashlightObject);
            UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(material); target.Release(); UnityEngine.Object.DestroyImmediate(target);
            LightmapSettings.lightmaps = oldMaps; RenderSettings.ambientMode = oldAmbient; RenderSettings.ambientLight = oldColor;
            RenderSettings.reflectionIntensity = oldReflection; RenderSettings.fog = oldFog;
        }
    }
    static void SnowWalls()
    {
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "Offscreen wall"; wall.layer = 0;
        wall.transform.position = new Vector3(0, 1.5f, -2); wall.transform.localScale = new Vector3(20, 5, .3f);
        wall.GetComponent<MeshRenderer>().enabled = false;
        var cameraObject = new GameObject("Snow wall test camera"); var camera = cameraObject.AddComponent<Camera>();
        cameraObject.transform.position = new Vector3(0, 1.5f, 0);
        var particleObject = new GameObject("Snow interception fixture"); var particles = particleObject.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = particles.main; main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 100; main.startSpeed = 0;
        particles.Play(); particles.Simulate(.01f, false, true); particles.Pause(); particles.Clear();
        var cache = Make("SnowfallWorldCollision");
        try
        {
            Physics.SyncTransforms(); Call(cache, "Prepare", cameraObject.transform.position);
            var a = new Vector3(0, 1.5f, -3); var b = new Vector3(0, 1.5f, -1);
            Check((bool)Call(cache, "Blocked", a, b), "Hidden wall failed to block snow");
            cameraObject.transform.rotation = Quaternion.Euler(0, 180, 0);
            Call(cache, "Prepare", cameraObject.transform.position);
            Check((bool)Call(cache, "Blocked", a, b), "Turning changed wall collision");
            Check(!(bool)Call(cache, "Blocked", a + Vector3.right * 15, b + Vector3.right * 15), "Wall blocked open air");
            var visual = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(mod.GetType("DVSeasons.Mod.SeasonVisualController", true));
            Set(visual, "snowSystem", particles); Set(visual, "snowCollisions", cache);
            Set(visual, "lastWindowParticleCheck", Time.time - .1f);
            var flakes = new[] {
                new ParticleSystem.Particle { position = b, velocity = new Vector3(0, 0, 20), startLifetime = 5, remainingLifetime = 4, startSize = .1f, randomSeed = 1 },
                new ParticleSystem.Particle { position = b + Vector3.forward * 2, velocity = new Vector3(0, 0, 20), startLifetime = 5, remainingLifetime = 4.99f, startSize = .1f, randomSeed = 2 },
                new ParticleSystem.Particle { position = b + Vector3.right * 15, velocity = new Vector3(0, 0, 20), startLifetime = 5, remainingLifetime = 4, startSize = .1f, randomSeed = 3 }
            };
            particles.SetParticles(flakes, flakes.Length);
            Check(particles.particleCount == 3, "Particle fixture failed to initialize");
            Call(visual, "InterceptSnow", camera, false);
            var result = new ParticleSystem.Particle[8]; int count = particles.GetParticles(result); bool outdoorAlive = false;
            for (int i = 0; i < count; i++)
                if (result[i].remainingLifetime > 0)
                { Check(result[i].randomSeed == 3, "Flake crossed wall or spawned behind shelter"); outdoorAlive = true; }
            Check(outdoorAlive, "Open-air snowfall removed");
            wall.transform.position += Vector3.right * 30; Physics.SyncTransforms(); Call(cache, "Prepare", cameraObject.transform.position);
            Check(!(bool)Call(cache, "Blocked", a, b), "Moved wall stayed cached at old position");
            Debug.Log("BLIZZARD_SNOW_WALLS_OK: invisible wall behind camera, view rotation, open air, newborn/crossing flakes, windows disabled, moving wall");
        }
        finally
        { UnityEngine.Object.DestroyImmediate(wall); UnityEngine.Object.DestroyImmediate(cameraObject); UnityEngine.Object.DestroyImmediate(particleObject); }
    }
    static void SnowCollisionBudget()
    {
        var root = new GameObject("Snow collision density fixture");
        var rng = new System.Random(91);
        var cache = Make("SnowfallWorldCollision");
        var blocked = (Func<Vector3, Vector3, bool>)Delegate.CreateDelegate(typeof(Func<Vector3, Vector3, bool>), cache, cache.GetType().GetMethod("Blocked", All));
        try
        {
            for (int i = 0; i < 512; i++)
            {
                var go = new GameObject("solid"); go.transform.SetParent(root.transform);
                go.transform.position = new Vector3((i % 32 - 15.5f) * 1.8f, (i % 5) * 1.5f, (i / 32 - 7.5f) * 2);
                go.AddComponent<BoxCollider>().size = new Vector3(1.4f, 2, .3f);
            }
            Physics.SyncTransforms(); Call(cache, "Prepare", Vector3.zero);
            for (int i = 0; i < 2000; i++)
            {
                var from = new Vector3((float)rng.NextDouble() * 32 - 16, (float)rng.NextDouble() * 12, (float)rng.NextDouble() * 32 - 16);
                var delta = new Vector3((float)rng.NextDouble() * 4 - 2, -.5f, (float)rng.NextDouble() * 4 - 2);
                bool expected = Physics.Raycast(from, delta.normalized, delta.magnitude + .01f, 1, QueryTriggerInteraction.Ignore);
                Check(blocked(from, from + delta) == expected, "Spatial collision cache differs from PhysX");
            }
            var positions = new Vector3[22500];
            for (int i = 0; i < positions.Length; i++) positions[i] = new Vector3((float)rng.NextDouble() * 80 - 40,
                (float)rng.NextDouble() * 36 - 12, (float)rng.NextDouble() * 60 - 30);
            var timer = System.Diagnostics.Stopwatch.StartNew(); int queries = 0; long preparationTicks = 0;
            for (int pass = 0; pass < 20; pass++)
            {
                long prepareStart = timer.ElapsedTicks; Call(cache, "Prepare", Vector3.zero); preparationTicks += timer.ElapsedTicks - prepareStart;
                for (int i = 0; i < positions.Length; i++)
                {
                    var position = positions[i]; if (position.sqrMagnitude > 400) continue;
                    blocked(position - (i % 40 == 0 ? new Vector3(24, 20, 0) : new Vector3(1.2f, -.35f, 0)), position); queries++;
                }
            }
            timer.Stop();
            Debug.Log("BLIZZARD_SNOW_COLLISION_BUDGET: 512 colliders, 22500 flakes, local checks/update=" + queries / 20 +
                ", mean collision update ms=" + timer.Elapsed.TotalMilliseconds / 20 + ", preparation ms=" + preparationTicks * 1000d / System.Diagnostics.Stopwatch.Frequency / 20 + "; 2000 PhysX parity rays passed (synthetic CPU fixture, not game FPS)");
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }
    static void Save(string path)
    {
        var controller = Make("BlizzardController", path, Make("SeasonModSettings"));
        var state = (BlizzardState)controller.GetType().GetProperty("State", All).GetValue(controller, null);
        state.Schedule(new System.Random(4), DateTime.UtcNow.Ticks);
        state.Advance(state.StartHours + .4, true, true, new System.Random(7), DateTime.UtcNow.Ticks);
        state.Cancel(); double earliest = state.EarliestStartHours;
        var data = new SaveGameData(); Call(controller, "Save", data);
        Call(controller, "Restore", data);
        var restored = (BlizzardState)controller.GetType().GetProperty("State", All).GetValue(controller, null);
        Check(restored.IsValid() && restored.EarliestStartHours == earliest && earliest >= restored.Hours + 240, "Save lost ten-day cooldown");
        ((IDisposable)controller).Dispose(); Debug.Log("BLIZZARD_SAVE_OK");
    }
}
