using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    // Shops are streamed separately from Shop/checkout logic. Their ceiling lamps
    // are baked into Bakery maps, so disabling Unity Lights alone cannot darken
    // them. Remap only the authored shop interiors through Bakery's own loader;
    // never modify a shared lightmap/material used by offices or other buildings.
    internal sealed class BlizzardShopLighting
    {
        private sealed class Maps
        {
            internal List<Texture2D> Color, Rnm0, Rnm1, Rnm2;
        }
        private sealed class Surface
        {
            internal Material[] Original, Dark;
            internal ReflectionProbeUsage Reflections;
        }
        private readonly Action<Light> darkenLight;
        private readonly Dictionary<ftLightmapsStorage, Maps> storages = new Dictionary<ftLightmapsStorage, Maps>();
        private readonly Dictionary<Renderer, Surface> surfaces = new Dictionary<Renderer, Surface>();
        private readonly Dictionary<Material, Material> materials = new Dictionary<Material, Material>();
        private readonly HashSet<Transform> lightRoots = new HashSet<Transform>();
        private readonly List<Light> lightBuffer = new List<Light>();

        internal BlizzardShopLighting(Action<Light> darkenLight) { this.darkenLight = darkenLight; }

        internal void Begin()
        {
            // Once per outage; subsequent shops arrive through RefreshScene/Awake.
            // Include inactive distance-culled shops, but not prefab assets.
            foreach (var storage in Resources.FindObjectsOfTypeAll<ftLightmapsStorage>())
            {
                if (storage == null || !storage.gameObject.scene.IsValid() || !storage.gameObject.scene.isLoaded) continue;
                if (!Prepare(storage, true)) continue;
                ftLightmaps.RefreshScene(storage.gameObject.scene, storage);
                DarkenSurfaces(storage);
            }
        }

        private static Transform ShopInterior(Transform node)
        {
            for (var t = node; t != null; t = t.parent)
                if (t.name == "ItemShop_interior" || t.name == "PropsShop") return t;
            return null;
        }

        internal bool Prepare(ftLightmapsStorage storage, bool alreadyLoaded)
        {
            if (storage == null || storages.ContainsKey(storage) || ShopInterior(storage.transform) == null) return false;
            var original = new Maps { Color = storage.maps, Rnm0 = storage.rnmMaps0,
                Rnm1 = storage.rnmMaps1, Rnm2 = storage.rnmMaps2 };
            storages.Add(storage, original);
            // Balance Bakery's reference counts before rebinding an existing
            // scene. Newly streamed storage has not registered its maps yet.
            if (alreadyLoaded) ftLightmaps.UnloadScene(storage);
            storage.maps = BlackMaps(original.Color);
            storage.rnmMaps0 = BlackMaps(original.Rnm0);
            storage.rnmMaps1 = BlackMaps(original.Rnm1);
            storage.rnmMaps2 = BlackMaps(original.Rnm2);
            return true;
        }

        private static List<Texture2D> BlackMaps(List<Texture2D> source)
        {
            var result = new List<Texture2D>(source.Count);
            for (int i = 0; i < source.Count; i++) result.Add(Texture2D.blackTexture);
            return result;
        }

        internal void DarkenSurfaces(ftLightmapsStorage storage)
        {
            if (!storages.ContainsKey(storage)) return;
            foreach (var renderer in storage.bakedRenderers)
            {
                if (renderer == null || surfaces.ContainsKey(renderer)) continue;
                var surface = new Surface { Original = renderer.sharedMaterials, Reflections = renderer.reflectionProbeUsage };
                for (int i = 0; i < surface.Original.Length; i++)
                {
                    var original = surface.Original[i];
                    if (original == null || !original.IsKeywordEnabled("_EMISSION")) continue;
                    if (!materials.TryGetValue(original, out var dark))
                    {
                        dark = new Material(original) { name = original.name + " (blizzard blackout)", hideFlags = HideFlags.HideAndDontSave };
                        dark.DisableKeyword("_EMISSION");
                        if (dark.HasProperty("_EmissionColor")) dark.SetColor("_EmissionColor", Color.black);
                        materials.Add(original, dark);
                    }
                    if (surface.Dark == null) surface.Dark = (Material[])surface.Original.Clone();
                    surface.Dark[i] = dark;
                }
                if (surface.Dark != null) renderer.sharedMaterials = surface.Dark;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                surfaces.Add(renderer, surface);
            }
            // The shipped shop has four REALTIME point lights (bake type 4),
            // while its storage.bakedLights is empty. Discover the small room
            // hierarchy immediately, including distance-disabled fixtures. The
            // incremental whole-world scan can take a long time to reach it.
            var root = ShopInterior(storage.transform);
            if (root != null && lightRoots.Add(root))
            {
                lightBuffer.Clear();
                root.GetComponentsInChildren(true, lightBuffer);
                foreach (var lamp in lightBuffer) if (lamp != null) darkenLight(lamp);
                lightBuffer.Clear();
            }
            foreach (var lamp in storage.bakedLights) if (lamp != null) darkenLight(lamp);
        }

        internal void Restore()
        {
            foreach (var pair in storages)
            {
                var storage = pair.Key;
                if (storage == null) continue;
                ftLightmaps.UnloadScene(storage);
                storage.maps = pair.Value.Color; storage.rnmMaps0 = pair.Value.Rnm0;
                storage.rnmMaps1 = pair.Value.Rnm1; storage.rnmMaps2 = pair.Value.Rnm2;
                ftLightmaps.RefreshScene(storage.gameObject.scene, storage);
            }
            storages.Clear();
            foreach (var pair in surfaces)
            {
                if (pair.Key == null) continue;
                if (pair.Value.Dark != null) pair.Key.sharedMaterials = pair.Value.Original;
                pair.Key.reflectionProbeUsage = pair.Value.Reflections;
            }
            surfaces.Clear();
            lightRoots.Clear(); lightBuffer.Clear();
            foreach (var material in materials.Values) UnityEngine.Object.Destroy(material);
            materials.Clear();
        }
    }
}
