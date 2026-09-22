Shader "BadDog/BGWater"
{
    Properties
    {
        _MainWave ("Wave", 2D) = "white" {}
        _SecondWave ("Wave 2", 2D) = "white" {}
        _MainWaveTilingOffset ("Wave Tiling", Vector) = (1,1,1,1)
        _SecondWaveTilingOffset ("Wave 2 Tiling", Vector) = (1,1,1,1)
        _WaterBaseColor ("Water", Color) = (.05,.1,.15,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Cull Off
            ZWrite On
            Offset 1, -1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 vert(float4 vertex:POSITION):SV_POSITION { return UnityObjectToClipPos(vertex); }
            fixed4 frag():SV_Target { return fixed4(.03,.05,.07,1); }
            ENDCG
        }
    }
}
