using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;

namespace RecoilPathfinding
{
    // Runs the actual targeting kernel on small, deterministic inputs without a scene or GameObjects.
    public static class RecoilBattleCombatValidation
    {
        private struct ProjectileGridCellCollector : IRecoilProjectileGridCellVisitor
        {
            public HashSet<long> Cells;

            public void VisitCell(int x, int z) => Cells.Add(((long)x << 32) ^ (uint)z);
        }

        public static void Run()
        {
            ValidateTargetSelection();
            ValidateDamageResolution();
            ValidateProjectileHandling();
            ValidateEcsLifecycleAndCastle();
            ValidateGridAndSnapshotOptimizations();
        }

        public static void ValidateTargetSelection()
        {
            ValidateCloseAcquisition();
            ValidateCloseAvoidedTargetReplacement();
            ValidateCloseSearchBeyondCrowdedCell();
            ValidateConvertedTargetWeights();
            ValidateRecentAttackerTargetSelection();
            ValidateStrategicLineOfFireSelection();
            ValidateTargetSearchKernel();
            ValidateUncappedPriorityTargetSearch();
        }

        public static void ValidateDamageResolution()
        {
            ValidateUnitStats();
            ValidateDamagePipeline();
            ValidateLastAttackerAttribution();
        }

        public static void ValidateProjectileHandling()
        {
            var unitOrigin = new float3(1f, 1f, 1f);
            var muzzleOrigin = new float3(1.2f, 2.1f, 1.4f);
            Require(math.distancesq(RecoilCombatProjectileTrajectory.SelectOrigin(
                    unitOrigin, muzzleOrigin, true), muzzleOrigin) == 0f &&
                math.distancesq(RecoilCombatProjectileTrajectory.SelectOrigin(
                    unitOrigin, muzzleOrigin, false), unitOrigin) == 0f,
                "Simulated projectiles must launch from the presentation muzzle when present and fall back to the ECS pose otherwise.");
            ValidateProjectileSweep();
            ValidateProjectileLeading();
            ValidateProjectileGridTraversal();
            ValidateTerrainLineOfFire();
            ValidateProjectileImpactDamageTiming();
        }

        private static void ValidateProjectileImpactDamageTiming()
        {
            using var world = new World("Recoil projectile impact timing checks");
            var manager = world.EntityManager;
            var targetEntity = manager.CreateEntity(typeof(RecoilBattleEcsState), typeof(RecoilUnitPose),
                typeof(RecoilUnitCombatTiming));
            manager.SetComponentData(targetEntity, new RecoilBattleEcsState
                { Team = 1, Active = 1, Health = 100 });
            manager.SetComponentData(targetEntity, new RecoilUnitPose
            {
                Position = new float3(13f, 1f, 0f), PreviousPosition = new float3(13f, 1f, 0f)
            });

            var root = new GameObject("Recoil projectile impact timing validation");
            root.SetActive(false);
            RecoilAutoBattleDemo battle = null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var battleType = typeof(RecoilAutoBattleDemo);
            var soldierType = battleType.GetNestedType("Soldier", flags);
            try
            {
                battle = root.AddComponent<RecoilAutoBattleDemo>();
                void Set(string name, object value) => battleType.GetField(name, flags).SetValue(battle, value);
                Set("_world", world);
                Set("_entityManager", manager);
                Set("_hasEntityManager", true);
                Set("_clockDriven", true);
                Set("_combatRosterDirty", true);
                Set("_projectileGridStale", true);
                Set("_blueCastleHealthRemaining", 1000);
                Set("_redCastleHealthRemaining", 1000);

                var soldier = Activator.CreateInstance(soldierType, true);
                soldierType.GetField("Entity").SetValue(soldier, targetEntity);
                soldierType.GetField("IsBlue").SetValue(soldier, false);
                ((IList)battleType.GetField("_combatSoldiers", flags).GetValue(battle)).Add(soldier);
                battleType.GetMethod("RefreshCombatRoster", flags).Invoke(battle, null);

                var projectileType = battleType.GetNestedType("PendingCombatProjectile", flags);
                var projectile = Activator.CreateInstance(projectileType);
                projectileType.GetField("FlightStartTime").SetValue(projectile, 0f);
                projectileType.GetField("LastProcessedTime").SetValue(projectile, 0f);
                projectileType.GetField("TravelDuration").SetValue(projectile, 1f);
                projectileType.GetField("FlightStartPoint").SetValue(projectile, new Vector3(0f, 1f, 0f));
                projectileType.GetField("ArcHeight").SetValue(projectile, 0f);
                projectileType.GetField("ImpactTime").SetValue(projectile, 1f);
                projectileType.GetField("ImpactPoint").SetValue(projectile, new Vector3(10f, 1f, 0f));
                projectileType.GetField("IntendedTarget").SetValue(projectile, targetEntity);
                projectileType.GetField("Team").SetValue(projectile, 0);
                projectileType.GetField("Damage").SetValue(projectile, 10);
                ((IList)battleType.GetField("_pendingCombatProjectiles", flags).GetValue(battle)).Add(projectile);

                var process = battleType.GetMethod("ProcessPendingCombatProjectiles", flags);
                Set("_tickTime", .4f);
                process.Invoke(battle, null);
                Require(manager.GetComponentData<RecoilBattleEcsState>(targetEntity).Health == 100 &&
                    battle.PendingCombatProjectileCount == 1,
                    "A projectile must remain in flight without applying damage before its swept path reaches the target.");

                var movedPose = manager.GetComponentData<RecoilUnitPose>(targetEntity);
                movedPose.PreviousPosition = movedPose.Position;
                movedPose.Position.x = 5f;
                manager.SetComponentData(targetEntity, movedPose);
                Set("_tickTime", 1f);
                process.Invoke(battle, null);
                Require(manager.GetComponentData<RecoilBattleEcsState>(targetEntity).Health == 90 &&
                    battle.PendingCombatProjectileCount == 0,
                    "Projectile processing must refresh moved target cells before sweeping, then damage the ECS target exactly when the flight reaches its body.");
            }
            finally
            {
                if (battle != null) SetEntityManagerUnavailable(battle, battleType, flags);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void SetEntityManagerUnavailable(RecoilAutoBattleDemo battle, Type battleType,
            BindingFlags flags)
        {
            battleType.GetField("_hasEntityManager", flags).SetValue(battle, false);
        }

        public static void ValidateEcsLifecycleAndCastle()
        {
            ValidateTeamApproachProjection();
            ValidateCombatRegions();
            ValidateCastleMarchGeometry();
            ValidateEcsResolve();
            ValidateMultipleCastleAttackers();
            ValidateAliveFlags();
            ValidateRosterIndices();
            ValidateStaleSimulationAccess();
        }

        private static void ValidateCombatRegions()
        {
            using var owned = new NativeArray<RecoilBattleCombatSnapshot>(5, Allocator.Temp);
            var soldiers = owned;
            soldiers[0] = new RecoilBattleCombatSnapshot { Active = 1, Health = 100, Team = 0, Position = new float3(0, 0, 0) };
            soldiers[1] = new RecoilBattleCombatSnapshot { Active = 1, Health = 100, Team = 1, Position = new float3(10, 0, 0) };
            soldiers[2] = new RecoilBattleCombatSnapshot { Active = 1, Health = 100, Team = 1, Position = new float3(10, 0, 50) };
            soldiers[3] = new RecoilBattleCombatSnapshot { Active = 1, Health = 0, Team = 1, Position = new float3(500, 0, 500) };
            soldiers[4] = new RecoilBattleCombatSnapshot { Active = 0, Health = 100, Team = 1, Position = new float3(-500, 0, -500) };
            var regions = RecoilCombatRegions.Build(soldiers, soldiers.Length);
            var blue = 0;
            var red = 0;
            for (var i = 0; i < regions.Length; i++)
            {
                if (regions[i] < 0) continue;
                Require(regions[i] < 3, "Dead or inactive soldiers must not become regional pursuit targets.");
                if (i < 64) blue++; else red++;
            }
            Require(blue == 1 && red == 2, "Separated enemy groups must retain separate regional pursuit targets.");
            soldiers[1] = default;
            regions = RecoilCombatRegions.Build(soldiers, soldiers.Length);
            for (var i = 64; i < regions.Length; i++)
                Require(regions[i] != 1, "Regional pursuit targets must be rebuilt after a unit leaves combat.");
        }

        private static void ValidateTeamApproachProjection()
        {
            using var ownedHeights = new NativeArray<float>(6, Allocator.Temp);
            using var ownedBlue = new NativeArray<int>(2, Allocator.Temp);
            using var ownedRed = new NativeArray<int>(2, Allocator.Temp);
            using var ownedStarts = new NativeArray<int>(2, Allocator.Temp);
            var heights = ownedHeights; var blue = ownedBlue; var red = ownedRed; var starts = ownedStarts;
            for (var i = 0; i < heights.Length; i++) heights[i] = 2f;
            blue[0] = 0; blue[1] = -1; red[0] = -1; red[1] = 0; starts[0] = 0; starts[1] = 2;
            var field = new RecoilTerrainHeightField { Heights = heights, Columns = 3, Rows = 2,
                Size = new float2(20f, 10f), BlueCellLeaves = blue, RedCellLeaves = red,
                BlueLeafTriangleStarts = starts, RedLeafTriangleStarts = starts };
            Require(field.TryFindUnitApproach(new float3(5f, 3f, 5f), new float3(15f, 3f, 5f), 1, out var blueApproach) &&
                blueApproach.x > 5f && blueApproach.x < 10f && math.abs(blueApproach.y - 2f) < 0.0001f,
                "Blue pursuit must choose its own walkable side of a restricted enemy destination at ground height.");
            Require(field.TryFindUnitApproach(new float3(15f, 3f, 5f), new float3(5f, 3f, 5f), 2, out var redApproach) &&
                redApproach.x >= 10f && redApproach.x < 15f && math.abs(redApproach.y - 2f) < 0.0001f,
                "Red pursuit must preserve its area restrictions as well.");
            Require(field.TryFindUnitApproach(new float3(2f, 3f, 5f), new float3(5f, 9f, 5f), 1, out var normal) &&
                math.distance(normal, new float3(5f, 2f, 5f)) < 0.0001f,
                "An already walkable target must retain its XZ while correcting vertical visual offsets.");
        }

        public static void ValidateGridAndSnapshotOptimizations()
        {
            ValidateOccupiedCircleMass();
            ValidateIncrementalGrid();
            ValidateBatchedGridOrder();
            ValidateChunkSnapshot();
            ValidateTargetSearchBudget();
            ValidateTargetRefresh();
        }

        private static void ValidateTargetRefresh()
        {
            using var world = new World("Recoil target refresh checks");
            var manager = world.EntityManager;
            var unit = manager.CreateEntity(typeof(RecoilBattleEcsState));
            var target = manager.CreateEntity(typeof(RecoilBattleEcsState));
            manager.SetComponentData(unit, new RecoilBattleEcsState { Target = target, Active = 1, Health = 100 });
            using var ownedEntities = new NativeArray<Entity>(2, Allocator.TempJob);
            using var scheduled = new NativeArray<int>(1, Allocator.TempJob);
            using var ownedSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(2, Allocator.TempJob);
            using var indices = new NativeParallelHashMap<Entity, int>(2, Allocator.TempJob);
            var entities = ownedEntities;
            var snapshots = ownedSnapshots;
            entities[0] = unit; entities[1] = target;
            indices.Add(unit, 0); indices.Add(target, 1);
            snapshots[0] = new RecoilBattleCombatSnapshot { Entity = unit, Active = 1, Health = 100,
                Stats = new RecoilUnitCombatStats { AvoidTargetCategoryMask = 1 } };
            snapshots[1] = new RecoilBattleCombatSnapshot { Entity = target, Active = 1, Health = 100,
                SearchTarget = 7, Stats = new RecoilUnitCombatStats { TargetCategoryMask = 1 } };
            var phase = unit.Index % RecoilCloseTargetJob.SlowTargetUpdateRate;
            var job = new RecoilRefreshCombatTargetsJob { Entities = entities, Scheduled = scheduled,
                Indices = indices, Snapshots = snapshots, Count = 1, SlowPhase = phase, Now = 1f };
            var system = world.GetOrCreateSystemManaged<RecoilCombatResolveSystem>();
            system.ScheduleRefreshTargets(job).Complete();
            Require(snapshots[0].CurrentTargetIndex == 1 && snapshots[0].SearchTarget == 1 && snapshots[1].SearchTarget == 7,
                "Target refresh must preserve close-target acquisition and leave unscheduled snapshots untouched.");
            job.SlowPhase = (phase + 1) % RecoilCloseTargetJob.SlowTargetUpdateRate;
            system.ScheduleRefreshTargets(job).Complete();
            Require(snapshots[0].SearchTarget == 0, "Avoided targets must wait for their normal slow-update phase.");
            var dead = snapshots[1]; dead.Health = 0; snapshots[1] = dead;
            system.ScheduleRefreshTargets(job).Complete();
            Require(snapshots[0].SearchTarget == 1, "Dead targets must request replacement outside the slow-update phase.");
            dead.Health = 100; snapshots[1] = dead;
            var restricted = snapshots[0]; restricted.Stats.OnlyTargetCategoryMask = 2; snapshots[0] = restricted;
            system.ScheduleRefreshTargets(job).Complete();
            Require(snapshots[0].SearchTarget == 1, "Disallowed categories must request replacement immediately.");
            manager.SetComponentData(unit, new RecoilBattleEcsState { Target = Entity.Null, Active = 1, Health = 100 });
            system.ScheduleRefreshTargets(job).Complete();
            Require(snapshots[0].CurrentTargetIndex == -1 && snapshots[0].SearchTarget == 0,
                "Units without a target must respect their search phase.");
            job.SlowPhase = phase;
            system.ScheduleRefreshTargets(job).Complete();
            Require(snapshots[0].SearchTarget == 1, "Units without a target must search when their phase is due.");
            restricted = snapshots[0]; restricted.NextTargetSearchTime = 2f; snapshots[0] = restricted;
            system.ScheduleRefreshTargets(job).Complete();
            Require(snapshots[0].SearchTarget == 0, "Refreshing a target must retain its cooldown.");
        }

        private static void ValidateTargetSearchBudget()
        {
            const int unitCount = 8;
            using var ownedSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(unitCount, Allocator.TempJob);
            var snapshots = ownedSnapshots;
            using var ownedScheduled = new NativeArray<int>(unitCount, Allocator.TempJob);
            var scheduled = ownedScheduled;
            var blueCursor = 0;
            var redCursor = 0;
            for (var i = 0; i < unitCount; i++)
            {
                snapshots[i] = new RecoilBattleCombatSnapshot
                { Team = i < unitCount / 2 ? 0 : 1, SearchTarget = 1 };
                scheduled[i] = i;
            }

            var deferred = RecoilTargetSearchBudget.Apply(snapshots, scheduled,
                unitCount, 4, ref blueCursor, ref redCursor);
            var firstSelection = new bool[unitCount];
            var firstSelectedCount = 0;
            for (var i = 0; i < unitCount; i++)
            {
                firstSelection[i] = snapshots[i].SearchTarget != 0;
                if (firstSelection[i]) firstSelectedCount++;
                snapshots[i] = new RecoilBattleCombatSnapshot
                { Team = i < unitCount / 2 ? 0 : 1, SearchTarget = 1 };
            }
            Require(deferred == unitCount - 4 && firstSelectedCount == 4 &&
                blueCursor > 0 && redCursor > unitCount / 2,
                "Target-search scheduling must enforce its cap and allocate work to both teams.");

            deferred = RecoilTargetSearchBudget.Apply(snapshots, scheduled,
                unitCount, 4, ref blueCursor, ref redCursor);
            var secondSelectedCount = 0;
            var selectedNewBlue = false;
            var selectedNewRed = false;
            for (var i = 0; i < unitCount; i++)
            {
                if (snapshots[i].SearchTarget == 0) continue;
                secondSelectedCount++;
                if (!firstSelection[i] && i < unitCount / 2) selectedNewBlue = true;
                if (!firstSelection[i] && i >= unitCount / 2) selectedNewRed = true;
            }
            Require(deferred == unitCount - 4 && secondSelectedCount == 4 &&
                selectedNewBlue && selectedNewRed,
                "Deferred target searches must rotate fairly for both teams on the next budget window.");
        }

        private static void ValidateTargetSearchKernel()
        {
            using var ownedSoldiers = new NativeArray<RecoilBattleCombatSnapshot>(5, Allocator.TempJob);
            var soldiers = ownedSoldiers;
            using var ownedScheduled = new NativeArray<int>(1, Allocator.TempJob);
            var scheduled = ownedScheduled;
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
                DetectionRangeSquared = 24f * 24f,
                AttackRangeSquared = 9f, AttackInterval = 1f, AttackDamage = 20, BlueCount = 2, RedCount = 2
            };
            job.Execute(0);
            Require(results[0].TargetIndex == 2 && results[0].Damage == 20 && results[0].CandidateChecks == 1 &&
                results[0].TargetWeightEvaluations == 1 && results[0].TargetSearchPerformed == 1,
                "Local search must find the nearby enemy, ignore friendly cells and attack in range.");
            soldiers[4] = new RecoilBattleCombatSnapshot
            {
                Position = new float3(5f, 0f, 0f), Team = 1, Health = 100, Active = 1,
                Stats = new RecoilUnitCombatStats { TargetPriority = 1 }
            };
            cells.Add(new int3(1, 0, 1), 4);
            job.Execute(0);
            Require(results[0].TargetIndex == 2 && results[0].CandidateChecks == 2 &&
                results[0].TargetWeightEvaluations == 1 && results[0].TargetPriorityWeightSkips == 1,
                "Worse-priority candidates must still count toward diagnostics while skipping the expensive weight calculation.");
            using var coarseCounts = new NativeParallelHashMap<int3, int>(3, Allocator.TempJob);
            coarseCounts.Add(new int3(0, 0, 0), 2);
            coarseCounts.Add(new int3(0, 0, 1), 1);
            coarseCounts.Add(new int3(6, 0, 1), 1);
            var baseline = results[0];
            job.TeamCellCounts = coarseCounts;
            job.Execute(0);
            Require(results[0].TargetIndex == baseline.TargetIndex &&
                results[0].CandidateChecks == baseline.CandidateChecks && results[0].Damage == baseline.Damage,
                "Quad occupancy must preserve candidate order, chosen target and damage.");

            var own = soldiers[0];
            own.CurrentTargetIndex = 2;
            own.SearchTarget = 0;
            soldiers[0] = own;
            job.Execute(0);
            Require(results[0].TargetIndex == 2 && results[0].CandidateChecks == 0 &&
                results[0].TargetSearchPerformed == 0,
                "A valid target must be retained without a grid query.");

            own.Stats.AvoidTargetCategoryMask = 1 << 4;
            own.SearchTarget = 1;
            soldiers[0] = own;
            var enemy = soldiers[2];
            enemy.Stats.TargetCategoryMask = 1 << 4;
            soldiers[2] = enemy;
            var preferred = soldiers[4];
            preferred.Active = 0;
            soldiers[4] = preferred;
            job.Execute(0);
            Require(results[0].TargetIndex == 2 && results[0].TargetSearchPerformed == 1,
                "A scheduled category search must retain the avoided target when no eligible alternative exists.");

            preferred.Active = 1;
            preferred.Stats.TargetPriority = 0;
            soldiers[4] = preferred;
            var counts = coarseCounts;
            counts[new int3(1, 0, 1)] = 1;
            job.Execute(0);
            Require(results[0].TargetIndex == 4 && results[0].TargetSearchPerformed == 1,
                "A scheduled search must replace an existing lower-priority target when an eligible target is available.");
            own.Stats = default;
            own.SearchTarget = 0;
            soldiers[0] = own;
            enemy.Stats = default;
            soldiers[2] = enemy;

            enemy = soldiers[2];
            enemy.Active = 0;
            soldiers[2] = enemy;
            job.Execute(0);
            Require(results[0].TargetIndex == -1 && results[0].CandidateChecks == 0,
                "A dead target must be dropped without bypassing the search schedule.");
            preferred.Active = 0;
            soldiers[4] = preferred;
            own.SearchTarget = 1;
            soldiers[0] = own;
            job.Execute(0);
            Require(results[0].TargetIndex == -1,
                "A distant enemy must not be selected outside detection range.");

        }

        private static void ValidateUncappedPriorityTargetSearch()
        {
            const int enemyCount = 129;
            using var ownedSoldiers = new NativeArray<RecoilBattleCombatSnapshot>(enemyCount + 1,
                Allocator.TempJob);
            var soldiers = ownedSoldiers;
            soldiers[0] = new RecoilBattleCombatSnapshot
            {
                Position = new float3(.1f, 0f, .1f), Team = 0, Health = 100, Active = 1,
                CurrentTargetIndex = -1, SearchTarget = 1
            };
            using var scheduled = new NativeArray<int>(1, Allocator.TempJob);
            using var results = new NativeArray<RecoilBattleCombatResult>(1, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int3, int>(enemyCount, Allocator.TempJob);
            using var cellCounts = new NativeParallelHashMap<int3, int>(1, Allocator.TempJob);
            cellCounts.Add(new int3(0, 0, 1), enemyCount);
            for (var i = 0; i < enemyCount; i++)
            {
                soldiers[i + 1] = new RecoilBattleCombatSnapshot
                {
                    Position = new float3(1f + i * .001f, 0f, 1f), Team = 1, Health = 100, Active = 1,
                    Stats = new RecoilUnitCombatStats { TargetPriority = i == enemyCount - 1 ? -1 : 1 }
                };
                cells.Add(new int3(0, 0, 1), i + 1);
            }
            var job = new RecoilBattleCombatJob
            {
                TeamCellCounts = cellCounts,
                Soldiers = soldiers,
                ScheduledSoldiers = scheduled,
                Cells = cells,
                Results = results,
                MinCellX = -10, MaxCellX = 10, MinCellZ = -10, MaxCellZ = 10,
                CellSize = 4f, DetectionRangeSquared = 100f,
                AttackRangeSquared = 1f, AttackInterval = 1f, AttackDamage = 10,
                BlueCount = 1, RedCount = enemyCount
            };
            job.Execute(0);
            Require(results[0].CandidateChecks == enemyCount &&
                results[0].TargetIndex == enemyCount,
                "A crowded local grid must scan past the old 128-candidate cap and select the best Recoil target priority.");
        }

        private static void ValidateConvertedTargetWeights()
        {
            Require(!RecoilCombatTargetWeight.IsPreferredCandidate(0, true, 1f, 1f,
                    0, false, 1000f, 100f) &&
                RecoilCombatTargetWeight.IsPreferredCandidate(0, false, 1000f, 100f,
                    0, true, 1f, 1f),
                "At equal game TargetPriority, Recoil's eligible-category fallback must precede score and distance.");
            Require(RecoilCombatTargetWeight.IsPreferredCandidate(0, true, 1000f, 100f,
                    1, false, 1f, 1f),
                "The game's explicit TargetPriority must remain stronger than Recoil's category fallback and score.");

            var attacker = new RecoilUnitCombatStats { Damage = 20, AttackSpeed = 1f, MaxHealth = 100 };
            var target = new float3(0f, 0f, 10f);
            var position = float3.zero;
            var forward = quaternion.identity;
            var facing = RecoilCombatTargetWeight.Calculate(position, target, forward, attacker, 100, 100f, 2f, 1f);
            var cachedFacing = RecoilCombatFacing.FlatForward(forward);
            var cachedDamagePerSecond = RecoilCombatTargetWeight.DamagePerSecond(attacker, 1f);
            var cachedWeight = RecoilCombatTargetWeight.Calculate(position, target, cachedFacing,
                cachedDamagePerSecond, 100, 100f, 2f);
            var turnedAway = RecoilCombatTargetWeight.Calculate(position, target, quaternion.RotateY(math.PI), attacker, 100, 100f, 2f, 1f);
            var damaged = RecoilCombatTargetWeight.Calculate(position, target, forward, attacker, 1, 100f, 2f, 1f);
            var far = RecoilCombatTargetWeight.Calculate(position, target, forward, attacker, 100, 400f, 2f, 1f);
            Require(math.abs(facing - cachedWeight) < .0001f && facing < turnedAway && damaged < facing && facing < far,
                "Target ranking must prefer facing, lower remaining health and shorter distance with otherwise equal candidates.");
            var immediatelyAttackable = RecoilCombatTargetWeight.Calculate(position,
                new float3(0f, 0f, 1.9f), forward, attacker, 100, 1.9f * 1.9f, 2f, 1f);
            var outsideWeaponRange = RecoilCombatTargetWeight.Calculate(position,
                new float3(0f, 0f, 2.1f), forward, attacker, 100, 2.1f * 2.1f, 2f, 1f);
            Require(outsideWeaponRange > immediatelyAttackable * 1000f,
                "A target outside weapon range must receive Recoil's strong priority penalty while remaining selectable for advance movement.");
            Require(RecoilCombatTargetWeight.DamagePerSecond(default, 1f) == 1f,
                "Unconfigured units must retain the fallback DPS used by the target weight.");
            var singleShot = RecoilCombatTargetWeight.Calculate(position, target, forward, attacker, 100, 100f, 2f, 1f);
            attacker.SalvoSize = 3;
            var salvo = RecoilCombatTargetWeight.Calculate(position, target, forward, attacker, 100, 100f, 2f, 1f);
            Require(salvo > singleShot,
                "Recoil target ranking must include all shots in a salvo when estimating weapon damage per second.");

            var armoredTarget = new RecoilUnitCombatStats
                { MaxHealth = 100, Armor = .8f, SecondaryArmor = .1f };
            var exposedTarget = new RecoilUnitCombatStats
                { MaxHealth = 100, Armor = .1f, SecondaryArmor = .8f };
            attacker.SecondaryDamage = false;
            var physicalAgainstArmor = RecoilCombatTargetWeight.Calculate(position, target, cachedFacing,
                cachedDamagePerSecond, 100, 100f, 2f,
                RecoilCombatTargetWeight.DamageMultiplier(attacker, armoredTarget));
            var physicalAgainstExposed = RecoilCombatTargetWeight.Calculate(position, target, cachedFacing,
                cachedDamagePerSecond, 100, 100f, 2f,
                RecoilCombatTargetWeight.DamageMultiplier(attacker, exposedTarget));
            attacker.SecondaryDamage = true;
            var magicAgainstArmor = RecoilCombatTargetWeight.Calculate(position, target, cachedFacing,
                cachedDamagePerSecond, 100, 100f, 2f,
                RecoilCombatTargetWeight.DamageMultiplier(attacker, armoredTarget));
            var magicAgainstExposed = RecoilCombatTargetWeight.Calculate(position, target, cachedFacing,
                cachedDamagePerSecond, 100, 100f, 2f,
                RecoilCombatTargetWeight.DamageMultiplier(attacker, exposedTarget));
            Require(physicalAgainstArmor > physicalAgainstExposed && magicAgainstArmor < magicAgainstExposed,
                "Target ranking must divide by the attacker's matching physical or secondary damage multiplier.");
            attacker.SecondaryDamage = false;
            var physicalMultiplier = RecoilCombatTargetWeight.DamageMultiplier(attacker, armoredTarget);
            attacker.SecondaryDamage = true;
            var secondaryMultiplier = RecoilCombatTargetWeight.DamageMultiplier(attacker, armoredTarget);
            Require(math.abs(physicalMultiplier - RecoilUnitDamage.Calculate(100, armoredTarget, false) / 100f) < .0001f &&
                math.abs(secondaryMultiplier - RecoilUnitDamage.Calculate(100, armoredTarget, true) / 100f) < .0001f,
                "Target score damage efficiency must use the same armor rules as resolved damage.");
            var fullyArmored = armoredTarget;
            fullyArmored.SecondaryArmor = 1f;
            Require(math.isfinite(RecoilCombatTargetWeight.Calculate(position, target, cachedFacing,
                    cachedDamagePerSecond, 100, 100f, 2f,
                    RecoilCombatTargetWeight.DamageMultiplier(attacker, fullyArmored))),
                "Fully resisted damage must keep target scores finite, matching Recoil's minimum damage factor.");

            var attackerEntity = new Entity { Index = 17, Version = 1 };
            var lastAttackerCandidate = new RecoilBattleCombatSnapshot { Entity = attackerEntity };
            var retaliatingUnit = new RecoilBattleCombatSnapshot
            { LastAttacker = attackerEntity, RecentAttackerValid = 1 };
            var retaliationMultiplier = RecoilCombatTargetWeight.PriorityMultiplier(retaliatingUnit,
                lastAttackerCandidate);
            var ordinaryMultiplier = RecoilCombatTargetWeight.PriorityMultiplier(
                default, lastAttackerCandidate);
            Require(retaliationMultiplier == RecoilCombatTargetWeight.RecentAttackerPriorityMultiplier &&
                ordinaryMultiplier == 1f && retaliationMultiplier < ordinaryMultiplier,
                "A recently damaging enemy must receive Recoil's 0.5 target-weight multiplier, which expires outside the recent-attacker window.");
            var tick = RecoilFixedTickAccumulator.Step;
            Require(RecoilCombatTargetWeight.IsRecentAttacker(attackerEntity, 0f,
                    (RecoilCombatTargetWeight.RecentAttackerWindowTicks - 1) * tick) &&
                !RecoilCombatTargetWeight.IsRecentAttacker(attackerEntity, 0f,
                    RecoilCombatTargetWeight.RecentAttackerWindowTicks * tick) &&
                !RecoilCombatTargetWeight.IsRecentAttacker(attackerEntity, tick, 0f),
                "The retaliation preference must expire at Recoil's exact 200-frame boundary and must ignore future attacker timestamps.");
        }

        private static void ValidateLastAttackerAttribution()
        {
            using var world = new World("Recoil last attacker attribution checks");
            var manager = world.EntityManager;
            var attacker = manager.CreateEntity(typeof(RecoilBattleEcsState));
            var target = manager.CreateEntity(typeof(RecoilBattleEcsState));
            manager.SetComponentData(attacker, new RecoilBattleEcsState { Team = 0, Active = 1, Health = 100 });
            manager.SetComponentData(target, new RecoilBattleEcsState { Team = 1, Active = 1, Health = 100 });
            using var ownedSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(2, Allocator.TempJob);
            var snapshots = ownedSnapshots;
            snapshots[0] = new RecoilBattleCombatSnapshot
                { Entity = attacker, Team = 0, Active = 1, Health = 100, Stats = new RecoilUnitCombatStats { MaxHealth = 100 } };
            snapshots[1] = new RecoilBattleCombatSnapshot
                { Entity = target, Team = 1, Active = 1, Health = 100, Stats = new RecoilUnitCombatStats { MaxHealth = 100 } };
            using var ownedEntities = new NativeArray<Entity>(2, Allocator.TempJob);
            var entities = ownedEntities;
            entities[0] = attacker; entities[1] = target;
            using var ownedAttacks = new NativeArray<RecoilContactAttack>(2, Allocator.TempJob);
            var attacks = ownedAttacks;
            attacks[0] = new RecoilContactAttack { TargetIndex = 1, Damage = 10 };
            using var damage = new NativeArray<int>(2, Allocator.TempJob);
            using var lastAttackers = new NativeArray<int>(2, Allocator.TempJob);
            using var indices = new NativeParallelHashMap<Entity, int>(2, Allocator.TempJob);
            indices.Add(attacker, 0); indices.Add(target, 1);
            using var cells = new NativeParallelMultiHashMap<int3, int>(2, Allocator.TempJob);
            using var deaths = new NativeList<int>(2, Allocator.TempJob);
            using var query = manager.CreateEntityQuery(ComponentType.ReadWrite<RecoilBattleEcsState>());
            var aggregate = new RecoilAggregateDamageJob
            {
                Attacks = attacks, Damage = damage, LastAttackerByTarget = lastAttackers,
                Count = 2, Soldiers = snapshots, Cells = cells
            }.Schedule();
            new RecoilApplyDamageJob
            {
                Entities = manager.GetEntityTypeHandle(), States = manager.GetComponentTypeHandle<RecoilBattleEcsState>(false),
                Indices = indices, Damage = damage, LastAttackerByTarget = lastAttackers,
                SnapshotEntities = entities, Now = 3.5f, Deaths = deaths.AsParallelWriter()
            }.ScheduleParallel(query, aggregate).Complete();
            var result = manager.GetComponentData<RecoilBattleEcsState>(target);
            Require(result.Health == 90 && result.LastAttacker == attacker &&
                math.abs(result.LastAttackedTime - 3.5f) < 0.0001f,
                "Resolved contact damage must store the actual damaging unit and timestamp for the retaliation target preference.");
        }

        private static void ValidateRecentAttackerTargetSelection()
        {
            using var ownedSoldiers = new NativeArray<RecoilBattleCombatSnapshot>(3, Allocator.TempJob);
            var soldiers = ownedSoldiers;
            var attackerEntity = new Entity { Index = 41, Version = 1 };
            var candidateAEntity = new Entity { Index = 42, Version = 1 };
            var candidateBEntity = new Entity { Index = 43, Version = 1 };
            soldiers[0] = new RecoilBattleCombatSnapshot
            {
                Entity = attackerEntity, Position = float3.zero, Team = 0, Health = 100,
                Active = 1, CurrentTargetIndex = -1, SearchTarget = 1,
                Stats = new RecoilUnitCombatStats { Damage = 10, AttackSpeed = 1, Range = 20, MaxHealth = 100 }
            };
            var candidate = new RecoilBattleCombatSnapshot
            {
                Position = new float3(5, 0, 0), Team = 1, Health = 100, Active = 1,
                Stats = new RecoilUnitCombatStats { MaxHealth = 100 }
            };
            candidate.Entity = candidateAEntity;
            soldiers[1] = candidate;
            candidate.Entity = candidateBEntity;
            soldiers[2] = candidate;

            using var ownedScheduled = new NativeArray<int>(1, Allocator.TempJob);
            var scheduled = ownedScheduled;
            scheduled[0] = 0;
            using var results = new NativeArray<RecoilBattleCombatResult>(1, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int3, int>(4, Allocator.TempJob);
            cells.Add(new int3(0, 0, 1), 1);
            cells.Add(new int3(0, 0, 1), 2);
            var job = new RecoilBattleCombatJob
            {
                Soldiers = soldiers, ScheduledSoldiers = scheduled, Cells = cells, Results = results,
                MinCellX = -1, MaxCellX = 5, MinCellZ = -5, MaxCellZ = 5, CellSize = 4,
                DetectionRangeSquared = 20 * 20,
                AttackRangeSquared = 4, AttackInterval = 1, AttackDamage = 10, BlueCount = 1, RedCount = 2
            };
            job.Execute(0);
            var initialTarget = results[0].TargetIndex;
            Require(initialTarget == 1 || initialTarget == 2,
                "The combat job must acquire one of two equally ranked nearby enemies before retaliation preference is applied.");

            var preferredTarget = initialTarget == 1 ? 2 : 1;
            var owner = soldiers[0];
            owner.LastAttacker = soldiers[preferredTarget].Entity;
            owner.RecentAttackerValid = 1;
            soldiers[0] = owner;
            job.Execute(0);
            Require(results[0].TargetIndex == preferredTarget,
                "The actual combat target selection job must prefer a recently damaging enemy when candidates are otherwise tied.");

            owner.RecentAttackerValid = 0;
            owner.LastAttacker = Entity.Null;
            owner.Stats.AvoidTargetCategoryMask = 1 << 4;
            soldiers[0] = owner;
            candidate = soldiers[1];
            candidate.Health = 10000;
            candidate.Stats.TargetCategoryMask = 0;
            soldiers[1] = candidate;
            candidate = soldiers[2];
            candidate.Health = 1;
            candidate.Stats.TargetCategoryMask = 1 << 4;
            soldiers[2] = candidate;
            job.Execute(0);
            Require(results[0].TargetIndex == 1 && results[0].AvoidedCategoryCandidates == 1 &&
                results[0].AvoidedCategoryTargetSelected == 0,
                "An eligible target category must outrank an avoided category even when the avoided target has a better damage-weight score.");

            candidate = soldiers[1];
            candidate.Active = 0;
            soldiers[1] = candidate;
            job.Execute(0);
            Require(results[0].TargetIndex == 2 && results[0].AvoidedCategoryTargetSelected == 1,
                "An avoided target category must remain a fallback when no eligible target can be acquired.");
        }

        private static void ValidateProjectileSweep()
        {
            Require(RecoilProjectileSweep.TryGetHitTime(new float2(-2f, 0f), new float2(2f, 0f),
                    float2.zero, .25f, out var hitTime) && math.abs(hitTime - .4375f) < .001f,
                "Projectile sweep must detect a target crossed between ticks and report first body contact.");
            Require(!RecoilProjectileSweep.TryGetHitTime(new float2(-2f, 0f), new float2(2f, 0f),
                    new float2(0f, 1f), .25f, out _),
                "Projectile sweep must reject targets outside its collision radius.");
            Require(RecoilProjectileSweep.TryGetBodyHitTime(new float3(-1f, 0f, 0f),
                    new float3(1f, 0f, 0f), float3.zero, .25f, .75f, .08f, out var bodyHit) &&
                math.abs(bodyHit - .335f) < .002f,
                "Projectile body sweeps must detect horizontal contact inside the unit's vertical body volume.");
            Require(!RecoilProjectileSweep.TryGetBodyHitTime(new float3(-1f, 2f, 0f),
                    new float3(1f, 2f, 0f), float3.zero, .25f, .75f, .08f, out _),
                "A high projectile passing above a unit must not deal a ground-level hit.");
            var relativeStart = new float3(-2f, 0f, 2f);
            var relativeEnd = new float3(2f, 0f, -2f);
            Require(RecoilProjectileSweep.TryGetBodyHitTime(relativeStart, relativeEnd,
                    float3.zero, .25f, .75f, .08f, out var movingBodyHit) &&
                movingBodyHit > 0f && movingBodyHit < 1f,
                "Projectile sweeps must detect a moving unit crossing the flight path within the same tick.");
            Require(RecoilProjectileSweep.TryGetHitTime(float2.zero, float2.zero, new float2(.2f, 0f), .25f, out _),
                "A stationary projectile segment must still resolve an overlap.");
            Require(!RecoilProjectileSweep.TryGetHitTime(float2.zero, float2.zero, new float2(.3f, 0f), .25f, out _) &&
                RecoilProjectileSweep.TryGetHitTime(new float2(-1f, .25f), new float2(1f, .25f),
                    float2.zero, .25f, out _),
                "A zero-length miss and a tangent sweep must be classified correctly.");
            Require(RecoilProjectileSweep.IsWithinSplash(100f, .5f, true) &&
                RecoilProjectileSweep.IsWithinSplash(.25f, .5f, false) &&
                !RecoilProjectileSweep.IsWithinSplash(.2501f, .5f, false),
                "Direct body hits must always receive damage while splash-only hits obey the configured radius.");
            Require(math.abs(RecoilProjectileSweep.SplashDamageMultiplier(2f, 4f) - .5f) < .001f &&
                RecoilProjectileSweep.SplashDamageMultiplier(4f, 4f) < .001f &&
                RecoilProjectileSweep.SplashDamageMultiplier(0f, 4f) == 1f &&
                RecoilProjectileSweep.SplashDamageMultiplier(2f, 4f, 1f) == 1f &&
                RecoilProjectileSweep.SplashDamageMultiplier(2f, 4f, .5f) > .5f &&
                RecoilProjectileSweep.SplashDamageMultiplier(4.01f, 4f) == 0f,
                "Splash damage must use Recoil's default linear falloff from body surface to blast radius, and support configured edge effectiveness.");
            Require(RecoilProjectileSweep.CanDamageSplashTeam(0, 1) &&
                RecoilProjectileSweep.CanDamageSplashTeam(1, 0) &&
                !RecoilProjectileSweep.CanDamageSplashTeam(0, 0) &&
                !RecoilProjectileSweep.CanDamageSplashTeam(1, 1),
                "Two-team battle splash damage must stay enemy-only even though Recoil weapons can be configured for friendly fire.");
            Require(RecoilExplosionDamageSchedule.BucketIndex(127) == 127 &&
                RecoilExplosionDamageSchedule.BucketIndex(128) == 0 &&
                RecoilExplosionDamageSchedule.BucketIndex(257) == 1 &&
                RecoilExplosionDamageSchedule.FirstTickToProcess(10, 11) == 11 &&
                RecoilExplosionDamageSchedule.FirstTickToProcess(10, 300) == 173,
                "The 128-slot Recoil-style damage ring must wrap at the correct ticks and cover every bucket after a long simulation skip.");
            var defaultExplosionSpeed = RecoilCombatProjectileTrajectory.DefaultExplosionSpeed(20);
            Require(math.abs(defaultExplosionSpeed -
                    (8f + 30f * 2.5f) / (9f + math.sqrt(30f) * .7f) * .5f) < .0001f &&
                RecoilCombatProjectileTrajectory.SplashDamageDelaySeconds(defaultExplosionSpeed * 3.99f,
                    defaultExplosionSpeed) == 0f &&
                math.abs(RecoilCombatProjectileTrajectory.SplashDamageDelaySeconds(defaultExplosionSpeed * 4f,
                    defaultExplosionSpeed) - RecoilFixedTickAccumulator.Step) < .0001f &&
                math.abs(RecoilCombatProjectileTrajectory.SplashDamageDelaySeconds(defaultExplosionSpeed * 8f,
                    defaultExplosionSpeed) - 5f * RecoilFixedTickAccumulator.Step) < .0001f,
                "Explosion damage must match Recoil's default explosion-speed formula, direct four-frame threshold and frame-quantized outward delay.");
        }

        private static void ValidateProjectileLeading()
        {
            var origin = float3.zero;
            var stationary = new float3(10f, 0f, 0f);
            Require(RecoilCombatProjectileTrajectory.TryGetInterceptTime(origin, stationary,
                    float3.zero, 10f, out var stationaryTime) && math.abs(stationaryTime - 1f) < 0.0001f,
                "Projectile interception must reduce to distance divided by speed for a stationary target.");
            var lateralVelocity = new float3(0f, 0f, 5f);
            Require(RecoilCombatProjectileTrajectory.TryGetInterceptTime(origin, stationary,
                    lateralVelocity, 10f, out var lateralTime) &&
                math.abs(lateralTime - math.sqrt(4f / 3f)) < 0.0001f,
                "A moving target must be led to the first positive constant-speed intercept.");
            var lead = RecoilCombatProjectileTrajectory.LeadTargetPoint(origin, stationary,
                lateralVelocity, 10f);
            Require(math.abs(lead.x - 10f) < 0.0001f && math.abs(lead.z - lateralVelocity.z * lateralTime) < 0.0001f,
                "The projectile aim point must advance along the target's movement vector.");
            var fastRunner = new float3(20f, 0f, 0f);
            Require(!RecoilCombatProjectileTrajectory.TryGetInterceptTime(origin, stationary,
                    fastRunner, 10f, out _) &&
                math.distancesq(RecoilCombatProjectileTrajectory.LeadTargetPoint(origin, stationary,
                    fastRunner, 10f), stationary) < 0.0001f,
                "When no intercept exists, projectile aiming must fall back to the observed target point.");
        }

        private static void ValidateProjectileGridTraversal()
        {
            const float cellSize = 4f;
            const float searchRadius = 0.6f;
            var extent = (int)math.ceil(searchRadius / cellSize);
            var paths = new[]
            {
                new float2(-35f, -23f), new float2(39f, 31f),
                new float2(-20f, 0f), new float2(20f, 0f),
                new float2(0f, -20f), new float2(0f, 20f),
                new float2(24f, -24f), new float2(-24f, 24f),
                new float2(12f, 12f), new float2(-12f, -12f),
                new float2(-16f, 16f), new float2(16f, -16f)
            };
            var collector = new ProjectileGridCellCollector { Cells = new HashSet<long>() };
            for (var path = 0; path < paths.Length; path += 2)
            {
                collector.Cells.Clear();
                var start = paths[path];
                var end = paths[path + 1];
                RecoilProjectileGridTraversal.Visit(start, end, cellSize, extent, ref collector);
                for (var sample = 0; sample <= 128; sample++)
                {
                    var center = math.lerp(start, end, sample / 128f);
                    for (var ring = 0; ring <= 2; ring++)
                    {
                        var radius = searchRadius * ring / 2f;
                        for (var direction = 0; direction < 16; direction++)
                        {
                            var angle = direction * (2f * math.PI / 16f);
                            var point = center + new float2(math.cos(angle), math.sin(angle)) * radius;
                            var cell = (int2)math.floor(point / cellSize);
                            var key = ((long)cell.x << 32) ^ (uint)cell.y;
                            Require(collector.Cells.Contains(key),
                                "Projectile grid traversal must visit every sampled target cell within collision padding of the segment.");
                        }
                    }
                }
                if (path == 0)
                {
                    var min = (int2)math.floor((math.min(start, end) - searchRadius) / cellSize);
                    var max = (int2)math.floor((math.max(start, end) + searchRadius) / cellSize);
                    var rectangleCells = (max.x - min.x + 1) * (max.y - min.y + 1);
                    Require(collector.Cells.Count < rectangleCells,
                        "Long diagonal projectile paths must visit fewer unique cells than the surrounding rectangle.");
                }
            }
        }

        private static void ValidateOccupiedCircleMass()
        {
            Require(math.abs(RecoilMovementMass.FromOccupiedCircle(1f, .5f) - .25f) < .0001f &&
                math.abs(RecoilMovementMass.FromOccupiedCircle(1f, 1f) - 1f) < .0001f &&
                math.abs(RecoilMovementMass.FromOccupiedCircle(1f, 2f) - 4f) < .0001f &&
                math.abs(RecoilMovementMass.FromOccupiedCircle(2f, 2f) - 8f) < .0001f,
                "Movement mass must scale with occupied circle area while preserving prefab mass scaling.");
        }

        private static void ValidateTerrainLineOfFire()
        {
            using var heights = new NativeArray<float>(9, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var field = new RecoilTerrainHeightField
            {
                Heights = heights,
                Columns = 3,
                Rows = 3,
                Origin = float3.zero,
                Size = new float2(2f, 2f)
            };
            for (var z = 0; z <= 8; z++)
            for (var x = 0; x <= 8; x++)
            {
                var sample = new float2(x * 0.25f, z * 0.25f);
                Require(field.TryHeight(sample, out var sampledHeight) &&
                    math.abs(sampledHeight - field.Height(sample)) < 0.0001f,
                    "The optimized terrain sample must preserve bilinear height values across cell edges.");
            }
            Require(!field.TryHeight(new float2(-0.01f, 1f), out _),
                "The optimized terrain sampler must continue rejecting samples outside the baked field.");
            var start = new float3(.1f, 1f, 1f);
            var end = new float3(1.9f, 1f, 1f);
            Require(field.HasLineOfFire(start, end),
                "A flat baked terrain must preserve unobstructed combat line of fire.");
            var mutableHeights = heights;
            mutableHeights[4] = 2f;
            Require(!field.HasLineOfFire(start, end),
                "A raised terrain cell between the units must block line of fire.");
            Require(!field.HasLineOfFire(new float3(.9f, 1f, 1f), new float3(1.1f, 1f, 1f)),
                "A short shot crossing a narrow raised terrain point must still sample and block line of fire.");
            mutableHeights[4] = 2.5f;
            Require(field.TryFindProjectileTerrainHitTime(
                    new float3(.8f, 2.2f, 1f), new float3(1.2f, 2.2f, 1f), 0f, 0f, 1f, out var shortHit) &&
                shortHit > 0f && shortHit < 1f,
                "A short projectile segment must detect terrain between clear endpoints.");
            mutableHeights[4] = 2f;
            Require(field.TryFindProjectileTerrainHitTime(start, end, 0f, 0f, 1f, out var groundHit) &&
                groundHit > 0f && groundHit < 1f,
                "A projectile whose target moves behind a raised terrain cell must impact that terrain.");
            Require(field.HasLineOfFire(start, end, 2f),
                "A Recoil-style arcing shot must clear a ridge when its trajectory rises above it.");
            Require(!field.TryFindProjectileTerrainHitTime(start, end, 2f, 0f, 1f, out _),
                "A projectile arc that clears the ridge must not be stopped by terrain.");
            Require(!field.TryFindProjectileTerrainHitTime(
                    new float3(.1f, 3.5f, 1f), new float3(1.9f, 3.5f, 1f), 4f, .2f, .8f, out _),
                "A projectile segment whose minimum height is above the baked terrain maximum must skip terrain sampling without reporting a hit.");
            Require(!field.TryFindProjectileTerrainHitTime(
                    new float3(.1f, 3f, 0f), new float3(1.9f, 3f, 0f), 0f, 0f, 1f, out _),
                "A distant terrain peak must not prevent the terrain-height early-out over a locally flat projectile segment.");
            var arcPoint = RecoilCombatProjectileTrajectory.Position(start, end, .5f, 2f);
            Require(math.distancesq(arcPoint, new float3(1f, 3f, 1f)) < .0001f &&
                math.distancesq(RecoilCombatProjectileTrajectory.Position(start, end, 0f, 2f), start) < .0001f &&
                math.distancesq(RecoilCombatProjectileTrajectory.Position(start, end, 1f, 2f), end) < .0001f,
                "Combat and visual projectiles must share the same parabolic arc endpoints and midpoint.");
            Require(math.abs(RecoilCombatProjectileTrajectory.TravelDuration(24f, 24f) - 1f) < .0001f &&
                math.abs(RecoilCombatProjectileTrajectory.TravelDuration(.1f, 24f) - .05f) < .0001f,
                "Visual and combat projectiles must use the same horizontal travel speed and minimum duration.");
            Require(field.HasLineOfFire(start + new float3(0f, 3f, 0f), end + new float3(0f, 3f, 0f)),
                "A shot above the terrain ridge must retain a clear line of fire.");
            Require(field.HasLineOfFire(new float3(-5f, 1f, -5f), new float3(-3f, 1f, -3f)),
                "Unavailable height samples outside the baked map must not invent terrain blockers.");
        }

        private static void ValidateStrategicLineOfFireSelection()
        {
            using var ownedSoldiers = new NativeArray<RecoilBattleCombatSnapshot>(3, Allocator.TempJob);
            var soldiers = ownedSoldiers;
            soldiers[0] = new RecoilBattleCombatSnapshot
            {
                Position = new float3(.1f, 0f, 1f), Team = 0, Health = 100, Active = 1,
                CurrentTargetIndex = 1, SearchTarget = 1
            };
            soldiers[1] = new RecoilBattleCombatSnapshot
                { Position = new float3(1.9f, 0f, 1f), Team = 1, Health = 100, Active = 1 };
            soldiers[2] = new RecoilBattleCombatSnapshot
                { Position = new float3(.1f, 0f, .1f), Team = 1, Health = 100, Active = 1 };
            using var scheduled = new NativeArray<int>(1, Allocator.TempJob);
            using var results = new NativeArray<RecoilBattleCombatResult>(1, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int3, int>(3, Allocator.TempJob);
            cells.Add(new int3(0, 0, 0), 0);
            cells.Add(new int3(0, 0, 1), 1);
            cells.Add(new int3(0, 0, 1), 2);
            using var cellCounts = new NativeParallelHashMap<int3, int>(2, Allocator.TempJob);
            cellCounts.Add(new int3(0, 0, 0), 1);
            cellCounts.Add(new int3(0, 0, 1), 2);
            using var heights = new NativeArray<float>(9, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var mutableHeights = heights;
            mutableHeights[4] = 2f;
            using var rangedUnits = new NativeArray<byte>(3, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var job = new RecoilBattleCombatJob
            {
                TeamCellCounts = cellCounts,
                Soldiers = soldiers,
                ScheduledSoldiers = scheduled,
                Cells = cells,
                RangedUnits = rangedUnits,
                Heights = new RecoilTerrainHeightField
                {
                    Heights = heights, Columns = 3, Rows = 3,
                    Origin = float3.zero, Size = new float2(2f, 2f)
                },
                Results = results,
                MinCellX = -1, MaxCellX = 1, MinCellZ = -1, MaxCellZ = 1,
                CellSize = 4f, DetectionRangeSquared = 16f,
                AttackRangeSquared = 4f, AttackInterval = 1f, AttackDamage = 10,
                BlueCount = 1, RedCount = 2
            };
            job.Execute(0);
            Require(results[0].TargetIndex == 2 && results[0].DamageTargetIndex == 2 &&
                results[0].Damage == 10 && results[0].CandidateChecks == 2 &&
                results[0].LineOfFireChecks >= 3 && results[0].LineOfFireBlocked >= 2,
                "Strategic target search must discard a terrain-blocked retained target and attack a clear enemy in range.");

            soldiers[0] = new RecoilBattleCombatSnapshot
            {
                Position = new float3(.1f, 0f, 1f), Team = 0, Health = 100, Active = 1,
                CurrentTargetIndex = 1, SearchTarget = 1
            };
            soldiers[1] = new RecoilBattleCombatSnapshot
            {
                Position = new float3(3.9f, 0f, 1f), Team = 1, Health = 100, Active = 1,
                Stats = new RecoilUnitCombatStats { TargetPriority = 0 }
            };
            soldiers[2] = new RecoilBattleCombatSnapshot
            {
                Position = new float3(1.9f, 0f, 1f), Team = 1, Health = 100, Active = 1,
                Stats = new RecoilUnitCombatStats { TargetPriority = 1 }
            };
            job.Execute(0);
            Require(results[0].TargetIndex == 1 && results[0].TargetPriorityWeightSkips == 1 &&
                results[0].LineOfFireChecks == 1,
                "Strategic search must skip terrain LOS for candidates whose TargetPriority is already worse than the retained target.");
        }

        private static void ValidateCastleMarchGeometry()
        {
            var goal = new float3(1000f, 0f, 0f);
            var march = new RecoilCastleMarchTarget
            { Origin = float3.zero, HasCastleBounds = 1, CastleCenter = goal, CastleExtents = new float3(20f, 10f, 30f) };
            var position = new float3(0f, 0f, 12f);
            Require(RecoilCastleMarch.TryStraightDestination(position, goal, march, out var destination) &&
                math.abs(destination.x - 830.5f) < .01f && math.abs(destination.z - 12f) < .001f,
                "Castle march must preserve its lane and measure 150m from the wall, not the castle center.");
            Require(!RecoilCastleMarch.TryStraightDestination(new float3(830f, 0f, 12f), goal, march, out _),
                "The 150m gate must enter normal pathfinding without requiring arrival at the castle.");
            var mirror = march; mirror.CastleCenter = -goal;
            Require(RecoilCastleMarch.TryStraightDestination(-position, -goal, mirror, out var mirrored) &&
                math.distancesq(destination, -mirrored) < .00001f,
                "Straight march must be symmetric for the two armies.");
            var flank = new float3(0f, 0f, 60f);
            Require(RecoilCastleMarch.TryStraightDestination(flank, goal, march, out var flankGate) &&
                math.abs(flankGate.z - flank.z) < .001f &&
                math.abs(RecoilCastleMarch.DistanceToBounds(flankGate.xz, goal.xz, march.CastleExtents.xz) - 149.5f) < .03f,
                "Flank lanes must use distance to the nearest castle boundary.");
            var meleePadding = RecoilCastleMarch.AttackApproachPadding(.5f, 4.25f, 1.5f);
            var meleePoint = RecoilCastleMarch.AttackApproachPoint(
                new float3(831f, 0f, 12f), march, meleePadding);
            Require(math.abs(meleePadding - 1.35f) < .001f &&
                RecoilCastleMarch.DistanceToBounds(meleePoint.xz, march.CastleCenter.xz,
                    march.CastleExtents.xz) <= 1.5f,
                "A coarse baked cell must not place melee units outside their castle attack range.");
            march.HasCastleBounds = 0;
            Require(RecoilCastleMarch.TryStraightDestination(position, goal, march, out var fallback) && fallback.x > 850f,
                "Maps without castle bounds must fall back to the enemy spawn as the reference.");
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
            using var scheduled = new NativeArray<int>(1, Allocator.TempJob);
            using var rangedUnits = new NativeArray<byte>(2, Allocator.TempJob);
            using var requests = new NativeList<int>(1, Allocator.TempJob);
            using var heights = new NativeArray<float>(4, Allocator.TempJob);
            using var leaves = new NativeArray<int>(0, Allocator.TempJob);
            using var starts = new NativeArray<int>(0, Allocator.TempJob);
            var entities = ownedEntities; var snapshots = ownedSnapshots; var results = ownedResults;
            entities[0] = unit; entities[1] = target;
            snapshots[0] = new RecoilBattleCombatSnapshot { Active = 1, Health = 100, SearchTarget = 1 };
            snapshots[1] = new RecoilBattleCombatSnapshot { Active = 1, Health = 100, Team = 1, Position = new float3(10f, 0f, 0f) };
            results[0] = new RecoilBattleCombatResult { TargetIndex = 1 };
            var job = new RecoilResolveCombatJob
            {
                Entities = entities, Scheduled = scheduled, Snapshots = snapshots, Results = results,
                RangedUnits = rangedUnits,
                PathRequests = requests.AsParallelWriter(), Now = 5f, SearchInterval = 0.4f, AttackRange = 2f,
                RetargetDistance = 2f, SoldierCount = 2, BlueGoal = new float3(50f, 0f, 50f),
                Heights = new RecoilTerrainHeightField { Heights = heights, CellLeaves = leaves, LeafTriangleStarts = starts,
                    Columns = 2, Rows = 2, Size = new float2(100f, 100f) }
            };
            var system = world.GetOrCreateSystemManaged<RecoilCombatResolveSystem>();
            system.ScheduleResolve(job, 1).Complete();
            Require(manager.GetComponentData<RecoilBattleEcsState>(unit).Target == target &&
                manager.GetComponentData<RecoilUnitOrder>(unit).Kind == RecoilUnitOrderKind.Attack &&
                manager.GetComponentData<RecoilUnitNavigation>(unit).HasDestination == 1 && requests.Length == 1,
                "Units must approach targets outside attack range without attacking while moving.");
            Require(math.abs(manager.GetComponentData<RecoilUnitCombatTiming>(unit).NextTargetSearchTime - 5.4f) < 0.001f,
                "ECS resolve must advance target search timing.");
            requests.Clear();
            var snapshot = snapshots[1]; snapshot.Position = new float3(0.5f, 0f, 0f); snapshots[1] = snapshot;
            job.Now = 6f;
            system.ScheduleResolve(job, 1).Complete();
            Require(manager.GetComponentData<RecoilUnitNavigation>(unit).HasDestination == 0 &&
                manager.GetComponentData<RecoilUnitPose>(unit).Speed == 0f && requests.Length == 0,
                "Targets in attack range must stop ECS movement before attacking.");
            var stoppedVersion = manager.GetComponentData<RecoilUnitNavigation>(unit).MovementVersion;
            job.Now = 6.1f;
            system.ScheduleResolve(job, 1).Complete();
            Require(manager.GetComponentData<RecoilUnitNavigation>(unit).MovementVersion == stoppedVersion &&
                manager.GetComponentData<RecoilUnitNavigation>(unit).RequestVersion == 1,
                "A unit holding attack range must keep its stopped path versions stable across combat ticks.");
            var navigation = manager.GetComponentData<RecoilUnitNavigation>(unit);
            navigation.HasDestination = 1; navigation.PathPending = 1;
            manager.SetComponentData(unit, navigation);
            manager.GetBuffer<RecoilUnitWaypoint>(unit).Add(new RecoilUnitWaypoint { Position = new float3(4f, 0f, 0f) });
            results[0] = new RecoilBattleCombatResult { TargetIndex = -1 };
            job.Now = 7f;
            system.ScheduleResolve(job, 1).Complete();
            Require(manager.GetBuffer<RecoilUnitWaypoint>(unit).Length == 1 &&
                manager.GetComponentData<RecoilUnitNavigation>(unit).PathPending == 1 && requests.Length == 0,
                "Combat resolution must not cancel a unit's existing movement path behind a departure gate.");
            results[0] = new RecoilBattleCombatResult { TargetIndex = -1 }; job.Now = 8f;
            system.ScheduleResolve(job, 1).Complete();
            Require(manager.GetComponentData<RecoilBattleEcsState>(unit).Target == Entity.Null &&
                manager.GetComponentData<RecoilUnitOrder>(unit).Kind == RecoilUnitOrderKind.Move && requests.Length == 1,
                "Units without a local target must advance to the strategic destination through ECS.");
            requests.Clear();
            navigation = manager.GetComponentData<RecoilUnitNavigation>(unit); navigation.PathPending = 1;
            manager.SetComponentData(unit, navigation);
            manager.GetBuffer<RecoilUnitWaypoint>(unit).Add(new RecoilUnitWaypoint { Position = new float3(4f, 0f, 0f) });
            snapshot.Position = new float3(20f, 0f, 0f); snapshots[1] = snapshot;
            results[0] = new RecoilBattleCombatResult { TargetIndex = 1 }; job.Now = 9f;
            system.ScheduleResolve(job, 1).Complete();
            Require(requests.Length == 0 &&
                manager.GetComponentData<RecoilUnitNavigation>(unit).HasDestination == 1 &&
                manager.GetComponentData<RecoilUnitNavigation>(unit).PathPending == 1 &&
                manager.GetComponentData<RecoilUnitNavigation>(unit).Destination.x == 20f,
                "Units outside attack range must keep approaching without duplicate path requests.");

            // The strategic goal takes priority when the castle itself is in weapon range,
            // even if combat targeting supplied a valid nearby soldier.
            manager.SetComponentData(unit, new RecoilBattleEcsState
                { Active = 1, Health = 100, Team = 0, Target = target });
            manager.SetComponentData(target, new RecoilBattleEcsState
                { Active = 1, Health = 100, Team = 1 });
            manager.SetComponentData(unit, new RecoilUnitPose { Position = new float3(38f, 0f, 0f) });
            manager.SetComponentData(unit, new RecoilUnitNavigation { Active = 1, CastleApproach = 1 });
            snapshots[0] = new RecoilBattleCombatSnapshot
                { Active = 1, Health = 100, Team = 0, Position = new float3(38f, 0f, 0f) };
            snapshots[1] = new RecoilBattleCombatSnapshot
                { Active = 1, Health = 100, Team = 1, Position = new float3(38f, 0f, 0f) };
            results[0] = new RecoilBattleCombatResult { TargetIndex = 1 };
            job.BlueMarchTarget = new RecoilCastleMarchTarget
                { HasCastleBounds = 1, CastleCenter = new float3(50f, 0f, 0f), CastleExtents = new float3(10f, 5f, 10f) };
            job.AttackRange = 2f;
            job.Now = 9.5f;
            system.ScheduleResolve(job, 1).Complete();
            var focusedCastleState = manager.GetComponentData<RecoilBattleEcsState>(unit);
            Require(focusedCastleState.AttackingCastle != 0 && focusedCastleState.Target == Entity.Null &&
                manager.GetComponentData<RecoilUnitOrder>(unit).Kind == RecoilUnitOrderKind.Attack &&
                requests.Length == 0,
                "A soldier in castle range must switch from a nearby soldier to the castle objective.");

            // With no local enemy, march on the current forward lane before routing to the castle.
            requests.Clear();
            manager.SetComponentData(unit, new RecoilUnitNavigation { Active = 1 });
            manager.SetComponentData(unit, new RecoilUnitCombatTiming());
            manager.SetComponentData(unit, new RecoilUnitPose { Position = new float3(0f, 0f, 12f) });
            results[0] = new RecoilBattleCombatResult { TargetIndex = -1 };
            job.BlueGoal = new float3(1000f, 0f, 0f);
            job.BlueMarchTarget = new RecoilCastleMarchTarget
            { Origin = float3.zero, HasCastleBounds = 1, CastleCenter = job.BlueGoal, CastleExtents = new float3(20f, 10f, 30f) };
            job.Now = 10f;
            system.ScheduleResolve(job, 1).Complete();
            var order = manager.GetComponentData<RecoilUnitOrder>(unit);
            Require(math.abs(order.Destination.z - 12f) < .001f && math.abs(order.Destination.x - 830.5f) < .01f &&
                manager.GetComponentData<RecoilUnitNavigation>(unit).CastleApproach == 0 && requests.Length == 1,
                "Far units must march straight to 150m before the castle boundary without early spread toward spawn.");
            requests.Clear();
            manager.SetComponentData(unit, new RecoilUnitPose { Position = new float3(831f, 0f, 12f) });
            manager.SetComponentData(unit, new RecoilUnitNavigation { Active = 1 });
            manager.SetComponentData(unit, new RecoilUnitCombatTiming());
            job.Now = 11f;
            system.ScheduleResolve(job, 1).Complete();
            order = manager.GetComponentData<RecoilUnitOrder>(unit);
            Require(manager.GetComponentData<RecoilUnitNavigation>(unit).CastleApproach == 1 && order.Destination.x > 900f,
                "Inside 150m, units must switch to routing toward the enemy spawn region.");
            requests.Clear();
            manager.SetComponentData(unit, new RecoilUnitPose { Position = float3.zero });
            job.Now = 12f;
            system.ScheduleResolve(job, 1).Complete();
            Require(manager.GetComponentData<RecoilUnitOrder>(unit).Destination.x > 900f,
                "Castle approach must not oscillate back to the straight-march stage.");

            manager.SetComponentData(unit, new RecoilBattleEcsState { Active = 1, Health = 100, Team = 0 });
            manager.SetComponentData(unit, new RecoilUnitPose
            { Position = new float3(979f, 0f, 0f), Speed = 1f, Velocity = new float3(1f, 0f, 0f) });
            manager.SetComponentData(unit, new RecoilUnitNavigation
            { Active = 1, HasDestination = 1, PathPending = 1, CastleApproach = 1 });
            manager.GetBuffer<RecoilUnitWaypoint>(unit).Add(new RecoilUnitWaypoint { Position = new float3(990f, 0f, 0f) });
            results[0] = new RecoilBattleCombatResult { TargetIndex = -1 };
            job.Now = 13f;
            system.ScheduleResolve(job, 1).Complete();
            var castleState = manager.GetComponentData<RecoilBattleEcsState>(unit);
            navigation = manager.GetComponentData<RecoilUnitNavigation>(unit);
            Require(castleState.AttackingCastle != 0 && manager.GetComponentData<RecoilUnitOrder>(unit).Kind ==
                RecoilUnitOrderKind.Attack && navigation.HasDestination == 0 && navigation.PathPending == 0 &&
                manager.GetBuffer<RecoilUnitWaypoint>(unit).Length == 0,
                "A unit inside castle attack range must cancel its march and enter the stationary castle attack state.");
            stoppedVersion = navigation.MovementVersion;
            var stoppedRequestVersion = navigation.RequestVersion;
            job.Now = 13.1f;
            system.ScheduleResolve(job, 1).Complete();
            navigation = manager.GetComponentData<RecoilUnitNavigation>(unit);
            Require(manager.GetComponentData<RecoilBattleEcsState>(unit).AttackingCastle != 0 &&
                navigation.MovementVersion == stoppedVersion && navigation.RequestVersion == stoppedRequestVersion,
                "Castle attack ticks must retain the attack state without repeatedly invalidating stopped paths.");
        }

        private static void ValidateMultipleCastleAttackers()
        {
            using var world = new World("Multiple Recoil castle attackers checks");
            var manager = world.EntityManager;
            var archetype = manager.CreateArchetype(typeof(RecoilBattleEcsState), typeof(RecoilUnitNavigation),
                typeof(RecoilUnitPose), typeof(RecoilUnitMovementSettings), typeof(RecoilUnitOrder),
                typeof(RecoilUnitCombatTiming), typeof(RecoilUnitWaypoint));
            using var ownedEntities = new NativeArray<Entity>(4, Allocator.TempJob);
            using var ownedSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(4, Allocator.TempJob);
            using var ownedResults = new NativeArray<RecoilBattleCombatResult>(2, Allocator.TempJob);
            using var ownedScheduled = new NativeArray<int>(2, Allocator.TempJob);
            using var ownedRangedUnits = new NativeArray<byte>(4, Allocator.TempJob);
            using var pathRequests = new NativeList<int>(2, Allocator.TempJob);
            var entities = ownedEntities;
            var snapshots = ownedSnapshots;
            var results = ownedResults;
            var scheduled = ownedScheduled;
            var rangedUnits = ownedRangedUnits;

            var positions = new[]
            {
                new float3(38f, 0f, 0f), new float3(62f, 0f, 0f),
                new float3(38f, 0f, 0f), new float3(62f, 0f, 0f)
            };
            for (var i = 0; i < entities.Length; i++)
            {
                entities[i] = manager.CreateEntity(archetype);
                var team = i < 2 ? 0 : 1;
                manager.SetComponentData(entities[i], new RecoilBattleEcsState
                    { Team = team, Health = 100, Active = 1 });
                manager.SetComponentData(entities[i], new RecoilUnitNavigation { Active = 1 });
                manager.SetComponentData(entities[i], new RecoilUnitPose { Position = positions[i] });
                manager.SetComponentData(entities[i], new RecoilUnitMovementSettings { BodyRadius = 0.5f });
                snapshots[i] = new RecoilBattleCombatSnapshot
                    { Position = positions[i], Team = team, Health = 100, Active = 1 };
            }

            scheduled[0] = 0;
            scheduled[1] = 1;
            results[0] = new RecoilBattleCombatResult { TargetIndex = 2 };
            results[1] = new RecoilBattleCombatResult { TargetIndex = 3 };
            var job = new RecoilResolveCombatJob
            {
                Entities = entities,
                Scheduled = scheduled,
                Snapshots = snapshots,
                Results = results,
                RangedUnits = rangedUnits,
                PathRequests = pathRequests.AsParallelWriter(),
                Now = 1f,
                SearchInterval = 0.5f,
                AttackRange = 2f,
                ArcherAttackRange = 36f,
                SiegeAttackRange = 54f,
                RetargetDistance = 2f,
                SoldierCount = 4,
                BlueMarchTarget = new RecoilCastleMarchTarget
                {
                    HasCastleBounds = 1,
                    CastleCenter = new float3(50f, 0f, 0f),
                    CastleExtents = new float3(10f, 5f, 10f)
                }
            };

            world.GetOrCreateSystemManaged<RecoilCombatResolveSystem>().ScheduleResolve(job, 2).Complete();
            for (var i = 0; i < 2; i++)
            {
                var state = manager.GetComponentData<RecoilBattleEcsState>(entities[i]);
                var order = manager.GetComponentData<RecoilUnitOrder>(entities[i]);
                Require(state.AttackingCastle != 0 && state.Target == Entity.Null &&
                    order.Kind == RecoilUnitOrderKind.Attack,
                    $"Castle focus resolution must include every scheduled soldier in range (soldier {i}).");
            }

            job.BlueAliveCount = job.RedAliveCount = 2;
            job.BlueRepresentative = 0;
            job.RedRepresentative = 2;
            var prioritySystem = world.GetOrCreateSystemManaged<RecoilCombatResolveSystem>();
            prioritySystem.ScheduleResolve(job, 2).Complete();
            for (var i = 0; i < 2; i++)
            {
                var state = manager.GetComponentData<RecoilBattleEcsState>(entities[i]);
                Require(state.AttackingCastle == 0 && state.Target == entities[i + 2],
                    "Living enemy soldiers must take priority even when the castle is in weapon range.");
            }
            // Spawned enemies can stand inside the castle's coarse bounds. They remain soldiers.
            var nearCastle = snapshots[2];
            nearCastle.Position = new float3(50f, 0f, 0f);
            snapshots[2] = nearCastle;
            job.RegionTargets = RecoilCombatRegions.Build(snapshots, snapshots.Length);
            results[0] = new RecoilBattleCombatResult { TargetIndex = -1 };
            results[1] = new RecoilBattleCombatResult { TargetIndex = -1 };
            prioritySystem.ScheduleResolve(job, 2).Complete();
            Require(manager.GetComponentData<RecoilBattleEcsState>(entities[0]).Target == entities[2],
                "Castle bounds must not discard living enemy soldiers and stall regional pursuit.");
            Require(manager.GetComponentData<RecoilUnitOrder>(entities[0]).Kind == RecoilUnitOrderKind.Attack &&
                manager.GetComponentData<RecoilBattleEcsState>(entities[0]).AttackingCastle == 0,
                "Pursuing a soldier near a castle must preserve the soldier-first rule.");
            job.RegionTargets = default;
            for (var i = 2; i < 4; i++)
            {
                var dead = snapshots[i]; dead.Active = 0; dead.Health = 0; snapshots[i] = dead;
            }
            job.RedAliveCount = 0;
            prioritySystem.ScheduleResolve(job, 2).Complete();
            for (var i = 0; i < 2; i++)
                Require(manager.GetComponentData<RecoilBattleEcsState>(entities[i]).AttackingCastle != 0,
                    "Castle attacks must unlock after the final enemy soldier dies.");
            job.BlueAliveCount = job.RedAliveCount = 0;

            // Use both real team conventions: each march target describes that team's enemy.
            job.BlueGoal = new float3(1000f, 0f, 0f);
            job.RedGoal = new float3(-1000f, 0f, 0f);
            job.BlueMarchTarget = new RecoilCastleMarchTarget
            { Origin = job.RedGoal, HasCastleBounds = 1, CastleCenter = job.BlueGoal, CastleExtents = new float3(10f, 5f, 10f) };
            job.RedMarchTarget = new RecoilCastleMarchTarget
            { Origin = job.BlueGoal, HasCastleBounds = 1, CastleCenter = job.RedGoal, CastleExtents = new float3(10f, 5f, 10f) };
            pathRequests.Clear();
            for (var i = 0; i < 2; i++)
            {
                var home = i == 0 ? job.RedGoal : job.BlueGoal;
                manager.SetComponentData(entities[i], new RecoilBattleEcsState { Team = i, Active = 1, Health = 100 });
                manager.SetComponentData(entities[i], new RecoilUnitNavigation { Active = 1 });
                manager.SetComponentData(entities[i], new RecoilUnitCombatTiming());
                manager.SetComponentData(entities[i], new RecoilUnitPose { Position = home });
                snapshots[i] = new RecoilBattleCombatSnapshot { Team = i, Active = 1, Health = 100, Position = home };
                results[i] = new RecoilBattleCombatResult { TargetIndex = -1 };
            }
            var system = world.GetOrCreateSystemManaged<RecoilCombatResolveSystem>();
            system.ScheduleResolve(job, 2).Complete();
            for (var i = 0; i < 2; i++)
            {
                var state = manager.GetComponentData<RecoilBattleEcsState>(entities[i]);
                var order = manager.GetComponentData<RecoilUnitOrder>(entities[i]);
                var home = i == 0 ? job.RedGoal : job.BlueGoal;
                Require(state.AttackingCastle == 0 && order.Kind == RecoilUnitOrderKind.Move &&
                    (i == 0 ? order.Destination.x > home.x : order.Destination.x < home.x),
                    $"Team {i} must march away from its own castle toward the enemy, rather than stop to attack home.");
            }
            Require(pathRequests.Length == 2, "Both teams at their home castles must request a march path.");
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
            using var noSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(0, Allocator.TempJob);
            using var noCells = new NativeParallelMultiHashMap<int3, int>(1, Allocator.TempJob);
            using var query = manager.CreateEntityQuery(ComponentType.ReadWrite<RecoilBattleEcsState>());
            var aggregate = new RecoilAggregateDamageJob { Attacks = attacks, Damage = damage, Count = 3, Soldiers = noSnapshots, Cells = noCells }.Schedule();
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
            aggregate = new RecoilAggregateDamageJob { Attacks = attacks, Damage = damage, Count = 3, Soldiers = noSnapshots, Cells = noCells }.Schedule();
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
                typeof(RecoilUnitWaypoint), typeof(RecoilUnitPose));
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
            {
                Team = 0, Active = 1, Health = 100, CurrentTargetIndex = -1, SearchTarget = 0,
                Stats = new RecoilUnitCombatStats { AvoidTargetCategoryMask = 1 << 4 }
            };
            snapshots[1] = new RecoilBattleCombatSnapshot
            {
                Team = 1, Active = 1, Health = 100, CurrentTargetIndex = -1,
                Position = new float3(1f, 0f, 0f),
                Stats = new RecoilUnitCombatStats { TargetCategoryMask = 1 << 4 }
            };
            using var cells = new NativeParallelMultiHashMap<int3, int>(2, Allocator.TempJob);
            cells.Add(new int3(0, 0, 0), 0); cells.Add(new int3(0, 0, 1), 1);
            using var attacks = new NativeArray<RecoilContactAttack>(2, Allocator.TempJob);
            using var ranged = new NativeArray<byte>(2, Allocator.TempJob);
            using var scheduledGenerations = new NativeArray<int>(2, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            using var teamCounts = new NativeParallelHashMap<int3, int>(2, Allocator.TempJob);
            teamCounts.Add(new int3(0, 0, 0), 1);
            teamCounts.Add(new int3(0, 0, 1), 1);
            var job = new RecoilCloseTargetJob
            {
                TeamCellCounts = teamCounts,
                Entities = manager.GetEntityTypeHandle(), States = manager.GetComponentTypeHandle<RecoilBattleEcsState>(false),
                Poses = manager.GetComponentTypeHandle<RecoilUnitPose>(false),
                Navigations = manager.GetComponentTypeHandle<RecoilUnitNavigation>(false),
                Waypoints = manager.GetBufferTypeHandle<RecoilUnitWaypoint>(false), Indices = indices,
                SnapshotEntities = entities, Soldiers = snapshots, Cells = cells, CellSize = 4f,
                ScheduledGenerations = scheduledGenerations, ScheduleGeneration = 1,
                RangeSquared = 16f, MeleeAttackRangeSquared = 2.25f,
                ArcherAttackRangeSquared = 2.25f, SiegeAttackRangeSquared = 2.25f,
                RangedUnits = ranged, Attacks = attacks, CombatDelta = 0.1f, AttackInterval = 0.65f, AttackDamage = 20
            };
            job.ScheduleParallel(query, default).Complete();
            Require(manager.GetComponentData<RecoilBattleEcsState>(own).Target == enemy &&
                manager.GetComponentData<RecoilBattleEcsState>(enemy).Target == own,
                "Both sides must acquire a close enemy even when the slow search budget is unavailable.");
            Require(attacks[0].TargetIndex == 1 && attacks[0].Damage == 20 &&
                attacks[1].TargetIndex == 0 && attacks[1].Damage == 20,
                "Contact attacks must occur on both sides without the slow combat scheduler.");
            Require(attacks[0].AvoidedCategoryCandidates == 1 && attacks[0].AvoidedCategoryTargetSelected == 1,
                "Close combat must apply and report the Recoil avoided-category score while still selecting the only eligible target.");
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

        private static void ValidateCloseAvoidedTargetReplacement()
        {
            using var world = new World("Recoil close avoided-target fallback checks");
            var manager = world.EntityManager;
            var archetype = manager.CreateArchetype(typeof(RecoilBattleEcsState), typeof(RecoilUnitNavigation),
                typeof(RecoilUnitWaypoint), typeof(RecoilUnitPose));
            var own = manager.CreateEntity(archetype);
            var avoided = manager.CreateEntity(archetype);
            var eligible = manager.CreateEntity(archetype);
            manager.SetComponentData(own, new RecoilBattleEcsState
            {
                Team = 0, Health = 100, Active = 1, Target = avoided,
                Stats = new RecoilUnitCombatStats { AvoidTargetCategoryMask = 1 << 4 }
            });
            manager.SetComponentData(avoided, new RecoilBattleEcsState
            {
                Team = 1, Health = 100, Active = 1,
                Stats = new RecoilUnitCombatStats { TargetCategoryMask = 1 << 4 }
            });
            manager.SetComponentData(eligible, new RecoilBattleEcsState
            { Team = 1, Health = 100, Active = 1 });
            manager.SetComponentData(own, new RecoilUnitPose { Position = float3.zero, Rotation = quaternion.identity });
            manager.SetComponentData(avoided, new RecoilUnitPose { Position = new float3(1f, 0f, 0f), Rotation = quaternion.identity });
            manager.SetComponentData(eligible, new RecoilUnitPose { Position = new float3(1.25f, 0f, 0f), Rotation = quaternion.identity });

            using var query = manager.CreateEntityQuery(ComponentType.ReadWrite<RecoilBattleEcsState>(),
                ComponentType.ReadWrite<RecoilUnitNavigation>(), ComponentType.ReadWrite<RecoilUnitWaypoint>(),
                ComponentType.ReadWrite<RecoilUnitPose>());
            using var indices = new NativeParallelHashMap<Entity, int>(3, Allocator.TempJob);
            indices.Add(own, 0); indices.Add(avoided, 1); indices.Add(eligible, 2);
            using var ownedEntities = new NativeArray<Entity>(3, Allocator.TempJob);
            var entities = ownedEntities;
            entities[0] = own; entities[1] = avoided; entities[2] = eligible;
            using var ownedSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(3, Allocator.TempJob);
            var snapshots = ownedSnapshots;
            snapshots[0] = new RecoilBattleCombatSnapshot
            {
                Entity = own, Team = 0, Health = 100, Active = 1, CurrentTargetIndex = 1,
                Stats = new RecoilUnitCombatStats { AvoidTargetCategoryMask = 1 << 4 }
            };
            snapshots[1] = new RecoilBattleCombatSnapshot
            {
                Entity = avoided, Team = 1, Health = 100, Active = 1,
                Position = new float3(1f, 0f, 0f),
                Stats = new RecoilUnitCombatStats { TargetCategoryMask = 1 << 4 }
            };
            snapshots[2] = new RecoilBattleCombatSnapshot
            { Entity = eligible, Team = 1, Health = 100, Active = 1, Position = new float3(1.25f, 0f, 0f) };
            using var cells = new NativeParallelMultiHashMap<int3, int>(3, Allocator.TempJob);
            cells.Add(new int3(0, 0, 0), 0);
            cells.Add(new int3(0, 0, 1), 1);
            cells.Add(new int3(0, 0, 1), 2);
            using var teamCounts = new NativeParallelHashMap<int3, int>(2, Allocator.TempJob);
            teamCounts.Add(new int3(0, 0, 0), 1);
            teamCounts.Add(new int3(0, 0, 1), 2);
            using var attacks = new NativeArray<RecoilContactAttack>(3, Allocator.TempJob);
            using var ranged = new NativeArray<byte>(3, Allocator.TempJob);
            using var scheduledGenerations = new NativeArray<int>(3, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var job = new RecoilCloseTargetJob
            {
                TeamCellCounts = teamCounts, Entities = manager.GetEntityTypeHandle(),
                States = manager.GetComponentTypeHandle<RecoilBattleEcsState>(false),
                Poses = manager.GetComponentTypeHandle<RecoilUnitPose>(false),
                Navigations = manager.GetComponentTypeHandle<RecoilUnitNavigation>(false),
                Waypoints = manager.GetBufferTypeHandle<RecoilUnitWaypoint>(false), Indices = indices,
                SnapshotEntities = entities, Soldiers = snapshots, Cells = cells, CellSize = 4f,
                ScheduledGenerations = scheduledGenerations, ScheduleGeneration = 1,
                RangeSquared = 16f, MeleeAttackRangeSquared = 2.25f,
                ArcherAttackRangeSquared = 2.25f, SiegeAttackRangeSquared = 2.25f,
                RangedUnits = ranged, Attacks = attacks, CombatDelta = 0.1f,
                AttackInterval = 0.65f, AttackDamage = 20
            };
            job.ScheduleParallel(query, default).Complete();
            Require(manager.GetComponentData<RecoilBattleEcsState>(own).Target == eligible &&
                attacks[0].TargetIndex == 2 && attacks[0].AvoidedCategoryTargetSelected == 0,
                "A close unit must replace its in-range avoided target with a close eligible target before attacking.");
        }

        private static void ValidateCloseSearchBeyondCrowdedCell()
        {
            const int enemyCount = 33;
            var total = enemyCount + 1;
            using var world = new World("Recoil uncapped close target checks");
            var manager = world.EntityManager;
            var archetype = manager.CreateArchetype(typeof(RecoilBattleEcsState), typeof(RecoilUnitNavigation),
                typeof(RecoilUnitWaypoint), typeof(RecoilUnitPose));
            using var query = manager.CreateEntityQuery(ComponentType.ReadWrite<RecoilBattleEcsState>(),
                ComponentType.ReadWrite<RecoilUnitNavigation>(), ComponentType.ReadWrite<RecoilUnitWaypoint>(),
                ComponentType.ReadWrite<RecoilUnitPose>());
            using var indices = new NativeParallelHashMap<Entity, int>(total, Allocator.TempJob);
            using var entities = new NativeArray<Entity>(total, Allocator.TempJob);
            using var snapshots = new NativeArray<RecoilBattleCombatSnapshot>(total, Allocator.TempJob);
            var writableEntities = entities;
            var writableSnapshots = snapshots;
            using var cells = new NativeParallelMultiHashMap<int3, int>(total, Allocator.TempJob);
            using var attacks = new NativeArray<RecoilContactAttack>(total, Allocator.TempJob);
            using var ranged = new NativeArray<byte>(total, Allocator.TempJob);
            using var scheduledGenerations = new NativeArray<int>(total, Allocator.TempJob,
                NativeArrayOptions.ClearMemory);
            using var teamCounts = new NativeParallelHashMap<int3, int>(2, Allocator.TempJob);

            var own = manager.CreateEntity(archetype);
            writableEntities[0] = own;
            indices.Add(own, 0);
            manager.SetComponentData(own, new RecoilBattleEcsState
                { Team = 0, Health = 100, Active = 1 });
            writableSnapshots[0] = new RecoilBattleCombatSnapshot
            {
                Entity = own, Team = 0, Health = 100, Active = 1,
                CurrentTargetIndex = -1, SearchTarget = 0
            };
            cells.Add(new int3(0, 0, 0), 0);

            for (var i = 0; i < enemyCount; i++)
            {
                var entity = manager.CreateEntity(archetype);
                var index = i + 1;
                var priority = i == enemyCount - 1 ? 0 : 1;
                var position = new float3(1f + i * .001f, 0f, 0f);
                writableEntities[index] = entity;
                indices.Add(entity, index);
                manager.SetComponentData(entity, new RecoilBattleEcsState
                    { Team = 1, Health = 100, Active = 1,
                        Stats = new RecoilUnitCombatStats { TargetPriority = priority } });
                manager.SetComponentData(entity, new RecoilUnitPose { Position = position });
                writableSnapshots[index] = new RecoilBattleCombatSnapshot
                {
                    Entity = entity, Position = position, Team = 1, Health = 100, Active = 1,
                    CurrentTargetIndex = -1,
                    Stats = new RecoilUnitCombatStats { TargetPriority = priority }
                };
                cells.Add(new int3(0, 0, 1), index);
            }
            teamCounts.Add(new int3(0, 0, 0), 1);
            teamCounts.Add(new int3(0, 0, 1), enemyCount);
            var job = new RecoilCloseTargetJob
            {
                TeamCellCounts = teamCounts,
                Entities = manager.GetEntityTypeHandle(),
                States = manager.GetComponentTypeHandle<RecoilBattleEcsState>(false),
                Poses = manager.GetComponentTypeHandle<RecoilUnitPose>(false),
                Navigations = manager.GetComponentTypeHandle<RecoilUnitNavigation>(false),
                Waypoints = manager.GetBufferTypeHandle<RecoilUnitWaypoint>(false),
                Indices = indices,
                SnapshotEntities = entities,
                Soldiers = snapshots,
                Cells = cells,
                CellSize = 4f,
                ScheduledGenerations = scheduledGenerations,
                ScheduleGeneration = 1,
                RangeSquared = 16f,
                MeleeAttackRangeSquared = 4f,
                ArcherAttackRangeSquared = 4f,
                SiegeAttackRangeSquared = 4f,
                RangedUnits = ranged,
                Attacks = attacks,
                CombatDelta = 0.1f,
                AttackInterval = 1f,
                AttackDamage = 10
            };
            job.ScheduleParallel(query, default).Complete();
            Require(attacks[0].CandidateChecks == enemyCount && attacks[0].TargetIndex == enemyCount &&
                manager.GetComponentData<RecoilBattleEcsState>(own).Target == entities[enemyCount],
                "Close acquisition must inspect a crowded grid bucket beyond 32 candidates and select the best Recoil priority.");
        }

        private static void ValidateRosterIndices()
        {
            using var world = new World("Recoil roster index checks");
            using var ownedEntities = new NativeArray<Entity>(2, Allocator.TempJob);
            var entities = ownedEntities;
            using var indices = new NativeParallelHashMap<Entity, int>(2, Allocator.TempJob);
            var first = world.EntityManager.CreateEntity();
            var second = world.EntityManager.CreateEntity();
            entities[0] = first; entities[1] = second;
            var job = new RecoilBuildCombatIndicesJob { Entities = entities, Indices = indices, Count = 2 };
            job.Schedule().Complete();
            Require(indices[first] == 0 && indices[second] == 1, "Roster slots must match snapshot slots.");
            entities[0] = second;
            job.Count = 1;
            job.Schedule().Complete();
            Require(!indices.ContainsKey(first) && indices[second] == 0,
                "Roster compaction must remove stale entities and reindex survivors.");
            job.Count = 0;
            job.Schedule().Complete();
            Require(indices.Count() == 0, "Empty rosters must clear all indices.");
        }

        private static void ValidateStaleSimulationAccess()
        {
            using var world = new World("Recoil stale simulation access checks");
            var manager = world.EntityManager;
            var entity = manager.CreateEntity();
            var simulation = new RecoilUnitSimulation(world, entity, null, default, quaternion.identity,
                new RecoilUnitMovementSettings { BodyRadius = 0.5f });

            Require(simulation.Exists && simulation.HasRequiredComponents &&
                simulation.TryGetSettings(out var settings) && math.abs(settings.BodyRadius - 0.5f) < 0.0001f,
                "A live simulation wrapper must expose its movement settings.");

            manager.DestroyEntity(entity);
            Require(!simulation.Exists && !simulation.HasRequiredComponents &&
                !simulation.TryGetSettings(out _) && !simulation.TryGetPose(out _) &&
                simulation.Settings.Equals(default(RecoilUnitMovementSettings)),
                "Combat roster and castle attack reads must safely reject a wrapper after its ECS entity is destroyed.");
        }

        private static void ValidateBatchedGridOrder()
        {
            const int capacity = 512;
            using var ownedSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(capacity, Allocator.TempJob);
            var snapshots = ownedSnapshots;
            using var cells = new NativeParallelMultiHashMap<int3, int>(capacity, Allocator.TempJob);
            using var reference = new NativeParallelMultiHashMap<int3, int>(capacity, Allocator.TempJob);
            using var previous = new NativeArray<int3>(capacity, Allocator.TempJob);
            using var active = new NativeArray<byte>(capacity, Allocator.TempJob);
            using var counts = new NativeParallelHashMap<int3, int>(capacity, Allocator.TempJob);
            using var changedCells = new NativeParallelHashSet<int3>(capacity, Allocator.TempJob);
            using var survivors = new NativeList<int>(capacity, Allocator.TempJob);
            using var changedIndices = new NativeArray<byte>(capacity, Allocator.TempJob);
            using var metadata = new NativeArray<int>(4, Allocator.TempJob);
            var job = new RecoilUpdateCombatGridJob
            {
                Snapshots = snapshots, Cells = cells, PreviousCells = previous, PreviousActive = active,
                TeamCellCounts = counts, ChangedCells = changedCells, Survivors = survivors,
                ChangedIndices = changedIndices, Metadata = metadata, Count = capacity, CellSize = 4f
            };
            var oldCells = new int3[capacity];
            var oldActive = new bool[capacity];
            for (var tick = 0; tick < 16; tick++)
            {
                for (var i = 0; i < capacity; i++)
                {
                    // Dense cells, stationary survivors, deaths, teams and negative coordinates.
                    var next = new int3((i % 3 == 0 ? tick : i) % 4 - 2, i % 2 - 1, (i + tick / 4) % 2);
                    var alive = (i + tick) % 7 != 0;
                    snapshots[i] = new RecoilBattleCombatSnapshot
                    {
                        Position = new float3(next.x * 4f, 0f, next.y * 4f), Team = next.z,
                        Health = alive ? 100 : 0, Active = (byte)(alive ? 1 : 0)
                    };
                    if (alive && oldActive[i] && math.all(oldCells[i] == next)) continue;
                    if (oldActive[i]) reference.Remove(oldCells[i], i);
                    if (alive) reference.Add(next, i);
                    oldCells[i] = next;
                    oldActive[i] = alive;
                }
                job.Schedule().Complete();
                Require(cells.Count() == reference.Count(), "Batched grid must preserve all memberships.");
                for (var x = -2; x <= 1; x++)
                for (var z = -1; z <= 0; z++)
                for (var team = 0; team <= 1; team++)
                {
                    var key = new int3(x, z, team);
                    var actualHasValue = cells.TryGetFirstValue(key, out var actual, out var cursor);
                    var expectedHasValue = reference.TryGetFirstValue(key, out var expected, out var referenceCursor);
                    while (actualHasValue || expectedHasValue)
                    {
                        Require(actualHasValue && expectedHasValue && actual == expected,
                            "Batched removal must preserve the exact candidate sequence of the original grid.");
                        actualHasValue = cells.TryGetNextValue(out actual, ref cursor);
                        expectedHasValue = reference.TryGetNextValue(out expected, ref referenceCursor);
                    }
                }
            }
        }

        private static void ValidateIncrementalGrid()
        {
            using var owned = new NativeArray<RecoilBattleCombatSnapshot>(4, Allocator.TempJob);
            var snapshots = owned;
            using var cells = new NativeParallelMultiHashMap<int3, int>(4, Allocator.TempJob);
            using var previous = new NativeArray<int3>(4, Allocator.TempJob);
            using var counts = new NativeParallelHashMap<int3, int>(4, Allocator.TempJob);
            using var active = new NativeArray<byte>(4, Allocator.TempJob);
            using var metadata = new NativeArray<int>(4, Allocator.TempJob);
            using var changedCells = new NativeParallelHashSet<int3>(4, Allocator.TempJob);
            using var survivors = new NativeList<int>(4, Allocator.TempJob);
            using var changedIndices = new NativeArray<byte>(4, Allocator.TempJob);
            snapshots[0] = new RecoilBattleCombatSnapshot { Active = 1, Health = 100, Team = 0 };
            snapshots[1] = new RecoilBattleCombatSnapshot
            { Active = 1, Health = 100, Team = 1, Position = new float3(10f, 0f, 0f) };
            var job = new RecoilUpdateCombatGridJob
            { ChangedCells = changedCells, Survivors = survivors, ChangedIndices = changedIndices,
                Snapshots = snapshots, Cells = cells, TeamCellCounts = counts, PreviousCells = previous, PreviousActive = active,
                Metadata = metadata, Count = 2, CellSize = 4f };
            job.Schedule().Complete();
            Require(cells.Count() == 2 && metadata[1] == 2, "New units must enter the incremental grid.");
            Require(counts[new int3(0, 0, 0)] == 1 && counts[new int3(0, 0, 1)] == 1,
                "Coarse occupancy must keep teams separate.");
            Require(RecoilCombatQuadOccupancy.MayHaveEnemyUnits(counts, int2.zero, 2, 1) &&
                !RecoilCombatQuadOccupancy.MayHaveEnemyUnits(counts, new int2(-8, -8), 1, 1),
                "Broad phase must include the complete fine-cell search square and reject empty regions.");
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
            Require(!RecoilCombatQuadOccupancy.MayHaveEnemyUnits(counts, int2.zero, 10, 1),
                "Removing the last enemy must clear coarse occupancy.");
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
            Require(cells.Count() == 0 && counts.Count() == 0, "An empty army must leave both grid levels empty.");
            enemy.Position = new float3(-0.1f, 0f, -16.1f);
            snapshots[0] = enemy;
            job.Count = 1;
            job.Schedule().Complete();
            Require(counts.ContainsKey(new int3(-1, -2, 1)) &&
                RecoilCombatQuadOccupancy.MayHaveEnemyUnits(counts, new int2(-1, -5), 0, 1),
                "Negative world coordinates must use floor division at both grid levels.");
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

        public static void ValidateUnitStats()
        {
            var config = new RecoilUnitCombatStats
                { MaxHealth = 150, Armor = .2f, SecondaryArmor = .1f, AttackSpeed = .8f };
            Require(RecoilUnitDamage.Calculate(100, config, false) == 80 &&
                RecoilUnitDamage.Calculate(100, config, true) == 90, "Melee armor and ranged armor must be distinct.");
            var cavalry = new RecoilUnitCombatStats { CombatRole = RecoilCombatRole.Cavalry, AttackType = RecoilAttackType.Melee };
            var archer = new RecoilUnitCombatStats { MaxHealth = 100, CombatRole = RecoilCombatRole.Archer, Armor = .05f, SecondaryArmor = .9f };
            Require(RecoilUnitDamage.Calculate(20, cavalry, archer) == 22, "Cavalry vs archer: 20 * 1.15 * .95 rounds to 22.");
            var spear = new RecoilUnitCombatStats { CombatRole = RecoilCombatRole.Spearman, AttackType = RecoilAttackType.Melee };
            var cavalryTarget = new RecoilUnitCombatStats { MaxHealth = 100, CombatRole = RecoilCombatRole.Cavalry, Armor = .2f };
            Require(RecoilUnitDamage.Calculate(30, spear, cavalryTarget) == 28, "Spearman vs cavalry: 30 * 1.15 * .8 rounds to 28.");
            archer.AttackType = RecoilAttackType.Ranged;
            spear.MaxHealth = 100; spear.Armor = .9f; spear.SecondaryArmor = .25f;
            Require(RecoilUnitDamage.Calculate(40, archer, spear) == 33, "Archer bonus uses ranged armor, not melee armor.");
            spear.SecondaryArmor = 1f;
            Require(RecoilUnitDamage.Calculate(100, archer, spear) == 0, "Full armor blocks damage even with counter bonus.");
            Require(RecoilUnitDamage.Bonus(RecoilCombatRole.Militia, RecoilCombatRole.Cavalry) == 0f &&
                RecoilUnitDamage.Bonus(RecoilCombatRole.Cavalry, RecoilCombatRole.Militia) == 0f,
                "Militia has no invented counter percentage: the numeric table defines none.");
            Require(math.abs(config.AttackInterval - 1.25f) < .0001f, "Attack speed is attacks per second.");
            using var ownedSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(6, Allocator.TempJob);
            var snapshots = ownedSnapshots;
            using var ownedAttacks = new NativeArray<RecoilContactAttack>(6, Allocator.TempJob);
            var attacks = ownedAttacks;
            using var damage = new NativeArray<int>(6, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int3, int>(6, Allocator.TempJob);
            for (var i = 0; i < 6; i++)
            {
                snapshots[i] = new RecoilBattleCombatSnapshot
                {
                    Active = 1, Health = 100, Team = i == 0 || i == 3 ? 0 : 1,
                    Position = new float3(i == 0 ? 0f : i == 4 ? 15f : 10f + i - 1, 0f, 0f), Stats = config
                };
                attacks[i] = new RecoilContactAttack { TargetIndex = -1 };
            }
            var caster = snapshots[0];
            var casterStats = config;
            casterStats.AreaRadius = 4f; casterStats.SecondaryDamage = true;
            caster.Stats = casterStats; snapshots[0] = caster;
            var dead = snapshots[5]; dead.Health = 0; snapshots[5] = dead;
            for (var i = 0; i < 6; i++)
            {
                var cell = (int2)math.floor(snapshots[i].Position.xz / 4f);
                cells.Add(new int3(cell.x, cell.y, snapshots[i].Team), i);
            }
            attacks[0] = new RecoilContactAttack { TargetIndex = 1, Damage = 100 };
            var job = new RecoilAggregateDamageJob
                { Attacks = attacks, Soldiers = snapshots, Cells = cells, Damage = damage, Count = 6, CellSize = 4f };
            job.Schedule().Complete();
            Require(damage[1] == 90 && damage[2] == 90 && damage[0] == 0 && damage[3] == 0 && damage[4] == 0 && damage[5] == 0,
                "Magic AoE must hit in-range enemies once, ignore allies, dead units and targets outside the radius.");
            casterStats.SecondaryDamage = false; casterStats.AreaRadius = 0f;
            caster.Stats = casterStats; snapshots[0] = caster;
            job.Schedule().Complete();
            Require(damage[1] == 80 && damage[2] == 0, "Non-AoE physical attacks must hit only the primary target.");
            using var world = new World("Per-unit range and attack speed checks");
            var manager = world.EntityManager;
            var archetype = manager.CreateArchetype(typeof(RecoilBattleEcsState), typeof(RecoilUnitNavigation), typeof(RecoilUnitWaypoint), typeof(RecoilUnitPose));
            var own = manager.CreateEntity(archetype);
            var enemy = manager.CreateEntity(archetype);
            var alternative = manager.CreateEntity(archetype);
            var outsideSearchRadius = manager.CreateEntity(archetype);
            config.Damage = 22; config.Range = 2.3f;
            manager.SetComponentData(own, new RecoilBattleEcsState { Team = 0, Active = 1, Health = 130, Stats = config });
            manager.SetComponentData(enemy, new RecoilBattleEcsState { Team = 1, Active = 1, Health = 130, Stats = config });
            manager.SetComponentData(alternative, new RecoilBattleEcsState { Team = 1, Active = 1, Health = 130, Stats = config });
            manager.SetComponentData(outsideSearchRadius, new RecoilBattleEcsState { Team = 1, Active = 1, Health = 130, Stats = config });
            manager.SetComponentData(own, new RecoilUnitPose { Rotation = quaternion.RotateY(-math.PI / 2f) });
            manager.SetComponentData(enemy, new RecoilUnitPose { Position = new float3(2.2f, 0f, 0f), Rotation = quaternion.RotateY(math.PI / 2f) });
            using var indices = new NativeParallelHashMap<Entity, int>(4, Allocator.TempJob);
            indices.Add(own, 0); indices.Add(enemy, 1);
            using var ownedEntities = new NativeArray<Entity>(6, Allocator.TempJob);
            var entities = ownedEntities; entities[0] = own; entities[1] = enemy;
            snapshots[0] = new RecoilBattleCombatSnapshot { Team = 0, Active = 1, Health = 130, Stats = config, CurrentTargetIndex = 1 };
            snapshots[1] = new RecoilBattleCombatSnapshot { Team = 1, Active = 1, Health = 130, Stats = config,
                CurrentTargetIndex = 0, Position = new float3(2.2f, 0f, 0f) };
            cells.Clear(); cells.Add(new int3(0, 0, 0), 0); cells.Add(new int3(0, 0, 1), 1);
            using var ranged = new NativeArray<byte>(6, Allocator.TempJob);
            var rangedValues = ranged;
            using var scheduledGenerations = new NativeArray<int>(6, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var scheduleGenerationValues = scheduledGenerations;
            using var query = manager.CreateEntityQuery(typeof(RecoilBattleEcsState), typeof(RecoilUnitNavigation), typeof(RecoilUnitWaypoint));
            using var teamCounts = new NativeParallelHashMap<int3, int>(3, Allocator.TempJob);
            teamCounts.Add(new int3(0, 0, 0), 1);
            teamCounts.Add(new int3(0, 0, 1), 1);
            teamCounts.Add(new int3(0, -1, 1), 1);
            var close = new RecoilCloseTargetJob
            {
                TeamCellCounts = teamCounts,
                Entities = manager.GetEntityTypeHandle(), States = manager.GetComponentTypeHandle<RecoilBattleEcsState>(false),
                Poses = manager.GetComponentTypeHandle<RecoilUnitPose>(false),
                Navigations = manager.GetComponentTypeHandle<RecoilUnitNavigation>(false), Waypoints = manager.GetBufferTypeHandle<RecoilUnitWaypoint>(false),
                Indices = indices, SnapshotEntities = entities, Soldiers = snapshots, Cells = cells, CellSize = 4f,
                ScheduledGenerations = scheduledGenerations, ScheduleGeneration = 0,
                RangedUnits = ranged, Attacks = attacks, RangeSquared = 16f, MeleeAttackRangeSquared = 2.25f,
                ArcherAttackRangeSquared = 2.25f, SiegeAttackRangeSquared = 2.25f,
                AttackDamage = 999, AttackInterval = 9f, CombatDelta = .1f
            };
            close.ScheduleParallel(query, default).Complete();
            Require(attacks[0].Damage == 22 && attacks[1].Damage == 22 &&
                math.abs(manager.GetComponentData<RecoilBattleEcsState>(own).AttackCooldown - 1.25f) < .0001f,
                "Per-unit range, damage and cooldown must override the global defaults.");
            Require(math.dot(math.forward(manager.GetComponentData<RecoilUnitPose>(own).Rotation), new float3(1f, 0f, 0f)) > .999f &&
                math.dot(math.forward(manager.GetComponentData<RecoilUnitPose>(enemy).Rotation), new float3(-1f, 0f, 0f)) > .999f,
                "Both attackers must face the opponent, including stationary units initially facing away.");
            close.ScheduleParallel(query, default).Complete();
            Require(attacks[0].Damage == 0, "Per-unit attack speed must enforce cooldown.");
            var movingTarget = snapshots[1];
            movingTarget.Position = new float3(0f, 0f, 10f);
            snapshots[1] = movingTarget;
            manager.SetComponentData(own, new RecoilUnitPose
                { Rotation = quaternion.RotateY(math.PI / 2f), Speed = 2f, Velocity = new float3(2f, 0f, 0f) });
            close.ScheduleParallel(query, default).Complete();
            var facingPose = manager.GetComponentData<RecoilUnitPose>(own);
            Require(math.dot(math.forward(facingPose.Rotation), new float3(0f, 0f, 1f)) > .999f && facingPose.Speed == 2f,
                "Approaching units must look at a moving enemy rather than the lateral movement direction, without stopping movement.");

            // Exercise the actual weapon state machine: windup, all salvo shots at their delay,
            // then reload from the first salvo shot before the next shot can be emitted.
            var weaponStats = new RecoilUnitCombatStats
            {
                MaxHealth = 100, Damage = 17, Range = 3f, AttackSpeed = 1f,
                SalvoSize = 3, SalvoDelay = 0.1f, Windup = 0.2f
            };
            var attackerState = manager.GetComponentData<RecoilBattleEcsState>(own);
            attackerState.Stats = weaponStats;
            attackerState.Health = 100;
            attackerState.AttackCooldown = 0f;
            attackerState.SalvoCooldown = 0f;
            attackerState.WindupCooldown = 0f;
            attackerState.SalvoShotsRemaining = 0;
            attackerState.WindupPending = 0;
            manager.SetComponentData(own, attackerState);
            var defenderState = manager.GetComponentData<RecoilBattleEcsState>(enemy);
            defenderState.Stats = weaponStats;
            defenderState.Health = 100;
            defenderState.AttackCooldown = 0f;
            defenderState.SalvoCooldown = 0f;
            defenderState.WindupCooldown = 0f;
            defenderState.SalvoShotsRemaining = 0;
            defenderState.WindupPending = 0;
            manager.SetComponentData(enemy, defenderState);
            var attackerSnapshot = snapshots[0];
            attackerSnapshot.Position = float3.zero;
            attackerSnapshot.Stats = weaponStats;
            attackerSnapshot.Health = 100;
            attackerSnapshot.CurrentTargetIndex = 1;
            attackerSnapshot.SearchTarget = 0;
            snapshots[0] = attackerSnapshot;
            var defenderSnapshot = snapshots[1];
            defenderSnapshot.Position = new float3(2f, 0f, 0f);
            defenderSnapshot.Stats = weaponStats;
            defenderSnapshot.Health = 100;
            defenderSnapshot.CurrentTargetIndex = 0;
            defenderSnapshot.SearchTarget = 0;
            snapshots[1] = defenderSnapshot;
            manager.SetComponentData(own, new RecoilUnitPose { Position = float3.zero });
            manager.SetComponentData(enemy, new RecoilUnitPose { Position = defenderSnapshot.Position });
            manager.SetComponentData(alternative, new RecoilUnitPose { Position = new float3(.1f, 0f, .1f) });
            manager.SetComponentData(outsideSearchRadius, new RecoilUnitPose { Position = new float3(.1f, 0f, -3.5f) });
            close.CombatDelta = 0.1f;
            var emitted = new int[13];
            for (var tick = 0; tick < emitted.Length; tick++)
            {
                close.ScheduleParallel(query, default).Complete();
                emitted[tick] = attacks[0].Damage;
            }
            Require(emitted[0] == 0 && emitted[1] == 0 &&
                emitted[2] == 17 && emitted[3] == 17 && emitted[4] == 17 &&
                emitted[5] == 0 && emitted[11] == 0 && emitted[12] == 17,
                "Windup must delay the first shot, salvo shots must use their cadence, and reload must start at the first shot.");

            // Verify the terrain line-of-fire test gates the actual weapon emission path,
            // rather than only testing the height-field helper in isolation.
            using var terrainHeights = new NativeArray<float>(9, Allocator.TempJob, NativeArrayOptions.ClearMemory);
            var mutableTerrainHeights = terrainHeights;
            mutableTerrainHeights[4] = 2f;
            close.Heights = new RecoilTerrainHeightField
            {
                Heights = terrainHeights,
                Columns = 3,
                Rows = 3,
                Origin = float3.zero,
                Size = new float2(2f, 2f)
            };
            var clearShotStats = weaponStats;
            clearShotStats.Windup = 0f;
            attackerState = manager.GetComponentData<RecoilBattleEcsState>(own);
            attackerState.Stats = clearShotStats;
            attackerState.AttackCooldown = 0f;
            attackerState.SalvoShotsRemaining = 0;
            attackerState.WindupPending = 0;
            manager.SetComponentData(own, attackerState);
            defenderState = manager.GetComponentData<RecoilBattleEcsState>(enemy);
            defenderState.Stats = clearShotStats;
            defenderState.AttackCooldown = 0f;
            defenderState.SalvoShotsRemaining = 0;
            defenderState.WindupPending = 0;
            manager.SetComponentData(enemy, defenderState);
            attackerSnapshot = snapshots[0];
            attackerSnapshot.Position = new float3(.1f, 0f, 1f);
            attackerSnapshot.Stats = clearShotStats;
            snapshots[0] = attackerSnapshot;
            defenderSnapshot = snapshots[1];
            defenderSnapshot.Position = new float3(1.9f, 0f, 1f);
            defenderSnapshot.Stats = clearShotStats;
            snapshots[1] = defenderSnapshot;
            manager.SetComponentData(own, new RecoilUnitPose { Position = attackerSnapshot.Position });
            manager.SetComponentData(enemy, new RecoilUnitPose { Position = defenderSnapshot.Position });
            close.CombatDelta = 0.1f;
            close.ScheduleParallel(query, default).Complete();
            Require(attacks[0].Damage == 0 && attacks[1].Damage == 0 &&
                attacks[0].LineOfFireBlocked > 0 && attacks[1].LineOfFireBlocked > 0,
                "A terrain ridge must block weapon emission for both retained close targets and be counted by combat diagnostics.");

            // Recoil's default burst control continues a salvo after firing has started.
            // The projectile must still resolve against terrain instead of being discarded.
            attackerState = manager.GetComponentData<RecoilBattleEcsState>(own);
            attackerState.SalvoShotsRemaining = 1;
            attackerState.SalvoCooldown = 0f;
            attackerState.AttackCooldown = 0f;
            manager.SetComponentData(own, attackerState);
            rangedValues[0] = 1;
            close.ScheduleParallel(query, default).Complete();
            Require(attacks[0].TargetIndex == 1 && attacks[0].Damage == clearShotStats.Damage &&
                attacks[0].Projectile != 0 && attacks[0].LineOfFireBlocked > 0,
                "An active ranged salvo must continue through temporary terrain occlusion and let projectile collision resolve the shot.");

            // A scheduled local scan must discard its terrain-blocked current target and acquire
            // another visible enemy in the same nearby grid region.
            indices.Add(alternative, 2);
            indices.Add(outsideSearchRadius, 3);
            entities[2] = alternative;
            entities[3] = outsideSearchRadius;
            var ownerSnapshot = snapshots[0];
            ownerSnapshot.Position = new float3(.1f, 0f, 1f);
            ownerSnapshot.CurrentTargetIndex = 1;
            ownerSnapshot.Stats = clearShotStats;
            snapshots[0] = ownerSnapshot;
            var blockedSnapshot = snapshots[1];
            blockedSnapshot.Position = new float3(1.9f, 0f, 1f);
            blockedSnapshot.Stats = clearShotStats;
            snapshots[1] = blockedSnapshot;
            snapshots[2] = new RecoilBattleCombatSnapshot
                { Team = 1, Active = 1, Health = 130, Stats = clearShotStats, Position = new float3(.1f, 0f, .1f) };
            var distantStats = clearShotStats;
            distantStats.TargetPriority = -1;
            snapshots[3] = new RecoilBattleCombatSnapshot
                { Team = 1, Active = 1, Health = 130, Stats = distantStats, Position = new float3(.1f, 0f, -3.5f) };
            manager.SetComponentData(own, new RecoilBattleEcsState { Team = 0, Active = 1, Health = 130, Stats = clearShotStats, Target = enemy });
            manager.SetComponentData(own, new RecoilUnitPose { Position = ownerSnapshot.Position });
            manager.SetComponentData(enemy, new RecoilUnitPose { Position = blockedSnapshot.Position });
            manager.SetComponentData(alternative, new RecoilUnitPose { Position = snapshots[2].Position });
            var raisedHeights = terrainHeights;
            raisedHeights[4] = 2f;
            cells.Clear();
            cells.Add(new int3(0, 0, 0), 0);
            cells.Add(new int3(0, 0, 1), 1);
            cells.Add(new int3(0, 0, 1), 2);
            cells.Add(new int3(0, -1, 1), 3);
            scheduleGenerationValues[0] = 1;
            scheduleGenerationValues[1] = 0;
            scheduleGenerationValues[2] = 0;
            close.ScheduleGeneration = 1;
            close.CombatDelta = 0.1f;
            close.ScheduleParallel(query, default).Complete();
            Require(manager.GetComponentData<RecoilBattleEcsState>(own).Target == alternative &&
                attacks[0].TargetIndex == 2 && attacks[0].Damage == clearShotStats.Damage &&
                attacks[0].LineOfFireBlocked > 0,
                "A scheduled target scan must replace a terrain-blocked target with a clear in-range enemy and ignore a higher-priority candidate outside the detection radius.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
