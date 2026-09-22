using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    // Device submission is replaced by StereoVerificationShaderAdapter; production
    // fragment code is compared with the real mono shader for each independent eye.
    public static class StereoPostEffectsVerification
    {
        const string Directory = "Assets/DVSeasons/DV99/Shaders/";
        const int Width = 96, Height = 64;
        static readonly Vector4[] EyeOffsets = { new Vector4(.5f,1,0,0), new Vector4(.5f,1,.5f,0) };
        static readonly Color Clear = new Color(.13f,.21f,.37f,.43f);
        public static void Run() { Run(false); }
        public static void RunPacked() { Run(true); }
        static void Run(bool packed)
        {
            int code = 0;
            AssetBundle bundle = null;
            try
            {
                if (packed)
                {
                    var root = Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
                    bundle = AssetBundle.LoadFromFile(Path.Combine(root,"artifacts/build/DVSeasons/AssetBundles/dvseasons_dv99"));
                    Require(bundle != null,"Post-effects verification bundle missing");
                }
                Verify(bundle);
            }
            catch (Exception error) { Debug.LogException(error); code = 1; }
            finally { if (bundle != null) bundle.Unload(true); }
            EditorApplication.Exit(code);
        }
        public static void Verify(AssetBundle bundle)
        {
            foreach (var name in new[] { "PuddleIceGBuffer", "SnowGlare" })
            {
                var path = Directory + name + ".shader";
                var shader = bundle == null ? AssetDatabase.LoadAssetAtPath<Shader>(path) : bundle.LoadAsset<Shader>(path.ToLowerInvariant());
                Require(shader != null && shader.isSupported,"Production post-effect unavailable: " + name);
                using (var adapter = new StereoVerificationShaderAdapter(path,true))
                using (var fixture = new Fixture(shader,adapter.Shader,path,name == "PuddleIceGBuffer")) fixture.Verify();
            }
            VerifyVehicleVariants(bundle);
            Debug.Log("STEREO_POST_EFFECTS_OK: independent packed-eye inputs, per-eye puddle reconstruction, mask/depth/color/smoothness, preserved specular RGB, glare sky exclusion, wrong-UV and wrong-matrix negative controls; source=" + (bundle == null) + " headset_submission_tested=false");
        }
        static void VerifyVehicleVariants(AssetBundle bundle)
        {
            var path=Directory+"SnowVehicle.shader";
            var shader=bundle==null?AssetDatabase.LoadAssetAtPath<Shader>(path):bundle.LoadAsset<Shader>(path.ToLowerInvariant());
            Require(shader!=null && shader.isSupported,"SnowVehicle shader unavailable for variant compilation");
            var material=new Material(shader);
            try
            {
                material.EnableKeyword("UNITY_SINGLE_PASS_STEREO");
                foreach(int pass in new[]{0,2,5,6})
                {
                    material.DisableKeyword("INSTANCING_ON");
                    ShaderUtil.CompilePass(material,pass,true);
                    Require(!ShaderUtil.ShaderHasError(shader),"SnowVehicle stereo variant compilation failed: pass="+pass);
                    if(pass==5 || pass==6)
                    {
                        material.EnableKeyword("INSTANCING_ON");
                        ShaderUtil.CompilePass(material,pass,true);
                        Require(!ShaderUtil.ShaderHasError(shader),"SnowVehicle instanced stereo variant compilation failed: pass="+pass);
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
            Debug.Log("STEREO_VEHICLE_VARIANTS_OK: passes=0,2,5,6; instanced_passes=5,6; source="+(bundle==null)+" pixel_submission_tested=false");
        }
        static void Require(bool value,string message) { if (!value) throw new InvalidOperationException(message); }
        static float Difference(Color a,Color b)
        { return Mathf.Max(Mathf.Max(Mathf.Abs(a.r-b.r),Mathf.Abs(a.g-b.g)),Mathf.Max(Mathf.Abs(a.b-b.b),Mathf.Abs(a.a-b.a))); }
        sealed class Fixture : IDisposable
        {
            readonly bool puddle;
            readonly List<UnityEngine.Object> objects = new List<UnityEngine.Object>();
            readonly Material[] mono = new Material[2];
            readonly Material stereo;
            readonly Material productionStereo;
            readonly RenderTexture[] monoTargets = new RenderTexture[2];
            readonly RenderTexture packedTarget;
            readonly Texture2D monoReader,packedReader;
            readonly CommandBuffer commands = new CommandBuffer { name = "Headset-free stereo post-effects" };
            readonly Matrix4x4[] inverses = new Matrix4x4[2];
            readonly Mesh quad;
            readonly RenderTexture previous;
            readonly Color[][] original = new Color[2][];
            readonly Color[][] depths = new Color[2][];
            Color[][] expected;
            T Keep<T>(T value) where T : UnityEngine.Object { objects.Add(value); return value; }
            public Fixture(Shader shader,Shader adapted,string path,bool isPuddle)
            {
                puddle = isPuddle; previous = RenderTexture.active;
                mono[0] = Keep(new Material(shader)); mono[1] = Keep(new Material(shader));
                productionStereo = Keep(new Material(shader)); productionStereo.EnableKeyword("UNITY_SINGLE_PASS_STEREO");
                stereo = Keep(new Material(adapted));
                stereo.EnableKeyword("UNITY_SINGLE_PASS_STEREO");
                for (int pass=0;pass<(puddle?2:1);pass++)
                {
                    ShaderUtil.CompilePass(productionStereo,pass,true);
                    ShaderUtil.CompilePass(stereo,pass,true);
                }
                Require(!ShaderUtil.ShaderHasError(shader) && !ShaderUtil.ShaderHasError(stereo.shader),"Post-effect stereo variant failed to compile: "+path);
                quad = Keep(new Mesh { vertices = new[] { new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0) },
                    uv = new[] { Vector2.zero,Vector2.right,Vector2.one,Vector2.up },triangles = new[] { 0,1,2,0,2,3 } });
                for (int eye=0;eye<2;eye++)
                {
                    mono[eye].DisableKeyword("UNITY_SINGLE_PASS_STEREO");
                    monoTargets[eye] = Target(Width);
                    inverses[eye] = Matrix4x4.TRS(new Vector3(eye*2.71f,0,eye*1.31f),Quaternion.Euler(13+eye*2,5,0),new Vector3(3,2,4));
                    mono[eye].SetMatrix("_DVInverseViewProjection",inverses[eye]);
                }
                packedTarget = Target(Width*2);
                monoReader = Keep(new Texture2D(Width,Height,TextureFormat.RGBAFloat,false,true));
                packedReader = Keep(new Texture2D(Width*2,Height,TextureFormat.RGBAFloat,false,true));
                var masks = new Color[2][];
                var smoothness = new Color[2][];
                for (int eye=0;eye<2;eye++)
                {
                    original[eye] = new Color[Width*Height]; depths[eye] = new Color[Width*Height];
                    masks[eye] = new Color[Width*Height]; smoothness[eye] = new Color[Width*Height];
                    for (int y=0;y<Height;y++) for(int x=0;x<Width;x++)
                    {
                        int p=y*Width+x,category=(x/9+(puddle?y/7:0)+eye*3)%7;
                        float u=(x+.5f)/Width,v=(y+.5f)/Height;
                        float brightness=.15f+category*.53f+eye*.21f;
                        original[eye][p] = category==2 ? new Color(2,.08f,.1f,.3f+eye*.3f) : new Color(brightness,brightness,brightness,.2f+.5f*u);
                        depths[eye][p] = new Color(category==1 || category==4 ? (SystemInfo.usesReversedZBuffer?0:1) : .2f+.6f*v,0,0,1);
                        masks[eye][p] = new Color(category<2?0:category*.14f,0,0,1);
                        smoothness[eye][p] = new Color(.12f+.3f*u+eye*.23f,0,0,1);
                    }
                }
                Bind("_MainTex",original,TextureFormat.RGBAFloat);
                Bind("_CameraDepthTexture",depths,TextureFormat.RFloat);
                Bind("_WetDecalSaturationMask",masks,TextureFormat.RFloat);
                Bind("_DVOriginalSpecular",smoothness,TextureFormat.RFloat);
                var iceValues = new Color[Width*Height];
                for(int y=0;y<Height;y++) for(int x=0;x<Width;x++)
                    iceValues[y*Width+x] = new Color(((x/3+y/5)%5)/4f,(x%11)/10f,(y%13)/12f,1);
                var ice = Texture(Width,iceValues,TextureFormat.RGBAFloat); ice.wrapMode = TextureWrapMode.Repeat;
                foreach(var material in new[]{mono[0],mono[1],stereo})
                { material.SetTexture("_IceTex",ice); material.SetFloat("_TileSize",3.5f); }
                // A stereo reconstruction accidentally taking the mono matrix must fail.
                stereo.SetMatrix("_DVInverseViewProjection",Matrix4x4.Scale(new Vector3(300,200,100)));
            }
            RenderTexture Target(int width)
            { var target=Keep(new RenderTexture(width,Height,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear)); target.Create(); return target; }
            Texture2D Texture(int width,Color[] pixels,TextureFormat format)
            {
                var texture=Keep(new Texture2D(width,Height,format,false,true) { filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp });
                texture.SetPixels(pixels);texture.Apply(false,false);return texture;
            }
            void Bind(string name,Color[][] values,TextureFormat format)
            {
                for(int eye=0;eye<2;eye++)mono[eye].SetTexture(name,Texture(Width,values[eye],format));
                var packed=new Color[Width*2*Height];
                for(int y=0;y<Height;y++)for(int eye=0;eye<2;eye++)
                    Array.Copy(values[eye],y*Width,packed,y*Width*2+eye*Width,Width);
                stereo.SetTexture(name,Texture(Width*2,packed,format));
            }
            Color[] Read(RenderTexture target,Texture2D reader)
            { RenderTexture.active=target;reader.ReadPixels(new Rect(0,0,target.width,target.height),0,0);reader.Apply(false,false);return reader.GetPixels(); }
            Color[] DrawMono(int eye,int pass)
            {
                commands.Clear();commands.SetRenderTarget(monoTargets[eye]);commands.ClearRenderTarget(false,true,Clear);
                commands.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.identity);
                commands.SetViewport(new Rect(0,0,Width,Height));commands.DrawMesh(quad,Matrix4x4.identity,mono[eye],0,pass);
                Graphics.ExecuteCommandBuffer(commands);return Read(monoTargets[eye],monoReader);
            }
            Color[] DrawPacked(int pass,Vector4[] offsets,Matrix4x4[] matrices)
            {
                commands.Clear();commands.SetRenderTarget(packedTarget);commands.ClearRenderTarget(false,true,Clear);
                commands.SetViewProjectionMatrices(Matrix4x4.identity,Matrix4x4.identity);
                commands.SetGlobalVectorArray("_DVPSVerifyScaleOffset",offsets);
                commands.SetGlobalMatrixArray("_DVInverseViewProjectionStereo",matrices);
                for(int eye=0;eye<2;eye++)
                {
                    commands.SetGlobalInt("_DVPSVerifyEye",eye);
                    commands.SetViewport(new Rect(eye*Width,0,Width,Height));commands.DrawMesh(quad,Matrix4x4.identity,stereo,0,pass);
                }
                Graphics.ExecuteCommandBuffer(commands);return Read(packedTarget,packedReader);
            }
            float Compare(Color[] actual)
            {
                float maximum=0;
                for(int y=0;y<Height;y++)for(int eye=0;eye<2;eye++)for(int x=0;x<Width;x++)
                {
                    float difference=Difference(expected[eye][y*Width+x],actual[y*Width*2+eye*Width+x]);
                    Require(!float.IsNaN(difference) && !float.IsInfinity(difference),"Non-finite post-effect output");
                    maximum=Mathf.Max(maximum,difference);
                }
                return maximum;
            }
            public void Verify()
            {
                foreach(int pass in puddle?new[]{0,1}:new[]{0})
                foreach(float amount in new[]{0f,.37f,1f})
                {
                    foreach(var material in new[]{mono[0],mono[1],stereo})
                    { material.SetFloat("_IceAmount",amount);material.SetFloat("_Strength",amount*2); }
                    expected=new[]{DrawMono(0,pass),DrawMono(1,pass)};
                    var actual=DrawPacked(pass,EyeOffsets,inverses);
                    float maximum=Compare(actual);
                    if(maximum>=.0001f)
                        foreach(int eye in new[]{0,1})foreach(int x in new[]{0,Width/2,Width-1})
                            Debug.Log("STEREO_POST_EFFECT_SAMPLE: puddle="+puddle+" pass="+pass+" eye="+eye+" x="+x+" expected="+expected[eye][Height/2*Width+x].ToString("G7")+" actual="+actual[Height/2*Width*2+eye*Width+x].ToString("G7"));
                    Require(maximum<.0001f,"Stereo post-effect differs from mono: puddle="+puddle+" pass="+pass+" amount="+amount+" max="+maximum);
                    if(puddle && pass==0)
                    {
                        foreach(var eye in expected)foreach(var pixel in eye)
                            Require(Mathf.Abs(pixel.r-Clear.r)<.00001f && Mathf.Abs(pixel.g-Clear.g)<.00001f && Mathf.Abs(pixel.b-Clear.b)<.00001f,"Puddle changed specular RGB");
                    }
                    else if(!puddle) VerifyGlareExclusions(amount);
                    Debug.Log("STEREO_POST_EFFECT_DIFF: puddle="+puddle+" pass="+pass+" amount="+amount+" max="+maximum.ToString("G9"));
                }
                // Re-evaluate an active main effect before attempting deliberately wrong inputs.
                expected=new[]{DrawMono(0,0),DrawMono(1,0)};
                var wrongUv=Compare(DrawPacked(0,new[]{new Vector4(1,1,0,0),new Vector4(1,1,0,0)},inverses));
                Require(wrongUv>.025f,"Wrong packed UV negative control did not exercise effect");
                Debug.Log("STEREO_POST_EFFECT_NEGATIVE: puddle="+puddle+" unpacked_uv_max="+wrongUv);
                if(puddle)
                {
                    var wrongMatrix=Compare(DrawPacked(0,EyeOffsets,new[]{inverses[0],inverses[0]}));
                    Require(wrongMatrix>.015f,"Wrong eye matrix negative control did not exercise ice texture");
                    Debug.Log("STEREO_POST_EFFECT_NEGATIVE: same_puddle_inverseVP_max="+wrongMatrix);
                }
            }
            void VerifyGlareExclusions(float amount)
            {
                int changed=0,sky=0;
                for(int eye=0;eye<2;eye++)for(int p=0;p<Width*Height;p++)
                {
                    bool skyPixel=depths[eye][p].r<.00001f || depths[eye][p].r>.99999f;
                    if(amount==0 || skyPixel)Require(Difference(expected[eye][p],original[eye][p])<.002f,"Glare changed sky/zero-strength source");
                    if(skyPixel)sky++;
                    if(Difference(expected[eye][p],original[eye][p])>.01f)changed++;
                    Require(Mathf.Abs(expected[eye][p].a-original[eye][p].a)<.0001f,"Glare changed scene alpha");
                }
                Require(sky>100 && (amount==0 || changed>100),"Glare fixture did not exercise sky and active reduction");
            }
            public void Dispose()
            { RenderTexture.active=previous;commands.Dispose();foreach(var value in objects)UnityEngine.Object.DestroyImmediate(value); }
        }
    }
}
