Shader "Recoil/GPU Skinned Crowd"
{
    Properties
    {
        _BaseMap("Base map", 2D) = "white" {}
        _BaseColor("Color", Color) = (1,1,1,1)
        _Cutoff("Alpha cutoff", Range(0,1)) = 0.5
        _AlphaClip("Alpha clip", Float) = 0
        _Cull("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Cull [_Cull]
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Cutoff, _AlphaClip;
        CBUFFER_END
        StructuredBuffer<float4x4> _SkinBones;
        UNITY_INSTANCING_BUFFER_START(SkinInstances)
            UNITY_DEFINE_INSTANCED_PROP(float, _BoneOffset)
        UNITY_INSTANCING_BUFFER_END(SkinInstances)
        struct Attributes
        {
            float3 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
            float4 boneIndices : TEXCOORD3;
            float4 boneWeights : TEXCOORD4;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        void Skin(Attributes input, out float3 positionOS, out float3 normalOS)
        {
            float boneOffset = UNITY_ACCESS_INSTANCED_PROP(SkinInstances, _BoneOffset);
            if (boneOffset < 0)
            {
                positionOS = input.positionOS;
                normalOS = input.normalOS;
                return;
            }
            uint offset = (uint)boneOffset;
            float4x4 skin = _SkinBones[offset + (uint)input.boneIndices.x] * input.boneWeights.x
                + _SkinBones[offset + (uint)input.boneIndices.y] * input.boneWeights.y
                + _SkinBones[offset + (uint)input.boneIndices.z] * input.boneWeights.z
                + _SkinBones[offset + (uint)input.boneIndices.w] * input.boneWeights.w;
            positionOS = mul(skin, float4(input.positionOS, 1)).xyz;
            normalOS = normalize(mul((float3x3)skin, input.normalOS));
        }
        half4 Surface(float2 uv)
        {
            half4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv) * _BaseColor;
            if (_AlphaClip > 0.5) clip(color.a - _Cutoff);
            return color;
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma instancing_options forcemaxcount:256
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                half fog : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                float3 p, n; Skin(input, p, n);
                output.positionWS = TransformObjectToWorld(p);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(n);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fog = ComputeFogFactor(output.positionCS.z);
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half4 color = Surface(input.uv);
                half3 n = normalize(input.normalWS);
                Light main = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                half3 lighting = SampleSH(n) + main.color * saturate(dot(n, main.direction))
                    * main.distanceAttenuation * main.shadowAttenuation;
                color.rgb = MixFog(color.rgb * lighting, input.fog);
                return color;
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask R
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            #pragma instancing_options forcemaxcount:256
            struct DepthVaryings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            DepthVaryings DepthVert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 p, n; Skin(input, p, n);
                DepthVaryings output;
                output.positionCS = TransformObjectToHClip(p);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }
            half4 DepthFrag(DepthVaryings input) : SV_Target { Surface(input.uv); return 0; }
            ENDHLSL
        }
    }
}
