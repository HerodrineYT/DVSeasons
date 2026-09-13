using System;
using System.Collections.Generic;
using System.IO;
using DVSeasons.Core;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Rendering;

namespace DVSeasons.Mod
{
    // Small, bounded local pollinator groups sourced from rendered flower
    // instances. No random world-space emitters and no work outside spring.
    internal sealed class SpringLifeController : IDisposable
    {
        private sealed class Patch
        {
            public long Key;
            public Vector3 Position;
            public GameObject Root;
            public AudioSource Audio;
            public readonly Transform[] Bees = new Transform[3];
            public Transform Butterfly;
            public readonly Transform[] Wings = new Transform[2];
            public float Phase, Volume, Visibility, Occlusion = 1;
        }
        private readonly string path;
        private AutumnTreeSourceProvider flowers;
        private readonly List<AutumnTreeSourceSnapshot> candidates = new List<AutumnTreeSourceSnapshot>();
        private readonly List<Patch> patches = new List<Patch>();
        private AudioClip clip;
        private Mesh mesh;
        private Mesh butterflyWing;
        private Material gold, dark, wings, ivory;
        private AudioMixerGroup mixer;
        private float nextScan, nextOcclusion;
        private bool audioAttempted;
        private float nextDiagnostic;
        private string lastDiagnostic;

        public SpringLifeController(string modPath) { path = Path.Combine(modPath, "Audio", "spring_bees.wav"); }

        public void Apply(SeasonState state, float rain, float daylight, Vector3 wind)
        {
            var camera = Camera.main;
            // Snow drift is intentionally amplified by WeatherAdapter; insect
            // activity must use the actual weather wind speed, not that drift.
            float activity = SpringLifeProfile.Activity(state, rain, daylight, wind.magnitude / 2.1f);
            if (state == null || (state.Current != SeasonKind.Spring && state.Next != SeasonKind.Spring))
            { Dispose(); return; }
            if (camera == null) return;
            if (flowers == null) flowers = new AutumnTreeSourceProvider(true);
            flowers.UpdateInterest(camera.transform.position, 24);
            var offset = DV.OriginShift.OriginShift.currentMove;
            float now = Time.time;
            if (now >= nextScan)
            {
                nextScan = now + 2;
                flowers.CopySnapshots(candidates);
                for (int i = patches.Count - 1; i >= 0; i--)
                {
                    var patch = patches[i];
                    bool found = false;
                    foreach (var source in candidates) if (source.Key == patch.Key)
                    { patch.Position = source.CanopyWorldPosition - offset; found = true; break; }
                    if (!found || Vector3.Distance(patch.Position + offset, camera.transform.position) > 28)
                    { UnityEngine.Object.Destroy(patch.Root); patches.RemoveAt(i); }
                }
                if (activity > .01f)
                {
                    candidates.Sort((a,b) => (a.BaseWorldPosition-camera.transform.position).sqrMagnitude.CompareTo(
                        (b.BaseWorldPosition-camera.transform.position).sqrMagnitude));
                    foreach (var source in candidates)
                    {
                        if (patches.Count >= 3) break;
                        if (patches.Count > 0 && (source.Key & 3) != 0) continue;
                        bool near = false;
                        foreach (var existing in patches)
                            if ((existing.Position + offset - source.CanopyWorldPosition).sqrMagnitude < 36) { near = true; break; }
                        if (!near) Create(source, offset);
                    }
                }
                if (now >= nextDiagnostic)
                {
                    nextDiagnostic = now + 30;
                    string status = candidates.Count == 0 ? "no loaded flowers nearby"
                        : activity <= .01f ? "resting in current weather/light" : patches.Count > 0 ? "active" : "awaiting flower patch";
                    if (status != lastDiagnostic)
                    {
                        lastDiagnostic = status;
                        Debug.Log("[DVSeasons] Spring pollinators: " + status + "; flowers=" + candidates.Count +
                            ", patches=" + patches.Count + ", temperature=" + state.TemperatureCelsius.ToString("F1") +
                            ", daylight=" + daylight.ToString("F2") + ", rain=" + rain.ToString("F2") +
                            ", wind=" + (wind.magnitude / 2.1f).ToString("F1"));
                    }
                }
            }
            bool checkOcclusion = now >= nextOcclusion;
            if (checkOcclusion) nextOcclusion = now + .5f;
            foreach (var patch in patches)
            {
                patch.Root.transform.position = patch.Position + offset;
                if (checkOcclusion) patch.Occlusion = Physics.Linecast(camera.transform.position,
                    patch.Root.transform.position + Vector3.up * .15f, SeasonSurfaceLayers.Mask, QueryTriggerInteraction.Ignore) ? .08f : 1;
                patch.Volume = Mathf.MoveTowards(patch.Volume, activity, Time.deltaTime * .3f);
                patch.Audio.volume = Mathf.Sqrt(patch.Volume) * patch.Occlusion * .3f;
                // Weather changes activity, not the physical size of an insect.
                patch.Visibility = Mathf.MoveTowards(patch.Visibility, activity > .01f ? 1 : 0, Time.deltaTime * .5f);
                for (int i = 0; i < patch.Bees.Length; i++)
                {
                    var bee = patch.Bees[i];
                    float t = now * (.85f + i * .09f) + patch.Phase + i * 2.1f;
                    // Pause at a blossom, then make a short looping visit to the
                    // next flower head; wind drifts the flight slightly sideways.
                    float flight = Mathf.SmoothStep(0, 1, Mathf.Clamp01((Mathf.Sin(t * .45f) + .35f) * 1.5f));
                    var p = new Vector3(Mathf.Sin(t) * .26f, .015f + flight * (.16f + Mathf.Sin(t * 2.7f) * .035f),
                        Mathf.Cos(t * .83f) * .2f) * flight;
                    p += wind * (.012f * flight);
                    p += new Vector3((i - 1) * .09f, 0, i * .025f);
                    var direction = new Vector3(Mathf.Cos(t), Mathf.Cos(t * 2.7f) * .12f, -Mathf.Sin(t * .83f));
                    bee.localPosition = p;
                    bee.localRotation = Quaternion.LookRotation(direction) * Quaternion.Euler(Mathf.Sin(t * 30) * 8, 0, 0);
                    bee.localScale = Vector3.one * Mathf.SmoothStep(0,1,patch.Visibility);
                }
                float flutterTime = now + patch.Phase;
                patch.Butterfly.localPosition = new Vector3(Mathf.Sin(flutterTime*.47f)*.65f,
                    .38f+Mathf.Sin(flutterTime*.71f)*.2f, Mathf.Cos(flutterTime*.39f)*.6f) + wind * .022f;
                patch.Butterfly.localRotation = Quaternion.Euler(10, flutterTime*27, Mathf.Sin(flutterTime*1.8f)*16);
                patch.Butterfly.localScale = Vector3.one * Mathf.SmoothStep(0,1,patch.Visibility);
                for(int side=0;side<2;side++) patch.Wings[side].localRotation = Quaternion.Euler(0,0,
                    (side==0 ? -1 : 1)*(22+Mathf.Sin(flutterTime*22)*48));
            }
        }

        private void Create(AutumnTreeSourceSnapshot source, Vector3 offset)
        {
            EnsureResources();
            if (mesh == null) return;
            var patch = new Patch { Key = source.Key, Position = source.CanopyWorldPosition - offset,
                Phase = (source.Key & 65535) * .017f, Root = new GameObject("DVSeasons spring pollinators") };
            patch.Root.hideFlags = HideFlags.HideAndDontSave;
            patch.Root.transform.position = source.CanopyWorldPosition;
            patch.Audio = patch.Root.AddComponent<AudioSource>();
            patch.Audio.clip = clip; patch.Audio.loop = true; patch.Audio.playOnAwake = false;
            patch.Audio.spatialBlend = 1; patch.Audio.minDistance = .8f; patch.Audio.maxDistance = 12;
            patch.Audio.rolloffMode = AudioRolloffMode.Linear; patch.Audio.dopplerLevel = 0;
            patch.Audio.volume = 0; patch.Audio.outputAudioMixerGroup = mixer;
            patch.Audio.pitch = .95f + ((source.Key >> 4) & 15) * .006f;
            if (clip != null) { patch.Audio.time = (float)(source.Key & 1023) / 1024 * clip.length; patch.Audio.Play(); }
            for (int i = 0; i < patch.Bees.Length; i++)
            {
                var go = new GameObject("Bee"); go.transform.SetParent(patch.Root.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterials = new[] { gold, dark, wings };
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = true;
                patch.Bees[i] = go.transform; go.transform.localScale = Vector3.zero;
            }
            patch.Butterfly = new GameObject("Spring white butterfly").transform;
            patch.Butterfly.SetParent(patch.Root.transform,false);patch.Butterfly.localScale=Vector3.zero;
            for(int side=0;side<2;side++)
            {
                var wing=new GameObject("Wing");wing.transform.SetParent(patch.Butterfly,false);
                wing.transform.localScale=new Vector3(side==0?-1:1,1,1);
                wing.AddComponent<MeshFilter>().sharedMesh=butterflyWing;
                var renderer=wing.AddComponent<MeshRenderer>();renderer.sharedMaterials=new[]{ivory,dark};
                renderer.shadowCastingMode=ShadowCastingMode.Off;patch.Wings[side]=wing.transform;
            }
            patches.Add(patch);
        }

        private void EnsureResources()
        {
            if (!audioAttempted)
            {
                audioAttempted = true;
                try
                {
                    int channels, frequency; float[] samples;
                    if (File.Exists(path) && SnowFootstepAudioController.TryReadPcm16Wave(File.ReadAllBytes(path), out channels, out frequency, out samples))
                    { clip = AudioClip.Create("Spring bees", samples.Length/channels,channels,frequency,false); clip.SetData(samples,0); }
                    else Debug.LogWarning("[DVSeasons] Spring bee recording is missing or unreadable: Audio/spring_bees.wav");
                    foreach (var group in Resources.FindObjectsOfTypeAll<AudioMixerGroup>())
                        if (group.name.IndexOf("ambient", StringComparison.OrdinalIgnoreCase) >= 0) { mixer = group; break; }
                }
                catch (Exception e) { Debug.LogWarning("[DVSeasons] Spring bee audio: " + e.Message); }
            }
            if (mesh != null) return;
            var shader = Shader.Find("Standard"); if (shader == null) return;
            gold = new Material(shader) { color = new Color(.48f,.29f,.035f), hideFlags = HideFlags.HideAndDontSave };
            dark = new Material(shader) { color = new Color(.055f,.034f,.015f), hideFlags = HideFlags.HideAndDontSave };
            wings = new Material(shader) { color = new Color(.42f,.43f,.39f), hideFlags = HideFlags.HideAndDontSave };
            ivory = new Material(shader) { color = new Color(.69f,.66f,.48f), hideFlags = HideFlags.HideAndDontSave };
            ivory.SetFloat("_Glossiness",.05f);
            gold.SetFloat("_Glossiness",.12f); dark.SetFloat("_Glossiness",.12f); wings.SetFloat("_Glossiness",.35f);
            var vertices = new List<Vector3>(); var triangles = new[] { new List<int>(), new List<int>(), new List<int>() };
            for (int ring = 0; ring < 5; ring++)
                for (int side = 0; side < 8; side++)
                {
                    float a = side * Mathf.PI / 4, z = (ring - 2) * .0075f;
                    float radius = ring == 0 || ring == 4 ? .003f : .007f;
                    vertices.Add(new Vector3(Mathf.Cos(a)*radius, Mathf.Sin(a)*radius,z));
                    if (ring == 0) continue;
                    int n = vertices.Count-1, prev = ring*8+(side+7)%8;
                    var indices = triangles[ring == 2 || ring == 4 ? 1 : 0];
                    indices.AddRange(new[]{n,prev,n-8,prev,prev-8,n-8});
                }
            for (int side = -1; side <= 1; side += 2)
            {
                int n=vertices.Count;
                vertices.AddRange(new[]{new Vector3(0,.006f,.006f),new Vector3(side*.022f,.01f,-.002f),new Vector3(side*.018f,.01f,-.015f),new Vector3(0,.006f,-.006f)});
                triangles[2].AddRange(new[]{n,n+1,n+2,n,n+2,n+3,n+2,n+1,n,n+3,n+2,n});
            }
            mesh = new Mesh { name = "Small spring bee", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(vertices); mesh.subMeshCount=3;
            for(int i=0;i<3;i++) mesh.SetTriangles(triangles[i],i);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            butterflyWing=new Mesh {name="Spring butterfly wing",hideFlags=HideFlags.HideAndDontSave};
            butterflyWing.vertices=new[]{Vector3.zero,new Vector3(.009f,0,.023f),new Vector3(.026f,0,.022f),
                new Vector3(.03f,0,.009f),new Vector3(.022f,0,-.013f),new Vector3(.008f,0,-.015f)};
            butterflyWing.subMeshCount=2;
            butterflyWing.SetTriangles(new[]{0,1,3,0,3,4,0,4,5,3,1,0,4,3,0,5,4,0},0);
            butterflyWing.SetTriangles(new[]{1,3,2,2,3,1},1);
            butterflyWing.normals=new[]{Vector3.up,Vector3.up,Vector3.up,Vector3.up,Vector3.up,Vector3.up};
            butterflyWing.RecalculateBounds();
        }
        public void Dispose()
        {
            flowers?.Dispose(); flowers=null; candidates.Clear();
            foreach(var patch in patches)
            {
                if (patch.Audio != null) patch.Audio.Stop();
                if (patch.Root != null)
                {
                    patch.Root.SetActive(false);
                    UnityEngine.Object.Destroy(patch.Root);
                }
            }
            patches.Clear();
            if(clip!=null) UnityEngine.Object.Destroy(clip); if(mesh!=null) UnityEngine.Object.Destroy(mesh);
            if(gold!=null) UnityEngine.Object.Destroy(gold); if(dark!=null) UnityEngine.Object.Destroy(dark); if(wings!=null) UnityEngine.Object.Destroy(wings);
            if(ivory!=null) UnityEngine.Object.Destroy(ivory);if(butterflyWing!=null) UnityEngine.Object.Destroy(butterflyWing);
            clip=null;mesh=butterflyWing=null;gold=dark=wings=ivory=null;mixer=null;audioAttempted=false;nextScan=nextOcclusion=0;
            nextDiagnostic=0;lastDiagnostic=null;
        }
    }
}
