Shader "Hidden/DVSeasons/SpringTerrain"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            UNITY_DECLARE_TEX2DARRAY(_SpringSource);
            float _SpringSlice, _SpringWeight;
            float4 frag(v2f_img i):SV_Target
            {
                float4 source=UNITY_SAMPLE_TEX2DARRAY(_SpringSource,float3(i.uv,_SpringSlice));
                float3 rgb=source.rgb;
                #ifndef UNITY_COLORSPACE_GAMMA
                rgb=LinearToGammaSpace(rgb);
                #endif
                float green=saturate((rgb.g-rgb.r*.90-rgb.b*.12)/.075)*saturate((rgb.g-rgb.b*.8)/.08);
                float luma=dot(rgb,float3(.2126,.7152,.0722));
                float3 young=saturate(luma*float3(.95,1.40,.46)+float3(.035,.045,.018));
                // Darken earth slightly; preserve the lightness of snow/rock.
                float damp=(1-green)*(1-smoothstep(.4,.7,luma))*.08;
                float3 spring=lerp(rgb*(1-damp),young,green*.85);
                rgb=lerp(rgb,spring,saturate(_SpringWeight));
                #ifndef UNITY_COLORSPACE_GAMMA
                rgb=GammaToLinearSpace(rgb);
                #endif
                return float4(rgb,source.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
