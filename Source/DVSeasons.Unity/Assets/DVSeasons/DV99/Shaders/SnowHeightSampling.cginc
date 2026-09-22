#ifndef DVPS_HEIGHT_SAMPLING_INCLUDED
#define DVPS_HEIGHT_SAMPLING_INCLUDED
// Interpolate visibility after comparing each height. Interpolating height
// before the comparison creates fictitious slopes across roofs and shelters.
#if defined(SHADER_API_D3D11) && !defined(DVPS_REFERENCE_HEIGHT_SAMPLING)
    #define DVPS_HEIGHT_TEXTURE(name) Texture2D name; SamplerState sampler##name
    #define DVPS_HEIGHT_PARAMETER Texture2D heights, SamplerState samplerheights
    #define DVPS_HEIGHT_ARGUMENT(name) name, sampler##name
    float4 DVPSReadHeights(DVPS_HEIGHT_PARAMETER,float2 uv,float2 corner,float texel)
    {
        // Gather is lower-left, lower-right, upper-right, upper-left; put it
        // into the original UV order (00,10,01,11). Point/Clamp is unchanged.
        // Query the centre of the chosen footprint. Gather's hardware UV
        // quantization at a texel boundary must not select an adjacent quad.
        return heights.GatherRed(samplerheights,corner+texel*.5).wzxy;
    }
    float4 DVPSReadArrayHeights(Texture2DArray heights,SamplerState heightSampler,float3 corner,float texel)
    {
        return heights.GatherRed(heightSampler,float3(corner.xy+texel*.5,corner.z)).wzxy;
    }
#else
    #define DVPS_HEIGHT_TEXTURE(name) sampler2D name
    #define DVPS_HEIGHT_PARAMETER sampler2D heights
    #define DVPS_HEIGHT_ARGUMENT(name) name
    float4 DVPSReadHeights(DVPS_HEIGHT_PARAMETER,float2 uv,float2 corner,float texel)
    {
        return float4(tex2D(heights,corner).r,tex2D(heights,corner+float2(texel,0)).r,
            tex2D(heights,corner+float2(0,texel)).r,tex2D(heights,corner+texel).r);
    }
#endif
#endif
