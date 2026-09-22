using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class SnowNativeMaterialVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static object Get(object o,string n)=>o.GetType().GetField(n,All).GetValue(o);
        static void Set(object o,string n,object v)=>o.GetType().GetField(n,All).SetValue(o,v);
        static object Call(object o,string n,params object[] a)=>o.GetType().GetMethod(n,All).Invoke(o,a);
        static int Count(object o,string n)=>(int)o.GetType().GetProperty(n,All).GetValue(o,null);
        static void Require(bool ok,string message){if(!ok)throw new Exception(message);}
        static bool source;
        static bool automatic;
        public static void RunAutomaticInstancing(){automatic=true;Run();}
        public static void RunSource(){source=true;Run();}
        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons"),game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,args)=>{
                foreach(var dir in new[]{runtime,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var file=Path.Combine(dir,new AssemblyName(args.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            int result=0;
            try
            {
                var mod=Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll"));
                var type=typeof(SnowYardBatchVerification).GetNestedType("Fixture",All);
                using(var fixture=(IDisposable)Activator.CreateInstance(type,All,null,new object[]{mod,runtime,128},null))Verify(fixture);
            }
            catch(Exception e){Debug.LogException(e);result=1;}
            EditorApplication.Exit(result);
        }
        static void Verify(object fixture)
        {
            var registry=Get(fixture,"registry");var repository=Get(fixture,"repository");
            var vehicles=(IList)Get(fixture,"vehicles");var camera=(Camera)Get(fixture,"camera");
            var native=Get(registry,"nativeMaterials");
            var testTextures=new List<Texture2D>();
            // Start with untouched Standard materials and actual game DLL data.
            foreach(var v in vehicles)Call(native,"Release",v);
            var sourceMaterials=new HashSet<Material>();
            foreach(var v in vehicles)foreach(var part in (IList)Get(v,"Parts"))
                foreach(var m in ((Renderer)Get(part,"Renderer")).sharedMaterials)sourceMaterials.Add(m);
            var normalMap=new Texture2D(4,4,TextureFormat.RGBA32,false,true);testTextures.Add(normalMap);
            var detailMap=new Texture2D(4,4,TextureFormat.RGBA32,false,true);testTextures.Add(detailMap);
            var metalMap=new Texture2D(4,4,TextureFormat.RGBA32,false,true);testTextures.Add(metalMap);
            var normalPixels=new Color[16];var detailPixels=new Color[16];var metalPixels=new Color[16];
            for(int p=0;p<16;p++){normalPixels[p]=new Color(.5f,.58f,1,.6f);detailPixels[p]=new Color(.44f,.51f,.48f,1);metalPixels[p]=new Color(.23f,0,0,.37f);}
            normalMap.SetPixels(normalPixels);normalMap.Apply();detailMap.SetPixels(detailPixels);detailMap.Apply();metalMap.SetPixels(metalPixels);metalMap.Apply();
            foreach(var m in sourceMaterials)
            {
                m.enableInstancing=!automatic; m.SetTexture("_BumpMap",normalMap);m.EnableKeyword("_NORMALMAP");
                m.SetTexture("_DetailAlbedoMap",detailMap);m.EnableKeyword("_DETAIL_MULX2");
                m.SetTexture("_MetallicGlossMap",metalMap);m.EnableKeyword("_METALLICGLOSSMAP");
            }
            var controlled=((Transform)Get(vehicles[7],"Root")).GetComponentInChildren<MeshRenderer>();
            var controllerProperties=new MaterialPropertyBlock();controllerProperties.SetColor("_Color",new Color(.3f,.5f,.7f,1));
            controllerProperties.SetFloat("_Glossiness",0);controlled.SetPropertyBlock(controllerProperties);
            var indexed=((Transform)Get(vehicles[9],"Root")).GetComponentInChildren<MeshRenderer>();
            var indexedProperties=new MaterialPropertyBlock();indexedProperties.SetColor("_Color",new Color(.7f,.3f,.2f,1));
            indexed.SetPropertyBlock(indexedProperties,0);
            var targets=new RenderTexture[4];var cb=new CommandBuffer{name="Native Standard GBuffer parity"};
            var read=new Texture2D(800,600,TextureFormat.RGBAFloat,false,true);
            
            var noise=(Texture2D)modNoise(registry.GetType().Assembly);
            try
            {
                for(int i=0;i<4;i++){targets[i]=new RenderTexture(800,600,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);targets[i].Create();}
                var buffers=new[]{BuiltinRenderTextureType.GBuffer0,BuiltinRenderTextureType.GBuffer1,BuiltinRenderTextureType.GBuffer2,BuiltinRenderTextureType.CameraTarget};
                for(int i=0;i<4;i++)cb.Blit(buffers[i],targets[i]);
                
                
                camera.AddCommandBuffer(CameraEvent.BeforeLighting,cb);
                Call(fixture,"Record",true);
                camera.Render();camera.Render();var original=Read(camera,targets,read);
                camera.RemoveCommandBuffer(CameraEvent.BeforeLighting,cb);
                Call(fixture,"Time",true,12,true);
                double oldRender=(double)Call(fixture,"Time",true,120,true),oldCpu=(double)Call(fixture,"Time",true,180,false);
                int oldDraws=Count(registry,"FrameDrawCount");
                camera.AddCommandBuffer(CameraEvent.BeforeLighting,cb);
                var fill=new CommandBuffer();
                foreach(var v in vehicles)
                {
                    int slot=(int)Get(v,"Slot");var area=new Vector4(0,0,2,2);Set(v,"Area",area);Set(v,"SnowArea",area);
                    fill.SetRenderTarget((RenderTexture)Get(registry,"heights"),0,CubemapFace.Unknown,slot);fill.ClearRenderTarget(false,true,Color.clear);
                    fill.SetRenderTarget((RenderTexture)Get(registry,"snow"),0,CubemapFace.Unknown,slot);fill.ClearRenderTarget(false,true,Color.white);
                }
                Graphics.ExecuteCommandBuffer(fill);fill.Dispose();
                Set(native,"Enabled",true);
                if(source)Set(native,"shader",AssetDatabase.LoadAssetAtPath<Shader>("Assets/DVSeasons/DV99/Shaders/SnowVehicleStandard.shader"));
                Require((bool)Call(native,"Initialize",repository),"Native shader unavailable");
                foreach(var v in vehicles)
                {
                    Call(native,"Bind",v);Call(Get(v,"PartCache"),"Build",Get(v,"Parts"));
                    bool all=true;foreach(var part in (IList)Get(v,"Parts"))if((bool)Get(part,"HasOpaque") && !(bool)Get(part,"NativeComplete"))all=false;
                    Set(v,"NativeComplete",all);
                }
                Call(registry,"PrepareNativeSnow",0f,0f,noise,Color.gray);
                Call(fixture,"Record",true);var noSnow=Read(camera,targets,read);
                var variants=new HashSet<Material>();
                foreach(var v in vehicles)foreach(var part in (IList)Get(v,"Parts"))
                    foreach(var m in ((Renderer)Get(part,"Renderer")).sharedMaterials)variants.Add(m);
                var sharedCount=native.GetType().GetProperty("SharedSlots",All);
                Debug.Log("NATIVE_SHARING cars=128 automatic="+automatic+" materials="+variants.Count+" shared_slots="+(sharedCount!=null?sharedCount.GetValue(native,null):"unavailable"));
                if(automatic && sharedCount!=null)
                {
                    Require(variants.Count<16,"Ordinary Standard materials still have one snow variant per car");
                    foreach(var m in sourceMaterials)Require(!m.enableInstancing,"Original Standard instancing flag was modified");
                    Require((int)sharedCount.GetValue(native,null)>800,"Compatible parts did not share native materials");
                }
                Require(Count(registry,"NativeMaterialSlots")>800,"Most standard slots did not bind");
                int marked=0;float max=0;int changed=0;var perChannel=new int[4];
                for(int i=0;i<noSnow[0].Length;i++)
                {
                    if(original[2][i].a>.5f && Mathf.Abs(noSnow[2][i].a-1f/3f)<.1f)marked++;
                    for(int channel=0;channel<4;channel++)
                    {
                        var d=original[channel][i]-noSnow[channel][i];if(channel==2)d.a=0;float e=Mathf.Max(Mathf.Abs(d.r),Mathf.Max(Mathf.Abs(d.g),Mathf.Max(Mathf.Abs(d.b),Mathf.Abs(d.a))));
                        max=Mathf.Max(max,e);if(e>.002f){changed++;perChannel[channel]++;if(perChannel[channel]<=3)Debug.Log("NATIVE_DIFF_SAMPLE channel="+channel+" pixel="+i+" before="+original[channel][i]+" after="+noSnow[channel][i]);}
                    }
                }
                Debug.Log("NATIVE_STANDARD_ZERO: slots="+Count(registry,"NativeMaterialSlots")+" remaining_surface_draws="+Count(registry,"FrameDrawCount")+" gbuffer_changes="+changed+" channels="+string.Join(",",perChannel)+" max="+max+" marker_pixels="+marked);
                Require(changed==0,"Native snow changed Standard's dry GBuffer");Require(marked>10000,"Native exclusion marker not written");
                Call(registry,"PrepareNativeSnow",1f,0f,noise,Color.gray);var snowy=Read(camera,targets,read);
                int bright=Brighter(snowy,noSnow);
                Debug.Log("NATIVE_STANDARD_SNOW: brighter_pixels="+bright);Require(bright>3000,"Native snow did not accumulate");
                Set(registry,"SnowRemaining",new Func<Component,float>(c=>0));
                Call(registry,"PrepareNativeSnow",1f,0f,noise,Color.gray);
                Require(Brighter(Read(camera,targets,read),noSnow)==0,"Melted roof retained snow");
                Set(registry,"SnowRemaining",new Func<Component,float>(c=>1));
                foreach(var v in vehicles)Set(v,"RollingStock",true);
                var limiter=Get(registry,"ObjectLimiter");Call(limiter,"InvalidateMembership");
                limiter.GetType().GetProperty("Limit",All).SetValue(limiter,35,null);Call(limiter,"Update",camera,Get(registry,"vehicles"));
                Call(registry,"PrepareNativeSnow",1f,0f,noise,Color.gray);var limited=Read(camera,targets,read);int limitedBright=Brighter(limited,noSnow);
                Require(limitedBright>500 && limitedBright<bright*.5f,"Native material ignored car limit");
                limiter.GetType().GetProperty("Limit",All).SetValue(limiter,0,null);
                var meshNormals=new Dictionary<Mesh,Vector3[]>();
                foreach(var v in vehicles)foreach(var part in (IList)Get(v,"Parts"))
                {
                    var mesh=(Mesh)Get(part,"Mesh");var renderer=(Renderer)Get(part,"Renderer");
                    if(mesh==null || !mesh.isReadable || renderer.isPartOfStaticBatch || meshNormals.ContainsKey(mesh))continue;
                    meshNormals.Add(mesh,mesh.normals);var side=new Vector3[mesh.vertexCount];for(int p=0;p<side.Length;p++)side[p]=Vector3.right;mesh.normals=side;
                }
                // Isolate directional accumulation from the roof and the game's
                // independent host-side thermal/wind state integrator.
                Call(registry,"PrepareNativeSnow",0f,0f,noise,Color.gray);var sideDry=Read(camera,targets,read);
                Set(registry,"SnowRemaining",new Func<Component,float>(c=>0));
                Set(registry,"SideSnowAmount",new Func<Component,Vector4>(c=>new Vector4(1,0,0,0)));
                Call(registry,"PrepareNativeSnow",1f,0f,noise,Color.gray);int windy=Brighter(Read(camera,targets,read),sideDry);
                Set(registry,"SideSnowAmount",new Func<Component,Vector4>(c=>new Vector4(0,1,0,0)));
                Call(registry,"PrepareNativeSnow",1f,0f,noise,Color.gray);int lee=Brighter(Read(camera,targets,read),sideDry);
                Require(windy>1000 && lee<windy/10,"Native side snow ignored directional state: "+windy+" / "+lee);
                foreach(var item in meshNormals)item.Key.normals=item.Value;
                Set(registry,"SnowRemaining",new Func<Component,float>(c=>1));Set(registry,"SideSnowAmount",new Func<Component,Vector4>(c=>Vector4.zero));
                Call(registry,"PrepareNativeSnow",1f,0f,noise,Color.gray);
                Debug.Log("NATIVE_THERMAL_LIMIT_WIND_OK: roof melt to zero; limited35_pixels="+limitedBright+" wind_side="+windy+" lee_side="+lee);
                // Native submission contains no vehicle surface geometry. Measure
                // the real camera, not just an empty command recorder.
                camera.RemoveCommandBuffer(CameraEvent.BeforeLighting,cb);
                TimeNative(fixture,registry,noise,camera,12,true);
                double render=TimeNative(fixture,registry,noise,camera,120,true),cpu=TimeNative(fixture,registry,noise,camera,180,false);
                Debug.Log("NATIVE_STANDARD_BENCH cars=128 native_snow=1 render_ms="+oldRender.ToString("F3")+"->"+render.ToString("F3")+" record_ms="+oldCpu.ToString("F3")+"->"+cpu.ToString("F3")+" surface_draws="+oldDraws+"->"+Count(registry,"FrameDrawCount")+"; comparison=legacy_surface_geometry_vs_native_shaded_geometry; not_game_FPS=true");
                camera.AddCommandBuffer(CameraEvent.BeforeLighting,cb);
                foreach(var v in vehicles)Call(native,"Release",v);
                var restored=Read(camera,targets,read);int restoration=0;
                for(int i=0;i<restored[0].Length;i++)if(restored[0][i]!=original[0][i])restoration++;
                Require(restoration==0,"Native materials were not restored exactly");
                if(sharedCount!=null)
                {
                    Require(Count(native,"SharedSlots")==0 && Count(native,"VariantCount")==0,"Native sharing counters or variants leaked on release");
                }
                controlled.GetPropertyBlock(controllerProperties);
                Require(controllerProperties.GetColor("_Color")==new Color(.3f,.5f,.7f,1),"Existing controller property block changed");
                indexed.GetPropertyBlock(indexedProperties,0);
                Require(indexedProperties.GetColor("_Color")==new Color(.7f,.3f,.2f,1),"Indexed material override was discarded");
                var car=vehicles[2];var paintRenderer=((Transform)Get(car,"Root")).GetComponentInChildren<MeshRenderer>();
                var sourceMaterial=paintRenderer.sharedMaterial;
                Call(native,"Bind",car);
                var liveBlock=new MaterialPropertyBlock();paintRenderer.GetPropertyBlock(liveBlock);
                liveBlock.SetFloat("_Glossiness",0);liveBlock.SetColor("_Color",Color.magenta);paintRenderer.SetPropertyBlock(liveBlock);
                var newPaint=new Color(.31f,.25f,.44f,1);sourceMaterial.color=newPaint;
                Call(registry,"PrepareNativeSnow",0f,0f,noise,Color.gray);
                Require(paintRenderer.sharedMaterial.color==newPaint,"Original material mutation was not propagated");
                var owned=paintRenderer.sharedMaterial;owned.color=Color.green;Call(native,"Release",car);
                var restoredPaint=paintRenderer.sharedMaterial;
                Require(restoredPaint!=sourceMaterial && restoredPaint.shader.name=="Standard" && restoredPaint.color==Color.green,"Direct live repaint was discarded on release");
                paintRenderer.GetPropertyBlock(liveBlock);
                Require(liveBlock.GetColor("_Color")==Color.magenta && liveBlock.GetFloat("_Glossiness")==0,"Live controller property block was discarded");
                Call(native,"Bind",car);var external=new Material(paintRenderer.sharedMaterial);paintRenderer.sharedMaterial=external;
                external.color=Color.blue;Call(native,"Release",car);
                Require(paintRenderer.sharedMaterial==external && external.shader.name=="Standard" && external.color==Color.blue,"External paintRenderer.material clone lost its properties");
                paintRenderer.sharedMaterial=sourceMaterial;UnityEngine.Object.DestroyImmediate(external);UnityEngine.Object.DestroyImmediate(restoredPaint);
                Debug.Log("NATIVE_MATERIAL_OWNERSHIP_OK: original updates, live repaint and external material clones survive refresh/release.");
                Debug.Log("SNOW_NATIVE_STANDARD_OK: zero-snow GBuffer parity, original alpha cutouts/normal/detail/lighting, GBuffer exclusion, roof heat, directional sides, car limit, real native snow, and exact restoration.");
            }
            finally
            {
                camera.RemoveCommandBuffer(CameraEvent.BeforeLighting,cb);cb.Dispose();
                foreach(var target in targets)if(target!=null)UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(read);UnityEngine.Object.DestroyImmediate(noise);
                foreach(var texture in testTextures)UnityEngine.Object.DestroyImmediate(texture);
            }
        }
        static object modNoise(Assembly assembly)=>assembly.GetType("DVSeasons.Mod.SnowCoveragePattern").GetMethod("CreateTexture",All).Invoke(null,null);
        static int Brighter(Color[][] snow,Color[][] dry)
        {int count=0;for(int i=0;i<snow[0].Length;i++)if(snow[0][i].r>dry[0][i].r+.1f)count++;return count;}
        static double TimeNative(object fixture,object registry,Texture noise,Camera camera,int count,bool render)
        {
            var prepare=(Action<float,float,Texture,Color>)Delegate.CreateDelegate(typeof(Action<float,float,Texture,Color>),registry,registry.GetType().GetMethod("PrepareNativeSnow",All));
            var record=(Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>),fixture,fixture.GetType().GetMethod("Record",All));
            var drain=(Action)Delegate.CreateDelegate(typeof(Action),fixture,fixture.GetType().GetMethod("Drain",All));
            drain();var watch=System.Diagnostics.Stopwatch.StartNew();
            for(int i=0;i<count;i++){prepare(1,0,noise,Color.gray);record(true);if(render)camera.Render();}
            if(render)drain();watch.Stop();return watch.Elapsed.TotalMilliseconds/count;
        }
        static Color[][] Read(Camera camera,RenderTexture[] targets,Texture2D read)
        {
            camera.Render();var result=new Color[targets.Length][];
            for(int i=0;i<targets.Length;i++){RenderTexture.active=targets[i];read.ReadPixels(new Rect(0,0,800,600),0,0);read.Apply();result[i]=read.GetPixels();}
            RenderTexture.active=null;return result;
        }
    }
}
