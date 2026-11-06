Shader "Recoil/Upper Hemisphere Impostor"
{
    Properties
    {
        _BaseMap("Atlas", 2D) = "white" {}
        _Diameter("Capture diameter", Float) = 3
        _FootHeight("Capture center above feet", Float) = 0
        _ColorGain("Color brightness", Range(0,2)) = 1
        _Saturation("Color saturation", Range(0,2)) = 1
        _BakeVersion("Bake version", Float) = 6
        _ViewOffsetSteps("Azimuth correction (45 degree steps)", Float) = 2
        _AnimationFrames("Frames per animation", Float) = 4
        _ImpostorFrame("Animation frame", Float) = -1
        _Cutoff("Alpha cutoff", Range(0,1)) = 0.2
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        Cull Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        CBUFFER_START(UnityPerMaterial)
            float _Diameter, _FootHeight, _ColorGain, _Saturation, _BakeVersion, _ViewOffsetSteps, _AnimationFrames;
            half _Cutoff;
        CBUFFER_END
        UNITY_INSTANCING_BUFFER_START(ImpostorInstances)
            UNITY_DEFINE_INSTANCED_PROP(float, _ImpostorFrame)
        UNITY_INSTANCING_BUFFER_END(ImpostorInstances)
        struct Attributes { float3 positionOS : POSITION; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; half fog : TEXCOORD1; };
        Varyings Vert(Attributes input)
        {
            UNITY_SETUP_INSTANCE_ID(input);
            Varyings output;
            float3 center = TransformObjectToWorld(float3(0,0,0));
            float3 toCamera = unity_OrthoParams.w > 0.5 ? -GetViewForwardDir() : normalize(_WorldSpaceCameraPos - center);
            float3 viewLocal = normalize(mul((float3x3)unity_WorldToObject, toCamera));
            float azimuth = dot(viewLocal.xz, viewLocal.xz) < 0.000001 ? 0.0 : atan2(viewLocal.x, viewLocal.z);
            // Calibrate each prefab independently; blue and red assets can have different forward axes.
            float column = fmod(floor(azimuth * (8.0 / TWO_PI) + 0.5) + 8.0 + _ViewOffsetSteps, 8.0);
            float elevation = degrees(asin(clamp(viewLocal.y, 0.0, 1.0)));
            float row = elevation < 40.0 ? 0.0 : elevation < 60.0 ? 1.0 : elevation < 80.0 ? 2.0 : 3.0;
            // Roll-stable camera-facing card, matching the bake camera's world-up orientation.
            float3 right = cross(toCamera, float3(0,1,0));
            right = dot(right, right) < 0.000001 ? -normalize(unity_ObjectToWorld._m00_m10_m20) : normalize(right);
            float3 up = normalize(cross(right, toCamera));
            float scale = max(length(unity_ObjectToWorld._m00_m10_m20),
                max(length(unity_ObjectToWorld._m01_m11_m21), length(unity_ObjectToWorld._m02_m12_m22)));
            float captureElevation = row == 0.0 ? 30.0 : row == 1.0 ? 50.0 : row == 2.0 ? 70.0 : 90.0;
            // Anchor the captured foot projection to its actual world-space ground point.
            // Keeping the card centered in mid-air makes feet float when the camera's
            // elevation differs from the discrete captured elevation.
            float3 groundAnchor = center - float3(0, _FootHeight * scale, 0);
            float3 cardCenter = groundAnchor + up * (_FootHeight * scale * cos(radians(captureElevation)));
            float3 world = cardCenter + (right * input.positionOS.x + up * input.positionOS.y) * _Diameter * scale;
            output.positionCS = TransformWorldToHClip(world);
            // Two-pixel gutter in each 128px capture tile.
            float frameCount = max(1.0, _AnimationFrames);
            float animationFrame = UNITY_ACCESS_INSTANCED_PROP(ImpostorInstances, _ImpostorFrame);
            if (animationFrame < 0.0) animationFrame = fmod(floor(_Time.y * frameCount), frameCount);
            row += clamp(floor(animationFrame), 0.0, frameCount * 3.0 - 1.0) * 4.0;
            output.uv = (float2(column, row) + (2.0 + input.uv * 124.0) / 128.0) / float2(8,4 * frameCount * 3);
            output.fog = ComputeFogFactor(output.positionCS.z);
            return output;
        }
        half4 Surface(Varyings input)
        {
            half4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
            clip(color.a - _Cutoff);
            half luminance = dot(color.rgb, half3(0.2126, 0.7152, 0.0722));
            color.rgb = max(0, lerp(luminance.xxx, color.rgb, _Saturation)) * _ColorGain;
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
            half4 Frag(Varyings input) : SV_Target
            {
                half4 color = Surface(input);
                color.rgb = MixFog(color.rgb, input.fog);
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
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma instancing_options forcemaxcount:256
            half4 Frag(Varyings input) : SV_Target { Surface(input); return 0; }
            ENDHLSL
        }
    }
}
