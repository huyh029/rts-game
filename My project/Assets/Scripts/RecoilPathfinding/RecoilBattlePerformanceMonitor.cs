using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace RecoilPathfinding
{
    public sealed class RecoilBattlePerformanceMonitor : MonoBehaviour
    {
        private static readonly ProfilerMarker UpdateMarker =
            new ProfilerMarker("RecoilBattlePerformanceMonitor.Update");

        private readonly FrameTiming[] _frameTimings = new FrameTiming[1];
        private float _fps;
        private float _cpuFrameTimeMs;
        private float _gpuFrameTimeMs;
        private float _nextObjectCountTime;
        private int _soldierCount;
        private int _rendererCount;
        private int _meshRendererCount;
        private int _instancedDrawCallCount;
        private int _visibleInstancedSoldiers;
        private int _culledInstancedSoldiers;
        private int _targetCandidateChecks;
        private int _combatUpdates;
        private RecoilAutoBattleDemo _battleDemo;
        private RecoilInstancedSoldierRenderer _instancedRenderer;
        private long _unityAllocatedMemory;
        private long _unityReservedMemory;
        private long _monoUsedMemory;
        private long _monoHeapMemory;

        private void Update()
        {
            using (UpdateMarker.Auto())
            {
                var deltaTime = Time.unscaledDeltaTime;
                if (deltaTime > 0f)
                    _fps = Mathf.Lerp(_fps, 1f / deltaTime, 0.1f);

                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _frameTimings) > 0)
                {
                    var timing = _frameTimings[0];
                    if (timing.cpuFrameTime > 0d)
                        _cpuFrameTimeMs = (float)timing.cpuFrameTime;
                    if (timing.gpuFrameTime > 0d)
                        _gpuFrameTimeMs = (float)timing.gpuFrameTime;
                }

                if (_battleDemo != null)
                {
                    _targetCandidateChecks = _battleDemo.TargetCandidateChecksLastFrame;
                    _combatUpdates = _battleDemo.CombatUpdatesLastFrame;
                }

                if (Time.unscaledTime >= _nextObjectCountTime)
                {
                    _nextObjectCountTime = Time.unscaledTime + 1f;
                    RefreshObjectCounts();
                }
            }
        }

        private void RefreshObjectCounts()
        {
            _unityAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong();
            _unityReservedMemory = Profiler.GetTotalReservedMemoryLong();
            _monoUsedMemory = Profiler.GetMonoUsedSizeLong();
            _monoHeapMemory = Profiler.GetMonoHeapSizeLong();

            var agents = FindObjectsByType<RecoilNavMeshPathAgent>(FindObjectsSortMode.None);
            _soldierCount = agents.Length;

            var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
            _rendererCount = 0;
            _meshRendererCount = 0;
            for (var i = 0; i < renderers.Length; i++)
            {
                if (!renderers[i].enabled || !renderers[i].gameObject.activeInHierarchy)
                    continue;

                _rendererCount++;
                if (renderers[i] is MeshRenderer)
                    _meshRendererCount++;
            }

            if (_instancedRenderer == null)
                _instancedRenderer = FindFirstObjectByType<RecoilInstancedSoldierRenderer>();
            _instancedDrawCallCount = _instancedRenderer != null
                ? _instancedRenderer.LastDrawCallCount
                : 0;
            _visibleInstancedSoldiers = _instancedRenderer != null
                ? _instancedRenderer.LastVisibleSoldierCount
                : 0;
            _culledInstancedSoldiers = _instancedRenderer != null
                ? _instancedRenderer.LastCulledSoldierCount
                : 0;
            if (_battleDemo == null)
                _battleDemo = FindFirstObjectByType<RecoilAutoBattleDemo>();
        }

        private void OnGUI()
        {
            const float panelWidth = 390f;
            const float panelHeight = 284f;
            GUI.Box(new Rect(8f, 96f, panelWidth, panelHeight), "Battle Performance");
            GUI.Label(new Rect(18f, 120f, panelWidth - 20f, 20f),
                $"FPS: {_fps:F1}   Frame: {Time.unscaledDeltaTime * 1000f:F1} ms");
            GUI.Label(new Rect(18f, 140f, panelWidth - 20f, 20f),
                $"CPU frame: {FormatTiming(_cpuFrameTimeMs)}   GPU frame: {FormatTiming(_gpuFrameTimeMs)}");
            GUI.Label(new Rect(18f, 160f, panelWidth - 20f, 20f),
                $"Soldiers: {_soldierCount}   Visible renderers: {_rendererCount}   Mesh: {_meshRendererCount}");
            GUI.Label(new Rect(18f, 180f, panelWidth - 20f, 20f),
                $"Instanced draws/frame: {_instancedDrawCallCount}   GPU: {SystemInfo.graphicsDeviceName}");
            GUI.Label(new Rect(18f, 200f, panelWidth - 20f, 20f),
                $"Unity RAM allocated/reserved: {FormatMemory(_unityAllocatedMemory)} / {FormatMemory(_unityReservedMemory)}");
            GUI.Label(new Rect(18f, 220f, panelWidth - 20f, 20f),
                $"Mono used/heap: {FormatMemory(_monoUsedMemory)} / {FormatMemory(_monoHeapMemory)}");
            GUI.Label(new Rect(18f, 240f, panelWidth - 20f, 20f),
                $"GPU memory capacity: {SystemInfo.graphicsMemorySize} MB");
            GUI.Label(new Rect(18f, 260f, panelWidth - 20f, 20f),
                "Use Unity Profiler > GPU Usage to inspect GPU bottlenecks.");
            GUI.Label(new Rect(18f, 280f, panelWidth - 20f, 20f),
                $"Target grid candidate checks/frame: {_targetCandidateChecks}");
            GUI.Label(new Rect(18f, 300f, panelWidth - 20f, 20f),
                $"Combat unit updates/frame: {_combatUpdates}");
            GUI.Label(new Rect(18f, 320f, panelWidth - 20f, 20f),
                $"Instanced visible/culled soldiers: {_visibleInstancedSoldiers} / {_culledInstancedSoldiers}");
            GUI.Label(new Rect(18f, 340f, panelWidth - 20f, 20f),
                $"Pooled soldiers: {(_battleDemo != null ? _battleDemo.PooledSoldierCount : 0)}");
            GUI.Label(new Rect(18f, 360f, panelWidth - 20f, 20f),
                $"ECS soldier entities: {(_battleDemo != null ? _battleDemo.EcsSoldierEntityCount : 0)}");
        }

        private static string FormatTiming(float milliseconds) =>
            milliseconds > 0f ? $"{milliseconds:F1} ms" : "not available";

        private static string FormatMemory(long bytes) =>
            $"{bytes / (1024f * 1024f):F0} MB";
    }
}
