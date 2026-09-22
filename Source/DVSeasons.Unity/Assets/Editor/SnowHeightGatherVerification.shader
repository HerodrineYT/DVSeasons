Shader "Hidden/DVSeasonsTests/HeightGather"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "../DVSeasons/DV99/Shaders/SnowHeightSampling.cginc"
            DVPS_HEIGHT_TEXTURE(_Heights);
            sampler2D _ReferenceHeights;
            UNITY_DECLARE_TEX2DARRAY(_ArrayHeights);
            float4 _TestUV;
            float4 frag(v2f_img i):SV_Target
            {
                float2 uv=i.uv*_TestUV.xy+_TestUV.zw;
                float2 corner=(floor(uv*1024-.5)+.5)/1024;
                float4 gathered=DVPSReadHeights(DVPS_HEIGHT_ARGUMENT(_Heights),uv,corner,1.0/1024);
                float4 reference=float4(tex2D(_ReferenceHeights,corner).r,tex2D(_ReferenceHeights,corner+float2(1.0/1024,0)).r,
                    tex2D(_ReferenceHeights,corner+float2(0,1.0/1024)).r,tex2D(_ReferenceHeights,corner+1.0/1024).r);
                #if defined(SHADER_API_D3D11)
                float4 arrayHeights=DVPSReadArrayHeights(_ArrayHeights,sampler_ArrayHeights,float3(corner,0),1.0/1024);
                return max(abs(gathered-reference),abs(arrayHeights-reference));
                #else
                return abs(gathered-reference);
                #endif
            }
            ENDCG
        }
    }
}
