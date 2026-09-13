using System;
using System.Collections;
using System.IO;
using System.Reflection;
using AwesomeTechnologies.VegetationSystem;
using UnityEngine;

// Exercises discovery through VSP's completion callback, then the normal Apply
// path, including a single flower (previous random thinning could reject it).
public static class VerifySpringRuntime
{
    const BindingFlags All=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
    static object Get(object o,string n){return o.GetType().GetField(n,All).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,All).SetValue(o,v);}
    static object Call(object o,string n,params object[] a){return o.GetType().GetMethod(n,All).Invoke(o,a);}
    static void Require(bool x,string m){if(!x)throw new Exception(m);}
    public static void Run(string path)
    {
        var mod=Assembly.LoadFrom(Path.Combine(path,"DVSeasons.dll"));
        var type=mod.GetType("DVSeasons.Mod.SpringLifeController",true);
        var controller=Activator.CreateInstance(type,new object[]{path});
        var provider=Activator.CreateInstance(mod.GetType("DVSeasons.Mod.AutumnTreeSourceProvider",true),new object[]{true});
        var go=new GameObject("VSP spring fixture");go.SetActive(false);
        var system=go.AddComponent<VegetationSystemPro>();
        var package=ScriptableObject.CreateInstance<VegetationPackagePro>();
        package.VegetationInfoList.Add(new VegetationItemInfoPro {Name="FlowersWhite01",VegetationItemID="single-flower",
            VegetationType=VegetationType.Grass,Bounds=new Bounds(Vector3.up*.25f,new Vector3(.3f,.5f,.3f))});
        system.VegetationPackageProList.Add(package);
        var cell=new VegetationCell(new Rect(-10,-10,20,20)){Prepared=true,LoadedDistanceBand=0,Index=17};
        var instances=new VegetationPackageInstances(1);cell.VegetationPackageInstancesList.Add(instances);
        instances.LoadStateList[0]=1;
        instances.VegetationItemMatrixList[0].Add(new MatrixInstance{Matrix=Matrix4x4.TRS(new Vector3(1,0,1),Quaternion.identity,Vector3.one)});
        system.LoadedVegetationCellList.Add(cell);
        var camera=new GameObject("Spring runtime camera").AddComponent<Camera>();camera.tag="MainCamera";camera.transform.position=new Vector3(0,1.5f,0);
        try
        {
            Call(provider,"SetEnabled",true);Set(provider,"interestCentreWorld",camera.transform.position);Set(provider,"interestRadius",24f);
            Set(provider,"nextSubscriptionRefresh",float.MaxValue);Call(provider,"AddSystem",system);
            var state=Activator.CreateInstance(Assembly.LoadFrom(Path.Combine(path,"DVSeasons.Core.dll")).GetType("DVSeasons.Core.SeasonState"),
                new object[]{0d,DVSeasons.Core.SeasonKind.Spring,DVSeasons.Core.SeasonKind.Summer,0f,0f,10f,0f});
            Set(controller,"flowers",provider);
            Call(controller,"Apply",state,0f,.6f,new Vector3(12,0,0));
            Require(((IList)Get(controller,"patches")).Count==0,"Read instances before render completion");
            system.OnRenderCompleteDelegate(system);
            Set(controller,"nextScan",0f);
            Call(controller,"Apply",state,0f,.6f,new Vector3(12,0,0));
            var patches=(IList)Get(controller,"patches");Require(patches.Count==1,"Rendered single flower did not produce spring patch");
            var patch=patches[0];Set(patch,"Volume",.5f);Set(patch,"Visibility",1f);
            Call(controller,"Apply",state,0f,.6f,new Vector3(12,0,0));
            var audio=(AudioSource)Get(patch,"Audio");
            Require(audio.clip!=null && audio.clip.length>27 && audio.volume>.15f,"Bee clip missing or inaudible in mild spring weather");
            Require(((Transform)Get(patch,"Butterfly")).localScale.x>.9f,"Butterfly shrank with weather activity");
            Require(((Transform[])Get(patch,"Bees"))[0].localScale.x>.9f,"Bees shrank with weather activity");
            var animalType=mod.GetType("DVSeasons.Mod.SnowExposureExclusions");
            var animal=new GameObject("Deer");var renderer=animal.AddComponent<MeshRenderer>();
            Require((bool)animalType.GetMethod("IsAnimal",All).Invoke(null,new object[]{renderer}),"Animal exclusion lost");
            UnityEngine.Object.DestroyImmediate(animal);
            Debug.Log("SPRING_RUNTIME_OK: real VSP completion, single flower, mild-weather audible bees, full-size butterfly and safe animal check.");
        }
        finally
        {
            ((IDisposable)controller).Dispose();system.LoadedVegetationCellList.Clear();cell.Dispose();
            UnityEngine.Object.DestroyImmediate(camera.gameObject);UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(package);
        }
    }
}
