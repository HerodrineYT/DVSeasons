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
    float4x4 _DVPSJunctionDelta;
    float4 _DVPSVehicleArea, _DVPSVehicleVertical, _DVPSVehicleST;
    float _DVPSVehicleIndex, _DVPSVehicleCutoff;
    sampler2D _DVPSVehicleAlbedo;
    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    struct v2f { float4 pos:SV_POSITION; float3 local:TEXCOORD0; float2 uv:TEXCOORD1; float4 screen:TEXCOORD2; float eye:TEXCOORD3; float3 normal:TEXCOORD4; };
    v2f vertexData(appdata_base v)
    {
        v2f o;
        o.pos=UnityObjectToClipPos(v.vertex);
        o.screen=ComputeScreenPos(o.pos); o.eye=-UnityObjectToViewPos(v.vertex).z;
        o.local=mul(_DVPSVehicleWorldToLocal,mul(unity_ObjectToWorld,v.vertex)).xyz;
        // Junction pixels carry only the small offset back to their captured
        // blade pose, never large world positions in the half-float target.
        if (_DVPSVehicleIndex < -2.5 && _DVPSVehicleIndex > -3.5) o.local=mul(_DVPSJunctionDelta,v.vertex).xyz;
        o.normal=UnityObjectToWorldNormal(v.normal);
        o.uv=v.texcoord.xy*_DVPSVehicleST.xy+_DVPSVehicleST.zw;
        return o;
    }
    v2f vertexHeight(appdata_base v)
    {
        v2f o=vertexData(v);
        // Height captures always use the vehicle frame. The previous screen
        // data draw may have left a junction marker in the shared globals.
        o.local=mul(_DVPSVehicleWorldToLocal,mul(unity_ObjectToWorld,v.vertex)).xyz;
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
    struct dataOutput { float4 surface:SV_Target0; half slope:SV_Target1; };
    void visibleSurface(float4 screen,float eye,float2 uv)
    {
        // Override draws must match the actually visible surface, not a closer
        // hidden LOD or a disabled proxy mesh over the rail bed.
        float visibleDepth=SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture,UNITY_PROJ_COORD(screen));
        float surfaceDepth=screen.z/screen.w;
        if (UNITY_NEAR_CLIP_VALUE < 0) surfaceDepth=surfaceDepth*0.5+0.5;
        // Compare in the stored depth space. Linearizing a distant 24-bit
        // sample magnifies one quantization step by distance squared, making
        // the same surface fail a centimetre-sized eye-depth comparison.
        // Keep the close-up tolerance, plus two native depth steps; never use
        // a large fixed world-space gap that would match another nearby mesh.
        float eyeTolerance=max(0.015,eye*0.00005);
        float depthTolerance=eyeTolerance/max(abs(_ZBufferParams.z)*eye*eye,0.000001);
        depthTolerance=lerp(depthTolerance,eyeTolerance/(_ProjectionParams.z-_ProjectionParams.y),unity_OrthoParams.w);
        clip(max(2.0/16777215.0,depthTolerance)-abs(visibleDepth-surfaceDepth));
        if (_DVPSVehicleCutoff>0) clip(tex2D(_DVPSVehicleAlbedo,uv).a-_DVPSVehicleCutoff);
    }
    dataOutput data(v2f i)
    {
        visibleSurface(i.screen,i.eye,i.uv);
        // Keep the body's unperturbed slope with its local coordinates. Deriving
        // it later from camera depth and a normal-mapped GBuffer made roof bevels
        // depend on paint detail, neighbouring pixels and viewing direction.
        // Integer IDs up to 1024 are exact in half precision. Keep the slope
        // in a separate byte so large fleets do not need a full-float RGBA map.
        dataOutput o;
        o.surface=float4(i.local,_DVPSVehicleIndex);
        o.slope=saturate(normalize(i.normal).y);
        return o;
    }
    struct exclusionVarying { float4 pos:SV_POSITION;float4 screen:TEXCOORD0;float2 uv:TEXCOORD1;float eye:TEXCOORD2; };
    exclusionVarying exclusionVertex(appdata_base v)
    {
        UNITY_SETUP_INSTANCE_ID(v);
        exclusionVarying o;o.pos=UnityObjectToClipPos(v.vertex);o.screen=ComputeScreenPos(o.pos);
        o.eye=-UnityObjectToViewPos(v.vertex).z;o.uv=v.texcoord.xy*_DVPSVehicleST.xy+_DVPSVehicleST.zw;
        return o;
    }
    half4 exclusionFragment(exclusionVarying i):SV_Target
    {
        visibleSurface(i.screen,i.eye,i.uv);
        return half4(0,0,0,-1);
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
            #pragma multi_compile __ UNITY_SINGLE_PASS_STEREO
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
            // Rail erasure replaces surface data, not the selected object's
            // slope/allow marker in the second attachment.
            ColorMask 0 1
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex railVertex
            #pragma fragment railFragment
            #pragma multi_compile __ UNITY_SINGLE_PASS_STEREO
            ENDCG
        }
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            CGPROGRAM
            #pragma target 4.5
            #pragma vertex accumulationVertex
            #pragma fragment accumulationFragment
            #include "SnowHeightSampling.cginc"
            UNITY_DECLARE_TEX2DARRAY(_DVPSVehicleHeights);
            UNITY_DECLARE_TEX2DARRAY(_DVPSVehicleSnow);
            DVPS_HEIGHT_TEXTURE(_DVPSNearHeight);
            DVPS_HEIGHT_TEXTURE(_DVPSFarHeight);
            DVPS_HEIGHT_TEXTURE(_DVPSDistantHeight);
            float4 _DVPSDistantArea;
            float _DVPSDistantOffset;
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
            float sky(DVPS_HEIGHT_PARAMETER,float4 area,float offset,float3 world)
            {
                float2 uv=(world.xz-area.xy)/(area.z*2)+0.5;
                float2 grid=uv*1024-0.5,b=frac(grid),p=(floor(grid)+0.5)/1024;
                float4 h=DVPSReadHeights(DVPS_HEIGHT_ARGUMENT(heights),uv,p,1.0/1024);
                float4 visible=1-smoothstep(0.08,0.2,h+offset-world.y);
                return lerp(lerp(visible.x,visible.y,b.x),lerp(visible.z,visible.w,b.x),b.y);
            }
            float4 accumulationFragment(accumulationOutput i):SV_Target
            {
                float h=UNITY_SAMPLE_TEX2DARRAY(_DVPSVehicleHeights,float3(i.uv,_DVPSVehicleIndex)).r;
                float2 xz=(i.uv-0.5)*2*_DVPSVehicleArea.zw+_DVPSVehicleArea.xy;
                float3 world=mul(_DVPSVehicleLocalToWorld,float4(xz.x,h,xz.y,1)).xyz;
                float d=max(abs(world.x-_DVPSNearArea.x),abs(world.z-_DVPSNearArea.y));
                float farDistance=max(abs(world.x-_DVPSFarArea.x),abs(world.z-_DVPSFarArea.y));
                float open;
                [branch] if (d<_DVPSNearArea.z*.85)
                    open=sky(DVPS_HEIGHT_ARGUMENT(_DVPSNearHeight),_DVPSNearArea,_DVPSAccumulation.z,world);
                else if (farDistance<_DVPSFarArea.z*.95)
                    open=sky(DVPS_HEIGHT_ARGUMENT(_DVPSFarHeight),_DVPSFarArea,_DVPSAccumulation.w,world);
                else
                    open=sky(DVPS_HEIGHT_ARGUMENT(_DVPSDistantHeight),_DVPSDistantArea,_DVPSDistantOffset,world);
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
        Pass
        {
            Name "SNOW_EXCLUSION"
            Cull Back ZWrite Off ZTest LEqual
            // Excluded pixels need only the marker, not local coordinates or
            // a slope. Preserve exact mesh/depth/alpha silhouettes while avoiding
            // normal transforms and both snow-data outputs for capped vehicles.
            ColorMask A 0
            ColorMask 0 1
            CGPROGRAM
            #pragma target 3.0
            #pragma multi_compile_instancing
            #pragma vertex exclusionVertex
            #pragma fragment exclusionFragment
            #pragma multi_compile __ UNITY_SINGLE_PASS_STEREO
            ENDCG
        }
        Pass
        {
            Name "INSTANCED_SNOW"
            Cull Back ZWrite Off ZTest LEqual
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma vertex instancedSnowVertex
            #pragma fragment instancedSnowFragment
            #pragma multi_compile __ UNITY_SINGLE_PASS_STEREO
            UNITY_INSTANCING_BUFFER_START(DVPSFullSnow)
                UNITY_DEFINE_INSTANCED_PROP(float, _DVPSInstanceVehicleIndex)
                UNITY_DEFINE_INSTANCED_PROP(float4x4, _DVPSInstanceWorldToLocal)
            UNITY_INSTANCING_BUFFER_END(DVPSFullSnow)
            struct instancedSnowVarying
            {
                float4 pos:SV_POSITION;
                float3 local:TEXCOORD0;
                float2 uv:TEXCOORD1;
                float4 screen:TEXCOORD2;
                float eye:TEXCOORD3;
                float3 normal:TEXCOORD4;
                nointerpolation float vehicleIndex:TEXCOORD5;
            };
            instancedSnowVarying instancedSnowVertex(appdata_base v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                instancedSnowVarying o;
                o.pos=UnityObjectToClipPos(v.vertex);
                o.screen=ComputeScreenPos(o.pos);o.eye=-UnityObjectToViewPos(v.vertex).z;
                float4x4 worldToLocal=UNITY_ACCESS_INSTANCED_PROP(DVPSFullSnow,_DVPSInstanceWorldToLocal);
                o.local=mul(worldToLocal,mul(unity_ObjectToWorld,v.vertex)).xyz;
                o.normal=UnityObjectToWorldNormal(v.normal);
                o.uv=v.texcoord.xy*_DVPSVehicleST.xy+_DVPSVehicleST.zw;
                o.vehicleIndex=UNITY_ACCESS_INSTANCED_PROP(DVPSFullSnow,_DVPSInstanceVehicleIndex);
                return o;
            }
            dataOutput instancedSnowFragment(instancedSnowVarying i)
            {
                visibleSurface(i.screen,i.eye,i.uv);
                dataOutput o;
                o.surface=float4(i.local,i.vehicleIndex);
                o.slope=saturate(normalize(i.normal).y);
                return o;
            }
            ENDCG
        }
        Pass
        {
            Name "CAPPED_CAR_VOLUME"
            Cull Front ZWrite Off ZTest Always
            ColorMask A 0
            ColorMask 0 1
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma vertex volumeVertex
            #pragma fragment volumeFragment
            float4x4 _DVPSExclusionInverseVP;
            struct volumeVarying
            {
                float4 pos:SV_POSITION;
                float4 screen:TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            volumeVarying volumeVertex(appdata_base v)
            {
                UNITY_SETUP_INSTANCE_ID(v);
                volumeVarying o;
                UNITY_TRANSFER_INSTANCE_ID(v,o);
                o.pos=UnityObjectToClipPos(v.vertex);o.screen=ComputeScreenPos(o.pos);
                return o;
            }
            half4 volumeFragment(volumeVarying i):SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float2 uv=i.screen.xy/i.screen.w;
                float depth=SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture,uv);
                #if defined(UNITY_REVERSED_Z)
                clip(depth-0.0000001);
                #else
                clip(0.9999999-depth);
                depth=lerp(UNITY_NEAR_CLIP_VALUE,1,depth);
                #endif
                float4 clipPosition=float4(uv*2-1,depth,1);
                #if UNITY_UV_STARTS_AT_TOP
                clipPosition.y=-clipPosition.y;
                #endif
                float4 world=mul(_DVPSExclusionInverseVP,clipPosition);
                float3 local=mul(unity_WorldToObject,float4(world.xyz/world.w,1)).xyz;
                // The box silhouette alone is insufficient: visible ground
                // behind/below a car must keep its normal snow coverage.
                clip(.5-abs(local));
                return half4(0,0,0,-1);
            }
            ENDCG
        }
        Pass
        {
            Name "DISTANT_CAR_SNOW"
            Cull Front ZWrite Off ZTest Always
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma vertex farVertex
            #pragma fragment farFragment
            float4x4 _DVPSExclusionInverseVP;
            sampler2D _CameraGBufferTexture2;
            UNITY_INSTANCING_BUFFER_START(DVPSFarSnow)
                UNITY_DEFINE_INSTANCED_PROP(float,_DVPSInstanceVehicleIndex)
                UNITY_DEFINE_INSTANCED_PROP(float4x4,_DVPSInstanceWorldToLocal)
            UNITY_INSTANCING_BUFFER_END(DVPSFarSnow)
            struct farVarying
            {
                float4 pos:SV_POSITION;float4 screen:TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            farVarying farVertex(appdata_base v)
            {
                UNITY_SETUP_INSTANCE_ID(v);farVarying o;UNITY_TRANSFER_INSTANCE_ID(v,o);
                o.pos=UnityObjectToClipPos(v.vertex);o.screen=ComputeScreenPos(o.pos);return o;
            }
            dataOutput farFragment(farVarying i)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float2 uv=i.screen.xy/i.screen.w;
                float depth=SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture,uv);
                #if defined(UNITY_REVERSED_Z)
                clip(depth-.0000001);
                #else
                clip(.9999999-depth);depth=lerp(UNITY_NEAR_CLIP_VALUE,1,depth);
                #endif
                float4 p=float4(uv*2-1,depth,1);
                #if UNITY_UV_STARTS_AT_TOP
                p.y=-p.y;
                #endif
                float4 h=mul(_DVPSExclusionInverseVP,p);float3 world=h.xyz/h.w;
                float3 geometric=normalize(cross(ddy(world),ddx(world)));
                float3 native=tex2D(_CameraGBufferTexture2,uv).xyz*2-1;
                if(dot(geometric,native)<0)geometric=-geometric;
                clip(.5-abs(mul(unity_WorldToObject,float4(world,1)).xyz));
                float4x4 toVehicle=UNITY_ACCESS_INSTANCED_PROP(DVPSFarSnow,_DVPSInstanceWorldToLocal);
                dataOutput o;
                o.surface=float4(mul(toVehicle,float4(world,1)).xyz,UNITY_ACCESS_INSTANCED_PROP(DVPSFarSnow,_DVPSInstanceVehicleIndex));
                o.slope=saturate(geometric.y);return o;
            }
            ENDCG
        }
    }
    Fallback Off
}
