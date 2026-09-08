Shader "Hidden/DVSeasons/SnowVehicle"
{
    Properties
    {
        _MainTex ("Original coal",2D)="white" {}
        _DVPSCoalSnow ("Winter coal",2D)="white" {}
        _DVPSCoalAmount ("Coal snow amount",Range(0,1))=0
    }
    CGINCLUDE
    #include "UnityCG.cginc"
    float4x4 _DVPSVehicleWorldToLocal;
    float4 _DVPSVehicleArea, _DVPSVehicleVertical, _DVPSVehicleST;
    float _DVPSVehicleIndex, _DVPSVehicleCutoff;
    sampler2D _DVPSVehicleAlbedo;
    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    struct v2f { float4 pos:SV_POSITION; float3 local:TEXCOORD0; float2 uv:TEXCOORD1; float4 screen:TEXCOORD2; float eye:TEXCOORD3; };
    v2f vertexData(appdata_base v)
    {
        v2f o;
        o.pos=UnityObjectToClipPos(v.vertex);
        o.screen=ComputeScreenPos(o.pos); o.eye=-UnityObjectToViewPos(v.vertex).z;
        o.local=mul(_DVPSVehicleWorldToLocal,mul(unity_ObjectToWorld,v.vertex)).xyz;
        o.uv=v.texcoord.xy*_DVPSVehicleST.xy+_DVPSVehicleST.zw;
        return o;
    }
    v2f vertexHeight(appdata_base v)
    {
        v2f o=vertexData(v);
        float2 xy=(o.local.xz-_DVPSVehicleArea.xy)/_DVPSVehicleArea.zw;
        #if UNITY_UV_STARTS_AT_TOP
        xy.y=-xy.y;
        #endif
        float depth=saturate((o.local.y-_DVPSVehicleVertical.x)/_DVPSVehicleVertical.y);
        #if !defined(UNITY_REVERSED_Z)
        depth=1-depth;
        #endif
        o.pos=float4(xy,depth,1);
        return o;
    }
    float4 data(v2f i):SV_Target
    {
        // Override draws must match the actually visible surface, not a closer
        // hidden LOD or a disabled proxy mesh over the rail bed.
        float visibleDepth=LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture,UNITY_PROJ_COORD(i.screen)));
        clip(max(0.015,i.eye*0.00005)-abs(visibleDepth-i.eye));
        if (_DVPSVehicleCutoff>0) clip(tex2D(_DVPSVehicleAlbedo,i.uv).a-_DVPSVehicleCutoff);
        return float4(i.local,_DVPSVehicleIndex);
    }
    float4 height(v2f i):SV_Target
    {
        if (_DVPSVehicleCutoff>0) clip(tex2D(_DVPSVehicleAlbedo,i.uv).a-_DVPSVehicleCutoff);
        return float4(i.local.y,0,0,1);
    }
    float _DVPSRailSnowClock;
    struct railOutput { float4 pos:SV_POSITION; float4 screen:TEXCOORD0; float2 uv:TEXCOORD1; float eye:TEXCOORD2; };
    railOutput railVertex(appdata_base v)
    {
        railOutput o; o.pos=UnityObjectToClipPos(v.vertex); o.screen=ComputeScreenPos(o.pos);
        o.eye=-UnityObjectToViewPos(v.vertex).z; o.uv=v.texcoord.xy; return o;
    }
    float4 railFragment(railOutput i):SV_Target
    {
        float depth=LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture,UNITY_PROJ_COORD(i.screen)));
        // Narrow depth tolerance prevents marks painting through trains/bridges.
        clip(0.12-abs(depth-i.eye));
        float clear=(1-smoothstep(0.65,1,abs(i.uv.x)))*saturate(1-(_DVPSRailSnowClock-i.uv.y));
        clip(clear-0.001); return float4(clear,0,0,-2);
    }
    ENDCG
    SubShader
    {
        Pass
        {
            Cull Back ZWrite Off ZTest LEqual
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vertexData
            #pragma fragment data
            ENDCG
        }
        Pass
        {
            Cull Off ZWrite On ZTest LEqual
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vertexHeight
            #pragma fragment height
            ENDCG
        }
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex railVertex
            #pragma fragment railFragment
            ENDCG
        }
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex accumulationVertex
            #pragma fragment accumulationFragment
            UNITY_DECLARE_TEX2DARRAY(_DVPSVehicleHeights);
            UNITY_DECLARE_TEX2DARRAY(_DVPSVehicleSnow);
            sampler2D _DVPSNearHeight, _DVPSFarHeight;
            float4 _DVPSNearArea, _DVPSFarArea, _DVPSAccumulation;
            float4 _DVPSPreviousSnowArea;
            float4x4 _DVPSVehicleLocalToWorld;
            struct accumulationOutput { float4 pos:SV_POSITION;float2 uv:TEXCOORD0; };
            accumulationOutput accumulationVertex(appdata_img v)
            {
                accumulationOutput o;o.pos=v.vertex;o.uv=v.texcoord;
                #if UNITY_UV_STARTS_AT_TOP
                o.pos.y=-o.pos.y;
                #endif
                return o;
            }
            float sky(sampler2D map,float4 area,float offset,float3 world)
            {
                float2 uv=(world.xz-area.xy)/(area.z*2)+0.5;
                float2 grid=uv*1024-0.5,b=frac(grid),p=(floor(grid)+0.5)/1024;
                float4 h=float4(tex2D(map,p).r,tex2D(map,p+float2(1.0/1024,0)).r,
                    tex2D(map,p+float2(0,1.0/1024)).r,tex2D(map,p+1.0/1024).r);
                float4 visible=1-smoothstep(0.08,0.2,h+offset-world.y);
                return lerp(lerp(visible.x,visible.y,b.x),lerp(visible.z,visible.w,b.x),b.y);
            }
            float4 accumulationFragment(accumulationOutput i):SV_Target
            {
                float h=UNITY_SAMPLE_TEX2DARRAY(_DVPSVehicleHeights,float3(i.uv,_DVPSVehicleIndex)).r;
                float2 xz=(i.uv-0.5)*2*_DVPSVehicleArea.zw+_DVPSVehicleArea.xy;
                float3 world=mul(_DVPSVehicleLocalToWorld,float4(xz.x,h,xz.y,1)).xyz;
                float d=max(abs(world.x-_DVPSNearArea.x),abs(world.z-_DVPSNearArea.y));
                float open=d<_DVPSNearArea.z*0.85 ? sky(_DVPSNearHeight,_DVPSNearArea,_DVPSAccumulation.z,world)
                    : sky(_DVPSFarHeight,_DVPSFarArea,_DVPSAccumulation.w,world);
                float2 oldUv=(xz-_DVPSPreviousSnowArea.xy)/(2*_DVPSPreviousSnowArea.zw)+0.5;
                bool inOldArea=all(oldUv>=0) && all(oldUv<=1);
                float previous=_DVPSAccumulation.x>0.5 || !inOldArea?0:UNITY_SAMPLE_TEX2DARRAY(_DVPSVehicleSnow,float3(oldUv,_DVPSVehicleIndex)).r;
                float amount=_DVPSAccumulation.x>0.5?open:saturate(previous+open*_DVPSAccumulation.y);
                return h<-10000?0:float4(amount,0,0,1);
            }
            ENDCG
        }
        Pass
        {
            Name "COAL_BLEND"
            Cull Off ZWrite Off ZTest Always
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert_img
            #pragma fragment coalBlend
            #include "UnityCG.cginc"
            sampler2D _MainTex, _DVPSCoalSnow;
            float _DVPSCoalAmount;
            float4 coalBlend(v2f_img i):SV_Target
            {float4 original=tex2D(_MainTex,i.uv);return float4(lerp(original.rgb,tex2D(_DVPSCoalSnow,i.uv).rgb,_DVPSCoalAmount),original.a);}
            ENDCG
        }
    }
    Fallback Off
}
