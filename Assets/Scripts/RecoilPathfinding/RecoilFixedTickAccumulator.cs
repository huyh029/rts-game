using System;

namespace RecoilPathfinding
{
    // Fixed simulation time is separate from rendered frame time. Overload is bounded,
    // rather than producing a giant movement step or an unbounded catch-up loop.
    public sealed class RecoilFixedTickAccumulator
    {
        private double _remaining;
        public long Tick { get; private set; }
        public double Time => Tick / 30.0;
        public double DroppedSeconds { get; private set; }
        public const float Step = 1f / 30f;
        public int Advance(double elapsed, int maxTicks)
        {
            if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed <= 0 || maxTicks <= 0) return 0;
            _remaining += elapsed;
            var available = (long)Math.Floor((_remaining + 1e-9) * 30.0);
            var ticks = (int)Math.Min(available, maxTicks);
            var dropped = available - ticks;
            DroppedSeconds += dropped / 30.0;
            _remaining -= available / 30.0;
            Tick += ticks;
            return ticks;
        }
    }

}
