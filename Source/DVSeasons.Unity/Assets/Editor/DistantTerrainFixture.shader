// Verification fixture for the exact public material contract of DV distant terrain.
Shader "DV/DistantTerrain"
{
    Properties { _Splats ("Splats", 2DArray)="" {} _FixtureHeightShift ("Height shift", Float)=0 }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry-99" }
        CGPROGRAM
        #pragma target 3.5
        #pragma surface surf Standard vertex:vert
        float _FixtureHeightShift;
        struct Input { float3 worldPos; };
        void vert(inout appdata_full v) { v.vertex.y+=_FixtureHeightShift; }
        void surf(Input i,inout SurfaceOutputStandard o)
        { o.Albedo=float3(0.16,0.12,0.09);o.Smoothness=0.15;o.Alpha=1; }
        ENDCG
    }
}
