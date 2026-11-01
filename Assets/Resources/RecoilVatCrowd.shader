// GPU-instanced crowd shader: positions AND normals come from baked vertex-animation-textures
// instead of SkinnedMeshRenderer skinning, so lighting follows the animation (a bind-pose-only
// normal would shade a mid-swing soldier as if it were still standing still). Unity caps a single
// texture dimension at 16384, which a high-poly character's vertex count alone can exceed, so
// vertices tile across _TextureWidth columns and _RowsPerFrame rows per baked frame instead of one
// row per frame (vertex order matches RecoilVatLibrary.BakedMesh exactly). Per-instance
// _RowPhase.x selects the frame index; _RowPhase.y, when >= 0, is seconds-since-death and
// reproduces the same sink effect RecoilCharacterPresentation applies to Animator-driven soldiers
// (ClampDeathAnimatorRootHeight), so a soldier looks identical whether it's instanced or fell back
// to the Animator path.
Shader "Recoil/VAT Crowd"
{
    Properties
    {
        _PositionTex ("Baked Positions", 2D) = "black" {}
        _NormalTex ("Baked Normals", 2D) = "black" {}
        _VertexCount ("Vertex Count", Float) = 1
        _TextureWidth ("Texture Width", Float) = 1
        _RowsPerFrame ("Rows Per Frame", Float) = 1
        _MainTex ("Albedo", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _DeathSinkDelay ("Death Sink Delay", Float) = 1.0
        _DeathSinkDuration ("Death Sink Duration", Float) = 1.5
        _DeathSinkDistance ("Death Sink Distance", Float) = 1.5
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Name "VatCrowdForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_PositionTex);
            SAMPLER(sampler_PositionTex);
            TEXTURE2D(_NormalTex);
            SAMPLER(sampler_NormalTex);
            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
                float _VertexCount;
                float _TextureWidth;
                float _RowsPerFrame;
                float _DeathSinkDelay;
                float _DeathSinkDuration;
                float _DeathSinkDistance;
            CBUFFER_END

            UNITY_INSTANCING_BUFFER_START(Props)
                // x: baked frame index (tiled into _PositionTex via _TextureWidth/_RowsPerFrame).
                // y: seconds since death, or < 0 if alive.
                UNITY_DEFINE_INSTANCED_PROP(float4, _RowPhase)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct Attributes
            {
                // normalOS is not read: normals come from _NormalTex per baked frame instead of the
                // mesh's static (bind-pose) attribute, so lighting follows the animation.
                float2 uv : TEXCOORD0;
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                float4 rowPhase = UNITY_ACCESS_INSTANCED_PROP(Props, _RowPhase);
                uint vertexIndex = min(input.vertexID, (uint)_VertexCount - 1);
                uint texWidth = max(1u, (uint)_TextureWidth);
                uint localRow = vertexIndex / texWidth;
                uint localCol = vertexIndex % texWidth;
                uint frameIndex = (uint)rowPhase.x;
                uint2 texel = uint2(localCol, frameIndex * (uint)_RowsPerFrame + localRow);
                float3 positionOS = LOAD_TEXTURE2D_LOD(_PositionTex, texel, 0).xyz;
                float3 normalOS = normalize(LOAD_TEXTURE2D_LOD(_NormalTex, texel, 0).xyz);

                // Mirrors RecoilCharacterPresentation.ClampDeathAnimatorRootHeight's sink math,
                // applied here instead of to a Transform since instanced soldiers have none.
                if (rowPhase.y >= 0.0)
                {
                    float sinkProgress = saturate((rowPhase.y - _DeathSinkDelay) / _DeathSinkDuration);
                    positionOS.y -= _DeathSinkDistance * sinkProgress;
                }

                output.positionHCS = TransformObjectToHClip(positionOS);
                output.normalWS = TransformObjectToWorldNormal(normalOS);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half4 albedo = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv) * _Color;
                Light light = GetMainLight();
                half ndotl = saturate(dot(normalize(input.normalWS), light.direction));
                half3 lit = albedo.rgb * (light.color * (ndotl * 0.8 + 0.2));
                return half4(lit, albedo.a);
            }
            ENDHLSL
        }
    }
}
