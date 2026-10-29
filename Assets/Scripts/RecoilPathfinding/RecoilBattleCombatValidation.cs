using System;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Entities;
using Unity.Jobs;

namespace RecoilPathfinding
{
    // Runs the actual targeting kernel on small, deterministic inputs without a scene or GameObjects.
    public static class RecoilBattleCombatValidation
    {
        public static void Run()
        {
            ValidateEcsResolve();
            ValidateDamagePipeline();
            ValidateAliveFlags();
            ValidateCloseAcquisition();
            ValidateIncrementalGrid();
            ValidateChunkSnapshot();
            using var ownedSoldiers = new NativeArray<RecoilBattleCombatSnapshot>(4, Allocator.TempJob);
            var soldiers = ownedSoldiers;
            using var scheduled = new NativeArray<int>(1, Allocator.TempJob);
            using var results = new NativeArray<RecoilBattleCombatResult>(1, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int3, int>(8, Allocator.TempJob);
            soldiers[0] = new RecoilBattleCombatSnapshot
            { Position = float3.zero, Team = 0, Health = 100, Active = 1, CurrentTargetIndex = -1, SearchTarget = 1 };
            soldiers[1] = new RecoilBattleCombatSnapshot
            { Position = new float3(1, 0, 0), Team = 0, Health = 100, Active = 1 };
            soldiers[2] = new RecoilBattleCombatSnapshot
            { Position = new float3(2, 0, 0), Team = 1, Health = 100, Active = 1 };
            soldiers[3] = new RecoilBattleCombatSnapshot
            { Position = new float3(100, 0, 0), Team = 1, Health = 100, Active = 1 };
            cells.Add(new int3(0, 0, 0), 0);
            cells.Add(new int3(0, 0, 0), 1);
            cells.Add(new int3(0, 0, 1), 2);
            cells.Add(new int3(25, 0, 1), 3);
            var job = new RecoilBattleCombatJob
            {
                Soldiers = soldiers, ScheduledSoldiers = scheduled, Cells = cells, Results = results,
                MinCellX = 0, MaxCellX = 25, MinCellZ = 0, MaxCellZ = 0, CellSize = 4f,
                DetectionRangeSquared = 24f * 24f, MaxCandidateChecks = 128,
                AttackRangeSquared = 9f, AttackInterval = 1f, AttackDamage = 20, BlueCount = 2, RedCount = 2
            };
            job.Execute(0);
            Require(results[0].TargetIndex == 2 && results[0].Damage == 20 && results[0].CandidateChecks == 1,
                "Local search must find the nearby enemy, ignore friendly cells and attack in range.");

            var own = soldiers[0];
            own.CurrentTargetIndex = 2;
            own.SearchTarget = 0;
            soldiers[0] = own;
            job.Execute(0);
            Require(results[0].TargetIndex == 2 && results[0].CandidateChecks == 0,
                "A valid target must be retained without a grid query.");

            var enemy = soldiers[2];
            enemy.Active = 0;
            soldiers[2] = enemy;
            job.Execute(0);
            Require(results[0].TargetIndex == -1 && results[0].CandidateChecks == 0,
                "A dead target must be dropped without bypassing the search schedule.");
            own.SearchTarget = 1;
            soldiers[0] = own;
            job.Execute(0);
            Require(results[0].TargetIndex == -1,
                "A distant enemy must not be selected outside detection range.");

            job.MaxCandidateChecks = 1;
            cells.Add(new int3(0, 0, 1), 3);
            job.Execute(0);
            Require(results[0].CandidateChecks <= 1, "A dense cell must respect the candidate budget.");
        }

        private static void ValidateEcsResolve()
        {
            using var world = new World("ECS combat resolution checks");
            var manager = world.EntityManager;
            var archetype = manager.CreateArchetype(typeof(RecoilBattleEcsState), typeof(RecoilUnitNavigation),
                typeof(RecoilUnitPose), typeof(RecoilUnitMovementSettings), typeof(RecoilUnitOrder),
                typeof(RecoilUnitCombatTiming), typeof(RecoilUnitWaypoint));
            var unit = manager.CreateEntity(archetype);
            var target = manager.CreateEntity(archetype);
            manager.SetComponentData(unit, new RecoilBattleEcsState { Active = 1, Health = 100 });
            manager.SetComponentData(unit, new RecoilUnitNavigation { Active = 1 });
            manager.SetComponentData(unit, new RecoilUnitPose { Speed = 1f, Velocity = new float3(1f, 0f, 0f) });
            manager.SetComponentData(unit, new RecoilUnitMovementSettings { BodyRadius = 0.5f });
            using var ownedEntities = new NativeArray<Entity>(2, Allocator.TempJob);
            using var ownedSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(2, Allocator.TempJob);
            using var ownedResults = new NativeArray<RecoilBattleCombatResult>(1, Allocator.TempJob);
            using var ownedReleased = new NativeArray<byte>(1, Allocator.TempJob);
            using var scheduled = new NativeArray<int>(1, Allocator.TempJob);
            using var requests = new NativeList<int>(1, Allocator.TempJob);
            using var heights = new NativeArray<float>(4, Allocator.TempJob);
            using var leaves = new NativeArray<int>(0, Allocator.TempJob);
            using var starts = new NativeArray<int>(0, Allocator.TempJob);
            var entities = ownedEntities; var snapshots = ownedSnapshots; var results = ownedResults; var released = ownedReleased;
            entities[0] = unit; entities[1] = target;
            snapshots[0] = new RecoilBattleCombatSnapshot { Active = 1, Health = 100, SearchTarget = 1 };
            snapshots[1] = new RecoilBattleCombatSnapshot { Active = 1, Health = 100, Team = 1, Position = new float3(10f, 0f, 0f) };
            results[0] = new RecoilBattleCombatResult { TargetIndex = 1 };
            released[0] = 1;
            var job = new RecoilResolveCombatJob
            {
                Entities = entities, Scheduled = scheduled, Released = released, Snapshots = snapshots, Results = results,
                PathRequests = requests.AsParallelWriter(), Now = 5f, SearchInterval = 0.4f, AttackRange = 1f,
                RetargetDistance = 2f, SoldierCount = 2, BlueGoal = new float3(50f, 0f, 50f),
                Heights = new RecoilTerrainHeightField { Heights = heights, CellLeaves = leaves, LeafTriangleStarts = starts,
                    Columns = 2, Rows = 2, Size = new float2(100f, 100f) }
            };
            var system = world.GetOrCreateSystemManaged<RecoilCombatResolveSystem>();
            system.ScheduleResolve(job, 1).Complete();
            Require(manager.GetComponentData<RecoilBattleEcsState>(unit).Target == target &&
                manager.GetComponentData<RecoilUnitOrder>(unit).Kind == RecoilUnitOrderKind.Attack &&
                manager.GetComponentData<RecoilUnitNavigation>(unit).HasDestination == 1 && requests.Length == 1,
                "ECS resolve must apply target/order/navigation and queue only new paths.");
            Require(math.abs(manager.GetComponentData<RecoilUnitCombatTiming>(unit).NextTargetSearchTime - 5.4f) < 0.001f,
                "ECS resolve must advance target search timing.");
            requests.Clear();
            var snapshot = snapshots[1]; snapshot.Position = new float3(0.5f, 0f, 0f); snapshots[1] = snapshot;
            job.Now = 6f;
            system.ScheduleResolve(job, 1).Complete();
            Require(manager.GetComponentData<RecoilUnitNavigation>(unit).HasDestination == 0 &&
                manager.GetComponentData<RecoilUnitPose>(unit).Speed == 0f && requests.Length == 0,
                "Targets in attack range must stop ECS movement without requesting a path.");
            var navigation = manager.GetComponentData<RecoilUnitNavigation>(unit);
            navigation.HasDestination = 1; navigation.PathPending = 1;
            manager.SetComponentData(unit, navigation);
            manager.GetBuffer<RecoilUnitWaypoint>(unit).Add(new RecoilUnitWaypoint { Position = new float3(4f, 0f, 0f) });
            released[0] = 0; job.Now = 7f;
            system.ScheduleResolve(job, 1).Complete();
            Require(manager.GetBuffer<RecoilUnitWaypoint>(unit).Length == 0 &&
                manager.GetComponentData<RecoilUnitNavigation>(unit).PathPending == 0 && requests.Length == 0,
                "Unreleased units must cancel movement while retaining close combat targeting.");
            released[0] = 1; results[0] = new RecoilBattleCombatResult { TargetIndex = -1 }; job.Now = 8f;
            system.ScheduleResolve(job, 1).Complete();
            Require(manager.GetComponentData<RecoilBattleEcsState>(unit).Target == Entity.Null &&
                manager.GetComponentData<RecoilUnitOrder>(unit).Kind == RecoilUnitOrderKind.Move && requests.Length == 1,
                "Units without a local target must advance to the strategic destination through ECS.");
            requests.Clear();
            navigation = manager.GetComponentData<RecoilUnitNavigation>(unit); navigation.PathPending = 1;
            manager.SetComponentData(unit, navigation);
            snapshot.Position = new float3(20f, 0f, 0f); snapshots[1] = snapshot;
            results[0] = new RecoilBattleCombatResult { TargetIndex = 1 }; job.Now = 9f;
            system.ScheduleResolve(job, 1).Complete();
            Require(requests.Length == 0 && manager.GetComponentData<RecoilUnitNavigation>(unit).Destination.x == 20f,
                "Pending requests must retain their callback and update the desired destination without duplicates.");
        }

        private static void ValidateDamagePipeline()
        {
            using var world = new World("Recoil damage pipeline checks");
            var manager = world.EntityManager;
            var own = manager.CreateEntity(typeof(RecoilBattleEcsState));
            var enemy = manager.CreateEntity(typeof(RecoilBattleEcsState));
            var survivor = manager.CreateEntity(typeof(RecoilBattleEcsState));
            var unrelated = manager.CreateEntity(typeof(RecoilBattleEcsState));
            manager.SetComponentData(own, new RecoilBattleEcsState { Active = 1, Health = 100 });
            manager.SetComponentData(enemy, new RecoilBattleEcsState { Active = 1, Health = 35, Target = own });
            manager.SetComponentData(survivor, new RecoilBattleEcsState { Active = 1, Health = 80 });
            manager.SetComponentData(unrelated, new RecoilBattleEcsState { Active = 1, Health = 100 });
            using var indices = new NativeParallelHashMap<Entity, int>(3, Allocator.TempJob);
            indices.Add(own, 0); indices.Add(enemy, 1); indices.Add(survivor, 2);
            using var ownedAttacks = new NativeArray<RecoilContactAttack>(3, Allocator.TempJob);
            var attacks = ownedAttacks;
            attacks[0] = new RecoilContactAttack { TargetIndex = 1, Damage = 20 };
            attacks[1] = new RecoilContactAttack { TargetIndex = 1, Damage = 20 };
            attacks[2] = new RecoilContactAttack { TargetIndex = 0, Damage = 5 };
            using var damage = new NativeArray<int>(3, Allocator.TempJob);
            using var deaths = new NativeList<int>(3, Allocator.TempJob);
            using var query = manager.CreateEntityQuery(ComponentType.ReadWrite<RecoilBattleEcsState>());
            var aggregate = new RecoilAggregateDamageJob { Attacks = attacks, Damage = damage, Count = 3 }.Schedule();
            var apply = new RecoilApplyDamageJob
            {
                Entities = manager.GetEntityTypeHandle(), States = manager.GetComponentTypeHandle<RecoilBattleEcsState>(false),
                Indices = indices, Damage = damage, Deaths = deaths.AsParallelWriter()
            };
            apply.ScheduleParallel(query, aggregate).Complete();
            var dead = manager.GetComponentData<RecoilBattleEcsState>(enemy);
            Require(damage[1] == 40 && dead.Health == 0 && dead.Active == 0 && dead.Target == Entity.Null &&
                deaths.Length == 1 && deaths[0] == 1,
                "Simultaneous hits must sum, clamp health and emit exactly one death event.");
            Require(manager.GetComponentData<RecoilBattleEcsState>(own).Health == 95 &&
                manager.GetComponentData<RecoilBattleEcsState>(survivor).Health == 80 &&
                manager.GetComponentData<RecoilBattleEcsState>(unrelated).Health == 100,
                "Chunk damage must affect only mapped victims and preserve untouched units.");
            for (var i = 0; i < 3; i++) attacks[i] = default;
            deaths.Clear();
            aggregate = new RecoilAggregateDamageJob { Attacks = attacks, Damage = damage, Count = 3 }.Schedule();
            apply.ScheduleParallel(query, aggregate).Complete();
            Require(deaths.Length == 0 && manager.GetComponentData<RecoilBattleEcsState>(own).Health == 95,
                "The next frame must clear old damage and must not emit duplicate deaths.");
        }

        private static void ValidateAliveFlags()
        {
            using var world = new World("Recoil bulk alive checks");
            var manager = world.EntityManager;
            var alive = manager.CreateEntity(typeof(RecoilBattleEcsState));
            var dead = manager.CreateEntity(typeof(RecoilBattleEcsState));
            var pooled = manager.CreateEntity(typeof(RecoilBattleEcsState));
            var destroyed = manager.CreateEntity(typeof(RecoilBattleEcsState));
            manager.SetComponentData(alive, new RecoilBattleEcsState { Active = 1, Health = 100 });
            manager.SetComponentData(dead, new RecoilBattleEcsState { Active = 1, Health = 0 });
            manager.SetComponentData(pooled, new RecoilBattleEcsState { Active = 0, Health = 100 });
            using var indices = new NativeParallelHashMap<Entity, int>(4, Allocator.TempJob);
            indices.Add(alive, 0); indices.Add(dead, 1); indices.Add(pooled, 2); indices.Add(destroyed, 3);
            manager.DestroyEntity(destroyed);
            using var flags = new NativeArray<byte>(4, Allocator.TempJob);
            using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<RecoilBattleEcsState>());
            void Capture()
            {
                var reset = new RecoilResetAliveFlagsJob { Alive = flags }.Schedule(4, 64);
                new RecoilCaptureAliveJob
                {
                    Entities = manager.GetEntityTypeHandle(),
                    States = manager.GetComponentTypeHandle<RecoilBattleEcsState>(true),
                    Indices = indices, Alive = flags
                }.ScheduleParallel(query, reset).Complete();
            }
            Capture();
            Require(flags[0] == 1 && flags[1] == 0 && flags[2] == 0 && flags[3] == 0,
                "Bulk lifecycle capture must distinguish alive, dead, pooled and destroyed entities.");
            manager.SetComponentData(dead, new RecoilBattleEcsState { Active = 1, Health = 100 });
            manager.DestroyEntity(alive);
            Capture();
            Require(flags[0] == 0 && flags[1] == 1,
                "Bulk lifecycle flags must reset stale entries and recognize reused units.");
        }

        private static void ValidateCloseAcquisition()
        {
            using var world = new World("Recoil close acquisition checks");
            var manager = world.EntityManager;
            var archetype = manager.CreateArchetype(typeof(RecoilBattleEcsState), typeof(RecoilUnitNavigation),
                typeof(RecoilUnitWaypoint));
            var own = manager.CreateEntity(archetype);
            var enemy = manager.CreateEntity(archetype);
            manager.SetComponentData(own, new RecoilBattleEcsState { Team = 0, Health = 100, Active = 1 });
            manager.SetComponentData(enemy, new RecoilBattleEcsState { Team = 1, Health = 100, Active = 1 });
            manager.SetComponentData(own, new RecoilUnitNavigation
            { Active = 1, HasDestination = 1, PathPending = 1, RequestVersion = 3, MovementVersion = 5 });
            manager.GetBuffer<RecoilUnitWaypoint>(own).Add(new RecoilUnitWaypoint { Position = new float3(20f, 0f, 0f) });
            using var query = manager.CreateEntityQuery(ComponentType.ReadWrite<RecoilBattleEcsState>(),
                ComponentType.ReadWrite<RecoilUnitNavigation>(), ComponentType.ReadWrite<RecoilUnitWaypoint>());
            using var indices = new NativeParallelHashMap<Entity, int>(2, Allocator.TempJob);
            indices.Add(own, 0); indices.Add(enemy, 1);
            using var ownedEntities = new NativeArray<Entity>(2, Allocator.TempJob);
            var entities = ownedEntities;
            entities[0] = own; entities[1] = enemy;
            using var ownedSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(2, Allocator.TempJob);
            var snapshots = ownedSnapshots;
            snapshots[0] = new RecoilBattleCombatSnapshot
            { Team = 0, Active = 1, Health = 100, CurrentTargetIndex = -1, SearchTarget = 0 };
            snapshots[1] = new RecoilBattleCombatSnapshot
            { Team = 1, Active = 1, Health = 100, CurrentTargetIndex = -1, Position = new float3(1f, 0f, 0f) };
            using var cells = new NativeParallelMultiHashMap<int3, int>(2, Allocator.TempJob);
            cells.Add(new int3(0, 0, 0), 0); cells.Add(new int3(0, 0, 1), 1);
            using var attacks = new NativeArray<RecoilContactAttack>(2, Allocator.TempJob);
            var job = new RecoilCloseTargetJob
            {
                Entities = manager.GetEntityTypeHandle(), States = manager.GetComponentTypeHandle<RecoilBattleEcsState>(false),
                Navigations = manager.GetComponentTypeHandle<RecoilUnitNavigation>(false),
                Waypoints = manager.GetBufferTypeHandle<RecoilUnitWaypoint>(false), Indices = indices,
                SnapshotEntities = entities, Soldiers = snapshots, Cells = cells, CellSize = 4f,
                RangeSquared = 16f, MeleeAttackRangeSquared = 2.25f,
                ArcherAttackRangeSquared = 2.25f, SiegeAttackRangeSquared = 2.25f,
                Attacks = attacks, CombatDelta = 0.1f, AttackInterval = 0.65f, AttackDamage = 20
            };
            job.ScheduleParallel(query, default).Complete();
            Require(manager.GetComponentData<RecoilBattleEcsState>(own).Target == enemy &&
                manager.GetComponentData<RecoilBattleEcsState>(enemy).Target == own,
                "Both sides must acquire a close enemy even when the slow search budget is unavailable.");
            Require(attacks[0].TargetIndex == 1 && attacks[0].Damage == 20 &&
                attacks[1].TargetIndex == 0 && attacks[1].Damage == 20,
                "Contact attacks must occur on both sides without the slow combat scheduler.");
            var navigation = manager.GetComponentData<RecoilUnitNavigation>(own);
            Require(navigation.HasDestination == 0 && navigation.PathPending == 0 &&
                navigation.MovementVersion == 6 && manager.GetBuffer<RecoilUnitWaypoint>(own).Length == 0,
                "Contact must stop strategic movement and invalidate stale path and movement results.");
            job.ScheduleParallel(query, default).Complete();
            Require(attacks[0].Damage == 0 && attacks[1].Damage == 0,
                "Cooldown must prevent contact damage from being emitted every frame.");
            Require(manager.GetComponentData<RecoilUnitNavigation>(own).MovementVersion == 6,
                "Holding contact must not invalidate movement every frame.");
            job.CombatDelta = 0.65f;
            job.ScheduleParallel(query, default).Complete();
            Require(attacks[0].Damage == 20 && attacks[1].Damage == 20,
                "Contact attacks must resume after cooldown expires.");
        }

        private static void ValidateIncrementalGrid()
        {
            using var owned = new NativeArray<RecoilBattleCombatSnapshot>(4, Allocator.TempJob);
            var snapshots = owned;
            using var cells = new NativeParallelMultiHashMap<int3, int>(4, Allocator.TempJob);
            using var previous = new NativeArray<int3>(4, Allocator.TempJob);
            using var active = new NativeArray<byte>(4, Allocator.TempJob);
            using var metadata = new NativeArray<int>(2, Allocator.TempJob);
            snapshots[0] = new RecoilBattleCombatSnapshot { Active = 1, Health = 100, Team = 0 };
            snapshots[1] = new RecoilBattleCombatSnapshot
            { Active = 1, Health = 100, Team = 1, Position = new float3(10f, 0f, 0f) };
            var job = new RecoilUpdateCombatGridJob
            { Snapshots = snapshots, Cells = cells, PreviousCells = previous, PreviousActive = active,
                Metadata = metadata, Count = 2, CellSize = 4f };
            job.Schedule().Complete();
            Require(cells.Count() == 2 && metadata[1] == 2, "New units must enter the incremental grid.");
            var unit = snapshots[0];
            unit.Position.x = 0.5f;
            snapshots[0] = unit;
            job.Schedule().Complete();
            Require(metadata[1] == 0 && cells.Count() == 2, "Movement inside the same cell must not mutate the grid.");
            unit.Position.x = 5f;
            snapshots[0] = unit;
            job.Schedule().Complete();
            Require(metadata[1] == 2 && !cells.ContainsKey(new int3(0, 0, 0)) && cells.ContainsKey(new int3(1, 0, 0)),
                "Crossing a cell boundary must remove the old entry and add the new entry.");
            var enemy = snapshots[1];
            enemy.Active = 0; enemy.Health = 0;
            snapshots[1] = enemy;
            job.Schedule().Complete();
            Require(metadata[1] == 1 && cells.Count() == 1, "Dead or pooled units must leave the grid.");
            enemy.Active = 1; enemy.Health = 100; enemy.Position.x = 12f;
            snapshots[1] = enemy;
            job.Schedule().Complete();
            Require(metadata[1] == 1 && cells.Count() == 2 && cells.ContainsKey(new int3(3, 0, 1)),
                "Reused units must re-enter at their new spawn position.");
            snapshots[0] = enemy; snapshots[1] = unit;
            job.Schedule().Complete();
            Require(cells.TryGetFirstValue(new int3(3, 0, 1), out var index, out _) && index == 0,
                "Reordering snapshot indices must not leave stale cell occupants.");
            job.Count = 1;
            job.Schedule().Complete();
            Require(cells.Count() == 1 && !cells.ContainsKey(new int3(1, 0, 0)),
                "Shrinking the army must remove trailing grid indices.");
            job.Count = 0;
            job.Schedule().Complete();
            Require(cells.Count() == 0, "An empty army must leave an empty grid.");
        }

        private static void ValidateChunkSnapshot()
        {
            using var world = new World("Recoil chunk snapshot checks");
            var manager = world.EntityManager;
            var archetype = manager.CreateArchetype(typeof(RecoilBattleEcsState), typeof(RecoilUnitPose),
                typeof(RecoilUnitCombatTiming));
            var own = manager.CreateEntity(archetype);
            var enemy = manager.CreateEntity(archetype);
            manager.CreateEntity(archetype); // Not owned by this battle: must not be written to the snapshot.
            manager.SetComponentData(own, new RecoilBattleEcsState
            { Team = 0, Health = 80, Active = 1, Target = enemy, AttackCooldown = 0.3f });
            manager.SetComponentData(enemy, new RecoilBattleEcsState { Team = 1, Health = 100, Active = 1 });
            manager.SetComponentData(own, new RecoilUnitPose { Position = new float3(2f, 1f, 2f) });
            manager.SetComponentData(enemy, new RecoilUnitPose { Position = new float3(3f, 1f, 2f) });
            using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<RecoilBattleEcsState>(),
                ComponentType.ReadOnly<RecoilUnitPose>(), ComponentType.ReadOnly<RecoilUnitCombatTiming>());
            using var indices = new NativeParallelHashMap<Entity, int>(2, Allocator.TempJob);
            indices.Add(own, 1);
            indices.Add(enemy, 0); // Deliberately remap entities out of chunk order.
            using var snapshots = new NativeArray<RecoilBattleCombatSnapshot>(2, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int3, int>(2, Allocator.TempJob);
            var job = new RecoilBuildCombatSnapshotJob
            {
                Entities = manager.GetEntityTypeHandle(),
                States = manager.GetComponentTypeHandle<RecoilBattleEcsState>(true),
                Poses = manager.GetComponentTypeHandle<RecoilUnitPose>(true),
                Timings = manager.GetComponentTypeHandle<RecoilUnitCombatTiming>(true),
                Indices = indices, Snapshots = snapshots, Cells = cells.AsParallelWriter(),
                Now = 1f, DeltaTime = 0.1f, InitialDelta = 0.1f, CellSize = 4f
            };
            job.ScheduleParallel(query, default).Complete();
            Require(snapshots[1].Health == 80 && snapshots[1].CurrentTargetIndex == 0 &&
                snapshots[1].SearchTarget == 0 && snapshots[0].SearchTarget == 1,
                "Chunk snapshot must preserve entity mapping, health and target retention.");
            Require(cells.Count() == 2 && cells.ContainsKey(new int3(0, 0, 0)) && cells.ContainsKey(new int3(0, 0, 1)),
                "Parallel grid must contain only this battle's units, separated by team.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
