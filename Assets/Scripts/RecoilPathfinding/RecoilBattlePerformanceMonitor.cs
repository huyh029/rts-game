using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace RecoilPathfinding
{
    public sealed class RecoilBattlePerformanceMonitor : MonoBehaviour
    {
        [SerializeField] private bool scanSceneRendererCounts = false;
        [SerializeField] private bool focusCpuBottlenecks = true;
        private float _nextCpuPanelRefresh;
        private string _cpuHeader = "";
        private string _movementPanel = "";
        private string _battlePanel = "";
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
        private RecoilNavMeshPathService _pathService;
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
            if (_pathService == null)
                _pathService = FindFirstObjectByType<RecoilNavMeshPathService>();
            _unityAllocatedMemory = Profiler.GetTotalAllocatedMemoryLong();
            _unityReservedMemory = Profiler.GetTotalReservedMemoryLong();
            _monoUsedMemory = Profiler.GetMonoUsedSizeLong();
            _monoHeapMemory = Profiler.GetMonoHeapSizeLong();

            if (_battleDemo == null)
                _battleDemo = FindFirstObjectByType<RecoilAutoBattleDemo>();
            _soldierCount = _battleDemo != null ? _battleDemo.AliveSoldierCount :
                FindObjectsByType<RecoilNavMeshPathAgent>(FindObjectsSortMode.None).Length;
            _rendererCount = -1;
            _meshRendererCount = -1;
            if (scanSceneRendererCounts)
            {
                var renderers = FindObjectsByType<Renderer>(FindObjectsSortMode.None);
                _rendererCount = 0;
                _meshRendererCount = 0;
                for (var i = 0; i < renderers.Length; i++)
                {
                    if (!renderers[i].enabled || !renderers[i].gameObject.activeInHierarchy) continue;
                    _rendererCount++;
                    if (renderers[i] is MeshRenderer) _meshRendererCount++;
                }
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
            if (focusCpuBottlenecks)
            {
                DrawCpuBottlenecks();
                return;
            }
            GUI.Box(new Rect(410f, 96f, 330f, 204f), "CPU phases - last samples (ms)");
            GUI.Label(new Rect(420f, 120f, 310f, 20f),
                $"Movement total*: {(_pathService != null ? _pathService.LastMovementScheduleMilliseconds : 0f):F2}");
            GUI.Label(new Rect(420f, 140f, 310f, 20f),
                $"Movement wait: {(_pathService != null ? _pathService.LastMovementWaitMilliseconds : 0f):F2}");
            GUI.Label(new Rect(420f, 160f, 310f, 20f),
                $"Movement apply/fallback: {(_pathService != null ? _pathService.LastMovementApplyMilliseconds : 0f):F2}");
            GUI.Label(new Rect(420f, 180f, 310f, 20f),
                $"Transform sync: {RecoilUnitPresentationSync.LastSyncMilliseconds:F2}");
            GUI.Label(new Rect(420f, 200f, 310f, 20f),
                $"Combat resolve: {(_battleDemo != null ? _battleDemo.LastCombatResolveMilliseconds : 0f):F2}");
            GUI.Label(new Rect(420f, 224f, 310f, 28f), "* Includes wait/apply; do not sum rows.");
            GUI.Label(new Rect(420f, 252f, 310f, 20f), $"30 Hz | Tick {RecoilSimulationClock.CurrentTick} | Steps/frame {RecoilSimulationClock.LastTicksThisFrame}");
            GUI.Label(new Rect(420f, 272f, 310f, 20f), $"Overload dropped time: {RecoilSimulationClock.DroppedSeconds:F2}s");
            const float panelWidth = 390f;
            const float panelHeight = 464f;
            GUI.Box(new Rect(8f, 96f, panelWidth, panelHeight), "Battle Performance");
            GUI.Label(new Rect(18f, 120f, panelWidth - 20f, 20f),
                $"FPS: {_fps:F1}   Frame: {Time.unscaledDeltaTime * 1000f:F1} ms");
            GUI.Label(new Rect(18f, 140f, panelWidth - 20f, 20f),
                $"CPU frame: {FormatTiming(_cpuFrameTimeMs)}   GPU frame: {FormatTiming(_gpuFrameTimeMs)}");
            GUI.Label(new Rect(18f, 160f, panelWidth - 20f, 20f),
                $"Soldiers: {_soldierCount}   Renderers: {(_rendererCount >= 0 ? _rendererCount.ToString() : "scan off")}   Mesh: {(_meshRendererCount >= 0 ? _meshRendererCount.ToString() : "-")}");
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
                "Dead-unit reuse pool: disabled");
            GUI.Label(new Rect(18f, 360f, panelWidth - 20f, 20f),
                $"ECS soldier entities: {(_battleDemo != null ? _battleDemo.EcsSoldierEntityCount : 0)}");
            GUI.Label(new Rect(18f, 380f, panelWidth - 20f, 20f),
                $"Surface fast/fallback: {(_pathService != null ? _pathService.LastSurfaceFastCount : 0)} / {(_pathService != null ? _pathService.LastSurfaceFallbackCount : 0)}");
            GUI.Label(new Rect(18f, 400f, panelWidth - 20f, 20f),
                $"Paths total funnel/fallback/direct: {(_pathService != null ? _pathService.FunnelPathCount : 0)} / {(_pathService != null ? _pathService.MidpointFallbackCount : 0)} / {(_pathService != null ? _pathService.DirectPathCount : 0)}");
            GUI.Label(new Rect(18f, 420f, panelWidth - 20f, 20f),
                $"Shared-edge crossings/step: {(_pathService != null ? _pathService.LastPortalCrossingCount : 0)}");
            GUI.Label(new Rect(18f, 440f, panelWidth - 20f, 20f),
                $"Combat snapshot/grid: {(_battleDemo != null ? _battleDemo.LastSnapshotBuildMilliseconds : 0f):F2} ms");
            GUI.Label(new Rect(18f, 460f, panelWidth - 20f, 20f),
                $"Combat grid removals/adds: {(_battleDemo != null ? _battleDemo.LastCombatGridMutations : 0)}");
            GUI.Label(new Rect(18f, 480f, panelWidth - 20f, 20f),
                $"Crowd grid removals/adds: {(_pathService != null ? _pathService.LastCrowdGridMutations : 0)}");
            GUI.Label(new Rect(18f, 500f, panelWidth - 20f, 20f),
                $"Combat roster rebuilds: {(_battleDemo != null ? _battleDemo.CombatRosterBuildCount : 0)}");
            GUI.Label(new Rect(18f, 520f, panelWidth - 20f, 20f),
                $"Roster lifecycle/prune: {(_battleDemo != null ? _battleDemo.LastRosterPruneMilliseconds : 0f):F2} ms");
            GUI.Label(new Rect(18f, 540f, panelWidth - 20f, 20f),
                $"Movement roster rebuilds: {(_pathService != null ? _pathService.MovementRosterBuildCount : 0)}");
        }

        private void DrawCpuBottlenecks()
        {
            if (Time.unscaledTime >= _nextCpuPanelRefresh || _cpuHeader.Length == 0)
            {
                _nextCpuPanelRefresh = Time.unscaledTime + 0.25f;
                var movement = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.Movement);
                var prepare = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.PrepareMovement);
                var wait = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.WaitMovement);
                var apply = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.ApplyMovement);
                var fallback = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.FallbackMovement);
                var battle = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.Battle);
                var life = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.Lifecycle);
                var departures = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.Departures);
                var schedule = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.ScheduleCombat);
                var snapshot = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.SnapshotCombat);
                var resolve = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.ResolveCombat);
                var retire = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.RetireCombat);
                _cpuHeader = $"FPS {_fps:F1} | CPU {FormatTiming(_cpuFrameTimeMs)} | Steps/frame {RecoilSimulationClock.LastTicksThisFrame} | Tick {RecoilSimulationClock.CurrentTick}";
                _movementPanel = $"RecoilTick.Movement: {movement:F2} ms/frame\n" +
                    $"Prepare / schedule: {prepare:F2}\nWait for jobs: {wait:F2}\nApply ECS chunks: {apply:F2}\nSurface fallback: {fallback:F2}\n" +
                    $"   Read ECS: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.FallbackRead):F2}\n" +
                    $"   Surface queries: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.FallbackProjection):F2}\n" +
                    $"   Write ECS: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.FallbackWrite):F2}\n" +
                    $"   Advance waypoint: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.FallbackCorner):F2}\n" +
                    $"Other: {System.Math.Max(0, movement - prepare - wait - apply - fallback):F2}\n" +
                    $"Fast/fallback last step: {(_pathService != null ? _pathService.LastSurfaceFastCount : 0)} / {(_pathService != null ? _pathService.LastSurfaceFallbackCount : 0)}\n" +
                    $"Backend: {(_pathService != null ? _pathService.SurfaceBackend : "-")}\n" +
                    $"Worker recovered/held (last step): {(_pathService != null ? _pathService.LastTerrainRecoveryCount : 0)} / {(_pathService != null ? _pathService.LastTerrainHeldCount : 0)}";
                _battlePanel = $"RecoilTick.Battle: {battle:F2} ms/frame\n" +
                    $"Lifecycle: {life:F2} | Departures: {departures:F2}\nSchedule combat: {schedule:F2}\n   Snapshot/grid (included): {snapshot:F2}\n" +
                    $"Resolve damage/navigation: {resolve:F2}\nRetire/death cleanup: {retire:F2}\n" +
                    $"Other: {System.Math.Max(0, battle - life - departures - schedule - resolve - retire):F2}\n" +
                    $"GPU slots/draws: {(_instancedRenderer != null ? _instancedRenderer.GpuImpostorCount : 0)} / {(_instancedRenderer != null ? _instancedRenderer.GpuIndirectDrawCalls : 0)}\n" +
                    $"Path project/delivery ms: {(_pathService != null ? _pathService.LastProjectionMilliseconds : 0f):F2} / {(_pathService != null ? _pathService.LastPathDeliveryMilliseconds : 0f):F2}\n" +
                    $"Paths done/ready/cancelled: {(_pathService != null ? _pathService.LastPathCompletions : 0)} / {(_pathService != null ? _pathService.PendingPathCompletions : 0)} / {(_pathService != null ? _pathService.LastCancelledPaths : 0)}\n" +
                    $"Field buffers new/reused: {(_pathService != null ? _pathService.FlowFieldAllocationCount : 0)} / {(_pathService != null ? _pathService.FlowFieldReuseCount : 0)}\n" +
                    $"Shared corridor full/suffix: {(_pathService != null ? _pathService.SharedCorridorHits : 0)} / {(_pathService != null ? _pathService.SharedCorridorSuffixHits : 0)}";
            }
            var width = Mathf.Min(440f, Mathf.Max(240f, Screen.width - 16f));
            var sideBySide = Screen.width >= 910f;
            var movementRect = new Rect(8f, 122f, width, 275f);
            var battleRect = new Rect(sideBySide ? 458f : 8f, sideBySide ? 122f : 404f, width, 265f);
            GUI.Box(new Rect(8f, 96f, Mathf.Min(Screen.width - 16f, sideBySide ? 890f : width), 24f), _cpuHeader);
            GUI.Box(movementRect, GUIContent.none);
            GUI.Box(battleRect, GUIContent.none);
            GUI.Label(new Rect(movementRect.x + 10f, movementRect.y + 8f, width - 20f, 255f), _movementPanel);
            GUI.Label(new Rect(battleRect.x + 10f, battleRect.y + 8f, width - 20f, 245f), _battlePanel);
            GUI.Label(new Rect(8f, Mathf.Max(movementRect.yMax, battleRect.yMax) + 4f, Mathf.Min(Screen.width - 16f, 890f), 40f),
                "Main-thread ms, all ticks/frame. Indented rows are included in their parent. Display refresh: 4 Hz.");
        }

        private static string FormatTiming(float milliseconds) =>
            milliseconds > 0f ? $"{milliseconds:F1} ms" : "not available";

        private static string FormatMemory(long bytes) =>
            $"{bytes / (1024f * 1024f):F0} MB";
    }
}
