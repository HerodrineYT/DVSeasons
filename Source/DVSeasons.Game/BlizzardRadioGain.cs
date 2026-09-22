using UnityEngine;

namespace DVSeasons.Mod
{
    // Runs on Unity's audio thread. No Unity calls, allocations or locks here.
    internal sealed class BlizzardRadioGain : MonoBehaviour
    {
        internal volatile float Boost = 1f;

        private void OnAudioFilterRead(float[] data, int channels)
        {
            float gain = Boost;
            if (gain == 1f) return;
            for (int i = 0; i < data.Length; i++) data[i] *= gain;
        }
    }
}
