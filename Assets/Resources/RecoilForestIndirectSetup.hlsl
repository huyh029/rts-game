#ifndef RECOIL_FOREST_INDIRECT_SETUP_INCLUDED
#define RECOIL_FOREST_INDIRECT_SETUP_INCLUDED
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
StructuredBuffer<float4x4> _RecoilForestObjectToWorld;
StructuredBuffer<float4x4> _RecoilForestWorldToObject;
StructuredBuffer<uint> _RecoilForestVisibleIndices;
#endif
void RecoilForestSetup()
{
    #if defined(UNITY_PROCEDURAL_INSTANCING_ENABLED)
        uint index = _RecoilForestVisibleIndices[unity_InstanceID];
        unity_ObjectToWorld = _RecoilForestObjectToWorld[index];
        unity_WorldToObject = _RecoilForestWorldToObject[index];
    #endif
}
#endif
