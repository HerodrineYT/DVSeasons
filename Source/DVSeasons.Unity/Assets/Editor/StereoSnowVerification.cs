using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DVSeasons.AssetBundleBuild
{
    // A headset-free test of the production double-wide fragment variant. Each
    // eye has its own off-axis perspective projection, camera and native data.
    // Explicit viewports/uniforms replace only XR device submission; the snow
    // shader and Unity's stereo UV helper are the real production code.
    public static class StereoSnowVerification
    {
        const string ShaderPath="Assets/DVSeasons/DV99/Shaders/ProceduralSnow.shader";
        const string ProbePath="Assets/Editor/StereoSnowProbe.shader";
        const int Width=320,Height=192;
        static readonly Color Untouched=new Color(-4,-3,-2,-1);
        static readonly Vector4[] ScaleOffsets={new Vector4(.5f,1,0,0),new Vector4(.5f,1,.5f,0)};
        static readonly string[] Inputs={"_CameraDepthTexture","_DVPSVehicleData","_DVPSVehicleSlope","_DVPSNormal","_DVPSDiffuse","_DVPSSpecular","_DVPSLighting"};
        public static void RunSource() {Run(false);}
        public static void RunPacked() {Run(true);}
        static void Run(bool packed)
        {
            int code=0;AssetBundle bundle=null;
            try
            {
                if(packed)
                {
                    var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
                    bundle=AssetBundle.LoadFromFile(Path.Combine(root,"artifacts/build/DVSeasons/AssetBundles/dvseasons_dv99"));
                    Require(bundle!=null,"Packed stereo snow bundle unavailable");
                }
                Verify(bundle);
            }
            catch(Exception error) {Debug.LogException(error);code=1;}
            finally {if(bundle!=null)bundle.Unload(true);}
            EditorApplication.Exit(code);
        }
        public static void Verify(AssetBundle bundle)
        {
            VerifyDescriptors();
            var shader=bundle==null?AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath):bundle.LoadAsset<Shader>(ShaderPath.ToLowerInvariant());
            Require(shader!=null && shader.isSupported,"Production stereo snow shader unavailable");
            using(var adapter=new StereoVerificationShaderAdapter(ShaderPath))
            using(var probeAdapter=new StereoVerificationShaderAdapter(ProbePath))
            using(var fixture=new Fixture(shader,adapter.Shader,probeAdapter.Shader))
            {
                fixture.VerifyProbe();
                foreach(bool hdr in new[]{false,true})
                foreach(float amount in new[]{.37f,1f})fixture.Compare(hdr,amount);
                fixture.VerifyNegativeControls();
            }
            Debug.Log("STEREO_SNOW_OK: asymmetric perspective eyes; packed double-wide depth/normal/vehicle/native GBuffer UVs; per-eye inverse VP; HDR/LDR; wrong-eye and wrong-UV negative controls; source="+(bundle==null)+" stereo_pixel_shader=current_source_with_device_uniform_adapter mono_shader="+(bundle==null?"source":"packed")+" headset_submission_tested=false");
        }
        static void VerifyDescriptors()
        {
            var root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            var assembly=Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.dll"));
            var type=assembly.GetType("DVSeasons.Mod.StereoRenderSupport",true);
            var convert=type.GetMethod("ColorDescriptor",BindingFlags.Static|BindingFlags.NonPublic);
            Require(convert!=null,"Stereo target descriptor helper unavailable");
            int checkedCases=0;
            foreach(var layout in new[]{0,1,2})
            foreach(int divisor in new[]{1,2,3})
            foreach(var format in new[]{RenderTextureFormat.ARGBHalf,RenderTextureFormat.R8})
            {
                int width=layout==0?77:2014,height=layout==0?55:1100;
                var source=new RenderTextureDescriptor(width,height,RenderTextureFormat.ARGB32,24)
                {
                    dimension=layout==2?TextureDimension.Tex2DArray:TextureDimension.Tex2D,
                    volumeDepth=layout==2?2:1,vrUsage=layout==0?VRTextureUsage.None:VRTextureUsage.TwoEyes,
                    msaaSamples=4,bindMS=true,sRGB=true,useMipMap=true,autoGenerateMips=true,
                    enableRandomWrite=true,memoryless=RenderTextureMemoryless.Depth,useDynamicScale=true
                };
                var actual=(RenderTextureDescriptor)convert.Invoke(null,new object[]{source,format,divisor});
                int expectedWidth=width/divisor;
                if(layout==1 && divisor>1)expectedWidth=(expectedWidth+1)&~1;
                Require(actual.width==expectedWidth && actual.height==height/divisor &&
                    actual.dimension==source.dimension && actual.volumeDepth==source.volumeDepth && actual.vrUsage==source.vrUsage &&
                    actual.colorFormat==format && actual.depthBufferBits==0 && actual.msaaSamples==1 && !actual.bindMS &&
                    !actual.sRGB && !actual.useMipMap && !actual.autoGenerateMips && !actual.enableRandomWrite &&
                    actual.memoryless==RenderTextureMemoryless.None && actual.useDynamicScale,
                    "Stereo descriptor conversion lost target layout/format: layout="+layout+" divisor="+divisor+" format="+format);
                checkedCases++;
            }
            Debug.Log("STEREO_SNOW_DESCRIPTOR_OK: cases="+checkedCases+" mono/double-wide/array eye layout retained; asymmetric mirror size not used; half-size packed-eye width stays even");
        }
        static void Require(bool value,string message) {if(!value)throw new InvalidOperationException(message);}
        static float Difference(Color a,Color b)
        {return Mathf.Max(Mathf.Max(Mathf.Abs(a.r-b.r),Mathf.Abs(a.g-b.g)),Mathf.Max(Mathf.Abs(a.b-b.b),Mathf.Abs(a.a-b.a)));}
        static bool Finite(Color value)
        {return !(float.IsNaN(value.r)||float.IsNaN(value.g)||float.IsNaN(value.b)||float.IsNaN(value.a)||float.IsInfinity(value.r)||float.IsInfinity(value.g)||float.IsInfinity(value.b)||float.IsInfinity(value.a));}

        sealed class Fixture:IDisposable
        {
            readonly List<UnityEngine.Object> resources=new List<UnityEngine.Object>();
            readonly Material left,right,stereo,probe;
            readonly SnowProceduralCostVerification.Fixture mono;
            readonly Matrix4x4[] inverse=new Matrix4x4[2];
            readonly Color[][] world=new Color[2][];
            readonly Color[][][] inputValues=new Color[2][][];
            readonly RenderTexture[] targets=new RenderTexture[3];
            readonly RenderTargetIdentifier[] identifiers=new RenderTargetIdentifier[3];
            readonly Texture2D reader;
            readonly CommandBuffer commands=new CommandBuffer {name="Headset-free double-wide snow verification"};
            readonly Mesh quad;
            readonly RenderTexture previous;
            Color[][][] expected;
            T Keep<T>(T value) where T:UnityEngine.Object {resources.Add(value);return value;}
            public Fixture(Shader shader,Shader stereoShader,Shader probeShader)
            {
                previous=RenderTexture.active;
                left=Keep(new Material(shader));right=Keep(new Material(shader));stereo=Keep(new Material(stereoShader));
                probe=Keep(new Material(probeShader));
                var actualStereo=Keep(new Material(shader));actualStereo.EnableKeyword("UNITY_SINGLE_PASS_STEREO");
                ShaderUtil.CompilePass(actualStereo,0,true);
                stereo.EnableKeyword("UNITY_SINGLE_PASS_STEREO");probe.EnableKeyword("UNITY_SINGLE_PASS_STEREO");
                left.DisableKeyword("UNITY_SINGLE_PASS_STEREO");right.DisableKeyword("UNITY_SINGLE_PASS_STEREO");
                mono=new SnowProceduralCostVerification.Fixture(Width,Height,left,right,stereo);
                mono.Prepare(new SnowProceduralCostVerification.Scenario("stereo-world") {GradientX=.13f,GradientZ=-.06f,Shelter=true,Span=30});
                for(int eye=0;eye<2;eye++)BuildEye(eye);
                for(int input=0;input<Inputs.Length;input++)
                {
                    var format=input==0?TextureFormat.RFloat:input==1?TextureFormat.RGBAHalf:TextureFormat.RGBAFloat;
                    left.SetTexture(Inputs[input],Texture(Width,Height,format,inputValues[0][input]));
                    right.SetTexture(Inputs[input],Texture(Width,Height,format,inputValues[1][input]));
                    var packed=new Color[Width*2*Height];
                    for(int y=0;y<Height;y++)for(int eye=0;eye<2;eye++)
                        Array.Copy(inputValues[eye][input],y*Width,packed,y*Width*2+eye*Width,Width);
                    var texture=Texture(Width*2,Height,format,packed);stereo.SetTexture(Inputs[input],texture);
                    if(input==0)probe.SetTexture(Inputs[input],texture);
                }
                left.SetMatrix("_DVPSInverseVP",inverse[0]);right.SetMatrix("_DVPSInverseVP",inverse[1]);
                // Using mono VP in the stereo branch must fail visibly, even if
                // the two headset eyes happen to have almost identical matrices.
                var poison=Matrix4x4.identity;poison.m13=800;stereo.SetMatrix("_DVPSInverseVP",poison);
                for(int target=0;target<3;target++)
                {
                    targets[target]=Keep(new RenderTexture(Width*2,Height,target==0?24:0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));
                    targets[target].Create();identifiers[target]=new RenderTargetIdentifier(targets[target]);
                }
                reader=Keep(new Texture2D(Width*2,Height,TextureFormat.RGBAFloat,false,true));
                quad=Keep(new Mesh {vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)},
                    uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up},triangles=new[]{0,1,2,0,2,3}});
                ShaderUtil.CompilePass(stereo,0,true);ShaderUtil.CompilePass(probe,0,true);
                Require(!ShaderUtil.ShaderHasError(shader) && !ShaderUtil.ShaderHasError(probe.shader),"Stereo shader variant failed to compile");
            }
            Texture2D Texture(int w,int h,TextureFormat format,Color[] values)
            {
                var texture=Keep(new Texture2D(w,h,format,false,true) {filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});
                texture.SetPixels(values);texture.Apply(false,false);return texture;
            }
            void BuildEye(int eye)
            {
                var position=new Vector3(eye==0?-.043f:.043f,12,eye==0?-8.05f:-7.95f);
                var rotation=Quaternion.LookRotation(new Vector3(eye==0?.015f:-.019f,-1,.45f),Vector3.up);
                var view=Matrix4x4.Scale(new Vector3(1,1,-1))*Matrix4x4.TRS(position,rotation,Vector3.one).inverse;
                var projection=Matrix4x4.Perspective(67,Width/(float)Height,.3f,500);
                projection.m02=eye==0?-.18f:.23f;projection.m12=eye==0?.06f:-.08f;
                var vp=GL.GetGPUProjectionMatrix(projection,true)*view;inverse[eye]=vp.inverse;
                inputValues[eye]=new Color[Inputs.Length][];
                for(int input=0;input<Inputs.Length;input++)inputValues[eye][input]=new Color[Width*Height];
                world[eye]=new Color[Width*Height];
                var planeNormal=new Vector3(-.13f,1,.06f);
                for(int y=0;y<Height;y++)for(int x=0;x<Width;x++)
                {
                    int pixel=y*Width+x;float u=(x+.5f)/Width,v=(y+.5f)/Height;
                    float clipY=(v*2-1)*(SystemInfo.graphicsUVStartsAtTop?-1:1);
                    var near=inverse[eye]*new Vector4(u*2-1,clipY,SystemInfo.usesReversedZBuffer?1:0,1);near/=near.w;
                    var far=inverse[eye]*new Vector4(u*2-1,clipY,SystemInfo.usesReversedZBuffer?0:1,1);far/=far.w;
                    var a=new Vector3(near.x,near.y,near.z);var ray=new Vector3(far.x-near.x,far.y-near.y,far.z-near.z);
                    var hit=a+ray*((2-Vector3.Dot(planeNormal,a))/Vector3.Dot(planeNormal,ray));
                    world[eye][pixel]=new Color(hit.x,hit.y,hit.z,eye+1);
                    var projected=vp*new Vector4(hit.x,hit.y,hit.z,1);
                    float depth=projected.z/projected.w;
                    inputValues[eye][0][pixel]=new Color(depth,0,0,1);
                    // Distinct per-eye patterns ensure accidental sampling of
                    // the other eye cannot pass on a uniform snow field.
                    int category=(x/17+y/11+eye*3)%9;
                    if(category==4)inputValues[eye][0][pixel]=new Color(SystemInfo.usesReversedZBuffer?0:1,0,0,1);
                    inputValues[eye][1][pixel]=category==1?new Color(0,0,0,-1):category==2?new Color(.35f+eye*.3f,0,0,-2):
                        category==5?new Color((u-.5f)*6,3,(v-.5f)*10,2+eye):Color.clear;
                    inputValues[eye][2][pixel]=new Color(eye==0?.83f:.57f,0,0,1);
                    var normal=planeNormal.normalized;
                    if(category==3)normal=new Vector3(.97f,.1f,.22f).normalized;
                    inputValues[eye][3][pixel]=new Color(normal.x*.5f+.5f,normal.y*.5f+.5f,normal.z*.5f+.5f,1);
                    inputValues[eye][4][pixel]=new Color(.12f+eye*.3f+u*.12f,.22f+v*.13f,.31f,.4f+eye*.3f);
                    inputValues[eye][5][pixel]=new Color(.04f+u*.1f,.06f+eye*.07f,.08f,.8f-v*.2f);
                    inputValues[eye][6][pixel]=new Color(.2f+u*.3f,.3f+v*.2f,.4f+eye*.2f,.6f+eye*.2f);
                }
            }
            Color[][] DrawPacked(Material material,Matrix4x4[] matrices,Vector4[] offsets)
            {
                commands.Clear();
                for(int target=0;target<3;target++) {commands.SetRenderTarget(targets[target]);commands.ClearRenderTarget(false,true,Untouched);}
                commands.SetRenderTarget(identifiers,targets[0]);
                commands.SetGlobalMatrixArray("_DVPSInverseVPStereo",matrices);
                commands.SetGlobalVectorArray("unity_StereoScaleOffset",offsets);
                commands.SetGlobalVectorArray("_DVPSVerifyScaleOffset",offsets);
                for(int eye=0;eye<2;eye++)
                {
                    commands.SetGlobalInt("unity_StereoEyeIndex",eye);
                    commands.SetGlobalInt("_DVPSVerifyEye",eye);
                    commands.SetViewport(new Rect(eye*Width,0,Width,Height));
                    commands.DrawMesh(quad,Matrix4x4.identity,material,0,0);
                }
                Graphics.ExecuteCommandBuffer(commands);
                var result=new Color[3][];
                for(int target=0;target<3;target++)
                {RenderTexture.active=targets[target];reader.ReadPixels(new Rect(0,0,Width*2,Height),0,0);reader.Apply(false,false);result[target]=reader.GetPixels();}
                Shader.SetGlobalInt("unity_StereoEyeIndex",0);return result;
            }
            public void VerifyProbe()
            {
                var actual=DrawPacked(probe,inverse,ScaleOffsets);float maxWorld=0,maxUv=0;
                foreach(int eye in new[]{0,1})foreach(int row in new[]{0,Height/2,Height-1})
                {
                    int p=row*Width*2+eye*Width+Width/2;
                    Debug.Log("STEREO_SNOW_PROBE_SAMPLE: eye="+eye+" row="+row+" uv="+actual[1][p].ToString("G7")+" world="+actual[0][p].ToString("G7")+" depth="+actual[2][p].r.ToString("G9"));
                }
                for(int eye=0;eye<2;eye++)for(int y=0;y<Height;y++)for(int x=0;x<Width;x++)
                {
                    int p=y*Width*2+eye*Width+x;
                    int sourceY=y;
                    float depth=inputValues[eye][0][sourceY*Width+x].r;
                    if(depth>0.000001f && depth<.999999f)
                        maxWorld=Mathf.Max(maxWorld,Difference(actual[0][p],world[eye][sourceY*Width+x]));
                    float u=(x+.5f)/Width,v=(sourceY+.5f)/Height;
                    maxUv=Mathf.Max(maxUv,Difference(actual[1][p],new Color(u*.5f+eye*.5f,v,u,v)));
                    Require(Finite(actual[0][p]),"Stereo reconstruction probe produced non-finite world");
                }
                Require(maxWorld<.003f && maxUv<.00001f,"Headset-free stereo uniform/projection setup failed: world="+maxWorld+" uv="+maxUv);
                Debug.Log("STEREO_SNOW_PROBE_OK: eye_index/packed_UV/independent_asymmetric_inverseVP max_world="+maxWorld.ToString("G9")+" max_uv="+maxUv.ToString("G9"));
            }
            public void Compare(bool hdr,float amount)
            {
                mono.Configure(hdr,amount);expected=new[]{mono.Draw(left),mono.Draw(right)};
                var actual=DrawPacked(stereo,inverse,ScaleOffsets);
                int changes;float worst;ComparePixels(actual,out changes,out worst);
                int written=0;
                for(int eye=0;eye<2;eye++)foreach(var pixel in expected[eye][0])if(pixel.r!=Untouched.r)written++;
                Require(written>Width*Height/10,"Stereo mono reference did not exercise snow output");
                Require(changes==0 && worst<.00001f,"Stereo snow differs from per-eye mono: HDR="+hdr+" amount="+amount+" changed_coverage="+changes+" max="+worst);
                Debug.Log("STEREO_SNOW_DIFF: HDR="+hdr+" amount="+amount+" written="+written+" coverage_changes="+changes+" max="+worst.ToString("G9"));
            }
            void ComparePixels(Color[][] actual,out int coverageChanges,out float worst)
            {
                coverageChanges=0;worst=0;
                for(int target=0;target<3;target++)for(int eye=0;eye<2;eye++)for(int y=0;y<Height;y++)for(int x=0;x<Width;x++)
                {
                    var a=expected[eye][target][y*Width+x];var b=actual[target][y*Width*2+eye*Width+x];
                    Require(Finite(a)&&Finite(b),"Stereo snow output was not finite");
                    if((a.r==Untouched.r)!=(b.r==Untouched.r))coverageChanges++;
                    worst=Mathf.Max(worst,Difference(a,b));
                }
            }
            public void VerifyNegativeControls()
            {
                // Require the fixture to detect both independent historical
                // defects; successful output cannot be merely blank or uniform.
                int coverage;float max;
                ComparePixels(DrawPacked(stereo,new[]{inverse[0],inverse[0]},ScaleOffsets),out coverage,out max);
                Require(coverage>100 || max>.01f,"Wrong inverse VP negative control did not affect snow");
                Debug.Log("STEREO_SNOW_NEGATIVE: same_inverse_for_both_eyes coverage="+coverage+" max="+max);
                ComparePixels(DrawPacked(stereo,inverse,new[]{new Vector4(1,1,0,0),new Vector4(1,1,0,0)}),out coverage,out max);
                Require(coverage>100 || max>.01f,"Unpacked UV negative control did not affect snow");
                Debug.Log("STEREO_SNOW_NEGATIVE: sampling_unpacked_UV coverage="+coverage+" max="+max);
            }
            public void Dispose()
            {
                RenderTexture.active=previous;commands.Dispose();mono.Dispose();
                foreach(var resource in resources)UnityEngine.Object.DestroyImmediate(resource);
            }
        }
    }
}
