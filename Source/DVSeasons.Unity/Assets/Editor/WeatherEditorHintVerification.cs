using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class WeatherEditorHintVerification
    {
        const BindingFlags Methods = BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        public static void Run()
        {
            int code=0;
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var mod=Path.Combine(root,"artifacts/build/DVSeasons");
            var managed=Path.Combine(Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_GAME"),"DerailValley_Data/Managed");
            AppDomain.CurrentDomain.AssemblyResolve+=(sender,args)=>
            {
                foreach(var directory in new[]{mod,managed,Path.Combine(managed,"UnityModManager")})
                {
                    string path=Path.Combine(directory,new AssemblyName(args.Name).Name+".dll");
                    if(File.Exists(path))return Assembly.LoadFrom(path);
                }
                return null;
            };
            GameObject scene=null;
            try
            {
                var production=Assembly.LoadFrom(Path.Combine(mod,"DVSeasons.dll"));
                var ui=Assembly.LoadFrom(Path.Combine(managed,"DV.UI.dll"));
                Type helperType=production.GetType("DVSeasons.Mod.WeatherEditorHintLayout",true);
                Type weatherType=ui.GetType("DV.UI.LocoHUD.PhotoModeWeatherController",true);
                Type panelType=ui.GetType("DV.UI.LocoHUD.HUDPanel",true);
                scene=new GameObject("weather hint regression",typeof(RectTransform));
                var parent=(RectTransform)scene.transform;
                parent.sizeDelta=new Vector2(1920,1080);
                var original=new GameObject("native corner",typeof(RectTransform));
                var originalRect=(RectTransform)original.transform;originalRect.SetParent(parent,false);
                originalRect.localScale=Vector3.one*.57344f;
                var before=new GameObject("before",typeof(RectTransform));before.transform.SetParent(originalRect,false);
                var label=new GameObject("text",typeof(RectTransform));var text=(RectTransform)label.transform;text.SetParent(originalRect,false);
                text.anchorMin=new Vector2(.1f,.8f);text.anchorMax=new Vector2(.3f,1);
                text.pivot=new Vector2(.2f,.7f);text.sizeDelta=new Vector2(220,50);
                text.anchoredPosition3D=new Vector3(7,-9,2);text.localScale=new Vector3(.9f,.8f,1);
                text.localRotation=Quaternion.Euler(0,0,3);
                Vector2 amin=text.anchorMin,amax=text.anchorMax,pivot=text.pivot,size=text.sizeDelta;
                Vector3 position=text.anchoredPosition3D,scale=text.localScale;
                Quaternion rotation=text.localRotation;int sibling=text.GetSiblingIndex();
                var weatherObject=new GameObject("weather",typeof(RectTransform));weatherObject.transform.SetParent(parent,false);
                var weatherRect=(RectTransform)weatherObject.transform;weatherRect.sizeDelta=new Vector2(1500,140);
                var weather=weatherObject.AddComponent(weatherType);
                var panel=weatherObject.AddComponent(panelType);
                weatherType.GetField("panel").SetValue(weather,panel);
                panelType.GetField("open").SetValue(panel,true);panelType.GetField("visible").SetValue(panel,true);
                var helper=original.AddComponent(helperType);
                Action attach=()=>helperType.GetMethod("Attach",Methods).Invoke(helper,new object[]{text,weather});
                Action restore=()=>helperType.GetMethod("Restore",Methods).Invoke(helper,null);
                Action checkOriginal=()=>
                {
                    Require(text.parent==originalRect && text.GetSiblingIndex()==sibling,"parent/sibling");
                    Require(text.anchorMin==amin && text.anchorMax==amax && text.pivot==pivot && text.sizeDelta==size,"anchors/pivot/size");
                    Require(text.anchoredPosition3D==position && text.localScale==scale && text.localRotation==rotation,"position/scale/rotation");
                };
                for(int i=0;i<40;i++)
                {
                    attach();var header=text.parent as RectTransform;
                    Require(header!=null && header.parent==weatherRect,"inside weather panel");
                    Require(header.anchorMin==new Vector2(0,1) && header.anchorMax==new Vector2(.42f,1),"responsive header anchors");
                    Require(Mathf.Abs(text.lossyScale.x-.57344f*.9f)<.00001f,"preserve visible scale");
                    Require(header.GetComponent<UnityEngine.UI.RectMask2D>()!=null,"clip long localized names");
                    Require(!header.GetComponent<CanvasGroup>().blocksRaycasts,"do not intercept weather input");
                    attach();Require(text.parent==header,"repeated hover is idempotent");
                    restore();checkOriginal();
                }
                attach();panelType.GetField("open").SetValue(panel,false);
                helperType.GetMethod("LateUpdate",Methods).Invoke(helper,null);checkOriginal();
                panelType.GetField("open").SetValue(panel,true);attach();
                helperType.GetMethod("OnDisable",Methods).Invoke(helper,null);checkOriginal();
                Debug.Log("WEATHER_HINT_LAYOUT_OK: 40 hover cycles; native scale retained; responsive clipped header; close and disable restore every transform property.");
            }
            catch(Exception error){Debug.LogException(error);code=1;}
            finally{if(scene!=null)UnityEngine.Object.DestroyImmediate(scene);}
            EditorApplication.Exit(code);
        }
        static void Require(bool condition,string message){if(!condition)throw new Exception("Weather hint: "+message);}
    }
}
