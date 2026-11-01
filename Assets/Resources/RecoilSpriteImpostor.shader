Shader "Recoil/Animated Sprite Impostor"
{
    Properties
    {
        _MainTex ("Sprite Atlas", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _ViewCount ("View Count", Float) = 16
        _FrameCount ("Frame Count", Float) = 8
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
        }
        Pass
        {
            Name "SpriteImpostor"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
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
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(Props)
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
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float4 frameView = UNITY_ACCESS_INSTANCED_PROP(Props, _FrameView);
                float2 localUv = clamp(input.uv, 0.5 * _MainTex_TexelSize.xy,
                    1.0 - 0.5 * _MainTex_TexelSize.xy);
                float2 atlasUv = float2(
                    (localUv.x + frameView.y) / _ViewCount,
                    (localUv.y + frameView.x) / _FrameCount);
                return SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, atlasUv) * _Color;
            }
            ENDHLSL
        }
    }
}
