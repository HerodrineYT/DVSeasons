using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;

namespace DVSeasons.AssetBundleBuild
{
    public static class BlizzardVerification
    {
        private static string audioPath;
        private static int audioIndex;
        private static UnityWebRequest audioRequest;
        private static double audioDeadline;
        private static readonly string[] AudioNames = { "early_ru.ogg", "early_en.ogg", "hour_ru.ogg", "hour_en.ogg", "ending_ru.ogg", "ending_en.ogg", "wind.mp3" };
        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            string managed = Path.Combine(Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME"), "DerailValley_Data/Managed");
            string mod = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD");
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                foreach (var dir in new[] { mod, managed, Path.Combine(managed, "UnityModManager") })
                { var file = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll"); if (File.Exists(file)) return Assembly.LoadFrom(file); }
                return null;
            };
            int code = 0;
            try { Assembly.LoadFrom(Path.Combine(root, "artifacts/verification/VerifyBlizzard.dll")).GetType("VerifyBlizzard").GetMethod("Run").Invoke(null, new object[] { mod }); }
            catch (Exception e) { Debug.LogException(e); code = 1; }
            if (code != 0) { EditorApplication.Exit(code); return; }
            audioPath = Path.Combine(mod, "Audio/Blizzard"); audioIndex = 0;
            EditorApplication.update += AudioTick;
        }
        private static void AudioTick()
        {
            try
            {
                if (audioRequest == null)
                {
                    if (audioIndex == AudioNames.Length)
                    {
                        Debug.Log("BLIZZARD_AUDIO_OK: all 7 supplied recordings decode and seek in Unity 2019");
                        EditorApplication.update -= AudioTick; EditorApplication.Exit(0); return;
                    }
                    string path = Path.Combine(audioPath, AudioNames[audioIndex]);
                    audioRequest = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri,
                        path.EndsWith(".mp3") ? AudioType.MPEG : AudioType.OGGVORBIS);
                    ((DownloadHandlerAudioClip)audioRequest.downloadHandler).streamAudio = path.EndsWith(".mp3");
                    audioRequest.SendWebRequest(); audioDeadline = EditorApplication.timeSinceStartup + 30;
                }
                if (!audioRequest.isDone)
                { if (EditorApplication.timeSinceStartup > audioDeadline) throw new TimeoutException("Audio loading stalled"); return; }
                if (audioRequest.isNetworkError || audioRequest.isHttpError) throw new Exception(audioRequest.error);
                var clip = DownloadHandlerAudioClip.GetContent(audioRequest);
                if (clip == null || clip.length < 1 || clip.frequency < 8000) throw new Exception("Invalid blizzard clip");
                var go = new GameObject("Blizzard audio verification"); var source = go.AddComponent<AudioSource>();
                source.clip = clip; source.time = clip.length * .3f;
                Debug.Log("BLIZZARD_CLIP " + AudioNames[audioIndex] + " seconds=" + clip.length + " channels=" + clip.channels);
                UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(clip);
                audioRequest.Dispose(); audioRequest = null; audioIndex++;
            }
            catch (Exception ex)
            {
                audioRequest?.Dispose(); audioRequest = null; Debug.LogException(ex);
                EditorApplication.update -= AudioTick; EditorApplication.Exit(1);
            }
        }
    }
}
