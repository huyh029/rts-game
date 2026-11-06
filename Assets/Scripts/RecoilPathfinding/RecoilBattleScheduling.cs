using System;

namespace RecoilPathfinding
{
    public static class RecoilBattleScheduling
    {
        // Carry fractional entitlement across frames so small spawn budgets still respect
        // the queue ratio. Counts are the remaining eligible requests, not army sizes.
        public static bool TakeProportionalSpawn(int blue, int red, ref double blueCredit)
        {
            if (blue <= 0) { blueCredit = 0; return false; }
            if (red <= 0) { blueCredit = 0; return true; }
            blueCredit += (double)blue / ((long)blue + red);
            if (blueCredit < .5) return false;
            blueCredit -= 1;
            return true;
        }

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
