using System.Collections.Generic;
using System.Reflection;
using DV.UI.LocoHUD;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace DVSeasons.Mod
{
    // The native HUD hint is anchored to the screen corner, independently of
    // the weather panel. Keep weather-control names in that panel's header.
    internal sealed class WeatherEditorHintLayout : MonoBehaviour
    {
        private static readonly FieldInfo TextField = AccessTools.Field(typeof(HUDHint), "text");
        private static readonly MethodInfo NativeHoverChanged = AccessTools.Method(typeof(HUDHint), "HUDHoverManagerOnHoveredChangedAll");
        private static readonly object[] NoHoveredControl = { null };
        private static readonly List<WeatherEditorHintLayout> instances = new List<WeatherEditorHintLayout>();
        private RectTransform textRect, originalParent, header;
        private PhotoModeWeatherController weather;
        private int originalSibling;
        private Vector2 originalAnchorMin, originalAnchorMax, originalPivot, originalSize;
        private Vector3 originalPosition, originalScale;
        private Quaternion originalRotation;
        private bool attached;

        internal static void HoverChanged(HUDHint hint, LocoHUDControlBase control)
        {
            if (hint == null) return;
            var layout = hint.GetComponent<WeatherEditorHintLayout>();
            var panel = control != null ? control.GetComponentInParent<PhotoModeWeatherController>() : null;
            var canvas = panel != null ? panel.GetComponentInParent<Canvas>() : null;
            if (panel == null || panel.panel == null || !panel.panel.open ||
                (canvas != null && canvas.renderMode == RenderMode.WorldSpace))
            {
                if (layout != null) layout.Restore();
                return;
            }
            var text = TextField.GetValue(hint) as Component;
            if (text == null || !(text.transform is RectTransform)) return;
            if (layout == null)
            {
                layout = hint.gameObject.AddComponent<WeatherEditorHintLayout>();
                instances.Add(layout);
            }
            layout.Attach((RectTransform)text.transform, panel);
        }

        internal void Attach(RectTransform text, PhotoModeWeatherController panel)
        {
            if (attached && textRect == text && weather == panel) return;
            Restore();
            var parent = panel.transform as RectTransform;
            if (parent == null || text == null || !(text.parent is RectTransform)) return;
            textRect = text; weather = panel; originalParent = (RectTransform)text.parent;
            originalSibling = text.GetSiblingIndex();
            originalAnchorMin = text.anchorMin; originalAnchorMax = text.anchorMax;
            originalPivot = text.pivot; originalSize = text.sizeDelta;
            originalPosition = text.anchoredPosition3D; originalScale = text.localScale;
            originalRotation = text.localRotation;
            Vector3 worldScale = text.lossyScale;
            if (header == null)
            {
                var obj = new GameObject("DVSeasons weather hint", typeof(RectTransform), typeof(RectMask2D), typeof(CanvasGroup));
                header = (RectTransform)obj.transform;
                var group = obj.GetComponent<CanvasGroup>();
                group.blocksRaycasts = false; group.interactable = false;
            }
            header.SetParent(parent, false);
            header.anchorMin = new Vector2(0f, 1f); header.anchorMax = new Vector2(.42f, 1f);
            header.pivot = new Vector2(0f, 1f); header.anchoredPosition = new Vector2(12f, -3f);
            header.sizeDelta = new Vector2(-24f, 29f);
            header.gameObject.SetActive(true);
            text.SetParent(header, false);
            text.anchorMin = text.anchorMax = text.pivot = new Vector2(0f, 1f);
            text.anchoredPosition3D = Vector3.zero; text.localRotation = Quaternion.identity;
            Vector3 parentScale = header.lossyScale;
            text.localScale = new Vector3(Ratio(worldScale.x,parentScale.x), Ratio(worldScale.y,parentScale.y), Ratio(worldScale.z,parentScale.z));
            // Retain the native font size and content sizing; the header clips
            // unexpectedly long localized names before they overlap its title.
            attached = true;
            enabled = true;
        }

        private static float Ratio(float value,float scale) {return Mathf.Abs(scale)>.00001f?value/scale:1f;}

        private void LateUpdate()
        {
            if (attached && (weather == null || !weather.isActiveAndEnabled ||
                weather.panel == null || !weather.panel.open || !weather.panel.visible ||
                !weather.gameObject.activeInHierarchy))
            {
                // Closing the animated panel need not deliver a pointer exit.
                // Clear its last name before restoring the shared screen hint.
                var hint = GetComponent<HUDHint>();
                if (hint != null) NativeHoverChanged.Invoke(hint, NoHoveredControl);
                Restore();
            }
        }

        internal void Restore()
        {
            if (attached && textRect != null && originalParent != null)
            {
                textRect.SetParent(originalParent, false);
                textRect.SetSiblingIndex(originalSibling);
                textRect.anchorMin = originalAnchorMin; textRect.anchorMax = originalAnchorMax;
                textRect.pivot = originalPivot; textRect.sizeDelta = originalSize;
                textRect.anchoredPosition3D = originalPosition; textRect.localScale = originalScale;
                textRect.localRotation = originalRotation;
            }
            attached = false; weather = null; textRect = null; originalParent = null;
            if (header != null) header.gameObject.SetActive(false);
            enabled = false;
        }

        private void OnDisable() { if (attached) Restore(); }
        private void OnDestroy()
        {
            Restore();
            if (header != null) Destroy(header.gameObject);
            instances.Remove(this);
        }

        internal static void RestoreAll()
        {
            for (int i = instances.Count - 1; i >= 0; i--)
                if (instances[i] != null) instances[i].Restore();
            instances.RemoveAll(item => item == null);
        }
    }
}
