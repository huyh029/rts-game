using System;

namespace RecoilPathfinding
{
    public static class RecoilBattleScheduling
    {
        // A slow rendered frame must not accumulate an entire army's worth of catch-up work.
        public static int TakeUpdates(int count, float deltaTime, float rate, int budget, ref float accumulator)
        {
            if (count <= 0 || budget <= 0)
            {
                accumulator = 0f;
                return 0;
            }
            var limit = Math.Min(count, budget);
            accumulator = Math.Min(limit, accumulator + count * Math.Min(Math.Max(0f, deltaTime), 1f / 30f) * Math.Max(0f, rate));
            var updates = Math.Min(limit, (int)accumulator);
            accumulator -= updates;
            return updates;
        }
    }
}
