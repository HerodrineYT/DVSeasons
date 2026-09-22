using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DVSeasons.AssetBundleBuild
{
    public static class TerrainDiscoveryVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static object Call(object target,string method,params object[] args) {return target.GetType().GetMethod(method,All).Invoke(target,args);}
        static object Get(object target,string field) {return target.GetType().GetField(field,All).GetValue(target);}
        static int Count(object target,string property) {return (int)target.GetType().GetProperty(property,All).GetValue(target,null);}
        static void Require(bool value,string message) {if(!value)throw new Exception(message);}

        public static void Run()
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string modPath=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            string game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=> {
                foreach(var dir in new[]{modPath,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var file=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            int result=0;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                var mod=Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll"));
                var repository=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true),new object[]{modPath});
                var type=mod.GetType("DVSeasons.Mod.MicroSplatSeasonalTerrainController",true);
                var controller=Activator.CreateInstance(type,new[]{repository});
                var materials=new List<Material>();var objects=new List<GameObject>();
                var clear=Source(false);var winter=Source(true);
                try
                {
                    VerifyTerrainIdentity(repository,clear,winter);
                    VerifyDiscovery(controller,clear,materials,objects);
                    VerifyNativeLandscape(controller,clear,materials,objects);
                    VerifyScanBufferLifecycle(controller,clear,materials,objects);
                    Require(((IDictionary)Get(repository,"loadedTerrainArrays")).Count==0,"Terrain discovery loaded seasonal arrays just to classify vanilla materials");
                    Require(Get(repository,"bundleLoadRequest")==null && Get(repository,"winterBundleLoadRequest")==null && Get(repository,"tracksBundleLoadRequest")==null,"Terrain discovery started bundle I/O just to classify vanilla materials");
                    VerifyLayerCopies(type,controller,clear,winter);
                    Debug.Log("TERRAIN_DISCOVERY_OK: identity/discovery without bundle I/O; bounded 3000-node and 30000-node scenes; inactive/private native terrain and late replacements found without global snapshots or recursive subtree queries; streamed scene priority; grid refresh restarts bounded discovery; pooled scan buffers exclusive to each live iterator, cleared/reused after completion/disposal/unload, released on reset; staged arrays use at most two slice copies per frame; unpublished until all slices/mips complete; retargeted build and thaw preserve exact source/order/alpha.");
                }
                finally
                {
                    ((IDisposable)controller).Dispose();((IDisposable)repository).Dispose();
                    foreach(var o in objects)if(o!=null)UnityEngine.Object.DestroyImmediate(o);
                    foreach(var m in materials)if(m!=null)UnityEngine.Object.DestroyImmediate(m);
                    UnityEngine.Object.DestroyImmediate(clear);UnityEngine.Object.DestroyImmediate(winter);
                }
            }
            catch(Exception e) {Debug.LogException(e);result=1;}
            EditorApplication.Exit(result);
        }

        static Material MaterialFor(Texture source,List<Material> materials,string name="MicroSplat discovery fixture")
        {var m=new Material(Shader.Find("Hidden/DVSeasons/Tests/MicroSplatClone")){name=name};m.SetTexture("_Diffuse",source);materials.Add(m);return m;}

        static GameObject RendererFor(Material material,List<GameObject> objects)
        {var o=new GameObject("Terrain discovery renderer");objects.Add(o);o.AddComponent<MeshRenderer>().sharedMaterial=material;return o;}

        static void VerifyTerrainIdentity(object repository,Texture2DArray clear,Texture2DArray winter)
        {
            var cache=(IDictionary)Get(repository,"loadedTerrainArrays");
            Require(cache.Count==0,"Terrain identity fixture needs a fresh repository");
            Require(!(bool)Call(repository,"IsTerrainArray",clear),"Vanilla terrain array was identified as seasonal");
            Require(Get(repository,"bundleLoadRequest")==null && Get(repository,"winterBundleLoadRequest")==null && Get(repository,"tracksBundleLoadRequest")==null,"Texture identity check unexpectedly started bundle I/O");
            var season=Enum.Parse(cache.GetType().GetGenericArguments()[0],"Winter");
            try
            {
                cache.Add(season,winter);
                Require((bool)Call(repository,"IsTerrainArray",winter),"Previously loaded seasonal array needs unrelated bundle readiness for identification");
                Require(!(bool)Call(repository,"IsTerrainArray",clear),"Unrelated terrain array matched a loaded seasonal array");
                cache[season]=null;
                Require(!(bool)Call(repository,"IsTerrainArray",winter),"Empty cache entry was treated as a loaded seasonal array");
            }
            finally {cache.Clear();}
        }

        static void VerifyDiscovery(object controller,Texture2DArray source,List<Material> materials,List<GameObject> objects)
        {
            var root=new GameObject("Large scene fixture");objects.Add(root);
            for(int i=0;i<3000;i++)new GameObject("Empty node "+i).transform.SetParent(root.transform);
            for(int i=0;i<100;i++)
            {
                var material=MaterialFor(source,materials);
                var o=RendererFor(material,objects);o.transform.SetParent(root.transform);
                if(i%3==0)o.SetActive(false);
                // Shared materials must be inspected only once per scene pass.
                var duplicate=RendererFor(material,objects);duplicate.transform.SetParent(root.transform);
            }
            var custom=MaterialFor(source,materials,"Unrelated custom map");
            custom.shader=Shader.Find("Standard");
            RendererFor(custom,objects).transform.SetParent(root.transform);
            Call(controller,"QueueScene",root.scene,false);
            Call(controller,"AdvanceDiscovery");
            var fixtureScenePath="Assets/TerrainDiscoveryFixture-"+Guid.NewGuid().ToString("N")+".unity";
            Require(EditorSceneManager.SaveScene(root.scene,fixtureScenePath),"Could not save temporary terrain discovery scene");
            var priority=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            var priorityMaterial=MaterialFor(source,materials);
            var priorityObject=RendererFor(priorityMaterial,objects);SceneManager.MoveGameObjectToScene(priorityObject,priority);
            Call(controller,"QueueScene",priority,true);
            var bindings=(IDictionary)Get(controller,"bindings");
            int frames=0;
            while(!bindings.Contains(priorityMaterial.GetInstanceID()+"|_Diffuse") && frames++<12)Call(controller,"AdvanceDiscovery");
            Require(bindings.Contains(priorityMaterial.GetInstanceID()+"|_Diffuse"),"Streamed scene waited behind the existing hierarchy");
            Drain(controller);
            foreach(var material in materials)
                Require(bindings.Contains(material.GetInstanceID()+"|_Diffuse")==(material!=custom),"Missing inactive terrain material or modified unrelated custom map: "+material.name);
            Require(bindings.Count==101,"Shared renderers produced duplicate or missing bindings");
            var replacement=MaterialFor(source,materials);
            priorityObject.GetComponent<Renderer>().sharedMaterial=replacement;
            Call(controller,"QueueScene",priority,false);Drain(controller);
            Require(bindings.Contains(replacement.GetInstanceID()+"|_Diffuse"),"Existing-scene material replacement was never discovered");
            Call(controller,"AdvanceDiscovery");Require(Count(controller,"LastDiscoverySteps")==0,"Finished discovery continues walking every frame");
            EditorSceneManager.CloseScene(priority,true);
            AssetDatabase.DeleteAsset(fixtureScenePath);
        }

        static void Drain(object controller)
        {
            int frames=0;
            do
            {
                Call(controller,"AdvanceDiscovery");
                Require(Count(controller,"LastDiscoverySteps")<=256,"Discovery exceeded its per-frame item limit");
                Require(++frames<20000,"Terrain discovery never completed");
            }while(Count(controller,"LastDiscoverySteps")!=0);
        }

        static int CollectionCount(object collection)
        {return (int)collection.GetType().GetProperty("Count").GetValue(collection,null);}
        static void BuffersEmpty(object buffers)
        {
            foreach(var name in new[]{"Roots","Pending","SeenMaterials"})
                Require(CollectionCount(Get(buffers,name))==0,"Released scan retained "+name);
        }
        static object IteratorBuffers(IEnumerator<int> iterator)
        {
            foreach(var field in iterator.GetType().GetFields(All))
                if(field.FieldType.Name=="SceneScanBuffers")return field.GetValue(iterator);
            throw new Exception("Scene iterator buffer ownership was unavailable");
        }
        static void VerifyScanBufferLifecycle(object controller,Texture2DArray source,List<Material> materials,List<GameObject> objects)
        {
            Drain(controller);
            var pool=Get(controller,"sceneScanBuffers");
            foreach(var buffers in (IEnumerable)pool)BuffersEmpty(buffers);
            Require(CollectionCount(pool)>0 && CollectionCount(pool)<=4,"Completed discovery did not retain bounded reusable storage");
            var scene=SceneManager.GetActiveScene();
            var reuse=Call(pool,"Peek");
            var first=((IEnumerable<int>)Call(controller,"ScanScene",scene)).GetEnumerator();
            var second=((IEnumerable<int>)Call(controller,"ScanScene",scene)).GetEnumerator();
            object firstBuffers,secondBuffers;
            try
            {
                Require(first.MoveNext(),"First scene walk did not start");firstBuffers=IteratorBuffers(first);
                Require(ReferenceEquals(firstBuffers,reuse),"Repeated scene walk allocated replacement storage");
                Require(second.MoveNext(),"Priority scene walk did not start");secondBuffers=IteratorBuffers(second);
                Require(!ReferenceEquals(firstBuffers,secondBuffers),"Two suspended walks share mutable scan buffers");
                Call(Get(firstBuffers,"SeenMaterials"),"Add",int.MinValue);
                int roots=CollectionCount(Get(firstBuffers,"Roots"));
                second.Dispose();BuffersEmpty(secondBuffers);
                Require(CollectionCount(Get(firstBuffers,"Roots"))==roots && CollectionCount(Get(firstBuffers,"SeenMaterials"))==1,"Disposing priority walk cleared the suspended walk");
                first.Dispose();BuffersEmpty(firstBuffers);
                var next=((IEnumerable<int>)Call(controller,"ScanScene",scene)).GetEnumerator();
                try
                {
                    Require(next.MoveNext(),"Reused scene walk did not restart");
                    Require(ReferenceEquals(IteratorBuffers(next),firstBuffers),"Cancelled walk storage was not reused");
                    Require(CollectionCount(Get(firstBuffers,"SeenMaterials"))==0,"New walk inherited material deduplication state");
                }
                finally {next.Dispose();}
            }
            finally {first.Dispose();second.Dispose();}

            // Exercise the real queued cancellation paths, including a walk that
            // already owns buffers when its scene disappears.
            var saved="Assets/TerrainScanBufferFixture-"+Guid.NewGuid().ToString("N")+".unity";
            Require(EditorSceneManager.SaveScene(scene,saved),"Could not save buffer lifecycle scene");
            var streamed=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            try
            {
                var root=new GameObject("Pooled scan cancellation fixture");objects.Add(root);SceneManager.MoveGameObjectToScene(root,streamed);
                for(int i=0;i<1024;i++)new GameObject("Pending child "+i).transform.SetParent(root.transform);
                var material=MaterialFor(source,materials);
                var renderer=RendererFor(material,objects);renderer.transform.SetParent(root.transform);
                Call(controller,"QueueScene",streamed,true);Call(controller,"AdvanceDiscovery");
                Call(controller,"SceneUnloaded",streamed);
                Require(CollectionCount(Get(controller,"pendingScenes"))==0 && CollectionCount(Get(controller,"queuedScenes"))==0,"Scene unload retained queued iterator");
                foreach(var buffers in (IEnumerable)pool)BuffersEmpty(buffers);
                Call(controller,"QueueScene",streamed,true);Drain(controller);
                Require(((IDictionary)Get(controller,"bindings")).Contains(material.GetInstanceID()+"|_Diffuse"),"Fresh scan after cancellation lost material discovery");
                Call(controller,"QueueScene",streamed,true);Call(controller,"AdvanceDiscovery");
                Call(controller,"StopDiscovery");
                Require(CollectionCount(pool)==0 && CollectionCount(Get(controller,"pendingScenes"))==0,"Reset retained pooled scene buffers");
            }
            finally
            {
                EditorSceneManager.CloseScene(streamed,true);SceneManager.SetActiveScene(scene);AssetDatabase.DeleteAsset(saved);
            }
        }

        static void VerifyNativeLandscape(object controller,Texture2DArray source,List<Material> materials,List<GameObject> objects)
        {
            var distantType=Type.GetType("DV.WorldTools.DistantTerrain, DV.DistantTerrain",true);
            var microType=Type.GetType("JBooth.MicroSplat.MicroSplatTerrain, JBooth.MicroSplat.Core",true);
            var root=new GameObject("Very large landscape discovery fixture");objects.Add(root);
            for(int i=0;i<30000;i++)new GameObject("Unrelated world node "+i).transform.SetParent(root.transform);
            var distantRoot=new GameObject("Native distant rings");objects.Add(distantRoot);
            distantRoot.transform.SetParent(root.transform);distantRoot.SetActive(false);
            var distant=distantRoot.AddComponent(distantType);
            var distantMaterial=new Material(Shader.Find("DV/DistantTerrain"));
            distantMaterial.SetTexture("_Splats",source);materials.Add(distantMaterial);
            var ring=RendererFor(distantMaterial,objects);ring.transform.SetParent(distantRoot.transform);
            var cachedDistant=new Material(distantMaterial);materials.Add(cachedDistant);
            ((IList)distantType.GetField("materials",All).GetValue(distant)).Add(cachedDistant);

            var microRoot=new GameObject("Native terrain outside view");objects.Add(microRoot);
            microRoot.transform.SetParent(root.transform);microRoot.SetActive(false);
            microRoot.AddComponent<Terrain>();
            var micro=microRoot.AddComponent(microType);
            var template=MaterialFor(source,materials);var hiddenInstance=MaterialFor(source,materials);
            microType.GetField("templateMaterial",All).SetValue(micro,template);
            microType.GetField("matInstance",All).SetValue(micro,hiddenInstance);
            var instances=(IList)microType.GetField("sInstances",All).GetValue(null);
            instances.Add(micro);
            try
            {
                var bindings=(IDictionary)Get(controller,"bindings");
                controller.GetType().GetField("includeDistantMaterials",All).SetValue(controller,true);
                Call(controller,"QueueScene",root.scene,false);Call(controller,"AdvanceDiscovery");
                Require(!bindings.Contains(distantMaterial.GetInstanceID()+"|_Splats"),"Large-scene fixture did not create a discovery backlog");
                Call(controller,"ScanNativeLandscapeMaterials");
                Require(!bindings.Contains(distantMaterial.GetInstanceID()+"|_Splats"),"Native refresh synchronously walked a 30000-node hierarchy");
                Require(Count(controller,"NativeSourceSnapshotCount")==0,"Startup took a global native source snapshot");
                Require(bindings.Contains(template.GetInstanceID()+"|_Diffuse") && bindings.Contains(hiddenInstance.GetInstanceID()+"|_Diffuse"),"Native visibility-hidden MicroSplat instance/template was ignored");
                Drain(controller);Call(controller,"ScanNativeLandscapeMaterials");
                Require(bindings.Contains(distantMaterial.GetInstanceID()+"|_Splats"),"Bounded discovery lost inactive distant ring");
                Require(bindings.Contains(cachedDistant.GetInstanceID()+"|_Splats"),"Native distant material cache was ignored");
                int snapshots=Count(controller,"NativeSourceSnapshotCount");
                var replacement=new Material(distantMaterial);materials.Add(replacement);
                ring.GetComponent<Renderer>().sharedMaterial=replacement;
                var replacedHidden=MaterialFor(source,materials);
                microType.GetField("matInstance",All).SetValue(micro,replacedHidden);
                Call(controller,"ScanNativeLandscapeMaterials");
                Require(bindings.Contains(replacement.GetInstanceID()+"|_Splats") && bindings.Contains(replacedHidden.GetInstanceID()+"|_Diffuse"),"Cached native sources missed replacement material instances");
                Require(Count(controller,"NativeSourceSnapshotCount")==snapshots,"Normal native refresh repeated a global object snapshot");
                VerifyStreamedNativeScenes(controller,source,materials,objects,distantType,bindings,snapshots);
                Drain(controller);
                // This object appears after both scene load and the last native
                // grid refresh. Its private-only material cannot be discovered
                // by merely visiting Renderer.sharedMaterials.
                var lateRoot=new GameObject("Late native component in an existing scene");objects.Add(lateRoot);
                lateRoot.SetActive(false);lateRoot.transform.SetParent(root.transform);
                var lateSource=lateRoot.AddComponent(distantType);
                var lateMaterial=new Material(distantMaterial);materials.Add(lateMaterial);
                ((IList)distantType.GetField("materials",All).GetValue(lateSource)).Add(lateMaterial);
                int beforeLate=Count(controller,"NativeSourceSnapshotCount");
                Call(controller,"QueueScene",root.scene,false);Drain(controller);
                Call(controller,"ScanNativeLandscapeMaterials");
                Require(bindings.Contains(lateMaterial.GetInstanceID()+"|_Splats"),"Bounded fallback lost the private materials of a late native component");
                Require(Count(controller,"NativeSourceSnapshotCount")==beforeLate,"Late-source fallback repeated a global source snapshot");
            }
            finally {instances.Remove(micro);}
        }

        static void VerifyStreamedNativeScenes(object controller,Texture2DArray source,List<Material> materials,List<GameObject> objects,
            Type distantType,IDictionary bindings,int snapshots)
        {
            var originalScene=SceneManager.GetActiveScene();
            var scenePath="Assets/TerrainNativeDiscoveryFixture-"+Guid.NewGuid().ToString("N")+".unity";
            Require(EditorSceneManager.SaveScene(originalScene,scenePath),"Could not save native discovery base scene");
            var unrelated=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            var unrelatedPath="Assets/TerrainNativeUnrelatedFixture-"+Guid.NewGuid().ToString("N")+".unity";
            Require(EditorSceneManager.SaveScene(unrelated,unrelatedPath),"Could not save unrelated discovery scene");
            var streamed=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            bool unrelatedOpen=true;
            try
            {
                var clutter=new GameObject("New unrelated streamed scene");objects.Add(clutter);
                SceneManager.MoveGameObjectToScene(clutter,unrelated);
                for(int i=0;i<512;i++)new GameObject("Empty streamed node "+i).transform.SetParent(clutter.transform);
                int localScans=Count(controller,"NativeSceneScanCount");
                for(int i=0;i<8;i++)Call(controller,"SceneLoaded",unrelated,LoadSceneMode.Additive);
                Call(controller,"ScanNativeLandscapeMaterials");
                Require(Count(controller,"NativeSourceSnapshotCount")==snapshots,"Unrelated scene repeated global terrain snapshot");
                Require(Count(controller,"NativeSceneScanCount")==localScans,"Native refresh performed synchronous scene discovery");
                Drain(controller);
                Require(Count(controller,"NativeSceneScanCount")==localScans+1,"Repeated pending scene notifications were not coalesced");
                Call(controller,"ScanNativeLandscapeMaterials");
                Require(Count(controller,"NativeSceneScanCount")==localScans+1,"Unchanged native refresh rescanned a scene hierarchy");

                var root=new GameObject("New inactive distant rings");objects.Add(root);root.SetActive(false);
                SceneManager.MoveGameObjectToScene(root,streamed);
                var native=root.AddComponent(distantType);
                var material=new Material(Shader.Find("DV/DistantTerrain"));materials.Add(material);material.SetTexture("_Splats",source);
                var ring=RendererFor(material,objects);ring.transform.SetParent(root.transform);
                var cached=new Material(material);materials.Add(cached);
                ((IList)distantType.GetField("materials",All).GetValue(native)).Add(cached);
                Call(controller,"SceneLoaded",streamed,LoadSceneMode.Additive);
                Drain(controller);
                Call(controller,"ScanNativeLandscapeMaterials");
                Require(bindings.Contains(material.GetInstanceID()+"|_Splats") && bindings.Contains(cached.GetInstanceID()+"|_Splats"),"New distant scene waited for general discovery or lost native material cache");
                Require(Count(controller,"NativeSourceSnapshotCount")==snapshots,"New distant scene repeated global terrain snapshot");

                Call(controller,"SceneLoaded",unrelated,LoadSceneMode.Additive);
                EditorSceneManager.CloseScene(unrelated,true);unrelatedOpen=false;
                localScans=Count(controller,"NativeSceneScanCount");
                Call(controller,"ScanNativeLandscapeMaterials");
                Require(Count(controller,"NativeSceneScanCount")==localScans,"Unloaded queued scene was scanned");
                Call(controller,"NativeTerrainInitialized");Call(controller,"ScanNativeLandscapeMaterials");
                Require(Count(controller,"NativeSourceSnapshotCount")==snapshots,"New native grid repeated a global source snapshot");
                Drain(controller);Call(controller,"ScanNativeLandscapeMaterials");
                Require(bindings.Contains(cached.GetInstanceID()+"|_Splats"),"Grid refresh lost native private materials");
            }
            finally
            {
                if(unrelatedOpen)EditorSceneManager.CloseScene(unrelated,true);
                EditorSceneManager.CloseScene(streamed,true);
                SceneManager.SetActiveScene(originalScene);
                AssetDatabase.DeleteAsset(unrelatedPath);AssetDatabase.DeleteAsset(scenePath);
            }
        }

        static Texture2DArray Source(bool winter)
        {
            var source=new Texture2DArray(16,16,16,TextureFormat.RGBA32,true,false) {name=winter?"Winter fixture":"MicroSplatConfig_diff_tarray"};
            for(int slice=0;slice<16;slice++)
            {
                var pixels=new Color[256];
                for(int i=0;i<pixels.Length;i++)pixels[i]=new Color((winter?.6f:.1f)+slice*.01f,.2f,slice*.025f,.2f+slice*.04f);
                source.SetPixels(pixels,slice);
            }
            source.Apply();return source;
        }

        static void VerifyLayerCopies(Type type,object controller,Texture2DArray clear,Texture2DArray winter)
        {
            var state=Activator.CreateInstance(type.GetNestedType("LayeredArrayState",All),true);
            state.GetType().GetField("Clear",All).SetValue(state,clear);
            RenderTexture output=null;
            try
            {
                Require((bool)Call(controller,"UpdateLayeredArray",state,15,winter),"Initial layered array failed");
                Require(Get(state,"Output")==null,"Initial request synchronously allocated a terrain render array");
                Require((int)Get(state,"CoverageStep")==-1 && Count(controller,"LayerBlitCount")==0,"Initial request published or built every slice synchronously");
                int ticks=0;
                while((int)Get(state,"CoverageStep")<0)
                {
                    controller.GetType().GetField("arrayWorkFrame",All).SetValue(controller,-1);
                    int before=Count(controller,"LayerBlitCount");
                    Call(controller,"AdvanceLayeredArrays",15);
                    Require(Count(controller,"LayerBlitCount")-before<=2,"Array build exceeded the two-slice frame budget");
                    if(++ticks<8) Require((int)Get(state,"CoverageStep")==-1 && Count(controller,"LayerMipGenerationCount")==0,"Incomplete array was published/generated mipmaps");
                    before=Count(controller,"LayerBlitCount");Call(controller,"AdvanceLayeredArrays",15);
                    Require(Count(controller,"LayerBlitCount")==before,"Repeated Apply in one frame bypassed the slice budget");
                    Require(ticks<=8,"Staged array never completed");
                }
                output=(RenderTexture)Get(state,"Output");
                Require(Count(controller,"LayerBlitCount")==16,"Initialization copied summer slices then overwrote winter slices");
                Require(Count(controller,"LayerMipGenerationCount")==1,"Initialization did not generate exactly one mip chain");
                VerifySlices(type,output,clear,winter,8);
                Call(controller,"UpdateLayeredArray",state,16,winter);
                Require(Count(controller,"LayerBlitCount")==16 && Count(controller,"LayerMipGenerationCount")==1,"Same layer count redrew slices or mipmaps");
                Call(controller,"UpdateLayeredArray",state,7,winter);
                Require(Count(controller,"LayerBlitCount")==20 && Count(controller,"LayerMipGenerationCount")==2,"Thaw did not update only the four changed slices");
                VerifySlices(type,output,clear,winter,4);
                var pending=Activator.CreateInstance(type.GetNestedType("LayeredArrayState",All),true);
                pending.GetType().GetField("Clear",All).SetValue(pending,clear);
                try
                {
                    Call(controller,"UpdateLayeredArray",pending,27,winter);
                    controller.GetType().GetField("arrayWorkFrame",All).SetValue(controller,-1);
                    Call(controller,"AdvanceLayeredArrays",27);
                    int before=Count(controller,"LayerBlitCount");
                    controller.GetType().GetField("arrayWorkFrame",All).SetValue(controller,-1);
                    Call(controller,"AdvanceLayeredArrays",0);
                    Require(Count(controller,"LayerBlitCount")==before,"Summer advanced an unused pending winter array");
                    Call(controller,"UpdateLayeredArray",pending,7,winter);
                    for(int i=0;i<8;i++)
                    {controller.GetType().GetField("arrayWorkFrame",All).SetValue(controller,-1);Call(controller,"AdvanceLayeredArrays",7);}
                    Require((int)Get(pending,"CoverageStep")==7,"In-flight season change did not retarget the staged array");
                    VerifySlices(type,(RenderTexture)Get(pending,"Output"),clear,winter,4);
                }
                finally {var pendingOutput=(RenderTexture)Get(pending,"Output");if(pendingOutput!=null)UnityEngine.Object.DestroyImmediate(pendingOutput);}
            }
            finally {if(output!=null)UnityEngine.Object.DestroyImmediate(output);}
        }

        static void VerifySlices(Type type,RenderTexture output,Texture2DArray clear,Texture2DArray winter,int snowLayers)
        {
            var order=(int[])type.GetField("WinterLayerOrder",All).GetValue(null);
            var scratch=RenderTexture.GetTemporary(16,16,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Default);
            var readback=new Texture2D(16,16,TextureFormat.RGBA32,false,true);var previous=RenderTexture.active;
            try
            {
                for(int rank=0;rank<order.Length;rank++)
                {
                    int slice=order[rank];
                    Graphics.CopyTexture(output,slice,0,scratch,0,0);RenderTexture.active=scratch;
                    readback.ReadPixels(new Rect(0,0,16,16),0,0);readback.Apply();var actual=readback.GetPixel(8,8);
                    Graphics.Blit(rank<snowLayers?winter:clear,scratch,slice,0);RenderTexture.active=scratch;
                    readback.ReadPixels(new Rect(0,0,16,16),0,0);readback.Apply();var expected=readback.GetPixel(8,8);
                    Require(Mathf.Abs(actual.r-expected.r)<.015f && Mathf.Abs(actual.b-expected.b)<.015f && Mathf.Abs(actual.a-expected.a)<.015f,"Layer source/order/alpha changed at slice "+slice);
                }
            }
            finally {RenderTexture.active=previous;RenderTexture.ReleaseTemporary(scratch);UnityEngine.Object.DestroyImmediate(readback);}
        }
    }
}
