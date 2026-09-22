Shader "Hidden/DVSeasons/Verification/StereoSnowProbe"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always Blend Off
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile __ UNITY_SINGLE_PASS_STEREO
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float4x4 _DVPSInverseVPStereo[2];
            struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(appdata_img v)
            {
                v2f o; o.pos=v.vertex; o.uv=v.texcoord;
                #if UNITY_UV_STARTS_AT_TOP
                o.uv.y=1-o.uv.y;
                #endif
                return o;
            }
            struct output { float4 world:SV_Target0; float4 uv:SV_Target1; float4 depth:SV_Target2; };
            output frag(v2f i)
            {
                float2 screenUV=UnityStereoTransformScreenSpaceTex(i.uv);
                float depth=SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture,screenUV);
                float4 clip=float4(i.uv*2-1,depth,1);
                #if UNITY_UV_STARTS_AT_TOP
                clip.y=-clip.y;
                #endif
                float4 world=mul(_DVPSInverseVPStereo[unity_StereoEyeIndex],clip);
                output o; o.world=float4(world.xyz/world.w,unity_StereoEyeIndex+1);
                o.uv=float4(screenUV,i.uv);o.depth=depth.xxxx;return o;
            }
            ENDCG
        }
    }
}
