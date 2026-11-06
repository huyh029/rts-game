using System;
using System.Diagnostics;

namespace RecoilPathfinding
{
    public enum RecoilCpuPhase
    {
        Movement, PrepareMovement, WaitMovement, ApplyMovement, FallbackMovement,
        FallbackRead, FallbackProjection, FallbackWrite, FallbackCorner,
        Battle, Lifecycle, Departures, ScheduleCombat, SnapshotCombat, ResolveCombat, RetireCombat,
        CaptureCombat, GridCombat, CloseCombat, RefreshCombat,
        WaitMovementGrid, WaitMovementSimulation, WaitMovementCollect,
        RosterBuild, AliveCapture, RosterCompact, ProjectileResolve, Count
    }

    // Main-thread wall time, accumulated across all ticks/services in one rendered frame.
    public static class RecoilCpuPhases
    {
        // Toggle to restore custom timing and profiler samples for diagnostics.
        public static bool Enabled { get; set; } = false;
        public static long Timestamp() => Enabled ? Stopwatch.GetTimestamp() : 0L;

        public readonly struct Marker
        {
            private readonly Unity.Profiling.ProfilerMarker _marker;
            public Marker(string name) { _marker = new Unity.Profiling.ProfilerMarker(name); }
            public Unity.Profiling.ProfilerMarker.AutoScope Auto() =>
                Enabled ? _marker.Auto() : default;
        }

        private static readonly double[] Times = new double[(int)RecoilCpuPhase.Count];
        private static readonly int[] Calls = new int[(int)RecoilCpuPhase.Count];
        private static int _frame = -1;
        public static void BeginFrame(int frame)
        {
            if (!Enabled) return;
            if (frame == _frame) return;
            _frame = frame;
            Array.Clear(Times, 0, Times.Length);
            Array.Clear(Calls, 0, Calls.Length);
        }
        public static double Milliseconds(RecoilCpuPhase phase) => Times[(int)phase];
        public static int SampleCount(RecoilCpuPhase phase) => Calls[(int)phase];
        public static void AddSample(RecoilCpuPhase phase, double milliseconds)
        {
            if (!Enabled) return;
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) return;
            Times[(int)phase] += milliseconds;
            Calls[(int)phase]++;
        }
        public static Scope Measure(RecoilCpuPhase phase) => new Scope(phase);
        public readonly struct Scope : IDisposable
        {
            private readonly RecoilCpuPhase _phase;
            private readonly long _start;
            public Scope(RecoilCpuPhase phase) { _phase = phase; _start = Timestamp(); }
            public void Dispose()
            {
                if (!Enabled) return;
                AddSample(_phase, (Stopwatch.GetTimestamp() - _start) * (1000.0 / Stopwatch.Frequency));
            }
        }
    }
}
