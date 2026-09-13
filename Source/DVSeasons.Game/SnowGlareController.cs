using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    // Compress bright neutral surfaces before native bloom/tone mapping. Sky and
    // dark cabin surfaces are excluded; no exposure/time/weather overrides.
    internal sealed class SnowGlareController : IDisposable
    {
        private readonly SeasonAssetBundleRepository repository;
        private Camera camera;
        private Material material;
        private CommandBuffer commands;
        private bool hdr;
        private float strength;
        private static readonly int Buffer = Shader.PropertyToID("_DVSeasonsGlareCopy");
        public float Strength => strength;
        public SnowGlareController(SeasonAssetBundleRepository repository) { this.repository = repository; }
        public void Apply(float setting, float daylight, float coverage)
        {
            float target = Mathf.Clamp(setting, 0f, 2f) * Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.12f, .8f, daylight))
                * Mathf.SmoothStep(0, 1, Mathf.Clamp01(coverage));
            strength = Mathf.MoveTowards(strength, target, Mathf.Max(0, Time.unscaledDeltaTime) * .5f);
            if (strength <= .0001f) { Unbind(); return; }
            var next = Camera.main;
            if (next == null) { Unbind(); return; }
            if (material == null)
            {
                var shader = repository.LoadShader("SnowGlare");
                if (shader == null || !shader.isSupported) return;
                material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            material.SetFloat("_Strength", strength);
            if (camera == next && hdr == next.allowHDR && commands != null) return;
            Unbind(); camera = next; hdr = camera.allowHDR;
            camera.depthTextureMode |= DepthTextureMode.Depth;
            commands = new CommandBuffer { name = "DVSeasons snow highlight reduction" };
            commands.GetTemporaryRT(Buffer, -1, -1, 0, FilterMode.Bilinear,
                hdr ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default);
            commands.Blit(BuiltinRenderTextureType.CameraTarget, Buffer);
            commands.Blit(Buffer, BuiltinRenderTextureType.CameraTarget, material, 0);
            commands.ReleaseTemporaryRT(Buffer);
            camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, commands);
        }
        private void Unbind()
        {
            if (commands != null)
            {
                if (camera != null) camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, commands);
                commands.Dispose(); commands = null;
            }
            camera = null;
        }
        public void Dispose()
        {
            Unbind(); strength = 0;
            if (material != null) UnityEngine.Object.Destroy(material);
            material = null;
        }
    }
}
