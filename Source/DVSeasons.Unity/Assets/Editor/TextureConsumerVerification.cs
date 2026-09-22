using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class TextureConsumerVerification
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static object Get(object owner, string field) { return owner.GetType().GetField(field, All).GetValue(owner); }
        private static void Set(object owner, string field, object value) { owner.GetType().GetField(field, All).SetValue(owner, value); }
        private static object Call(object owner, string method, params object[] args) { return owner.GetType().GetMethod(method, All).Invoke(owner, args); }
        private static void Require(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); }

        public static void Run()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            var runtime = Environment.GetEnvironmentVariable("DVSEASONS_TEXTURE_CONSUMER_RUNTIME");
            if (string.IsNullOrEmpty(runtime)) runtime = Path.Combine(root, "artifacts/build/DVSeasons");
            var game = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            ResolveEventHandler resolve = (sender, args) => {
                foreach (var directory in new[] { runtime, Path.Combine(game, "DerailValley_Data/Managed"), Path.Combine(game, "DerailValley_Data/Managed/UnityModManager") })
                {
                    var path = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            var code = 0;
            try { Verify(Assembly.LoadFrom(Path.Combine(runtime, "DVSeasons.dll")), runtime); }
            catch (Exception error) { UnityEngine.Debug.LogException(error); code = 1; }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
            EditorApplication.Exit(code);
        }

        private static void Verify(Assembly mod, string runtime)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var repoType = mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository", true);
            var repository = Activator.CreateInstance(repoType, new object[] { runtime });
            var bundle = AssetBundle.LoadFromFile(Path.Combine(runtime, "AssetBundles/dvseasons_dv99"));
            Require(bundle != null, "Main shader bundle missing.");
            repoType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository, new object[] { bundle });
            Set(repository, "bundleLoadFinished", true);
            foreach (var asset in bundle.GetAllAssetNames()) Call(repository, "IndexAsset", asset);
            var winterField = repoType.GetField("winterBundle", All);
            if (winterField != null)
            {
                var winter = AssetBundle.LoadFromFile(Path.Combine(runtime, "AssetBundles/dvseasons_winter"));
                Require(winter != null, "Winter texture bundle missing.");
                winterField.SetValue(repository, winter);
                Set(repository, "winterBundleLoadFinished", true);
                foreach (var asset in winter.GetAllAssetNames()) Call(repository, "IndexAsset", asset);
                var tracksField=repoType.GetField("tracksBundle",All);
                if(tracksField!=null)
                {
                    var tracks=AssetBundle.LoadFromFile(Path.Combine(runtime,"AssetBundles/dvseasons_tracks"));
                    Require(tracks!=null,"Prepared track texture bundle missing.");
                    tracksField.SetValue(repository,tracks);Set(repository,"tracksBundleLoadFinished",true);
                    foreach(var asset in tracks.GetAllAssetNames()) Call(repository,"IndexAsset",asset);
                }
            }
            try
            {
                VerifyWater(mod, repository);
                VerifyCoal(mod, repository, winterField != null);
            }
            finally { ((IDisposable)repository).Dispose(); }
            UnityEngine.Debug.Log("TEXTURE_CONSUMERS_OK: water ownership/world projection, coal shared stages/alpha/native material mutations, restoration.");
        }

        private static void VerifyWater(Assembly mod, object repository)
        {
            var type = mod.GetType("DVSeasons.Mod.WaterIceController", true);
            var controller = Activator.CreateInstance(type, new[] { repository });
            var clock = Stopwatch.StartNew();
            Call(controller, "TryLoadIceNormal");
            Call(controller, "TryLoadIceAlbedo");
            clock.Stop();
            var normal = (Texture2D)Get(controller, "iceNormal");
            var albedo = (Texture2D)Get(controller, "iceAlbedo");
            Require(normal != null && albedo != null, "Water ice textures missing.");
            Require(normal.mipmapCount > 1 && albedo.mipmapCount > 1, "Water ice mipmaps lost.");
            var direct = repository.GetType().GetMethod("TryLoadTexture", All);
            if (direct != null)
            {
                var winter = Enum.Parse(direct.GetParameters()[1].ParameterType, "Winter");
                var normalArgs = new object[] { "WaterIceNormal", winter, null };
                var albedoArgs = new object[] { "WaterIceAlbedo", winter, null };
                Require((bool)direct.Invoke(repository, normalArgs) && ReferenceEquals(normalArgs[2], normal), "Water normal was unnecessarily cloned.");
                Require((bool)direct.Invoke(repository, albedoArgs) && ReferenceEquals(albedoArgs[2], albedo), "Water albedo was unnecessarily cloned.");
                VerifyWorldProjection((Material)Get(controller, "iceOverlayMaterial"));
                ((IDisposable)controller).Dispose();
                Require(normal != null && albedo != null, "Water reset destroyed repository-owned textures.");
            }
            else ((IDisposable)controller).Dispose();
            UnityEngine.Debug.Log("TEXTURE_CONSUMER_WATER_CPU_MS=" + clock.Elapsed.TotalMilliseconds.ToString("F3"));
        }

        private static void VerifyWorldProjection(Material material)
        {
            // The old path extracted one submesh and wrote world UVs; the new
            // path renders the source submesh unchanged. Compare both on the GPU.
            var vertices = new[] { new Vector3(-.8f, -.8f, 0), new Vector3(.8f, -.8f, 0),
                new Vector3(.8f, .8f, 0), new Vector3(-.8f, .8f, 0) };
            var indices = new[] { 0, 2, 1, 0, 3, 2 };
            var source = new Mesh { vertices = vertices, uv = new[] { Vector2.zero, Vector2.one, Vector2.zero, Vector2.one }, subMeshCount = 2 };
            source.SetTriangles(new[] { 0, 2, 1 }, 0); source.SetTriangles(indices, 1);
            var extracted = new Mesh { vertices = vertices, triangles = indices,
                uv = new[] { new Vector2(-100, 41), new Vector2(100, -80), new Vector2(4, 93), Vector2.one } };
            var a = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var b = new RenderTexture(64, 64, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var commands = new CommandBuffer();
            try
            {
                source.UploadMeshData(true); // Native world meshes may be non-readable.
                material.SetColor("_Color", Color.white);
                material.SetFloat("_TileSize", 1.4f);
                a.Create(); b.Create();
                var matrix = Matrix4x4.Rotate(Quaternion.Euler(0, 24, 0));
                commands.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                commands.SetRenderTarget(a); commands.ClearRenderTarget(false, true, Color.black);
                commands.DrawMesh(source, matrix, material, 1);
                commands.SetRenderTarget(b); commands.ClearRenderTarget(false, true, Color.black);
                commands.DrawMesh(extracted, matrix, material);
                Graphics.ExecuteCommandBuffer(commands);
                var readA = AsyncGPUReadback.Request(a, 0, TextureFormat.RGBA32);
                var readB = AsyncGPUReadback.Request(b, 0, TextureFormat.RGBA32);
                readA.WaitForCompletion(); readB.WaitForCompletion();
                Require(!readA.hasError && !readB.hasError, "Water world projection readback failed.");
                var pixelsA = readA.GetData<Color32>(); var pixelsB = readB.GetData<Color32>();
                var visible = 0;
                for (var i = 0; i < pixelsA.Length; i++)
                {
                    Require(pixelsA[i].Equals(pixelsB[i]), "Original water submesh differs from extracted ice mesh.");
                    if (pixelsA[i].r > 5) visible++;
                }
                Require(visible > 100, "Water world projection fixture drew no ice.");
            }
            finally
            {
                commands.Dispose();
                foreach (var item in new UnityEngine.Object[] { source, extracted, a, b }) UnityEngine.Object.DestroyImmediate(item);
            }
        }

        private static void VerifyCoal(Assembly mod, object repository, bool optimized)
        {
            const int count = 16;
            var type = mod.GetType("DVSeasons.Mod.TenderCoalSnowController", true);
            var controller = Activator.CreateInstance(type, new[] { repository });
            var apply = (Action<float, Func<Component, float>>)Delegate.CreateDelegate(typeof(Action<float, Func<Component, float>>), controller, type.GetMethod("Apply"));
            var coal = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) { name = "Coal_01d" };
            coal.SetPixels(new[] { new Color(.1f, .1f, .1f, .4f), new Color(.1f, .1f, .1f, .4f), new Color(.1f, .1f, .1f, .4f), new Color(.1f, .1f, .1f, .4f) });
            coal.Apply();
            var original = new Material(Shader.Find("Standard")) { mainTexture = coal };
            var roots = new List<GameObject>();
            var renderers = new List<MeshRenderer>();
            var cars = new List<Component>();
            var carType = Assembly.Load("Assembly-CSharp").GetType("TrainCar", true);
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(primitive);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var root = new GameObject("Inactive tender " + i); root.SetActive(false); roots.Add(root);
                    var car = root.AddComponent(carType);
                    cars.Add(car);
                    root.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = root.AddComponent<MeshRenderer>(); renderer.sharedMaterial = original; renderers.Add(renderer);
                    Call(controller, "Register", car, root.transform);
                }
                Set(controller, "nextScan", float.MaxValue);
                var clock = Stopwatch.StartNew(); apply(.5f, null); clock.Stop();
                var coldMs = clock.Elapsed.TotalMilliseconds;
                var textures = new HashSet<int>();
                foreach (var renderer in renderers)
                {
                    var texture = (RenderTexture)renderer.sharedMaterial.mainTexture;
                    Require(texture.width == 1024 && texture.height == 1024 && texture.useMipMap, "Coal blend resolution or mipmaps changed.");
                    textures.Add(texture.GetInstanceID());
                }
                if (optimized) Require(textures.Count == 1, "Identical coal snow created duplicate render targets.");
                var actual = (RenderTexture)renderers[0].sharedMaterial.mainTexture;
                var request = AsyncGPUReadback.Request(actual, 0, TextureFormat.RGBA32); request.WaitForCompletion();
                Require(!request.hasError && Mathf.Abs(request.GetData<Color32>()[512 * 1024 + 512].a / 255f - .4f) < .006f, "Coal clipping alpha changed.");
                var copyProperty = type.GetProperty("MaterialCopyCount", All);
                var copiesBefore = copyProperty == null ? 0 : (int)copyProperty.GetValue(controller, null);
                for (var warm = 0; warm < 20; warm++) apply(.5f, null);
                clock.Restart();
                for (var frame = 0; frame < 600; frame++) apply(.5f, null);
                clock.Stop();
                if (optimized) Require((int)copyProperty.GetValue(controller, null) == copiesBefore, "Unchanged coal material properties copied each Apply.");
                UnityEngine.Debug.Log("TEXTURE_CONSUMER_COAL: tenders=" + count + " first_apply_cpu_ms=" + coldMs.ToString("F3") +
                    " steady_mean_cpu_ms=" + (clock.Elapsed.TotalMilliseconds / 600).ToString("F5") + " blend_targets=" + textures.Count);

                // Values changed on the original must reach every private winter
                // material, including texture ST and the native clipping controls.
                original.SetFloat("_Cutoff", .23f); original.SetColor("_Color", Color.red);
                original.SetTextureScale("_MainTex", new Vector2(2, 3));
                original.EnableKeyword("_ALPHATEST_ON"); original.renderQueue = 2477;
                apply(.5f, null);
                foreach (var renderer in renderers)
                {
                    var winter = renderer.sharedMaterial;
                    Require(Mathf.Abs(winter.GetFloat("_Cutoff") - .23f) < .0001f && winter.color == Color.red &&
                        winter.GetTextureScale("_MainTex") == new Vector2(2, 3) && winter.IsKeywordEnabled("_ALPHATEST_ON") && winter.renderQueue == 2477,
                        "Native coal material changes were hidden by the cache.");
                }
                if (optimized)
                {
                    // _ClipPlane is not a uniform in DV's Standard (clipped) or
                    // Unity Standard. Use an actual vector/color material slot;
                    // an unknown property would be ignored before caching too.
                    var vector = new Vector4(.2f, .3f, .4f, .5f);
                    original.SetVector("_EmissionColor", vector);
                    var block = new MaterialPropertyBlock();
                    block.SetFloat("_Cutoff", .71f);
                    renderers[0].SetPropertyBlock(block);
                    apply(.5f, null);
                    Require(renderers[0].sharedMaterial.GetVector("_EmissionColor") == vector, "Native vector material property change missed.");
                    renderers[0].GetPropertyBlock(block);
                    Require(Mathf.Abs(block.GetFloat("_Cutoff") - .71f) < .0001f, "Per-renderer native clipping override was overwritten.");
                    apply(.5f, component => component == cars[0] ? .25f : 1f);
                    Require(renderers[0].sharedMaterial.mainTexture != renderers[1].sharedMaterial.mainTexture,
                        "Per-locomotive melt amount was lost when sharing coal textures.");
                    Require(((IDictionary)Get(controller, "blends")).Count == 2, "Independent melt stages were not cached separately.");
                    apply(.5f, null);
                    var allowed = type.GetField("SnowObjectAllowed", All);
                    if (allowed != null)
                    {
                        var keptTexture = renderers[1].sharedMaterial.mainTexture;
                        allowed.SetValue(controller, new Func<Component, bool>(car => car != cars[0]));
                        apply(.5f, null);
                        Require(renderers[0].sharedMaterial.mainTexture != keptTexture &&
                            renderers[1].sharedMaterial.mainTexture == keptTexture,
                            "Object limit did not independently remove capped tender snow.");
                        var bare = AsyncGPUReadback.Request((RenderTexture)renderers[0].sharedMaterial.mainTexture, 0, TextureFormat.RGBA32);
                        bare.WaitForCompletion();
                        Require(!bare.hasError && bare.GetData<Color32>()[512 * 1024 + 512].r < 100,
                            "Capped coal did not return to the original dark albedo.");
                        allowed.SetValue(controller, null);
                        apply(.5f, null);
                        Require(renderers[0].sharedMaterial.mainTexture == keptTexture,
                            "Lifting the object limit lost the previous coal snow stage.");
                    }
                }
                var idsBefore = new HashSet<int>();
                foreach (var renderer in renderers) idsBefore.Add(renderer.sharedMaterial.mainTexture.GetInstanceID());
                for (var step = 1; step <= 32; step++) apply(step / 32f, null);
                if (optimized)
                {
                    var active = (IDictionary)Get(controller, "blends");
                    Require(active.Count == 1, "Old coal blend stages accumulate indefinitely.");
                    Require(idsBefore.Contains(renderers[0].sharedMaterial.mainTexture.GetInstanceID()), "Sequential coal stages did not reuse their render target.");
                }
                apply(0, null);
                foreach (var renderer in renderers) Require(renderer.sharedMaterial == original, "Coal material not restored on thaw.");
            }
            finally
            {
                ((IDisposable)controller).Dispose();
                foreach (var root in roots) UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(original); UnityEngine.Object.DestroyImmediate(coal);
            }
        }
    }
}
