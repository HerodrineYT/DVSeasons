using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class WaterIceRuntimeVerification
    {
        private const BindingFlags All=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        private static object repository, controller;
        private static Action<float,float> apply;
        private static Camera camera;
        private static RenderTexture target;
        private static Material water;
        private static Mesh mesh;
        private static MeshFilter lakeFilter;
        private static GameObject root;
        private static float deadline;
        private static int stage, frames, discoveryFrames;
        private static int sceneryCount=10000;
        private static void Require(bool ok,string message) { if(!ok) throw new InvalidOperationException(message); }
        private static object Field(object item,string name) { return item.GetType().GetField(name,All).GetValue(item); }

        public static void Run()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                var project=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
                var runtime=Path.Combine(project,"artifacts/build/DVSeasons");
                var assembly=Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll"));
                var repoType=assembly.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
                repository=Activator.CreateInstance(repoType,new object[]{runtime});
                repoType.GetMethod("BeginLoad",All).Invoke(repository,null);
                var type=assembly.GetType("DVSeasons.Mod.WaterIceController",true);
                controller=Activator.CreateInstance(type,new[]{repository});
                apply=(Action<float,float>)Delegate.CreateDelegate(typeof(Action<float,float>),controller,type.GetMethod("Apply",All,null,new[]{typeof(float),typeof(float)},null));
                root=new GameObject("Water ice runtime regression");
                // A large unrelated scene hierarchy precedes the lake, like DV's
                // loaded world. Discovery must not wait for all scenery nodes.
                var scenery=new GameObject("Unrelated loaded scenery");scenery.transform.SetParent(root.transform);
                for(var i=0;i<sceneryCount;i++) new GameObject("Scenery "+i).transform.SetParent(scenery.transform);
                var waterRoot=new GameObject("World water root");waterRoot.transform.SetParent(root.transform);
                mesh=CreateWaterMesh();
                var lake=new GameObject("Runtime lake");lake.transform.SetParent(waterRoot.transform);
                lake.transform.localScale=new Vector3(22,1,22);
                lakeFilter=lake.AddComponent<MeshFilter>();lakeFilter.sharedMesh=mesh;
                water=new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/WaterIceDiscoveryFixture.shader")){name="WaterLake"};
                lake.AddComponent<MeshRenderer>().sharedMaterial=water;
                var cameraGo=new GameObject("Water verification camera");cameraGo.transform.SetParent(root.transform);
                camera=cameraGo.AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;camera.orthographicSize=20;
                camera.transform.position=new Vector3(0,40,0);camera.transform.rotation=Quaternion.Euler(90,0,0);
                camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
                camera.nearClipPlane=.1f;camera.farClipPlane=100;
                target=new RenderTexture(128,128,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);target.Create();camera.targetTexture=target;
                deadline=Time.realtimeSinceStartup+90;
                EditorApplication.update+=Tick;
            }
            catch(Exception error) { Finish(error); }
        }

        public static void RunMeshReplacement()
        {
            sceneryCount=0;
            Run();
        }

        private static void Tick()
        {
            try
            {
                Require(Time.realtimeSinceStartup<deadline,"Water runtime fixture timed out.");
                if(!(bool)repository.GetType().GetProperty("IsLoadFinished",All).GetValue(repository,null)) return;
                apply(1,1);
                Require(++discoveryFrames<=8 || ((IDictionary)Field(controller,"renderers")).Count>0,
                    "World water discovery was starved by unrelated scenery for more than eight Apply frames.");
                if((IDictionary)Field(controller,"renderers") is IDictionary renderers && renderers.Count==0) return;
                if(++frames<3) return;
                Require(water.GetTexture("_MainWave")==Field(controller,"iceNormal") as Texture2D,"Discovered water did not receive bundled ice normal.");
                camera.Render();
                var read=AsyncGPUReadback.Request(target,0,TextureFormat.RGBA32);read.WaitForCompletion();
                Require(!read.hasError,"Water camera readback failed.");
                var data=read.GetData<Color32>();double sum=0,squares=0;int count=0;
                for(int y=8;y<120;y++) for(int x=8;x<120;x++) {double v=data[y*128+x].r/255d;sum+=v;squares+=v*v;count++;}
                double mean=sum/count,variance=squares/count-mean*mean;
                Debug.Log("WATER_RUNTIME_PIXELS: stage="+stage+" mean="+mean+" variance="+variance);
                Require(mean>.08 && variance>.000002,"Apply -> scene discovery -> camera produced no visible ice texture.");
                if(stage==0)
                {
                    var previous=mesh;mesh=CreateWaterMesh();lakeFilter.sharedMesh=mesh;
                    UnityEngine.Object.DestroyImmediate(previous);
                    frames=0;stage++;return;
                }
                if(stage++==1)
                {
                    apply(0,1);
                    Require(water.GetTexture("_MainWave")!=Field(controller,"iceNormal") as Texture2D,"Water normal not restored on thaw.");
                    controller.GetType().GetMethod("ResetForSession",All).Invoke(controller,null);
                    frames=0;discoveryFrames=0;return;
                }
                Debug.Log("WATER_RUNTIME_OK: asynchronous bundle loading, bounded discovery ahead of "+sceneryCount+" scenery nodes, non-readable mesh replacement, camera-rendered texture, thaw and session reset.");
                Finish(null);
            }
            catch(Exception error) { Finish(error); }
        }

        private static Mesh CreateWaterMesh()
        {
            var result=new Mesh {vertices=new[]{new Vector3(-1,0,-1),new Vector3(1,0,-1),new Vector3(1,0,1),new Vector3(-1,0,1)},
                triangles=new[]{0,2,1,0,3,2}};
            result.RecalculateBounds();result.UploadMeshData(true);return result;
        }

        private static void Finish(Exception error)
        {
            EditorApplication.update-=Tick;
            if(error!=null) Debug.LogException(error);
            if(controller is IDisposable disposable) disposable.Dispose();
            if(repository is IDisposable repo) repo.Dispose();
            if(root!=null) UnityEngine.Object.DestroyImmediate(root);
            foreach(var item in new UnityEngine.Object[]{mesh,water,target}) if(item!=null) UnityEngine.Object.DestroyImmediate(item);
            EditorApplication.Exit(error==null?0:1);
        }
    }
}
