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
    public static class AnimalSnowVerification
    {
        [Serializable] private sealed class CatMeshData {public Vector3[] vertices,normals;public Vector2[] uv;public int[] indices;}
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
                Verify(Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll")),modPath,Path.Combine(root,"artifacts/verification/animal-snow"));
                VerifyNativeCat(Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll")),modPath,Path.Combine(root,"artifacts/verification/cat-geometry/cat-rig.json"),Path.Combine(root,"artifacts/verification/animal-snow"));
                Debug.Log("ANIMAL_SNOW_OK: native farm/cat meshes, pet/bird/plural names, no scenery/stockcar false positives, bounded priority discovery, depth-matched per-pixel exclusion, skinned movement, ground preservation and cleanup.");
            }
            catch(Exception exception) {Debug.LogException(exception);code=1;}
            finally {AppDomain.CurrentDomain.AssemblyResolve-=resolver;}
            EditorApplication.Exit(code);
        }

        private static void Verify(Assembly mod,string modPath,string output)
        {
            Directory.CreateDirectory(output);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            VerifyBoundedDiscovery(mod);
            var nameTest=mod.GetType("DVSeasons.Mod.SnowAnimalExclusion",true).GetMethod("IsAnimalName",All);
            foreach(var value in new[]{"Cow_rigged","Cow_Mesh","PigPink_rigged","PigBrown_rigged","Goat_rigged","goat_mesh","ChickenWhite_rigged","ChickenBrown_rigged","ChickenGray_rigged","Sheep_rigged","sheep_test001","Deer(Clone)","WildBoar_LOD0",
                "Cat_1_LowPoly","CatSimpleWhiteSpotted_rigged","CatSimpleYellow_rigged","CatSimpleBlack_rigged","CatSimpleGray_rigged","DogBrown_rigged","kitten_mesh","Puppy(Clone)","Cats","Dogs_LOD1","Pigeon_rigged","Swan_goose_mesh","Geese","Stockcar_Cargo_Cattle_LOD0","Stockcar_Cargo_Goats_NoHealth_LOD1","Stockcar_Cargo_Pigs_LOD0","Stockcar_Cargo_Sheep_LOD0"})
                Require((bool)nameTest.Invoke(null,new object[]{value}),"Animal name missed: "+value);
            foreach(var value in new[]{"BoardDecal1","MB_BoardsOld","Billboard_1","Keyboard_LOD2","Cowcatcher","Stage","SwitchBlade","Utility_Catwalk_LOD0","CatWalk","Cat_Walk","Catenary-DC","Dogbox","DogBox_LOD1","DogHouse","dog_bone_joint","CowCatcher","CattleGuard","Stockcar_Cattle","C_Stockcar_Cattle_NoHealth","Stockcar_Sheep","Mouse_LOD0","catch","indicator","FishPlate","HorsePowerGauge"})
                Require(!(bool)nameTest.Invoke(null,new object[]{value}),"Non-animal name excluded: "+value);

            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.gray;RenderSettings.fog=false;
            QualitySettings.antiAliasing=0;
            var camera=new GameObject("Animal snow camera") {tag="MainCamera"}.AddComponent<Camera>();
            camera.renderingPath=RenderingPath.DeferredShading;camera.allowHDR=true;camera.allowMSAA=false;camera.farClipPlane=100;
            camera.transform.position=new Vector3(0,8,-7);camera.transform.LookAt(new Vector3(0,0,0));
            camera.targetTexture=new RenderTexture(512,384,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);camera.targetTexture.Create();
            var albedo=new RenderTexture(512,384,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);albedo.Create();
            var probe=new CommandBuffer {name="Animal snow albedo verification"};probe.Blit(BuiltinRenderTextureType.GBuffer0,albedo);
            camera.AddCommandBuffer(CameraEvent.AfterLighting,probe);
            var material=new Material(Shader.Find("Standard")) {color=new Color(.08f,.08f,.08f)};material.SetFloat("_Glossiness",0);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.name="BoardsOld ground";floor.transform.position=new Vector3(0,-.25f,0);
            floor.transform.localScale=new Vector3(18,.5f,18);floor.GetComponent<Renderer>().sharedMaterial=material;

            var animal=new GameObject("PigPink_rigged");
            var body=new GameObject("Body");body.transform.SetParent(animal.transform,false);
            var bone=new GameObject("Root bone").transform;bone.SetParent(animal.transform,false);
            var mesh=new Mesh {name="Body skin",vertices=new[]{new Vector3(-.7f,.06f,-.7f),new Vector3(-.7f,.06f,.7f),new Vector3(.7f,.06f,.7f),new Vector3(.7f,.06f,-.7f)},
                normals=new[]{Vector3.up,Vector3.up,Vector3.up,Vector3.up},uv=new[]{Vector2.zero,Vector2.up,Vector2.one,Vector2.right},triangles=new[]{0,1,2,0,2,3}};
            mesh.boneWeights=new[]{new BoneWeight {boneIndex0=0,weight0=1},new BoneWeight {boneIndex0=0,weight0=1},new BoneWeight {boneIndex0=0,weight0=1},new BoneWeight {boneIndex0=0,weight0=1}};
            mesh.bindposes=new[]{Matrix4x4.identity};mesh.RecalculateBounds();
            var skin=body.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=mesh;skin.bones=new[]{bone};skin.rootBone=bone;
            skin.updateWhenOffscreen=true;skin.localBounds=new Bounds(Vector3.zero,Vector3.one*12);skin.sharedMaterial=material;

            var repositoryType=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
            var repository=Activator.CreateInstance(repositoryType,new object[]{modPath});
            var bundle=AssetBundle.LoadFromFile(Path.Combine(modPath,"AssetBundles/dvseasons_dv99"));
            repositoryType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{bundle});
            var controller=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.ProceduralSnowController",true),new[]{repository});
            Call(controller,"SetVehicleDiscovery",new Func<IEnumerable<Component>>(()=>new Component[0]));
            var exclusions=Get(controller,"exposureExclusions");
            try
            {
                for(var i=0;i<6;i++) {Call(controller,"Apply",1f,true);camera.Render();}
                Require(((IDictionary)Get(exclusions,"animals")).Contains(skin.GetInstanceID()),"Skinned animal was not discovered before the first scene snow capture");
                var image=Read(albedo,Path.Combine(output,"animal-excluded.png"));
                Require(Sample(image,camera,new Vector3(0,.06f,0))<.2f,"Snow remained on the animal close to the ground");
                Require(Sample(image,camera,new Vector3(2,0,0))>.5f,"Animal exclusion removed snow from adjacent ground");
                UnityEngine.Object.DestroyImmediate(image);

                // Demonstrate the original failure, using exactly the same
                // exposure map with the animal already absent from it.
                ((IDictionary)Get(exclusions,"animals")).Clear();camera.Render();
                image=Read(albedo,Path.Combine(output,"old-height-only-snow.png"));
                var oldSnow=Sample(image,camera,new Vector3(0,.06f,0));
                Require(oldSnow>.4f,"Fixture did not reproduce height-only animal snow: "+oldSnow);
                UnityEngine.Object.DestroyImmediate(image);Call(exclusions,"VisitSkinned",skin);

                bone.localPosition=new Vector3(2,0,0);camera.Render();
                image=Read(albedo,Path.Combine(output,"animal-skin-moved.png"));
                Require(Sample(image,camera,new Vector3(2,.06f,0))<.2f,"Exclusion did not follow the skinned bone");
                Require(Sample(image,camera,Vector3.zero)>.5f,"Moving animal left an exclusion on ground");
                UnityEngine.Object.DestroyImmediate(image);

                skin.enabled=false;camera.Render();image=Read(albedo,Path.Combine(output,"animal-hidden.png"));
                Require(Sample(image,camera,new Vector3(2,0,0))>.5f,"Hidden animal left snow missing on ground");UnityEngine.Object.DestroyImmediate(image);
                skin.enabled=true;skin.forceRenderingOff=true;Call(exclusions,"HideForCapture");Call(exclusions,"Restore");
                Require(skin.forceRenderingOff,"Capture changed an externally hidden renderer");skin.forceRenderingOff=false;
                UnityEngine.Object.DestroyImmediate(animal);Call(exclusions,"PrepareVisibleAnimals",camera);
                Require(((IDictionary)Get(exclusions,"animals")).Count==0,"Destroyed animal remained registered");
            }
            finally
            {
                ((IDisposable)controller).Dispose();((IDisposable)repository).Dispose();
                camera.RemoveCommandBuffer(CameraEvent.AfterLighting,probe);probe.Dispose();
                var target=camera.targetTexture;camera.targetTexture=null;
                UnityEngine.Object.DestroyImmediate(target);UnityEngine.Object.DestroyImmediate(albedo);
                UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(floor);
                if(animal!=null) UnityEngine.Object.DestroyImmediate(animal);
                UnityEngine.Object.DestroyImmediate(mesh);UnityEngine.Object.DestroyImmediate(material);
            }
        }

        private static void VerifyNativeCat(Assembly mod,string modPath,string geometryPath,string output)
        {
            Require(File.Exists(geometryPath),"Export the installed game's Cat_1_LowPoly mesh before native-cat GPU verification");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            RenderSettings.ambientMode=AmbientMode.Flat;RenderSettings.ambientLight=Color.gray;RenderSettings.fog=false;
            var data=JsonUtility.FromJson<CatMeshData>(File.ReadAllText(geometryPath));
            var mesh=new Mesh {name="Body",vertices=data.vertices,normals=data.normals,uv=data.uv,triangles=data.indices};mesh.RecalculateBounds();
            var weights=new BoneWeight[mesh.vertexCount];for(int i=0;i<weights.Length;i++) weights[i]=new BoneWeight {boneIndex0=0,weight0=1};
            mesh.boneWeights=weights;mesh.bindposes=new[]{Matrix4x4.identity};
            var animal=new GameObject("Unknown creature");var child=new GameObject("Body");child.transform.SetParent(animal.transform,false);
            var bone=new GameObject("Root bone").transform;bone.SetParent(animal.transform,false);
            var skin=child.AddComponent<SkinnedMeshRenderer>();skin.sharedMesh=mesh;skin.bones=new[]{bone};skin.rootBone=bone;
            skin.updateWhenOffscreen=true;skin.localBounds=mesh.bounds;
            var material=new Material(Shader.Find("Standard")) {color=new Color(.05f,.05f,.05f)};material.SetFloat("_Glossiness",0);skin.sharedMaterial=material;
            animal.transform.position=Vector3.up*(-mesh.bounds.min.y+.015f);
            var floor=GameObject.CreatePrimitive(PrimitiveType.Cube);floor.name="Station paving";floor.transform.position=new Vector3(0,-.25f,0);floor.transform.localScale=new Vector3(8,.5f,8);floor.GetComponent<Renderer>().sharedMaterial=material;
            var camera=new GameObject("Native cat verification") {tag="MainCamera"}.AddComponent<Camera>();
            camera.renderingPath=RenderingPath.DeferredShading;camera.allowHDR=true;camera.allowMSAA=false;camera.farClipPlane=50;
            var center=animal.transform.TransformPoint(mesh.bounds.center);var extent=mesh.bounds.extents.magnitude;
            camera.transform.position=center+new Vector3(1,1.5f,-1.2f).normalized*extent*3.5f;camera.transform.LookAt(center);
            camera.targetTexture=new RenderTexture(512,384,24,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);camera.targetTexture.Create();
            var albedo=new RenderTexture(512,384,0,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear);albedo.Create();
            var probe=new CommandBuffer {name="Native cat albedo verification"};probe.Blit(BuiltinRenderTextureType.GBuffer0,albedo);camera.AddCommandBuffer(CameraEvent.AfterLighting,probe);
            var repositoryType=mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);var repository=Activator.CreateInstance(repositoryType,new object[]{modPath});
            var bundle=AssetBundle.LoadFromFile(Path.Combine(modPath,"AssetBundles/dvseasons_dv99"));repositoryType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{bundle});
            var controller=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.ProceduralSnowController",true),new[]{repository});
            Call(controller,"SetVehicleDiscovery",new Func<IEnumerable<Component>>(()=>new Component[0]));var exclusions=Get(controller,"exposureExclusions");
            Texture2D before=null,after=null;
            try
            {
                // The prior classifier omitted cats. Capture that exact failure
                // with neutral names, then use the shipped prefab names without
                // replacing the height map: visible pixels must be protected even
                // while an old streamed capture still contains the cat surface.
                for(int i=0;i<8;i++){Call(controller,"Apply",1f,true);camera.Render();}
                before=Read(albedo,Path.Combine(output,"native-cat-old-snow.png"));
                animal.name="CatSimpleWhiteSpotted_rigged";child.name="Cat_1_LowPoly";mesh.name="Cat_1_LowPoly";
                Call(exclusions,"VisitSkinned",skin);camera.Render();after=Read(albedo,Path.Combine(output,"native-cat-excluded.png"));
                var oldPixels=before.GetPixels();var newPixels=after.GetPixels();int cleaned=0,unchangedSnow=0;
                for(int i=0;i<oldPixels.Length;i++)
                {
                    if(oldPixels[i].r>.4f && newPixels[i].r<.15f) cleaned++;
                    if(oldPixels[i].r>.5f && newPixels[i].r>.5f && Mathf.Abs(oldPixels[i].r-newPixels[i].r)<.03f) unchangedSnow++;
                }
                Require(cleaned>150,"Native cat geometry still received snow, cleaned pixels="+cleaned);
                Require(unchangedSnow>500,"Protecting the native cat removed nearby ground snow");
                var classify=mod.GetType("DVSeasons.Mod.SnowAnimalExclusion",true).GetMethod("IsAnimal",All);
                animal.name="Neutral root";child.name="Neutral mesh";
                Require((bool)classify.Invoke(null,new object[]{skin}),"Renamed native cat no longer matched its mesh asset name");
                Debug.Log("NATIVE_CAT_SNOW_OK: shipped Cat_1_LowPoly "+mesh.vertexCount+" vertices, cleaned pixels="+cleaned+", surrounding snow pixels="+unchangedSnow);
            }
            finally
            {
                ((IDisposable)controller).Dispose();((IDisposable)repository).Dispose();camera.RemoveCommandBuffer(CameraEvent.AfterLighting,probe);probe.Dispose();
                var target=camera.targetTexture;camera.targetTexture=null;
                foreach(var resource in new UnityEngine.Object[]{target,albedo,camera.gameObject,animal,floor,mesh,material,before,after}) if(resource!=null) UnityEngine.Object.DestroyImmediate(resource);
            }
        }

        private static void VerifyBoundedDiscovery(Assembly mod)
        {
            var source=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SnowExposureExclusions",true),true);
            var scenery=new GameObject("Dense station scenery");
            for(var i=0;i<2200;i++) new GameObject("Structure "+i).transform.SetParent(scenery.transform,false);
            var cow=new GameObject("Cow_rigged");var skin=cow.AddComponent<SkinnedMeshRenderer>();
            var initialScene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            // Unity 2019 cannot open an additive scene beside an untitled scene.
            var fixtureScenePath="Assets/AnimalSnowDiscoveryFixture-"+Guid.NewGuid().ToString("N")+".unity";
            Require(EditorSceneManager.SaveScene(initialScene,fixtureScenePath),"Could not save temporary discovery fixture scene");
            var streamed=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Additive);
            var sheep=new GameObject("Sheep_rigged");var sheepSkin=sheep.AddComponent<SkinnedMeshRenderer>();
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(sheep,streamed);
            try
            {
                Call(source,"Update");
                var steps=(int)source.GetType().GetProperty("LastAnimalDiscoverySteps",All).GetValue(source,null);
                Require(steps>0 && steps<=96,"Initial animal discovery exceeded its node budget");
                Require(((ICollection)Get(source,"pendingAnimalScenes")).Count>0,"Dense scene was traversed synchronously");
                // A newly loaded scene must preempt an unfinished old scene;
                // it must not wait behind thousands of unrelated transforms.
                Call(source,"QueueAnimalScene",streamed,true);
                for(var step=0;step<24;step++)
                {
                    Call(source,"Update");
                    steps=(int)source.GetType().GetProperty("LastAnimalDiscoverySteps",All).GetValue(source,null);
                    Require(steps<=96,"Streaming animal discovery exceeded its node budget");
                    var animals=(IDictionary)Get(source,"animals");
                    if(animals.Contains(skin.GetInstanceID()) && animals.Contains(sheepSkin.GetInstanceID())) break;
                }
                var found=(IDictionary)Get(source,"animals");
                Require(found.Contains(skin.GetInstanceID()),"Named animal root was starved by dense scenery");
                Require(found.Contains(sheepSkin.GetInstanceID()),"Streamed scene animal was not prioritized");
                ((IDisposable)source).Dispose();
                Require(((ICollection)Get(source,"pendingAnimalScenes")).Count==0,"Dispose retained scene walkers");
                Debug.Log("ANIMAL_BOUNDED_DISCOVERY_OK: 2200 unrelated transforms; <=96 steps per update; scene priority; named roots; disposal.");
            }
            finally
            {
                ((IDisposable)source).Dispose();
                UnityEngine.Object.DestroyImmediate(scenery);UnityEngine.Object.DestroyImmediate(cow);
                UnityEngine.Object.DestroyImmediate(sheep);
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(initialScene);
                EditorSceneManager.CloseScene(streamed,true);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                AssetDatabase.DeleteAsset(fixtureScenePath);
            }
        }

        private static Texture2D Read(RenderTexture target,string path)
        {
            var previous=RenderTexture.active;RenderTexture.active=target;
            var result=new Texture2D(target.width,target.height,TextureFormat.RGBAFloat,false,true);
            result.ReadPixels(new Rect(0,0,target.width,target.height),0,0);result.Apply();RenderTexture.active=previous;
            var png=new Texture2D(target.width,target.height,TextureFormat.RGB24,false);var colors=result.GetPixels();
            for(var i=0;i<colors.Length;i++) colors[i]=colors[i].gamma;
            png.SetPixels(colors);png.Apply();File.WriteAllBytes(path,png.EncodeToPNG());UnityEngine.Object.DestroyImmediate(png);
            return result;
        }
        private static float Sample(Texture2D image,Camera camera,Vector3 point)
        {
            var uv=camera.WorldToViewportPoint(point);
            return image.GetPixel(Mathf.RoundToInt(uv.x*(image.width-1)),Mathf.RoundToInt(uv.y*(image.height-1))).r;
        }
    }
}
