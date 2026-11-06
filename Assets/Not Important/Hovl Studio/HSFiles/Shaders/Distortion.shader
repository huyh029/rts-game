Shader "Hovl/Particles/Distortion"
{
    Properties
    {
        [Normal] _NormalMap("Normal Map", 2D) = "bump" {}
        _Distortionpower("Distortion power", Float) = 0.05
        _InvFade("Soft Particles Factor", Range(0.01, 3.0)) = 1.0
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass
        {
            Name "Distortion"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ SOFTPARTICLES_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _NormalMap_ST;
                float _Distortionpower;
                float _InvFade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 depthAndDistance : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.uv = TRANSFORM_TEX(input.uv, _NormalMap);
                output.color = input.color;
                output.depthAndDistance = float2(-position.positionVS.z,
                    max(distance(GetCameraPositionWS(), position.positionWS), 0.0001));
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 screenUV = GetNormalizedScreenSpaceUV(input.positionCS);
                half alpha = input.color.a;
                #if defined(SOFTPARTICLES_ON)
                    float rawDepth = SampleSceneDepth(screenUV);
                    float sceneDepth = LinearEyeDepth(rawDepth, _ZBufferParams);
                    // Orthographic depth is linear, unlike perspective depth.
                    if (unity_OrthoParams.w > 0.5)
                    {
                        #if UNITY_REVERSED_Z
                            rawDepth = 1.0 - rawDepth;
                        #endif
                        sceneDepth = lerp(_ProjectionParams.y, _ProjectionParams.z, rawDepth);
                    }
                    alpha *= saturate(_InvFade * (sceneDepth - input.depthAndDistance.x));
                #endif

                half3 normal = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, input.uv));
                half mask = saturate(abs(normal.r) + abs(normal.g) * 30.0 - 0.03);
                // Keep the original pixel-sized, distance-attenuated distortion.
                float2 offset = normal.rg * _CameraOpaqueTexture_TexelSize.xy
                    * _Distortionpower * alpha / input.depthAndDistance.y;
                return half4(SampleSceneColor(saturate(screenUV + offset)), mask * alpha);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
