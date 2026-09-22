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
    public static class SnowObjectLimitVerification
    {
        static bool fallback;
        public static void RunFallback(){fallback=true;Run();}
        private const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static object Get(object owner,string name) {return owner.GetType().GetField(name,All).GetValue(owner);}
        private static void Set(object owner,string name,object value) {owner.GetType().GetField(name,All).SetValue(owner,value);}
        private static object Call(object owner,string name,params object[] args) {return owner.GetType().GetMethod(name,All).Invoke(owner,args);}
        private static int Count(object owner,string name) {return (int)owner.GetType().GetProperty(name,All).GetValue(owner,null);}
        private static void Require(bool pass,string message) {if(!pass) throw new InvalidOperationException(message);}
        private static bool Selected(object limiter,Renderer renderer)
        {return (bool)limiter.GetType().GetMethod("IsSelected",All,null,new[]{typeof(Renderer)},null).Invoke(limiter,new object[]{renderer});}

        public static void Run()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var runtime=Path.Combine(root,"artifacts/build/DVSeasons");
            var game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            ResolveEventHandler resolver=(sender,args)=>
            {
                foreach(var dir in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {
                    var file=Path.Combine(dir,new AssemblyName(args.Name).Name+".dll");
                    if(File.Exists(file)) return Assembly.LoadFrom(file);
                }
                return null;
            };
            AppDomain.CurrentDomain.AssemblyResolve+=resolver;int exit=0;bool targetError=false;
            Application.LogCallback errors=(condition,trace,type)=>
            {if(condition.IndexOf("Dimensions of color surface",StringComparison.OrdinalIgnoreCase)>=0) targetError=true;};
            Application.logMessageReceived+=errors;
            try
            {
                Verify(Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll")),runtime);
                Require(!targetError,"Object limit introduced mismatched color/depth render targets");
                Debug.Log("SNOW_OBJECT_LIMIT_OK: rolling-stock-only nearest budget; static buildings, LOD scenery and turntables retain snow; capped cars remain bare; camera relocation, limit changes and floating origin preserve vehicle masks and slots.");
            }
            catch(Exception exception) {Debug.LogException(exception);exit=1;}
            finally {Application.logMessageReceived-=errors;AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(exit);
        }

        private static void Verify(Assembly mod,string runtime)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.gray;RenderSettings.fog=false;
            QualitySettings.antiAliasing=0;
            var camera=new GameObject("Snow object budget camera") {tag="MainCamera"}.AddComponent<Camera>();
            camera.renderingPath=RenderingPath.DeferredShading;camera.allowHDR=true;camera.allowMSAA=false;
            camera.orthographic=false;camera.fieldOfView=110;camera.nearClipPlane=.1f;camera.farClipPlane=1500;
            camera.transform.position=new Vector3(-70,100,0);camera.transform.rotation=Quaternion.Euler(90,0,0);
            camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Color.black;
            camera.targetTexture=new RenderTexture(640,480,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
            camera.targetTexture.Create();
            var albedo=new RenderTexture(640,480,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);albedo.Create();
            var probe=new CommandBuffer {name="Snow object limit albedo"};probe.Blit(BuiltinRenderTextureType.GBuffer0,albedo);
            camera.AddCommandBuffer(CameraEvent.AfterLighting,probe);
            var dark=new Material(Shader.Find("Standard")) {color=new Color(.05f,.05f,.05f)};dark.SetFloat("_Glossiness",0);
            var roots=new List<GameObject>();
            var near=Cube("Budget station roof",new Vector3(-70,3,0),new Vector3(20,.4f,20),dark,roots);
            var lodRoot=new GameObject("Budget building LOD group");roots.Add(lodRoot);lodRoot.transform.position=new Vector3(-30,0,0);
            var lodNear=Cube("Budget LOD0",new Vector3(-30,3,0),new Vector3(15,.4f,20),dark,roots);
            var lodFar=Cube("Budget LOD1",new Vector3(-30,3,0),new Vector3(15,.4f,20),dark,roots);
            lodNear.transform.SetParent(lodRoot.transform,true);lodFar.transform.SetParent(lodRoot.transform,true);
            var lod=lodRoot.AddComponent<LODGroup>();
            lod.SetLODs(new[]{new LOD(.8f,new Renderer[]{lodNear}),new LOD(.001f,new Renderer[]{lodFar})});lod.RecalculateBounds();
            var train=new GameObject("Budget dynamic train");roots.Add(train);train.transform.position=new Vector3(70,0,0);
            var roof=Cube("Budget vehicle roof",new Vector3(70,3,0),new Vector3(12,.4f,30),dark,roots);
            roof.transform.SetParent(train.transform,true);
            var secondTrain=new GameObject("Budget second rolling stock");roots.Add(secondTrain);secondTrain.transform.position=new Vector3(-70,0,-40);
            var secondRoof=Cube("Budget second vehicle roof",new Vector3(-70,3,-40),new Vector3(12,.4f,30),dark,roots);
            secondRoof.transform.SetParent(secondTrain.transform,true);
            var turntable=new GameObject("Budget turntable bridge");roots.Add(turntable);turntable.transform.position=new Vector3(20,0,35);
            var bridge=Cube("Budget turntable surface",new Vector3(20,3,35),new Vector3(12,.4f,30),dark,roots);
            bridge.transform.SetParent(turntable.transform,true);
            var animal=Cube("Cow_rigged",new Vector3(-70,3,35),new Vector3(5,5,5),dark,roots);
            var fleet=new List<Transform> {train.transform,secondTrain.transform,turntable.transform};
            var repositoryType=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
            var repository=Activator.CreateInstance(repositoryType,new object[]{runtime});
            var bundle=AssetBundle.LoadFromFile(Path.Combine(runtime,"AssetBundles/dvseasons_dv99"));
            repositoryType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{bundle});
            var controller=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.ProceduralSnowController",true),new[]{repository});
            Call(controller,"SetVehicleDiscovery",new Func<IEnumerable<Component>>(()=>new Component[0]));
            Call(controller,"SetMovingSurfaceDiscovery",new Func<IEnumerable<Transform>>(()=>fleet));
            var registry=Get(controller,"vehicles");var limiter=Get(registry,"ObjectLimiter");Texture2D mask=null;
            if(fallback)
            {
                Set(registry,"NativeMaterialsEnabled",false);Set(registry,"DistantSurfacesEnabled",false);
                Set(registry,"ExclusionVolumesEnabled",false);Set(registry,"CombinedMeshesEnabled",false);
            }
            try
            {
                Call(controller,"LoadResources");
                for(int i=0;i<30;i++) {Call(controller,"Apply",1f,true);camera.Render();}
                var vehicle=Find(registry,train.transform);
                var secondVehicle=Find(registry,secondTrain.transform);
                var bridgeVehicle=Find(registry,turntable.transform);
                Set(vehicle,"RollingStock",true);Set(secondVehicle,"RollingStock",true);
                Require(!(bool)Get(bridgeVehicle,"RollingStock"),"Moving turntable was incorrectly classified as rolling stock");
                Require((bool)Get(vehicle,"Ready") && (bool)Get(vehicle,"SnowReady"),"Fixture vehicle did not prepare its snow");
                int slot=(int)Get(vehicle,"Slot"),captures=Count(registry,"CaptureCount");
                mask=new Texture2D(256,256,TextureFormat.RHalf,false,true);var values=new Color[256*256];
                for(int y=0;y<256;y++) for(int x=0;x<256;x++) values[y*256+x]=new Color(y<128?0:1,0,0,1);
                mask.SetPixels(values);mask.Apply(false,false);
                Graphics.CopyTexture(mask,0,0,(RenderTexture)Get(registry,"snow"),slot,0);Call(controller,"SetWeather",0f);
                byte[] original=ReadSlice((RenderTexture)Get(registry,"snow"),slot);

                Call(controller,"SetObjectLimit",1);
                Call(controller,"Apply",1f,true);camera.Render();
                Require(Count(limiter,"SelectedCount")==1 && Selected(limiter,secondRoof),"One-car limit did not select the nearest rolling stock");
                Require(!Selected(limiter,roof),"One-car budget selected both vehicles");
                Require(Selected(limiter,near) && Selected(limiter,lodNear) && Selected(limiter,bridge),"Rolling stock limit affected environment or moving turntable");
                AssertSample(albedo,camera,near.transform.position+Vector3.up*.2f,true,"unaffected static building");
                AssertSample(albedo,camera,lodNear.transform.position+Vector3.up*.2f,true,"unaffected LOD building");
                AssertSample(albedo,camera,bridge.transform.position+Vector3.up*.2f,true,"unaffected turntable");
                AssertSample(albedo,camera,secondRoof.transform.position+new Vector3(0,.2f,8),true,"nearest selected vehicle");
                AssertSample(albedo,camera,roof.transform.position+new Vector3(0,.2f,8),false,"unselected vehicle");
                AssertSample(albedo,camera,animal.transform.position+Vector3.up*2.5f,false,"animal remains excluded");
                int limitedSnowDraws=Count(registry,"FrameSnowDrawCount"),limitedExclusionDraws=Count(registry,"FrameExclusionDrawCount");
                bool native=Count(registry,"NativeMaterialSlots")>0;
                Require(native?Count(registry,"FrameDrawCount")==0:limitedExclusionDraws>0,"Capped rolling stock did not avoid detailed snow draws");

                Call(controller,"SetObjectLimit",2);Call(controller,"Apply",1f,true);camera.Render();
                Require(Count(limiter,"SelectedCount")==2 && Selected(limiter,roof) && Selected(limiter,secondRoof),"Two-car budget did not restore both vehicles");
                AssertSample(albedo,camera,roof.transform.position+new Vector3(0,.2f,8),true,"restored vehicle snow");
                AssertSample(albedo,camera,roof.transform.position+new Vector3(0,.2f,-8),false,"retained bare half of vehicle");
                int allSnowDraws=Count(registry,"FrameSnowDrawCount"),allExclusionDraws=Count(registry,"FrameExclusionDrawCount");
                Require(native?allSnowDraws+allExclusionDraws==0:allSnowDraws>limitedSnowDraws && allExclusionDraws<limitedExclusionDraws,
                    "Lowering the car limit did not replace full snow draws with cheap exclusions");
                Require(allSnowDraws+allExclusionDraws==Count(registry,"FrameDrawCount"),"Frame draw diagnostics do not account for all vehicle draws");
                Debug.Log("SNOW_LIMIT_DRAW_WORK: two cars="+allSnowDraws+" full/"+allExclusionDraws+" cheap; one car="+limitedSnowDraws+" full/"+limitedExclusionDraws+" cheap. Exact silhouette draws remain required; this is not an FPS measurement.");
                // The fallback suite exercises custom texture ownership. The
                // native suite above checks that both limits need zero passes.
                if(!native)
                {
                Set(registry,"HasNativeSnowTexture",new Func<Renderer,int,bool>((renderer,submesh)=>renderer==roof));
                Call(controller,"Apply",1f,true);camera.Render();
                Require(Count(registry,"FrameExclusionDrawCount")>allExclusionDraws,"Native snow texture did not use the cheap exclusion pass");
                AssertSample(albedo,camera,roof.transform.position+new Vector3(0,.2f,8),false,"native texture retains ownership of its snow");
                Set(registry,"HasNativeSnowTexture",null);
                }

                Call(controller,"SetObjectLimit",1);camera.transform.position=new Vector3(70,100,0);
                Call(controller,"Apply",1f,true);camera.Render();
                Require(Selected(limiter,roof) && !Selected(limiter,secondRoof),"Camera relocation did not immediately select the nearest vehicle");
                Require(Selected(limiter,near) && Selected(limiter,bridge),"Camera relocation removed snow allowance from static world or turntable");
                AssertSample(albedo,camera,roof.transform.position+new Vector3(0,.2f,8),true,"vehicle after camera relocation");
                AssertSample(albedo,camera,secondRoof.transform.position+new Vector3(0,.2f,8),false,"capped second vehicle after relocation");
                AssertSample(albedo,camera,bridge.transform.position+Vector3.up*.2f,true,"turntable after camera relocation");
                Require(((IList)Get(registry,"vehicles")).Count==3 && (int)Get(vehicle,"Slot")==slot,"Rendering limit evicted or reassigned the vehicle cache");
                Require(Count(registry,"CaptureCount")==captures,"Limit or camera change recaptured the unchanged vehicle");
                Require(Equal(original,ReadSlice((RenderTexture)Get(registry,"snow"),slot)),"Limit changes destroyed the vehicle's accumulated mask");

                var shift=new Vector3(600,0,-800);
                foreach(var rootObject in roots)
                    if(rootObject!=null && rootObject.transform.parent==null) rootObject.transform.position+=shift;
                camera.transform.position+=shift;Call(controller,"SetWorldOffset",shift);
                Call(controller,"Apply",1f,true);camera.Render();
                Require(Selected(limiter,roof),"Floating origin shift changed the nearest object");
                AssertSample(albedo,camera,roof.transform.position+new Vector3(0,.2f,8),true,"vehicle after floating origin shift");

                Call(controller,"SetObjectLimit",0);Call(controller,"Apply",1f,true);camera.Render();
                Require(Selected(limiter,near) && Selected(limiter,lodNear) && Selected(limiter,roof) && Selected(limiter,secondRoof),"Unlimited mode did not restore all objects");
                AssertSample(albedo,camera,near.transform.position+Vector3.up*.2f,true,"unlimited static building");
                AssertSample(albedo,camera,secondRoof.transform.position+new Vector3(0,.2f,8),true,"unlimited second vehicle");
                Require(Equal(original,ReadSlice((RenderTexture)Get(registry,"snow"),slot)),"Unlimited mode changed the asymmetric persistent snow mask");

                Call(controller,"SetVehicleSnowEnabled",false);Call(controller,"Apply",1f,true);camera.Render();
                AssertSample(albedo,camera,roof.transform.position+new Vector3(0,.2f,8),false,"disabled vehicle roof");
                AssertSample(albedo,camera,secondRoof.transform.position+new Vector3(0,.2f,8),false,"disabled second vehicle");
                AssertSample(albedo,camera,near.transform.position+Vector3.up*.2f,true,"building with vehicle snow disabled");
                AssertSample(albedo,camera,bridge.transform.position+Vector3.up*.2f,true,"turntable with vehicle snow disabled");
                AssertSample(albedo,camera,animal.transform.position+Vector3.up*2.5f,false,"animal with vehicle snow disabled");
                Require(!(bool)Get(vehicle,"FrameSnow") && !(bool)Get(secondVehicle,"FrameSnow"),"Disabled rolling stock still emits full snow surfaces");
                Require(Equal(original,ReadSlice((RenderTexture)Get(registry,"snow"),slot)),"Turning vehicle snow off cleared its persistent mask");
                Call(controller,"SetVehicleSnowEnabled",true);Call(controller,"Apply",1f,true);camera.Render();
                AssertSample(albedo,camera,roof.transform.position+new Vector3(0,.2f,8),true,"re-enabled vehicle snow");
                AssertSample(albedo,camera,roof.transform.position+new Vector3(0,.2f,-8),false,"re-enabled vehicle keeps cleared half");
                Require(Equal(original,ReadSlice((RenderTexture)Get(registry,"snow"),slot)),"Re-enabling vehicle snow reset its history");
                Debug.Log("SNOW_VEHICLE_TOGGLE_OK: native="+native+"; cars bare, building/turntable snow retained, animal exclusion retained, asymmetric mask restored.");

                Call(controller,"SetObjectLimit",1);
                var coldRoots=new List<Transform>();var coldRoofs=new List<Renderer>();
                for(int i=0;i<2;i++)
                {
                    var cold=new GameObject("New capped rolling stock "+i);roots.Add(cold);
                    cold.transform.position=new Vector3(160+i*20,0,0)+shift;
                    var coldRoof=Cube("New capped roof "+i,cold.transform.position+Vector3.up*3,new Vector3(12,.4f,20),dark,roots);
                    coldRoof.transform.SetParent(cold.transform,true);fleet.Add(cold.transform);coldRoots.Add(cold.transform);coldRoofs.Add(coldRoof);
                    Call(registry,"Register",cold.transform,null,null);Set(Find(registry,cold.transform),"RollingStock",true);
                }
                for(int frame=0;frame<3;frame++)
                {
                    Call(controller,"Apply",1f,true);camera.Render();
                    foreach(var coldRoof in coldRoofs)
                    {
                        Require(!Selected(limiter,coldRoof),"New distant car bypassed the rolling stock limit");
                        AssertSample(albedo,camera,coldRoof.transform.position+Vector3.up*.2f,false,"fresh capped rolling stock frame "+frame);
                    }
                }
                foreach(var cold in coldRoots)
                {
                    var entry=Find(registry,cold);
                    Require(((IList)Get(entry,"Parts")).Count>0,"New capped car was never prepared for snow exclusion");
                    Require(!(bool)Get(entry,"Ready") && !(bool)Get(entry,"SnowReady"),"A capped car unnecessarily captured a snow height/mask");
                }
                Require(Count(registry,"CaptureCount")==captures,"New capped cars incurred height captures");
                AssertSample(albedo,camera,bridge.transform.position+Vector3.up*.2f,true,"turntable with newly streamed capped cars");
            }
            finally
            {
                ((IDisposable)controller).Dispose();((IDisposable)repository).Dispose();
                camera.RemoveCommandBuffer(CameraEvent.AfterLighting,probe);probe.Dispose();
                var target=camera.targetTexture;camera.targetTexture=null;
                UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(albedo);
                UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(dark);
                if(mask!=null) UnityEngine.Object.DestroyImmediate(mask);
                foreach(var rootObject in roots) if(rootObject!=null) UnityEngine.Object.DestroyImmediate(rootObject);
            }
        }
        private static Renderer Cube(string name,Vector3 position,Vector3 size,Material material,List<GameObject> roots)
        {
            var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);cube.name=name;cube.transform.position=position;cube.transform.localScale=size;
            var renderer=cube.GetComponent<Renderer>();renderer.sharedMaterial=material;roots.Add(cube);return renderer;
        }
        private static object Find(object registry,Transform root)
        {
            foreach(var vehicle in (IList)Get(registry,"vehicles")) if((Transform)Get(vehicle,"Root")==root) return vehicle;
            throw new InvalidOperationException("Missing registered root "+root.name);
        }
        private static void AssertSample(RenderTexture target,Camera camera,Vector3 world,bool snow,string label)
        {
            var uv=camera.WorldToViewportPoint(world);Require(uv.x>0 && uv.x<1 && uv.y>0 && uv.y<1,"Probe outside camera: "+label);
            var previous=RenderTexture.active;RenderTexture.active=target;
            var image=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true);
            image.ReadPixels(new Rect(0,0,target.width,target.height),0,0);image.Apply(false,false);RenderTexture.active=previous;
            float value=image.GetPixel(Mathf.RoundToInt(uv.x*(image.width-1)),Mathf.RoundToInt(uv.y*(image.height-1))).r;
            UnityEngine.Object.DestroyImmediate(image);
            Require(snow?value>.5f:value<.2f,label+" albedo="+value+", expected "+(snow?"snow":"bare surface"));
        }
        private static byte[] ReadSlice(RenderTexture texture,int slot)
        {
            var request=AsyncGPUReadback.Request(texture,0,0,256,0,256,slot,1);request.WaitForCompletion();
            Require(!request.hasError,"GPU mask readback failed");var data=request.GetData<byte>();var result=new byte[data.Length];data.CopyTo(result);return result;
        }
        private static bool Equal(byte[] left,byte[] right)
        {if(left.Length!=right.Length) return false;for(int i=0;i<left.Length;i++) if(left[i]!=right[i]) return false;return true;}
    }
}
