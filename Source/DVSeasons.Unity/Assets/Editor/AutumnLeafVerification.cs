using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class AutumnLeafVerification
    {
        public static void Verify(AssetBundle bundle)
        {
            var shader=bundle.LoadAsset<Shader>("assets/dvseasons/dv99/shaders/autumnleaf.shader");
            if(shader==null || !shader.isSupported)throw new Exception("Leaf shader unavailable");
            var material=new Material(shader);
            var target=new RenderTexture(64,64,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            var pixels=new Texture2D(64,64,TextureFormat.RGBA32,false,true);
            var mesh=new Mesh {vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)},
                triangles=new[]{0,1,2,0,2,3},normals=new[]{Vector3.forward,Vector3.forward,Vector3.forward,Vector3.forward},
                colors=new[]{Color.white,Color.white,Color.white,Color.white},uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up}};
            var cmd=new CommandBuffer();var previous=RenderTexture.active;
            try
            {
                target.Create();material.SetTexture("_MainTex",Texture2D.whiteTexture);
                Func<float,float,float> draw=(light,ambient)=>
                {
                    material.SetVector("_LeafAmbientSky",Vector4.one*ambient);
                    material.SetVector("_LeafAmbientEquator",Vector4.one*ambient);
                    material.SetVector("_LeafAmbientGround",Vector4.one*ambient);
                    cmd.Clear();cmd.SetRenderTarget(target);cmd.ClearRenderTarget(true,true,Color.black);
                    cmd.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.identity);
                    cmd.SetGlobalVector("_WorldSpaceLightPos0",new Vector4(0,0,light>0 ? 1 : 0,0));
                    cmd.SetGlobalVector("_LightColor0",new Vector4(light,light,light,1));
                    foreach(var n in new[]{"unity_SHAr","unity_SHAg","unity_SHAb","unity_SHBr","unity_SHBg","unity_SHBb","unity_SHC"})
                        cmd.SetGlobalVector(n,Vector4.zero);
                    cmd.DisableShaderKeyword("VERTEXLIGHT_ON");
                    cmd.DrawMesh(mesh,Matrix4x4.identity,material);Graphics.ExecuteCommandBuffer(cmd);
                    RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,64,64),0,0);pixels.Apply();
                    float sum=0;foreach(var c in pixels.GetPixels())sum+=c.r;return sum/4096;
                };
                float day=draw(1,0),overcast=draw(0,.2f),night=draw(0,.002f),dark=draw(0,0);
                if(day<.2f || overcast<.15f || night>overcast*.04f || dark>.001f)
                    throw new Exception("Leaf scene lighting mismatch: "+day+","+overcast+","+night+","+dark);
                Debug.Log("DVSeasons leaf GPU verified: day="+day+", overcast="+overcast+
                    ", night="+night+", dark="+dark+", no emission.");
            }
            finally
            {
                RenderTexture.active=previous;cmd.Dispose();
                foreach(var o in new UnityEngine.Object[]{material,target,pixels,mesh})UnityEngine.Object.DestroyImmediate(o);
            }
        }
    }
}
