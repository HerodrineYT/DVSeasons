Shader "Hidden/DVSeasons/SnowGlare"
{
    Properties { _MainTex ("Scene", 2D) = "white" {} _Strength ("Reduction", Range(0,2)) = 0 }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma multi_compile __ UNITY_SINGLE_PASS_STEREO
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float _Strength;
            half4 frag(v2f_img i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half4 color = tex2D(_MainTex, UnityStereoTransformScreenSpaceTex(i.uv));
                float2 depthUV = i.uv;
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0) depthUV.y = 1 - depthUV.y;
                #endif
                depthUV = UnityStereoTransformScreenSpaceTex(depthUV);
                float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, depthUV);
                #if defined(UNITY_REVERSED_Z)
                if (depth <= .000001) return color;
                #else
                if (depth >= .999999) return color;
                #endif
                float luminance = dot(color.rgb, float3(.2126,.7152,.0722));
                float peak = max(color.r,max(color.g,color.b));
                float minimum = min(color.r,min(color.g,color.b));
                float neutral = smoothstep(.4,.8,minimum / max(.0001,peak));
                // A continuous shoulder preserves differences between bright
                // snow levels instead of clipping them to one white value.
                float excess = max(0,luminance-.45);
                float mapped = .45 + excess / (1 + excess/.5);
                float reduced = lerp(luminance,min(luminance,mapped),saturate(_Strength*.5)*neutral);
                color.rgb *= reduced / max(.0001,luminance);
                return color;
            }
            ENDCG
        }
    }
    Fallback Off
}
