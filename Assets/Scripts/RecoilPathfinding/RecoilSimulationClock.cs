using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;

namespace RecoilPathfinding
{
    [DefaultExecutionOrder(100)]
    public sealed class RecoilSimulationClock : MonoBehaviour
    {
        private static RecoilSimulationClock _instance;
        private static readonly ProfilerMarker MovementTickMarker = new("RecoilTick.Movement");
        private static readonly ProfilerMarker BattleTickMarker = new("RecoilTick.Battle");
        public static int LastTicksThisFrame { get; private set; }
        private readonly List<RecoilNavMeshPathService> _services = new();
        private readonly List<RecoilAutoBattleDemo> _battles = new();
        private readonly RecoilFixedTickAccumulator _clock = new();
        [SerializeField, Range(1, 4)] private int maxTicksPerFrame = 2;
        public static long CurrentTick => _instance != null ? _instance._clock.Tick : 0;
        public static double DroppedSeconds => _instance != null ? _instance._clock.DroppedSeconds : 0;

        private static RecoilSimulationClock Instance
        {
            get
            {
                if (_instance == null) _instance = FindFirstObjectByType<RecoilSimulationClock>();
                if (_instance == null) _instance = new GameObject("Recoil Simulation Clock").AddComponent<RecoilSimulationClock>();
                return _instance;
            }
        }

        internal static void Register(RecoilNavMeshPathService service)
        { var clock = Instance; if (!clock._services.Contains(service)) clock._services.Add(service); }
        internal static void Register(RecoilAutoBattleDemo battle)
        { var clock = Instance; if (!clock._battles.Contains(battle)) clock._battles.Add(battle); }
        internal static void Unregister(RecoilNavMeshPathService service)
        { if (_instance != null) _instance._services.Remove(service); }
        internal static void Unregister(RecoilAutoBattleDemo battle)
        { if (_instance != null) _instance._battles.Remove(battle); }

        private void Update()
        {
            RecoilCpuPhases.BeginFrame(UnityEngine.Time.frameCount);
            if (_services.Count == 0 && _battles.Count == 0) return;
            var ticks = _clock.Advance(UnityEngine.Time.deltaTime, maxTicksPerFrame);
            LastTicksThisFrame = ticks;
            var firstTick = _clock.Tick - ticks + 1;
            for (var i = 0; i < ticks; i++)
            {
                var now = (float)((firstTick + i) / 30.0);
                using (MovementTickMarker.Auto())
                using (RecoilCpuPhases.Measure(RecoilCpuPhase.Movement))
                    foreach (var service in _services)
                        if (service != null && service.isActiveAndEnabled) service.SimulateFixedTick(RecoilFixedTickAccumulator.Step);
                using (BattleTickMarker.Auto())
                using (RecoilCpuPhases.Measure(RecoilCpuPhase.Battle))
                    foreach (var battle in _battles)
                        if (battle != null && battle.isActiveAndEnabled) battle.SimulateFixedTick(RecoilFixedTickAccumulator.Step, now);
            }
        }

        private void OnDestroy() { if (_instance == this) _instance = null; }
    }
}
