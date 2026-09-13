using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    public static class WinterWindowVerification
    {
        public static void Preview()
        {
            var path=System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"../../Resources/Runtime/AssetBundles/dvseasons_dv99"));
            var bundle=AssetBundle.LoadFromFile(path);
            if(bundle==null)throw new Exception("Preview bundle missing");
            try {Verify(bundle);} finally {bundle.Unload(true);}
        }
        public static void Verify(AssetBundle bundle)
        {
            var shader = bundle.LoadAsset<Shader>("assets/dvseasons/dv99/shaders/winterwindow.shader");
            if (shader == null || !shader.isSupported) throw new Exception("Winter window shader unavailable.");
            var material = new Material(shader);
            var target = new RenderTexture(256,256,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
            var pixels = new Texture2D(256,256,TextureFormat.RGBA32,false,true);
            var mask = new Texture2D(1,1,TextureFormat.RGBA32,false,true);
            var mesh = new Mesh { vertices = new[] { new Vector3(-1,-1,0),new Vector3(1,-1,0),
                new Vector3(1,1,0),new Vector3(-1,1,0) }, triangles = new[] { 0,1,2,0,2,3 },
                uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up} };
            var cmd = new CommandBuffer(); var previous = RenderTexture.active;
            try
            {
                target.Create(); material.SetMatrix("_MeshToPane",Matrix4x4.identity);
                // Keep the old shader's initial binding for the negative control.
                // Neither binding may be changed when the door moves later.
                material.SetMatrix("_WorldToPane",Matrix4x4.identity);
                material.SetVector("_PaneSize",new Vector4(2,2,1,1)); material.SetFloat("_Daylight",1);
                material.SetTexture("_SnowMask",mask);
                float glassTemperature=0;
                Func<float,float,Color,float> draw = (frost,fog,maskColor) =>
                {
                    mask.SetPixel(0,0,maskColor); mask.Apply();
                    material.SetVector("_Climate",new Vector4(frost,fog,0,glassTemperature));
                    cmd.Clear();cmd.SetRenderTarget(target);cmd.ClearRenderTarget(true,true,Color.black);
                    cmd.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.identity);
                    cmd.DrawMesh(mesh,Matrix4x4.identity,material);Graphics.ExecuteCommandBuffer(cmd);
                    RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,256,256),0,0);pixels.Apply();
                    float sum=0;foreach(var c in pixels.GetPixels())sum+=c.r;
                    return sum/65536;
                };
                float frozen=draw(1,0,Color.black);
                var preview=System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"../../artifacts/build/frost-0.3.13.png"));
                System.IO.File.WriteAllBytes(preview,pixels.EncodeToPNG());
                float min=1,max=0;
                foreach(var c in pixels.GetPixels()) { min=Mathf.Min(min,c.r);max=Mathf.Max(max,c.r); }
                if(max-min<.08f) throw new Exception("Frozen pane lost crystal contrast.");
                float thawing=draw(.45f,0,Color.black);
                float fogged=draw(0,.65f,Color.black),wet=draw(0,0,Color.black);
                foreach(var c in pixels.GetPixels()) if(c.maxColorComponent>.001f && c.r+c.g+c.b>.001f)
                    throw new Exception("Zero frost still leaves visible pixels.");
                float snow=draw(0,0,Color.red),wiped=draw(1,0,Color.green);
                if(frozen<.25f || thawing<.02f || thawing>=frozen || fogged<.05f || fogged>=frozen ||
                    wet>.01f || snow<.5f || wiped>.01f)
                    throw new Exception("Winter window GPU states mismatch: "+frozen+","+thawing+","+fogged+","+wet+","+snow+","+wiped);
                Debug.Log("DVSeasons winter window GPU verified: frozen, thawing, fogged, native-wet transparency, snow deposits and wiper clearing.");
                // After crystals have thawed, the swept area must become clear
                // while the cloudy film survives outside the blade's path.
                glassTemperature=6;
                draw(0,.65f,Color.black);var mistReference=pixels.GetPixels32();
                var sweptMask=new Texture2D(2,1,TextureFormat.RGBA32,false,true)
                    {filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};
                try
                {
                    sweptMask.SetPixels(new[]{Color.green,Color.black});sweptMask.Apply();
                    material.SetTexture("_SnowMask",sweptMask);
                    draw(0,.65f,Color.black);var clearedPixels=pixels.GetPixels32();
                    float untouched=0;
                    for(int y=0;y<256;y++) for(int x=0;x<256;x++)
                    {
                        int i=y*256+x;
                        if(x<120 && clearedPixels[i].r+clearedPixels[i].g+clearedPixels[i].b>1)
                            throw new Exception("Wiper retained cloudy film after the crystals thawed.");
                        if(x>136)
                        {
                            if(Math.Abs(clearedPixels[i].r-mistReference[i].r)>1)
                                throw new Exception("Wiper cleared cloudy film outside its swept area.");
                            untouched+=clearedPixels[i].r/255f;
                        }
                    }
                    if(untouched/(119*256)<.05f) throw new Exception("Unwiped cloudy film vanished.");
                    System.IO.File.WriteAllBytes(System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,
                        "../../artifacts/build/window-wiper-film-0.3.18.png")),pixels.EncodeToPNG());
                }
                finally { material.SetTexture("_SnowMask",mask);UnityEngine.Object.DestroyImmediate(sweptMask); }
                glassTemperature=0;
                Debug.Log("DVSeasons wipers clear the thawed cloudy film only inside the swept area.");
                // Material properties are captured before LateUpdate. Change the
                // root and door transforms afterwards, without refreshing those
                // properties. This reproduces the frame-order bug in 0.3.14.
                var frames=new[] {
                    Matrix4x4.Translate(new Vector3(.25f,0,0)),
                    Matrix4x4.TRS(new Vector3(234,18,-421),Quaternion.Euler(0,63,0),Vector3.one),
                    Matrix4x4.TRS(new Vector3(4300,0,-4200),Quaternion.Euler(0,25,0),Vector3.one)*
                        Matrix4x4.TRS(new Vector3(.48f,1.2f,0),Quaternion.Euler(0,82,0),new Vector3(1,.9f,1)),
                    Matrix4x4.TRS(new Vector3(.2f,0,-.3f),Quaternion.Euler(0,25,0),Vector3.one)*
                        Matrix4x4.TRS(new Vector3(.65f,1.2f,0),Quaternion.Euler(0,0,0),Vector3.one)
                };
                for(int baked=0;baked<=1;baked++)
                {
                    material.SetFloat("_UseBakedUVs",baked);
                    draw(.7f,.15f,Color.black);var reference=pixels.GetPixels32();
                    foreach(var moving in frames)
                    {
                        cmd.Clear();cmd.SetRenderTarget(target);cmd.ClearRenderTarget(true,true,Color.black);
                        cmd.SetViewProjectionMatrices(moving.inverse,Matrix4x4.identity);
                        cmd.DrawMesh(mesh,moving,material);Graphics.ExecuteCommandBuffer(cmd);
                        RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,256,256),0,0);pixels.Apply();
                        float error=0;var actual=pixels.GetPixels32();
                        for(int i=0;i<actual.Length;i++) error+=Math.Abs(actual[i].r-reference[i].r)/255f;
                        if(error/actual.Length>.025f) throw new Exception("Late moving glass projection drifted: "+error/actual.Length+", baked="+baked);
                    }
                }
                // Thermal fading must preserve the spatial crystal pattern.
                // A low mean frame delta alone also accepts an erased pattern.
                draw(.65f,0,Color.black);var crystalReference=pixels.GetPixels32();
                glassTemperature=3;
                float warmPattern=draw(.65f,0,Color.black);
                var warmPixels=pixels.GetPixels32();float patternError=0;
                for(int i=0;i<warmPixels.Length;i++)
                    patternError+=Math.Abs(warmPixels[i].r-crystalReference[i].r*(20f/27f))/255f;
                if(patternError/warmPixels.Length>.008f || warmPattern<.12f)
                    throw new Exception("Thawing erased crystal shapes instead of fading their opacity: "+patternError/warmPixels.Length+", brightness="+warmPattern);
                glassTemperature=2;
                draw(.65f,0,Color.black);var earlyPixels=pixels.GetPixels32();
                for(int i=0;i<earlyPixels.Length;i++)
                    if(Math.Abs(earlyPixels[i].r-crystalReference[i].r)>1)
                        throw new Exception("Crystal pattern faded before +2C.");
                var comparison = new Texture2D(1024,256,TextureFormat.RGBA32,false,true);
                try
                {
                    for(int phase=0;phase<4;phase++)
                    {
                        glassTemperature=phase<2 ? 0 : phase==2 ? 3 : 4.5f;
                        draw(phase==0 ? 1 : .65f,0,Color.black);
                        comparison.SetPixels(phase*256,0,256,256,pixels.GetPixels());
                    }
                    comparison.Apply();
                    System.IO.File.WriteAllBytes(System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,
                        "../../artifacts/build/frost-pattern-0.3.17.png")),comparison.EncodeToPNG());
                }
                finally {UnityEngine.Object.DestroyImmediate(comparison);}
                glassTemperature=0;
                Debug.Log("DVSeasons frost pattern preserved through partial thaw; crystal opacity fades independently of coverage.");
                // Sweep across both temperature thresholds with deliberately
                // full restored layers, so a hard cutoff cannot hide behind
                // the climate simulation having already melted most of them.
                for(int layer=0;layer<3;layer++)
                {
                    Color32[] last=null;
                    mask.SetPixel(0,0,layer==1 ? Color.red : Color.black);mask.Apply();
                    for(int step=-1;step<=121;step++)
                    {
                        material.SetVector("_Climate",new Vector4(layer==0?1:0,layer==2?.65f:0,0,step*.1f));
                        cmd.Clear();cmd.SetRenderTarget(target);cmd.ClearRenderTarget(true,true,Color.black);
                        cmd.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.identity);
                        cmd.DrawMesh(mesh,Matrix4x4.identity,material);Graphics.ExecuteCommandBuffer(cmd);
                        RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,256,256),0,0);pixels.Apply();
                        var current=pixels.GetPixels32();
                        if(last!=null)
                        {
                            float difference=0;
                            for(int i=0;i<current.Length;i++) difference+=Math.Abs(current[i].r-last[i].r)/255f;
                            if(difference/current.Length>.035f)
                                throw new Exception("Glass layer popped during thaw: layer="+layer+", temperature="+step*.1f+", delta="+difference/current.Length);
                        }
                        last=current;
                    }
                }
                Debug.Log("DVSeasons glass transition GPU sweep passed: frost, deposited snow and fog fade continuously across +5C/+12C.");
                material.SetFloat("_UseBakedUVs",1);
                material.SetVector("_Climate",new Vector4(1,0,0,5));
                mask.SetPixel(0,0,Color.red);mask.Apply();
                cmd.Clear();cmd.SetRenderTarget(target);cmd.ClearRenderTarget(true,true,Color.black);
                cmd.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.identity);
                cmd.DrawMesh(mesh,Matrix4x4.identity,material);Graphics.ExecuteCommandBuffer(cmd);
                RenderTexture.active=target;pixels.ReadPixels(new Rect(0,0,256,256),0,0);pixels.Apply();
                foreach(var c in pixels.GetPixels()) if(c.r+c.g+c.b>.001f)
                    throw new Exception("Warm glass retained ice/snow pixels at +5C.");
                Debug.Log("DVSeasons winter windows: late root motion, sliding/hinged panes, origin shifts, both UV modes and exact +5C clearing verified.");
            }
            finally
            {
                RenderTexture.active=previous;cmd.Dispose();
                foreach(var obj in new UnityEngine.Object[]{material,target,pixels,mask,mesh}) UnityEngine.Object.DestroyImmediate(obj);
            }
        }
    }
}
