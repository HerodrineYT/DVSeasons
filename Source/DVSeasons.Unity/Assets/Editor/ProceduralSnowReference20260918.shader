Shader "Hidden/DVSeasonsTests/ProceduralSnowReference20260918"
{
    Properties
    {
        _MainTex ("Ambient occlusion source",2D)="white" {}
        _SnowSrcBlend ("Source",Float)=1
        _SnowDstBlend ("Destination",Float)=0
        _SnowAlphaSrcBlend ("Alpha source",Float)=1
        _SnowAlphaDstBlend ("Alpha destination",Float)=0
        _SnowSpecAlphaDstBlend ("Roughness destination",Float)=0
    }
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            Blend 0 [_SnowSrcBlend] [_SnowDstBlend], [_SnowAlphaSrcBlend] [_SnowAlphaDstBlend]
            Blend 1 [_SnowSrcBlend] [_SnowDstBlend], [_SnowAlphaSrcBlend] [_SnowSpecAlphaDstBlend]
            Blend 2 [_SnowSrcBlend] [_SnowDstBlend], [_SnowAlphaSrcBlend] [_SnowAlphaDstBlend]
            CGPROGRAM
            #pragma target 4.5
            #pragma multi_compile __ DVPS_FAST_HDR
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "../DVSeasons/DV99/Shaders/SnowHeightSampling.cginc"
            sampler2D _DVPSDiffuse, _DVPSSpecular, _DVPSNormal, _DVPSLighting;
            DVPS_HEIGHT_TEXTURE(_DVPSNearHeight);
            DVPS_HEIGHT_TEXTURE(_DVPSFarHeight);
            DVPS_HEIGHT_TEXTURE(_DVPSDistantHeight);
            sampler2D _DVPSVehicleData, _DVPSVehicleSlope;
            UNITY_DECLARE_TEX2DARRAY(_DVPSVehicleHeights);
            UNITY_DECLARE_TEX2DARRAY(_DVPSVehicleSnow);
            float4 _DVPSVehicleAreas[1024];
            float4 _DVPSVehicleSnowAreas[1024];
            float _DVPSVehicleSnowRemaining[1024];
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float4x4 _DVPSInverseVP;
            float4 _DVPSNearArea, _DVPSFarArea, _DVPSDistantArea;
            float3 _DVPSWorldOffset;
            float3 _DVPSHeightOffsets;
            float4 _DVPSAmbient;
            float _DVPSAmount, _DVPSHDR;
            float _DVPSGlareReduction;
            float _DVPSObjectLimitEnabled;
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata_img v)
            {
                v2f o; o.pos = v.vertex; o.uv = v.texcoord;
                #if UNITY_UV_STARTS_AT_TOP
                o.uv.y = 1 - o.uv.y;
                #endif
                return o;
            }
            sampler2D _DVPSNoise;
            float noise(float3 p) { return tex2D(_DVPSNoise,(p.xz+p.y*float2(0.37,0.61)+0.5)/128).r; }
            float exposure(DVPS_HEIGHT_PARAMETER, float4 area, float heightOffset, float3 world, float normalY, float2 heightGradient, bool movingBlade)
            {
                // Height maps use explicit world-XZ UVs, independent of camera UV orientation.
                float2 uv = (world.xz-area.xy)/(area.z*2)+0.5;
                float2 grid=uv*1024-0.5, blend=frac(grid);
                float2 corner=(floor(grid)+0.5)/1024;
                float bias=0.04+min(0.5,area.w*sqrt(saturate(1-normalY*normalY))/max(0.3,normalY));
                float4 h=DVPSReadHeights(DVPS_HEIGHT_ARGUMENT(heights),uv,corner,1.0/1024);
                // Only detailed geometry present in the exposure map receives
                // the dynamic overlay. Unloaded/displaced distant landscape keeps
                // its native winter textures instead of sampling an empty map.
                // A rail head is only 7 cm wide, below the exposure texel size.
                // Sampling its sleeper or ballast does not mean the known blade
                // disappeared. Only geometry ABOVE a registered moving blade can
                // shelter it; stationary world/proxy validation remains symmetric.
                float4 difference=h+heightOffset-world.y;
                // Compare each sample at the pixel's position on its geometric
                // plane. Far-map texels span metres: comparing their raw height
                // to this pixel creates narrow snow contours on ordinary slopes.
                // Keep the four visibility tests separate so roof edges never
                // become fictitious interpolated slopes through sheltered ground.
                if (!movingBlade)
                {
                    float2 offset=(corner-.5)*(area.z*2)+area.xy-world.xz;
                    float baseHeight=dot(heightGradient,offset);
                    float2 stepHeight=heightGradient*area.w;
                    difference-=baseHeight+float4(0,stepHeight.x,stepHeight.y,stepHeight.x+stepHeight.y);
                }
                if (!movingBlade) difference=abs(difference);
                float4 open=1-smoothstep(bias,bias+0.12,difference);
                return lerp(lerp(open.x,open.y,blend.x),lerp(open.z,open.w,blend.x),blend.y);
            }
            float vehicleExposure(float3 local,int index,float normalY)
            {
                float4 area=_DVPSVehicleAreas[index];
                float2 uv=(local.xz-area.xy)/(area.zw*2)+0.5;
                float2 grid=uv*256-0.5, blend=frac(grid), corner=(floor(grid)+0.5)/256;
                #if defined(SHADER_API_D3D11) && !defined(DVPS_REFERENCE_HEIGHT_SAMPLING)
                float4 h=DVPSReadArrayHeights(_DVPSVehicleHeights,sampler_DVPSVehicleHeights,float3(corner,index),1.0/256);
                #else
                float4 h=float4(UNITY_SAMPLE_TEX2DARRAY(_DVPSVehicleHeights,float3(corner,index)).r,
                    UNITY_SAMPLE_TEX2DARRAY(_DVPSVehicleHeights,float3(corner+float2(1.0/256,0),index)).r,
                    UNITY_SAMPLE_TEX2DARRAY(_DVPSVehicleHeights,float3(corner+float2(0,1.0/256),index)).r,
                    UNITY_SAMPLE_TEX2DARRAY(_DVPSVehicleHeights,float3(corner+1.0/256,index)).r);
                #endif
                float bias=0.025+min(0.15,max(area.z,area.w)/128*sqrt(saturate(1-normalY*normalY))/max(0.3,normalY));
                float4 open=1-smoothstep(bias,bias+0.07,h-local.y);
                return lerp(lerp(open.x,open.y,blend.x),lerp(open.z,open.w,blend.x),blend.y);
            }
            struct output { half4 diffuse : SV_Target0; half4 specular : SV_Target1;
                half4 lighting : SV_Target2; };
            output frag(v2f i)
            {
                if (_DVPSAmount <= 0) discard;
                float4 vehicle=tex2D(_DVPSVehicleData,i.uv);
                if (vehicle.w < -0.5 && vehicle.w > -1.5) discard;
                // Finite mode admits only selected objects. Rail marks retain
                // the selected surface's slope channel when overwriting its data.
                if (_DVPSObjectLimitEnabled > .5 && vehicle.w < .5 &&
                    vehicle.w > -2.5 && (vehicle.w > -.5 || tex2D(_DVPSVehicleSlope,i.uv).r <= .001)) discard;
                float depth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture,i.uv);
                #if defined(UNITY_REVERSED_Z)
                if (depth <= 0.000001) discard;
                #else
                if (depth >= 0.999999) discard;
                #endif
                float4 clipPos = float4(i.uv*2-1,depth,1);
                #if UNITY_UV_STARTS_AT_TOP
                clipPos.y = -clipPos.y;
                #endif
                float4 wp = mul(_DVPSInverseVP,clipPos);
                float3 world = wp.xyz/wp.w;
                // Depth derivatives describe the actual surface rather than its
                // decorative normal map. Calculate before surface-dependent exits.
                float3 geometricNormal=cross(ddy(world),ddx(world));
                float3 unitGeometricNormal=normalize(geometricNormal);
                float2 heightGradient=abs(unitGeometricNormal.y)>.1 ?
                    -unitGeometricNormal.xz/unitGeometricNormal.y : float2(0,0);
                // Animated junction blades sample their original captured pose.
                // Keeping a displacement survives both floating-origin shifts
                // and half-float precision far from the origin.
                bool movingBlade=vehicle.w < -2.5 && vehicle.w > -3.5;
                if (movingBlade) world += vehicle.xyz;
                half4 n0=tex2D(_DVPSNormal,i.uv);
                float3 n=normalize(n0.xyz*2-1);
                // Vehicle slope comes from its mesh, independently of the
                // material normal map and screen-space depth discontinuities.
                float normalY=vehicle.w>0.5?tex2D(_DVPSVehicleSlope,i.uv).r:n.y;
                float slope = vehicle.w>0.5 ? smoothstep(0.12,0.65,normalY) : smoothstep(0.25,0.85,n.y);
                if (slope <= 0) discard;
                float distantDistance = max(abs(world.x-_DVPSDistantArea.x),abs(world.z-_DVPSDistantArea.y));
                float limit = 1-smoothstep(_DVPSDistantArea.z*0.85,_DVPSDistantArea.z*0.97,distantDistance);
                if (limit <= 0) discard;
                float shelter;
                // DV rebases the entire scene while driving. Grain and partial
                // accumulation stay in absolute coordinates across those shifts.
                float3 pattern=world-_DVPSWorldOffset;
                if (vehicle.w>0.5)
                {
                    pattern=vehicle.xyz;
                    int slot=(int)(vehicle.w+0.5)-1;
                    float4 area=_DVPSVehicleSnowAreas[slot];
                    float2 uv=(pattern.xz-area.xy)/(2*area.zw)+0.5;
                    shelter=vehicleExposure(pattern,slot,normalY)*UNITY_SAMPLE_TEX2DARRAY(_DVPSVehicleSnow,float3(uv,slot)).r*_DVPSVehicleSnowRemaining[slot];
                }
                else
                {
                    // Vehicles use their local maps exclusively; sampling the
                    // world maps first only discarded 4-12 fetches per pixel.
                    float nearDistance=max(abs(world.x-_DVPSNearArea.x),abs(world.z-_DVPSNearArea.y));
                    float farDistance=max(abs(world.x-_DVPSFarArea.x),abs(world.z-_DVPSFarArea.y));
                    if (nearDistance < _DVPSNearArea.z*0.8)
                        shelter=exposure(DVPS_HEIGHT_ARGUMENT(_DVPSNearHeight),_DVPSNearArea,_DVPSHeightOffsets.x,world,n.y,heightGradient,movingBlade);
                    else
                    {
                        shelter=exposure(DVPS_HEIGHT_ARGUMENT(_DVPSFarHeight),_DVPSFarArea,_DVPSHeightOffsets.y,world,n.y,heightGradient,movingBlade);
                        if (farDistance > _DVPSFarArea.z*.8)
                            shelter=lerp(shelter,exposure(DVPS_HEIGHT_ARGUMENT(_DVPSDistantHeight),_DVPSDistantArea,_DVPSHeightOffsets.z,world,n.y,heightGradient,movingBlade),
                                smoothstep(_DVPSFarArea.z*.8,_DVPSFarArea.z*.95,farDistance));
                        if (nearDistance < _DVPSNearArea.z*0.95)
                            shelter=lerp(exposure(DVPS_HEIGHT_ARGUMENT(_DVPSNearHeight),_DVPSNearArea,_DVPSHeightOffsets.x,world,n.y,heightGradient,movingBlade),shelter,
                                smoothstep(_DVPSNearArea.z*0.8,_DVPSNearArea.z*0.95,nearDistance));
                    }
                }
                float rank = noise(pattern*0.8)*0.7+noise(pattern*3.2)*0.3;
                float edge=max(0.10,fwidth(rank));
                float cover = smoothstep(rank-edge,rank+edge,_DVPSAmount*1.24-0.12)*slope*shelter*limit;
                if(vehicle.w < -1.5 && vehicle.w > -2.5) cover*=1-saturate(vehicle.x);
                if (cover <= 0.0001) discard;
                output o;
                half4 diffuse=tex2D(_DVPSDiffuse,i.uv);

                float grain = lerp(0.5,noise(pattern*32),1-saturate(length(fwidth(pattern))*24));
                half3 snow=half3(0.78,0.81,0.84)*lerp(0.92,1.0,grain);
                snow *= lerp(1,.55,saturate(_DVPSGlareReduction*.5));
                #if defined(DVPS_FAST_HDR)
                // Hardware blending keeps the native buffers in place. Powder
                // lowers smoothness towards zero; AO and lighting alpha survive.
                o.diffuse=half4(snow,cover);
                o.specular=half4(0.04,0.04,0.04,cover);
                o.lighting=half4(snow*max(0,_DVPSAmbient.rgb)*diffuse.r,cover);
                #else
                half4 specular=tex2D(_DVPSSpecular,i.uv);
                half4 lighting=tex2D(_DVPSLighting,i.uv);
                o.diffuse=half4(lerp(diffuse.rgb,snow,cover),diffuse.a);
                o.specular=lerp(specular,half4(0.04,0.04,0.04,0.22),cover);
                // Keep the object's actual curvature. Tiny procedural grain belongs
                // in albedo, rather than a second expensive, flickering normal noise.
                // GBuffer3 already contains indirect light. Replace only the covered
                // fraction with scene ambient, before native reflections/direct lights.
                half3 indirect=_DVPSHDR>0.5?lighting.rgb:-log2(max(lighting.rgb,0.00001));
                indirect=lerp(indirect,snow*max(0,_DVPSAmbient.rgb)*diffuse.a,cover);
                o.lighting=half4(_DVPSHDR>0.5?indirect:exp2(-indirect),lighting.a);
                #endif
                return o;
            }
            ENDCG
        }
        // Extract only native ambient occlusion for the compact HDR copy. The
        // LDR path still reads full diffuse RGBA from its unchanged first copy.
        Pass
        {
            Cull Off ZWrite Off ZTest Always Blend Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment copyAmbientOcclusion
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            half4 copyAmbientOcclusion(v2f_img i):SV_Target
            { return tex2D(_MainTex,i.uv).aaaa; }
            ENDCG
        }
    }
    Fallback Off
}
