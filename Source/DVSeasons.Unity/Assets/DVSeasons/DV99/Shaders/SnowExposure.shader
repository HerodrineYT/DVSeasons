Shader "Hidden/DVSeasons/SnowExposure"
{
    Properties { _MainTex("Albedo",2D)="white" {} _Cutoff("Cutoff",Range(0,1))=0.5 }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex; float4 _MainTex_ST; float _Cutoff;
    float4 _DVPSCaptureArea;
    struct v2f { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; float height:TEXCOORD1; };
    v2f vert(appdata_base v)
    {
        v2f o;
        UNITY_SETUP_INSTANCE_ID(v);
        float3 world=mul(unity_ObjectToWorld,v.vertex).xyz;
        o.pos=UnityObjectToClipPos(v.vertex);
        // Explicit XZ mapping makes sampling independent of RenderTexture Y flips.
        o.pos.xy=(world.xz-_DVPSCaptureArea.xy)/_DVPSCaptureArea.z;
        #if UNITY_UV_STARTS_AT_TOP
        o.pos.y=-o.pos.y;
        #endif
        o.uv=TRANSFORM_TEX(v.texcoord,_MainTex); o.height=world.y; return o;
    }
    float4 solid(v2f i):SV_Target { return float4(i.height,0,0,1); }
    float4 cutout(v2f i):SV_Target { clip(tex2D(_MainTex,i.uv).a-_Cutoff); return solid(i); }
    ENDCG
    SubShader { Tags { "RenderType"="Opaque" }
        Pass { Name "SOLID" Cull Off ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment solid
            #pragma target 3.0
            #pragma multi_compile_instancing
            ENDCG
        }
        Pass { Name "TERRAIN_HEIGHT" Tags { "LightMode"="DVSeasonsTerrainHeight" } Cull Off ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma vertex terrainVert
            #pragma fragment terrainFrag
            #pragma target 3.0
            sampler2D _DVPSTerrainHeightmap, _DVPSTerrainHoles;
            float4 _DVPSTerrainArea, _DVPSTerrainHeightScale;
            float4x4 _DVPSTerrainVP;
            struct terrainVarying { float4 pos:SV_POSITION;float2 uv:TEXCOORD0;float2 worldXZ:TEXCOORD1; };
            terrainVarying terrainVert(appdata_img v)
            {
                terrainVarying o;o.uv=v.texcoord;
                o.worldXZ=_DVPSTerrainArea.xy+v.texcoord*_DVPSTerrainArea.zw;
                o.pos=float4((o.worldXZ-_DVPSCaptureArea.xy)/_DVPSCaptureArea.z,0,1);
                #if UNITY_UV_STARTS_AT_TOP
                o.pos.y=-o.pos.y;
                #endif
                return o;
            }
            struct terrainOutput { float4 color:SV_Target;float depth:SV_Depth; };
            terrainOutput terrainFrag(terrainVarying i)
            {
                clip(tex2D(_DVPSTerrainHoles,i.uv).r-0.5);
                float resolution=_DVPSTerrainHeightScale.z;
                float2 uv=(i.uv*(resolution-1)+0.5)/resolution;
                // Explicit bilinear filtering also works if the native RT is Point.
                float2 grid=uv*resolution-0.5, f=frac(grid);
                float2 a=(floor(grid)+0.5)/resolution;
                float4 h=float4(UnpackHeightmap(tex2Dlod(_DVPSTerrainHeightmap,float4(a,0,0))),
                    UnpackHeightmap(tex2Dlod(_DVPSTerrainHeightmap,float4(a+float2(1/resolution,0),0,0))),
                    UnpackHeightmap(tex2Dlod(_DVPSTerrainHeightmap,float4(a+float2(0,1/resolution),0,0))),
                    UnpackHeightmap(tex2Dlod(_DVPSTerrainHeightmap,float4(a+1/resolution,0,0))));
                float height=_DVPSTerrainHeightScale.x+lerp(lerp(h.x,h.y,f.x),lerp(h.z,h.w,f.x),f.y)*_DVPSTerrainHeightScale.y;
                float4 pos=mul(_DVPSTerrainVP,float4(i.worldXZ.x,height,i.worldXZ.y,1));
                terrainOutput o;o.color=float4(height,0,0,1);o.depth=pos.z/pos.w;return o;
            }
            ENDCG
        }
    }
    SubShader { Tags { "RenderType"="TransparentCutout" }
        Pass { Name "CUTOUT" Cull Off ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment cutout
            #pragma target 3.0
            #pragma multi_compile_instancing
            ENDCG
        }
    }
    SubShader { Tags { "RenderType"="TreeOpaque" } UsePass "Hidden/DVSeasons/SnowExposure/SOLID" }
    SubShader { Tags { "RenderType"="TreeTransparentCutout" } UsePass "Hidden/DVSeasons/SnowExposure/CUTOUT" }
    Fallback Off
}
