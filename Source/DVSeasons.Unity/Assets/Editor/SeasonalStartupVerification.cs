using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    // Exercise the real SeasonVisualController texture dispatch without starting
    // locomotive, weather, particle, audio, or player services in the editor.
    public static class SeasonalStartupVerification
    {
        const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static object visual,seasonal,procedural,repository,settings,state;
        static Type stateType,seasonType,readbackType;
        static readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
        static readonly Dictionary<int,object> expectedSets=new Dictionary<int,object>();
        static Texture2D foliage,ballast,road,concrete;
        static Material roadMaterial,concreteMaterial;
        static MeshRenderer concreteRenderer;
        static Camera renderCamera;
        static RenderTexture renderTarget;
        static int stage,checks,configuration,exitCode;
        static double deadline;
        static bool finishing;

        static object Get(object target,string field) {return target.GetType().GetField(field,All).GetValue(target);}
        static void Set(object target,string field,object value) {target.GetType().GetField(field,All).SetValue(target,value);}
        static object Call(object target,string method,params object[] args) {return target.GetType().GetMethod(method,All).Invoke(target,args);}
        static void Require(bool value,string message) {if(!value)throw new Exception(message);}
        static int Requests {get {return (int)readbackType.GetProperty("ActiveRequests",All).GetValue(null,null);}}

        public static void Run()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var modPath=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            var game=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME");
            AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=> {
                foreach(var dir in new[]{modPath,Path.Combine(game,"DerailValley_Data/Managed"),Path.Combine(game,"DerailValley_Data/Managed/UnityModManager")})
                {var file=Path.Combine(dir,new AssemblyName(e.Name).Name+".dll");if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
                var mod=Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll"));
                var core=Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.Core.dll"));
                stateType=core.GetType("DVSeasons.Core.SeasonState",true);
                seasonType=core.GetType("DVSeasons.Core.SeasonKind",true);
                readbackType=mod.GetType("DVSeasons.Mod.SeasonTextureReadback",true);
                repository=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true),new object[]{modPath});
                seasonal=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SeasonalTextureController",true),new[]{repository});
                visual=FormatterServices.GetUninitializedObject(mod.GetType("DVSeasons.Mod.SeasonVisualController",true));
                procedural=FormatterServices.GetUninitializedObject(mod.GetType("DVSeasons.Mod.ProceduralSnowController",true));
                Set(visual,"seasonalTextures",seasonal);Set(visual,"proceduralSurfaceSnow",procedural);
                settings=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.SeasonModSettings",true));
                Set(settings,"SeasonalTexturesEnabled",true);Set(settings,"TerrainTextureChanges",true);
                Set(settings,"VegetationTextureChanges",true);Set(settings,"ProceduralSnowEnabled",true);
                Set(settings,"SeasonalTextureResolution",32);Set(settings,"MaximumSeasonalTextures",128);
                Set(settings,"TextureUpdatesPerFrame",1);Set(settings,"TextureChangeStrength",1f);
                // Native beech albedo is recognized through its leaf material or
                // SpeedTree shader. Standard alone supplies neither signature.
                foliage=MaterialTexture("T_beech_atlas_BC v2","beech leaves");ballast=MaterialTexture("BallastNew_d");road=MaterialTexture("AsphaltRoad_01d");
                roadMaterial=(Material)owned[owned.Count-1];
                concrete=MaterialTexture("MB_Concrete_01d");concreteMaterial=(Material)owned[owned.Count-1];
                var pavement=GameObject.CreatePrimitive(PrimitiveType.Plane);owned.Add(pavement);
                concreteRenderer=pavement.GetComponent<MeshRenderer>();concreteRenderer.sharedMaterial=concreteMaterial;
                var cameraObject=new GameObject("Startup fixture render loop");owned.Add(cameraObject);
                renderCamera=cameraObject.AddComponent<Camera>();renderCamera.enabled=false;
                renderTarget=new RenderTexture(32,32,16,RenderTextureFormat.ARGB32);renderTarget.Create();owned.Add(renderTarget);
                renderCamera.targetTexture=renderTarget;
                state=State(false);stage=checks=0;deadline=EditorApplication.timeSinceStartup+120;
                EditorApplication.update+=Poll;
            }
            catch(Exception error) {Debug.LogException(error);EditorApplication.Exit(1);}
        }

        static Texture2D MaterialTexture(string name,string materialDescription=null)
        {
            var texture=new Texture2D(32,32,TextureFormat.RGBA32,false){name=name};owned.Add(texture);
            var pixels=new Color32[1024];for(int i=0;i<pixels.Length;i++)pixels[i]=new Color32(64,85,47,255);
            texture.SetPixels32(pixels);texture.Apply();
            var material=new Material(Shader.Find("Standard")){name="Startup fixture "+(materialDescription??name)};owned.Add(material);material.mainTexture=texture;
            return texture;
        }

        static object State(bool winter)
        {
            return Activator.CreateInstance(stateType,new object[]{winter?3d:1d,
                Enum.Parse(seasonType,winter?"Winter":"Summer"),Enum.Parse(seasonType,winter?"Spring":"Autumn"),
                0f,winter?1f:0f,winter?-20f:20f,0f});
        }

        static object FindSet(Texture2D source)
        {
            foreach(var value in ((IDictionary)Get(seasonal,"sets")).Values)
                if((Texture2D)Get(value,"source")==source)return value;
            return null;
        }

        static void SaveSets()
        {
            expectedSets.Clear();
            foreach(var value in ((IDictionary)Get(seasonal,"sets")).Values)
                expectedSets.Add(((Texture2D)Get(value,"source")).GetInstanceID(),value);
            configuration=(int)Get(seasonal,"configurationKey");
        }

        static void CheckSets()
        {
            Require((int)Get(seasonal,"configurationKey")==configuration,"Transient renderer activity changed the texture configuration");
            foreach(var value in ((IDictionary)Get(seasonal,"sets")).Values)
            {
                object previous;var id=((Texture2D)Get(value,"source")).GetInstanceID();
                if(expectedSets.TryGetValue(id,out previous))Require(ReferenceEquals(previous,value),"Cached seasonal texture set was recreated without a settings change");
            }
            foreach(var texture in new[]{foliage,ballast})
            {
                object previous;Require(expectedSets.TryGetValue(texture.GetInstanceID(),out previous)&&ReferenceEquals(previous,FindSet(texture)),"An existing cached texture set disappeared");
            }
        }

        static void Poll()
        {
            try
            {
                // EditorApplication.update alone does not run a player frame in
                // headless edit mode. Give queued GPU copies the same render and
                // player-loop progress they receive during normal game frames.
                EditorApplication.QueuePlayerLoopUpdate();
                if(renderCamera!=null)renderCamera.Render();
                if(finishing)
                {
                    if(Requests>0 && EditorApplication.timeSinceStartup<deadline)return;
                    Require(Requests==0,"Cancelled startup readbacks did not release their requests");
                    ((IDisposable)repository).Dispose();
                    foreach(var resource in owned)if(resource!=null)UnityEngine.Object.DestroyImmediate(resource);
                    EditorApplication.update-=Poll;EditorApplication.Exit(exitCode);return;
                }
                if(EditorApplication.timeSinceStartup>deadline)throw new Exception("Seasonal startup verification timed out");
                // Match the real staged startup: authored profiles are only
                // discovered once all three asset bundles have loaded.
                if(!(bool)repository.GetType().GetProperty("IsLoadFinished",All).GetValue(repository,null))return;
                Call(visual,"ApplySeasonalTextures",state,settings);
                if(stage==0)
                {
                    if(Get(seasonal,"materialScan")!=null)return;
                    Require((bool)Get(seasonal,"proceduralSnow"),"Dry startup selected legacy textures despite procedural setting");
                    Require(FindSet(foliage)!=null,"Startup discovery missed the leaf material");
                    Require(FindSet(ballast)!=null,"Startup discovery missed ballast");
                    Require(FindSet(road)==null,"Procedural startup created an expensive legacy road texture set");
                    if(!Ready(foliage)||!Ready(ballast))return;
                    SaveSets();Set(procedural,"<IsActive>k__BackingField",true);Set(procedural,"coverageInitialized",true);Set(procedural,"amount",1f);
                    state=State(true);stage=1;checks=0;
                }
                else if(stage==1)
                {
                    CheckSets();Require(FindSet(road)==null,"Winter activation introduced legacy road textures");
                    if(++checks<12)return;
                    Set(procedural,"<IsActive>k__BackingField",false);stage=2;checks=0;
                }
                else if(stage==2)
                {
                    CheckSets();if(++checks<12)return;
                    Set(settings,"ProceduralSnowEnabled",false);stage=3;
                }
                else if(stage==3)
                {
                    CheckSets();
                    if(Get(seasonal,"materialScan")!=null)return;
                    Require(FindSet(road)!=null,"Explicit legacy mode did not restore road textures");
                    Require(FindSet(concrete)!=null,"Legacy surface scan missed concrete pavement");
                    if(!Finished(road)||!Finished(ballast)||!Finished(concrete))return;
                    Call(seasonal,"ApplyBindings");
                    Require(roadMaterial.mainTexture!=road,"Legacy road material still has its summer albedo");
                    Require(concreteRenderer.sharedMaterial!=concreteMaterial && concreteRenderer.sharedMaterial.mainTexture!=concrete,"Legacy concrete renderer was not rebound");
                    Require(((Texture2D)roadMaterial.mainTexture).GetPixel(8,8).r>.3f,"Legacy road output has no winter pixels");
                    SaveSets();stage=4;checks=0;
                }
                else if(stage==4)
                {
                    CheckSets();if(++checks<12)return;
                    Set(settings,"ProceduralSnowEnabled",true);stage=5;
                }
                else if(stage==5)
                {
                    CheckSets();
                    if(Get(seasonal,"materialScan")!=null)return;
                    Require(ReferenceEquals(expectedSets[road.GetInstanceID()],FindSet(road)),"Road cache was destroyed on procedural toggle");
                    Require(roadMaterial.mainTexture==road && concreteRenderer.sharedMaterial==concreteMaterial,"Procedural toggle did not restore original road/concrete bindings");
                    SaveSets();stage=6;checks=0;
                }
                else
                {
                    CheckSets();if(++checks<12)return;
                    if(!Ready(foliage)||!Ready(ballast))return;
                    Set(settings,"ProceduralSnowEnabled",false);Call(visual,"ApplySeasonalTextures",state,settings);Call(seasonal,"ApplyBindings");
                    CheckSets();
                    Require(roadMaterial.mainTexture!=road && concreteRenderer.sharedMaterial!=concreteMaterial,"Warm legacy toggle did not immediately restore winter bindings");
                    Debug.Log("SEASONAL_STARTUP_OK: mode toggles preserve warm texture sets; legacy road/concrete winter pixels reach actual materials; procedural restores native bindings; returning to legacy restores cached winter bindings immediately.");
                    Finish(0);
                }
            }
            catch(Exception error){Debug.LogException(error);if(finishing){EditorApplication.update-=Poll;EditorApplication.Exit(1);}else Finish(1);}
        }

        static void Finish(int code)
        {
            exitCode=code;finishing=true;deadline=EditorApplication.timeSinceStartup+30;
            if(seasonal!=null)((IDisposable)seasonal).Dispose();
            // Fixture teardown only. Never block while exercising startup or
            // settings changes; finish cancelled copies before destroying their
            // source textures and shutting the batch editor down.
            AsyncGPUReadback.WaitAllRequests();
        }

        static bool Ready(Texture2D texture)
        {
            var set=FindSet(texture);
            return set!=null && (bool)set.GetType().GetProperty("IsReady",All).GetValue(set,null);
        }
        static bool Finished(Texture2D texture)
        {
            var set=FindSet(texture);
            return set!=null && (int)Get(set,"lastStyleKey")== (int)Get(seasonal,"lastStyleKey") && !(bool)set.GetType().GetProperty("HasPendingUpdate",All).GetValue(set,null);
        }
    }
}

