using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    // Renders the actual runtime particle mesh, rather than a shader-only quad.
    public static class LeafTrailRuntimeVerification
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic;
        private static object Get(object o, string field) { return o.GetType().GetField(field, All).GetValue(o); }
        private static void Set(object o, string field, object value) { o.GetType().GetField(field, All).SetValue(o, value); }
        private static object Call(object o, string name, params object[] args)
        { return o.GetType().GetMethod(name, All).Invoke(o, args); }
        private static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }

        public static void Run()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            var modPath = Path.Combine(root, "artifacts/build/DVSeasons");
            var output = Path.Combine(root, "artifacts/verification/0.3.33-leaves");
            var game = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            ResolveEventHandler resolve = (s, e) =>
            {
                foreach (var dir in new[] { modPath, Path.Combine(game, "DerailValley_Data/Managed"),
                    Path.Combine(game, "DerailValley_Data/Managed/UnityModManager") })
                {
                    var path = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
            int code = 0;
            try
            {
                Directory.CreateDirectory(output);
                var mod = Assembly.LoadFrom(Path.Combine(modPath, "DVSeasons.dll"));
                VerifyLeaves(mod, modPath, output);
                VerifyTrail(mod, modPath);
                Debug.Log("LEAF_TRAIL_RUNTIME_OK: leaf species/colour atlas, flight collisions, moving surfaces, roof occlusion, lighting, snowflake size.");
            }
            catch (Exception e) { Debug.LogException(e); code = 1; }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolve; }
            EditorApplication.Exit(code);
        }

        private static void VerifyLeaves(Assembly mod, string modPath, string output)
        {
            var bundle = AssetBundle.LoadFromFile(Path.Combine(modPath, "AssetBundles/dvseasons_dv99"));
            var repositoryType = mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository", true);
            var repository = Activator.CreateInstance(repositoryType, new object[] { modPath });
            repositoryType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository, new object[] { bundle });
            var controller = Activator.CreateInstance(mod.GetType("DVSeasons.Mod.AutumnLeafGroundController", true),
                All, null, new[] { repository }, null);
            var cameraObject = new GameObject("leaf test camera");
            var camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
            camera.cullingMask = 1 << 30; camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.05f, .07f, .10f); camera.nearClipPlane = .01f;
            camera.farClipPlane = 10; camera.fieldOfView = 50;
            var target = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
            camera.targetTexture = target; target.Create();
            var roof = GameObject.CreatePrimitive(PrimitiveType.Cube); roof.layer = 30;
            roof.transform.position = new Vector3(0, -.03f, 0); roof.transform.localScale = new Vector3(2, .06f, 2);
            var roofMaterial = new Material(Shader.Find("Standard"));
            roofMaterial.color = new Color(.22f, .25f, .27f);
            roof.GetComponent<Renderer>().sharedMaterial = roofMaterial;
            var sunObject = new GameObject("leaf test sun");
            var sun = sunObject.AddComponent<Light>(); sun.type = LightType.Directional; sun.intensity = 1;
            sun.transform.rotation = Quaternion.Euler(70, 20, 0); sun.cullingMask = 1 << 30;
            var baked = new Mesh();
            var oldAmbient = RenderSettings.ambientLight; var oldMode = RenderSettings.ambientMode;
            var oldFog = RenderSettings.fog; var oldSun = RenderSettings.sun;
            try
            {
                RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = new Color(.45f, .45f, .45f);
                RenderSettings.fog = false; RenderSettings.sun = sun;
                Call(controller, "EnsureRenderer");
                var particles = (ParticleSystem)Get(controller, "particles");
                Require(particles != null, "Runtime leaf renderer unavailable");
                particles.gameObject.layer = 30;
                var renderer = particles.GetComponent<ParticleSystemRenderer>();
                VerifyLeafAtlas(particles, renderer, camera, baked);
                particles.SetParticles(new[] { new ParticleSystem.Particle {
                    position = new Vector3(0, .018f, 0), startSize = .27f,
                    rotation3D = Quaternion.FromToRotation(Vector3.forward, Vector3.up).eulerAngles,
                    startColor = new Color32(245, 245, 245, 255), randomSeed = 73,
                    startLifetime = 1000, remainingLifetime = 999 } }, 1);
                Vector3[] first = null;
                foreach (var position in new[] { new Vector3(0, 1, 0), new Vector3(.5f, .15f, .4f),
                    new Vector3(0, .08f, .03f), new Vector3(0, -1, 0) })
                {
                    camera.transform.position = position; camera.transform.LookAt(Vector3.zero, Vector3.forward);
                    renderer.BakeMesh(baked, camera, true);
                    var vertices = baked.vertices;
                    Require(vertices.Length >= 15, "Runtime leaf mesh is empty");
                    Require(baked.bounds.size.y < .035f, "Resting leaf rotated through its support");
                    if (first == null) first = vertices;
                    else for (var i = 0; i < first.Length; i++)
                        Require(Vector3.Distance(first[i], vertices[i]) < .00001f,
                            "Camera changed the resting leaf geometry");
                }

                camera.transform.position = new Vector3(0, -1, 0);
                camera.transform.LookAt(Vector3.zero, Vector3.forward);
                renderer.enabled = false; var baseline = Capture(camera, target, null);
                renderer.enabled = true; var beneath = Capture(camera, target, Path.Combine(output, "below-roof.png"));
                Require(Difference(baseline, beneath) < .00001f, "Leaves draw through an opaque cab roof");

                camera.transform.position = new Vector3(0, .7f, .08f);
                camera.transform.LookAt(Vector3.zero, Vector3.forward);
                renderer.enabled = false; baseline = Capture(camera, target, null); renderer.enabled = true;
                var day = Capture(camera, target, Path.Combine(output, "day.png"));
                Require(Difference(baseline, day) > .001f, "Textured leaf not visible above roof");
                sun.enabled = false; Call(controller, "UpdateLighting");
                var cloudy = Capture(camera, target, Path.Combine(output, "overcast.png"));
                RenderSettings.ambientLight = new Color(.015f, .015f, .015f);
                Call(controller, "UpdateLighting");
                var night = Capture(camera, target, Path.Combine(output, "night.png"));
                Debug.Log("Leaf lighting samples: overcast=" + CentreBrightness(cloudy) +
                    ", night=" + CentreBrightness(night));
                Require(CentreBrightness(cloudy) > .015f, "Leaf became black without direct sunlight");
                Require(CentreBrightness(night) < CentreBrightness(cloudy) * .2f, "Leaf glows at night");

                var texture = (Texture2D)Get(controller, "texture");
                var sheet = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(texture, sheet);
                SaveTexture(sheet, Path.Combine(output, "leaf-texture.png"));
                RenderTexture.ReleaseTemporary(sheet);
                Debug.Log("Runtime leaves verified: stable baked vertices at four camera angles, " +
                    "roof occlusion, overcast=" + CentreBrightness(cloudy) + ", night=" + CentreBrightness(night));
                VerifyAttachment(controller, roof.transform);
                VerifyLeafCollisions(controller);
                VerifyInstalledCab(controller);
            }
            finally
            {
                RenderSettings.ambientLight = oldAmbient; RenderSettings.ambientMode = oldMode;
                RenderSettings.fog = oldFog; RenderSettings.sun = oldSun;
                DestroyRuntimeObjects(controller, "owner", "material", "texture", "leafMesh");
                ((IDisposable)controller).Dispose(); ((IDisposable)repository).Dispose();
                foreach (var o in new UnityEngine.Object[] { cameraObject, target, roof, roofMaterial, sunObject, baked })
                    UnityEngine.Object.DestroyImmediate(o);
            }
        }

        private static void VerifyLeafAtlas(ParticleSystem particles, ParticleSystemRenderer renderer,
            Camera camera, Mesh baked)
        {
            var sheet = particles.textureSheetAnimation;
            Require(sheet.enabled && sheet.numTilesX == 4 && sheet.numTilesY == 4,
                "Leaf species/colour sheet not configured");
            var cells = new System.Collections.Generic.HashSet<int>();
            for (uint seed = 1; seed <= 192; seed++)
            {
                var particle = new ParticleSystem.Particle { position = Vector3.zero, startSize = .27f,
                    startColor = Color.white, randomSeed = seed * 7919, startLifetime = 1000, remainingLifetime = 999 };
                particles.SetParticles(new[] { particle }, 1);
                renderer.BakeMesh(baked, camera, true);
                var uv = baked.uv;
                Require(uv.Length > 0, "Leaf atlas UVs missing");
                var centre = Vector2.zero;
                foreach (var value in uv) centre += value;
                centre /= uv.Length;
                cells.Add(Mathf.FloorToInt(centre.x * 4) + 4 * Mathf.FloorToInt(centre.y * 4));
                particle.position = Vector3.one; particle.remainingLifetime = 400;
                particles.SetParticles(new[] { particle }, 1);
                renderer.BakeMesh(baked, camera, true);
                var moved = baked.uv;
                for (int i = 0; i < uv.Length; i++)
                    Require((uv[i] - moved[i]).sqrMagnitude < .000001f,
                        "A leaf changed species/pigment during flight");
            }
            Require(cells.Count == 16, "Only " + cells.Count + " leaf atlas cells are used");
            Debug.Log("Leaf atlas: all 16 variants sampled; stable UVs after movement and aging.");
        }

        private static void VerifyLeafCollisions(object controller)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                wall.layer = roof.layer = 11; floor.layer = 16;
                wall.transform.position = new Vector3(1, 2, 0);
                wall.transform.localScale = new Vector3(.025f, 4, 3);
                roof.transform.position = new Vector3(0, 4, 0);
                roof.transform.localScale = new Vector3(3, .05f, 3);
                floor.transform.localScale = new Vector3(3, .1f, 3);
                Physics.SyncTransforms();
                Set(controller, "surfaceQueriesRemaining", 10);
                var query = new object[] { Vector3.zero, Vector3.zero, null };
                Require((bool)Call(controller, "TryFindSurface", query), "No exterior surface found");
                Require(((Vector3)Get(query[2], "Position")).y > 4,
                    "Low probe seeded cab floor instead of exterior roof");

                var type = controller.GetType().GetNestedType("LeafBody", All);
                var leaf = Activator.CreateInstance(type, true);
                Set(leaf, "Size", .27f);
                Set(leaf, "Position", new Vector3(0, 2, 0));
                Set(leaf, "Velocity", new Vector3(-20, 0, 0));
                Require((bool)Call(controller, "MoveWithCollisions", leaf, new Vector3(2, 2, 0), Vector3.zero),
                    "Leaf tunneled through closed WALKABLE window");
                Require(((Vector3)Get(leaf, "Position")).x > 1.1f && !(bool)Get(leaf, "Settled"),
                    "Window collision left a leaf inside the cab");

                // A stationary leaf must collide when the wall moves over it.
                var entries = (System.Collections.IList)Get(controller, "collisionCars");
                var entry = Activator.CreateInstance(controller.GetType().GetNestedType("CollisionCar", All), true);
                Set(entry, "PreviousWorldToLocal", Matrix4x4.Translate(Vector3.right));
                Set(entry, "CurrentLocalToWorld", Matrix4x4.Translate(Vector3.right));
                Set(entry, "SweptBounds", new Bounds(new Vector3(0, 2, 0), Vector3.one * 6));
                Set(entry, "Moved", true); entries.Add(entry);
                Set(leaf, "Position", new Vector3(0, 2, 0)); Set(leaf, "Velocity", Vector3.zero);
                Require((bool)Call(controller, "MoveWithCollisions", leaf, new Vector3(0, 2, 0), Vector3.zero) &&
                    ((Vector3)Get(leaf, "Position")).x > 1.1f, "Moving cab crossed a stationary leaf");
                entries.Clear();

                Set(leaf, "Position", new Vector3(0, 2, 0)); Set(leaf, "Velocity", Vector3.down * 10);
                Require((bool)Call(controller, "MoveWithCollisions", leaf, new Vector3(0, 6, 0), Vector3.zero) &&
                    (bool)Get(leaf, "Settled") && ((Vector3)Get(leaf, "Position")).y > 4,
                    "Falling leaf penetrated roof before reaching its old landing height");

                var before = (Matrix4x4)Call(controller, "StableMatrix", wall.transform, Vector3.zero);
                var shift = new Vector3(1000, 0, -500);
                wall.transform.position += shift;
                var after = (Matrix4x4)Call(controller, "StableMatrix", wall.transform, shift);
                Require(before == after, "Origin shift became a moving train collision");
                Debug.Log("Leaf collision checks: exterior spawn, thin window, moving cab, roof landing, floating origin OK.");
            }
            finally
            {
                ((System.Collections.IList)Get(controller, "collisionCars")).Clear();
                UnityEngine.Object.DestroyImmediate(wall); UnityEngine.Object.DestroyImmediate(roof);
                UnityEngine.Object.DestroyImmediate(floor);
            }
        }

        [Serializable] private sealed class CabGeometry { public CabPart[] parts; }
        [Serializable] private sealed class CabPart
        { public string name; public PoseData[] poses; public Vector3[] vertices; public int[] indices; }
        [Serializable] private sealed class PoseData
        { public Vector3 position; public Quaternion rotation; public Vector3 scale; }

        private static void VerifyInstalledCab(object controller)
        {
            var path = Path.GetFullPath(Path.Combine(Application.dataPath,
                "../../artifacts/verification/leaf-cab-geometry.json"));
            Require(File.Exists(path), "Run Tools/prepare_leaf_cab_verification.py before this verification");
            var geometry = JsonUtility.FromJson<CabGeometry>(File.ReadAllText(path));
            var objects = new System.Collections.Generic.List<UnityEngine.Object>();
            var carrier = new GameObject("detached DH4 WALKABLE pose");
            objects.Add(carrier);
            try
            {
                foreach (var part in geometry.parts)
                {
                    var matrix = Matrix4x4.identity;
                    foreach (var pose in part.poses)
                        matrix *= Matrix4x4.TRS(pose.position, pose.rotation, pose.scale);
                    for (int i = 0; i < part.vertices.Length; i++)
                        part.vertices[i] = matrix.MultiplyPoint3x4(part.vertices[i]);
                    var mesh = new Mesh { vertices = part.vertices, triangles = part.indices };
                    mesh.RecalculateBounds(); objects.Add(mesh);
                    var go = new GameObject(part.name); go.layer = 11; objects.Add(go);
                    go.transform.SetParent(carrier.transform, false);
                    go.AddComponent<MeshCollider>().sharedMesh = mesh;
                }
                Physics.SyncTransforms();
                var type = controller.GetType().GetNestedType("LeafBody", All);
                foreach (float side in new[] { -1f, 1f })
                {
                    var leaf = Activator.CreateInstance(type, true);
                    Set(leaf, "Size", .27f);
                    var inside = new Vector3(0, 2.8f, -.95f);
                    Set(leaf, "Position", inside); Set(leaf, "Velocity", Vector3.left * side * 20);
                    Require((bool)Call(controller, "MoveWithCollisions", leaf,
                        inside + Vector3.right * side * 4, Vector3.zero) &&
                        ((Vector3)Get(leaf, "Position")).x * side > .7f,
                        "Leaf entered installed DH4 through a closed side window, side=" + side);
                }
                Set(controller, "surfaceQueriesRemaining", 10);
                var query = new object[] { new Vector3(0, 1, -.95f), Vector3.zero, null };
                Require((bool)Call(controller, "TryFindSurface", query) &&
                    ((Vector3)Get(query[2], "Position")).y > 3.5f, "Installed DH4 cab floor was seeded");
                // Departure starts very close to a supporting surface or a
                // window edge, rather than metres away as in the old sweep test.
                foreach (float side in new[] { -1f, 1f })
                {
                    var inside = new Vector3(0, 2.8f, -.95f);
                    RaycastHit hit;
                    Require(Physics.Raycast(inside + Vector3.right * side * 4,
                        Vector3.left * side, out hit, 4, 1 << 11), "DH4 window test setup failed");
                    var start = hit.point + hit.normal * .025f;
                    var leaf = Activator.CreateInstance(type, true);
                    Set(leaf, "Size", .27f); Set(leaf, "Position", inside);
                    Set(leaf, "Velocity", -hit.normal * 10);
                    Require((bool)Call(controller, "MoveWithCollisions", leaf, start, Vector3.zero) &&
                        Vector3.Dot((Vector3)Get(leaf, "Position") - hit.point, hit.normal) > .025f,
                        "Departure leaf with initial sphere/window overlap entered DH4, side=" + side);
                }
                VerifyDepartureSequence(controller, carrier.transform);
                Debug.Log("Installed DH4 WALKABLE body/windows: leaf sweeps blocked from both sides; low spawn probe selects cab roof.");
            }
            finally
            {
                for (int i = objects.Count - 1; i >= 0; i--) UnityEngine.Object.DestroyImmediate(objects[i]);
            }
        }

        private static void VerifyDepartureSequence(object controller, Transform carrier)
        {
            var entries = (System.Collections.IList)Get(controller, "collisionCars");
            var leafArray = (Array)Get(controller, "leaves");
            var leaf = Activator.CreateInstance(controller.GetType().GetNestedType("LeafBody", All), true);
            var entry = Activator.CreateInstance(controller.GetType().GetNestedType("CollisionCar", All), true);
            var offset = Vector3.zero;
            RaycastHit support;
            Require(Physics.Raycast(new Vector3(0, 8, 1.6f), Vector3.down, out support, 8, 1 << 11),
                "DH4 bonnet surface not found");
            Set(leaf, "Size", .27f); Set(leaf, "Settled", true);
            Set(leaf, "LandingPosition", support.point); Set(leaf, "Position", support.point + support.normal * .018f);
            Set(leaf, "GroundNormal", support.normal);
            Set(leaf, "SurfaceTransform", support.collider.transform);
            Set(leaf, "SurfaceLocalPosition", support.collider.transform.InverseTransformPoint(support.point));
            Set(leaf, "SurfaceLocalNormal", support.collider.transform.InverseTransformDirection(support.normal));
            Set(leaf, "SurfaceLocalRotation", Quaternion.FromToRotation(Vector3.forward, support.normal));
            Set(leaf, "SurfacePoseReady", true); Set(leaf, "SurfaceVelocitySamplePosition", support.point);
            leafArray.SetValue(leaf, 0); Set(controller, "leafCount", 1);
            entries.Add(entry);
            var previous = Matrix4x4.identity;
            int airborneFrames = 0;
            try
            {
                for (int frame = 0; frame < 240; frame++)
                {
                    const float dt = .02f;
                    float speed = Mathf.Min(12, (frame + 1) * .25f);
                    // Move the actual detached collider hierarchy each frame.
                    carrier.position += Vector3.forward * speed * dt;
                    carrier.rotation = Quaternion.Euler(0, frame * .025f, 0);
                    if (frame == 130) { offset = new Vector3(1000, 0, -500); carrier.position += offset; }
                    Physics.SyncTransforms();
                    var current = (Matrix4x4)Call(controller, "StableMatrix", carrier, offset);
                    Set(entry, "PreviousWorldToLocal", previous.inverse);
                    Set(entry, "CurrentLocalToWorld", current); Set(entry, "Moved", true);
                    Set(entry, "SweptBounds", new Bounds(carrier.position - offset, new Vector3(12, 16, 40)));
                    Call(controller, "UpdateAnchoredLeaves", offset, dt);
                    if (frame == 18)
                        Call(controller, "LiftLeafByWind", leaf, new Vector3(0, 0, -12), 1f);
                    Set(controller, "surfaceQueriesRemaining", frame % 2 == 0 ? 0 : 10);
                    Call(controller, "SimulateLeaves", 1f, carrier.position - offset, offset, new Vector3(0, 0, -12), dt);
                    var local = carrier.InverseTransformPoint((Vector3)Get(leaf, "Position") + offset);
                    Require(!(Mathf.Abs(local.x) < 1.08f && local.z > -2.1f && local.z < .28f &&
                        local.y > 1.5f && local.y < 3.7f), "DH4 departure sequence entered cab at frame " + frame + ": " + local);
                    if (!(bool)Get(leaf, "Settled")) airborneFrames++;
                    Call(controller, "UpdateAnchoredLeaves", offset, 0f);
                    previous = current;
                }
                Require(airborneFrames > 10, "Departure fixture never released the bonnet leaf");

                // The old spawn probe found a roof overhead and froze airborne
                // leaves in place. A failed landing lookup must allow descent.
                entries.Clear(); Set(leaf, "Settled", false);
                Set(leaf, "SurfaceTransform", null); Set(leaf, "Position", new Vector3(30, 2, 0));
                Set(leaf, "LandingPosition", new Vector3(30, 4, 0)); Set(leaf, "Velocity", Vector3.down);
                Set(leaf, "LandingRefreshed", false); Set(controller, "surfaceQueriesRemaining", 0);
                Call(controller, "SimulateLeaves", 1f, Vector3.zero, Vector3.zero, Vector3.zero, .02f);
                Require(((Vector3)Get(leaf, "Position")).y < 1.99f, "Missed landing query froze a flying leaf");
                Debug.Log("DH4 departure: 240 accelerating/turning frames, bonnet release, changing query budget and origin shift OK.");
            }
            finally
            {
                entries.Clear(); Set(controller, "leafCount", 0);
            }
        }

        private static void VerifyTrail(Assembly mod, string modPath)
        {
            var type = mod.GetType("DVSeasons.Mod.TrainSnowTrailController", true);
            var trail = Activator.CreateInstance(type, All, null, new object[] { modPath }, null);
            try
            {
                Call(trail, "EnsureParticles");
                var particles = (ParticleSystem)Get(trail, "particles");
                Require(particles != null, "Snow trail renderer unavailable");
                var main = particles.main; var sheet = particles.textureSheetAnimation;
                Require(Mathf.Abs(main.startSize.constantMin - .05f) < .0001f &&
                    Mathf.Abs(main.startSize.constantMax - .17f) < .0001f && !particles.sizeOverLifetime.enabled,
                    "Trail enlarged the individual snowfall flakes");
                Require(sheet.enabled && sheet.numTilesX == 4 && sheet.numTilesY == 4,
                    "Trail does not use the shared snowflake atlas");
                var emission = particles.emission;
                emission.rateOverTimeMultiplier = 4200; emission.rateOverDistanceMultiplier = 0;
                particles.Simulate(4, true, true, true);
                Require(particles.particleCount > 6000, "Dense trail is prematurely capped: " + particles.particleCount);
                var buffer = new ParticleSystem.Particle[main.maxParticles];
                var count = particles.GetParticles(buffer);
                for (var i = 0; i < count; i++)
                    Require(buffer[i].GetCurrentSize(particles) >= .0499f && buffer[i].GetCurrentSize(particles) <= .1701f,
                        "Snowflake size changed during its lifetime");
                Debug.Log("Runtime snow trail verified: " + count + " individual flakes, 0.05–0.17 m, 16 atlas shapes.");
            }
            finally
            {
                DestroyRuntimeObjects(trail, "owner", "material", "texture");
                ((IDisposable)trail).Dispose();
            }
        }

        private static void VerifyAttachment(object controller, Transform anchor)
        {
            var leaf = Activator.CreateInstance(controller.GetType().GetNestedType("LeafBody", All), true);
            ((Array)Get(controller, "leaves")).SetValue(leaf, 0); Set(controller, "leafCount", 1);
            Set(leaf, "SurfaceTransform", anchor);
            var localPosition = new Vector3(.2f, .1f, .3f);
            Set(leaf, "SurfaceLocalPosition", localPosition); Set(leaf, "SurfaceLocalNormal", Vector3.up);
            var localRotation = Quaternion.FromToRotation(Vector3.forward, Vector3.up);
            Set(leaf, "SurfaceLocalRotation", localRotation); Set(leaf, "SurfacePoseReady", true);
            Set(leaf, "Settled", true);
            var shift = new Vector3(1000, 0, -500);
            anchor.position = shift + new Vector3(15, 2, 8); anchor.rotation = Quaternion.Euler(4, 77, 6);
            Call(controller, "UpdateAnchoredLeaves", shift, 0f);
            var expected = anchor.TransformPoint(localPosition) - shift + anchor.up * .018f;
            Require(Vector3.Distance((Vector3)Get(leaf, "Position"), expected) < .0002f,
                "Leaf slid off a translated/rotated vehicle after an origin shift");
            Require(Quaternion.Angle(Quaternion.Euler((Vector3)Get(leaf, "Rotation")),
                anchor.rotation * localRotation) < .02f, "Leaf orientation slipped relative to its vehicle");
            Set(leaf, "SurfaceVelocitySamplePosition", (Vector3)Get(leaf, "LandingPosition"));
            anchor.position += Vector3.right * .2f;
            Call(controller, "UpdateAnchoredLeaves", shift, 0f);
            Call(controller, "UpdateAnchoredLeaves", shift, .02f);
            Require(Mathf.Abs(((Vector3)Get(leaf, "SurfaceVelocity")).x - 10f) < .02f,
                "Pre-cull pose refresh erased carrier velocity before lift-off");
            Call(controller, "LiftLeafByWind", leaf, new Vector3(0, 0, 12), 1f);
            Require(!(bool)Get(leaf, "Settled") && Get(leaf, "SurfaceTransform") == null &&
                ((Vector3)Get(leaf, "Velocity")).x > 9.5f, "Wind cannot release an anchored leaf with train momentum");
            Debug.Log("Runtime leaves verified: moving surface attachment and wind release with inherited velocity.");
        }

        private static void DestroyRuntimeObjects(object controller, params string[] names)
        {
            // The runtime uses delayed Destroy. This fixture runs in edit mode,
            // so release its transient objects before invoking normal disposal.
            foreach (var name in names)
            {
                var obj = Get(controller, name) as UnityEngine.Object;
                if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
            }
        }

        private static Color[] Capture(Camera camera, RenderTexture target, string path)
        { camera.Render(); return SaveTexture(target, path); }
        private static Color[] SaveTexture(RenderTexture target, string path)
        {
            var old = RenderTexture.active;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
                if (path != null) File.WriteAllBytes(path, image.EncodeToPNG());
                return image.GetPixels();
            }
            finally { RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(image); }
        }
        private static float Difference(Color[] a, Color[] b)
        {
            float sum = 0; for (var i = 0; i < a.Length; i++)
                sum += Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b);
            return sum / a.Length;
        }
        private static float CentreBrightness(Color[] pixels)
        {
            float sum = 0; for (var y = 120; y < 136; y++) for (var x = 120; x < 136; x++)
                sum += pixels[y * 256 + x].grayscale;
            return sum / 256;
        }
    }
}
