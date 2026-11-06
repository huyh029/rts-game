using Unity.Mathematics;

namespace RecoilPathfinding
{
    public struct RecoilFightCorridor
    {
        public float3 Start, End;
        public float Leash;
        public float IdleSeconds;
        public byte Enabled;

        public bool Allows(float3 owner, float3 target, float range)
        {
            if (Enabled == 0) return true;
            var line = End.xz - Start.xz;
            var t = math.saturate(math.dot(owner.xz - Start.xz, line) / math.max(.001f, math.lengthsq(line)));
            var center = Start.xz + line * t;
            var radius = math.max(0, Leash) + math.max(0, range);
            return math.distancesq(center, target.xz) <= radius * radius;
        }
    }
}
