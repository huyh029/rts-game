using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace RecoilPathfinding
{
    [DisableAutoCreation]
    public partial class RecoilCombatResolveSystem : SystemBase
    {
        // GetComponentLookup<T>()/GetBufferLookup<T>() re-register their type handle with the
        // system's dependency/safety tracking on every call. ScheduleResolve runs up to
        // maxTicksPerFrame times per frame, so caching these once and refreshing them with the
        // cheap .Update() call (the idiomatic DOTS pattern) avoids paying that setup repeatedly.
        private ComponentLookup<RecoilBattleEcsState> _statesLookup;
        private ComponentLookup<RecoilBattleEcsState> _readStatesLookup;
        private ComponentLookup<RecoilUnitCombatTiming> _timingsLookup;
        private ComponentLookup<RecoilUnitOrder> _ordersLookup;
        private ComponentLookup<RecoilUnitNavigation> _navigationsLookup;
        private ComponentLookup<RecoilUnitPose> _posesLookup;
        private ComponentLookup<RecoilUnitMovementSettings> _settingsLookup;
        private BufferLookup<RecoilUnitWaypoint> _pathsLookup;

        protected override void OnCreate()
        {
            _statesLookup = GetComponentLookup<RecoilBattleEcsState>();
            _readStatesLookup = GetComponentLookup<RecoilBattleEcsState>(true);
            _timingsLookup = GetComponentLookup<RecoilUnitCombatTiming>();
            _ordersLookup = GetComponentLookup<RecoilUnitOrder>();
            _navigationsLookup = GetComponentLookup<RecoilUnitNavigation>();
            _posesLookup = GetComponentLookup<RecoilUnitPose>();
            _settingsLookup = GetComponentLookup<RecoilUnitMovementSettings>(true);
            _pathsLookup = GetBufferLookup<RecoilUnitWaypoint>();
        }

        protected override void OnUpdate() { }

        internal JobHandle ScheduleRefreshTargets(RecoilRefreshCombatTargetsJob job)
        {
            _readStatesLookup.Update(this);
            job.States = _readStatesLookup;
            Dependency = job.Schedule(Dependency);
            return Dependency;
        }

        internal JobHandle ScheduleResolve(RecoilResolveCombatJob job, int count)
        {
            _statesLookup.Update(this);
            _timingsLookup.Update(this);
            _ordersLookup.Update(this);
            _navigationsLookup.Update(this);
            _posesLookup.Update(this);
            _settingsLookup.Update(this);
            _pathsLookup.Update(this);
            job.States = _statesLookup;
            job.Timings = _timingsLookup;
            job.Orders = _ordersLookup;
            job.Navigations = _navigationsLookup;
            job.Poses = _posesLookup;
            job.Settings = _settingsLookup;
            job.Paths = _pathsLookup;
            Dependency = job.Schedule(count, 32, Dependency);
            return Dependency;
        }
    }

    [BurstCompile]
    internal struct RecoilRefreshCombatTargetsJob : IJob
    {
        [ReadOnly] public ComponentLookup<RecoilBattleEcsState> States;
        [ReadOnly] public NativeArray<Entity> Entities;
        [ReadOnly] public NativeArray<int> Scheduled;
        [ReadOnly] public NativeParallelHashMap<Entity, int> Indices;
        public NativeArray<RecoilBattleCombatSnapshot> Snapshots;
        public int Count, SlowPhase;
        public float Now;

        public void Execute()
        {
            // Sequential Burst pass: target stats remain stable while this pass updates each
            // snapshot's target/search fields. No per-unit EntityManager calls or parallel races.
            for (var i = 0; i < Count; i++)
            {
                var index = Scheduled[i];
                var entity = Entities[index];
                if (!States.HasComponent(entity)) continue;
                var snapshot = Snapshots[index];
                snapshot.CurrentTargetIndex = Indices.TryGetValue(States[entity].Target, out var target) ? target : -1;
                var searchDue = Now >= snapshot.NextTargetSearchTime;
                var slowDue = entity.Index % RecoilCloseTargetJob.SlowTargetUpdateRate == SlowPhase;
                if (snapshot.CurrentTargetIndex >= 0)
                {
                    var other = Snapshots[snapshot.CurrentTargetIndex];
                    var only = snapshot.Stats.OnlyTargetCategoryMask;
                    var allowed = only == 0 || (other.Stats.TargetCategoryMask & only) != 0;
                    var avoided = snapshot.Stats.AvoidTargetCategoryMask != 0 &&
                        (other.Stats.TargetCategoryMask & snapshot.Stats.AvoidTargetCategoryMask) != 0;
                    snapshot.SearchTarget = (byte)(other.Active == 0 || other.Health <= 0 || !allowed ||
                        (slowDue && avoided && searchDue) ? 1 : 0);
                }
                else snapshot.SearchTarget = (byte)(searchDue && slowDue ? 1 : 0);
                Snapshots[index] = snapshot;
            }
        }
    }

    internal struct RecoilCombatResolveSummary
    {
        public FixedList4096Bytes<int> RegionTargets;
        public int BlueAliveCount, RedAliveCount, BlueRepresentative, RedRepresentative;
    }

    [BurstCompile]
    internal struct RecoilBuildResolveSummaryJob : IJob
    {
        [ReadOnly] public NativeArray<RecoilBattleCombatSnapshot> Soldiers;
        public int Count;
        public NativeReference<RecoilCombatResolveSummary> Output;
        public void Execute()
        {
            var result = new RecoilCombatResolveSummary { RegionTargets = RecoilCombatRegions.Build(Soldiers, Count),
                BlueRepresentative = -1, RedRepresentative = -1 };
            for (var i = 0; i < Count; i++)
            {
                var soldier = Soldiers[i];
                if (soldier.Active == 0 || soldier.Health <= 0) continue;
                if (soldier.Team == 0)
                {
                    result.BlueAliveCount++;
                    if (result.BlueRepresentative < 0) result.BlueRepresentative = i;
                }
                else
                {
                    result.RedAliveCount++;
                    if (result.RedRepresentative < 0) result.RedRepresentative = i;
                }
            }
            Output.Value = result;
        }
    }

    internal static class RecoilCombatRegions
    {
        private const int Width = 8;
        public static FixedList4096Bytes<int> Build(NativeArray<RecoilBattleCombatSnapshot> soldiers, int count)
        {
            var targets = new FixedList4096Bytes<int>();
            for (var i = 0; i < Width * Width * 2; i++) targets.Add(-1);
            var min = new float2(float.PositiveInfinity);
            var max = new float2(float.NegativeInfinity);
            for (var i = 0; i < count; i++)
            {
                var soldier = soldiers[i];
                if (soldier.Active == 0 || soldier.Health <= 0) continue;
                min = math.min(min, soldier.Position.xz);
                max = math.max(max, soldier.Position.xz);
            }
            var size = math.max(new float2(1f), max - min);
            for (var i = 0; i < count; i++)
            {
                var soldier = soldiers[i];
                if (soldier.Active == 0 || soldier.Health <= 0) continue;
                var cell = math.clamp((int2)math.floor((soldier.Position.xz - min) / size * Width), 0, Width - 1);
                var key = (soldier.Team == 0 ? 0 : Width * Width) + cell.y * Width + cell.x;
                var center = min + ((float2)cell + 0.5f) * size / Width;
                var previous = targets[key];
                if (previous < 0 || math.distancesq(soldier.Position.xz, center) < math.distancesq(soldiers[previous].Position.xz, center))
                    targets[key] = i;
            }
            return targets;
        }
    }

    // Each scheduled slot must reference a unique unit. Target positions come from the
    // immutable snapshot, so stopping one unit cannot race with another unit reading it.
    [BurstCompile]
    internal struct RecoilResolveCombatJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Entity> Entities;
        [ReadOnly] public NativeArray<int> Scheduled;
        [ReadOnly] public NativeArray<RecoilBattleCombatSnapshot> Snapshots;
        [ReadOnly] public NativeArray<RecoilBattleCombatResult> Results;
        [ReadOnly] public NativeArray<byte> RangedUnits;
        [ReadOnly] public ComponentLookup<RecoilUnitMovementSettings> Settings;
        [NativeDisableParallelForRestriction] public ComponentLookup<RecoilBattleEcsState> States;
        [NativeDisableParallelForRestriction] public ComponentLookup<RecoilUnitCombatTiming> Timings;
        [NativeDisableParallelForRestriction] public ComponentLookup<RecoilUnitOrder> Orders;
        [NativeDisableParallelForRestriction] public ComponentLookup<RecoilUnitNavigation> Navigations;
        [NativeDisableParallelForRestriction] public ComponentLookup<RecoilUnitPose> Poses;
        [NativeDisableParallelForRestriction] public BufferLookup<RecoilUnitWaypoint> Paths;
        public NativeList<int>.ParallelWriter PathRequests;
        public RecoilTerrainHeightField Heights;
        public float Now, SearchInterval, AttackRange, ArcherAttackRange, SiegeAttackRange, RetargetDistance;
        public float3 BlueGoal, RedGoal;
        public RecoilCastleMarchTarget BlueMarchTarget, RedMarchTarget;
        public int SoldierCount;
        public int BlueAliveCount, RedAliveCount;
        public int BlueRepresentative, RedRepresentative;
        public FixedList4096Bytes<int> RegionTargets;

        public void Execute(int index)
        {
            var slot = Scheduled[index];
            if (slot < 0 || slot >= SoldierCount) return;
            var entity = Entities[slot];
            if (!States.HasComponent(entity) || !Navigations.HasComponent(entity)) return;
            var state = States[entity];
            var navigation = Navigations[entity];
            if (state.Active == 0 || state.Health <= 0 || navigation.Active == 0) return;
            var timing = Timings[entity];
            var pose = Poses[entity];
            var path = Paths[entity];
            var result = Results[index];
            var target = result.TargetIndex;
            if (target < 0 || target >= SoldierCount || Snapshots[target].Active == 0 ||
                Snapshots[target].Health <= 0 || Snapshots[target].Team == state.Team) target = -1;
            // March targets are named for the attacking team and already point at its enemy.
            var enemyCastle = state.Team == 0 ? BlueMarchTarget : RedMarchTarget;
            var enemiesRemain = (state.Team == 0 ? RedAliveCount : BlueAliveCount) > 0;
            // Castle bounds describe a building, not whether a living soldier is a valid target.
            // A straight segment crossing the building can still have a navigable route around it.
            var unitAttackRange = state.Stats.IsConfigured ? state.Stats.Range :
                RangedUnits[slot] == 2 ? SiegeAttackRange : RangedUnits[slot] == 1 ? ArcherAttackRange : AttackRange;
            if (target >= 0 && !timing.Fight.Allows(pose.Position, Snapshots[target].Position, unitAttackRange)) target = -1;
            if (timing.Fight.Enabled != 0)
            {
                var delta = timing.LastCombatUpdateTime > 0 ? math.max(0, Now - timing.LastCombatUpdateTime) : 1f / 30f;
                var waiting = enemiesRemain && target < 0 && navigation.PathPending == 0 && pose.Speed < .05f;
                timing.Fight.IdleSeconds = waiting ? timing.Fight.IdleSeconds + delta : 0;
                // Preserve the front during the march, but allow isolated/blocked survivors
                // to rejoin combat instead of remaining outside every valid target's leash.
                if (timing.Fight.IdleSeconds >= 5f) timing.Fight.Enabled = 0;
            }
            if (target < 0 && enemiesRemain)
            {
                var fallback = state.Team == 0 ? RedRepresentative : BlueRepresentative;
                if (RegionTargets.Length > 0)
                {
                    fallback = -1;
                    var best = float.PositiveInfinity;
                    var start = state.Team == 0 ? 64 : 0;
                    for (var region = start; region < start + 64; region++)
                    {
                        var candidate = RegionTargets[region];
                        if (candidate < 0 || candidate >= SoldierCount) continue;
                        var other = Snapshots[candidate];
                        if (other.Active == 0 || other.Health <= 0 || other.Team == state.Team) continue;
                        if (!timing.Fight.Allows(pose.Position, other.Position, unitAttackRange)) continue;
                        var only = state.Stats.OnlyTargetCategoryMask;
                        if (only != 0 && (other.Stats.TargetCategoryMask & only) == 0) continue;
                        var score = math.distancesq(pose.Position.xz, other.Position.xz);
                        if ((other.Stats.TargetCategoryMask & state.Stats.AvoidTargetCategoryMask) != 0) score = (score + 1f) * 4f;
                        if (score < best) { best = score; fallback = candidate; }
                    }
                }
                if (fallback >= 0 && fallback < SoldierCount && Snapshots[fallback].Active != 0 &&
                    Snapshots[fallback].Health > 0 && Snapshots[fallback].Team != state.Team &&
                    timing.Fight.Allows(pose.Position, Snapshots[fallback].Position, unitAttackRange)) target = fallback;
            }
            // Castle priority unlocks only after the opposing army has no living units.
            var castleDistance = RecoilCastleMarch.DistanceToBounds(pose.Position.xz,
                enemyCastle.CastleCenter.xz, enemyCastle.CastleExtents.xz);
            if (!enemiesRemain && enemyCastle.HasCastleBounds != 0 &&
                castleDistance <= unitAttackRange)
                target = -1;
            if (!enemiesRemain && target >= 0 && navigation.CastleApproach != 0 &&
                math.distancesq(Snapshots[target].Position.xz, Snapshots[slot].Position.xz) >
                unitAttackRange * unitAttackRange)
                target = -1;
            state.AttackingCastle = 0;
            state.Target = target >= 0 ? Entities[target] : Entity.Null;
            navigation.CombatApproach = (byte)(target >= 0 ? 1 : 0);
            if (target >= 0) RecoilCombatFacing.FaceTarget(ref pose, Snapshots[target].Position);
            timing.LastCombatUpdateTime = Now;
            if (Snapshots[slot].SearchTarget != 0) timing.NextTargetSearchTime = Now + SearchInterval;
            var settings = Settings[entity];
            if (enemiesRemain && target < 0 && timing.Fight.Enabled == 0)
            {
                Stop(ref navigation, ref pose, ref path);
                States[entity] = state;
                Timings[entity] = timing;
                Navigations[entity] = navigation;
                Poses[entity] = pose;
                return;
            }
            if (!enemiesRemain && target < 0 && enemyCastle.HasCastleBounds != 0)
            {
                if (castleDistance <= unitAttackRange)
                {
                    state.AttackingCastle = 1;
                    RecoilCombatFacing.FaceTarget(ref pose, enemyCastle.CastleCenter);
                    Stop(ref navigation, ref pose, ref path);
                    Orders[entity] = new RecoilUnitOrder
                    { Kind = RecoilUnitOrderKind.Attack, Target = Entity.Null,
                        Destination = enemyCastle.CastleCenter };
                    timing.NavigationTarget = Entity.Null;
                    States[entity] = state;
                    Timings[entity] = timing;
                    Navigations[entity] = navigation;
                    Poses[entity] = pose;
                    return;
                }
            }
            var destination = target >= 0 ? Snapshots[target].Position : (state.Team == 0 ? BlueGoal : RedGoal);
            var followFightLine = enemiesRemain && target < 0 && timing.Fight.Enabled != 0;
            if (followFightLine)
            {
                destination = timing.Fight.End;
                navigation.CastleApproach = 0;
                if (Heights.TryFindUnitApproach(pose.Position, destination, settings.AreaMask, out var laneEnd, settings.BodyRadius + .05f))
                    destination = laneEnd;
            }
            if (target >= 0 && Heights.TryFindUnitApproach(pose.Position, destination, settings.AreaMask, out var approach, settings.BodyRadius + 0.05f))
                destination = approach;
            if (target < 0 && !followFightLine)
            {
                var marchTarget = state.Team == 0 ? BlueMarchTarget : RedMarchTarget;
                if (navigation.CastleApproach == 0 &&
                    RecoilCastleMarch.TryStraightDestination(pose.Position, destination, marchTarget, out var straight, settings.WaypointTolerance))
                    destination = straight;
                else
                {
                    navigation.CastleApproach = 1;
                    if (marchTarget.HasCastleBounds != 0)
                    {
                        var cellPadding = Heights.Columns > 1 && Heights.Rows > 1
                            ? math.max(Heights.Size.x / (Heights.Columns - 1),
                                Heights.Size.y / (Heights.Rows - 1)) + 0.25f
                            : 1f;
                        destination = RecoilCastleMarch.AttackApproachPoint(pose.Position,
                            marchTarget, RecoilCastleMarch.AttackApproachPadding(
                                settings.BodyRadius, cellPadding, unitAttackRange));
                    }
                }
                destination.y = Heights.Height(destination.xz);
            }
            Orders[entity] = new RecoilUnitOrder
            { Kind = target >= 0 ? RecoilUnitOrderKind.Attack : RecoilUnitOrderKind.Move,
                Target = state.Target, Destination = destination };
            var attackRange = RangedUnits[slot] == 2
                ? SiegeAttackRange
                : RangedUnits[slot] == 1
                    ? ArcherAttackRange
                    : AttackRange;
            if (state.Stats.IsConfigured) attackRange = state.Stats.Range;
            var adjustedApproach = target >= 0 && math.distancesq(destination.xz, Snapshots[target].Position.xz) > 0.0001f;
            var stoppingRange = target >= 0 && !adjustedApproach ? attackRange :
                adjustedApproach ? math.max(0.1f, settings.WaypointTolerance) : math.max(0.5f, settings.BodyRadius);
            var horizontalDistance = math.length(destination.xz - pose.Position.xz);
            var arcHeight = RangedUnits[slot] != 0
                ? RecoilCombatProjectileTrajectory.ArcHeight(pose.Position.xz, destination.xz)
                : 0f;
            var hasLineOfFire = target < 0 || adjustedApproach || Heights.HasLineOfFire(pose.Position, destination, arcHeight);
            if (math.distancesq(pose.Position.xz, destination.xz) <= stoppingRange * stoppingRange &&
                hasLineOfFire)
            {
                Stop(ref navigation, ref pose, ref path);
                if (target >= 0) RecoilCombatFacing.FaceTarget(ref pose, destination);
                timing.NavigationTarget = state.Target;
            }
            else
            {
                var changed = (target >= 0 && timing.NavigationTarget != state.Target) ||
                              math.distancesq(timing.LastDestination.xz, destination.xz) >=
                              RetargetDistance * RetargetDistance;
                if (changed || (navigation.WaypointIndex >= path.Length && navigation.PathPending == 0))
                {
                    timing.LastDestination = destination;
                    if (target >= 0) timing.NavigationTarget = state.Target;
                    if (navigation.HasDestination == 0 ||
                        (navigation.WaypointIndex >= path.Length && navigation.PathPending == 0) ||
                        math.distancesq(navigation.Destination.xz, destination.xz) > 0.0001f)
                    {
                        navigation.MovementVersion++;
                        navigation.Destination = destination;
                        navigation.HasDestination = 1;
                        if (navigation.PathPending == 0) PathRequests.AddNoResize(slot);
                    }
                }
                if (target < 0) timing.NavigationTarget = Entity.Null;
            }
            States[entity] = state;
            Timings[entity] = timing;
            Navigations[entity] = navigation;
            Poses[entity] = pose;
        }

        private static void Stop(ref RecoilUnitNavigation navigation, ref RecoilUnitPose pose,
            ref DynamicBuffer<RecoilUnitWaypoint> path)
        {
            if (navigation.HasDestination == 0 && navigation.PathPending == 0 &&
                navigation.WaypointIndex == 0 && navigation.HasPathEnd == 0 && path.Length == 0 &&
                pose.Speed == 0f && math.lengthsq(pose.Velocity) <= 1e-8f)
                return;
            navigation.MovementVersion++; navigation.RequestVersion++;
            navigation.HasDestination = 0; navigation.PathPending = 0; navigation.WaypointIndex = 0; navigation.HasPathEnd = 0;
            path.Clear(); pose.Speed = 0f; pose.Velocity = float3.zero;
        }
    }
}
