#ifndef RECOIL_GPU_IMPOSTOR_DATA
#define RECOIL_GPU_IMPOSTOR_DATA
struct RecoilGpuImpostorInstance
{
    float4x4 ObjectToWorld;
    float4x4 WorldToObject;
    float4 Playback;
    float4 Options;
    float4 Flags;
};
#endif
