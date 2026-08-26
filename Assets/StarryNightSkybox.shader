Shader "Custom/StarryNightSkybox"
{
    Properties
    {
        [Header(Base Sky)]
        _Tex ("Cubemap", Cube) = "grey" {}
        _Tint ("Tint Color", Color) = (.5, .5, .5, 1)
        _Exposure ("Exposure", Range(0, 8)) = 1.0
        _Rotation ("Rotation", Range(0, 360)) = 0

        [Header(Stars)]
        _StarDensity ("Star Density", Range(20, 200)) = 90
        _StarSize ("Star Size (cell fraction)", Range(0.02, 0.35)) = 0.10
        _StarBrightness ("Star Brightness", Range(0, 6)) = 2.2
        _StarColor ("Star Color", Color) = (0.82, 0.88, 1.0, 1)
        _WarmStarChance ("Warm Star Chance", Range(0, 1)) = 0.25
        _TwinkleAmount ("Twinkle Amount", Range(0, 1)) = 0.25
        _TwinkleSpeed ("Twinkle Speed", Range(0, 10)) = 1.5

        [Header(Masks)]
        _HorizonFadeStart ("Horizon Fade Start (dir.y)", Range(0, 0.6)) = 0.03
        _HorizonFadeEnd ("Horizon Fade End (dir.y)", Range(0, 0.6)) = 0.18
        _CloudMaskStart ("Cloud Mask Start (luminance)", Range(0, 1)) = 0.045
        _CloudMaskEnd ("Cloud Mask End (luminance)", Range(0, 1)) = 0.16
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            samplerCUBE _Tex;
            half4 _Tex_HDR;
            half4 _Tint;
            half _Exposure;
            float _Rotation;

            float _StarDensity, _StarSize, _StarBrightness;
            half4 _StarColor;
            float _WarmStarChance, _TwinkleAmount, _TwinkleSpeed;
            float _HorizonFadeStart, _HorizonFadeEnd, _CloudMaskStart, _CloudMaskEnd;

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float3 dir : TEXCOORD0; };

            float3 RotateY(float3 v, float deg)
            {
                float a = deg * UNITY_PI / 180.0;
                float s = sin(a), c = cos(a);
                return float3(c * v.x + s * v.z, v.y, -s * v.x + c * v.z);
            }

            // 3D hash: one cell -> 3 pseudo-random numbers in [0,1)
            float3 hash33(float3 p)
            {
                p = frac(p * float3(443.897, 441.423, 437.195));
                p += dot(p, p.yxz + 19.19);
                return frac((p.xxy + p.yxx) * p.zyx);
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(RotateY(i.dir, _Rotation));

                // --- Base sky: exact replica of Unity's Skybox/Cubemap math ---
                half4 tex = texCUBE(_Tex, dir);
                half3 sky = DecodeHDR(tex, _Tex_HDR);
                sky *= _Tint.rgb * unity_ColorSpaceDouble.rgb;
                sky *= _Exposure;

                // --- Stars: 3D cell grid over the view direction ---
                float3 p = dir * _StarDensity;          // scale sphere to star-grid space
                float3 cell = floor(p);
                float3 rnd = hash33(cell);              // this cell's random triplet

                // star center inside the cell, pulled away from edges so it never clips
                float3 starPos = cell + 0.5 + (rnd - 0.5) * (1.0 - 2.0 * _StarSize);

                float dist = length(p - starPos);
                float star = 1.0 - smoothstep(_StarSize * 0.35, _StarSize, dist);

                // magnitude: many dim stars, few bright ones (pow biases toward 0)
                float mag = pow(hash33(cell + 101.0).x, 5.0);
                float brightness = lerp(0.12, 1.0, mag);

                // twinkle: subtle per-star sinusoidal shimmer (0 = static sky)
                float tw = sin(_Time.y * _TwinkleSpeed + rnd.y * 6.2831) * 0.5 + 0.5;
                brightness *= lerp(1.0, lerp(0.6, 1.15, tw), _TwinkleAmount);

                // warm/cool tint variation per star
                half3 warm = half3(1.0, 0.85, 0.6);
                half3 starTint = lerp(_StarColor.rgb, warm, step(1.0 - _WarmStarChance, rnd.z));

                // --- Masks ---
                // 1) horizon: no stars at/below the horizon band
                float horizonMask = smoothstep(_HorizonFadeStart, _HorizonFadeEnd, dir.y);
                // 2) clouds: where the sky texture is brighter than clear night, hide stars
                float lum = dot(sky, half3(0.299, 0.587, 0.114));
                float cloudMask = 1.0 - smoothstep(_CloudMaskStart, _CloudMaskEnd, lum);

                half3 stars = starTint * star * brightness * _StarBrightness * horizonMask * cloudMask;

                return fixed4(sky + stars, 1);
            }
            ENDCG
        }
    }
    Fallback Off
}