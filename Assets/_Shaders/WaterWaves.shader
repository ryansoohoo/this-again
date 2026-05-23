// Animated open-water shader for the dual-grid mesh. Two DECOUPLED systems:
//  1) WAVE FIELD: a scrolling value-noise (Perlin-like) field, sampled once per water cell, decides which
//     tiles are rippling vs flat-calm and drifts slowly so swells travel. _NoiseScale = wave frequency
//     (higher -> smaller, more frequent waves); _FlowSpeed = drift; _Calm = fraction that stays flat blue.
//  2) SPRITE ANIMATION: rippling tiles cycle the 4-frame sheet loop at _AnimSpeed frames/sec, desynced per
//     tile by a hash, INDEPENDENT of how fast waves drift -> you can have slow ripples + slow-moving swells.
// All GPU-side (no per-frame CPU). Mesh feeds frame-0 / row-0 UVs; this shader picks frame (U) and style (V).
// Noise is sampled at round(worldPos) so each display tile shares ONE frame (no split tiles). All knobs are
// driven by GridManager from WaterSettings (live-tunable via TunerPanels).
Shader "Custom/WaterWaves"
{
    Properties
    {
        [PerRendererData] _MainTex ("Water Sheet", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _CalmColor ("Calm Water Color", Color) = (0.1137, 0.1686, 0.3255, 1)
        _CellSize ("World Units / Cell", Float) = 1
        _FrameCount ("Frame Count", Float) = 4
        _FrameStride ("Frame Stride (U)", Float) = 0.25
        _AnimSpeed ("Ripple Frames/sec", Float) = 1.5
        _NoiseScale ("Wave Frequency", Float) = 0.35
        _WindDir ("Wind Direction (xy)", Vector) = (1, 0.35, 0, 0)
        _FlowSpeed ("Wave Drift", Float) = 0.06
        _Calm ("Calm Fraction", Range(0,1)) = 0.5
        _StyleRow ("Water Style Row", Float) = 0
        _DayTint ("Day/Night Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f     { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float2 wpos : TEXCOORD1; fixed4 color : COLOR; };

            sampler2D _MainTex;
            fixed4 _Color, _CalmColor, _DayTint;
            float4 _WindDir;
            float _CellSize, _FrameCount, _FrameStride, _AnimSpeed, _NoiseScale, _FlowSpeed, _Calm, _StyleRow;

            float hash21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
            float vnoise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float a = hash21(i), b = hash21(i + float2(1,0)), c = hash21(i + float2(0,1)), d = hash21(i + float2(1,1));
                float2 u = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xy;
                o.uv = v.uv;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 cell = floor(i.wpos / max(_CellSize, 0.0001) + 0.5);     // one sample per display tile
                float2 uv = i.uv;
                uv.y -= _StyleRow * (1.0 / 3.0);                                // pick water style row (3 rows)

                // --- wave field: where ripples are, drifting slowly ---
                float2 dir = normalize(_WindDir.xy + float2(1e-5, 1e-5));
                float2 flow = dir * (_Time.y * _FlowSpeed);
                float wave = vnoise(cell * _NoiseScale + flow);
                wave = 0.7 * wave + 0.3 * vnoise(cell * _NoiseScale * 2.3 + flow * 1.7 + 17.0);
                if (saturate(wave) < _Calm) return _CalmColor * i.color * _DayTint;   // flat calm water

                // --- sprite animation: cycle frames over time, desynced per tile ---
                float phase = hash21(cell + 3.7);
                float frame = floor(frac(_Time.y * _AnimSpeed / _FrameCount + phase) * _FrameCount);
                frame = min(frame, _FrameCount - 1.0);
                uv.x += frame * _FrameStride;
                return tex2D(_MainTex, uv) * i.color * _DayTint;
            }
            ENDCG
        }
    }
}
