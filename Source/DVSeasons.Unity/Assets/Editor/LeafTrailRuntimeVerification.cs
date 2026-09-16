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
            var output = Path.Combine(root, "artifacts/verification/0.3.13-lifetime-heater");
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
                VerifyRailContacts(mod);
                VerifyAxleContactHeights(mod);
                Debug.Log("LEAF_TRAIL_RUNTIME_OK: leaf species/colour atlas, flight collisions, moving surfaces, roof occlusion, lighting, native per-car dust size and position.");
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
            var type=mod.GetType("DVSeasons.Mod.TrainSnowTrailController",true);
            var repositoryType=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
            var repository=Activator.CreateInstance(repositoryType,new object[]{modPath});
            var bundle=AssetBundle.LoadFromFile(Path.Combine(modPath,"AssetBundles/dvseasons_dv99"));
            repositoryType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{bundle});
            var trail=Activator.CreateInstance(type,All,null,new object[]{repository},null);
            var nativeObject=new GameObject("native dust fixture");
            var native=nativeObject.AddComponent<ParticleSystem>();
            var nativeMain=native.main;nativeMain.startSize=8f;
            var nativeMaterial=new Material(Shader.Find("Particles/Standard Unlit"));
            var nativeTexture=new Texture2D(4,4,TextureFormat.RGBA32,false,false);
            var originalPixels=new Color32[16];
            for(int i=0;i<16;i++)originalPixels[i]=new Color32(110,65,28,(byte)(i*17));
            nativeTexture.SetPixels32(originalPixels);nativeTexture.Apply();nativeMaterial.mainTexture=nativeTexture;
            nativeObject.GetComponent<ParticleSystemRenderer>().sharedMaterial=nativeMaterial;
            var poseObject=new GameObject("car pose");
            try
            {
                SnowDustVerification.Verify(bundle);
                Call(trail,"EnsureParticles",native);
                var particles=(ParticleSystem)Get(trail,"particles");
                Require(particles!=null,"Native dust unavailable");
                var recoloured=(Texture2D)Get(trail,"snowTexture");
                var recolouredPixels=recoloured.GetPixels32();
                Require(recoloured!=nativeTexture,"Shared derailment texture was changed");
                for(int i=0;i<16;i++)
                {
                    var p=recolouredPixels[i];
                    Require(p.r>225 && p.b>=p.g && p.g>=p.r,"Dust still has an earth-coloured tint");
                    Require(Math.Abs(p.a-originalPixels[i].a)<=1,"Dust recolouring changed transparency");
                    Require(nativeTexture.GetPixels32()[i].Equals(originalPixels[i]),"Native dust texture mutated");
                }
                File.WriteAllBytes(Path.Combine(modPath,"../../verification/0.3.13-snow-dust.png"),recoloured.EncodeToPNG());
                Require(((Material)Get(trail,"material")).shader.name=="DVSeasons/SnowDust","Native earth-dust shader still used");
                Require(Mathf.Abs(particles.main.startSize.constant-.5f)<.001f,"Dust size is not 25% of the previous trail (6.25% of native)");
                Require(!particles.emission.enabled,"Automatic emission can draw a streak when source jumps");
                Require(particles.main.simulationSpace==ParticleSystemSimulationSpace.Custom,
                    "Snow dust cannot follow floating origin independently of cars");
                var pose=poseObject.transform;
                foreach(float angle in new[]{0f,75f,180f}) foreach(float reverse in new[]{-1f,1f})
                {
                    pose.rotation=Quaternion.Euler(0,angle,0);pose.position=new Vector3(100,3,-30);
                    var direction=pose.forward*reverse;
                    var localContact=new Vector3(.1f,-1.4f,.6f);
                    var point=(Vector3)Call(trail,"EmissionPoint",pose,localContact,pose.up);
                    Require(Vector3.Distance(point,pose.TransformPoint(localContact)+pose.up*.1f)<.00001f,"Snow source is not just above the rail contact");
                }
                particles.Emit(new ParticleSystem.EmitParams{position=new Vector3(2,3,4),velocity=Vector3.zero},1);
                var buffer=new ParticleSystem.Particle[4];particles.GetParticles(buffer);var before=buffer[0].position;
                ((GameObject)Get(trail,"owner")).transform.position=new Vector3(1000,0,-500);
                particles.GetParticles(buffer);
                Require(buffer[0].position==before,"Origin shift modified stable dust coordinates");
                VerifyWheelDust(trail,particles);
                VerifySnowEnvironment(trail,particles);
                var flow=particles.velocityOverLifetime;
                Set(trail,"wind",new Vector3(8,0,-3));Call(trail,"UpdateAirflow");
                Require(flow.enabled && Mathf.Abs(flow.x.constant-4.4f)<.001f,"Wind does not advect existing airborne snow");
                Require(particles.main.gravityModifier.constant>0 && particles.limitVelocityOverLifetime.drag.constant>0,"Snow never settles or slows");
                Require(particles.main.maxParticles<=1800,"Snow particle budget grew unexpectedly");
                Require(Mathf.Abs((float)Call(trail,"SampleTrailSize",25f)-.325f)<.001f,"Low-speed particle size not reduced");
                Require(Mathf.Abs((float)Call(trail,"SampleTrailSize",100f)-1.1f)<.001f,"High-speed particle size did not grow");
                Require(Mathf.Abs((float)Call(trail,"SampleTrailSize",180f)-1.1f)<.001f,"High-speed particle size has no cap");
                Require(particles.main.startLifetime.constantMin>=3 && particles.main.startLifetime.constantMax>=5,
                    "Main snow particles still have the short lifetime");
                Require(particles.sizeOverLifetime.size.Evaluate(1) / particles.sizeOverLifetime.size.Evaluate(0)>3,
                    "Cloud expansion did not increase");
                SnowPlumePreview.Verify(particles,Path.GetFullPath(Path.Combine(modPath,"../../..")),(float)Call(trail,"SampleTrailSize",60f));
                Require((float)Call(trail,"EmissionRate",24f/3.6f,1f)==0,"Main trail appeared below 25 km/h");
                Require((float)Call(trail,"EmissionRate",26f/3.6f,1f)>0,"Main trail missing above 25 km/h");
                Require((float)Call(trail,"EmissionRate",26f/3.6f,0f)==0,"Snow-free season emits dust");
                Debug.Log("Snow dust verified: 25% of previous trail size, no distance streaks, one rail-height source forward/reverse/turn, stable origin.");
            }
            finally
            {
                DestroyRuntimeObjects(trail,"owner","material","snowTexture");((IDisposable)trail).Dispose();
                UnityEngine.Object.DestroyImmediate(nativeObject);UnityEngine.Object.DestroyImmediate(nativeMaterial);
                UnityEngine.Object.DestroyImmediate(nativeTexture);
                UnityEngine.Object.DestroyImmediate(poseObject);
                ((IDisposable)repository).Dispose();
            }
        }

        private static void VerifyWheelDust(object trail,ParticleSystem particles)
        {
            var cameraObject=new GameObject("wheel dust camera");cameraObject.tag="MainCamera";
            var camera=cameraObject.AddComponent<Camera>();camera.transform.position=new Vector3(0,2,-3);
            var roof=GameObject.CreatePrimitive(PrimitiveType.Cube);roof.layer=0;
            roof.transform.position=new Vector3(20,2,0);roof.transform.localScale=new Vector3(5,.2f,5);
            try
            {
                Physics.SyncTransforms();particles.Clear();Set(trail,"coverage",1f);Set(trail,"light",1f);
                Set(trail,"wheelBudget",64);
                Vector2 remaining=Vector2.one;
                Action<int,Vector3,Vector3> wheel=(id,p,v)=>Call(trail,"WheelAt",id,p,Vector3.right,Vector3.up,v,remaining);
                wheel(1,Vector3.zero,Vector3.forward*2);Require(particles.particleCount==0,"Spawn emitted wheel dust");
                wheel(1,Vector3.forward,Vector3.forward*2);
                var wheelState=((System.Collections.IDictionary)Get(trail,"wheels"))[1];
                Require(particles.particleCount==4,"Low-speed snowy rail emitted no dust: count="+particles.particleCount+
                    ", tracked="+(wheelState!=null)+
                    ", camera="+(Camera.main!=null ? Camera.main.name : "none"));
                var buffer=new ParticleSystem.Particle[8];particles.GetParticles(buffer);
                bool left=false,right=false;foreach(var p in buffer)
                {left|=p.position.x<-.7f;right|=p.position.x>.7f;}
                Require(left && right,"Wheel dust missed one rail");
                particles.Clear();wheel(1,Vector3.forward,Vector3.zero);
                Require(particles.particleCount==0,"Stationary wheel emitted dust");
                wheel(1,Vector3.forward*30,Vector3.forward*2);
                Require(particles.particleCount==0,"Teleport emitted dust");
                wheel(2,Vector3.right*20,Vector3.forward*2);wheel(2,Vector3.right*20+Vector3.forward,Vector3.forward*2);
                Require(particles.particleCount==0,"Sheltered depot rails emitted snow");
                Set(trail,"coverage",0f);wheel(3,Vector3.zero,Vector3.forward*2);wheel(3,Vector3.forward,Vector3.forward*2);
                Require(particles.particleCount==0,"Snow-free rails emitted snow");
                Set(trail,"coverage",1f);wheel(4,Vector3.forward*2,Vector3.back*2);wheel(4,Vector3.forward,Vector3.back*2);
                Require(particles.particleCount>0,"Reverse wheel movement emitted no snow");
                particles.Clear();remaining=new Vector2(0,1);
                wheel(5,Vector3.zero,Vector3.forward*2);wheel(5,Vector3.forward,Vector3.forward*2);
                Require(particles.particleCount==2,"Cleared left rail emitted snow or snowy right rail failed");
                int actual=particles.GetParticles(buffer);for(int i=0;i<actual;i++)Require(buffer[i].position.x>.7f,"Cleared rail emitted snow");
                particles.Clear();remaining=Vector2.zero;
                wheel(5,Vector3.forward*2,Vector3.forward*2);Require(particles.particleCount==0,"Following axle emitted from cleared rails");
                // Keep physical queries bounded even when many axles request
                // shelter checks at once; oldest queued requests must progress.
                particles.Clear();remaining=Vector2.one;
                var tracked=(System.Collections.IDictionary)Get(trail,"wheels");
                Set(trail,"wheelShelterBudget",16);
                for(int id=100;id<140;id++){wheel(id,Vector3.zero,Vector3.forward*2);wheel(id,Vector3.forward,Vector3.forward*2);}
                var pending=Get(trail,"wheelShelterQueries");
                Require((int)pending.GetType().GetProperty("Count").GetValue(pending,null)>0,"Shelter query budget did not queue work");
                for(int frame=0;frame<6;frame++)
                {Set(trail,"wheelShelterBudget",16);Call(trail,"ProcessShelterQueries",Vector3.zero);}
                for(int id=100;id<140;id++)
                {var ready=(bool[])Get(tracked[id],"ShelterReady");Require(ready[0] && ready[1],"Tail axle starved in shelter query queue");}
                Require((int)pending.GetType().GetProperty("Count").GetValue(pending,null)==0,"Shelter queue did not drain");
                Debug.Log("WHEEL_SNOW_OK: both rails, low speed and reverse; no stationary, spawn, teleport, roof or snow-free emissions; native dust alpha preserved.");
            }
            finally{UnityEngine.Object.DestroyImmediate(cameraObject);UnityEngine.Object.DestroyImmediate(roof);}
        }

        private static void VerifySnowEnvironment(object trail,ParticleSystem particles)
        {
            var ground=GameObject.CreatePrimitive(PrimitiveType.Cube);ground.layer=0;
            ground.transform.position=new Vector3(40,-.2f,0);ground.transform.localScale=new Vector3(6,.4f,6);
            var roof=GameObject.CreatePrimitive(PrimitiveType.Cube);roof.layer=0;
            roof.transform.position=new Vector3(40,2,0);roof.transform.localScale=new Vector3(6,.2f,6);roof.SetActive(false);
            try
            {
                Physics.SyncTransforms();
                Func<Vector3,float,float> sample=(p,amount)=>(float)Call(trail,"SampleSurfaceSnow",p,Vector3.right,Vector3.up,amount,Vector3.zero);
                Require(sample(new Vector3(40,0,0),1)>.95f,"Snowy ballast cannot feed the main plume");
                Require(sample(new Vector3(40,0,0),0)==0,"Bare ballast feeds snow plume");
                Require(sample(new Vector3(40,4,0),1)==0,"Unsupported space above rail feeds plume");
                roof.SetActive(true);Physics.SyncTransforms();
                Require(sample(new Vector3(40,0,0),1)==0,"Covered depot feeds main snow plume");
                Debug.Log("SNOW_ENVIRONMENT_OK: snowy ballast, bare ballast, missing support and roof checks; wind, drag and settling verified.");
            }
            finally{UnityEngine.Object.DestroyImmediate(ground);UnityEngine.Object.DestroyImmediate(roof);}
        }

        private static void VerifyAxleContactHeights(Assembly mod)
        {
            var type=mod.GetType("DVSeasons.Mod.RailSnowGameSource",true);
            var contact=type.GetMethod("ContactPoint",All);
            var bogie=new GameObject("S282 bogie fixture");
            var axle=new GameObject("S282 axle fixture");axle.transform.SetParent(bogie.transform,false);
            var tracks=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.RailSnowTracks",true),true);
            try
            {
                // Actual LocoS282A prefab heights/offsets: leading, powered,
                // trailing axle. All share a rail plane despite different radii.
                var offsets=new[]{new Vector3(-.0000858f,.35361266f,2.54748249f),
                    new Vector3(0,.71212983f,.0058775f),new Vector3(0,.57801986f,-2.52498198f)};
                foreach(var shift in new[]{Vector3.zero,new Vector3(1000,120,-500)})
                foreach(var slope in new[]{Quaternion.identity,Quaternion.Euler(9,127,7)})
                foreach(var position in offsets)foreach(float spin in new[]{0f,83f,241f})
                {
                    bogie.transform.SetPositionAndRotation(shift,slope);
                    axle.transform.localPosition=position;axle.transform.localRotation=Quaternion.Euler(spin,0,0);
                    var expected=bogie.transform.TransformPoint(new Vector3(position.x,0,position.z));
                    var actual=(Vector3)contact.Invoke(null,new object[]{axle.transform,bogie.transform});
                    Require(Vector3.Distance(actual,expected)<.0002f,"Small S282 wheel contact fell below the rail on slope/spin/origin shift");
                }
                bogie.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);
                axle.transform.localPosition=offsets[0];
                var first=(Vector3)contact.Invoke(null,new object[]{axle.transform,bogie.transform});
                Call(tracks,"WheelAt",1,first,Vector3.right,Vector3.forward);
                bogie.transform.position=Vector3.forward;
                var next=(Vector3)contact.Invoke(null,new object[]{axle.transform,bogie.transform});
                Call(tracks,"WheelAt",1,next,Vector3.right,Vector3.forward);
                float remaining=(float)Call(tracks,"RemainingAt",(first+next)*.5f+Vector3.right*.75f);
                Require(remaining<.01f,"Leading axle did not clear the visible rail for following wheels");
                Debug.Log("S282_AXLE_CONTACT_OK: actual leading/powered/trailing heights, slopes, spinning wheels, origin shift and rail clearing.");
            }
            finally{((IDisposable)tracks).Dispose();UnityEngine.Object.DestroyImmediate(bogie);}
        }

        private static void VerifyRailContacts(Assembly mod)
        {
            var type=mod.GetType("DVSeasons.Mod.RailSnowTracks",true);
            var tracks=Activator.CreateInstance(type,true);
            try
            {
                Action<int,Vector3> stamp=(id,p)=>Call(tracks,"WheelAt",id,p,Vector3.right,Vector3.forward);
                Func<Vector3,float> remaining=p=>(float)Call(tracks,"RemainingAt",p);
                Require(remaining(new Vector3(.75f,0,3))==1,"Fresh rail is clear");
                stamp(1,Vector3.zero);stamp(1,Vector3.forward*5);
                Require(remaining(new Vector3(.75f,0,3))<.01f && remaining(new Vector3(-.75f,0,3))<.01f,"Extended track missing from index");
                Require(remaining(new Vector3(.75f,1,3))==1 && remaining(new Vector3(.75f,0,6))==1,"Track clears another height or untouched rail");
                stamp(2,new Vector3(1.5f,0,0));stamp(2,new Vector3(1.5f,0,5));
                Require(remaining(new Vector3(2.25f,0,3))<.01f,"Independent adjacent rail missed");
                var records=Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(mod.GetType("DVSeasons.Mod.RailSnowStamp",true)));
                Call(tracks,"Save",records);Call(tracks,"Restore",records);
                Require(remaining(new Vector3(.75f,0,3))<.01f,"Restored clear rails emit snow");
                Call(tracks,"Advance",1f,90f);
                Require(Mathf.Abs(remaining(new Vector3(.75f,0,3))-.5f)<.001f,"Snowfall does not refill cleared tracks");
                Set(tracks,"WorldOffset",new Vector3(1000,0,-500));
                Require(Mathf.Abs(remaining(new Vector3(1000.75f,0,-497))-.5f)<.001f,"Origin shift moved rail history");
                var pattern=mod.GetType("DVSeasons.Mod.SnowCoveragePattern",true);
                var sample=pattern.GetMethod("At",All);
                var texture=(Texture2D)pattern.GetMethod("CreateTexture",All).Invoke(null,null);
                Require(texture!=null,"Shared snow noise unavailable");UnityEngine.Object.DestroyImmediate(texture);
                bool snowy=false,bare=false;
                for(int z=0;z<100;z++)
                {
                    float cover=(float)sample.Invoke(null,new object[]{new Vector3(.75f,0,z),.4f});
                    snowy|=cover>.8f;bare|=cover<.01f;
                }
                Require(snowy && bare,"Partial snow does not distinguish bare rail patches");
                Debug.Log("RAIL_CONTACT_SNOW_OK: cleared/untouched rails, two sides, vertical separation, extending ribbons, save/restore, snowfall refill, origin shift, partial coverage.");
            }
            finally{((IDisposable)tracks).Dispose();}
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
