using Unity.Mathematics;

namespace RecoilPathfinding
{
    public struct RecoilCastleMarchTarget
    {
        public float3 Origin;
        public float3 CastleCenter;
        public float3 CastleExtents;
        public byte HasCastleBounds;
    }

    public static class RecoilCastleMarch
    {
        public const float ApproachDistance = 150f;

        // Before this gate, route only to a point on the unit's current forward lane.
        // Do not ask the pathfinder to route around the distant enemy castle yet.
        public static bool TryStraightDestination(float3 position, float3 enemySpawn,
            RecoilCastleMarchTarget target, out float3 destination, float waypointTolerance = .25f)
        {
            var center = target.HasCastleBounds != 0 ? target.CastleCenter : enemySpawn;
            var extents = target.HasCastleBounds != 0 ? target.CastleExtents : float3.zero;
            var forward = math.normalizesafe(new float2(enemySpawn.x - target.Origin.x,
                enemySpawn.z - target.Origin.z), new float2(1f, 0f));
            destination = position;
            if (DistanceToBounds(position.xz, center.xz, extents.xz) <= ApproachDistance + .1f) return false;
            var along = math.dot(center.xz - position.xz, forward);
            if (along <= 0f) return false;
            var near = 0f;
            var far = along;
            if (DistanceToBounds(position.xz + forward * far, center.xz, extents.xz) > ApproachDistance)
            {
                // A very wide flank lane misses the 150m envelope: use the front
                // approach plane so the unit can turn inward instead of getting stuck.
                var support = math.dot(math.abs(forward), extents.xz);
                far = math.max(0f, along - support - ApproachDistance);
                if (far <= .1f) return false;
            }
            else
            {
                for (var i = 0; i < 20; i++)
                {
                    var middle = (near + far) * .5f;
                    if (DistanceToBounds(position.xz + forward * middle, center.xz, extents.xz) > ApproachDistance)
                        near = middle;
                    else far = middle;
                }
            }
            // Aim slightly inside the gate, avoiding final-waypoint tolerance stopping
            // the unit just outside the range needed to switch to castle pathfinding.
            var point = position.xz + forward * (far + math.max(.5f, waypointTolerance + .1f));
            destination = new float3(point.x, position.y, point.y);
            return true;
        }

        public static float DistanceToBounds(float2 point, float2 center, float2 extents) =>
            math.length(math.max(math.abs(point - center) - extents, float2.zero));
    }

    public sealed partial class RecoilAutoBattleDemo
    {
        private RecoilCastleMarchTarget GetCastleMarchTarget(bool blue)
        {
            var target = new RecoilCastleMarchTarget { Origin = GetSpawnTargetPosition(blue) };
            if (terrainMap == null) return target;
            var enemy = blue ? RecoilCastleTeam.Red : RecoilCastleTeam.Blue;
            for (var i = 0; i < terrainMap.CastleBlockCount; i++)
            {
                var block = terrainMap.GetCastleBlock(i);
                if (block.Team != enemy) continue;
                target.HasCastleBounds = 1;
                target.CastleCenter = block.WorldBounds.center;
                target.CastleExtents = block.WorldBounds.extents;
                break;
            }
            return target;
        }
    }
}
