using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.InputSystem;

namespace RecoilPathfinding
{
    [DefaultExecutionOrder(100)]
    public sealed class RecoilSimulationClock : MonoBehaviour
    {
        private static RecoilSimulationClock _instance;
        private static readonly ProfilerMarker MovementTickMarker = new("RecoilTick.Movement");
        private static readonly ProfilerMarker BattleTickMarker = new("RecoilTick.Battle");
        private static readonly ProfilerMarker VatTickMarker = new("RecoilTick.VAT");
        public static int LastTicksThisFrame { get; private set; }
        private readonly List<RecoilNavMeshPathService> _services = new();
        private readonly List<RecoilAutoBattleDemo> _battles = new();
        private readonly RecoilFixedTickAccumulator _clock = new();
        [SerializeField, Range(1, 4)] private int maxTicksPerFrame = 2;
        [SerializeField, Range(0f, 10f)] private float simulationSpeed = 1f;
        private long _executingTick;
        public static bool IsRunning => _instance != null && _instance.isActiveAndEnabled;
        public static float Speed
        {
            get => _instance != null ? _instance.simulationSpeed : 1f;
            set
            {
                if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
                Instance.simulationSpeed = Mathf.Clamp(value, 0f, 10f);
            }
        }
        public static long CurrentTick => _instance != null ?
            (_instance._executingTick > 0 ? _instance._executingTick : _instance._clock.Tick) : 0;
        public static float SimulationSeconds => (float)(CurrentTick / 30.0);
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
            var keyboard = Keyboard.current;
            if (Application.isFocused && keyboard != null && keyboard.pKey.wasPressedThisFrame)
                Speed = Speed > 1f ? 1f : 10f;
            RecoilCpuPhases.BeginFrame(UnityEngine.Time.frameCount);
            if (_services.Count == 0 && _battles.Count == 0) return;
            // Allow proportionally more fixed ticks; the normal two-tick cap would
            // otherwise discard most of the additional simulation time at 10x.
            var tickBudget = maxTicksPerFrame * Mathf.Clamp(Mathf.CeilToInt(simulationSpeed), 1, 10);
            var ticks = _clock.Advance(UnityEngine.Time.unscaledDeltaTime * simulationSpeed, tickBudget);
            LastTicksThisFrame = ticks;
            var firstTick = _clock.Tick - ticks + 1;
            for (var i = 0; i < ticks; i++)
            {
                _executingTick = firstTick + i;
                var now = (float)((firstTick + i) / 30.0);
                Shader.SetGlobalFloat("_RecoilSimulationTime", now);
                using (MovementTickMarker.Auto())
                using (RecoilCpuPhases.Measure(RecoilCpuPhase.Movement))
                    foreach (var service in _services)
                        if (service != null && service.isActiveAndEnabled) service.SimulateFixedTick(RecoilFixedTickAccumulator.Step);
                using (BattleTickMarker.Auto())
                using (RecoilCpuPhases.Measure(RecoilCpuPhase.Battle))
                    foreach (var battle in _battles)
                        if (battle != null && battle.isActiveAndEnabled) battle.SimulateFixedTick(RecoilFixedTickAccumulator.Step, now);
                using (VatTickMarker.Auto()) RecoilVatEcsBridge.TickAll(RecoilFixedTickAccumulator.Step);
            }
            _executingTick = 0;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
