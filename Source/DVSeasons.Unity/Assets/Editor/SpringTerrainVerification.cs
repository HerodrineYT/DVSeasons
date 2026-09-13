using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    public static class SpringTerrainVerification
    {
        private static readonly Color[] Colours={new Color(.24f,.34f,.09f,.23f),
            new Color(.35f,.22f,.12f,.48f),new Color(.4f,.4f,.4f,.71f),new Color(.93f,.95f,.97f,.94f)};
        private static Texture2DArray Source(bool reverse=false)
        {
            var source=new Texture2DArray(32,32,4,TextureFormat.RGBA32,true,false);
            for(int s=0;s<4;s++)
            {
                var pixels=new Color[1024];for(int i=0;i<pixels.Length;i++) pixels[i]=Colours[reverse?3-s:s];
                source.SetPixels(pixels,s);
            }
            source.Apply();return source;
        }
        private static Color Sample(Material material,Texture source,int slice,float weight)
        {
            var previous=RenderTexture.active;
            var target=RenderTexture.GetTemporary(32,32,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Default);
            var pixels=new Texture2D(32,32,TextureFormat.RGBA32,false,true);
            try
            {
                material.SetTexture("_SpringSource",source);material.SetFloat("_SpringSlice",slice);material.SetFloat("_SpringWeight",weight);
                Graphics.Blit(null,target,material);RenderTexture.active=target;
                pixels.ReadPixels(new Rect(0,0,32,32),0,0);pixels.Apply();
                var c=pixels.GetPixel(16,16);
                return QualitySettings.activeColorSpace==ColorSpace.Linear ? c.gamma : c;
            }
            finally { RenderTexture.active=previous;RenderTexture.ReleaseTemporary(target);UnityEngine.Object.DestroyImmediate(pixels); }
        }
        private static void Require(bool pass,string message) {if(!pass) throw new Exception(message);}
        public static void Verify(AssetBundle bundle)
        {
            var shader=bundle.LoadAsset<Shader>("assets/dvseasons/dv99/shaders/springterrain.shader");
            Require(shader!=null && shader.isSupported,"Spring terrain shader missing");
            var material=new Material(shader);var source=Source();
            try
            {
                for(int slice=0;slice<4;slice++)
                {
                    var summer=Sample(material,source,slice,0);var spring=Sample(material,source,slice,1);
                    Require(Math.Abs(summer.r-Colours[slice].r)<.02f,"Terrain slice identity changed");
                    Require(Math.Abs(spring.a-Colours[slice].a)<.01f,"Spring changed terrain alpha data");
                    if(slice==0) Require(spring.g>summer.g+.07f && spring.r>summer.r+.025f,"Spring grass not distinct from summer");
                    if(slice==1) Require(spring.r>spring.g && spring.g>spring.b,"Spring earth turned green");
                    if(slice==2) Require(Math.Abs(spring.r-spring.g)<.01f,"Spring rocks turned green");
                    if(slice==3) Require(Math.Abs(spring.r-summer.r)<.01f,"Spring discoloured remaining snow");
                    var half=Sample(material,source,slice,.5f);
                    Require(Math.Abs(half.g-(summer.g+spring.g)*.5f)<.02f,"Spring transition not continuous");
                }
                Debug.Log("DVSeasons spring terrain GPU verified: young green grass, neutral rock/snow, brown earth, alpha and slice identity preserved.");
            }
            finally { UnityEngine.Object.DestroyImmediate(material);UnityEngine.Object.DestroyImmediate(source); }
        }
        public static void VerifyRuntimeCache()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var modPath=Path.Combine(root,"artifacts/build/DVSeasons");
            var assembly=Assembly.LoadFrom(Path.Combine(modPath,"DVSeasons.dll"));
            var repositoryType=assembly.GetType("DVSeasons.Mod.SeasonAssetBundleRepository",true);
            var repository=Activator.CreateInstance(repositoryType,new object[]{modPath});
            var bundle=AssetBundle.LoadFromFile(Path.Combine(modPath,"AssetBundles/dvseasons_dv99"));
            repositoryType.GetProperty("Bundle").GetSetMethod(true).Invoke(repository,new object[]{bundle});
            var type=assembly.GetType("DVSeasons.Mod.SpringTerrainTint",true);
            var cache=Activator.CreateInstance(type,new[]{repository});var get=type.GetMethod("Get");
            var a=Source();var b=Source(true);
            var mat=new Material(bundle.LoadAsset<Shader>("assets/dvseasons/dv99/shaders/springterrain.shader"));
            try
            {
                var output=(Texture)get.Invoke(cache,new object[]{a,a,32,0});
                Require(output is RenderTexture,"Runtime did not create a spring array");
                Require(Sample(mat,output,0,0).g>Colours[0].g+.07f,"Runtime spring array lost grass colour");
                var same=get.Invoke(cache,new object[]{a,a,32,0});
                Require(ReferenceEquals(output,same) && (int)type.GetProperty("BuildCount").GetValue(cache,null)==1,"Unchanged season rebuilt the spring array");
                var second=(Texture)get.Invoke(cache,new object[]{b,b,32,0});
                Require(Sample(mat,second,0,0).r>.9f && Sample(mat,second,3,0).g>Colours[0].g+.07f,"Distant array reused another source's layer order");
                get.Invoke(cache,new object[]{a,a,16,0});
                Require((int)type.GetProperty("BuildCount").GetValue(cache,null)==3,"Transition did not update cache");
                Require(ReferenceEquals(a,get.Invoke(cache,new object[]{a,a,0,0})),"Summer did not restore original terrain");
                Debug.Log("DVSeasons spring runtime cache verified: source arrays isolated, unchanged state reused, transition updated, summer restores originals.");
            }
            finally
            {
                type.GetMethod("Dispose").Invoke(cache,null);
                UnityEngine.Object.DestroyImmediate(a);UnityEngine.Object.DestroyImmediate(b);UnityEngine.Object.DestroyImmediate(mat);bundle.Unload(true);
            }
        }
    }
}
