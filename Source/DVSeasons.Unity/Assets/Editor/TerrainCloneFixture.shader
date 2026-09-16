Shader "Hidden/DVSeasons/Tests/MicroSplatClone"
{
    Properties { _Diffuse("Diffuse",2DArray)="" {} }
    SubShader { Pass { CGPROGRAM
    #pragma vertex vert_img
    #pragma fragment frag
    #pragma target 3.5
    #include "UnityCG.cginc"
    UNITY_DECLARE_TEX2DARRAY(_Diffuse);
    fixed4 frag(v2f_img i):SV_Target { return UNITY_SAMPLE_TEX2DARRAY(_Diffuse,float3(i.uv,0)); }
    ENDCG } }
}
