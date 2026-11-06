using Unity.Profiling;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace RecoilPathfinding
{
    public sealed class RecoilBattlePerformanceMonitor : MonoBehaviour
    {
        [SerializeField] private bool scanSceneRendererCounts = false;
        [SerializeField] private bool focusCpuBottlenecks = true;
        [SerializeField, Min(5f)] private float benchmarkDurationSeconds = 60f;
        private float _nextCpuPanelRefresh;
        private string _cpuHeader = "";
        private string _movementPanel = "";
        private string _battlePanel = "";
        private static readonly RecoilCpuPhases.Marker UpdateMarker =
            new RecoilCpuPhases.Marker("RecoilBattlePerformanceMonitor.Update");
        private static readonly (string Name, RecoilCpuPhase Phase)[] BenchmarkPhaseMetrics =
        {
            ("movement_prepare_ms", RecoilCpuPhase.PrepareMovement),
            ("movement_wait_ms", RecoilCpuPhase.WaitMovement),
            ("movement_wait_grid_ms", RecoilCpuPhase.WaitMovementGrid),
            ("movement_wait_simulation_ms", RecoilCpuPhase.WaitMovementSimulation),
            ("movement_wait_collect_ms", RecoilCpuPhase.WaitMovementCollect),
            ("movement_apply_ms", RecoilCpuPhase.ApplyMovement),
            ("movement_fallback_ms", RecoilCpuPhase.FallbackMovement),
            ("combat_lifecycle_ms", RecoilCpuPhase.Lifecycle),
            ("combat_departures_ms", RecoilCpuPhase.Departures),
            ("combat_schedule_ms", RecoilCpuPhase.ScheduleCombat),
            ("combat_snapshot_grid_ms", RecoilCpuPhase.SnapshotCombat),
            ("combat_capture_ecs_ms", RecoilCpuPhase.CaptureCombat),
            ("combat_grid_update_ms", RecoilCpuPhase.GridCombat),
            ("combat_close_targeting_ms", RecoilCpuPhase.CloseCombat),
            ("combat_refresh_ecs_ms", RecoilCpuPhase.RefreshCombat),
            ("combat_resolve_ms", RecoilCpuPhase.ResolveCombat),
            ("combat_retire_ms", RecoilCpuPhase.RetireCombat),
            ("combat_roster_build_ms", RecoilCpuPhase.RosterBuild),
            ("combat_alive_capture_ms", RecoilCpuPhase.AliveCapture),
            ("combat_roster_compact_ms", RecoilCpuPhase.RosterCompact),
            ("combat_projectile_resolve_ms", RecoilCpuPhase.ProjectileResolve)
        };

        private readonly FrameTiming[] _frameTimings = new FrameTiming[1];
        private float _fps;
        private float _cpuFrameTimeMs;
        private bool _cpuFrameTimingValid;
        private float _gpuFrameTimeMs;
        private bool _gpuFrameTimingValid;
        private float _nextObjectCountTime;
        private int _soldierCount;
        private int _rendererCount;
        private int _meshRendererCount;
        private int _instancedDrawCallCount;
        private int _visibleInstancedSoldiers;
        private int _culledInstancedSoldiers;
        private int _fullDetailSoldierCount;
        private int _targetCandidateChecks;
        private int _combatUpdates;
        private RecoilAutoBattleDemo _battleDemo;
        private RecoilNavMeshPathService _pathService;
        private RecoilInstancedSoldierRenderer _instancedRenderer;
        private bool _searchedForInstancedRenderer;
        private long _unityAllocatedMemory;
        private long _unityReservedMemory;
        private long _monoUsedMemory;
        private long _monoHeapMemory;
        private StreamWriter _benchmarkWriter;
        private float _benchmarkStartedAt;
        private int _benchmarkFrames;
        private double _benchmarkFrameTimeSum;
        private float _benchmarkFrameTimeMax;
        private double _benchmarkCpuFrameSum;
        private double _benchmarkDroppedSeconds;
        private readonly List<float> _benchmarkFrameTimeSamples = new();
        private readonly List<float> _benchmarkCpuFrameSamples = new();

        private void Update()
        {
            if (!RecoilCpuPhases.Enabled) return;
            using (UpdateMarker.Auto())
            {
                var deltaTime = Time.unscaledDeltaTime;
                _cpuFrameTimingValid = false;
                _gpuFrameTimingValid = false;
                if (deltaTime > 0f)
                    _fps = Mathf.Lerp(_fps, 1f / deltaTime, 0.1f);

                if (Input.GetKeyDown(KeyCode.F9))
                {
                    if (_benchmarkWriter == null) StartBenchmark();
                    else FinishBenchmark("stopped by user");
                }

                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _frameTimings) > 0)
                {
                    var timing = _frameTimings[0];
                    if (timing.cpuFrameTime > 0d)
                    {
                        _cpuFrameTimeMs = (float)timing.cpuFrameTime;
                        _cpuFrameTimingValid = true;
                    }
                    if (timing.gpuFrameTime > 0d)
                    {
                        _gpuFrameTimeMs = (float)timing.gpuFrameTime;
                        _gpuFrameTimingValid = true;
                    }
                }

                if (Time.unscaledTime >= _nextObjectCountTime)
                {
                    _nextObjectCountTime = Time.unscaledTime + 1f;
                    RefreshObjectCounts();
                }
            }
        }

        private void LateUpdate()
        {
            if (!RecoilCpuPhases.Enabled) return;
            // RecoilSimulationClock runs in Update with execution order 100. Capture phase
            // timings after it has completed this rendered frame's fixed simulation ticks.
            if (_battleDemo != null)
            {
                _targetCandidateChecks = _battleDemo.TargetCandidateChecksLastFrame;
                _combatUpdates = _battleDemo.CombatUpdatesLastFrame;
            }
            CaptureBenchmarkFrame();
        }

        private void StartBenchmark()
        {
            try
            {
                var directory = Path.Combine(Application.persistentDataPath, "RecoilConversionBenchmarks");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, $"battle_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                _benchmarkWriter = new StreamWriter(path, false);
                var header = "scene,scene_path,platform,screen_width,screen_height,quality_level,target_frame_rate,vsync_count,elapsed_s,frame,ticks_this_frame,dropped_simulation_ms,blue_units,red_units,blue_castle_hp,red_castle_hp,fps,frame_ms,cpu_timing_valid,cpu_frame_ms,gpu_timing_valid,gpu_frame_ms,movement_ms,battle_ms,presentation_ms,combat_candidates,close_candidates,los_checks,los_blocked,strategic_los_checks,strategic_los_blocked,target_weight_evaluations,target_priority_skips,close_weight_evaluations,close_priority_skips,projectile_candidates,projectile_impacts,lead_solutions,lead_fallbacks,avoided_category_candidates,avoided_category_selection_events,path_completions,path_cancellations,memory_allocated_mb,target_search_requests,target_searches_performed,target_searches_deferred";
                for (var i = 0; i < BenchmarkPhaseMetrics.Length; i++)
                    header += "," + BenchmarkPhaseMetrics[i].Name;
                _benchmarkWriter.WriteLine(header);
                _benchmarkStartedAt = Time.unscaledTime;
                _benchmarkFrames = 0;
                _benchmarkFrameTimeSum = 0d;
                _benchmarkFrameTimeMax = 0f;
                _benchmarkCpuFrameSum = 0d;
                _benchmarkDroppedSeconds = RecoilSimulationClock.DroppedSeconds;
                _benchmarkFrameTimeSamples.Clear();
                _benchmarkCpuFrameSamples.Clear();
                Debug.Log($"Recoil benchmark started for {Mathf.Max(5f, benchmarkDurationSeconds):F0}s. CSV: {path}");
            }
            catch (Exception exception)
            {
                _benchmarkWriter?.Dispose();
                _benchmarkWriter = null;
                Debug.LogError($"Could not start Recoil benchmark: {exception.Message}");
            }
        }

        private void CaptureBenchmarkFrame()
        {
            if (_benchmarkWriter == null) return;
            var elapsed = Time.unscaledTime - _benchmarkStartedAt;
            var frameTime = Time.unscaledDeltaTime * 1000f;
            var cpu = _cpuFrameTimingValid ? _cpuFrameTimeMs : 0f;
            var gpu = _gpuFrameTimingValid ? _gpuFrameTimeMs : 0f;
            var battle = (float)RecoilCpuPhases.Milliseconds(RecoilCpuPhase.Battle);
            var movement = (float)RecoilCpuPhases.Milliseconds(RecoilCpuPhase.Movement);
            var blueUnits = _battleDemo != null ? _battleDemo.BlueSoldierCount : 0;
            var redUnits = _battleDemo != null ? _battleDemo.RedSoldierCount : 0;
            var blueCastleHp = _battleDemo != null ? _battleDemo.BlueCastleHealthRemaining : 0;
            var redCastleHp = _battleDemo != null ? _battleDemo.RedCastleHealthRemaining : 0;
            var pathCompletions = _pathService != null ? _pathService.LastPathCompletions : 0;
            var pathCancellations = _pathService != null ? _pathService.LastCancelledPaths : 0;
            var allocatedMb = Profiler.GetTotalAllocatedMemoryLong() / (1024d * 1024d);
            var activeScene = SceneManager.GetActiveScene();
            var sceneName = CsvEscape(activeScene.name);
            var scenePath = CsvEscape(activeScene.path);
            var totalDroppedSeconds = RecoilSimulationClock.DroppedSeconds;
            var droppedSimulationMs = Math.Max(0d, totalDroppedSeconds - _benchmarkDroppedSeconds) * 1000d;
            _benchmarkDroppedSeconds = totalDroppedSeconds;
            _benchmarkWriter.Write(FormattableString.Invariant(
                $"{sceneName},{scenePath},{Application.platform},{Screen.width},{Screen.height},{QualitySettings.GetQualityLevel()},{Application.targetFrameRate},{QualitySettings.vSyncCount},{elapsed:F3},{Time.frameCount},{RecoilSimulationClock.LastTicksThisFrame},{droppedSimulationMs:F3},{blueUnits},{redUnits},{blueCastleHp},{redCastleHp},{_fps:F2},{frameTime:F3},{(_cpuFrameTimingValid ? 1 : 0)},{cpu:F3},{(_gpuFrameTimingValid ? 1 : 0)},{gpu:F3},{movement:F3},{battle:F3},{_battleDemo?.PresentationTierUpdateMillisecondsLastFrame ?? 0f:F3},{_targetCandidateChecks},{_battleDemo?.CloseCandidateChecksLastFrame ?? 0},{_battleDemo?.LineOfFireChecksLastFrame ?? 0},{_battleDemo?.LineOfFireBlockedLastFrame ?? 0},{_battleDemo?.StrategicLineOfFireChecksLastFrame ?? 0},{_battleDemo?.StrategicLineOfFireBlockedLastFrame ?? 0},{_battleDemo?.TargetWeightEvaluationsLastFrame ?? 0},{_battleDemo?.TargetPriorityWeightSkipsLastFrame ?? 0},{_battleDemo?.CloseTargetWeightEvaluationsLastFrame ?? 0},{_battleDemo?.CloseTargetPriorityWeightSkipsLastFrame ?? 0},{_battleDemo?.ProjectileCandidateChecksLastFrame ?? 0},{_battleDemo?.ProjectileImpactsLastFrame ?? 0},{_battleDemo?.ProjectileLeadSolutionsLastFrame ?? 0},{_battleDemo?.ProjectileLeadFallbacksLastFrame ?? 0},{_battleDemo?.AvoidedCategoryCandidatesLastFrame ?? 0},{_battleDemo?.AvoidedCategoryTargetsSelectedLastFrame ?? 0},{pathCompletions},{pathCancellations},{allocatedMb:F2},{_battleDemo?.TargetSearchRequestsLastFrame ?? 0},{_battleDemo?.TargetSearchesPerformedLastFrame ?? 0},{_battleDemo?.TargetSearchesDeferredLastFrame ?? 0}"));
            for (var i = 0; i < BenchmarkPhaseMetrics.Length; i++)
            {
                _benchmarkWriter.Write(',');
                _benchmarkWriter.Write(RecoilCpuPhases.Milliseconds(BenchmarkPhaseMetrics[i].Phase)
                    .ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            }
            _benchmarkWriter.WriteLine();
            _benchmarkFrames++;
            _benchmarkFrameTimeSum += frameTime;
            _benchmarkFrameTimeMax = Mathf.Max(_benchmarkFrameTimeMax, frameTime);
            _benchmarkFrameTimeSamples.Add(frameTime);
            if (_cpuFrameTimingValid)
            {
                _benchmarkCpuFrameSum += cpu;
                _benchmarkCpuFrameSamples.Add(cpu);
            }

            if (elapsed >= Mathf.Max(5f, benchmarkDurationSeconds))
                FinishBenchmark("duration complete");
        }

        private void FinishBenchmark(string reason)
        {
            if (_benchmarkWriter == null) return;
            var path = _benchmarkWriter.BaseStream is FileStream file ? file.Name : "unknown";
            try
            {
                _benchmarkWriter.Flush();
                _benchmarkWriter.Dispose();
            }
            finally
            {
                _benchmarkWriter = null;
            }
            _benchmarkFrameTimeSamples.Sort();
            _benchmarkCpuFrameSamples.Sort();
            var average = _benchmarkFrames > 0 ? _benchmarkFrameTimeSum / _benchmarkFrames : 0d;
            var p95 = _benchmarkFrames > 0
                ? _benchmarkFrameTimeSamples[Mathf.Clamp(Mathf.CeilToInt(_benchmarkFrames * 0.95f) - 1, 0, _benchmarkFrames - 1)]
                : 0f;
            var averageCpu = _benchmarkCpuFrameSamples.Count > 0
                ? _benchmarkCpuFrameSum / _benchmarkCpuFrameSamples.Count : 0d;
            Debug.Log(FormattableString.Invariant(
                $"Recoil benchmark {reason}: frames={_benchmarkFrames}, frame avg/p95/max={average:F2}/{p95:F2}/{_benchmarkFrameTimeMax:F2} ms, CPU timing avg={averageCpu:F2} ms ({_benchmarkCpuFrameSamples.Count} samples). CSV: {path}"));
        }

        private void OnDisable()
        {
            if (_benchmarkWriter != null) FinishBenchmark("monitor disabled");
        }

        private static string CsvEscape(string value) =>
            "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

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
            _fullDetailSoldierCount = _battleDemo != null ? _battleDemo.FullDetailSoldierCount : 0;
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
            {
                if (!_searchedForInstancedRenderer)
                {
                    _searchedForInstancedRenderer = true;
                    _instancedRenderer = FindFirstObjectByType<RecoilInstancedSoldierRenderer>();
                }
                else if (_battleDemo != null)
                    _instancedRenderer = _battleDemo.GetComponent<RecoilInstancedSoldierRenderer>();
            }
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
            if (!RecoilCpuPhases.Enabled) return;
            if (_battleDemo == null)
                _battleDemo = FindFirstObjectByType<RecoilAutoBattleDemo>();
            if (_battleDemo != null && !_battleDemo.ShouldShowGameplayHud)
                return;

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
            const float panelHeight = 610f;
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
            GUI.Label(new Rect(18f, 580f, panelWidth - 20f, 20f),
                $"Projectile candidates / unit hits / terrain hits / impacts: {_battleDemo?.ProjectileCandidateChecksLastFrame ?? 0} / " +
                $"{_battleDemo?.ProjectileUnitHitsLastFrame ?? 0} / {_battleDemo?.ProjectileTerrainImpactsLastFrame ?? 0} / " +
                $"{_battleDemo?.ProjectileImpactsLastFrame ?? 0}");
            GUI.Label(new Rect(18f, 600f, panelWidth - 20f, 20f),
                $"Projectile grid cells / stale linear scans: {_battleDemo?.ProjectileGridCellsVisitedLastFrame ?? 0} / " +
                $"{_battleDemo?.ProjectileLinearSearchesLastFrame ?? 0}");
            GUI.Label(new Rect(18f, 620f, panelWidth - 20f, 20f),
                $"Projectile resolve / pending shots / splash: {_battleDemo?.ProjectileResolveMillisecondsLastFrame ?? 0f:F2} ms / " +
                $"{_battleDemo?.PendingCombatProjectileCount ?? 0} / {_battleDemo?.PendingSplashDamageCount ?? 0}");
            GUI.Label(new Rect(18f, 640f, panelWidth - 20f, 20f),
                $"Target weights / priority skips: {_battleDemo?.TargetWeightEvaluationsLastFrame ?? 0} / " +
                $"{_battleDemo?.TargetPriorityWeightSkipsLastFrame ?? 0}");
            GUI.Label(new Rect(18f, 660f, panelWidth - 20f, 20f),
                $"Close weights / priority skips: {_battleDemo?.CloseTargetWeightEvaluationsLastFrame ?? 0} / " +
                $"{_battleDemo?.CloseTargetPriorityWeightSkipsLastFrame ?? 0}");
            GUI.Label(new Rect(18f, 680f, panelWidth - 20f, 20f),
                $"Avoided-category evaluations / selection events: {_battleDemo?.AvoidedCategoryCandidatesLastFrame ?? 0} / " +
                $"{_battleDemo?.AvoidedCategoryTargetsSelectedLastFrame ?? 0}");
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
                var schedule = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.ScheduleCombat);
                var snapshot = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.SnapshotCombat);
                var resolve = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.ResolveCombat);
                var retire = RecoilCpuPhases.Milliseconds(RecoilCpuPhase.RetireCombat);
                var benchmarkStatus = _benchmarkWriter != null
                    ? $" | BENCH {_benchmarkFrames}f (F9 stop)"
                    : $" | F9 record {Mathf.Max(5f, benchmarkDurationSeconds):F0}s";
                _cpuHeader = $"FPS {_fps:F1} | CPU {FormatTiming(_cpuFrameTimeMs)} | Steps/frame {RecoilSimulationClock.LastTicksThisFrame} | Tick {RecoilSimulationClock.CurrentTick}{benchmarkStatus}";
                _movementPanel = $"RecoilTick.Movement: {movement:F2} ms/frame\n" +
                    $"Prepare / schedule: {prepare:F2}\nWait for jobs: {wait:F2}\nApply ECS chunks: {apply:F2}\nSurface fallback: {fallback:F2}\n" +
                    $"Remaining grid wait: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.WaitMovementGrid):F2}\n" +
                    $"Remaining simulation wait: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.WaitMovementSimulation):F2}\n" +
                    $"Remaining collect wait: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.WaitMovementCollect):F2}\n" +
                    $"Grid remove+add (last tick): {_pathService?.LastCrowdGridMutations ?? 0}\n" +
                    $"Grid changed cells/scanned: {_pathService?.LastCrowdGridChangedCells ?? 0} / {_pathService?.LastCrowdGridScannedEntries ?? 0}\n" +
                    $"   Read ECS: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.FallbackRead):F2}\n" +
                    $"   Surface queries: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.FallbackProjection):F2}\n" +
                    $"   Write ECS: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.FallbackWrite):F2}\n" +
                    $"   Advance waypoint: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.FallbackCorner):F2}\n" +
                    $"Other: {System.Math.Max(0, movement - prepare - wait - apply - fallback):F2}\n" +
                    $"Fast/fallback last step: {(_pathService != null ? _pathService.LastSurfaceFastCount : 0)} / {(_pathService != null ? _pathService.LastSurfaceFallbackCount : 0)}\n" +
                    $"Backend: {(_pathService != null ? _pathService.SurfaceBackend : "-")}\n" +
                    $"Worker recovered/held (last step): {(_pathService != null ? _pathService.LastTerrainRecoveryCount : 0)} / {(_pathService != null ? _pathService.LastTerrainHeldCount : 0)}\n" +
                    $"Stuck repaths/failures (total): {(_pathService != null ? _pathService.TotalStuckRepaths : 0)} / {(_pathService != null ? _pathService.TotalMoveFailures : 0)}";
                _battlePanel = $"RecoilTick.Battle: {battle:F2} ms/frame\n" +
                    $"Lifecycle: {life:F2}\n" +
                    $"Movement command age max / >2s: {(_battleDemo?.MaxMovementCommandAgeLastTick ?? 0f):F2}s / {_battleDemo?.MovementCommandOverdueUnitsLastTick ?? 0}\n" +
                    $"Roster build (all calls/frame): {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.RosterBuild):F2}\n" +
                    $"Alive capture + wait: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.AliveCapture):F2}\n" +
                    $"Prune compact: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.RosterCompact):F2}\n" +
                    $"Schedule combat: {schedule:F2}\n   Snapshot/grid (included): {snapshot:F2}\n" +
                    $"      Capture ECS + wait: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.CaptureCombat):F2}\n" +
                    $"      Update grid + wait: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.GridCombat):F2}\n" +
                    $"   Close targeting + wait: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.CloseCombat):F2}\n" +
                    $"   Refresh scheduled ECS: {RecoilCpuPhases.Milliseconds(RecoilCpuPhase.RefreshCombat):F2}\n" +
                    $"Grid units/regions (last tick): {_battleDemo?.CombatGridUnits ?? 0} / {_battleDemo?.CombatGridRegions ?? 0}\n" +
                    $"Grid remove+add (last tick): {_battleDemo?.LastCombatGridMutations ?? 0}\n" +
                    $"Grid changed cells/entries scanned: {_battleDemo?.LastCombatGridChangedCells ?? 0} / {_battleDemo?.LastCombatGridScannedEntries ?? 0}\n" +
                    $"Close searches/empty skips (frame): {_battleDemo?.CloseSearchesLastFrame ?? 0} / {_battleDemo?.CloseEmptySkipsLastFrame ?? 0}\n" +
                    $"Close candidates/attacks (frame): {_battleDemo?.CloseCandidateChecksLastFrame ?? 0} / {_battleDemo?.ContactAttacksLastFrame ?? 0}\n" +
                    $"Close weight evals/priority skips: {_battleDemo?.CloseTargetWeightEvaluationsLastFrame ?? 0} / {_battleDemo?.CloseTargetPriorityWeightSkipsLastFrame ?? 0}\n" +
                    $"Terrain LOS checks/blocked: {_battleDemo?.LineOfFireChecksLastFrame ?? 0} / {_battleDemo?.LineOfFireBlockedLastFrame ?? 0}\n" +
                    $"Strategic LOS checks/blocked: {_battleDemo?.StrategicLineOfFireChecksLastFrame ?? 0} / {_battleDemo?.StrategicLineOfFireBlockedLastFrame ?? 0}\n" +
                    $"Target weight evals/priority skips: {_battleDemo?.TargetWeightEvaluationsLastFrame ?? 0} / {_battleDemo?.TargetPriorityWeightSkipsLastFrame ?? 0}\n" +
                    $"Target searches requested/performed/deferred: {_battleDemo?.TargetSearchRequestsLastFrame ?? 0} / {_battleDemo?.TargetSearchesPerformedLastFrame ?? 0} / {_battleDemo?.TargetSearchesDeferredLastFrame ?? 0}\n" +
                    $"Avoided-category score/selection events: {_battleDemo?.AvoidedCategoryCandidatesLastFrame ?? 0} / {_battleDemo?.AvoidedCategoryTargetsSelectedLastFrame ?? 0}\n" +
                    $"Resolve damage/navigation: {resolve:F2}\nRetire/death cleanup: {retire:F2}\n" +
                    $"Other: {System.Math.Max(0, battle - life - schedule - resolve - retire):F2}\n" +
                    $"3D prefab soldiers: {_fullDetailSoldierCount}\n" +
                    $"GPU bones renderers/parts drawn/draw calls: {_battleDemo?.GpuSkinnedRenderers ?? 0} / {_battleDemo?.GpuSkinnedInstancesLastFrame ?? 0} / {_battleDemo?.GpuSkinnedDrawCallsLastFrame ?? 0}\n" +
                    $"Path project/delivery ms: {(_pathService != null ? _pathService.LastProjectionMilliseconds : 0f):F2} / {(_pathService != null ? _pathService.LastPathDeliveryMilliseconds : 0f):F2}\n" +
                    $"Paths done/ready/cancelled: {(_pathService != null ? _pathService.LastPathCompletions : 0)} / {(_pathService != null ? _pathService.PendingPathCompletions : 0)} / {(_pathService != null ? _pathService.LastCancelledPaths : 0)}\n" +
                    $"Field buffers new/reused: {(_pathService != null ? _pathService.FlowFieldAllocationCount : 0)} / {(_pathService != null ? _pathService.FlowFieldReuseCount : 0)}\n" +
                    $"Projectile resolve: {(_battleDemo?.ProjectileResolveMillisecondsLastFrame ?? 0f):F2} ms | pending shots / splash: {_battleDemo?.PendingCombatProjectileCount ?? 0} / {_battleDemo?.PendingSplashDamageCount ?? 0}\n" +
                    $"Projectile candidates / units / terrain / impacts: {_battleDemo?.ProjectileCandidateChecksLastFrame ?? 0} / {_battleDemo?.ProjectileUnitHitsLastFrame ?? 0} / {_battleDemo?.ProjectileTerrainImpactsLastFrame ?? 0} / {_battleDemo?.ProjectileImpactsLastFrame ?? 0}\n" +
                    $"Projectile lead solved/fallback: {_battleDemo?.ProjectileLeadSolutionsLastFrame ?? 0} / {_battleDemo?.ProjectileLeadFallbacksLastFrame ?? 0}\n" +
                    $"Projectile grid cells visited: {_battleDemo?.ProjectileGridCellsVisitedLastFrame ?? 0}\n" +
                    $"Projectile linear searches (stale grid): {_battleDemo?.ProjectileLinearSearchesLastFrame ?? 0}\n" +
                    $"Shared corridor full/suffix: {(_pathService != null ? _pathService.SharedCorridorHits : 0)} / {(_pathService != null ? _pathService.SharedCorridorSuffixHits : 0)}";
            }
            var width = Mathf.Min(440f, Mathf.Max(240f, Screen.width - 16f));
            var sideBySide = Screen.width >= 910f;
            var movementRect = new Rect(8f, 122f, width, 365f);
            var battleRect = new Rect(sideBySide ? 458f : 8f, sideBySide ? 122f : 494f, width, 569f);
            GUI.Box(new Rect(8f, 96f, Mathf.Min(Screen.width - 16f, sideBySide ? 890f : width), 24f), _cpuHeader);
            GUI.Box(movementRect, GUIContent.none);
            GUI.Box(battleRect, GUIContent.none);
            GUI.Label(new Rect(movementRect.x + 10f, movementRect.y + 8f, width - 20f, 345f), _movementPanel);
            GUI.Label(new Rect(battleRect.x + 10f, battleRect.y + 8f, width - 20f, 549f), _battlePanel);
            GUI.Label(new Rect(8f, Mathf.Max(movementRect.yMax, battleRect.yMax) + 4f, Mathf.Min(Screen.width - 16f, 890f), 40f),
                "Main-thread ms, all ticks/frame. Remaining waits exclude work already completed in background. Refresh: 4 Hz.");
        }

        private static string FormatTiming(float milliseconds) =>
            milliseconds > 0f ? $"{milliseconds:F1} ms" : "not available";

        private static string FormatMemory(long bytes) =>
            $"{bytes / (1024f * 1024f):F0} MB";
    }
}
