// Instanced crowd impostor: one quad per distant soldier, sampled from a baked sprite atlas
// (columns: views, rows: animation frames). Alpha-tested with depth writes so thousands of
// overlapping sprites need no sorting.
Shader "Recoil/Impostor Crowd"
{
    Properties
    {
        _MainTex ("Sprite Atlas", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ViewCount ("View Count", Float) = 16
        _FrameCount ("Frame Count", Float) = 8
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
            "IgnoreProjector" = "True"
        }
        Pass
        {
            Name "ImpostorCrowd"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _MainTex_TexelSize;
                float4 _Color;
                float _ViewCount;
                float _FrameCount;
                float _Cutoff;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(Props)
                // x: frame row, y: view column.
                UNITY_DEFINE_INSTANCED_PROP(float4, _FrameView)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionHCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float4 frameView = UNITY_ACCESS_INSTANCED_PROP(Props, _FrameView);
                // Keep bilinear taps inside the cell so neighbouring frames never bleed in.
                float2 cellTexel = _MainTex_TexelSize.xy * float2(_ViewCount, _FrameCount);
                float2 localUv = clamp(input.uv, 0.5 * cellTexel, 1.0 - 0.5 * cellTexel);
                float2 atlasUv = float2((localUv.x + frameView.y) / _ViewCount, (localUv.y + frameView.x) / _FrameCount);
                half4 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, atlasUv) * _Color;
                clip(color.a - _Cutoff);
                return half4(color.rgb, 1.0);
            }
            ENDHLSL
        }
    }
}
