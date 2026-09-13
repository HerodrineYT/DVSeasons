using System;
using UnityEngine;

namespace DVSeasons.Mod
{
    internal sealed class CabHeaterLamp : IDisposable
    {
        private static readonly int Emission = Shader.PropertyToID("_EmissionColor");
        private readonly Renderer renderer;
        private readonly MaterialPropertyBlock original = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock current = new MaterialPropertyBlock();

        public CabHeaterLamp(Renderer renderer)
        {
            this.renderer = renderer;
            if (renderer != null) renderer.GetPropertyBlock(original);
        }

        public void Set(float level)
        {
            if (renderer == null) return;
            // Lamps_02 already has an emission map and the emission variant.
            // Change this lamp's block, never the shared material used by gauges.
            renderer.GetPropertyBlock(current);
            current.SetColor(Emission, level > 0 ? Color.white : Color.black);
            renderer.SetPropertyBlock(current);
        }

        public void Dispose()
        {
            if (renderer != null) renderer.SetPropertyBlock(original);
        }
    }
}
