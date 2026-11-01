// Benchmark-only rendering: one flat procedural circle per soldier, no texture sampling, no
// animation. Used to find the simulation layer's own ceiling (pathfinding/combat/ECS) by removing
// rendering cost (VAT or Animator+SkinnedMeshRenderer) from the equation almost entirely.
Shader "Recoil/Dot Crowd"
{
    Properties
    {
        _BlueColor ("Blue Team Color", Color) = (0.25, 0.5, 1, 1)
        _RedColor ("Red Team Color", Color) = (1, 0.3, 0.25, 1)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Name "DotCrowdForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BlueColor;
                float4 _RedColor;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(Props)
                // x: 0 = blue, 1 = red.
                UNITY_DEFINE_INSTANCED_PROP(float4, _Team)
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
                // Procedural circle: discard corners outside radius 0.5 from quad center.
                float dist = length(input.uv - 0.5);
                clip(0.5 - dist);
                float4 team = UNITY_ACCESS_INSTANCED_PROP(Props, _Team);
                half4 color = team.x < 0.5 ? _BlueColor : _RedColor;
                return color;
            }
            ENDHLSL
        }
    }
}
