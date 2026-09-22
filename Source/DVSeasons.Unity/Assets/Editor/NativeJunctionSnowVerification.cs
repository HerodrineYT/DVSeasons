using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class NativeJunctionSnowVerification
    {
        [Serializable] private sealed class PartData
        {
            public string name, meshName;
            public Vector3 position, scale;
            public Quaternion rotation;
            public Vector3[] vertices, normals;
            public Vector2[] uv;
            public int[] indices;
        }
        [Serializable] private sealed class NativeData
        {
            public string nativeRoot;
            public Vector3 position, scale;
            public Quaternion rotation;
            public Vector3[] poses;
            public PartData[] parts;
        }
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static object Get(object o, string n) { return o.GetType().GetField(n, All).GetValue(o); }
        private static object Call(object o, string n, params object[] args) { return o.GetType().GetMethod(n, All).Invoke(o, args); }
        private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            string modPath = Path.Combine(root, "artifacts/build/DVSeasons");
            string game = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            ResolveEventHandler resolver = (sender, args) =>
            {
                foreach (var directory in new[] { modPath, Path.Combine(game, "DerailValley_Data/Managed"),
                    Path.Combine(game, "DerailValley_Data/Managed/UnityModManager") })
                {
                    var path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            int code = 0;
            try
            {
                Verify(Assembly.LoadFrom(Path.Combine(modPath, "DVSeasons.dll")), modPath, game, root);
                Debug.Log("NATIVE_JUNCTION_SNOW_OK: actual installed blade, fixed rails, ballast and sleepers; native mirrored transform and both native animation poses.");
            }
            catch (Exception e) { Debug.LogException(e); code = 1; }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
            EditorApplication.Exit(code);
        }

        private static void Verify(Assembly mod, string modPath, string game, string root)
        {
            var output = Path.Combine(root, "artifacts/verification/junction-native");
            var data = JsonUtility.FromJson<NativeData>(File.ReadAllText(Path.Combine(output, "junction.json")));
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Color.gray; RenderSettings.fog = false;
            QualitySettings.antiAliasing = 0;
            var camera = new GameObject("Native snow junction camera") { tag = "MainCamera" }.AddComponent<Camera>();
            camera.renderingPath = RenderingPath.DeferredShading; camera.fieldOfView = 55;
            camera.allowHDR = true; camera.allowMSAA = false; camera.farClipPlane = 100;
            camera.targetTexture = new RenderTexture(2048, 2048, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            camera.targetTexture.Create();
            var albedo = new RenderTexture(2048, 2048, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear); albedo.Create();
            var probe = new CommandBuffer { name = "Native junction snow albedo" };
            probe.Blit(BuiltinRenderTextureType.GBuffer0, albedo); camera.AddCommandBuffer(CameraEvent.AfterLighting, probe);
            var material = new Material(Shader.Find("Standard")) { color = new Color(.08f, .08f, .08f) }; material.SetFloat("_Glossiness", 0);
            var junction = new GameObject(data.nativeRoot);
            var animator = junction.AddComponent<Animator>(); animator.enabled = false;
            var native = Assembly.LoadFrom(Path.Combine(game, "DerailValley_Data/Managed/Assembly-CSharp.dll"));
            var owner = junction.AddComponent(native.GetType("VisualSwitch", true));
            ((Behaviour)owner).enabled = false; owner.GetType().GetField("animator").SetValue(owner, animator);
            var graphical = new GameObject("Graphical").transform; graphical.SetParent(junction.transform, false);
            graphical.localPosition = data.position; graphical.localRotation = data.rotation; graphical.localScale = data.scale;
            var ownedMeshes = new List<Mesh>(); var probes = new List<Vector3>(); Renderer blade = null;
            foreach (var part in data.parts)
            {
                var item = new GameObject(part.name); item.transform.SetParent(graphical, false);
                item.transform.localPosition = part.position; item.transform.localRotation = part.rotation; item.transform.localScale = part.scale;
                var mesh = new Mesh { name = part.meshName, vertices = part.vertices, normals = part.normals, uv = part.uv, triangles = part.indices };
                mesh.RecalculateBounds(); ownedMeshes.Add(mesh); item.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = item.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                if (part.meshName == "rails_moving")
                {
                    blade = renderer;
                    for (int i = 0; i + 2 < part.indices.Length; i += 3)
                    {
                        int a = part.indices[i], b = part.indices[i + 1], c = part.indices[i + 2];
                        if (part.normals[a].y < .9f || part.normals[b].y < .9f || part.normals[c].y < .9f) continue;
                        var p = (part.vertices[a] + part.vertices[b] + part.vertices[c]) / 3f;
                        // The real tapered tips are the area most affected by
                        // a 0.3-degree switch, and are not approximated by a cube.
                        if (p.z > 10f && p.z < 20f && p.y > .145f) probes.Add(p);
                    }
                }
            }
            Require(blade != null && probes.Count > 10, "Exported native rail head samples were not found");
            blade.transform.localRotation = Quaternion.Euler(data.poses[0]);
            var repoType = mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository", true);
            var repository = Activator.CreateInstance(repoType, new object[] { modPath });
            repoType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,
                new object[] { AssetBundle.LoadFromFile(Path.Combine(modPath, "AssetBundles/dvseasons_dv99")) });
            var controller = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.ProceduralSnowController", true), new[] { repository });
            Call(controller, "SetVehicleDiscovery", new Func<IEnumerable<Component>>(() => new Component[0]));
            var registry = Get(controller, "vehicles"); var source = Get(registry, "junctions");
            try
            {
                bool oblique = Environment.GetEnvironmentVariable("DVSEASONS_JUNCTION_OBLIQUE") == "1";
                if (oblique)
                {
                    junction.transform.position = new Vector3(15535.193f, 214.36f, 15265.421f);
                    junction.transform.rotation = Quaternion.Euler(0, 67.33f, 0);
                    camera.transform.position = junction.transform.TransformPoint(new Vector3(0, 1.85f, -10.8f));
                    camera.transform.LookAt(junction.transform.TransformPoint(new Vector3(0, 0, -4.8f)), Vector3.up);
                }
                else { camera.transform.position = new Vector3(0, 27, 0); camera.transform.LookAt(Vector3.zero, Vector3.forward); }
                Call(controller, "SetWeather", 0f);
                for (int i = 0; i < 5; i++) { Call(controller, "Apply", 1f, true); camera.Render(); }
                if (Environment.GetEnvironmentVariable("DVSEASONS_JUNCTION_SOURCE_SHADER") == "1")
                {
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/ProceduralSnow.shader");
                    Require(shader != null, "Source snow shader was not imported");
                    var old = (Material)Get(controller, "material");
                    controller.GetType().GetField("material", All).SetValue(controller, new Material(shader));
                    UnityEngine.Object.DestroyImmediate(old);
                }
                Require((int)source.GetType().GetProperty("Count").GetValue(source, null) == 1, "Actual native blade was not discovered");
                var before = Render(controller, camera, albedo, blade, probes, Path.Combine(output, "pose0.png"));
                blade.transform.localRotation = Quaternion.Euler(data.poses[1]);
                var after = Render(controller, camera, albedo, blade, probes, Path.Combine(output, "pose1.png"));
                Compare(before, after, "native mirrored switch");
                Require(Mean(before) > .62f && Mean(after) > .62f,
                    "Native narrow rail heads sampled lower sleepers as shelter: " + Mean(before) + " -> " + Mean(after));
                blade.transform.localRotation = Quaternion.Euler(data.poses[0]);
                var returned = Render(controller, camera, albedo, blade, probes, null); Compare(before, returned, "native mirrored return");
                var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
                roof.name = "Junction shelter check"; roof.transform.SetParent(junction.transform, false);
                roof.transform.localPosition = new Vector3(0, 2, 0); roof.transform.localScale = new Vector3(7, .2f, 28);
                roof.GetComponent<Renderer>().sharedMaterial = material;
                Call(controller, "InvalidateGeometry");
                for (int i = 0; i < 3; i++)
                {
                    controller.GetType().GetField("nextProxyRefresh", All).SetValue(controller, 0f);
                    Call(controller, "Apply", 1f, true); camera.Render();
                }
                // Reveal the sheltered rails without altering the already
                // captured roof. This tests exposure independently of occlusion.
                roof.GetComponent<Renderer>().enabled = false;
                var sheltered = Render(controller, camera, albedo, blade, probes, null);
                Require(Mean(sheltered) < .15f, "Sky test placed snow on a native blade under a roof: " + Mean(sheltered));
                Debug.Log("Native junction means/count: " + Mean(before) + " -> " + Mean(after) + "; n=" + probes.Count);
            }
            finally
            {
                ((IDisposable)controller).Dispose(); ((IDisposable)repository).Dispose();
                camera.RemoveCommandBuffer(CameraEvent.AfterLighting, probe); probe.Dispose();
                var target = camera.targetTexture; camera.targetTexture = null;
                foreach (var o in new UnityEngine.Object[] { junction, material, target, albedo, camera.gameObject })
                    if (o != null) UnityEngine.Object.DestroyImmediate(o);
                foreach (var mesh in ownedMeshes) UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        private static float[] Render(object controller, Camera camera, RenderTexture albedo, Renderer blade, List<Vector3> points, string png)
        {
            Call(controller, "Apply", 1f, true); camera.Render();
            var previous = RenderTexture.active; RenderTexture.active = albedo;
            var texture = new Texture2D(albedo.width, albedo.height, TextureFormat.RGBAFloat, false, true);
            texture.ReadPixels(new Rect(0, 0, albedo.width, albedo.height), 0, 0); texture.Apply();
            var values = new float[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                var uv = camera.WorldToViewportPoint(blade.transform.TransformPoint(points[i]));
                values[i] = uv.z <= 0 || uv.x < .01f || uv.y < .01f || uv.x > .99f || uv.y > .99f ? float.NaN :
                    texture.GetPixel(Mathf.RoundToInt(uv.x * (texture.width - 1)), Mathf.RoundToInt(uv.y * (texture.height - 1))).r;
            }
            if (png != null)
            {
                var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
                var pixels = texture.GetPixels(); for (int i = 0; i < pixels.Length; i++) pixels[i] = pixels[i].gamma;
                image.SetPixels(pixels); image.Apply(); File.WriteAllBytes(png, image.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(image);
            }
            UnityEngine.Object.DestroyImmediate(texture); RenderTexture.active = previous; return values;
        }
        private static float Mean(float[] values) { float sum = 0; int n = 0; foreach (var v in values) if (!float.IsNaN(v)) { sum += v; n++; } return sum / Mathf.Max(1, n); }
        private static void Compare(float[] before, float[] after, string label)
        {
            int snow = 0, lost = 0; float difference = 0;
            for (int i = 0; i < before.Length; i++)
            {
                if (float.IsNaN(before[i]) || float.IsNaN(after[i]) || before[i] < .32f) continue;
                snow++; if (after[i] < .15f) lost++;
                difference += Mathf.Abs(before[i] - after[i]);
            }
            Debug.Log(label + ": mean=" + Mean(before) + " -> " + Mean(after) + "; snowy=" + snow + "; lost=" + lost + "; difference=" + difference / Mathf.Max(1, snow));
            Require(snow >= 10, "Native fixture had too little snow to exercise switch movement");
            Require(lost <= snow / 10 && difference / snow < .13f, label + " lost snow from actual tapered rail heads");
        }
    }
}
