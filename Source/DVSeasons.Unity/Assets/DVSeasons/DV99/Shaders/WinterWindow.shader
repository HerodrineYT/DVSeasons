Shader "Hidden/DVSeasons/WinterWindow"
{
    Properties { _FrostPattern("Static frost pattern",2D)="gray" {} _SnowMask("Deposited snow and wiped ice",2D)="black" {} }
    SubShader
    {
        Tags { "Queue"="Transparent+20" "RenderType"="Transparent" "IgnoreProjector"="True" "DisableBatching"="True" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off ZTest LEqual Cull Off Offset -1,-1
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _SnowMask, _FrostPattern;
            float4x4 _MeshToPane;
            float4 _PaneSize, _Climate;
            float _Daylight, _UseBakedUVs;
            struct v2f { float4 pos:SV_POSITION;float2 uv:TEXCOORD0;UNITY_FOG_COORDS(1) };
            v2f vert(appdata_base v)
            {
                v2f o;o.pos=UnityObjectToClipPos(v.vertex);
                // Local vertices remain fixed even if animation or interpolation
                // moves the renderer after its property block was updated.
                float3 p=mul(_MeshToPane,v.vertex).xyz;
                o.uv=p.xy/max(_PaneSize.xy,.01)*_PaneSize.zw+.5;
                if (_UseBakedUVs > .5) o.uv=v.texcoord.xy;
                UNITY_TRANSFER_FOG(o,o.pos);return o;
            }
            fixed4 frag(v2f i):SV_Target
            {
                float2 uv=saturate(i.uv);
                float2 mask=tex2D(_SnowMask,uv).rg;
                float4 pattern=tex2D(_FrostPattern,uv);
                float cloud=pattern.r, crystal=pattern.g, fern=pattern.b, rank=pattern.a;
                // Continuous thermal envelope also covers restored snow masks.
                // At +5C nothing remains to disappear in a single frame.
                // Keep the original crystal shapes during thawing. Temperature
                // fades their opacity, rather than cutting away their footprint.
                // Range matches WindowWinterClimate.IceVisibility.
                float iceRemaining=1-smoothstep(2,5,_Climate.w);
                float coverage=saturate(_Climate.x);
                float frost=smoothstep(rank-.12,rank+.10,coverage)*
                    saturate(.48+.30*cloud+.14*crystal+.22*fern);
                frost*=(1-mask.g)*saturate(coverage*20)*iceRemaining;
                // The blade also clears the cloudy film left after thawing.
                // Reuse its local swept mask so untouched glass stays misted.
                float fog=min(_Climate.y,.65*(1-smoothstep(8,12,_Climate.w)))*(.48+.22*cloud)*(1-frost)*(1-mask.g);
                // Dense deposited snow keeps the broader fade from 0.3.16.
                float snow=smoothstep(.02,.7,mask.r)*.98*(1-smoothstep(0,5,_Climate.w));
                float alpha=saturate(frost+fog+snow*(1-frost));
                clip(alpha-.003);
                float light=lerp(.10,.93,saturate(_Daylight));
                float3 ice=lerp(float3(.48,.61,.72),float3(.88,.93,.97),
                    saturate(cloud*.50+crystal*.20+fern*.62));
                ice=lerp(ice,float3(.87,.91,.95),snow);
                fixed4 colour=fixed4(ice*light,alpha);
                UNITY_APPLY_FOG(i.fogCoord,colour);return colour;
            }
            ENDCG
        }
        Pass
        {
            ZTest Always ZWrite Off Cull Off Blend Off
            Tags { "LightMode"="DVSeasonsBake" }
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment bake
            #pragma target 3.0
            #include "UnityCG.cginc"
            float hash(float2 p) { return frac(sin(dot(p,float2(127.1,311.7)))*43758.5453); }
            float noise(float2 p)
            {
                float2 i=floor(p),f=frac(p);f=f*f*(3-2*f);
                return lerp(lerp(hash(i),hash(i+float2(1,0)),f.x),lerp(hash(i+float2(0,1)),hash(i+1),f.x),f.y);
            }
            fixed4 bake(v2f_img i):SV_Target
            {
                float2 uv=i.uv;
                float cloud=noise(uv*9)*.65+noise(uv*31)*.35;
                float crystal=noise(uv*180);
                // Warp the crystal field so seeds do not form a visible grid.
                float2 field=uv*11+float2(noise(uv*4.7),noise(uv*4.7+19.3))*5;
                float2 cell=floor(field), p=frac(field)-.5;
                p+=(float2(hash(cell+3.1),hash(cell+8.7))-.5)*.25;
                float angle=hash(cell)*6.28318;
                p=mul(float2x2(cos(angle),-sin(angle),sin(angle),cos(angle)),p);
                p*=lerp(.9,2.4,hash(cell+11.2));
                // Randomly oriented fern crystals, with a spine and paired
                // branches. Derivative filtering keeps fine veins stable in motion.
                float aa=max(fwidth(p.x),.006);
                float spine=1-smoothstep(.008,.008+aa,abs(p.x));
                float branch=abs(frac((p.y-abs(p.x)*.72)*9)-.5)/9;
                float fern=max(spine,(1-smoothstep(.004,.004+aa,branch))*
                    saturate((.42-abs(p.x))*5))*saturate((.5-length(p))*8)*
                    smoothstep(.20,.65,hash(cell+5.7));
                // Warm air clears the centre/bottom first, leaving feathered
                // frost at the frame. The pattern stays fixed to this pane.
                float edge=pow(saturate(length((uv-float2(.5,.27))*float2(1.35,1))),.75);
                float rank=saturate(.82-edge*.64+(cloud-.5)*.28);
                return float4(cloud,crystal,fern,rank);
            }
            ENDCG
        }
    }
    Fallback Off
}
