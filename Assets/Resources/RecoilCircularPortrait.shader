Shader "Recoil/CircularPortrait"
{
    Properties
    {
        _MainTex ("Portrait", 2D) = "white" {}
        _Cover ("Cover UV", Vector) = (1,1,0,0)
        _PortraitSize ("Portrait diameter in pixels", Float) = 200
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" }
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Cover;
            float _PortraitSize;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Output { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            Output vert(Input v)
            {
                Output o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            fixed4 frag(Output i) : SV_Target
            {
                const float padding = 5.0;
                float diameter = max(_PortraitSize, 1.0);
                float2 pixel = (i.uv - 0.5) * (diameter + 2 * padding);
                float radius = diameter * 0.5;
                float distance = length(pixel);
                float edge = max(fwidth(distance) * 0.5, 0.5);
                float circle = 1 - smoothstep(radius - edge, radius + edge, distance);
                float border = circle * smoothstep(radius - 1 - edge, radius - 1 + edge, distance);
                float outside = max(distance - radius, 0);
                float glow = exp(-0.5 * outside * outside / 2.8) * (1 - circle);
                glow *= 1 - smoothstep(4, 5, outside);
                float2 portraitUV = pixel / diameter + 0.5;
                fixed4 portrait = tex2D(_MainTex, saturate(portraitUV) * _Cover.xy + _Cover.zw);
                float portraitAlpha = portrait.a * circle;
                float whiteAlpha = saturate(border + glow);
                float alpha = whiteAlpha + portraitAlpha * (1 - whiteAlpha);
                float3 premultiplied = whiteAlpha + portrait.rgb * portraitAlpha * (1 - whiteAlpha);
                return fixed4(premultiplied / max(alpha, 0.0001), alpha);
            }
            ENDCG
        }
    }
}
