// Runtime texture upscaling for SWBF2's 2005-era textures.
//
// Pass 0: Catmull-Rom bicubic upsample. Reconstructs a smooth, non-blocky
//         magnification from the low-res source (far better than the bilinear
//         the GPU would otherwise give a 256x256 texture stretched over a 4K
//         screen).
// Pass 1: Contrast-adaptive sharpening (CAS-style). Restores the micro-detail
//         bicubic softens, while clamping against the local min/max so it
//         does not ring on edges or amplify the source's compression noise.
//
// Normal maps go through pass 0 only (sharpening a normal map distorts it),
// then are re-normalized.
Shader "BF3Legacy/TextureUpscale"
{
    Properties
    {
        _MainTex ("Source", 2D) = "white" {}
        _Sharpness ("Sharpness", Range(0,1)) = 0.5
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"

        sampler2D _MainTex;
        float4 _MainTex_TexelSize;   // (1/w, 1/h, w, h)
        float _Sharpness;
        float _IsNormalMap;

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv  : TEXCOORD0;
        };

        v2f vert(appdata_img v)
        {
            v2f o;
            o.pos = UnityObjectToClipPos(v.vertex);
            o.uv = v.texcoord;
            return o;
        }

        // Catmull-Rom weights for a 4-tap row
        float4 CatmullRomWeights(float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5 * float4(
                -t3 + 2.0 * t2 - t,
                 3.0 * t3 - 5.0 * t2 + 2.0,
                -3.0 * t3 + 4.0 * t2 + t,
                 t3 - t2);
        }

        float4 SampleBicubic(float2 uv)
        {
            float2 texSize = _MainTex_TexelSize.zw;
            float2 invTex  = _MainTex_TexelSize.xy;

            float2 coord = uv * texSize - 0.5;
            float2 base  = floor(coord);
            float2 f     = coord - base;

            float4 wx = CatmullRomWeights(f.x);
            float4 wy = CatmullRomWeights(f.y);

            float4 result = 0;
            [unroll]
            for (int y = 0; y < 4; ++y)
            {
                float4 row = 0;
                [unroll]
                for (int x = 0; x < 4; ++x)
                {
                    float2 sampleUV = (base + float2(x - 1, y - 1) + 0.5) * invTex;
                    row += tex2D(_MainTex, sampleUV) * wx[x];
                }
                result += row * wy[y];
            }
            return result;
        }
        ENDCG

        // ---- Pass 0: bicubic upsample ----
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag_upsample
            #pragma target 3.0

            float4 frag_upsample(v2f i) : SV_Target
            {
                float4 c = SampleBicubic(i.uv);

                // keep normal maps unit-length after filtering
                if (_IsNormalMap > 0.5)
                {
                    float3 n = c.rgb * 2.0 - 1.0;
                    n = normalize(n);
                    c.rgb = n * 0.5 + 0.5;
                }
                return c;
            }
            ENDCG
        }

        // ---- Pass 1: contrast-adaptive sharpen ----
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag_sharpen
            #pragma target 3.0

            float4 frag_sharpen(v2f i) : SV_Target
            {
                float2 d = _MainTex_TexelSize.xy;

                float4 c  = tex2D(_MainTex, i.uv);
                float3 n  = tex2D(_MainTex, i.uv + float2(0, +d.y)).rgb;
                float3 s  = tex2D(_MainTex, i.uv + float2(0, -d.y)).rgb;
                float3 e  = tex2D(_MainTex, i.uv + float2(+d.x, 0)).rgb;
                float3 w  = tex2D(_MainTex, i.uv + float2(-d.x, 0)).rgb;

                // local range drives how much sharpening is safe here
                float3 mn = min(min(min(n, s), min(e, w)), c.rgb);
                float3 mx = max(max(max(n, s), max(e, w)), c.rgb);
                float3 range = mx - mn;

                // low-contrast areas get the most sharpening; edges the least,
                // which is what keeps this from ringing
                float3 amount = saturate(1.0 - range) * _Sharpness;

                float3 blur = (n + s + e + w) * 0.25;
                float3 sharp = c.rgb + (c.rgb - blur) * amount * 2.0;

                // never push outside the neighbourhood - hard anti-ring clamp
                sharp = clamp(sharp, mn, mx);

                return float4(sharp, c.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
