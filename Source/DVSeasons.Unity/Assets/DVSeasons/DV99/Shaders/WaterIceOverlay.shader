Shader "DVSeasons/WaterIceOverlay"
{
    Properties
    {
        _MainTex ("Ice Albedo", 2D) = "white" {}
        _Color ("Tint", Color) = (1, 1, 1, 0.32)
        _TileSize ("World Tile Size", Float) = 22
    }

    SubShader
    {
        Tags { "Queue"="Transparent+100" "RenderType"="Transparent" }
        Pass
        {
            Cull Back
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            fixed4 _Color;
            float _TileSize;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 worldXZ : TEXCOORD0;
            };

            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                float3 world = mul(unity_ObjectToWorld, input.vertex).xyz;
                output.worldXZ = world.xz / max(0.01, _TileSize);
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 ice = tex2D(_MainTex, input.worldXZ);
                return fixed4(ice.rgb * _Color.rgb, ice.a * _Color.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
