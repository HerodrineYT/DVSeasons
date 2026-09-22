using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
namespace DVSeasons.AssetBundleBuild
{
    public static class NativePropertyBlockProbe
    {
        public static void Run()
        {
            var camera=new GameObject("Camera").AddComponent<Camera>();camera.enabled=false;camera.renderingPath=RenderingPath.DeferredShading;
            camera.transform.position=new Vector3(0,40,-12);camera.transform.LookAt(Vector3.zero);camera.farClipPlane=200;
            var target=new RenderTexture(800,600,24,RenderTextureFormat.ARGBHalf);target.Create();camera.targetTexture=target;
            var material=new Material(Shader.Find("Standard")){enableInstancing=true};
            var read=new Texture2D(1,1,TextureFormat.RGBAFloat,false,true);var renderers=new Renderer[1024];
            for(int i=0;i<renderers.Length;i++){var go=GameObject.CreatePrimitive(PrimitiveType.Cube);go.transform.position=new Vector3((i%32-16)*1.1f,0,(i/32-16)*1.1f);var r=go.GetComponent<Renderer>();r.sharedMaterial=material;renderers[i]=r;}
            var block=new MaterialPropertyBlock();
            foreach(var mode in new[]{0,1,0,1,0,1})
            {
                for(int i=0;i<renderers.Length;i++){block.Clear();if(mode==1)block.SetFloat("_DVPSNativeSlot",i+1);renderers[i].SetPropertyBlock(mode==0?null:block);}
                for(int i=0;i<12;i++)camera.Render();var clock=Stopwatch.StartNew();
                for(int i=0;i<80;i++)camera.Render();RenderTexture.active=target;read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();clock.Stop();
                UnityEngine.Debug.Log("UNKNOWN_NATIVE_PROPERTY_BLOCK mode="+mode+" render_ms="+(clock.Elapsed.TotalMilliseconds/80).ToString("F3"));
            }
            EditorApplication.Exit(0);
        }
    }
}