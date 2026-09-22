using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DVSeasons.AssetBundleBuild
{
    // Reuses the established three-target fixture, then supplies geometric
    // planes with matching native normals and car-local surface coordinates.
    // This exercises the actual fragment program and derivative orientation.
    public static class VehicleSideSnowVerification
    {
        const string ShaderPath="Assets/DVSeasons/DV99/Shaders/ProceduralSnow.shader";
        const string ReferencePath="Assets/Editor/ProceduralSnowReference20260918.shader";
        const int Width=320,Height=192;
        enum Face { PositiveX,NegativeX,PositiveZ,NegativeZ,Roof,Underside }
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
                    Require(bundle!=null,"Packed side snow bundle unavailable");
                }
                Verify(bundle);
            }
            catch(Exception exception) {Debug.LogException(exception);code=1;}
            finally {if(bundle!=null)bundle.Unload(true);}
            EditorApplication.Exit(code);
        }
        public static void Verify(AssetBundle bundle)
        {
            Shader shader=bundle==null?AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath):bundle.LoadAsset<Shader>(ShaderPath.ToLowerInvariant());
            Require(shader!=null && shader.isSupported,"Side snow shader unsupported");
            var current=new Material(shader);
            var reference=new Material(AssetDatabase.LoadAssetAtPath<Shader>(ReferencePath));
            var global=new Material(shader);
            string output=Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath,"../..")),"artifacts/verification/vehicle-side-snow/"+(bundle==null?"source":"packed"));
            Directory.CreateDirectory(output);
            try
            {
                using(var fixture=new SnowProceduralCostVerification.Fixture(Width,Height,current,reference,global))
                using(var inputs=new Inputs(Width,Height,global,current,reference,global))
                {
                    fixture.Prepare(new SnowProceduralCostVerification.Scenario("side-snow") {Vehicles=true},global);
                    inputs.Initialize();
                    foreach(bool hdr in new[]{true,false})
                    {
                        fixture.Configure(hdr,1);
                        foreach(Face face in Enum.GetValues(typeof(Face)))
                        {
                            inputs.Plane(face,Quaternion.identity,Vector3.zero,1);
                            inputs.Amount(Vector4.zero,1);
                            Equal(fixture.Draw(reference),fixture.Draw(current),"zero side state "+face+" HDR="+hdr,0);
                        }
                        inputs.Plane(Face.Roof,Quaternion.identity,Vector3.zero,1);inputs.Amount(Vector4.one,1);
                        var roof=fixture.Draw(current);
                        Equal(fixture.Draw(reference),roof,"roof unchanged HDR="+hdr,0);
                        Require(Written(roof)>Width*Height*.9f,"Roof baseline did not exercise snow");
                        for(int side=0;side<4;side++)
                        {
                            Face face=(Face)side;inputs.Plane(face,Quaternion.identity,Vector3.zero,1);
                            var amount=Vector4.zero;amount[side]=.28f;inputs.Amount(amount,1);
                            var initial=fixture.Draw(current);
                            amount[side]=.6f;inputs.Amount(amount,1);var partial=fixture.Draw(current);
                            amount[side]=1;inputs.Amount(amount,1);var full=fixture.Draw(current);
                            if(hdr)Save(partial,Path.Combine(output,face+"-partial.png"));
                            if(hdr)Save(full,Path.Combine(output,face+"-full.png"));
                            float a=Coverage(initial,hdr),b=Coverage(partial,hdr),c=Coverage(full,hdr);
                            Require(a>0 && b>a+c*.02f && c>b+c*.02f,"Side growth not monotonic "+face+" HDR="+hdr+": "+a+","+b+","+c);
                            Require(Written(full)>Width*Height*.9f,"Full side snow did not cover "+face);
                            float previousLow=0;
                            foreach(float lowAmount in new[]{.005f,.01f,.03f,.1f})
                            {
                                var low=Vector4.zero;low[side]=lowAmount;inputs.Amount(low,1);
                                var lowImage=fixture.Draw(current);
                                float lowCoverage=NormalizedCoverage(lowImage,full,hdr);
                                Require(Written(lowImage)>Width*Height*.05f && lowCoverage>lowAmount*.03f,
                                    "Weak side buildup is invisible: "+face+" HDR="+hdr+" amount="+lowAmount+" coverage="+lowCoverage);
                                Require(lowCoverage>previousLow+lowAmount*.01f,
                                    "Weak side buildup is not increasing: "+face+" HDR="+hdr+" amount="+lowAmount+" coverage="+lowCoverage+" previous="+previousLow);
                                previousLow=lowCoverage;
                                Debug.Log("VEHICLE_SIDE_SNOW_WEAK: face="+face+" HDR="+hdr+" amount="+lowAmount+" coverage="+lowCoverage.ToString("G9")+" written="+Written(lowImage));
                                if(hdr && side==0 && lowAmount==.01f)Save(lowImage,Path.Combine(output,"PositiveX-weak-001.png"));
                            }
                            inputs.Amount(amount,.5f);Equal(full,fixture.Draw(current),"roof thermal remaining does not double-melt sides "+face,0);
                            inputs.Amount(amount,0);Equal(full,fixture.Draw(current),"roof completely melted while side state persists "+face,0);
                            inputs.Amount(amount*.5f,0);var half=fixture.Draw(current);
                            Require(Coverage(half,hdr)>0 && Coverage(half,hdr)<c*.95f,"Physical side-state half melt failed on "+face);
                            inputs.Amount(Vector4.zero,0);Require(Written(fixture.Draw(current))==0,"Melted side retained snow: "+face);
                            inputs.Amount(amount,1);
                            var other=side^1;inputs.Plane((Face)other,Quaternion.identity,Vector3.zero,1);
                            Require(Written(fixture.Draw(current))==0,"Wind-side amount crossed onto opposite face "+face);
                            inputs.Plane(face,Quaternion.Euler(0,103,0),new Vector3(37,0,-29),1);
                            amount[side]=.6f;inputs.Amount(amount,1);
                            Equal(partial,fixture.Draw(current),"car rotation preserves partial local pattern "+face+" HDR="+hdr,.00002f);
                            Debug.Log("VEHICLE_SIDE_SNOW_GROWTH: face="+face+" HDR="+hdr+" coverage="+a.ToString("F4")+","+b.ToString("F4")+","+c.ToString("F4"));
                        }
                        inputs.Amount(Vector4.one,1);
                        foreach(float marker in new[]{-1f,0f})
                        {
                            inputs.Plane(Face.PositiveX,Quaternion.identity,Vector3.zero,marker);
                            Require(Written(fixture.Draw(current))==0,"Side state affected excluded/static marker "+marker);
                        }
                        inputs.Plane(Face.Underside,Quaternion.identity,Vector3.zero,1);
                        Require(Written(fixture.Draw(current))==0,"Downward underside received side snow");
                        inputs.Plane(Face.PositiveX,Quaternion.identity,Vector3.zero,2);
                        Require(Written(fixture.Draw(current))==0,"Side state leaked into a different vehicle slot");
                        inputs.Plane(Face.PositiveX,Quaternion.identity,Vector3.zero,1);
                        fixture.Configure(hdr,0);Require(Written(fixture.Draw(current))==0,"Zero global coverage retained side snow");
                        fixture.Configure(hdr,1);inputs.Amount(new Vector4(1,0,0,0),1,1022);
                        inputs.Plane(Face.PositiveX,Quaternion.identity,Vector3.zero,1023);
                        Require(Written(fixture.Draw(current))>Width*Height*.9f,"Last supported vehicle slot lost side state: 1022 HDR="+hdr);
                        inputs.Amount(Vector4.one,1);
                    }
                    VerifyProfileOutput(fixture,inputs,current);
                    // Verify the production upload route, independently of the
                    // material-local values used by the other isolated fixtures.
                    {
                        fixture.Configure(true,1);
                        inputs.Plane(Face.PositiveX,Quaternion.identity,Vector3.zero,1);
                        var previous=Shader.GetGlobalVectorArray("_DVPSVehicleSideSnow");
                        var previousRotation=Shader.GetGlobalVectorArray("_DVPSVehicleRotation");
                        try
                        {
                            var sides=new Vector4[1023];sides[0]=new Vector4(1,0,0,0);
                            Shader.SetGlobalVectorArray("_DVPSVehicleRotation",inputs.Rotations);
                            Shader.SetGlobalVectorArray("_DVPSVehicleSideSnow",sides);
                            Require(Written(fixture.Draw(global))>Width*Height*.9f,"Global vector-array upload did not reach DVPSSideSnow constant buffer");
                            sides[0]=Vector4.zero;Shader.SetGlobalVectorArray("_DVPSVehicleSideSnow",sides);
                            Require(Written(fixture.Draw(global))==0,"Global side-state reset failed");
                            inputs.Plane(Face.NegativeZ,Quaternion.Euler(0,127,0),Vector3.zero,1023);
                            Shader.SetGlobalVectorArray("_DVPSVehicleRotation",inputs.Rotations);
                            sides[1022]=new Vector4(0,0,0,1);Shader.SetGlobalVectorArray("_DVPSVehicleSideSnow",sides);
                            Require(Written(fixture.Draw(global))>Width*Height*.9f,"Global side array truncated at last supported slot 1022");
                            // Same command-buffer upload/draw order used by the
                            // runtime registry. The values must change next frame.
                            Shader.SetGlobalVectorArray("_DVPSVehicleSideSnow",new Vector4[1023]);
                            Require(Written(fixture.Draw(global,sides,inputs.Rotations))>Width*Height*.9f,"Command-buffer side upload lost slot1022");
                            sides[1022]=Vector4.zero;
                            Require(Written(fixture.Draw(global,sides,inputs.Rotations))==0,"Command-buffer side upload did not clear next frame");
                            inputs.Plane(Face.PositiveX,Quaternion.identity,Vector3.zero,1);sides[0]=new Vector4(1,0,0,0);
                            Require(Written(fixture.Draw(global,sides,inputs.Rotations))>Width*Height*.9f,"Command-buffer side upload lost slot0");
                            sides[0]=Vector4.zero;
                            Require(Written(fixture.Draw(global,sides,inputs.Rotations))==0,"Command-buffer slot0 reset failed");
                        }
                        finally
                        {
                            Shader.SetGlobalVectorArray("_DVPSVehicleSideSnow",previous!=null && previous.Length>0?previous:new Vector4[1023]);
                            Shader.SetGlobalVectorArray("_DVPSVehicleRotation",previousRotation!=null && previousRotation.Length>0?previousRotation:new Vector4[1023]);
                        }
                    }
                }
                VerifyCloseups(current,output);
                Debug.Log("VEHICLE_SIDE_SNOW_GPU_OK: packed="+(bundle!=null)+"; production RGBAHalf, zero-state exact legacy parity, unchanged roof, four directional side amounts, monotonic growth, half/zero physically melted side state independent of roof history, rotated local pattern, cabin/static/underside/other-slot rejection, zero coverage, global amount/rotation array upload including slot1022; HDR and LDR; 1080p/4K closeups.");
            }
            finally {UnityEngine.Object.DestroyImmediate(current);UnityEngine.Object.DestroyImmediate(reference);UnityEngine.Object.DestroyImmediate(global);}
        }
        static void VerifyProfileOutput(SnowProceduralCostVerification.Fixture fixture,Inputs inputs,Material current)
        {
            string root=Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            string runtime=Environment.GetEnvironmentVariable("DVSEASONS_VERIFY_MOD")??Path.Combine(root,"artifacts/build/DVSeasons");
            var core=Assembly.LoadFrom(Path.Combine(runtime,"DVSeasons.Core.dll"));
            var type=core.GetType("DVSeasons.Core.VehicleSideSnowProfile",true);
            var advance=type.GetMethod("Advance",BindingFlags.Public|BindingFlags.Static);
            var impact=type.GetMethod("ImpactFactor",BindingFlags.Public|BindingFlags.Static);
            // Speed, snowfall, heat, airflow X/Z, duration. Small quarter-second
            // increments must survive the logical profile before the GPU sees them.
            var cases=new[]{
                new[]{31f,.2f,0f,0f,-31f/3.6f,120f},
                new[]{31f,.2f,0f,0f,-31f/3.6f,15f},
                new[]{31f,.2f,.5f,0f,-31f/3.6f,15f},
                new[]{0f,.11f,0f,-7f,0f,15f}};
            const float step=.25f;
            for(int scenario=0;scenario<cases.Length;scenario++)
            {
                var values=cases[scenario];float speed=values[0],snowfall=values[1],heat=values[2],seconds=values[5];
                float factor=(float)impact.Invoke(null,new object[]{speed,values[3],values[4],1f,0f});
                float amount=0;
                for(int i=0;i<(int)(seconds/step);i++)
                    amount=(float)advance.Invoke(null,new object[]{amount,speed,snowfall,-8f,heat,true,step,factor});
                Require(amount>0 && amount<.1f,"Weak native-profile case did not produce a small layer: case="+scenario+" amount="+amount);
                if(scenario==0)Require(amount>.014f && amount<.017f,"Weak native-profile fixture expected its independently checked 1.4–1.7% layer: "+amount);
                inputs.Plane(Face.PositiveX,Quaternion.identity,Vector3.zero,1);
                foreach(bool hdr in new[]{true,false})
                {
                    fixture.Configure(hdr,1);inputs.Amount(new Vector4(1,0,0,0),1);var full=fixture.Draw(current);
                    inputs.Amount(new Vector4(amount,0,0,0),1);var actual=fixture.Draw(current);
                    float coverage=NormalizedCoverage(actual,full,hdr);
                    Require(Written(actual)>0 && coverage>amount*.03f,"Accumulated native-profile layer disappeared in shader: amount="+amount+" coverage="+coverage+" HDR="+hdr);
                    Debug.Log("VEHICLE_SIDE_SNOW_PROFILE_GPU_OK: speed="+speed+" snow="+snowfall+" heat="+heat+" windX="+values[3]+" seconds="+seconds+" tick="+step+" amount="+amount.ToString("G9")+" coverage="+coverage.ToString("G9")+" HDR="+hdr+"; actual compiled profile output, no synthetic scaling.");
                }
            }
        }
        static void VerifyCloseups(Material current,string output)
        {
            foreach(int width in new[]{1920,3840})
            {
                int height=width*9/16;
                using(var fixture=new SnowProceduralCostVerification.Fixture(width,height,current))
                using(var inputs=new Inputs(width,height,null,current))
                {
                    fixture.Prepare(new SnowProceduralCostVerification.Scenario("side-closeup") {Vehicles=true});
                    fixture.Configure(true,1);inputs.Initialize();
                    foreach(float span in new[]{2f,.5f})
                    {
                        float vertical=span==2?1:.3f;
                        inputs.Amount(new Vector4(1,0,0,0),1);
                        inputs.Plane(Face.PositiveX,Quaternion.identity,Vector3.zero,1,span,vertical);
                        var full=fixture.Draw(current);int marked=Written(full);
                        Require(marked==width*height,"RGBAHalf closeup lost side pixels: "+width+" span="+span+" marked="+marked+"/"+width*height);
                        full=null;
                        inputs.Amount(new Vector4(.6f,0,0,0),1);
                        var partial=fixture.Draw(current);
                        inputs.Plane(Face.PositiveX,Quaternion.Euler(0,103,0),new Vector3(37,0,-29),1,span,vertical);
                        Equal(partial,fixture.Draw(current),"RGBAHalf closeup partial rotation "+width+" span="+span,.00002f);
                        if(span==.5f)Save(partial,Path.Combine(output,"closeup-"+width+"-partial.png"),width,height);
                        partial=null;
                        inputs.Plane(Face.NegativeX,Quaternion.Euler(0,103,0),Vector3.zero,1,span,vertical);
                        Require(Written(fixture.Draw(current))==0,"RGBAHalf closeup changed wind-facing side: "+width+" span="+span);
                        Debug.Log("VEHICLE_SIDE_SNOW_CLOSEUP_OK: resolution="+width+"x"+height+" localCenter=(2,2.5,10) span="+span+"x"+vertical+" full_pixels="+marked+"; RGBAHalf, partial rotated pixel parity, opposite face rejected.");
                    }
                }
                // The fixture intentionally reads every MRT pixel; release its
                // large CPU images before testing the next display resolution.
                GC.Collect();GC.WaitForPendingFinalizers();
            }
        }
        sealed class Inputs:IDisposable
        {
            readonly Material[] materials;
            readonly int width,height;
            readonly Material globalSideStateMaterial;
            readonly List<UnityEngine.Object> resources=new List<UnityEngine.Object>();
            readonly Texture2D data,normal,slope;
            readonly Vector4[] sides=new Vector4[1023];
            public readonly Vector4[] Rotations=new Vector4[1023];
            readonly float[] remaining=new float[1023];
            public Inputs(int width,int height,Material globalSideStateMaterial,params Material[] materials)
            {
                this.materials=materials;this.globalSideStateMaterial=globalSideStateMaterial;this.width=width;this.height=height;
                for(int i=0;i<Rotations.Length;i++)Rotations[i]=new Vector4(0,0,0,1);
                // Unity fixes material array capacity at its first assignment.
                // Reserve the complete production capacity before Prepare fills
                // the legacy fixture's first three vehicle entries.
                foreach(var material in materials)
                {
                    material.SetFloatArray("_DVPSVehicleSnowRemaining",remaining);
                    material.SetVectorArray("_DVPSVehicleAreas",new Vector4[1023]);
                    material.SetVectorArray("_DVPSVehicleSnowAreas",new Vector4[1023]);
                }
                data=Texture(width,height,TextureFormat.RGBAHalf);normal=Texture(width,height,TextureFormat.RGBAFloat);slope=Texture(1,1,TextureFormat.RFloat);
            }
            Texture2D Texture(int width,int height,TextureFormat format)
            {var result=new Texture2D(width,height,format,false,true){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};resources.Add(result);return result;}
            public void Initialize()
            {
                var heights=new Texture2DArray(256,256,3,TextureFormat.RFloat,false,true){filterMode=FilterMode.Point,wrapMode=TextureWrapMode.Clamp};resources.Add(heights);
                var snow=new Texture2DArray(256,256,3,TextureFormat.RHalf,false,true){filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};resources.Add(snow);
                var h=new Color[256*256];var s=new Color[h.Length];for(int i=0;i<h.Length;i++){h[i]=new Color(3,0,0,1);s[i]=Color.white;}
                for(int slice=0;slice<3;slice++){heights.SetPixels(h,slice);snow.SetPixels(s,slice);}heights.Apply();snow.Apply();
                foreach(var material in materials)
                {
                    material.SetTexture("_DVPSVehicleHeights",heights);material.SetTexture("_DVPSVehicleSnow",snow);
                    material.SetTexture("_DVPSVehicleData",data);material.SetTexture("_DVPSVehicleSlope",slope);material.SetTexture("_DVPSNormal",normal);
                }
            }
            public void Amount(Vector4 value,float thermalRemaining,int slot=0)
            {
                Array.Clear(sides,0,sides.Length);sides[slot]=value;
                for(int i=0;i<remaining.Length;i++)remaining[i]=1;
                remaining[slot]=thermalRemaining;
                foreach(var material in materials){if(material!=globalSideStateMaterial)material.SetVectorArray("_DVPSVehicleSideSnow",sides);material.SetFloatArray("_DVPSVehicleSnowRemaining",remaining);}
            }
            public void Plane(Face face,Quaternion rotation,Vector3 translation,float marker,float closeWidth=0,float closeHeight=0)
            {
                Vector3 center,u,v,n;
                if(face==Face.PositiveX || face==Face.NegativeX)
                {float sign=face==Face.PositiveX?1:-1;center=new Vector3(sign*2,1.5f,0);u=Vector3.forward*10;v=Vector3.up*2.4f;n=Vector3.right*sign;}
                else if(face==Face.PositiveZ || face==Face.NegativeZ)
                {float sign=face==Face.PositiveZ?1:-1;center=new Vector3(0,1.5f,sign*6);u=Vector3.right*3.6f;v=Vector3.up*2.4f;n=Vector3.forward*sign;}
                else
                {center=new Vector3(0,face==Face.Roof?3:0,0);u=Vector3.right*3.6f;v=Vector3.forward*10;n=face==Face.Roof?Vector3.up:Vector3.down;}
                if(closeWidth>0)
                {center=new Vector3(face==Face.NegativeX?-2:2,2.5f,10);u=Vector3.forward*closeWidth;v=Vector3.up*closeHeight;}
                var pixels=new Color[width*height];var normals=new Color[pixels.Length];var worldNormal=rotation*n;
                for(int y=0;y<height;y++)for(int x=0;x<width;x++)
                {
                    var point=center+u*((x+.5f)/width-.5f)+v*((y+.5f)/height-.5f);int p=y*width+x;
                    pixels[p]=new Color(point.x,point.y,point.z,marker);normals[p]=new Color(worldNormal.x*.5f+.5f,worldNormal.y*.5f+.5f,worldNormal.z*.5f+.5f,1);
                }
                data.SetPixels(pixels);data.Apply();normal.SetPixels(normals);normal.Apply();slope.SetPixel(0,0,new Color(Mathf.Max(0,worldNormal.y),0,0,1));slope.Apply();
                var matrix=Matrix4x4.zero;var column0=rotation*u*.5f;var column1=rotation*v*(SystemInfo.graphicsUVStartsAtTop?-.5f:.5f);var origin=rotation*center+translation;
                matrix.SetColumn(0,new Vector4(column0.x,column0.y,column0.z,0));matrix.SetColumn(1,new Vector4(column1.x,column1.y,column1.z,0));matrix.SetColumn(3,new Vector4(origin.x,origin.y,origin.z,1));
                int slot=Mathf.Clamp(Mathf.RoundToInt(marker)-1,0,Rotations.Length-1);Rotations[slot]=new Vector4(rotation.x,rotation.y,rotation.z,rotation.w);
                foreach(var material in materials)
                {
                    material.SetMatrix("_DVPSInverseVP",matrix);
                    if(material!=globalSideStateMaterial)material.SetVectorArray("_DVPSVehicleRotation",Rotations);
                }
            }
            public void Dispose(){foreach(var resource in resources)UnityEngine.Object.DestroyImmediate(resource);}
        }
        static int Written(Color[][] image)
        {int count=0;foreach(var pixel in image[0])if(pixel.r!=-4)count++;return count;}
        static float Coverage(Color[][] image,bool hdr)
        {
            float sum=0;foreach(var pixel in image[0])if(pixel.r!=-4)sum+=hdr?pixel.a:Mathf.Max(0,pixel.r-.23f);
            return sum/image[0].Length;
        }
        static float NormalizedCoverage(Color[][] image,Color[][] full,bool hdr)
        {
            float sum=0;
            for(int i=0;i<image[0].Length;i++)
            {
                var pixel=image[0][i];if(pixel.r==-4)continue;
                if(hdr)sum+=pixel.a;
                else
                {
                    // The shared fixture's native diffuse is RGBA32. Subtract
                    // its exact red sample before measuring a weak overlay;
                    // unmodified native brightness must not count as snow.
                    float native=Mathf.Round((.18f+((i%Width+.5f)/Width)*.1f)*255f)/255f;
                    sum+=Mathf.Max(0,(pixel.r-native)/Mathf.Max(.01f,full[0][i].r-native));
                }
            }
            return sum/image[0].Length;
        }
        static void Equal(Color[][] expected,Color[][] actual,string phase,float tolerance)
        {
            float maximum=0;for(int target=0;target<3;target++)for(int i=0;i<expected[target].Length;i++)
            {
                var a=expected[target][i];var b=actual[target][i];
                float delta=Mathf.Max(Mathf.Max(Mathf.Abs(a.r-b.r),Mathf.Abs(a.g-b.g)),Mathf.Max(Mathf.Abs(a.b-b.b),Mathf.Abs(a.a-b.a)));
                Require(!float.IsNaN(delta) && !float.IsInfinity(delta),"Non-finite side snow output: "+phase);maximum=Mathf.Max(maximum,delta);
            }
            Require(maximum<=tolerance,"Side snow parity changed: "+phase+" max="+maximum);
        }
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static void Save(Color[][] image,string path,int width=Width,int height=Height)
        {
            var pixels=new Color[image[0].Length];
            for(int i=0;i<pixels.Length;i++)
            {
                var native=new Color(.23f,.25f,.24f,1);var snow=image[0][i];
                pixels[i]=snow.r==-4?native:Color.Lerp(native,new Color(snow.r,snow.g,snow.b,1),snow.a);
            }
            var texture=new Texture2D(width,height,TextureFormat.RGB24,false,true);texture.SetPixels(pixels);texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
