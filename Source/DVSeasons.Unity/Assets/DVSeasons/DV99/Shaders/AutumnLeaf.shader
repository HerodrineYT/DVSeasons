Shader "Hidden/DVSeasons/AutumnLeaf"
{
    Properties { _MainTex("Leaf",2D)="white" {} }
    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Cull Off ZWrite On ZTest LEqual Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"
            sampler2D _MainTex;
            half3 _LeafAmbientSky, _LeafAmbientEquator, _LeafAmbientGround;
            struct input { float4 vertex:POSITION; float3 normal:NORMAL; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
            struct output { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR;
                half3 ambient:TEXCOORD1; half3 sunlight:TEXCOORD2; SHADOW_COORDS(3) UNITY_FOG_COORDS(4) };
            output vert(input v)
            {
                output o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.uv; o.color=v.color;
                #ifndef UNITY_COLORSPACE_GAMMA
                o.color.rgb = GammaToLinearSpace(o.color.rgb);
                #endif
                float3 p=mul(unity_ObjectToWorld,v.vertex).xyz;
                float3 n=UnityObjectToWorldNormal(v.normal);
                // Two-sided thin leaves receive sunlight and ambient scene light.
                // No emission, additive blending or camera-distance soft fade.
                o.ambient=max(0,lerp(_LeafAmbientEquator,
                    n.y>=0 ? _LeafAmbientSky : _LeafAmbientGround, abs(n.y)));
                // No directional light is selected during some weather states.
                // normalize(0) can produce NaN; 0 * NaN would erase ambient too.
                float3 lightDirection = UnityWorldSpaceLightDir(p);
                lightDirection *= rsqrt(max(dot(lightDirection,lightDirection), .0001));
                o.sunlight = _LightColor0.rgb *
                    (.12+.88*abs(dot(n,lightDirection)));
                #ifdef VERTEXLIGHT_ON
                o.ambient+=Shade4PointLights(unity_4LightPosX0,unity_4LightPosY0,unity_4LightPosZ0,
                    unity_LightColor[0].rgb,unity_LightColor[1].rgb,unity_LightColor[2].rgb,
                    unity_LightColor[3].rgb,unity_4LightAtten0,p,n);
                #endif
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o,o.pos); return o;
            }
            fixed4 frag(output i):SV_Target
            {
                fixed4 c=tex2D(_MainTex,i.uv)*i.color; clip(c.a-.08);
                c.rgb*=i.ambient+i.sunlight*SHADOW_ATTENUATION(i);
                UNITY_APPLY_FOG(i.fogCoord,c); return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
