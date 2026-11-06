using Unity.Entities;
using Unity.Mathematics;

namespace RecoilPathfinding
{
    public static class RecoilExplosionDamageSchedule
    {
        public const int BucketCount = 128;

        public static int BucketIndex(long tick) => (int)(tick & (BucketCount - 1));

        public static long FirstTickToProcess(long lastProcessedTick, long currentTick) =>
            System.Math.Max(lastProcessedTick + 1, currentTick - BucketCount + 1);
    }

    public static class RecoilCombatProjectileTrajectory
    {
        public const int DirectExplosionDamageFrames = 4;

        public static float3 SelectOrigin(float3 unitPosition, float3 muzzlePosition, bool hasMuzzle) =>
            hasMuzzle ? muzzlePosition : unitPosition;

        // Recoil's default weapon explosion speed, in simulation world units per 30 Hz frame.
        public static float DefaultExplosionSpeed(int damage)
        {
            var damageScale = math.max(30f, math.max(0, damage) / 20f);
            return (8f + damageScale * 2.5f) /
                (9f + math.sqrt(damageScale) * 0.7f) * 0.5f;
        }

        public static int SplashDamageDelayFrames(float surfaceDistance, float explosionSpeed)
        {
            var speed = math.max(0.001f, explosionSpeed);
            var distance = math.max(0f, surfaceDistance);
            if (distance < speed * DirectExplosionDamageFrames) return 0;
            return math.max(0, (int)math.floor(distance / speed) -
                (DirectExplosionDamageFrames - 1));
        }

        public static float SplashDamageDelaySeconds(float surfaceDistance, float explosionSpeed) =>
            SplashDamageDelayFrames(surfaceDistance, explosionSpeed) * RecoilFixedTickAccumulator.Step;

        public static float ArcHeight(float2 start, float2 end) =>
            math.max(1f, math.length(end - start) * 0.18f);

        public static float TravelDuration(float horizontalDistance, float speed) =>
            math.max(0.05f, math.max(0f, horizontalDistance) / math.max(0.1f, speed));

        public static bool TryGetInterceptTime(float3 origin, float3 target, float3 targetVelocity,
            float projectileSpeed, out float time)
        {
            var speed = math.max(0.1f, projectileSpeed);
            var relative = target.xz - origin.xz;
            var velocity = targetVelocity.xz;
            var a = math.dot(velocity, velocity) - speed * speed;
            var b = 2f * math.dot(relative, velocity);
            var c = math.dot(relative, relative);
            time = 0f;

            if (c <= 1e-8f) return true;
            if (math.abs(a) <= 1e-6f)
            {
                if (math.abs(b) <= 1e-6f) return false;
                time = -c / b;
                return time > 0f && math.isfinite(time);
            }

            var discriminant = b * b - 4f * a * c;
            if (discriminant < 0f) return false;
            var root = math.sqrt(discriminant);
            var inverse = 0.5f / a;
            var first = (-b - root) * inverse;
            var second = (-b + root) * inverse;
            var firstValid = first > 0f && math.isfinite(first);
            var secondValid = second > 0f && math.isfinite(second);
            if (!firstValid && !secondValid) return false;
            time = firstValid && secondValid ? math.min(first, second) : firstValid ? first : second;
            return true;
        }

        public static float3 LeadTargetPoint(float3 origin, float3 target, float3 targetVelocity,
            float projectileSpeed)
        {
            if (!TryGetInterceptTime(origin, target, targetVelocity, projectileSpeed, out var time))
                return target;
            return target + targetVelocity * time;
        }

        public static float3 Position(float3 start, float3 end, float normalizedTime, float arcHeight)
        {
            var t = math.saturate(normalizedTime);
            var position = math.lerp(start, end, t);
            position.y += 4f * math.max(0f, arcHeight) * t * (1f - t);
            return position;
        }
    }

    public interface IRecoilProjectileGridCellVisitor
    {
        void VisitCell(int x, int z);
    }

    public static class RecoilProjectileGridTraversal
    {
        public static void Visit<TVisitor>(float2 start, float2 end, float cellSize,
            int neighborExtent, ref TVisitor visitor)
            where TVisitor : struct, IRecoilProjectileGridCellVisitor
        {
            if (cellSize <= 0f) return;
            var startCell = (int2)math.floor(start / cellSize);
            var endCell = (int2)math.floor(end / cellSize);
            var delta = end - start;
            var stepX = delta.x > 0f ? 1 : delta.x < 0f ? -1 : 0;
            var stepZ = delta.y > 0f ? 1 : delta.y < 0f ? -1 : 0;
            var tDeltaX = stepX == 0 ? float.PositiveInfinity : cellSize / math.abs(delta.x);
            var tDeltaZ = stepZ == 0 ? float.PositiveInfinity : cellSize / math.abs(delta.y);
            var boundaryX = stepX > 0 ? (startCell.x + 1) * cellSize : startCell.x * cellSize;
            var boundaryZ = stepZ > 0 ? (startCell.y + 1) * cellSize : startCell.y * cellSize;
            var tMaxX = stepX == 0 ? float.PositiveInfinity : (boundaryX - start.x) / delta.x;
            var tMaxZ = stepZ == 0 ? float.PositiveInfinity : (boundaryZ - start.y) / delta.y;
            var x = startCell.x;
            var z = startCell.y;
            var maxSteps = math.abs(endCell.x - startCell.x) + math.abs(endCell.y - startCell.y) + 2;
            for (var step = 0; step < maxSteps; step++)
            {
                for (var offsetX = -neighborExtent; offsetX <= neighborExtent; offsetX++)
                for (var offsetZ = -neighborExtent; offsetZ <= neighborExtent; offsetZ++)
                    visitor.VisitCell(x + offsetX, z + offsetZ);

                if (x == endCell.x && z == endCell.y) break;
                if (tMaxX < tMaxZ)
                {
                    x += stepX;
                    tMaxX += tDeltaX;
                }
                else if (tMaxZ < tMaxX)
                {
                    z += stepZ;
                    tMaxZ += tDeltaZ;
                }
                else
                {
                    x += stepX;
                    z += stepZ;
                    tMaxX += tDeltaX;
                    tMaxZ += tDeltaZ;
                }
            }
        }
    }

    public static class RecoilCombatFacing
    {
        public static void FaceTarget(ref RecoilUnitPose pose, float3 target)
        {
            var direction = target - pose.Position;
            direction.y = 0f;
            if (math.lengthsq(direction) > 0.000001f)
                pose.Rotation = quaternion.LookRotationSafe(math.normalize(direction), math.up());
        }

        public static float3 FlatForward(quaternion rotation)
        {
            var forward = math.mul(rotation, new float3(0f, 0f, 1f));
            forward.y = 0f;
            return math.normalizesafe(forward, new float3(0f, 0f, 1f));
        }
    }

    public static class RecoilCombatTargetWeight
    {
        public const float RecentAttackerPriorityMultiplier = 0.5f;
        public const int RecentAttackerWindowTicks = 200;

        internal static bool IsRecentAttacker(Entity attacker, float lastAttackedTime, float now) =>
            attacker != Entity.Null && now >= lastAttackedTime &&
            now - lastAttackedTime < RecentAttackerWindowTicks * RecoilFixedTickAccumulator.Step;

        internal static float PriorityMultiplier(RecoilBattleCombatSnapshot attacker,
            RecoilBattleCombatSnapshot target)
        {
            var multiplier = attacker.RecentAttackerValid != 0 && attacker.LastAttacker == target.Entity
                ? RecentAttackerPriorityMultiplier
                : 1f;
            return multiplier;
        }

        // Recoil sorts bad target categories behind eligible categories and only uses one
        // when no eligible candidate remains. Keep project TargetPriority as the outer rule.
        internal static bool IsPreferredCandidate(int candidatePriority, bool candidateAvoided,
            float candidateWeight, float candidateDistance, int currentPriority, bool currentAvoided,
            float currentWeight, float currentDistance)
        {
            if (candidatePriority != currentPriority) return candidatePriority < currentPriority;
            if (candidateAvoided != currentAvoided) return !candidateAvoided;
            return candidateWeight < currentWeight ||
                (candidateWeight == currentWeight && candidateDistance < currentDistance);
        }

        internal static bool IsAvoidedTargetCategory(RecoilUnitCombatStats attacker,
            RecoilUnitCombatStats target) => attacker.AvoidTargetCategoryMask != 0 &&
            (target.TargetCategoryMask & attacker.AvoidTargetCategoryMask) != 0;

        // Core spatial/damage terms from Recoil's GenerateWeaponTargets score. This game
        // has no radar/LOS state, paralyze damage, unit-power metadata or Lua target hooks;
        // its TargetPriority and category masks remain explicit outer rules. Lower scores win.
        public static float Calculate(float3 ownerPosition, float3 targetPosition, quaternion ownerRotation,
            RecoilUnitCombatStats attacker, int targetHealth,
            float distanceSquared, float attackRange, float fallbackDamagePerSecond)
        {
            return Calculate(ownerPosition, targetPosition, RecoilCombatFacing.FlatForward(ownerRotation),
                DamagePerSecond(attacker, fallbackDamagePerSecond), targetHealth, distanceSquared, attackRange);
        }

        public static float Calculate(float3 ownerPosition, float3 targetPosition, float3 flatOwnerForward,
            RecoilUnitCombatStats attacker, int targetHealth,
            float distanceSquared, float attackRange, float fallbackDamagePerSecond)
        {
            return Calculate(ownerPosition, targetPosition, flatOwnerForward,
                DamagePerSecond(attacker, fallbackDamagePerSecond), targetHealth, distanceSquared, attackRange);
        }

        public static float DamagePerSecond(RecoilUnitCombatStats attacker, float fallbackDamagePerSecond) =>
            attacker.IsConfigured
                ? math.max(0, attacker.Damage) * math.max(0f, attacker.AttackSpeed) * math.max(1, attacker.SalvoSize)
                : math.max(0f, fallbackDamagePerSecond);

        public static float DamageMultiplier(RecoilUnitCombatStats attacker, RecoilUnitCombatStats target)
        {
            var armor = !target.IsConfigured ? 0f :
                RecoilUnitDamage.UsesRangedArmor(attacker) ? target.SecondaryArmor : target.Armor;
            return math.max(0.0001f, (1f + RecoilUnitDamage.Bonus(attacker.CombatRole, target.CombatRole)) *
                (1f - math.clamp(armor, 0f, 1f)));
        }

        public static float Calculate(float3 ownerPosition, float3 targetPosition, float3 flatOwnerForward,
            float damagePerSecond, int targetHealth, float distanceSquared, float attackRange,
            float targetDamageMultiplier = 1f, float priorityMultiplier = 1f)
        {
            var direction = targetPosition - ownerPosition;
            direction.y = 0f;
            direction = math.normalizesafe(direction);
            var angleOffset = 1f - math.clamp(math.dot(flatOwnerForward, direction), -1f, 1f);
            const float weaponAimAdjustPriority = 1f;
            const float proximityPriority = 1f;
            var angleMultiplier = math.square(1f + angleOffset * weaponAimAdjustPriority);
            var distance = math.sqrt(math.max(0f, distanceSquared));
            var rangeMultiplier = distance * proximityPriority + math.max(0f, attackRange) * 0.4f + 100f;
            // Recoil's GenerateWeaponTargets applies a 100000x priority penalty outside
            // the weapon's unboosted range. Keep distant targets selectable for this game's
            // advance behavior, but prefer a valid target that can be attacked now.
            var outOfRangeMultiplier = distance > attackRange ? 100000f : 1f;
            var targetDurability = math.max(0, targetHealth) + damagePerSecond;
            return angleMultiplier * rangeMultiplier * outOfRangeMultiplier * math.max(1f, targetDurability) *
                   math.max(0.0001f, priorityMultiplier) /
                   math.max(0.0001f, targetDamageMultiplier);
        }
    }

    public static class RecoilProjectileSweep
    {
        public static bool TryGetBodyHitTime(float3 segmentStart, float3 segmentEnd,
            float3 bodyCenter, float horizontalRadius, float verticalHalfHeight,
            float projectileRadius, out float hitTime)
        {
            hitTime = 0f;
            var radius = math.max(0f, horizontalRadius) + math.max(0f, projectileRadius);
            var flatStart = segmentStart.xz - bodyCenter.xz;
            var flatDelta = segmentEnd.xz - segmentStart.xz;
            var a = math.lengthsq(flatDelta);
            var c = math.lengthsq(flatStart) - radius * radius;
            var horizontalEnter = 0f;
            var horizontalExit = 1f;
            if (a <= 1e-8f)
            {
                if (c > 0f) return false;
            }
            else
            {
                var b = 2f * math.dot(flatStart, flatDelta);
                var discriminant = b * b - 4f * a * c;
                if (discriminant < 0f) return false;
                var root = math.sqrt(discriminant);
                var first = (-b - root) / (2f * a);
                var second = (-b + root) / (2f * a);
                if (second < 0f || first > 1f) return false;
                horizontalEnter = math.max(0f, first);
                horizontalExit = math.min(1f, second);
                if (horizontalEnter > horizontalExit) return false;
            }

            var verticalPadding = math.max(0f, projectileRadius);
            var minY = bodyCenter.y - math.max(0f, verticalHalfHeight) - verticalPadding;
            var maxY = bodyCenter.y + math.max(0f, verticalHalfHeight) + verticalPadding;
            var deltaY = segmentEnd.y - segmentStart.y;
            var verticalEnter = 0f;
            var verticalExit = 1f;
            if (math.abs(deltaY) <= 1e-8f)
            {
                if (segmentStart.y < minY || segmentStart.y > maxY) return false;
            }
            else
            {
                var first = (minY - segmentStart.y) / deltaY;
                var second = (maxY - segmentStart.y) / deltaY;
                verticalEnter = math.max(0f, math.min(first, second));
                verticalExit = math.min(1f, math.max(first, second));
                if (verticalEnter > verticalExit) return false;
            }

            hitTime = math.max(horizontalEnter, verticalEnter);
            return hitTime <= math.min(horizontalExit, verticalExit);
        }

        public static bool IsWithinSplash(float distanceSquared, float radius, bool directHit)
        {
            if (directHit) return true;
            var safeRadius = math.max(0f, radius);
            return distanceSquared <= safeRadius * safeRadius;
        }

        public static bool CanDamageSplashTeam(int projectileTeam, int targetTeam) =>
            projectileTeam != targetTeam;

        // Recoil's default edgeEffectiveness is zero: damage falls off linearly from
        // full damage at the collision surface to zero at the configured blast radius.
        public static float SplashDamageMultiplier(float surfaceDistance, float radius,
            float edgeEffectiveness = 0f)
        {
            var safeRadius = math.max(0f, radius);
            var distance = math.max(0f, surfaceDistance);
            if (distance > safeRadius) return 0f;
            var edge = math.clamp(edgeEffectiveness, 0f, 1f);
            if (edge >= 1f || distance < 1f) return 1f;
            var rimDistance = distance * edge;
            return math.saturate((safeRadius + 0.001f - distance) /
                math.max(0.001f, safeRadius + 0.001f - rimDistance));
        }

        public static bool TryGetHitTime(float2 segmentStart, float2 segmentEnd,
            float2 targetPosition, float radius, out float hitTime)
        {
            var delta = segmentEnd - segmentStart;
            var lengthSquared = math.lengthsq(delta);
            var safeRadius = math.max(0f, radius);
            var offset = segmentStart - targetPosition;
            var c = math.lengthsq(offset) - safeRadius * safeRadius;
            if (c <= 0f)
            {
                hitTime = 0f;
                return true;
            }

            if (lengthSquared <= 0.000001f)
            {
                hitTime = 0f;
                return false;
            }

            var halfB = math.dot(offset, delta);
            var discriminant = halfB * halfB - lengthSquared * c;
            if (discriminant < 0f)
            {
                hitTime = 0f;
                return false;
            }

            var firstContact = (-halfB - math.sqrt(discriminant)) / lengthSquared;
            if (firstContact < 0f || firstContact > 1f)
            {
                hitTime = 0f;
                return false;
            }

            hitTime = firstContact;
            return true;
        }
    }
}
