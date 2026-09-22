Shader "Hidden/DVSeasons/SnowVehicleStandard"
{
    Properties
    {
        _Color("Color",Color)=(1,1,1,1)
        _MainTex("Albedo",2D)="white" {}
        _Cutoff("Cutoff",Range(0,1))=.5
        _Glossiness("Smoothness",Range(0,1))=.5
        _GlossMapScale("Smoothness scale",Range(0,1))=1
        _SmoothnessTextureChannel("Smoothness channel",Float)=0
        _Metallic("Metallic",Range(0,1))=0
        _MetallicGlossMap("Metallic",2D)="white" {}
        _SpecularHighlights("Specular",Float)=1
        _GlossyReflections("Reflections",Float)=1
        _BumpScale("Normal scale",Float)=1
        _BumpMap("Normal",2D)="bump" {}
        _Parallax("Height",Range(.005,.08))=.02
        _ParallaxMap("Height",2D)="black" {}
        _OcclusionStrength("Occlusion",Range(0,1))=1
        _OcclusionMap("Occlusion",2D)="white" {}
        _EmissionColor("Emission",Color)=(0,0,0,0)
        _EmissionMap("Emission",2D)="white" {}
        _DetailMask("Detail mask",2D)="white" {}
        _DetailAlbedoMap("Detail albedo",2D)="grey" {}
        _DetailNormalMapScale("Detail normal scale",Float)=1
        _DetailNormalMap("Detail normal",2D)="bump" {}
        _UVSec("Secondary UV",Float)=0
        [HideInInspector] _Mode("Mode",Float)=0
        [HideInInspector] _SrcBlend("Source",Float)=1
        [HideInInspector] _DstBlend("Destination",Float)=0
        [HideInInspector] _ZWrite("Depth write",Float)=1
        [HideInInspector] _DVPSNativeSlot("Car",Float)=0
        [HideInInspector] _DVPSNativeCar("Private car",Float)=0
        [HideInInspector] _DVPSNativeExterior("Exterior",Float)=0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "PerformanceChecks"="False" }
        LOD 300
        UsePass "Standard/FORWARD"
        UsePass "Standard/FORWARD_DELTA"
        UsePass "Standard/ShadowCaster"
        Pass
        {
            Name "DEFERRED"
            Tags { "LightMode"="Deferred" }
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vertDeferred
            #pragma fragment snowDeferred
            #pragma multi_compile_prepassfinal
            #pragma multi_compile_instancing
            #pragma multi_compile __ UNITY_SINGLE_PASS_STEREO
            #pragma multi_compile_local __ _NORMALMAP
            #pragma multi_compile_local __ _ALPHATEST_ON
            #pragma multi_compile_local __ _EMISSION
            #pragma multi_compile_local __ _METALLICGLOSSMAP
            #pragma multi_compile_local __ _DETAIL_MULX2
            #pragma multi_compile_local __ _GLOSSYREFLECTIONS_OFF
            #define UNITY_SETUP_BRDF_INPUT MetallicSetup
            #include "UnityStandardCore.cginc"
            #include "SnowHeightSampling.cginc"
            struct NativeSnowFrame
            {
                float4x4 worldToVehicle;
                float4 area,snowArea,sides,rotation,state;
            };
            StructuredBuffer<NativeSnowFrame> _DVPSNativeFrames;
            UNITY_DECLARE_TEX2DARRAY(_DVPSNativeHeights);
            UNITY_DECLARE_TEX2DARRAY(_DVPSNativeSnow);
            sampler2D _DVPSNativeNoise;
            UNITY_INSTANCING_BUFFER_START(DVPSNativeCar)
                UNITY_DEFINE_INSTANCED_PROP(float,_DVPSNativeSlot)
            UNITY_INSTANCING_BUFFER_END(DVPSNativeCar)
            float _DVPSNativeCar,_DVPSNativeExterior,_DVPSNativeAmount,_DVPSNativeGlare;
            float4 _DVPSNativeAmbient;
            float nativeNoise(float3 p){return tex2D(_DVPSNativeNoise,(p.xz+p.y*float2(.37,.61)+.5)/128).r;}
            void snowDeferred(VertexOutputDeferred i,
                out half4 diffuse:SV_Target0,out half4 specular:SV_Target1,
                out half4 normal:SV_Target2,out half4 lighting:SV_Target3
                #if defined(SHADOWS_SHADOWMASK) && (UNITY_ALLOWED_MRT_COUNT > 4)
                ,out half4 shadowMask:SV_Target4
                #endif
                )
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                UNITY_SETUP_INSTANCE_ID(i);
                fragDeferred(i,diffuse,specular,normal,lighting
                    #if defined(SHADOWS_SHADOWMASK) && (UNITY_ALLOWED_MRT_COUNT > 4)
                    ,shadowMask
                    #endif
                );
                float car=_DVPSNativeCar>0?_DVPSNativeCar:UNITY_ACCESS_INSTANCED_PROP(DVPSNativeCar,_DVPSNativeSlot);
                if(car<.5)return;
                // Standard's GBuffer2 alpha is unused by its lighting decode.
                // Mark native-snow pixels without another geometry/stencil pass.
                normal.a=1.0/3.0;
                if(_DVPSNativeAmount<=0 || _DVPSNativeExterior<.5)return;
                int slot=(int)(car+.5)-1;
                NativeSnowFrame frame=_DVPSNativeFrames[slot];
                if(frame.state.x<.5)return;
                float3 world=IN_WORLDPOS(i);
                float distanceFade=1-smoothstep(260,300,distance(world,_WorldSpaceCameraPos));
                if(distanceFade<=0)return;
                float3 local=mul(frame.worldToVehicle,float4(world,1)).xyz;
                float normalY=saturate(normalize(i.tangentToWorldAndPackedData[2].xyz).y);
                float slope=smoothstep(.12,.65,normalY);
                float2 uv=(local.xz-frame.snowArea.xy)/(2*frame.snowArea.zw)+.5;
                float accumulated=UNITY_SAMPLE_TEX2DARRAY(_DVPSNativeSnow,float3(uv,slot)).r;
                float2 heightUV=(local.xz-frame.area.xy)/(2*frame.area.zw)+.5;
                float2 pixel=heightUV*256-.5,corner=(floor(pixel)+.5)/256,blend=frac(pixel);
                float4 heights=DVPSReadArrayHeights(_DVPSNativeHeights,sampler_DVPSNativeHeights,float3(corner,slot),1.0/256);
                float bias=.025+min(.15,max(frame.area.z,frame.area.w)/128*sqrt(saturate(1-normalY*normalY))/max(.3,normalY));
                float4 open=1-smoothstep(bias,bias+.07,heights-local.y);
                float shelter=lerp(lerp(open.x,open.y,blend.x),lerp(open.z,open.w,blend.x),blend.y)*accumulated*frame.state.y;
                float rank=nativeNoise(local*.8)*.7+nativeNoise(local*3.2)*.3;
                float edge=max(.1,fwidth(rank));
                float cover=smoothstep(rank-edge,rank+edge,_DVPSNativeAmount*1.24-.12)*slope*shelter;
                if(dot(frame.sides,1)>0 && normalY<.7)
                {
                    float3 n=normalize(normal.xyz*2-1);
                    float3 twiceCross=2*cross(-frame.rotation.xyz,n);
                    float3 localNormal=n+frame.rotation.w*twiceCross+cross(-frame.rotation.xyz,twiceCross);
                    float4 weights=max(float4(localNormal.x,-localNormal.x,localNormal.z,-localNormal.z),0);
                    float density=saturate(dot(weights,frame.sides)/max(dot(weights,1),.0001));
                    float sideSlope=(1-smoothstep(.25,.7,normalY))*smoothstep(-.25,-.05,n.y);
                    cover=max(cover,smoothstep(rank-edge,rank+edge,lerp(.48,1.12,density))*sqrt(density)*saturate(_DVPSNativeAmount)*sideSlope);
                }
                float grainWeight=1-saturate(length(fwidth(local))*24);
                cover*=distanceFade;
                float grain=lerp(.5,nativeNoise(local*32),grainWeight);
                half3 snow=half3(.78,.81,.84)*lerp(.92,1,grain)*lerp(1,.55,saturate(_DVPSNativeGlare*.5));
                half occlusion=diffuse.a;
                diffuse.rgb=lerp(diffuse.rgb,snow,cover);
                specular=lerp(specular,half4(.04,.04,.04,.22),cover);
                #ifdef UNITY_HDR_ON
                lighting.rgb=lerp(lighting.rgb,snow*max(0,_DVPSNativeAmbient.rgb)*occlusion,cover);
                #else
                lighting.rgb=exp2(-lerp(-log2(max(lighting.rgb,.00001)),snow*max(0,_DVPSNativeAmbient.rgb)*occlusion,cover));
                #endif
            }
            ENDCG
        }
        UsePass "Standard/META"
    }
    FallBack "Standard"
}
