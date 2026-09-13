using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class Release022Verification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static object Get(object o,string name) {return o.GetType().GetField(name,All).GetValue(o);}
        static void Set(object o,string name,object value) {o.GetType().GetField(name,All).SetValue(o,value);}
        static object Call(object o,string name,params object[] args) {return o.GetType().GetMethod(name,All).Invoke(o,args);}
        static void Require(bool ok,string message) {if(!ok)throw new Exception(message);}
        public static void Run()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var modPath=Path.Combine(root,"artifacts/build/DVSeasons");
            var managed=Path.Combine(Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME"),"DerailValley_Data/Managed");
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=> {
                foreach(var dir in new[]{modPath,managed,Path.Combine(managed,"UnityModManager")})
                {string file=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}
                return null;
            };
            int code=0;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                var mod=Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll"));
                ColdFeaturesVerification.Run();
                VerifyGlass(mod,root);
                VerifyPollinators(mod,modPath,root);
                Assembly.LoadFrom(Path.Combine(root,"artifacts/verification/VerifySpringRuntime.dll"))
                    .GetType("VerifySpringRuntime",true).GetMethod("Run").Invoke(null,new object[]{modPath});
                Debug.Log("RELEASE_023_GPU_OK: dry winter selection, unregistered rear glass, flower insects and supplied audio.");
            }
            catch(Exception e) {Debug.LogException(e);code=1;}
            EditorApplication.Exit(code);
        }
        static void VerifyGlass(Assembly mod,string root)
        {
            var type=mod.GetType("DVSeasons.Mod.WinterWindowController",true);
            var controller=Activator.CreateInstance(type,new object[]{null});
            var go=new GameObject("dm1u-150_window_door");
            var external=new GameObject("LocoDM1U_ExternalInteractables/C_DoorRear");
            go.transform.SetParent(external.transform,false);
            var carRoot=new GameObject("Inactive DM1U fixture");carRoot.SetActive(false);
            var car=carRoot.AddComponent(Assembly.Load("Assembly-CSharp").GetType("TrainCar",true));
            Set(car,"<loadedExternalInteractables>k__BackingField",external);
            var mesh=new Mesh();mesh.vertices=new[]{new Vector3(1,2,-4),new Vector3(3,2,-4),new Vector3(3,3,-4),new Vector3(1,3,-4)};
            mesh.triangles=new[]{0,1,2,0,2,3};mesh.RecalculateBounds();mesh.RecalculateNormals();
            go.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=new Material(Shader.Find("Standard")){name="Glass",color=Color.black};
            var camera=new GameObject("Glass test camera").AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=.6f;
            camera.transform.position=new Vector3(2,2.5f,-8);camera.transform.LookAt(new Vector3(2,2.5f,-4));
            camera.backgroundColor=Color.black;camera.clearFlags=CameraClearFlags.SolidColor;
            camera.targetTexture=new RenderTexture(256,128,24);camera.targetTexture.Create();
            var shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/WinterWindow.shader");Set(controller,"shader",shader);Set(controller,"lighting",1f);
            try
            {
                Call(controller,"DiscoverDm1uCarGlass",car,0f);
                Call(controller,"DiscoverDm1uCarGlass",car,0f);
                Require(((IList)Get(controller,"panes")).Count==1,"Detached rear door missed or bound twice");
                var pane=((IList)Get(controller,"panes"))[0];
                var cab=Get(pane,"Cab");
                Call(Get(cab,"Climate"),"Advance",0f,-20f,false,-20f,0f,false,1f);
                Require(Get(pane,"Window")==null,"Fallback unexpectedly registered a native rain Window");
                Call(controller,"SetProperties",pane,0);
                var overlay=(MeshRenderer)((IList)Get(pane,"Overlays"))[0];overlay.SetPropertyBlock((MaterialPropertyBlock)Get(pane,"Properties"));
                float frozen=Sample(camera,Path.Combine(root,"artifacts/verification/dm1u-rear-frost-0.3.23.png"));
                Require(frozen>.15f,"Rear glass without native Window has no visible frost: "+frozen);
                Call(controller,"Wipe",pane);
                var matrix=(Matrix4x4)Call(controller,"MovingPaneMatrix",pane);
                var old=matrix.MultiplyPoint3x4(go.transform.TransformPoint(new Vector3(2,2.5f,-4)));
                go.transform.position=new Vector3(500,20,-800);go.transform.rotation=Quaternion.Euler(0,72,0);
                matrix=(Matrix4x4)Call(controller,"MovingPaneMatrix",pane);
                Require(Vector3.Distance(old,matrix.MultiplyPoint3x4(go.transform.TransformPoint(new Vector3(2,2.5f,-4))))<.001f,"Rear projection drifts after movement");
                go.transform.position=Vector3.zero;go.transform.rotation=Quaternion.identity;
                for(int i=0;i<300;i++)Call(Get(cab,"Climate"),"Advance",5f,20f,true,80f,1f,false,0f);
                Call(controller,"SetProperties",pane,0);overlay.SetPropertyBlock((MaterialPropertyBlock)Get(pane,"Properties"));
                float clear=Sample(camera,null);Require(clear<frozen*.1f,"Rear frost fails to thaw");
                Debug.Log("DVSeasons unregistered DM1U rear pane verified: visible frost, local projection, no fake rain component, complete thaw.");
            }
            finally {((IDisposable)controller).Dispose();UnityEngine.Object.DestroyImmediate(external);UnityEngine.Object.DestroyImmediate(carRoot);UnityEngine.Object.DestroyImmediate(camera.gameObject);}
        }
        static void VerifyPollinators(Assembly mod,string modPath,string root)
        {
            var type=mod.GetType("DVSeasons.Mod.SpringLifeController",true);
            var controller=Activator.CreateInstance(type,new object[]{modPath});
            try
            {
                var source=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.AutumnTreeSourceSnapshot",true));
                Set(source,"Key",4L);Set(source,"CanopyWorldPosition",Vector3.zero);
                Call(controller,"Create",source,Vector3.zero);
                var patches=(IList)Get(controller,"patches");Require(patches.Count==1,"Flower patch not created");
                var clip=(AudioClip)Get(controller,"clip");Require(clip!=null && clip.length>27 && clip.length<28,"Supplied bee recording missing or truncated");
                var patch=patches[0];var audio=(AudioSource)Get(patch,"Audio");
                Require(audio.spatialBlend==1 && audio.maxDistance<=12 && audio.loop,"Bee sound is not local to flowers");
                var bees=(Transform[])Get(patch,"Bees");Require(bees.Length==3,"Unbounded bee group");
                var butterfly=(Transform)Get(patch,"Butterfly");butterfly.localScale=Vector3.one;
                var camera=new GameObject("Pollinator camera").AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=.065f;camera.nearClipPlane=.005f;
                camera.transform.position=new Vector3(0,.2f,-.08f);camera.transform.LookAt(Vector3.zero);
                camera.backgroundColor=new Color(.08f,.13f,.055f);camera.clearFlags=CameraClearFlags.SolidColor;
                camera.targetTexture=new RenderTexture(512,384,24);camera.targetTexture.Create();
                var sun=new GameObject("Sun").AddComponent<Light>();sun.type=LightType.Directional;sun.transform.rotation=Quaternion.Euler(55,0,0);
                RenderSettings.ambientLight=Color.gray*.3f;
                Require(Sample(camera,Path.Combine(root,"artifacts/verification/spring-butterfly-0.3.23.png"))>camera.backgroundColor.grayscale+.005f,
                    "Butterfly geometry did not produce visible lit pixels");
                UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(sun.gameObject);
                Debug.Log("DVSeasons spring patch verified: 27.12s provided audio, 3 positional bees, one lit butterfly, complete disposal.");
            }
            finally {((IDisposable)controller).Dispose();Require(((IList)Get(controller,"patches")).Count==0,"Spring emitters survive disposal");}
        }
        static float Sample(Camera camera,string file)
        {
            camera.Render();var previous=RenderTexture.active;RenderTexture.active=camera.targetTexture;
            var pixels=new Texture2D(camera.targetTexture.width,camera.targetTexture.height,TextureFormat.RGB24,false);
            pixels.ReadPixels(new Rect(0,0,pixels.width,pixels.height),0,0);pixels.Apply();
            float sum=0;foreach(var c in pixels.GetPixels())sum+=c.grayscale;
            if(file!=null)File.WriteAllBytes(file,pixels.EncodeToPNG());
            float mean=sum/(pixels.width*pixels.height);UnityEngine.Object.DestroyImmediate(pixels);RenderTexture.active=previous;return mean;
        }
    }
}
