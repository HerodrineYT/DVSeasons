Shader "DVSeasons/SnowDust"
{
    Properties
    {
        _MainTex ("Snow powder silhouette",2D)="white" {}
        _SoftIntersectionDistance ("Soft intersection distance (m)",Range(.05,2))=.4
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off ZWrite Off ZTest LEqual
        CGPROGRAM
        #pragma target 3.0
        #pragma surface surf Standard alpha:fade vertex:vert fullforwardshadows nolightmap nodynlightmap
        #include "UnityCG.cginc"
        sampler2D _MainTex;
        UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
        float _SoftIntersectionDistance;
        float _DVPSGlareReduction;
        struct Input
        {
            float2 uv_MainTex;
            float2 snowData;
            float4 screenPos;
        };
        void vert(inout appdata_full v,out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input,o);
            // Keep depth and opacity together within shader-model 3's forward
            // interpolator limit. A custom varying is essential: the surface
            // compiler fills any COLOR semantic from the original vertex data.
            o.snowData=float2(-UnityObjectToViewPos(v.vertex).z,v.color.a);
            // Billboard orientation must not turn white snow into dark dust.
            // Match the upward-facing rail snow's response to scene lighting.
            v.normal=normalize(mul(float3(0,1,0),(float3x3)unity_ObjectToWorld));
        }
        void surf(Input i,inout SurfaceOutputStandard o)
        {
            fixed4 dust=tex2D(_MainTex,i.uv_MainTex);
            o.Albedo=float3(.78,.81,.84)*.96*dust.rgb*lerp(1,.55,saturate(_DVPSGlareReduction*.5));
            // The opaque depth buffer hides snow behind rolling stock. Fade the
            // last few centimetres in front of it as well, so a billboard does
            // not cut a hard rectangle into rails, ballast or a wagon side.
            float rawDepth=SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture,UNITY_PROJ_COORD(i.screenPos));
            float sceneDepth=LinearEyeDepth(rawDepth);
            float orthoDepth=rawDepth;
            #if defined(UNITY_REVERSED_Z)
                orthoDepth=1-orthoDepth;
            #endif
            sceneDepth=lerp(sceneDepth,lerp(_ProjectionParams.y,_ProjectionParams.z,orthoDepth),unity_OrthoParams.w);
            float intersectionFade=saturate((sceneDepth-i.snowData.x)/max(.001,_SoftIntersectionDistance));
            o.Alpha=dust.a*i.snowData.y*intersectionFade;
            o.Metallic=0;o.Smoothness=.02;o.Occlusion=1;
        }
        ENDCG
    }
    Fallback Off
}
