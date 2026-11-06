using Unity.Mathematics;
using UnityEngine;

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

        public static float AttackApproachPadding(float bodyRadius, float cellPadding, float attackRange)
        {
            var desiredClearance = math.max(math.max(0.25f, bodyRadius + 0.25f), cellPadding);
            var inRangeLimit = math.max(0.1f, attackRange * 0.9f);
            return math.max(0.1f, math.min(desiredClearance, inRangeLimit));
        }

        // Mirrors Recoil's immobile-target approach: aim at the nearest edge of the
        // target footprint, then stop just outside it so the unit remains on walkable ground.
        public static float3 AttackApproachPoint(float3 unitPosition, RecoilCastleMarchTarget target, float padding)
        {
            if (target.HasCastleBounds == 0)
                return target.Origin;

            var relative = unitPosition.xz - target.CastleCenter.xz;
            var closest = target.CastleCenter.xz + math.clamp(relative, -target.CastleExtents.xz,
                target.CastleExtents.xz);
            var outward = unitPosition.xz - closest;
            if (math.lengthsq(outward) < 0.0001f)
            {
                // If the caller starts inside the footprint, choose its nearest edge.
                var remaining = target.CastleExtents.xz - math.abs(relative);
                var axis = remaining.x <= remaining.y ? new float2(math.sign(relative.x), 0f) :
                    new float2(0f, math.sign(relative.y));
                if (math.lengthsq(axis) < 0.5f) axis = new float2(1f, 0f);
                closest += axis * (remaining.x <= remaining.y ? target.CastleExtents.x : target.CastleExtents.y);
                outward = axis;
            }
            outward = math.normalizesafe(outward, math.normalizesafe(unitPosition.xz - target.CastleCenter.xz,
                new float2(1f, 0f)));
            var point = closest + outward * math.max(0.25f, padding);
            return new float3(point.x, target.CastleCenter.y, point.y);
        }

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

        public static bool SegmentIntersectsBounds(float2 start, float2 end, float2 center, float2 extents)
        {
            var direction = end - start;
            var min = center - extents;
            var max = center + extents;
            var near = 0f;
            var far = 1f;
            for (var axis = 0; axis < 2; axis++)
            {
                var origin = axis == 0 ? start.x : start.y;
                var delta = axis == 0 ? direction.x : direction.y;
                var lower = axis == 0 ? min.x : min.y;
                var upper = axis == 0 ? max.x : max.y;
                if (math.abs(delta) < 0.00001f)
                {
                    if (origin < lower || origin > upper) return false;
                    continue;
                }
                var first = (lower - origin) / delta;
                var second = (upper - origin) / delta;
                if (first > second)
                {
                    var swap = first;
                    first = second;
                    second = swap;
                }
                near = math.max(near, first);
                far = math.min(far, second);
                if (near > far) return false;
            }
            return far >= 0f && near <= 1f;
        }
    }

    public sealed partial class RecoilAutoBattleDemo
    {
        private Vector3 GetCastleTargetCenter(bool blueAttacker)
        {
            if (terrainMap == null || terrainMap.CastleBlockCount != 2)
                return GetSpawnTargetPosition(!blueAttacker);
            return (Vector3)GetCastleMarchTarget(blueAttacker).CastleCenter;
        }

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
