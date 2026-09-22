using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using DV;
using DV.InventorySystem;
using DVSeasons.Core;
using I2.Loc;
using UnityEngine;
using UnityEngine.Networking;

namespace DVSeasons.Mod
{
    // One local receiver: no network AudioSource, no duplicate playback per radio.
    internal sealed class BlizzardAudio : MonoBehaviour
    {
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
        private AudioSource wind, voice;
        private BlizzardRadioGain voiceGain;
        private UnityWebRequest request;
        private BlizzardState state;
        private uint playedSequence;
        private long serverTicks;
        private float receivedAt, nextRadioScan, nextListenerScan;
        private bool storm, carried;
        private CommsRadioController[] radios = new CommsRadioController[0];
        private Transform radio;
        private Inventory inventory;
        private string rootPath;
        private SeasonModSettings settings;

        internal void Initialize(string modPath, SeasonModSettings localSettings)
        {
            settings = localSettings;
            rootPath = Path.Combine(modPath, "Audio", "Blizzard");
            CreateSources();
            StartCoroutine(LoadClips());
        }
        private void CreateSources()
        {
            wind = gameObject.AddComponent<AudioSource>(); wind.playOnAwake = false;
            wind.loop = true; wind.spatialBlend = 0f; wind.volume = 0;
            // The gain filter belongs only to this source, never to the wind.
            var receiver = new GameObject("Radio announcements");
            receiver.transform.SetParent(transform, false);
            voice = receiver.AddComponent<AudioSource>(); voice.playOnAwake = false;
            voiceGain = receiver.AddComponent<BlizzardRadioGain>();
            ApplyRadioVolume();
            voice.priority = 20; voice.dopplerLevel = 0; voice.minDistance = 1.5f; voice.maxDistance = 12f;
            voice.rolloffMode = AudioRolloffMode.Linear;
        }
        private void ApplyRadioVolume()
        {
            // AudioSource.volume saturates at 1; the DSP filter supplies the
            // extra amplification. At 100% its samples pass through unchanged.
            float volume = settings.BlizzardRadioVolume;
            voiceGain.Boost = Mathf.Max(1f, volume);
            voice.volume = Mathf.Min(1f, volume);
        }
        private IEnumerator LoadClips()
        {
            foreach (string name in new[] { "early_ru", "early_en", "hour_ru", "hour_en", "ending_ru", "ending_en", "wind" })
            {
                string path = Path.Combine(rootPath, name + (name == "wind" ? ".mp3" : ".ogg"));
                if (!File.Exists(path)) { Debug.LogWarning("[DVSeasons] Missing blizzard audio: " + path); continue; }
                request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri,
                    name == "wind" ? AudioType.MPEG : AudioType.OGGVORBIS);
                // Decode off the main thread. Stream the long wind recording.
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = name == "wind";
                yield return request.SendWebRequest();
                if (!request.isNetworkError && !request.isHttpError)
                {
                    var clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null) { clip.name = name; clips[name] = clip; }
                }
                else Debug.LogWarning("[DVSeasons] Blizzard audio: " + request.error);
                request.Dispose(); request = null;
                yield return null;
            }
        }
        internal void Receive(BlizzardState incoming)
        {
            state = incoming;
            serverTicks = incoming.HostUtcTicks;
            receivedAt = Time.realtimeSinceStartup;
        }
        internal void SetStorm(bool active) { storm = active; }
        private void Update()
        {
            if (wind == null || voice == null) return;
            // Local preference, also while paused or a notice is already playing.
            // Keep the live broadcast timeline running at zero volume.
            ApplyRadioVolume();
            var player = PlayerManager.PlayerCamera;
            bool paused = Time.timeScale <= 0 || UnloadWatcher.isUnloading;
            if (wind.clip == null && clips.TryGetValue("wind", out var windClip)) wind.clip = windClip;
            wind.volume = Mathf.MoveTowards(wind.volume, storm && !paused ? (voice.isPlaying && !voice.mute && voice.volume > 0f ? .25f : .65f) : 0f, Time.unscaledDeltaTime * .2f);
            if (wind.volume > .001f && wind.clip != null && !wind.isPlaying) wind.Play();
            if (wind.volume <= .001f && wind.isPlaying) wind.Stop();
            if (state == null || state.Notice == BlizzardNotice.None || player == null) { voice.Stop(); return; }
            if (playedSequence == state.NoticeSequence && !voice.isPlaying) return;
            double age = (serverTicks - state.NoticeUtcTicks) / (double)TimeSpan.TicksPerSecond +
                Time.realtimeSinceStartup - receivedAt;
            if (age < 0 || paused) return;
            if (Time.realtimeSinceStartup >= nextListenerScan)
            { nextListenerScan = Time.realtimeSinceStartup + .2f; FindReceiver(player.transform.position); }
            bool audible = carried || (radio != null && radio.gameObject.activeInHierarchy &&
                (radio.position - player.transform.position).sqrMagnitude < 144f);
            if (playedSequence != state.NoticeSequence)
            {
                string prefix = state.Notice == BlizzardNotice.Early ? "early" : state.Notice == BlizzardNotice.OneHour ? "hour" : "ending";
                bool russian = (LocalizationManager.CurrentLanguageCode ?? "").StartsWith("ru", StringComparison.OrdinalIgnoreCase);
                if (!clips.TryGetValue(prefix + (russian ? "_ru" : "_en"), out var clip)) return;
                if (age >= clip.length) { playedSequence = state.NoticeSequence; return; }
                if (!audible) return; // Walking into range receives only the remaining live broadcast.
                voice.Stop(); voice.clip = clip; voice.time = (float)age; voice.Play();
                playedSequence = state.NoticeSequence;
            }
            voice.mute = !audible;
            voice.spatialBlend = carried ? 0f : 1f;
            transform.position = carried || radio == null ? player.transform.position : radio.position;
        }
        private void FindReceiver(Vector3 listener)
        {
            carried = false; radio = null;
            if (inventory == null) inventory = UnityEngine.Object.FindObjectOfType<Inventory>();
            if (inventory != null)
                for (int i = 0; i < inventory.Capacity; i++)
                {
                    var item = inventory.PeekItemAtSlot(i, false);
                    if (item != null && item.GetComponent<CommsRadioController>() != null) { carried = true; return; }
                }
            if (Time.realtimeSinceStartup >= nextRadioScan)
            {
                nextRadioScan = Time.realtimeSinceStartup + 2f;
                radios = UnityEngine.Object.FindObjectsOfType<CommsRadioController>();
            }
            float closest = 144f;
            foreach (var candidate in radios)
            {
                if (candidate == null || !candidate.gameObject.activeInHierarchy) continue;
                float distance = (candidate.transform.position - listener).sqrMagnitude;
                if (distance < closest) { closest = distance; radio = candidate.transform; }
            }
        }
        private void OnDestroy()
        {
            StopAllCoroutines(); request?.Abort(); request?.Dispose(); request = null;
            if (voice != null) voice.Stop(); if (wind != null) wind.Stop();
            foreach (var clip in clips.Values) if (clip != null) UnityEngine.Object.Destroy(clip);
            clips.Clear();
        }
    }
}
