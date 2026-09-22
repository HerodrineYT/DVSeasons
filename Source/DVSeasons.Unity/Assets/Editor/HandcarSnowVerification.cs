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
    public static class HandcarSnowVerification
    {
        [Serializable] private sealed class MeshData { public Vector3[] vertices,normals; public Vector2[] uv; public int[] indices; }
        private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static object Get(object owner,string name) {return owner.GetType().GetField(name,All).GetValue(owner);}
        private static object Call(object owner,string name,params object[] args)
        {return owner.GetType().GetMethod(name,All).Invoke(owner,args);}
        private static void Require(bool condition,string message) {if(!condition) throw new InvalidOperationException(message);}

        public static void Run()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var modPath=Path.Combine(root,"artifacts/build/DVSeasons");
            var game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            ResolveEventHandler resolver=(sender,args)=>
            {
                foreach(var dir in new[]{modPath,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {
                    var file=Path.Combine(dir,new AssemblyName(args.Name).Name+".dll");
                    if(File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;
            var code=0;
            try
            {
                Verify(Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll")),modPath,game,root);
                Debug.Log("HANDCAR_SNOW_OK: native visualHandlebar discovery, actual H1 meshes at +/-25 degrees, local reference sampling, train translation/rotation, origin shift and LOD2 without height recapture.");
            }
            catch(Exception exception) {Debug.LogException(exception);code=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(code);
        }

        private static void Verify(Assembly mod,string modPath,string game,string root)
        {
            var output=Path.Combine(root,"artifacts/verification/handcar-snow");Directory.CreateDirectory(output);
            var geometry=Path.Combine(root,"artifacts/verification/handcar-geometry");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.gray;RenderSettings.fog=false;
            QualitySettings.antiAliasing=0;
            var camera=new GameObject("H1 snow camera") {tag="MainCamera"}.AddComponent<Camera>();
            camera.renderingPath=RenderingPath.DeferredShading;camera.allowHDR=true;camera.allowMSAA=false;
            camera.farClipPlane=100;camera.nearClipPlane=.01f;camera.fieldOfView=42;
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
            camera.targetTexture=new RenderTexture(640,480,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);camera.targetTexture.Create();
            var albedo=new RenderTexture(640,480,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);albedo.Create();
            var probe=new CommandBuffer {name="H1 snow albedo verification"};probe.Blit(BuiltinRenderTextureType.GBuffer0,albedo);
            camera.AddCommandBuffer(CameraEvent.AfterLighting,probe);
            var material=new Material(Shader.Find("Standard")) {color=new Color(.07f,.07f,.07f)};material.SetFloat("_Glossiness",0);
            var car=new GameObject("H1 snow fixture");car.SetActive(false);
            var pivot=new GameObject("V handlebar").transform;pivot.SetParent(car.transform,false);
            pivot.localPosition=new Vector3(-.0000160914f,1.3893534f,0);
            var native=Assembly.LoadFrom(Path.Combine(game,"DerailValley_Data/Managed/Assembly-CSharp.dll"));
            var handcar=car.AddComponent(native.GetType("DV.Simulation.Controllers.HandcarController",true));
            handcar.GetType().GetField("visualHandlebar").SetValue(handcar,pivot);
            ((Behaviour)handcar).enabled=false;
            var detailed=LoadPart("handlebar",pivot,geometry,material);
            var distant=LoadPart("handlebar_LOD2",pivot,geometry,material);distant.enabled=false;
            var stationary=GameObject.CreatePrimitive(PrimitiveType.Cube);stationary.name="Fixed crank base";
            stationary.transform.SetParent(car.transform,false);stationary.transform.localPosition=new Vector3(0,.5f,0);
            stationary.transform.localScale=new Vector3(.2f,.2f,.2f);stationary.GetComponent<Renderer>().sharedMaterial=material;
            car.SetActive(true);

            var frameType=mod.GetType("DVSeasons.Mod.SnowMovingVehiclePartFrame",true);
            var discovered=(Transform)frameType.GetMethod("FindHandlebar",All).Invoke(null,new object[]{car.transform});
            Require(discovered==pivot,"Native H1 visualHandlebar reference was not found");
            Require(frameType.GetMethod("TryCreate",All).Invoke(null,new object[]{car.transform,pivot,stationary.GetComponent<Renderer>()})==null,"Stationary chassis was treated as animated handle");
            var direct=frameType.GetMethod("TryCreate",All).Invoke(null,new object[]{car.transform,pivot,detailed});
            Require(direct!=null,"Explicit handle frame binding failed");
            var testPoint=new Vector3(.25f,.01f,.7f);
            var reference=car.transform.InverseTransformPoint(detailed.transform.TransformPoint(testPoint));
            foreach(var angle in new[]{-25f,25f})
            {
                pivot.localRotation=Quaternion.Euler(angle,0,0);car.transform.SetPositionAndRotation(new Vector3(15000,123,-10000),Quaternion.Euler(0,63,0));
                var matrix=(Matrix4x4)frameType.GetProperty("WorldToCapturedVehicle",All).GetValue(direct,null);
                Require(Vector3.Distance(matrix.MultiplyPoint3x4(detailed.transform.TransformPoint(testPoint)),reference)<.006f,"Reference drifted at large world coordinates");
            }
            car.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);pivot.localRotation=Quaternion.identity;

            var repositoryType=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
            var repository=Activator.CreateInstance(repositoryType,new object[]{modPath});
            var bundle=AssetBundle.LoadFromFile(Path.Combine(modPath,"AssetBundles/dvseasons_dv99"));
            repositoryType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{bundle});
            var controller=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.ProceduralSnowController",true),new[]{repository});
            Call(controller,"SetVehicleDiscovery",new Func<IEnumerable<Component>>(()=>new Component[0]));
            Call(controller,"SetMovingSurfaceDiscovery",new Func<IEnumerable<Transform>>(()=>new[]{car.transform}));
            var registry=Get(controller,"vehicles");
            try
            {
                PositionCamera(camera,pivot);
                for(var i=0;i<8;i++) {Call(controller,"Apply",1f,true);camera.Render();}
                Require(((IList)Get(registry,"vehicles")).Count==1,"Fixture handcar was not registered");
                var vehicle=((IList)Get(registry,"vehicles"))[0];var parts=(IList)Get(vehicle,"Parts");
                int movingParts=0;
                foreach(var part in parts)
                {
                    var renderer=(Renderer)Get(part,"Renderer");
                    // Count the new helper independent of the field name chosen
                    // by the registry integration.
                    foreach(var field in part.GetType().GetFields(All))
                        if(field.FieldType==frameType && field.GetValue(part)!=null) movingParts++;
                }
                Require(movingParts==2,"Both H1 LOD meshes must own a captured pose; found "+movingParts);
                var captures=(int)registry.GetType().GetProperty("CaptureCount",All).GetValue(registry,null);
                var baseline=Read(albedo,Path.Combine(output,"h1-neutral.png"));
                var selected=SelectSnowPixels(baseline);Require(selected.Count>250,"Actual H1 handle snow was not visible");
                UnityEngine.Object.DestroyImmediate(baseline);
                foreach(var angle in new[]{-25f,25f})
                {
                    pivot.localRotation=Quaternion.Euler(angle,0,0);PositionCamera(camera,pivot);
                    Call(controller,"Apply",1f,true);camera.Render();
                    var image=Read(albedo,Path.Combine(output,"h1-angle-"+angle+".png"));
                    RequireSnowRemains(image,selected,"H1 angle "+angle);UnityEngine.Object.DestroyImmediate(image);
                }
                car.transform.SetPositionAndRotation(new Vector3(55,4,-43),Quaternion.Euler(0,117,0));PositionCamera(camera,pivot);
                Call(controller,"Apply",1f,true);camera.Render();
                var shifted=Read(albedo,Path.Combine(output,"h1-moving-car.png"));RequireSnowRemains(shifted,selected,"moving and rotating H1");UnityEngine.Object.DestroyImmediate(shifted);
                detailed.enabled=false;distant.enabled=true;camera.Render();
                var lod=Read(albedo,Path.Combine(output,"h1-lod2.png"));RequireSnowRemains(lod,selected,"H1 LOD2",.68f);UnityEngine.Object.DestroyImmediate(lod);
                Require((int)registry.GetType().GetProperty("CaptureCount",All).GetValue(registry,null)==captures,"Handle pumping recaptured the car height map");
                Debug.Log("H1 snow baseline pixels="+selected.Count+"; unchanged height captures="+captures);
            }
            finally
            {
                ((IDisposable)controller).Dispose();((IDisposable)repository).Dispose();
                camera.RemoveCommandBuffer(CameraEvent.AfterLighting,probe);probe.Dispose();
                var target=camera.targetTexture;camera.targetTexture=null;
                UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(albedo);
                UnityEngine.Object.DestroyImmediate(camera.gameObject);
                UnityEngine.Object.DestroyImmediate(detailed.GetComponent<MeshFilter>().sharedMesh);UnityEngine.Object.DestroyImmediate(distant.GetComponent<MeshFilter>().sharedMesh);
                UnityEngine.Object.DestroyImmediate(car);UnityEngine.Object.DestroyImmediate(material);
            }
        }

        private static MeshRenderer LoadPart(string name,Transform parent,string directory,Material material)
        {
            var data=JsonUtility.FromJson<MeshData>(File.ReadAllText(Path.Combine(directory,name+".json")));
            var mesh=new Mesh {name=name,vertices=data.vertices,normals=data.normals,uv=data.uv,triangles=data.indices};mesh.RecalculateBounds();
            var go=new GameObject(name);go.transform.SetParent(parent,false);go.AddComponent<MeshFilter>().sharedMesh=mesh;
            var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=material;return renderer;
        }
        private static void PositionCamera(Camera camera,Transform pivot)
        {camera.transform.position=pivot.TransformPoint(new Vector3(0,3.1f,0));camera.transform.LookAt(pivot.position,pivot.forward);}
        private static Texture2D Read(RenderTexture target,string path)
        {
            var previous=RenderTexture.active;RenderTexture.active=target;
            var result=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true);
            result.ReadPixels(new Rect(0,0,target.width,target.height),0,0);result.Apply();RenderTexture.active=previous;
            var png=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);var colors=result.GetPixels();
            for(var i=0;i<colors.Length;i++) colors[i]=colors[i].gamma;
            png.SetPixels(colors);png.Apply();File.WriteAllBytes(path,png.EncodeToPNG());UnityEngine.Object.DestroyImmediate(png);return result;
        }
        private static List<int> SelectSnowPixels(Texture2D image)
        {
            var colors=image.GetPixels();var result=new List<int>();
            for(int y=2;y<image.height-2;y++) for(int x=2;x<image.width-2;x++)
            {
                int i=y*image.width+x;bool snow=true;
                for(int yy=-1;yy<=1;yy++) for(int xx=-1;xx<=1;xx++) if(colors[i+yy*image.width+xx].r<.6f) snow=false;
                if(snow) result.Add(i);
            }
            return result;
        }
        private static void RequireSnowRemains(Texture2D image,List<int> pixels,string label,float threshold=.82f)
        {
            var colors=image.GetPixels();int retained=0;foreach(var pixel in pixels) if(colors[pixel].r>.42f) retained++;
            float fraction=(float)retained/pixels.Count;Debug.Log(label+" retained="+fraction);
            Require(fraction>=threshold,"Snow detached from "+label+": only "+fraction+" of stable surface pixels retain snow");
        }
    }
}
