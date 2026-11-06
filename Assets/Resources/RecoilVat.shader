Shader "Recoil/VAT Preview"
{
    Properties
    {
        _BaseMap("Albedo", 2D) = "white" {}
        _BaseColor("Color", Color) = (1,1,1,1)
        _Cutoff("Alpha cutoff", Range(0,1)) = 0
        [NoScaleOffset] _VatPositions("Positions", 2D) = "black" {}
        _VatSize("Texture width, rows per frame", Vector) = (1,1,0,0)
        _VatClip("Start, count, duration, loop", Vector) = (0,1,1,1)
        _VatPlayback("Start time, speed", Vector) = (0,1,0,0)
        _VatVertexMap("Use source vertex IDs", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Tags { "LightMode"="SRPDefaultUnlit" }
            Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            Texture2D<float4> _VatPositions;
            float _RecoilSimulationTime;
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST, _BaseColor, _VatSize;
                float _Cutoff, _VatVertexMap;
            CBUFFER_END
            UNITY_INSTANCING_BUFFER_START(Vat)
                UNITY_DEFINE_INSTANCED_PROP(float4, _VatClip)
                UNITY_DEFINE_INSTANCED_PROP(float4, _VatPlayback)
            UNITY_INSTANCING_BUFFER_END(Vat)
            struct Input { float3 position:POSITION; float2 uv:TEXCOORD0; float2 sourceVertex:TEXCOORD3; uint vertex:SV_VertexID; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Output { float4 position:SV_POSITION; float2 uv:TEXCOORD0; };
            float3 ReadPosition(uint vertex, uint frame)
            {
                uint width = (uint)_VatSize.x;
                return _VatPositions.Load(int3(vertex % width, vertex / width + frame * (uint)_VatSize.y, 0)).xyz;
            }
            Output Vert(Input input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float4 clip = UNITY_ACCESS_INSTANCED_PROP(Vat, _VatClip);
                float4 playback = UNITY_ACCESS_INSTANCED_PROP(Vat, _VatPlayback);
                uint vertex = _VatVertexMap > .5 ? (uint)round(input.sourceVertex.x) : input.vertex;
                float now = playback.z > .5 ? _RecoilSimulationTime : _Time.y;
                float t = max(0, now - playback.x) * playback.y / max(.001, clip.z);
                float frame = clip.w > .5 ? frac(t) * clip.y : saturate(t) * max(0, clip.y - 1);
                uint first = (uint)floor(frame);
                uint next = clip.w > .5 ? (first + 1) % (uint)clip.y : min(first + 1, (uint)clip.y - 1);
                float3 p = lerp(ReadPosition(vertex, (uint)clip.x + first),
                    ReadPosition(vertex, (uint)clip.x + next), frac(frame));
                Output output;
                output.position = TransformObjectToHClip(p);
                output.uv = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                return output;
            }
            half4 Frag(Output input):SV_Target
            {
                half4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                clip(color.a - _Cutoff);
                return color;
            }
            ENDHLSL
        }
    }
}
