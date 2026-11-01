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
        private ComponentLookup<RecoilUnitCombatTiming> _timingsLookup;
        private ComponentLookup<RecoilUnitOrder> _ordersLookup;
        private ComponentLookup<RecoilUnitNavigation> _navigationsLookup;
        private ComponentLookup<RecoilUnitPose> _posesLookup;
        private ComponentLookup<RecoilUnitMovementSettings> _settingsLookup;
        private BufferLookup<RecoilUnitWaypoint> _pathsLookup;

        protected override void OnCreate()
        {
            _statesLookup = GetComponentLookup<RecoilBattleEcsState>();
            _timingsLookup = GetComponentLookup<RecoilUnitCombatTiming>();
            _ordersLookup = GetComponentLookup<RecoilUnitOrder>();
            _navigationsLookup = GetComponentLookup<RecoilUnitNavigation>();
            _posesLookup = GetComponentLookup<RecoilUnitPose>();
            _settingsLookup = GetComponentLookup<RecoilUnitMovementSettings>(true);
            _pathsLookup = GetBufferLookup<RecoilUnitWaypoint>();
        }

        protected override void OnUpdate() { }

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

    // Each scheduled slot must reference a unique unit. Target positions come from the
    // immutable snapshot, so stopping one unit cannot race with another unit reading it.
    [BurstCompile]
    internal struct RecoilResolveCombatJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Entity> Entities;
        [ReadOnly] public NativeArray<int> Scheduled;
        [ReadOnly] public NativeArray<byte> Released;
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
            state.Target = target >= 0 ? Entities[target] : Entity.Null;
            navigation.CombatApproach = (byte)(target >= 0 ? 1 : 0);
            if (target >= 0) RecoilCombatFacing.FaceTarget(ref pose, Snapshots[target].Position);
            timing.LastCombatUpdateTime = Now;
            if (Snapshots[slot].SearchTarget != 0) timing.NextTargetSearchTime = Now + SearchInterval;
            if (Released[index] == 0)
            {
                if (navigation.WaypointIndex < path.Length || navigation.PathPending != 0)
                    Stop(ref navigation, ref pose, ref path);
            }
            else
            {
                var settings = Settings[entity];
                var destination = target >= 0 ? Snapshots[target].Position : (state.Team == 0 ? BlueGoal : RedGoal);
                if (target < 0)
                {
                    var marchTarget = state.Team == 0 ? BlueMarchTarget : RedMarchTarget;
                    if (navigation.CastleApproach == 0 &&
                        RecoilCastleMarch.TryStraightDestination(pose.Position, destination, marchTarget, out var straight, settings.WaypointTolerance))
                        destination = straight;
                    else
                    {
                        navigation.CastleApproach = 1;
                        var formationSlot = entity.Index % 1024;
                        var spacing = math.max(1.1f, settings.BodyRadius * 2f + 0.1f);
                        destination += new float3(formationSlot % 32 - 15.5f, 0f, formationSlot / 32 - 15.5f) * spacing;
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
                var stoppingRange = target >= 0 ? attackRange : math.max(0.5f, settings.BodyRadius);
                if (math.distancesq(pose.Position.xz, destination.xz) <= stoppingRange * stoppingRange)
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
                    if (Now >= timing.NextPathRequestTime &&
                        (changed || (navigation.WaypointIndex >= path.Length && navigation.PathPending == 0)))
                    {
                        timing.LastDestination = destination;
                        timing.NextPathRequestTime = Now + (target >= 0 && changed ? 0.35f : 1f);
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
            }
            States[entity] = state;
            Timings[entity] = timing;
            Navigations[entity] = navigation;
            Poses[entity] = pose;
        }

        private static void Stop(ref RecoilUnitNavigation navigation, ref RecoilUnitPose pose,
            ref DynamicBuffer<RecoilUnitWaypoint> path)
        {
            navigation.MovementVersion++; navigation.RequestVersion++;
            navigation.HasDestination = 0; navigation.PathPending = 0; navigation.WaypointIndex = 0; navigation.HasPathEnd = 0;
            path.Clear(); pose.Speed = 0f; pose.Velocity = float3.zero;
        }
    }
}
