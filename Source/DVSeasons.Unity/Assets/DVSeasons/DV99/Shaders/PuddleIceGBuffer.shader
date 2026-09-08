Shader "Hidden/DVSeasons/PuddleIceGBuffer"
{
    // Values are supplied by the command buffer per camera. Material defaults
    // would override globals (in particular, an _IceAmount default of zero).

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _DVOriginalSpecular;
            sampler2D _WetDecalSaturationMask;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            sampler2D _IceTex;
            float _IceAmount;
            float _TileSize;
            float4x4 _DVInverseViewProjection;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };


            v2f vert(appdata input)
            {
                v2f output;
                // The mesh is already authored in clip space. Transforming it by
                // the camera would recreate the world-space plane defect.
                output.position = input.vertex;
                output.uv = input.uv;
                #if UNITY_UV_STARTS_AT_TOP
                output.uv.y = 1.0 - output.uv.y;
                #endif
                return output;
            }

            float3 ReconstructWorldPosition(float2 uv)
            {
                float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                float4 clip = float4(uv * 2.0 - 1.0, rawDepth, 1.0);
                #if UNITY_UV_STARTS_AT_TOP
                clip.y = -clip.y;
                #endif
                float4 world = mul(_DVInverseViewProjection, clip);
                return world.xyz / world.w;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                if (_IceAmount <= 0.001) discard;
                fixed4 specular = tex2D(_DVOriginalSpecular, input.uv);

                float wetMask = tex2D(_WetDecalSaturationMask, input.uv).r;
                float amount = smoothstep(0.06, 0.68, wetMask) * saturate(_IceAmount);
                if (amount <= 0.001) discard;
                float3 world = ReconstructWorldPosition(input.uv);
                fixed3 ice = tex2D(_IceTex, world.xz / max(0.01, _TileSize)).rgb;

                // Ice texture affects only micro-roughness. Neither albedo nor
                // coloured specular reflectance is replaced by a winter tint.
                float textureDetail = dot(ice, float3(0.2126, 0.7152, 0.0722));
                specular.a = lerp(specular.a, lerp(0.78, 0.86, textureDetail), amount);
                return specular;
            }
            ENDCG
        }
    }
    Fallback Off
}
