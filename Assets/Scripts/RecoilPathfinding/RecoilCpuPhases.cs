using System;
using System.Diagnostics;

namespace RecoilPathfinding
{
    public enum RecoilCpuPhase
    {
        Movement, PrepareMovement, WaitMovement, ApplyMovement, FallbackMovement,
        FallbackRead, FallbackProjection, FallbackWrite, FallbackCorner,
        Battle, Lifecycle, Departures, ScheduleCombat, SnapshotCombat, ResolveCombat, RetireCombat, Count
    }

    // Main-thread wall time, accumulated across all ticks/services in one rendered frame.
    public static class RecoilCpuPhases
    {
        private static readonly double[] Times = new double[(int)RecoilCpuPhase.Count];
        private static readonly int[] Calls = new int[(int)RecoilCpuPhase.Count];
        private static int _frame = -1;
        public static void BeginFrame(int frame)
        {
            if (frame == _frame) return;
            _frame = frame;
            Array.Clear(Times, 0, Times.Length);
            Array.Clear(Calls, 0, Calls.Length);
        }
        public static double Milliseconds(RecoilCpuPhase phase) => Times[(int)phase];
        public static int SampleCount(RecoilCpuPhase phase) => Calls[(int)phase];
        public static void AddSample(RecoilCpuPhase phase, double milliseconds)
        {
            if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) return;
            Times[(int)phase] += milliseconds;
            Calls[(int)phase]++;
        }
        public static Scope Measure(RecoilCpuPhase phase) => new Scope(phase);
        public readonly struct Scope : IDisposable
        {
            private readonly RecoilCpuPhase _phase;
            private readonly long _start;
            public Scope(RecoilCpuPhase phase) { _phase = phase; _start = Stopwatch.GetTimestamp(); }
            public void Dispose() => AddSample(_phase, (Stopwatch.GetTimestamp() - _start) * (1000.0 / Stopwatch.Frequency));
        }
    }
}
