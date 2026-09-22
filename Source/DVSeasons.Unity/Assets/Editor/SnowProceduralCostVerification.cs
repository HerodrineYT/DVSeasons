using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace DVSeasons.AssetBundleBuild
{
    // The reference is the complete pre-optimization fragment program, not a
    // second implementation of the optimized formulas. No DV session is needed.
    public static class SnowProceduralCostVerification
    {
        const string CurrentPath = "Assets/DVSeasons/DV99/Shaders/ProceduralSnow.shader";
        const string ReferencePath = "Assets/Editor/ProceduralSnowReference20260918.shader";
        const int Width = 320, Height = 192;
        const float MaximumError = 0.000001f; // Permit only floating-point roundoff.
        static readonly Color Untouched = new Color(-4, -3, -2, -1);
        static readonly float[] VehicleSlopes = { 0f, .12f, .121f, .3f, .65f, 1f };

        internal sealed class Scenario
        {
            public string Name;
            public float Center, Span = 30, GradientX, GradientZ, NormalY = 1;
            public float Depth = .5f, DepthSpread;
            public bool Vehicles, Mixed, Shelter, Sky, Limit;
            public Scenario(string name) { Name = name; }
        }

        public static void Run()
        {
            int code = 0;
            try { Verify(); }
            catch (Exception error) { Debug.LogException(error); code = 1; }
            EditorApplication.Exit(code);
        }

        public static void Verify()
        { Verify(null); }

        // Allows release verification to run the same baseline comparison on
        // the actual packed shader after opening the newly built bundle.
        public static void Verify(AssetBundle bundle)
        {
            var current = bundle == null ? Load(CurrentPath) :
                Load(bundle.LoadAsset<Shader>(CurrentPath.ToLowerInvariant()), "provided AssetBundle");
            var reference = Load(ReferencePath);
            var cases = new[]
            {
                new Scenario("near-flat"),
                new Scenario("near-boundary-slope") { Center=112,Span=24,GradientX=.6f,GradientZ=-.2f,NormalY=.72f,Shelter=true },
                new Scenario("far-blend-rocks") { Center=896,Span=180,GradientX=1.2f,GradientZ=.3f,NormalY=.35f,Shelter=true },
                new Scenario("distant-rocks") { Center=1600,Span=450,GradientX=.7f,GradientZ=-.15f,NormalY=.6f,Shelter=true },
                new Scenario("outer-distance-fade") { Center=3700,Span=450,GradientX=.25f,NormalY=.8f,Shelter=true },
                new Scenario("vertical-rejection") { Center=1600,Span=450,GradientX=4,NormalY=.2f },
                new Scenario("slope-threshold") { GradientX=2,GradientZ=.4f,NormalY=.251f,Shelter=true },
                new Scenario("grain-near") { Span=.1f },
                new Scenario("grain-transition") { Span=3.3f },
                new Scenario("vehicle-yard") { Vehicles=true,Shelter=true },
                new Scenario("mixed-surface-edges") { Center=850,Span=350,Vehicles=true,Mixed=true,Shelter=true,Sky=true },
                new Scenario("mixed-shallow-depth") { Center=850,Span=350,GradientX=.6f,GradientZ=.15f,NormalY=.7f,Vehicles=true,Mixed=true,Shelter=true,Depth=.1f,DepthSpread=.09f },
                new Scenario("mixed-deep-depth") { Center=850,Span=350,GradientX=.6f,GradientZ=.15f,NormalY=.7f,Vehicles=true,Mixed=true,Shelter=true,Depth=.9f,DepthSpread=.09f },
                new Scenario("finite-object-mask") { Vehicles=true,Mixed=true,Shelter=true,Limit=true },
                new Scenario("sky-and-exclusions") { Mixed=true,Sky=true },
                new Scenario("all-sky") { Sky=true,NormalY=0 }
            };
            int comparisons = 0;
            float worst = 0;
            try
            {
                using (var fixture = new Fixture(Width, Height, current, reference))
                {
                    foreach (var scenario in cases)
                    {
                        fixture.Prepare(scenario);
                        foreach (bool hdr in new[] { false, true })
                        foreach (float amount in new[] { 0f, .37f, 1f })
                        {
                            fixture.Configure(hdr, amount);
                            var expected = fixture.Draw(reference);
                            var actual = fixture.Draw(current);
                            float max = 0;
                            int changed = 0, changedCoverage = 0, written = 0;
                            for (int target = 0; target < 3; target++)
                            for (int pixel = 0; pixel < expected[target].Length; pixel++)
                            {
                                var a = expected[target][pixel]; var b = actual[target][pixel];
                                if (!Finite(a) || !Finite(b)) throw new Exception("Non-finite snow output: " + scenario.Name);
                                bool coveredA = a.r != Untouched.r, coveredB = b.r != Untouched.r;
                                if (coveredA != coveredB) changedCoverage++;
                                if (target == 0 && coveredA) written++;
                                float delta = Difference(a, b);
                                if (delta > 0) changed++;
                                max = Mathf.Max(max, delta);
                            }
                            if (changedCoverage != 0 || max > MaximumError)
                                throw new Exception("Snow image changed: case=" + scenario.Name + " hdr=" + hdr + " amount=" + amount +
                                    " max=" + max + " coverage=" + changedCoverage + " changed=" + changed);
                            if (amount > 0 && scenario.Name == "near-flat" && written < Width * Height / 100)
                                throw new Exception("Near-flat fixture did not exercise snow output");
                            if ((amount == 0 || scenario.Name == "all-sky" || scenario.Name == "vertical-rejection") && written != 0)
                                throw new Exception("Expected complete rejection for " + scenario.Name + " amount=" + amount);
                            worst = Mathf.Max(worst, max); comparisons++;
                            Debug.Log("SNOW_PROCEDURAL_DIFF: case=" + scenario.Name + " hdr=" + hdr + " amount=" + amount +
                                " max=" + max.ToString("G9") + " changed_channels_pixels=" + changed +
                                " coverage_changes=" + changedCoverage + " written_pixels=" + written);
                        }
                    }
                }
                Debug.Log("SNOW_PROCEDURAL_EQUIVALENCE_OK: comparisons=" + comparisons + " MRT_channels=12 max=" + worst.ToString("G9") +
                    " coverage_changes=0 device=" + SystemInfo.graphicsDeviceName + " API=" + SystemInfo.graphicsDeviceType);
                if (Environment.GetEnvironmentVariable("DVSEASONS_SNOW_GPU_TIMING") == "1") Benchmark(current, reference);
            }
            finally { UnityEngine.Object.DestroyImmediate(current); UnityEngine.Object.DestroyImmediate(reference); }
        }

        static Material Load(string path)
        { return Load(AssetDatabase.LoadAssetAtPath<Shader>(path), path); }

        static Material Load(Shader shader, string label)
        {
            if (shader == null || !shader.isSupported) throw new Exception("Unsupported snow shader: " + label);
            var material = new Material(shader);
            foreach (bool hdr in new[] { false, true })
            {
                if (hdr) material.EnableKeyword("DVPS_FAST_HDR"); else material.DisableKeyword("DVPS_FAST_HDR");
                ShaderUtil.CompilePass(material, 0, true);
            }
            if (ShaderUtil.ShaderHasError(shader))
            {
                foreach (var message in ShaderUtil.GetShaderMessages(shader)) Debug.LogError(message.message);
                UnityEngine.Object.DestroyImmediate(material);
                throw new Exception("Snow shader compile failed: " + label);
            }
            return material;
        }

        static void Benchmark(Material current, Material reference)
        {
            // Optional throughput measurement. The fence is a blocking readback;
            // numbers include CPU submission and synchronization, not pure GPU
            // timestamps, scene FPS, map capture, or vehicle geometry replay.
            using (var fixture = new Fixture(1920, 1080, current, reference))
            {
                foreach (var scenario in new[]
                {
                    new Scenario("distant-rocks") { Center=1600,Span=450,GradientX=.7f,NormalY=.6f,Shelter=true },
                    new Scenario("vehicle-yard") { Vehicles=true,Shelter=true },
                    new Scenario("sky-heavy") { Sky=true,Mixed=true }
                })
                {
                    fixture.Prepare(scenario); fixture.Configure(true, 1);
                    fixture.TimeBatch(reference, 128); fixture.TimeBatch(current, 128);
                    var oldTimes = new List<double>(); var newTimes = new List<double>();
                    for (int trial=0; trial<7; trial++)
                    {
                        if ((trial & 1) == 0) { oldTimes.Add(fixture.TimeBatch(reference, 128)); newTimes.Add(fixture.TimeBatch(current, 128)); }
                        else { newTimes.Add(fixture.TimeBatch(current, 128)); oldTimes.Add(fixture.TimeBatch(reference, 128)); }
                    }
                    oldTimes.Sort(); newTimes.Sort();
                    Debug.Log("SNOW_PROCEDURAL_THROUGHPUT: case=" + scenario.Name + " resolution=1920x1080 draws_per_batch=128 alternating_trials=7" +
                        " reference_ms_per_draw=" + oldTimes[3].ToString("F5") + " current_ms_per_draw=" + newTimes[3].ToString("F5") +
                        " measurement=CPU_submission_plus_GPU_completion_readback not_game_FPS=true");
                }
            }
        }

        static bool Finite(Color value)
        { return !(float.IsNaN(value.r)||float.IsInfinity(value.r)||float.IsNaN(value.g)||float.IsInfinity(value.g)||
            float.IsNaN(value.b)||float.IsInfinity(value.b)||float.IsNaN(value.a)||float.IsInfinity(value.a)); }
        static float Difference(Color a, Color b)
        { return Mathf.Max(Mathf.Max(Mathf.Abs(a.r-b.r),Mathf.Abs(a.g-b.g)),Mathf.Max(Mathf.Abs(a.b-b.b),Mathf.Abs(a.a-b.a))); }

        internal sealed class Fixture : IDisposable
        {
            readonly int width, height;
            readonly Material[] materials;
            readonly List<UnityEngine.Object> resources = new List<UnityEngine.Object>();
            readonly List<UnityEngine.Object> scenarioResources = new List<UnityEngine.Object>();
            readonly Dictionary<string,Texture> boundTextures = new Dictionary<string,Texture>();
            readonly RenderTexture[] targets = new RenderTexture[3];
            readonly RenderTargetIdentifier[] targetIds = new RenderTargetIdentifier[3];
            readonly CommandBuffer commands = new CommandBuffer();
            readonly Mesh quad;
            readonly Texture2D reader, fence;
            readonly RenderTexture previous;

            public Fixture(int width, int height, params Material[] materials)
            {
                this.width=width; this.height=height; this.materials=materials; previous=RenderTexture.active;
                for (int i=0;i<3;i++)
                {
                    targets[i]=Keep(new RenderTexture(width,height,i==0?24:0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear));
                    targets[i].Create(); targetIds[i]=new RenderTargetIdentifier(targets[i]);
                }
                quad=Keep(new Mesh { vertices=new[]{new Vector3(-1,-1,0),new Vector3(1,-1,0),new Vector3(1,1,0),new Vector3(-1,1,0)},
                    uv=new[]{Vector2.zero,Vector2.right,Vector2.one,Vector2.up},triangles=new[]{0,1,2,0,2,3} });
                reader=Keep(new Texture2D(width,height,TextureFormat.RGBAFloat,false,true));
                fence=Keep(new Texture2D(1,1,TextureFormat.RGBAFloat,false,true));
                var noise=new Color[128*128]; uint seed=0x71623u;
                for(int i=0;i<noise.Length;i++) { seed=unchecked(1664525u*seed+1013904223u);float n=(seed>>24)/255f;noise[i]=new Color(n,n,n,1); }
                var noiseTexture=Keep(Texture(128,128,TextureFormat.RGBA32,noise,FilterMode.Bilinear)); noiseTexture.wrapMode=TextureWrapMode.Repeat;
                foreach(var material in materials) material.SetTexture("_DVPSNoise",noiseTexture);
            }

            T Keep<T>(T value) where T:UnityEngine.Object { resources.Add(value); return value; }
            T Scene<T>(T value) where T:UnityEngine.Object { scenarioResources.Add(value); return value; }
            static Texture2D Texture(int w,int h,TextureFormat format,Color[] values,FilterMode filter=FilterMode.Point)
            {
                var texture=new Texture2D(w,h,format,false,true) {filterMode=filter,wrapMode=TextureWrapMode.Clamp};
                texture.SetPixels(values);texture.Apply(false,false);return texture;
            }
            void Bind(string name,Texture value) { boundTextures[name]=value;foreach(var material in materials) material.SetTexture(name,value); }
            internal Texture BoundTexture(string name) {Texture value;return boundTextures.TryGetValue(name,out value)?value:null;}
            void Set(string name,float value) { foreach(var material in materials) material.SetFloat(name,value); }
            void Set(string name,Vector4 value) { foreach(var material in materials) material.SetVector(name,value); }

            public void Prepare(Scenario s,Material globalSideStateMaterial=null)
            {
                boundTextures.Clear();
                foreach(var resource in scenarioResources) UnityEngine.Object.DestroyImmediate(resource); scenarioResources.Clear();
                // Inputs have the verification grid's native dimensions, including
                // one-pixel category boundaries; the optional throughput run uses
                // the same pattern stretched to its larger output targets.
                var data=new Color[Width*Height]; var normal=new Color[data.Length];
                var slope=new Color[data.Length]; var depth=new Color[data.Length]; var diffuse=new Color[data.Length];
                Vector3 n=new Vector3(Mathf.Sqrt(1-s.NormalY*s.NormalY),s.NormalY,0);
                for(int y=0;y<Height;y++) for(int x=0;x<Width;x++)
                {
                    int p=y*Width+x;float u=(x+.5f)/Width,v=(y+.5f)/Height;
                    int category=s.Mixed ? ((x/7+y/5)%6) : (s.Vehicles?1:0);
                    float localX=(u-.5f)*8, localZ=(v-.5f)*14;
                    if(category==1) data[p]=new Color(localX,3,localZ,1+(x/19)%3);
                    else if(category==2) data[p]=new Color(0,0,0,-1);
                    else if(category==3) data[p]=new Color((x%17)/16f,0,0,-2);
                    else if(category==4) data[p]=new Color(.03f,0,-.02f,-3);
                    else data[p]=Color.clear;
                    float ny=s.Vehicles?VehicleSlopes[(y/13)%VehicleSlopes.Length]:s.NormalY;
                    slope[p]=new Color(ny,0,0,1);
                    normal[p]=new Color(n.x*.5f+.5f,n.y*.5f+.5f,.5f,1);
                    bool sky=s.Sky && (s.Name=="all-sky" || y>Height/2 || (x+y)%23==0);
                    depth[p]=new Color(sky?(SystemInfo.usesReversedZBuffer?0:1):s.Depth+(u*2-1)*s.DepthSpread,0,0,1);
                    diffuse[p]=new Color(.18f+u*.1f,.2f+v*.1f,.24f,.35f+.5f*u);
                }
                Bind("_DVPSVehicleData",Scene(Texture(Width,Height,TextureFormat.RGBAHalf,data)));
                Bind("_DVPSVehicleSlope",Scene(Texture(Width,Height,TextureFormat.RFloat,slope)));
                Bind("_CameraDepthTexture",Scene(Texture(Width,Height,TextureFormat.RFloat,depth)));
                Bind("_DVPSNormal",Scene(Texture(Width,Height,TextureFormat.RGBA32,normal)));
                var native=Scene(Texture(Width,Height,TextureFormat.RGBA32,diffuse));
                Bind("_DVPSDiffuse",native);Bind("_DVPSSpecular",native);Bind("_DVPSLighting",native);
                foreach(var map in new[]{new KeyValuePair<string,float>("Near",128),new KeyValuePair<string,float>("Far",1024),new KeyValuePair<string,float>("Distant",4096)})
                {
                    var heights=new Color[1024*1024]; float radius=map.Value;
                    for(int y=0;y<1024;y++) for(int x=0;x<1024;x++)
                    {
                        float wx=((x+.5f)/1024-.5f)*2*radius,wz=((y+.5f)/1024-.5f)*2*radius;
                        float h=2+s.GradientX*wx+s.GradientZ*wz;
                        if(s.Shelter && wx>s.Center-s.Span*.3f && wx<s.Center+s.Span*.2f) h+=3;
                        // Independent shelter discontinuities exercise the old
                        // four comparisons and map-transition endpoints.
                        if(s.Shelter && ((x/31+y/47)%17)==0) h=-100000;
                        heights[y*1024+x]=new Color(h,0,0,1);
                    }
                    Bind("_DVPS"+map.Key+"Height",Scene(Texture(1024,1024,TextureFormat.RFloat,heights)));
                    Set("_DVPS"+map.Key+"Area",new Vector4(0,0,radius,radius*2/1024));
                }
                var vehicleHeights=Scene(new Texture2DArray(256,256,3,TextureFormat.RFloat,false,true) {filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp});
                var vehicleSnow=Scene(new Texture2DArray(256,256,3,TextureFormat.RHalf,false,true) {filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp});
                for(int slice=0;slice<3;slice++)
                {
                    var heights=new Color[256*256];var snow=new Color[heights.Length];
                    for(int y=0;y<256;y++) for(int x=0;x<256;x++)
                    {
                        heights[y*256+x]=new Color(s.Shelter && x>80 && x<145?5:3,0,0,1);
                        snow[y*256+x]=new Color(x<85?0:x<170?.4f:1,0,0,1);
                    }
                    vehicleHeights.SetPixels(heights,slice);vehicleSnow.SetPixels(snow,slice);
                }
                vehicleHeights.Apply();vehicleSnow.Apply();Bind("_DVPSVehicleHeights",vehicleHeights);Bind("_DVPSVehicleSnow",vehicleSnow);
                var areas=new Vector4[3];var remaining=new float[3];
                for(int slot=0;slot<3;slot++) {areas[slot]=new Vector4(0,0,4,7);remaining[slot]=slot*.5f;}
                Matrix4x4 inverse=Matrix4x4.zero;
                inverse.m00=s.Span;inverse.m03=s.Center;
                inverse.m10=s.GradientX*s.Span;inverse.m11=s.GradientZ*s.Span;inverse.m13=2+s.GradientX*s.Center;
                // Depth changes reconstructed X and Y together, preserving the
                // input geometric plane while exercising distinct depth ranges.
                inverse.m02=s.Span*.25f;inverse.m12=s.GradientX*inverse.m02;
                inverse.m21=s.Span;inverse.m33=1;
                foreach(var material in materials)
                {
                    material.SetVectorArray("_DVPSVehicleAreas",areas);material.SetVectorArray("_DVPSVehicleSnowAreas",areas);
                    material.SetFloatArray("_DVPSVehicleSnowRemaining",remaining);material.SetMatrix("_DVPSInverseVP",inverse);
                    if(material!=globalSideStateMaterial)material.SetVectorArray("_DVPSVehicleSideSnow",new Vector4[1023]);
                }
                Set("_DVPSObjectLimitEnabled",s.Limit?1:0);Set("_DVPSHeightOffsets",Vector4.zero);Set("_DVPSWorldOffset",Vector4.zero);
                Set("_DVPSAmbient",new Vector4(.17f,.2f,.23f,1));Set("_DVPSGlareReduction",.25f);
            }

            public void Configure(bool hdr,float amount)
            {
                foreach(var material in materials)
                {
                    if(hdr) material.EnableKeyword("DVPS_FAST_HDR");else material.DisableKeyword("DVPS_FAST_HDR");
                    // Compare the shader outputs directly so unchanged/discarded
                    // coverage is observable independently from hardware blend.
                    material.SetInt("_SnowSrcBlend",(int)BlendMode.One);material.SetInt("_SnowDstBlend",(int)BlendMode.Zero);
                    material.SetInt("_SnowAlphaSrcBlend",(int)BlendMode.One);material.SetInt("_SnowAlphaDstBlend",(int)BlendMode.Zero);
                    material.SetInt("_SnowSpecAlphaDstBlend",(int)BlendMode.Zero);
                }
                Set("_DVPSHDR",hdr?1:0);Set("_DVPSAmount",amount);
            }
            void Record(Material material,int count,Vector4[] globalSideSnow=null,Vector4[] globalVehicleRotation=null)
            {
                commands.Clear();
                for(int target=0;target<3;target++) {commands.SetRenderTarget(targets[target]);commands.ClearRenderTarget(false,true,Untouched);}
                commands.SetRenderTarget(targetIds,targets[0]);
                if(globalSideSnow!=null)commands.SetGlobalVectorArray("_DVPSVehicleSideSnow",globalSideSnow);
                if(globalVehicleRotation!=null)commands.SetGlobalVectorArray("_DVPSVehicleRotation",globalVehicleRotation);
                for(int draw=0;draw<count;draw++) commands.DrawMesh(quad,Matrix4x4.identity,material,0,0);
            }
            public Color[][] Draw(Material material,Vector4[] globalSideSnow=null,Vector4[] globalVehicleRotation=null)
            {
                Record(material,1,globalSideSnow,globalVehicleRotation);Graphics.ExecuteCommandBuffer(commands);
                var result=new Color[3][];
                for(int target=0;target<3;target++)
                {
                    RenderTexture.active=targets[target];reader.ReadPixels(new Rect(0,0,width,height),0,0);reader.Apply();result[target]=reader.GetPixels();
                }
                return result;
            }
            public double TimeBatch(Material material,int count)
            {
                Record(material,count);var watch=Stopwatch.StartNew();Graphics.ExecuteCommandBuffer(commands);
                RenderTexture.active=targets[0];fence.ReadPixels(new Rect(0,0,1,1),0,0);fence.Apply();watch.Stop();
                return watch.Elapsed.TotalMilliseconds/count;
            }
            public void Dispose()
            {
                boundTextures.Clear();
                RenderTexture.active=previous;commands.Dispose();
                foreach(var resource in scenarioResources) UnityEngine.Object.DestroyImmediate(resource);
                foreach(var resource in resources) UnityEngine.Object.DestroyImmediate(resource);
            }
        }
    }
}
