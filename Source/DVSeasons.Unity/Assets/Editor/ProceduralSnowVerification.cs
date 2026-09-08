using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class ProceduralSnowVerification
    {
        private static object controller;
        private static MethodInfo apply;
        private static Camera camera;
        private static string directory;

        public static void Verify()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            directory = Path.Combine(root,"artifacts/verification/0.2.26");
            Directory.CreateDirectory(directory);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            QualitySettings.antiAliasing = 0;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.12f,0.12f,0.12f);
            RenderSettings.fog = false;
            var cameraObject = new GameObject("Snow verification camera") { tag = "MainCamera" };
            camera = cameraObject.AddComponent<Camera>(); camera.enabled=true;
            camera.renderingPath=RenderingPath.DeferredShading;
            camera.allowHDR=true; camera.allowMSAA=false;
            camera.clearFlags=CameraClearFlags.SolidColor; camera.backgroundColor=Color.black;
            camera.nearClipPlane=0.1f; camera.farClipPlane=100;
            camera.orthographic=false; camera.fieldOfView=60;
            camera.transform.position=new Vector3(0,7,-16);
            camera.transform.LookAt(new Vector3(0,1,1));
            var target=new RenderTexture(512,384,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
            target.Create(); camera.targetTexture=target;
            var material = new Material(Shader.Find("Standard"));
            material.color=new Color(0.16f,0.12f,0.09f);
            material.SetFloat("_Glossiness",0.15f);
            material.enableInstancing=true;
            Box("Ground",new Vector3(0,-0.25f,0),new Vector3(20,0.5f,20),material);
            Box("Roof",new Vector3(-3,3,2),new Vector3(4,0.25f,4),material);
            Box("Wall",new Vector3(5,1.5f,2),new Vector3(2,3,0.3f),material);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type=LightType.Directional; sun.intensity=1; sun.transform.rotation=Quaternion.Euler(55,-30,0);
            sun.shadows=LightShadows.Soft;
            var modPath=Path.Combine(root,"artifacts/build/DVSeasons");
            var assembly=Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll"));
            var repositoryType=assembly.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
            var repository=Activator.CreateInstance(repositoryType,new object[]{modPath});
            var type=assembly.GetType("DVSeasons.Mod.ProceduralSnowController",true);
            controller=Activator.CreateInstance(type,new[]{repository});
            apply=type.GetMethod("Apply");
            type.GetMethod("SetWeather").Invoke(controller,new object[]{1f});
            Debug.Log("Snow verification rendering path: " + camera.actualRenderingPath);
            try
            {
                var baseline=Capture(0,"snow-0");
                var middle=Capture(0.5f,"snow-50");
                var winter=Capture(1,"snow-100");
                var thaw=Capture(0,"snow-thaw");
                var floor=new Vector3(3,0,0);
                var sheltered=new Vector3(-3,0,1);
                var roof=new Vector3(-3,3.125f,2);
                var wall=new Vector3(5,1.5f,1.849f);
                var floorGain=Sample(winter,floor)-Sample(baseline,floor);
                var roofGain=Sample(winter,roof)-Sample(baseline,roof);
                var shelteredChange=Mathf.Abs(Sample(winter,sheltered)-Sample(baseline,sheltered));
                var wallChange=Mathf.Abs(Sample(winter,wall)-Sample(baseline,wall));
                Debug.Log("Snow verification samples: groundGain="+floorGain+", roofGain="+roofGain+
                    ", shelteredChange="+shelteredChange+", wallChange="+wallChange);
                Require(floorGain>0.12f,"Exposed floor did not receive snow.");
                Require(roofGain>0.12f,"Roof did not receive snow.");
                Require(shelteredChange<0.035f,"Snow appeared under the roof.");
                Require(wallChange<0.035f,"Vertical wall was recolored.");
                double sum0=0,sumHalf=0,sumFull=0;
                var b=baseline.GetPixels(); var m=middle.GetPixels(); var w=winter.GetPixels(); var t=thaw.GetPixels();
                for(var i=0;i<b.Length;i++)
                {
                    sum0+=b[i].r; sumHalf+=m[i].r; sumFull+=w[i].r;
                    Require(Mathf.Abs(b[i].r-t[i].r)<0.012f,"Thaw did not restore original render.");
                }
                Require(sumHalf>sum0+100 && sumFull>sumHalf+100,"Coverage stages did not grow.");
                sun.intensity=0; RenderSettings.ambientLight=Color.black;
                var night=Capture(1,"snow-night");
                Require(Sample(night,floor)<0.025f && Sample(night,roof)<0.025f,"Snow emits light in darkness.");
                sun.intensity=1; RenderSettings.ambientLight=new Color(0.12f,0.12f,0.12f);
                apply.Invoke(controller,new object[]{1f,false}); camera.Render();
                Require(camera.GetCommandBuffers(CameraEvent.BeforeReflections).Length==0,"Disable left camera commands attached.");
                ((IDisposable)controller).Dispose();
                var reloaded=Capture(1,"snow-reloaded");
                Require(Sample(reloaded,floor)>Sample(baseline,floor)+0.12f,"Session reload lost snow.");
                camera.allowHDR=false;
                var ldrTarget=new RenderTexture(512,384,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
                ldrTarget.Create(); camera.targetTexture=ldrTarget;
                var ldrBase=Capture(0,"snow-ldr-0"); var ldrWinter=Capture(1,"snow-ldr-100");
                Require(Sample(ldrWinter,floor)>Sample(ldrBase,floor)+0.10f,"LDR snow failed.");
                camera.allowHDR=true; camera.targetTexture=target; UnityEngine.Object.DestroyImmediate(ldrTarget);
                var terrainData=new TerrainData { heightmapResolution=33,size=new Vector3(4,2,4) };
                var terrainHeights=new float[33,33];
                for(var y=0;y<33;y++) for(var x=0;x<33;x++) terrainHeights[y,x]=0.5f;
                terrainData.SetHeights(0,0,terrainHeights);
                var terrainObject=Terrain.CreateTerrainGameObject(terrainData);
                terrainObject.transform.position=new Vector3(1,0.5f,-5);
                var terrain=terrainObject.GetComponent<Terrain>();
                var terrainLayer=new TerrainLayer { diffuseTexture=Texture2D.whiteTexture };
                terrainData.terrainLayers=new[]{terrainLayer};
                terrain.drawInstanced=true;
                ((IDisposable)controller).Dispose();
                var terrainBare=Capture(0,"terrain-0"); var terrainSnow=Capture(1,"terrain-100");
                var terrainPoint=new Vector3(3,1.5f,-3);
                // A white terrain layer makes brightness an unreliable snow signal;
                // check that the overlay also updates its near exposure map correctly.
                var map=type.GetField("near",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
                var mapType=map.GetType();
                var area=(Vector4)mapType.GetField("Area").GetValue(map);
                var heightTexture=(RenderTexture)mapType.GetField("Texture").GetValue(map);
                var previous=RenderTexture.active; RenderTexture.active=heightTexture;
                var heightRead=new Texture2D(1024,1024,TextureFormat.RGBAFloat,false,true);
                heightRead.ReadPixels(new Rect(0,0,1024,1024),0,0); heightRead.Apply(); RenderTexture.active=previous;
                var tx=Mathf.FloorToInt(((terrainPoint.x-area.x)/(area.z*2)+0.5f)*1024);
                var ty=Mathf.FloorToInt(((terrainPoint.z-area.y)/(area.z*2)+0.5f)*1024);
                var height=heightRead.GetPixel(tx,ty).r;
                Debug.Log("Snow terrain exposure height: "+height);
                Require(Mathf.Abs(height-terrainPoint.y)<0.1f,"Instanced terrain exposure height is incorrect.");
                Require(terrain.drawInstanced,"Exposure capture did not restore terrain instancing.");
                foreach(var extra in new UnityEngine.Object[]{ldrBase,ldrWinter,terrainBare,terrainSnow,heightRead,terrainObject,terrainData,terrainLayer})
                    UnityEngine.Object.DestroyImmediate(extra);
                VerifyUnevenTerrain(type);
                VerifyDistantExposure(type);
                VerifyWeatherAndRails(type);
                VerifyWorldOrigin(type);
                VerifyIncrementalScan(assembly);
                VerifyMovingVehicle(type,material,sun);
                VerifyFreightAndFleet(type,material);
                VerifyVehicleAccumulation(type,material);
                VerifyExternalParts(type,material);
                VerifyVehicleHeat(type,material,repository);
                VerifyDistantTerrain(assembly,repository);
                Measure1440p();
                Debug.Log("DVSeasons procedural snow verified: real deferred scene, floor/roof coverage, shelter, vertical wall, stages, thaw, night, disable, reload, HDR/LDR and terrain exposure.");
                foreach(var texture in new[]{baseline,middle,winter,thaw,night,reloaded}) UnityEngine.Object.DestroyImmediate(texture);
            }
            finally
            {
                ((IDisposable)controller).Dispose(); ((IDisposable)repository).Dispose();
                camera.targetTexture=null; UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }
        private static void VerifyVehicleHeat(Type type,Material bodyMaterial,object repository)
        {
            ((IDisposable)controller).Dispose();
            var root=GameObject.CreatePrimitive(PrimitiveType.Cube);
            root.transform.position=new Vector3(3,1,-2);root.transform.localScale=new Vector3(3,1,3);
            root.GetComponent<Renderer>().sharedMaterial=bodyMaterial;
            var registry=type.GetField("vehicles",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
            registry.GetType().GetMethod("Register").Invoke(registry,new object[]{root.transform,null,null});
            var setter=type.GetMethod("SetVehicleSnowRemaining");
            var p=new Vector3(3,1.5f,-2);
            try
            {
                var bare=Capture(0,"heat-bare");var full=Capture(1,"heat-off-or-be2");
                setter.Invoke(controller,new object[]{new Func<Component,float>(_=>.7f)});
                var diesel=Capture(1,"heat-diesel");
                setter.Invoke(controller,new object[]{new Func<Component,float>(_=>0f)});
                var steam=Capture(1,"heat-steam");
                float a=Sample(bare,p),b=Sample(full,p),d=Sample(diesel,p),s=Sample(steam,p);
                Require(b>a+.12f && d>a+.05f && d<b-.04f,"Diesel heat did not partially remove snow.");
                Require(Mathf.Abs(s-a)<.015f,"Steam heat left the dynamic snow overlay.");
                foreach(var texture in new[]{bare,full,diesel,steam}) UnityEngine.Object.DestroyImmediate(texture);
                var shader=(Shader)repository.GetType().GetMethod("LoadShader").Invoke(repository,new object[]{"SnowVehicle"});
                var mat=new Material(shader);var source=new Texture2D(2,2,TextureFormat.RGBA32,false,true);
                var snowy=new Texture2D(2,2,TextureFormat.RGBA32,false,true);
                source.SetPixels(new[]{new Color(.1f,.1f,.1f,.4f),new Color(.1f,.1f,.1f,.4f),new Color(.1f,.1f,.1f,.4f),new Color(.1f,.1f,.1f,.4f)});source.Apply();
                snowy.SetPixels(new[]{Color.white,Color.white,Color.white,Color.white});snowy.Apply();
                var target=new RenderTexture(2,2,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);target.Create();
                try
                {
                    foreach(float weight in new[]{0f,.5f,1f})
                    {
                        mat.SetTexture("_DVPSCoalSnow",snowy);mat.SetFloat("_DVPSCoalAmount",weight);
                        Graphics.Blit(source,target,mat,4);
                        var read=AsyncGPUReadback.Request(target,0,TextureFormat.RGBAFloat);read.WaitForCompletion();
                        var pixel=read.GetData<Color>()[0];
                        Debug.Log("Coal blend sample weight="+weight+" color="+pixel+" passes="+mat.passCount+" pass="+mat.GetPassName(4));
                        Require(Mathf.Abs(pixel.r-Mathf.Lerp(.1f,1,weight))<.01f && Mathf.Abs(pixel.a-.4f)<.01f,
                            "Coal blend lost its snow amount or native clipping alpha.");
                    }
                }
                finally {foreach(var item in new UnityEngine.Object[]{mat,source,snowy,target}) UnityEngine.Object.DestroyImmediate(item);}
            }
            finally
            {
                setter.Invoke(controller,new object[]{null});UnityEngine.Object.DestroyImmediate(root);((IDisposable)controller).Dispose();
            }
            Debug.Log("DVSeasons heat and coal checks passed: full/partial/zero vehicle cover and coal blend preserving native alpha.");
        }
        private static void VerifyUnevenTerrain(Type type)
        {
            var oldPosition=camera.transform.position;var oldRotation=camera.transform.rotation;
            var oldFar=camera.farClipPlane;
            var data=new TerrainData {heightmapResolution=129,size=new Vector3(64,16,64)};
            var heights=new float[129,129];
            for(int z=0;z<129;z++) for(int x=0;x<129;x++)
                heights[z,x]=0.3f+0.12f*Mathf.Sin(x*0.22f)*Mathf.Cos(z*0.17f);
            data.SetHeights(0,0,heights);
            var tex=new Texture2D(2,2,TextureFormat.RGBA32,false);
            tex.SetPixels(new[]{Color.gray,Color.gray,Color.gray,Color.gray});tex.Apply();
            var layer=new TerrainLayer {diffuseTexture=tex};data.terrainLayers=new[]{layer};
            var owner=Terrain.CreateTerrainGameObject(data);owner.transform.position=new Vector3(180,0,-32);
            var terrain=owner.GetComponent<Terrain>();terrain.drawInstanced=true;terrain.heightmapPixelError=5;
            try
            {
                camera.transform.position=new Vector3(212,45,-30);camera.transform.LookAt(new Vector3(212,4,0));
                camera.farClipPlane=500;
                ((IDisposable)controller).Dispose();
                var bare=Capture(0,"uneven-terrain-bare");var snow=Capture(1,"uneven-terrain-snow");
                var exposureMap=type.GetField("near",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
                var mapArea=(Vector4)exposureMap.GetType().GetField("Area").GetValue(exposureMap);
                var mapTexture=(RenderTexture)exposureMap.GetType().GetField("Texture").GetValue(exposureMap);
                var request=AsyncGPUReadback.Request(mapTexture,0,TextureFormat.RFloat);request.WaitForCompletion();
                var mapPixels=request.GetData<float>();
                var rows=new System.Text.StringBuilder("x,z,actual,map,error,gain\n");
                int covered=0,total=0;
                for(int z=20;z<=44;z+=2) for(int x=20;x<=44;x+=2)
                {
                    var point=new Vector3(180+x,terrain.SampleHeight(new Vector3(180+x,0,-32+z)),-32+z);
                    // Restrict to upward gentle slopes so steep rocks legitimately remain bare.
                    if(data.GetInterpolatedNormal(x/64f,z/64f).y<0.85f) continue;
                    float gain=Sample(snow,point)-Sample(bare,point);
                    int mx=Mathf.FloorToInt(((point.x-mapArea.x)/(2*mapArea.z)+0.5f)*1024);
                    int mz=Mathf.FloorToInt(((point.z-mapArea.y)/(2*mapArea.z)+0.5f)*1024);
                    float mapHeight=mapPixels[mz*1024+mx];
                    rows.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,"{0},{1},{2},{3},{4},{5}",point.x,point.z,point.y,mapHeight,mapHeight-point.y,gain));
                    total++;if(gain>0.10f) covered++;
                }
                File.WriteAllText(Path.Combine(directory,"uneven-height-errors.csv"),rows.ToString());
                Debug.Log("Uneven terrain covered samples: "+covered+"/"+total);
                Require(total>20 && covered>=total*0.95f,"Gentle uneven terrain has dynamic snow holes.");
                Require(terrain.drawInstanced && terrain.drawHeightmap && Mathf.Abs(terrain.heightmapPixelError-5)<0.01f,
                    "Terrain capture changed native rendering quality/visibility.");
                var bridge=GameObject.CreatePrimitive(PrimitiveType.Cube);
                bridge.transform.position=new Vector3(212,12,0);bridge.transform.localScale=new Vector3(8,0.25f,8);
                try
                {
                    ((IDisposable)controller).Dispose();
                    UnityEngine.Object.DestroyImmediate(Capture(1,"uneven-terrain-bridge"));
                    exposureMap=type.GetField("near",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
                    mapArea=(Vector4)exposureMap.GetType().GetField("Area").GetValue(exposureMap);
                    mapTexture=(RenderTexture)exposureMap.GetType().GetField("Texture").GetValue(exposureMap);
                    request=AsyncGPUReadback.Request(mapTexture,0,TextureFormat.RFloat);request.WaitForCompletion();
                    int mx=Mathf.FloorToInt(((212-mapArea.x)/(2*mapArea.z)+0.5f)*1024);
                    int mz=Mathf.FloorToInt(((0-mapArea.y)/(2*mapArea.z)+0.5f)*1024);
                    Require(Mathf.Abs(request.GetData<float>()[mz*1024+mx]-12.125f)<0.02f,
                        "Direct terrain capture overwrote the bridge occluder.");
                    UnityEngine.Object.DestroyImmediate(bridge);bridge=null;
                    var holes=new bool[128,128];
                    for(int z=0;z<128;z++) for(int x=0;x<128;x++) holes[z,x]=!(x>=56 && x<=72 && z>=56 && z<=72);
                    data.SetHoles(0,0,holes);
                    ((IDisposable)controller).Dispose();
                    UnityEngine.Object.DestroyImmediate(Capture(1,"uneven-terrain-hole"));
                    exposureMap=type.GetField("near",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
                    mapTexture=(RenderTexture)exposureMap.GetType().GetField("Texture").GetValue(exposureMap);
                    request=AsyncGPUReadback.Request(mapTexture,0,TextureFormat.RFloat);request.WaitForCompletion();
                    Require(request.GetData<float>()[mz*1024+mx]<-90000,"Terrain holes were filled by the height capture.");
                }
                finally {if(bridge!=null) UnityEngine.Object.DestroyImmediate(bridge);}
                UnityEngine.Object.DestroyImmediate(bare);UnityEngine.Object.DestroyImmediate(snow);
            }
            finally
            {
                camera.transform.SetPositionAndRotation(oldPosition,oldRotation);camera.farClipPlane=oldFar;
                foreach(var item in new UnityEngine.Object[]{owner,data,layer,tex}) UnityEngine.Object.DestroyImmediate(item);
                ((IDisposable)controller).Dispose();
            }
            Debug.Log("DVSeasons uneven terrain checks passed: gentle slopes, native flags restored, bridge depth and terrain holes.");
        }
        private static void VerifyDistantExposure(Type type)
        {
            // Native DV/DistantTerrain is Opaque but displaces its mesh using a
            // heightmap/anchor. Plain replacement vertices create a false roof.
            var proxyMaterial=new Material(Shader.Find("DV/DistantTerrain"));
            proxyMaterial.SetFloat("_FixtureHeightShift",-30f);
            var proxy=GameObject.CreatePrimitive(PrimitiveType.Cube);
            proxy.name="Displaced distant landscape proxy";
            proxy.transform.position=new Vector3(3,10,0);
            proxy.transform.localScale=new Vector3(8,1,8);
            var renderer=proxy.GetComponent<MeshRenderer>();renderer.sharedMaterial=proxyMaterial;
            var point=new Vector3(3,0,0);
            try
            {
                ((IDisposable)controller).Dispose();
                var bare=Capture(0,"distant-exposure-bare");
                var snowy=Capture(1,"distant-exposure-snow");
                float gain=Sample(snowy,point)-Sample(bare,point);
                Debug.Log("Distant proxy exposure ground gain: "+gain);
                Require(gain>0.12f,"Displaced distant proxy falsely shelters exposed ground.");
                Require(!renderer.forceRenderingOff,"Distant terrain visibility was not restored after capture.");
                foreach(string name in new[]{"near","far"})
                {
                    var map=type.GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
                    var mt=map.GetType();var area=(Vector4)mt.GetField("Area").GetValue(map);
                    var texture=(RenderTexture)mt.GetField("Texture").GetValue(map);
                    var request=AsyncGPUReadback.Request(texture,0,TextureFormat.RFloat);request.WaitForCompletion();
                    Require(!request.hasError,"Distant exposure readback failed.");
                    int x=Mathf.FloorToInt(((point.x-area.x)/(area.z*2)+0.5f)*texture.width);
                    int y=Mathf.FloorToInt(((point.z-area.y)/(area.z*2)+0.5f)*texture.height);
                    Require(Mathf.Abs(request.GetData<float>()[y*texture.width+x])<0.1f,
                        name+" exposure contains distant proxy height instead of actual ground.");
                }
                UnityEngine.Object.DestroyImmediate(bare);UnityEngine.Object.DestroyImmediate(snowy);
                // A visible deferred distant mesh without loaded terrain must
                // retain its material. Loading detailed geometry enables snow,
                // including while the weather is dry and the camera is stationary.
                var ground=GameObject.Find("Ground");ground.SetActive(false);
                proxyMaterial.SetFloat("_FixtureHeightShift",-9f);
                GameObject detailed=null;
                var loadedMaterial=new Material(Shader.Find("Standard"));
                loadedMaterial.color=new Color(0.16f,0.12f,0.09f);
                try
                {
                    ((IDisposable)controller).Dispose();
                    var proxyBare=Capture(0,"unloaded-distant-bare");
                    var proxySnow=Capture(1,"unloaded-distant-texture-only");
                    var top=new Vector3(3,1.5f,0);
                    Require(Mathf.Abs(Sample(proxyBare,top)-Sample(proxySnow,top))<0.015f,
                        "Unloaded distant geometry received a dynamic overlay.");
                    renderer.enabled=false;
                    detailed=GameObject.CreatePrimitive(PrimitiveType.Cube);
                    detailed.transform.position=new Vector3(3,1.25f,0);
                    detailed.transform.localScale=new Vector3(4,0.5f,4);
                    detailed.GetComponent<MeshRenderer>().sharedMaterial=loadedMaterial;
                    var loadedBare=Capture(0,"loaded-detail-bare");
                    var before=Capture(1,"loaded-detail-before-refresh");
                    type.GetMethod("SetWeather").Invoke(controller,new object[]{0f});
                    type.GetMethod("InvalidateGeometry").Invoke(controller,null);
                    // The real scheduler throttles captures during streaming. Make
                    // this test deterministic without waiting for editor wall time.
                    type.GetField("nextProxyRefresh",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(controller,0f);
                    var loadedSnow=Capture(1,"loaded-detail-dry-snow");
                    Require(Sample(loadedSnow,top)>Sample(loadedBare,top)+0.12f,
                        "Loaded detailed terrain did not receive snow after geometry invalidation in dry weather.");
                    int captures=(int)type.GetProperty("ExposureCaptureCount").GetValue(controller,null);
                    // One pending far-map refresh is allowed after the near map.
                    type.GetField("nextProxyRefresh",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(controller,0f);
                    apply.Invoke(controller,new object[]{1f,true});
                    int settled=(int)type.GetProperty("ExposureCaptureCount").GetValue(controller,null);
                    for(int i=0;i<20;i++) apply.Invoke(controller,new object[]{1f,true});
                    Require((int)type.GetProperty("ExposureCaptureCount").GetValue(controller,null)==settled && settled<=captures+1,
                        "Stationary dry terrain caused repeated exposure captures.");
                    renderer.forceRenderingOff=true;
                    type.GetMethod("InvalidateGeometry").Invoke(controller,null);
                    type.GetField("nextProxyRefresh",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(controller,0f);
                    apply.Invoke(controller,new object[]{1f,true});
                    Require(renderer.forceRenderingOff && !renderer.enabled,"Capture changed external terrain visibility flags.");
                    foreach(var texture in new[]{proxyBare,proxySnow,loadedBare,before,loadedSnow}) UnityEngine.Object.DestroyImmediate(texture);
                }
                finally
                {
                    ground.SetActive(true);if(detailed!=null) UnityEngine.Object.DestroyImmediate(detailed);
                    UnityEngine.Object.DestroyImmediate(loadedMaterial);
                    type.GetMethod("SetWeather").Invoke(controller,new object[]{1f});
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(proxy);UnityEngine.Object.DestroyImmediate(proxyMaterial);
                ((IDisposable)controller).Dispose();
            }
            Debug.Log("DVSeasons distant exposure checks passed: no phantom roof, both maps, distant texture-only, dry detail streaming, cached captures, visibility restored.");
        }
        private static void VerifyExternalParts(Type type,Material material)
        {
            ((IDisposable)controller).Dispose();
            var root=new GameObject("External parts fixture");
            var interior=new GameObject("Interior with external parts");interior.transform.SetParent(root.transform);
            var hood=GameObject.CreatePrimitive(PrimitiveType.Cube);hood.transform.SetParent(interior.transform);
            hood.transform.position=new Vector3(2,2,-4);hood.transform.localScale=new Vector3(2,0.2f,2);
            hood.GetComponent<Renderer>().sharedMaterial=material;
            var panel=GameObject.CreatePrimitive(PrimitiveType.Cube);panel.transform.SetParent(interior.transform);
            panel.transform.position=new Vector3(-1,2,-4);panel.transform.localScale=new Vector3(1,0.2f,1);
            panel.GetComponent<Renderer>().sharedMaterial=material;
            var registry=type.GetField("vehicles",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
            var rt=registry.GetType();rt.GetMethod("Register").Invoke(registry,new object[]{root.transform,interior.transform,null});
            rt.GetMethod("SetExternalParts").Invoke(registry,new object[]{root.transform,hood.transform,null});
            var bare=Capture(0,"external-parts-bare");var covered=Capture(1,"external-parts-snow");
            try
            {
                Require(Sample(covered,new Vector3(2,2.1f,-4))-Sample(bare,new Vector3(2,2.1f,-4))>0.1f,
                    "External hood inside interior group was excluded from snow.");
                Require(Mathf.Abs(Sample(covered,new Vector3(-1,2.1f,-4))-Sample(bare,new Vector3(-1,2.1f,-4)))<0.025f,
                    "External-part inclusion also recolored the cab.");
            }
            finally
            {
                foreach(var obj in new UnityEngine.Object[]{bare,covered,root}) UnityEngine.Object.DestroyImmediate(obj);
                ((IDisposable)controller).Dispose();
            }
            Debug.Log("DVSeasons external parts checks passed: external hood covered, cab excluded.");
        }

        private static void VerifyVehicleAccumulation(Type type,Material material)
        {
            ((IDisposable)controller).Dispose();
            camera.transform.position=new Vector3(40,8,-16);camera.transform.LookAt(new Vector3(40,1,-2));
            var root=new GameObject("Sheltered locomotive");root.transform.position=new Vector3(40,0,0);
            var body=GameObject.CreatePrimitive(PrimitiveType.Cube);body.transform.SetParent(root.transform);
            body.transform.localPosition=new Vector3(0,1,-2);body.transform.localScale=new Vector3(4,1,5);
            body.GetComponent<Renderer>().sharedMaterial=material;
            var roof=GameObject.CreatePrimitive(PrimitiveType.Cube);roof.transform.position=new Vector3(40,4,-2);
            roof.transform.localScale=new Vector3(6,0.2f,8);roof.GetComponent<Renderer>().sharedMaterial=material;
            var registry=type.GetField("vehicles",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
            var rt=registry.GetType();rt.GetMethod("Register").Invoke(registry,new object[]{root.transform,null,null});
            var tracks=type.GetField("RailTracks").GetValue(controller);
            var advance=tracks.GetType().GetMethod("Advance");
            var target=new RenderTexture(256,256,0,RenderTextureFormat.RHalf,RenderTextureReadWrite.Linear);target.Create();
            Func<float> cover=()=>
            {
                var array=(RenderTexture)rt.GetField("snow",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(registry);
                Graphics.CopyTexture(array,0,0,target,0,0);
                // Read numeric mask data directly. ReadPixels applies a gamma
                // conversion to RHalf on this Unity version (0.5 became 0.219).
                var request=AsyncGPUReadback.Request(target,0,TextureFormat.RFloat);
                request.WaitForCompletion();Require(!request.hasError,"Vehicle mask readback failed.");
                return request.GetData<float>()[128*256+128];
            };
            try
            {
                type.GetMethod("SetWeather").Invoke(controller,new object[]{0f});
                UnityEngine.Object.DestroyImmediate(Capture(1,"vehicle-sheltered-initial"));
                Require(cover()<0.05f,"New vehicle under a roof accumulated snow.");
                root.transform.position+=Vector3.right*8;camera.transform.position+=Vector3.right*8;
                UnityEngine.Object.DestroyImmediate(Capture(1,"vehicle-exit-dry"));
                Require(cover()<0.05f,"Dry vehicle motion changed stored snow.");
                advance.Invoke(tracks,new object[]{1f,90f});
                UnityEngine.Object.DestroyImmediate(Capture(1,"vehicle-recovery-half"));
                Debug.Log("Vehicle recovery half stored coverage="+cover()+" snow clock="+tracks.GetType().GetProperty("SnowClock").GetValue(tracks,null));
                Require(cover()>0.4f && cover()<0.6f,"Snowfall did not gradually restore uncovered vehicle surfaces.");
                advance.Invoke(tracks,new object[]{1f,90f});
                UnityEngine.Object.DestroyImmediate(Capture(1,"vehicle-recovery-full"));
                Require(cover()>0.95f,"Continued snowfall did not refill vehicle snow.");
                root.transform.position-=Vector3.right*8;camera.transform.position-=Vector3.right*8;
                UnityEngine.Object.DestroyImmediate(Capture(1,"vehicle-return-to-shelter"));
                Require(cover()>0.95f,"Moving into shelter erased accumulated vehicle snow.");
                var cabRoot=new GameObject("Loaded cab");cabRoot.transform.SetParent(root.transform);
                rt.GetMethod("Register").Invoke(registry,new object[]{root.transform,cabRoot.transform,null});
                UnityEngine.Object.DestroyImmediate(Capture(1,"vehicle-cab-streamed"));
                Require(cover()>0.95f,"Interior streaming erased the vehicle's stored snow.");
                advance.Invoke(tracks,new object[]{1f,180f});
                UnityEngine.Object.DestroyImmediate(Capture(1,"vehicle-shelter-persists"));
                Require(cover()>0.95f,"Shelter erased stored snow during snowfall.");
                UnityEngine.Object.DestroyImmediate(Capture(0,"vehicle-full-thaw"));
                type.GetMethod("SetWeather").Invoke(controller,new object[]{1f});
                UnityEngine.Object.DestroyImmediate(Capture(1,"vehicle-new-winter-under-roof"));
                Require(cover()<0.05f,"Full thaw retained old vehicle accumulation into the next winter.");
            }
            finally
            {
                foreach(var obj in new UnityEngine.Object[]{root,roof,target}) UnityEngine.Object.DestroyImmediate(obj);
                ((IDisposable)controller).Dispose();
                camera.transform.position=new Vector3(0,7,-16);camera.transform.LookAt(new Vector3(0,1,1));
                type.GetMethod("SetWeather").Invoke(controller,new object[]{1f});
            }
            Debug.Log("DVSeasons vehicle accumulation checks passed: shelter, dry motion, gradual snowfall recovery and retained cover.");
        }

        private static void VerifyIncrementalScan(Assembly assembly)
        {
            var root=new GameObject("Incremental scan fixture");
            var ids=new System.Collections.Generic.HashSet<int>();
            for(int i=0;i<80;i++)
            {
                var child=new GameObject("Node "+i);child.transform.SetParent(root.transform);
                if(i%4==0) ids.Add(child.AddComponent<MeshRenderer>().GetInstanceID());
                if(i%8==0) child.SetActive(false);
            }
            int visits=0;
            Action<MeshRenderer> visit=r=>{if(ids.Remove(r.GetInstanceID())) visits++;};
            var scanType=assembly.GetType("DVSeasons.Mod.IncrementalSceneScan`1",true).MakeGenericType(typeof(MeshRenderer));
            var scan=Activator.CreateInstance(scanType,new object[]{0f,4,visit});
            var step=scanType.GetMethod("Step");
            try
            {
                step.Invoke(scan,null);
                Require(visits<=4,"Incremental discovery exceeded its per-frame node budget.");
                for(int i=0;i<500 && ids.Count>0;i++) step.Invoke(scan,null);
                Require(ids.Count==0,"Incremental discovery missed inactive or late scene objects.");
                var late=new GameObject("Streamed renderer");late.transform.SetParent(root.transform);
                ids.Add(late.AddComponent<MeshRenderer>().GetInstanceID());
                for(int i=0;i<500 && ids.Count>0;i++) step.Invoke(scan,null);
                Require(ids.Count==0,"Incremental discovery did not rescan newly loaded objects.");
            }
            finally {((IDisposable)scan).Dispose();UnityEngine.Object.DestroyImmediate(root);}
            Debug.Log("DVSeasons incremental scan checks passed: bounded work, inactive renderers and streamed objects.");
        }

        private static void VerifyWorldOrigin(Type type)
        {
            ((IDisposable)controller).Dispose();
            type.GetMethod("SetWeather").Invoke(controller,new object[]{1f});
            var before=Capture(0.65f,"origin-before");
            type.GetMethod("SetWeather").Invoke(controller,new object[]{0f});
            int captures=(int)type.GetProperty("ExposureCaptureCount").GetValue(controller,null);
            var roots=UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            var delta=new Vector3(32,-8,16);
            Texture2D after=null;
            try
            {
                foreach(var root in roots) root.transform.position+=delta;
                type.GetMethod("SetWorldOffset")?.Invoke(controller,new object[]{delta});
                after=Capture(0.65f,"origin-after");
                var b=before.GetPixels();var a=after.GetPixels();double difference=0;
                for(int i=0;i<b.Length;i++) difference+=Mathf.Abs(b[i].r-a[i].r);
                difference/=b.Length;
                Debug.Log("Snow origin shift mean image error="+difference);
                Require(difference<0.005,"World origin shift changed snow patches or shelter on stationary world geometry.");
                Require((int)type.GetProperty("ExposureCaptureCount").GetValue(controller,null)==captures,
                    "Origin shift unnecessarily recaptured the frozen exposure maps.");
            }
            finally
            {
                foreach(var root in roots) if(root!=null) root.transform.position-=delta;
                type.GetMethod("SetWorldOffset")?.Invoke(controller,new object[]{Vector3.zero});
                UnityEngine.Object.DestroyImmediate(before);
                if(after!=null) UnityEngine.Object.DestroyImmediate(after);
                ((IDisposable)controller).Dispose();
                type.GetMethod("SetWeather").Invoke(controller,new object[]{1f});
            }
            Debug.Log("DVSeasons origin checks passed: XYZ rebasing preserves frozen snow pattern and shelter without recapture.");
        }

        private static void VerifyFreightAndFleet(Type type,Material material)
        {
            ((IDisposable)controller).Dispose();
            camera.transform.position=new Vector3(0,7,-16);camera.transform.LookAt(new Vector3(0,1,1));
            var registry=type.GetField("vehicles",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
            var registryType=registry.GetType();
            var carrier=new GameObject("Freight carrier");
            var interior=new GameObject("interior");
            var cab=GameObject.CreatePrimitive(PrimitiveType.Cube);cab.transform.SetParent(interior.transform);
            cab.transform.position=new Vector3(-1,2,-4);cab.transform.localScale=new Vector3(1,0.2f,1);
            cab.GetComponent<Renderer>().sharedMaterial=material;
            var cargo=new GameObject("Cargo cars inside interior");cargo.transform.SetParent(interior.transform);
            var car=GameObject.CreatePrimitive(PrimitiveType.Cube);car.transform.SetParent(cargo.transform);
            car.transform.position=new Vector3(2,2,-4);car.transform.localScale=new Vector3(1,0.3f,2);
            car.GetComponent<Renderer>().sharedMaterial=material;
            // A coarser silhouette in another LOD must not become the top surface.
            var coarse=GameObject.CreatePrimitive(PrimitiveType.Cube);coarse.transform.SetParent(cargo.transform);
            coarse.transform.position=new Vector3(2,2.5f,-4);coarse.transform.localScale=new Vector3(2,1,3);
            coarse.GetComponent<Renderer>().sharedMaterial=material;
            var group=cargo.AddComponent<LODGroup>();
            group.SetLODs(new[]{new LOD(0.05f,new[]{car.GetComponent<Renderer>()}),new LOD(0.001f,new[]{coarse.GetComponent<Renderer>()})});
            group.RecalculateBounds();
            registryType.GetMethod("Register").Invoke(registry,new object[]{carrier.transform,interior.transform,null});
            registryType.GetMethod("SetCargo").Invoke(registry,new object[]{carrier.transform,cargo.transform});
            var bare=Capture(0,"freight-bare");var covered=Capture(1,"freight-snow");
            float gain=Sample(covered,new Vector3(2,2.15f,-4))-Sample(bare,new Vector3(2,2.15f,-4));
            Require(gain>0.1f,"Freight parented inside interior was excluded from snow or blocked by coarse LOD.");
            Require(Mathf.Abs(Sample(covered,new Vector3(-1,2.1f,-4))-Sample(bare,new Vector3(-1,2.1f,-4)))<0.025f,"Cab was recolored with freight.");
            int draws=(int)registryType.GetProperty("FrameDrawCount").GetValue(registry,null);
            Require(draws==2,"Snow mask drew multiple LOD representations of freight. Draws="+draws);
            // Replacing cargo on the same car must refresh its cached exterior.
            registryType.GetMethod("SetCargo").Invoke(registry,new object[]{carrier.transform,null});
            var unloaded=Capture(1,"freight-unloaded");
            Require(Mathf.Abs(Sample(unloaded,new Vector3(2,2.15f,-4))-Sample(bare,new Vector3(2,2.15f,-4)))<0.025f,"Cargo identity change left stale classification.");
            foreach(var t in new UnityEngine.Object[]{bare,covered,unloaded,carrier,interior}) UnityEngine.Object.DestroyImmediate(t);
            ((IDisposable)controller).Dispose();
            var fleet=new GameObject[40];
            for(int index=0;index<fleet.Length;index++)
            {
                fleet[index]=GameObject.CreatePrimitive(PrimitiveType.Cube);fleet[index].name="Fleet "+index;
                fleet[index].transform.position=index==39?new Vector3(-3,3,-6):new Vector3(60+index*3,3,20);
                fleet[index].GetComponent<Renderer>().sharedMaterial=material;
                registryType.GetMethod("TrackStaticRoot").Invoke(registry,new object[]{fleet[index].transform,null,null});
                if(index<32) registryType.GetMethod("Register").Invoke(registry,new object[]{fleet[index].transform,null,null});
            }
            int count=(int)registryType.GetProperty("CaptureCount").GetValue(registry,null);
            var fleetSnow=Capture(1,"fleet-static-exclusion");
            Require((int)registryType.GetProperty("CaptureCount").GetValue(registry,null)-count==1,"Fleet startup captured multiple vehicle maps in one frame.");
            var map=type.GetField("near",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
            var area=(Vector4)map.GetType().GetField("Area").GetValue(map);
            var texture=(RenderTexture)map.GetType().GetField("Texture").GetValue(map);
            RenderTexture.active=texture;
            var pixel=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);
            int x=Mathf.FloorToInt(((-3-area.x)/(2*area.z)+0.5f)*1024);
            int y=Mathf.FloorToInt(((-6-area.y)/(2*area.z)+0.5f)*1024);
            pixel.ReadPixels(new Rect(x,y,1,1),0,0);pixel.Apply();RenderTexture.active=null;
            Debug.Log("Fleet static height="+pixel.GetPixel(0,0).r+" area="+area+" x="+x+" y="+y);
            Require(pixel.GetPixel(0,0).r<0.1f,"Car outside 32-slot cache left a rectangular height-map footprint.");
            foreach(var obj in fleet) {Require(!obj.GetComponent<Renderer>().forceRenderingOff,"Static capture left a train hidden.");UnityEngine.Object.DestroyImmediate(obj);}
            UnityEngine.Object.DestroyImmediate(pixel);UnityEngine.Object.DestroyImmediate(fleetSnow);
            ((IDisposable)controller).Dispose();
            Debug.Log("DVSeasons freight/fleet checks passed: cargo inside interior, cab exclusion, single LOD, cargo change, 40-car static exclusion, one capture per frame.");
        }
        private static void VerifyDistantTerrain(Assembly assembly,object repository)
        {
            var distantShader=Shader.Find("DV/DistantTerrain");
            Require(distantShader!=null,"Distant terrain fixture is missing.");
            var material=new Material(distantShader);
            var summer=new Texture2DArray(4,4,16,TextureFormat.RGBA32,false);
            summer.name="Verification summer landscape";
            material.SetTexture("_Splats",summer);
            var terrainType=assembly.GetType("DVSeasons.Mod.MicroSplatSeasonalTerrainController",true);
            var terrain=Activator.CreateInstance(terrainType,new[]{repository});
            var gamePath=Environment.GetEnvironmentVariable("DERAIL_VALLEY_DIR");
            Require(!string.IsNullOrEmpty(gamePath),"Set DERAIL_VALLEY_DIR for the settings integration check.");
            Assembly.LoadFrom(Path.Combine(gamePath,"DerailValley_Data/Managed/UnityModManager/UnityModManager.dll"));
            var settingsType=assembly.GetType("DVSeasons.Mod.SeasonModSettings",true);
            var settings=Activator.CreateInstance(settingsType);
            Require((bool)settingsType.GetField("NativeWinterVegetationLod").GetValue(settings),"Native winter vegetation detail is not enabled by default.");
            var core=Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(assembly.Location),"DVSeasons.Core.dll"));
            var stateType=core.GetType("DVSeasons.Core.SeasonState",true);
            var seasonType=core.GetType("DVSeasons.Core.SeasonKind",true);
            var winter=Enum.Parse(seasonType,"Winter");
            var state=Activator.CreateInstance(stateType,new object[]{0d,winter,winter,0f,1f,-5f,0.5f});
            try
            {
                var applyTerrain=terrainType.GetMethod("Apply");
                applyTerrain.Invoke(terrain,new object[]{state,settings,true,(float?)1f});
                var snowy=material.GetTexture("_Splats");
                Require(snowy!=null && snowy!=summer,"Procedural mode disabled distant winter landscape textures.");
                applyTerrain.Invoke(terrain,new object[]{state,settings,true,(float?)0f});
                Require(material.GetTexture("_Splats")==summer,"Distant terrain ignored the frozen coverage override or thaw.");
                applyTerrain.Invoke(terrain,new object[]{state,settings,true,(float?)1f});
                ((IDisposable)terrain).Dispose();
                Require(material.GetTexture("_Splats")==summer,"Distant terrain original array was not restored.");
                int restores=(int)terrainType.GetProperty("RestorePassCount").GetValue(terrain,null);
                settingsType.GetField("SeasonalTexturesEnabled").SetValue(settings,false);
                for(int i=0;i<100;i++) applyTerrain.Invoke(terrain,new object[]{state,settings,true,(float?)1f});
                Require((int)terrainType.GetProperty("RestorePassCount").GetValue(terrain,null)==restores,"Disabled terrain rewrote/restored its bindings every frame.");
                settingsType.GetField("SeasonalTexturesEnabled").SetValue(settings,true);
                var texturesType=assembly.GetType("DVSeasons.Mod.SeasonalTextureController",true);
                var textures=Activator.CreateInstance(texturesType,new[]{repository});
                var trackMaterials=new Material[3];var originals=new Texture2D[3];
                var names=new[]{"BallastNew_d","SleeperNew_d","RailMed_d"};
                try
                {
                    for(int i=0;i<3;i++)
                    {
                        originals[i]=new Texture2D(16,16,TextureFormat.RGBA32,false);
                        originals[i].name=names[i];var colors=new Color[256];
                        for(int pixel=0;pixel<colors.Length;pixel++) colors[pixel]=new Color(0.08f,0.04f,0.02f,1);
                        originals[i].SetPixels(colors);originals[i].Apply();
                        trackMaterials[i]=new Material(Shader.Find("Standard"));trackMaterials[i].mainTexture=originals[i];
                    }
                    var applyTextures=texturesType.GetMethod("Apply");
                    for(int frame=0;frame<12;frame++)
                    {
                        texturesType.GetField("nextBindingCheck",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(textures,-1f);
                        applyTextures.Invoke(textures,new object[]{state,settings,true,(float?)1f});
                    }
                    Require(trackMaterials[0].mainTexture!=originals[0],"Procedural snow disabled native ballast snow.");
                    Require(trackMaterials[1].mainTexture!=originals[1],"Procedural snow disabled native sleeper snow.");
                    Require(trackMaterials[2].mainTexture==originals[2],"Baked snow obscured rail heads required for wheel clearing.");
                    for(int i=0;i<2;i++)
                    {
                        var pixels=((Texture2D)trackMaterials[i].mainTexture).GetPixels32();double total=0;
                        foreach(var pixel in pixels) total+=pixel.r/255d;
                        Require(total/pixels.Length>0.16,"Native track winter texture stayed summer-dark.");
                    }
                    for(int frame=0;frame<12;frame++)
                    {
                        texturesType.GetField("nextBindingCheck",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(textures,-1f);
                        applyTextures.Invoke(textures,new object[]{state,settings,true,(float?)0f});
                    }
                    var thawed=((Texture2D)trackMaterials[0].mainTexture).GetPixels32();double thawTotal=0;
                    foreach(var pixel in thawed) thawTotal+=pixel.r/255d;
                    Require(thawTotal/thawed.Length<0.12,"Track textures ignored the frozen snow coverage override.");
                    Debug.Log("DVSeasons native track checks passed: ballast and sleepers covered with procedural mode; rail-head source preserved.");
                }
                finally
                {
                    ((IDisposable)textures).Dispose();
                    for(int i=0;i<3;i++) {UnityEngine.Object.DestroyImmediate(trackMaterials[i]);UnityEngine.Object.DestroyImmediate(originals[i]);}
                }
                Debug.Log("DVSeasons distant terrain checks passed: procedural mode keeps native winter arrays, coverage override/thaw, restore, native vegetation defaults.");
            }
            finally {((IDisposable)terrain).Dispose();UnityEngine.Object.DestroyImmediate(material);UnityEngine.Object.DestroyImmediate(summer);}
        }
        private static void Measure1440p()
        {
            var original=camera.targetTexture;
            var target=new RenderTexture(2560,1440,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);
            target.Create(); camera.targetTexture=target;
            var pixel=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);
            double[] costs=new double[2];
            try
            {
                for(int state=0;state<2;state++)
                {
                    var watch=new System.Diagnostics.Stopwatch();
                    for(int frame=0;frame<68;frame++)
                    {
                        if(frame==8) watch.Start();
                        apply.Invoke(controller,new object[]{(float)state,true});camera.Render();
                        RenderTexture.active=target;pixel.ReadPixels(new Rect(0,0,1,1),0,0);pixel.Apply();
                    }
                    watch.Stop(); costs[state]=watch.Elapsed.TotalMilliseconds/60;
                }
                File.WriteAllText(Path.Combine(directory,"render-cost-1440p.txt"),
                    "Synthetic Unity scene, 2560x1440 HDR, 60 frames after warmup, blocking GPU readback. Bare: "+
                    costs[0].ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+" ms; snow: "+
                    costs[1].ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+" ms. Not in-game FPS.");
            }
            finally
            {
                RenderTexture.active=null; camera.targetTexture=original;
                UnityEngine.Object.DestroyImmediate(pixel);UnityEngine.Object.DestroyImmediate(target);
            }
        }
        private static void VerifyWeatherAndRails(Type type)
        {
            var weather=type.GetMethod("SetWeather");
            weather.Invoke(controller,new object[]{1f});
            var full=Capture(1,"rails-before");
            weather.Invoke(controller,new object[]{0f});
            int before=(int)type.GetProperty("ExposureCaptureCount").GetValue(controller,null);
            foreach(var name in new[]{"near","far"})
            {
                var map=type.GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(controller);
                map.GetType().GetField("NextUpdate").SetValue(map,-100f);
            }
            var frozen=Capture(0.5f,"dry-frozen");
            Require((float)type.GetProperty("Coverage").GetValue(controller,null)==1f,"Dry weather changed snow coverage.");
            Require(before==(int)type.GetProperty("ExposureCaptureCount").GetValue(controller,null),"Dry weather recaptured static maps on timer.");
            var rails=type.GetField("RailTracks").GetValue(controller);var rt=rails.GetType();
            var wheel=rt.GetMethod("WheelAt");var advance=rt.GetMethod("Advance");
            wheel.Invoke(rails,new object[]{1,new Vector3(2.5f,0.01f,-7),Vector3.right,Vector3.forward});
            wheel.Invoke(rails,new object[]{1,new Vector3(2.5f,0.01f,-2),Vector3.right,Vector3.forward});
            var cleared=Capture(1,"rails-cleared");
            var point=new Vector3(3.25f,0,-5);
            var change=Sample(full,point)-Sample(cleared,point);
            Debug.Log("Rail clearing gain: "+change);
            Require(change>0.1f,"Wheels did not immediately clear swept rail strip.");
            Require(Mathf.Abs(Sample(full,new Vector3(2.5f,0,-5))-Sample(cleared,new Vector3(2.5f,0,-5)))<0.03f,"Rail clearing erased ballast between rails.");
            advance.Invoke(rails,new object[]{0f,3600f});
            var dry=Capture(1,"rails-dry-persistent");
            Require(Mathf.Abs(Sample(dry,point)-Sample(cleared,point))<0.02f,"Dry weather erased wheel tracks.");
            advance.Invoke(rails,new object[]{1f,180f});
            var resnow=Capture(1,"rails-resnow");
            Require(Mathf.Abs(Sample(resnow,point)-Sample(full,point))<0.02f,"Snowfall did not recover cleared rails.");
            ((IDisposable)rails).Dispose();
            int countBefore=(int)rt.GetProperty("SegmentCount").GetValue(rails,null);
            for(int step=0;step<100;step++)
                wheel.Invoke(rails,new object[]{5,new Vector3(0,0,step*0.05f),Vector3.right,Vector3.forward});
            int merged=(int)rt.GetProperty("SegmentCount").GetValue(rails,null)-countBefore;
            Require(merged<=4,"Straight wheel motion allocated a new ribbon each frame.");
            rt.GetField("WorldOffset").SetValue(rails,new Vector3(1000,0,0));
            wheel.Invoke(rails,new object[]{5,new Vector3(1000,0,4.95f),Vector3.right,Vector3.forward});
            Require((int)rt.GetProperty("SegmentCount").GetValue(rails,null)==merged,"Floating-origin shift created wheel motion.");
            ((IDisposable)rails).Dispose();rt.GetField("WorldOffset").SetValue(rails,Vector3.zero);
            weather.Invoke(controller,new object[]{1f});
            Debug.Log("DVSeasons weather/rail checks passed: dry coverage/cache frozen, swept immediate clearing, ballast preserved, dry persistence, snowfall recovery.");
            foreach(var t in new[]{full,frozen,cleared,dry,resnow}) UnityEngine.Object.DestroyImmediate(t);
        }
        private static void VerifyMovingVehicle(Type type,Material material,Light sun)
        {
            sun.shadows=LightShadows.None;
            var vehicle=new GameObject("Moving locomotive test");
            var body=GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name="Body"; body.transform.SetParent(vehicle.transform);
            body.transform.localPosition=new Vector3(0,1,-2);
            body.transform.localScale=new Vector3(4,1,5);
            body.GetComponent<Renderer>().sharedMaterial=material;
            // DV detaches interiors into a separate root, and can hide the roof.
            var cab=new GameObject("Detached interior");
            var panel=GameObject.CreatePrimitive(PrimitiveType.Cube);
            panel.transform.SetParent(cab.transform); panel.transform.localPosition=new Vector3(0,2,-2);
            panel.transform.localScale=new Vector3(1,0.2f,1);
            panel.GetComponent<Renderer>().sharedMaterial=material;
            var registry=type.GetField("vehicles",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
            var registryType=registry.GetType();
            registryType.GetMethod("Register").Invoke(registry,new object[]{vehicle.transform,cab.transform,null});
            var bare=Capture(0,"vehicle-bare"); var snow=Capture(1,"vehicle-full");
            Require(Mathf.Abs(Sample(snow,new Vector3(0,2.1f,-2))-Sample(bare,new Vector3(0,2.1f,-2)))<0.025f,
                "Detached cab interior received snow.");
            Require(Sample(snow,new Vector3(1.4f,1.5f,-3))>Sample(bare,new Vector3(1.4f,1.5f,-3))+0.1f,
                "Vehicle body did not receive snow.");
            var staged=Capture(0.5f,"vehicle-before-move");
            var points=new Vector3[32]; var samples=new float[32];
            for(var i=0;i<points.Length;i++)
            {
                points[i]=new Vector3(-1.7f+(i%8)*0.48f,1.5f,-4.1f+(i/8)*0.35f);
                samples[i]=Sample(staged,points[i]);
            }
            var captures=(int)registryType.GetProperty("CaptureCount").GetValue(registry,null);
            var delta=new Vector3(6,0,0);
            vehicle.transform.position+=delta; cab.transform.position+=delta; camera.transform.position+=delta;
            var moved=Capture(0.5f,"vehicle-after-move");
            float maxDifference=0;
            for(var i=0;i<points.Length;i++) maxDifference=Mathf.Max(maxDifference,Mathf.Abs(samples[i]-Sample(moved,points[i]+delta)));
            Debug.Log("Vehicle movement pattern error: "+maxDifference);
            Require(maxDifference<0.035f,"Snow pattern did not remain attached to the moving vehicle.");
            Require(captures==(int)registryType.GetProperty("CaptureCount").GetValue(registry,null),
                "Moving a rigid vehicle rebuilt its height map.");
            var turn=Quaternion.Euler(0,25,0);
            vehicle.transform.rotation=turn; cab.transform.rotation=turn;
            camera.transform.position=vehicle.transform.position+turn*(camera.transform.position-vehicle.transform.position);
            camera.transform.rotation=turn*camera.transform.rotation;
            var rotated=Capture(0.5f,"vehicle-after-turn");
            maxDifference=0;
            for(var i=0;i<points.Length;i++) maxDifference=Mathf.Max(maxDifference,
                Mathf.Abs(samples[i]-Sample(rotated,vehicle.transform.TransformPoint(points[i]))));
            Debug.Log("Vehicle rotation pattern error: "+maxDifference);
            Require(maxDifference<0.05f,"Snow pattern did not remain attached when the vehicle turned.");

            Camera.CameraCallback late=c=>{if(c==camera)c.transform.position+=new Vector3(0.8f,0,0);};
            Camera.onPreCull+=late;
            Texture2D lateImage;
            try {lateImage=Capture(0.5f,"camera-late-move");} finally {Camera.onPreCull-=late;}
            var stable=Capture(0.5f,"camera-stable-reference");
            var a=lateImage.GetPixels(); var b=stable.GetPixels();
            float bottomError=0;
            for(var y=0;y<96;y++) for(var x=0;x<512;x++) bottomError=Mathf.Max(bottomError,Mathf.Abs(a[y*512+x].r-b[y*512+x].r));
            Debug.Log("Late camera movement bottom error: "+bottomError);
            Require(bottomError<0.02f,"Late camera movement broke bottom-of-screen coverage.");
            var before=(int)type.GetProperty("ExposureCaptureCount").GetValue(controller,null);
            var watch=System.Diagnostics.Stopwatch.StartNew();
            var pixel=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);
            for(var i=0;i<40;i++)
            {
                vehicle.transform.position+=Vector3.right*0.1f; cab.transform.position+=Vector3.right*0.1f;
                camera.transform.position+=Vector3.right*0.1f;
                apply.Invoke(controller,new object[]{0.5f,true}); camera.Render();
                RenderTexture.active=camera.targetTexture; pixel.ReadPixels(new Rect(0,0,1,1),0,0); pixel.Apply();
            }
            watch.Stop();
            var after=(int)type.GetProperty("ExposureCaptureCount").GetValue(controller,null);
            File.WriteAllText(Path.Combine(directory,"render-cost.txt"),"Synthetic scene 512x384, 40 moving frames with GPU readback: "+
                (watch.Elapsed.TotalMilliseconds/40).ToString("F3",System.Globalization.CultureInfo.InvariantCulture)+
                " ms/frame; world exposure recaptures="+(after-before)+"; vehicle cache captures="+captures+". Not in-game FPS.");
            Require(after-before<10,"Static exposure maps are being rendered too often.");
            Require(captures==(int)registryType.GetProperty("CaptureCount").GetValue(registry,null),"Motion repeatedly rebuilt vehicle cache.");
            Debug.Log("DVSeasons motion checks passed: detached interior excluded, local pattern, late camera movement, cached vehicle exposure.");
            foreach(var item in new UnityEngine.Object[]{bare,snow,staged,moved,rotated,lateImage,stable,pixel,vehicle,cab}) UnityEngine.Object.DestroyImmediate(item);
        }
        private static void Box(string name,Vector3 position,Vector3 scale,Material material)
        {
            var item=GameObject.CreatePrimitive(PrimitiveType.Cube); item.name=name;
            item.transform.position=position; item.transform.localScale=scale;
            item.GetComponent<Renderer>().sharedMaterial=material;
        }
        private static Texture2D Capture(float amount,string name)
        {
            apply.Invoke(controller,new object[]{amount,true}); camera.Render();
            var previous=RenderTexture.active; RenderTexture.active=camera.targetTexture;
            var texture=new Texture2D(512,384,TextureFormat.RGBAFloat,false,true);
            texture.ReadPixels(new Rect(0,0,512,384),0,0); texture.Apply();
            var png=new Texture2D(512,384,TextureFormat.RGB24,false);
            var colors=texture.GetPixels();
            for(var i=0;i<colors.Length;i++) colors[i]=colors[i].gamma;
            png.SetPixels(colors); png.Apply();
            File.WriteAllBytes(Path.Combine(directory,name+".png"),png.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(png); RenderTexture.active=previous; return texture;
        }
        private static float Sample(Texture2D texture,Vector3 point)
        {
            var screen=camera.WorldToViewportPoint(point);
            return texture.GetPixel(Mathf.RoundToInt(screen.x*511),Mathf.RoundToInt(screen.y*383)).r;
        }
        private static void Require(bool condition,string message)
        {
            if(!condition) throw new InvalidOperationException(message);
        }
    }
}
