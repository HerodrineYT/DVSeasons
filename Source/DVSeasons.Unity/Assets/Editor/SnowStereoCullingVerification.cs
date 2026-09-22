using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowStereoCullingVerification
    {
        const BindingFlags All = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        static int checks;
        static void Require(bool condition, string message)
        { checks++; if (!condition) throw new InvalidOperationException(message); }

        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "../.."));
            string runtime = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD") ?? Path.Combine(root, "artifacts/build/DVSeasons");
            string game = Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME") ?? "F:/steam/steamapps/common/Derail Valley";
            ResolveEventHandler resolver = (sender, args) => {
                foreach (string directory in new[] {runtime, Path.Combine(game, "DerailValley_Data/Managed"),
                    Path.Combine(game, "DerailValley_Data/Managed/UnityModManager")})
                { string file = Path.Combine(directory, new AssemblyName(args.Name).Name + ".dll"); if (File.Exists(file)) return Assembly.LoadFrom(file); }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            int result = 0;
            try { Verify(Assembly.LoadFrom(Path.Combine(runtime, "DVSeasons.dll"))); }
            catch (Exception error) { Debug.LogException(error); result = 1; }
            finally { AppDomain.CurrentDomain.AssemblyResolve -= resolver; }
            EditorApplication.Exit(result);
        }

        static void Verify(Assembly mod)
        {
            Type type = mod.GetType("DVSeasons.Mod.SnowCameraFrustum", true);
            var prepare = (Func<Camera, Plane[], Plane[], Plane[]>)Delegate.CreateDelegate(
                typeof(Func<Camera, Plane[], Plane[], Plane[]>), type.GetMethod("Prepare", All));
            var intersects = (Func<Plane[], Plane[], Bounds, bool>)Delegate.CreateDelegate(
                typeof(Func<Plane[], Plane[], Bounds, bool>), type.GetMethod("Intersects", All));
            var rayLength = (Func<Matrix4x4, float>)Delegate.CreateDelegate(typeof(Func<Matrix4x4, float>),
                type.GetMethod("MaximumRayLength", All, null, new[] {typeof(Matrix4x4)}, null));
            var cameraObject = new GameObject("Snow stereo culling verification");
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.aspect = 1f; camera.fieldOfView = 60f;
            camera.nearClipPlane = .1f; camera.farClipPlane = 100f;
            var primary = new Plane[6]; var secondary = new Plane[6]; var reference = new Plane[6];
            try
            {
                Require(!camera.stereoEnabled, "Offline mono fixture unexpectedly has XR enabled");
                Require(prepare(camera, primary, secondary) == null, "Mono path retained a second eye");
                GeometryUtility.CalculateFrustumPlanes(camera, reference);
                for (int i = 0; i < 6; i++)
                    Require(primary[i].normal == reference[i].normal && primary[i].distance == reference[i].distance,
                        "Mono plane changed at " + i);
                for (int i = -80; i <= 80; i++)
                {
                    var bounds = new Bounds(new Vector3(i * .1f, 0, 3), Vector3.one * .005f);
                    Require(intersects(primary, null, bounds) == GeometryUtility.TestPlanesAABB(reference, bounds),
                        "Mono visibility changed at x=" + bounds.center.x);
                }

                float tangent = Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f);
                Require(Mathf.Abs(rayLength(camera.projectionMatrix) - Mathf.Sqrt(1f + tangent * tangent * 2f)) < .00001f,
                    "Symmetric projection padding changed");
                var offAxis = Matrix4x4.Frustum(-.2f, .08f, -.1f, .14f, .1f, 100f);
                Require(Mathf.Abs(rayLength(offAxis) - Mathf.Sqrt(1f + 2f * 2f + 1.4f * 1.4f)) < .0001f,
                    "Off-axis projection padding did not include the furthest eye corner");

                // Use actual Unity camera view matrices and clip-space plane
                // extraction, with a normal 64 mm eye separation. CPU frustum
                // extraction deliberately receives projection, not GPU depth UVs.
                foreach (bool orthographic in new[] {false, true})
                {
                    camera.orthographic = orthographic; camera.orthographicSize = 1.5f;
                    camera.transform.position = new Vector3(-.032f, 0, 0);
                    GeometryUtility.CalculateFrustumPlanes(camera.projectionMatrix * camera.worldToCameraMatrix, primary);
                    camera.transform.position = new Vector3(.032f, 0, 0);
                    GeometryUtility.CalculateFrustumPlanes(camera.projectionMatrix * camera.worldToCameraMatrix, secondary);
                    float edge = orthographic ? camera.orthographicSize : 3f * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f);
                    var leftOnly = new Bounds(new Vector3(-edge - .016f, 0, 3), Vector3.one * .001f);
                    var rightOnly = new Bounds(new Vector3(edge + .016f, 0, 3), Vector3.one * .001f);
                    Require(GeometryUtility.TestPlanesAABB(primary, leftOnly) && !GeometryUtility.TestPlanesAABB(secondary, leftOnly), "Left-only case did not straddle eye frusta");
                    Require(!GeometryUtility.TestPlanesAABB(primary, rightOnly) && GeometryUtility.TestPlanesAABB(secondary, rightOnly), "Right-only case did not straddle eye frusta");
                    Require(intersects(primary, secondary, leftOnly), "Left-eye-only object disappeared");
                    Require(intersects(primary, secondary, rightOnly), "Right-eye-only object disappeared");
                    Require(intersects(primary, secondary, new Bounds(new Vector3(0, 0, 3), Vector3.one * .001f)), "Binocular object disappeared");
                    Require(!intersects(primary, secondary, new Bounds(new Vector3(edge + .1f, 0, 3), Vector3.one * .001f)), "Object outside both eyes was retained");
                    Require(!intersects(primary, secondary, new Bounds(new Vector3(0, 0, -.1f), Vector3.one * .001f)), "Object behind both eyes was retained");
                    Require(!intersects(primary, secondary, new Bounds(new Vector3(0, 0, 101f), Vector3.one * .001f)), "Object past both far planes was retained");

                    // Repeated production delegate calls avoid reflection boxing.
                    for (int i = 0; i < 100000; i++)
                        if (!intersects(primary, secondary, (i & 1) == 0 ? leftOnly : rightOnly))
                            throw new InvalidOperationException("Reused eye planes became stale");
                    checks += 100000;
                }
                Debug.Log("SNOW_STEREO_CULLING_OK: " + checks + " checks; unchanged mono planes/visibility; perspective and orthographic left-eye-only/right-eye-only objects retained; binocular/offscreen/behind/far rejection; reusable plane arrays and 200000 unboxed production tests.");
            }
            finally { UnityEngine.Object.DestroyImmediate(cameraObject); }
        }
    }
}
