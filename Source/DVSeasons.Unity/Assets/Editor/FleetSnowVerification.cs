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
    public static class FleetSnowVerification
    {
        private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static object Get(object owner,string name) {return owner.GetType().GetField(name,All).GetValue(owner);}
        private static void Set(object owner,string name,object value) {owner.GetType().GetField(name,All).SetValue(owner,value);}
        private static object Call(object owner,string name,params object[] args) {return owner.GetType().GetMethod(name,All).Invoke(owner,args);}
        private static int Count(object owner,string name) {return (int)owner.GetType().GetProperty(name,All).GetValue(owner,null);}
        private static void Require(bool condition,string message) {if(!condition) throw new InvalidOperationException(message);}
        private static bool useSourceShaders;

        public static void RunSourceShaders()
        { useSourceShaders=true; Run(); }

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
                Verify(Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll")),modPath,Path.Combine(root,"artifacts/verification/fleet-snow"));
                Debug.Log("FLEET_SNOW_OK: 80 live vehicle roots; incremental height preparation; 32-to-128 GPU array growth preserves asymmetric snow and height; cars 0/39/79 snowy at 100/400 m; camera moves preserve slots, height captures and masks.");
            }
            catch(Exception exception) {Debug.LogException(exception);code=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(code);
        }

        private static void Verify(Assembly mod,string modPath,string output)
        {
            Directory.CreateDirectory(output);EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.gray;RenderSettings.fog=false;
            QualitySettings.antiAliasing=0;
            var camera=new GameObject("Fleet snow camera") {tag="MainCamera"}.AddComponent<Camera>();
            camera.renderingPath=RenderingPath.DeferredShading;camera.allowHDR=true;camera.allowMSAA=false;
            camera.farClipPlane=1600;camera.nearClipPlane=.1f;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
            camera.targetTexture=new RenderTexture(640,480,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);camera.targetTexture.Create();
            var albedo=new RenderTexture(640,480,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);albedo.Create();
            var probe=new CommandBuffer {name="Fleet snow albedo verification"};probe.Blit(BuiltinRenderTextureType.GBuffer0,albedo);
            camera.AddCommandBuffer(CameraEvent.AfterLighting,probe);
            var material=new Material(Shader.Find("Standard")) {color=new Color(.07f,.07f,.07f)};material.SetFloat("_Glossiness",0);
            var fleet=new List<Transform>();var allRoots=new List<GameObject>();
            for(var i=0;i<20;i++) AddCar(i,fleet,allRoots,material);
            var repositoryType=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
            var repository=Activator.CreateInstance(repositoryType,new object[]{modPath});
            var bundle=AssetBundle.LoadFromFile(Path.Combine(modPath,"AssetBundles/dvseasons_dv99"));
            repositoryType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{bundle});
            var controller=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.ProceduralSnowController",true),new[]{repository});
            // The discovery boundary accepts moving roots; this exercises the
            // identical vehicle slice/render path without waking 80 native car
            // simulations in an empty editor scene.
            Call(controller,"SetVehicleDiscovery",new Func<IEnumerable<Component>>(()=>new Component[0]));
            Call(controller,"SetMovingSurfaceDiscovery",new Func<IEnumerable<Transform>>(()=>fleet));
            var registry=Get(controller,"vehicles");Texture2D mask=null;
            try
            {
                Position(camera,fleet[0],100);
                if(useSourceShaders)
                {
                    Call(controller,"LoadResources");
                    ((Material)Get(registry,"material")).shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/SnowVehicle.shader");
                    ((Material)Get(controller,"material")).shader=AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/ProceduralSnow.shader");
                }
                for(var i=0;i<50;i++) {Call(controller,"Apply",1f,true);camera.Render();}
                Require(((IList)Get(registry,"vehicles")).Count==20,"Initial fleet did not register exactly 20 roots");
                Require(Count(registry,"CaptureCount")==20,"Initial cars were recaptured instead of prepared once");
                var first=Find(registry,fleet[0]);int slot=(int)Get(first,"Slot");
                var snow=(RenderTexture)Get(registry,"snow");
                Require(snow.volumeDepth==32,"Initial GPU snow capacity should be 32, was "+snow.volumeDepth);
                Require((bool)Get(first,"SnowReady"),"Initial car snow was not accumulated");
                mask=new Texture2D(256,256,TextureFormat.RHalf,false,true);
                var pixels=new Color[256*256];
                for(var y=0;y<256;y++) for(var x=0;x<256;x++) pixels[y*256+x]=new Color(y<128?0:1,0,0,1);
                mask.SetPixels(pixels);mask.Apply(false,false);Graphics.CopyTexture(mask,0,0,snow,slot,0);
                var oldSnow=ReadSlice(snow,slot);var oldHeight=ReadSlice((RenderTexture)Get(registry,"heights"),slot);
                Call(controller,"SetWeather",0f);camera.Render();
                var image=Read(albedo,Path.Combine(output,"fleet-before-growth.png"));
                AssertRoof(image,camera,fleet[0],true,"before array growth");UnityEngine.Object.DestroyImmediate(image);

                for(var i=20;i<80;i++) AddCar(i,fleet,allRoots,material);
                Set(registry,"nextScan",0f);var before=Count(registry,"CaptureCount");Call(controller,"Apply",1f,true);
                Require(((IList)Get(registry,"vehicles")).Count==80,"Cars beyond the original 32-car/300 m cache were dropped");
                Require(Count(registry,"CaptureCount")-before<=1,"Adding 60 cars performed an unbounded height capture burst");
                var grownSnow=(RenderTexture)Get(registry,"snow");var grownHeight=(RenderTexture)Get(registry,"heights");
                Require(grownSnow.volumeDepth==128 && grownHeight.volumeDepth==128,"Arrays did not grow to the next power of two for 80 cars");
                Require((int)Get(Find(registry,fleet[0]),"Slot")==slot,"GPU growth changed an existing car slot");
                Require(Equal(oldSnow,ReadSlice(grownSnow,slot)),"Growing the snow array lost an existing asymmetric mask");
                Require(Equal(oldHeight,ReadSlice(grownHeight,slot)),"Growing the height array lost the previously captured car surface");
                for(var i=0;i<175;i++)
                {
                    before=Count(registry,"CaptureCount");Call(controller,"Apply",1f,true);
                    Require(Count(registry,"CaptureCount")-before<=1,"Fleet preparation exceeded one height capture per frame");
                    if(i%12==0) camera.Render();
                }
                Require(Count(registry,"CaptureCount")==80,"Every loaded car should have exactly one height capture");
                foreach(var car in fleet) Require((bool)Get(Find(registry,car),"SnowReady"),"Loaded car has no initialized snow: "+car.name);
                Require(Equal(oldSnow,ReadSlice((RenderTexture)Get(registry,"snow"),slot)),"Preparing the new fleet changed a dry, already accumulated mask");

                int captures=Count(registry,"CaptureCount");var slots=new Dictionary<int,int>();
                foreach(var car in fleet) slots[car.GetInstanceID()]=(int)Get(Find(registry,car),"Slot");
                foreach(var index in new[]{0,39,79}) foreach(var distance in new[]{100f,400f})
                {
                    Position(camera,fleet[index],distance);Call(controller,"Apply",1f,true);camera.Render();
                    image=Read(albedo,Path.Combine(output,"fleet-car-"+index+"-"+distance+"m.png"));
                    AssertRoof(image,camera,fleet[index],index==0,"car "+index+" at "+distance+" m");UnityEngine.Object.DestroyImmediate(image);
                    Require(((IList)Get(registry,"vehicles")).Count==80,"Camera movement evicted loaded fleet members");
                    Require(Count(registry,"CaptureCount")==captures,"Camera movement forced a new height capture");
                    foreach(var car in fleet) Require((int)Get(Find(registry,car),"Slot")==slots[car.GetInstanceID()],"Camera movement reassigned a live car slot");
                }
                Require(Equal(oldSnow,ReadSlice((RenderTexture)Get(registry,"snow"),slot)),"Camera relocation changed persistent accumulated snow");
                Require(Equal(oldHeight,ReadSlice((RenderTexture)Get(registry,"heights"),slot)),"Camera relocation changed the height slice");
                Debug.Log("Fleet texture capacity="+grownSnow.volumeDepth+"; live cars="+fleet.Count+"; height captures="+captures);
            }
            finally
            {
                ((IDisposable)controller).Dispose();((IDisposable)repository).Dispose();
                camera.RemoveCommandBuffer(CameraEvent.AfterLighting,probe);probe.Dispose();
                var target=camera.targetTexture;camera.targetTexture=null;
                UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(albedo);
                UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(material);
                if(mask!=null) UnityEngine.Object.DestroyImmediate(mask);
                foreach(var go in allRoots) if(go!=null) UnityEngine.Object.DestroyImmediate(go);
            }
        }

        private static void AddCar(int index,List<Transform> fleet,List<GameObject> objects,Material material)
        {
            var root=new GameObject("Fleet car "+index);root.transform.position=new Vector3((index%10)*25,0,(index/10)*25);
            var roof=GameObject.CreatePrimitive(PrimitiveType.Cube);roof.name="Roof";roof.transform.SetParent(root.transform,false);
            roof.transform.localPosition=new Vector3(0,3,0);roof.transform.localScale=new Vector3(3,.4f,9);
            roof.GetComponent<Renderer>().sharedMaterial=material;fleet.Add(root.transform);objects.Add(root);
        }
        private static object Find(object registry,Transform root)
        {
            foreach(var vehicle in (IList)Get(registry,"vehicles")) if((Transform)Get(vehicle,"Root")==root) return vehicle;
            throw new InvalidOperationException("Missing live vehicle "+root.name);
        }
        private static void Position(Camera camera,Transform car,float distance)
        {
            var roof=car.position+Vector3.up*3.2f;camera.transform.position=roof+Vector3.up*distance;
            camera.transform.LookAt(roof,Vector3.forward);camera.fieldOfView=2*Mathf.Atan(7f/distance)*Mathf.Rad2Deg;
        }
        private static void AssertRoof(Texture2D image,Camera camera,Transform car,bool asymmetric,string label)
        {
            var bare=Sample(image,camera,car.position+new Vector3(0,3.2f,-2.5f));
            var covered=Sample(image,camera,car.position+new Vector3(0,3.2f,2.5f));
            Require(covered>.5f,"Snow disappeared on "+label+": snow="+covered);
            if(asymmetric) Require(bare<.2f && covered-bare>.3f,"Asymmetric mask was lost on "+label+": "+bare+" / "+covered);
            else Require(bare>.5f,"Partial roof disappeared on "+label+": "+bare+" / "+covered);
            Debug.Log(label+" roof="+bare+", "+covered);
        }
        private static float Sample(Texture2D image,Camera camera,Vector3 point)
        {var uv=camera.WorldToViewportPoint(point);return image.GetPixel(Mathf.RoundToInt(uv.x*(image.width-1)),Mathf.RoundToInt(uv.y*(image.height-1))).r;}
        private static Texture2D Read(RenderTexture target,string path)
        {
            var previous=RenderTexture.active;RenderTexture.active=target;
            var result=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true);
            result.ReadPixels(new Rect(0,0,target.width,target.height),0,0);result.Apply();RenderTexture.active=previous;
            var png=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);var colors=result.GetPixels();
            for(var i=0;i<colors.Length;i++) colors[i]=colors[i].gamma;
            png.SetPixels(colors);png.Apply();File.WriteAllBytes(path,png.EncodeToPNG());UnityEngine.Object.DestroyImmediate(png);return result;
        }
        private static byte[] ReadSlice(RenderTexture texture,int slot)
        {
            var request=AsyncGPUReadback.Request(texture,0,0,256,0,256,slot,1);request.WaitForCompletion();
            Require(!request.hasError,"Fleet GPU slice readback failed");var data=request.GetData<byte>();var result=new byte[data.Length];data.CopyTo(result);return result;
        }
        private static bool Equal(byte[] expected,byte[] actual)
        {if(expected.Length!=actual.Length) return false;for(var i=0;i<expected.Length;i++) if(expected[i]!=actual[i]) return false;return true;}
    }
}
