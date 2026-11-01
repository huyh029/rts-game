using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using Unity.Profiling;
using Unity.Entities;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Burst.Intrinsics;

namespace RecoilPathfinding
{
    [BurstCompile]
    internal struct RecoilAggregateDamageJob : IJob
    {
        [ReadOnly] public NativeArray<RecoilContactAttack> Attacks;
        public NativeArray<int> Damage;
        [ReadOnly] public NativeArray<RecoilBattleCombatSnapshot> Soldiers;
        [ReadOnly] public NativeParallelMultiHashMap<int3, int> Cells;
        public float CellSize;
        public int Count;
        public void Execute()
        {
            for (var i = 0; i < Count; i++) Damage[i] = 0;
            for (var i = 0; i < Count; i++)
            {
                var hit = Attacks[i];
                if (hit.Damage <= 0 || hit.TargetIndex < 0 || hit.TargetIndex >= Count) continue;
                if (!Soldiers.IsCreated || Soldiers.Length < Count)
                {
                    Damage[hit.TargetIndex] = math.min(Damage[hit.TargetIndex], int.MaxValue - hit.Damage) + hit.Damage;
                    continue;
                }
                var attacker = Soldiers[i];
                var area = attacker.Stats.IsConfigured ? attacker.Stats.AreaRadius : 0f;
                if (area <= 0f || !Cells.IsCreated || CellSize <= 0f)
                {
                    ApplyHit(hit.TargetIndex, hit.Damage, attacker);
                    continue;
                }
                var center = Soldiers[hit.TargetIndex].Position;
                var cell = (int2)math.floor(center.xz / CellSize);
                var extent = (int)math.ceil(area / CellSize);
                for (var x = -extent; x <= extent; x++)
                for (var z = -extent; z <= extent; z++)
                {
                    if (!Cells.TryGetFirstValue(new int3(cell.x + x, cell.y + z, attacker.Team == 0 ? 1 : 0),
                        out var target, out var cursor)) continue;
                    do
                    {
                        if (target >= 0 && target < Count && math.distancesq(Soldiers[target].Position.xz, center.xz) <= area * area)
                            ApplyHit(target, hit.Damage, attacker);
                    } while (Cells.TryGetNextValue(out target, ref cursor));
                }
            }
        }

        private void ApplyHit(int index, int raw, RecoilBattleCombatSnapshot attacker)
        {
            var target = Soldiers[index];
            if (target.Active == 0 || target.Health <= 0 || target.Team == attacker.Team) return;
            var damage = RecoilUnitDamage.Calculate(raw, target.Stats, attacker.Stats.SecondaryDamage);
            Damage[index] = math.min(Damage[index], int.MaxValue - damage) + damage;
        }
    }

    [BurstCompile]
    internal struct RecoilApplyDamageJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle Entities;
        public ComponentTypeHandle<RecoilBattleEcsState> States;
        [ReadOnly] public NativeParallelHashMap<Entity, int> Indices;
        [ReadOnly] public NativeArray<int> Damage;
        public NativeList<int>.ParallelWriter Deaths;
        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var entities = chunk.GetNativeArray(Entities);
            var states = chunk.GetNativeArray(ref States);
            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out var i))
            {
                if (!Indices.TryGetValue(entities[i], out var index) || Damage[index] <= 0) continue;
                var state = states[i];
                if (state.Active == 0 || state.Health <= 0) continue;
                state.Health = math.max(0, state.Health - Damage[index]);
                if (state.Health == 0)
                {
                    state.Active = 0; state.Target = Entity.Null;
                    Deaths.AddNoResize(index);
                }
                states[i] = state;
            }
        }
    }

    [BurstCompile]
    internal struct RecoilResetAliveFlagsJob : IJobParallelFor
    {
        public NativeArray<byte> Alive;
        public void Execute(int index) => Alive[index] = 0;
    }

    [BurstCompile]
    internal struct RecoilCaptureAliveJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle Entities;
        [ReadOnly] public ComponentTypeHandle<RecoilBattleEcsState> States;
        [ReadOnly] public NativeParallelHashMap<Entity, int> Indices;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Alive;
        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var entities = chunk.GetNativeArray(Entities);
            var states = chunk.GetNativeArray(ref States);
            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out var i))
            {
                if (!Indices.TryGetValue(entities[i], out var index)) continue;
                var state = states[i];
                Alive[index] = (byte)(state.Active != 0 && state.Health > 0 ? 1 : 0);
            }
        }
    }

    internal struct RecoilContactAttack
    {
        public int TargetIndex;
        public int Damage;
    }
    [BurstCompile]
    internal struct RecoilCloseTargetJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle Entities;
        public ComponentTypeHandle<RecoilUnitPose> Poses;
        public ComponentTypeHandle<RecoilBattleEcsState> States;
        public ComponentTypeHandle<RecoilUnitNavigation> Navigations;
        public BufferTypeHandle<RecoilUnitWaypoint> Waypoints;
        [ReadOnly] public NativeParallelHashMap<Entity, int> Indices;
        [ReadOnly] public NativeArray<Entity> SnapshotEntities;
        [ReadOnly] public NativeArray<RecoilBattleCombatSnapshot> Soldiers;
        [ReadOnly] public NativeParallelMultiHashMap<int3, int> Cells;
        public float CellSize;
        public float RangeSquared;
        public float MeleeAttackRangeSquared;
        public float ArcherAttackRangeSquared;
        public float SiegeAttackRangeSquared;
        [ReadOnly] public NativeArray<byte> RangedUnits;
        [NativeDisableParallelForRestriction] public NativeArray<RecoilContactAttack> Attacks;
        public float CombatDelta;
        public float AttackInterval;
        public int AttackDamage;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var entities = chunk.GetNativeArray(Entities);
            var states = chunk.GetNativeArray(ref States);
            var poses = chunk.GetNativeArray(ref Poses);
            var navigations = chunk.GetNativeArray(ref Navigations);
            var paths = chunk.GetBufferAccessor(ref Waypoints);
            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out var i))
            {
                if (!Indices.TryGetValue(entities[i], out var index)) continue;
                var soldier = Soldiers[index];
                if (Attacks.IsCreated)
                {
                    Attacks[index] = new RecoilContactAttack { TargetIndex = -1 };
                    var cooling = states[i];
                    cooling.AttackCooldown = math.max(0f, cooling.AttackCooldown - CombatDelta);
                    states[i] = cooling;
                }
                if (soldier.Active == 0 || soldier.Health <= 0) continue;
                var attackRangeSquared = soldier.Stats.IsConfigured ? soldier.Stats.Range * soldier.Stats.Range : GetAttackRangeSquared(RangedUnits[index]);
                var searchRangeSquared = math.max(RangeSquared, attackRangeSquared);
                var target = soldier.CurrentTargetIndex;
                var nearest = searchRangeSquared;
                if (target >= 0 && target < Soldiers.Length && Soldiers[target].Active != 0 &&
                    Soldiers[target].Health > 0 && Soldiers[target].Team != soldier.Team)
                {
                    var distance = math.distancesq(soldier.Position.xz, Soldiers[target].Position.xz);
                    FaceEnemy(i, target, ref poses);
                    if (distance <= attackRangeSquared)
                    {
                        FaceAtContact(i, target, ref poses);
                        Fire(i, index, target, ref states);
                        HaltAtContact(i, ref navigations, ref paths); continue;
                    }
                }
                var cell = (int2)math.floor(soldier.Position.xz / CellSize);
                var radius = (int)math.ceil(math.sqrt(searchRangeSquared) / CellSize);
                var found = -1;
                var checks = 0;
                // Start with the occupied cell; then its immediate neighbours. Never scan the whole battlefield.
                for (var ring = 0; ring <= radius && checks < 32; ring++)
                for (var x = -ring; x <= ring && checks < 32; x++)
                for (var z = -ring; z <= ring && checks < 32; z++)
                {
                    if (math.max(math.abs(x), math.abs(z)) != ring) continue;
                    if (!Cells.TryGetFirstValue(new int3(cell.x + x, cell.y + z, soldier.Team == 0 ? 1 : 0),
                        out var candidate, out var cursor)) continue;
                    do
                    {
                        checks++;
                        var enemy = Soldiers[candidate];
                        if (enemy.Active == 0 || enemy.Health <= 0 || enemy.Team == soldier.Team) continue;
                        var distance = math.distancesq(soldier.Position.xz, enemy.Position.xz);
                        if (distance >= nearest) continue;
                        nearest = distance; found = candidate;
                    } while (checks < 32 && Cells.TryGetNextValue(out candidate, ref cursor));
                }
                if (found < 0) continue;
                var state = states[i];
                state.Target = SnapshotEntities[found];
                states[i] = state;
                var approach = navigations[i];
                approach.CombatApproach = 1;
                navigations[i] = approach;
                FaceEnemy(i, found, ref poses);
                if (nearest <= attackRangeSquared)
                {
                    FaceAtContact(i, found, ref poses);
                    Fire(i, index, found, ref states);
                    HaltAtContact(i, ref navigations, ref paths);
                }
            }
        }

        private void FaceEnemy(int index, int target, ref NativeArray<RecoilUnitPose> poses)
        {
            var pose = poses[index];
            RecoilCombatFacing.FaceTarget(ref pose, Soldiers[target].Position);
            poses[index] = pose;
        }

        private void FaceAtContact(int index, int target, ref NativeArray<RecoilUnitPose> poses)
        {
            var pose = poses[index];
            RecoilCombatFacing.FaceTarget(ref pose, Soldiers[target].Position);
            pose.Speed = 0f;
            pose.Velocity = float3.zero;
            poses[index] = pose;
        }

        private float GetAttackRangeSquared(byte unitType)
        {
            if (unitType == 2)
                return SiegeAttackRangeSquared;
            return unitType == 1 ? ArcherAttackRangeSquared : MeleeAttackRangeSquared;
        }

        private void Fire(int local, int index, int target, ref NativeArray<RecoilBattleEcsState> states)
        {
            if (!Attacks.IsCreated) return;
            var state = states[local];
            if (state.AttackCooldown > 0f) return;
            Attacks[index] = new RecoilContactAttack { TargetIndex = target, Damage = state.Stats.IsConfigured ? state.Stats.Damage : AttackDamage };
            state.AttackCooldown = math.max(0.01f, state.Stats.IsConfigured ? state.Stats.AttackInterval : AttackInterval);
            states[local] = state;
        }

        private static void HaltAtContact(int i, ref NativeArray<RecoilUnitNavigation> navigations,
            ref BufferAccessor<RecoilUnitWaypoint> paths)
        {
            var navigation = navigations[i];
            var path = paths[i];
            navigation.CombatApproach = 1;
            navigations[i] = navigation;
            if (navigation.HasDestination == 0 && navigation.PathPending == 0 && path.Length == 0) return;
            navigation.HasDestination = 0; navigation.PathPending = 0; navigation.WaypointIndex = 0; navigation.HasPathEnd = 0;
            navigation.RequestVersion++; navigation.MovementVersion++;
            path.Clear();
            navigations[i] = navigation;
        }
    }

    [BurstCompile]
    internal struct RecoilBuildCombatSnapshotJob : IJobChunk
    {
        [ReadOnly] public EntityTypeHandle Entities;
        [ReadOnly] public ComponentTypeHandle<RecoilBattleEcsState> States;
        [ReadOnly] public ComponentTypeHandle<RecoilUnitPose> Poses;
        [ReadOnly] public ComponentTypeHandle<RecoilUnitCombatTiming> Timings;
        [ReadOnly] public NativeParallelHashMap<Entity, int> Indices;
        [NativeDisableParallelForRestriction] public NativeArray<RecoilBattleCombatSnapshot> Snapshots;
        public NativeParallelMultiHashMap<int3, int>.ParallelWriter Cells;
        public float Now;
        public float DeltaTime;
        public float InitialDelta;
        public float CellSize;
        public byte SkipGridBuild;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var entities = chunk.GetNativeArray(Entities);
            var states = chunk.GetNativeArray(ref States);
            var poses = chunk.GetNativeArray(ref Poses);
            var timings = chunk.GetNativeArray(ref Timings);
            var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (iterator.NextEntityIndex(out var i))
            {
                if (!Indices.TryGetValue(entities[i], out var index)) continue;
                var state = states[i];
                var timing = timings[i];
                var hasTarget = state.Target != Entity.Null && Indices.TryGetValue(state.Target, out _);
                var target = -1;
                if (hasTarget) Indices.TryGetValue(state.Target, out target);
                Snapshots[index] = new RecoilBattleCombatSnapshot
                {
                    Position = poses[i].Position, Team = state.Team, Health = state.Health, Stats = state.Stats,
                    CurrentTargetIndex = target, AttackCooldown = state.AttackCooldown, Active = state.Active,
                    DeltaTime = timing.LastCombatUpdateTime > 0f ? math.max(DeltaTime, Now - timing.LastCombatUpdateTime) : InitialDelta,
                    SearchTarget = (byte)(!hasTarget && Now >= timing.NextTargetSearchTime ? 1 : 0)
                };
                if (SkipGridBuild != 0 || state.Active == 0 || state.Health <= 0) continue;
                var cell = (int2)math.floor(poses[i].Position.xz / CellSize);
                Cells.Add(new int3(cell.x, cell.y, state.Team), index);
            }
        }
    }

    [BurstCompile]
    internal struct RecoilUpdateCombatGridJob : IJob
    {
        [ReadOnly] public NativeArray<RecoilBattleCombatSnapshot> Snapshots;
        public NativeParallelMultiHashMap<int3, int> Cells;
        public NativeArray<int3> PreviousCells;
        public NativeArray<byte> PreviousActive;
        // [0] = previous snapshot count; [1] = number of removals/additions in this update.
        public NativeArray<int> Metadata;
        public int Count;
        public float CellSize;

        public void Execute()
        {
            var mutations = 0;
            var length = math.max(Count, Metadata[0]);
            for (var i = 0; i < length; i++)
            {
                var active = i < Count && Snapshots[i].Active != 0 && Snapshots[i].Health > 0;
                var next = int3.zero;
                if (active)
                {
                    var snapshot = Snapshots[i];
                    var cell = (int2)math.floor(snapshot.Position.xz / CellSize);
                    next = new int3(cell.x, cell.y, snapshot.Team);
                }
                var wasActive = PreviousActive[i] != 0;
                if (active && wasActive && math.all(PreviousCells[i] == next)) continue;
                if (wasActive)
                {
                    Cells.Remove(PreviousCells[i], i);
                    mutations++;
                }
                if (active)
                {
                    Cells.Add(next, i);
                    mutations++;
                    PreviousCells[i] = next;
                }
                PreviousActive[i] = (byte)(active ? 1 : 0);
            }
            Metadata[0] = Count;
            Metadata[1] = mutations;
        }
    }

    public struct RecoilBattleEcsState : IComponentData
    {
        public int Team;
        public int Health;
        public RecoilUnitCombatStats Stats;
        public float AttackCooldown;
        public byte Active;
        public Entity Target;
    }

    [BurstCompile]
    internal struct RecoilBattleCombatJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<RecoilBattleCombatSnapshot> Soldiers;
        [ReadOnly] public NativeArray<int> ScheduledSoldiers;
        [ReadOnly] public NativeParallelMultiHashMap<int3, int> Cells;
        [WriteOnly] public NativeArray<RecoilBattleCombatResult> Results;
        public int MinCellX;
        public int MaxCellX;
        public int MinCellZ;
        public int MaxCellZ;
        public float CellSize;
        public float DetectionRangeSquared;
        public int MaxCandidateChecks;
        public float AttackRangeSquared;
        public float AttackInterval;
        public int AttackDamage;
        public int BlueCount;
        public int RedCount;

        public void Execute(int index)
        {
            var soldierIndex = ScheduledSoldiers[index];
            var soldier = Soldiers[soldierIndex];
            var targetIndex = soldier.CurrentTargetIndex;
            var candidateChecks = 0;
            var enemyCount = soldier.Team == 0 ? RedCount : BlueCount;
            if (enemyCount == 0)
            {
                targetIndex = -1;
            }
            else
            {
                if (!IsValidEnemy(soldier, targetIndex)) targetIndex = -1;
                if (targetIndex < 0 && soldier.SearchTarget != 0)
                    targetIndex = FindNearestEnemy(soldier, out candidateChecks);
            }

            var cooldown = math.max(0f, soldier.AttackCooldown - soldier.DeltaTime);
            var damageTargetIndex = -1;
            var damage = 0;
            if (targetIndex >= 0)
            {
                var offset = Soldiers[targetIndex].Position - soldier.Position;
                offset.y = 0f;
                if (math.lengthsq(offset) <= AttackRangeSquared && cooldown <= 0f)
                {
                    damageTargetIndex = targetIndex;
                    damage = AttackDamage;
                    cooldown = AttackInterval;
                }
            }

            Results[index] = new RecoilBattleCombatResult
            {
                TargetIndex = targetIndex,
                DamageTargetIndex = damageTargetIndex,
                Cooldown = cooldown,
                Damage = damage,
                CandidateChecks = candidateChecks
            };
        }

        private int FindNearestEnemy(RecoilBattleCombatSnapshot soldier, out int candidateChecks)
        {
            candidateChecks = 0;
            var cell = ToCell(soldier.Position);
            var maxRadius = (int)math.ceil(math.sqrt(DetectionRangeSquared) / CellSize);
            var nearestIndex = -1;
            var nearestDistanceSquared = DetectionRangeSquared;

            for (var radius = 0; radius <= maxRadius; radius++)
            {
                var minX = cell.x - radius;
                var maxX = cell.x + radius;
                var minZ = cell.y - radius;
                var maxZ = cell.y + radius;
                if (minX > maxX || minZ > maxZ)
                    continue;

                for (var x = minX; x <= maxX; x++)
                {
                    CheckCell(soldier, new int2(x, minZ), ref nearestIndex, ref nearestDistanceSquared, ref candidateChecks);
                    if (maxZ != minZ)
                        CheckCell(soldier, new int2(x, maxZ), ref nearestIndex, ref nearestDistanceSquared, ref candidateChecks);
                }

                for (var z = minZ + 1; z < maxZ; z++)
                {
                    CheckCell(soldier, new int2(minX, z), ref nearestIndex, ref nearestDistanceSquared, ref candidateChecks);
                    if (maxX != minX)
                        CheckCell(soldier, new int2(maxX, z), ref nearestIndex, ref nearestDistanceSquared, ref candidateChecks);
                }

                if (candidateChecks >= MaxCandidateChecks) break;
                if (nearestIndex >= 0 && IsCellInsideBounds(cell))
                {
                    var left = math.max(cell.x - radius, MinCellX) * CellSize;
                    var right = (math.min(cell.x + radius, MaxCellX) + 1) * CellSize;
                    var bottom = math.max(cell.y - radius, MinCellZ) * CellSize;
                    var top = (math.min(cell.y + radius, MaxCellZ) + 1) * CellSize;
                    var distanceToUnsearchedArea = math.min(
                        math.min(soldier.Position.x - left, right - soldier.Position.x),
                        math.min(soldier.Position.z - bottom, top - soldier.Position.z));
                    if (nearestDistanceSquared <= distanceToUnsearchedArea * distanceToUnsearchedArea)
                        break;
                }
            }

            return nearestIndex;
        }

        private void CheckCell(
            RecoilBattleCombatSnapshot soldier,
            int2 cell,
            ref int nearestIndex,
            ref float nearestDistanceSquared,
            ref int candidateChecks)
        {
            if (candidateChecks >= MaxCandidateChecks || !IsCellInsideBounds(cell)) return;
            var enemyCell = new int3(cell.x, cell.y, soldier.Team == 0 ? 1 : 0);
            if (!Cells.TryGetFirstValue(enemyCell, out var candidateIndex, out var iterator))
                return;

            do
            {
                var candidate = Soldiers[candidateIndex];
                candidateChecks++;
                if (!IsValidEnemy(soldier, candidateIndex))
                    continue;

                var offset = candidate.Position - soldier.Position;
                offset.y = 0f;
                var distanceSquared = math.lengthsq(offset);
                if (distanceSquared >= nearestDistanceSquared)
                    continue;

                nearestIndex = candidateIndex;
                nearestDistanceSquared = distanceSquared;
            }
            while (candidateChecks < MaxCandidateChecks && Cells.TryGetNextValue(out candidateIndex, ref iterator));
        }

        private bool IsValidEnemy(RecoilBattleCombatSnapshot soldier, int candidateIndex)
        {
            if (candidateIndex < 0 || candidateIndex >= Soldiers.Length)
                return false;

            var candidate = Soldiers[candidateIndex];
            return candidate.Active != 0 && candidate.Health > 0 && candidate.Team != soldier.Team &&
                math.lengthsq(candidate.Position.xz - soldier.Position.xz) <= DetectionRangeSquared;
        }

        private bool IsCellInsideBounds(int2 cell) =>
            cell.x >= MinCellX && cell.x <= MaxCellX &&
            cell.y >= MinCellZ && cell.y <= MaxCellZ;

        private int2 ToCell(float3 position) =>
            (int2)math.floor(position.xz / CellSize);
    }

    internal struct RecoilBattleCombatSnapshot
    {
        public float3 Position;
        public int Team;
        public int Health;
        public RecoilUnitCombatStats Stats;
        public int CurrentTargetIndex;
        public float AttackCooldown;
        public float DeltaTime;
        public byte Active;
        public byte SearchTarget;
    }

    internal struct RecoilBattleCombatResult
    {
        public int TargetIndex;
        public int DamageTargetIndex;
        public float Cooldown;
        public int Damage;
        public int CandidateChecks;
    }

    public sealed partial class RecoilAutoBattleDemo : MonoBehaviour
    {
        private const int UnitsPerSpawnBatch = 5000;
        private const int DefaultNewUnitsPerFrame = 4;
        private const int DefaultSpawnQueueRateFrames = 5;
        private const int DefaultMaxSpawnsPerFrame = 128;
        private const int VariantFormationColumns = 5;
        private const int CharacterTypeCount = 7;
        private const int UnitsPerManualSpawn = 50000;
        private const float TargetGridCellSize = 4f;
        private static readonly int[] MatchDurationsMinutes = { 5, 10, 15, 30 };
        private static readonly string[] CharacterTypeLabels =
        {
            "Cung kỵ",
            "Đao binh",
            "Giáo binh",
            "Kỵ binh",
            "Máy bắn đá",
            "Quân sư",
            "Voi chiến"
        };
        private static readonly ProfilerMarker BattleUpdateMarker =
            new ProfilerMarker("RecoilBattle.Update");
        private static readonly ProfilerMarker CombatJobMarker =
            new ProfilerMarker("RecoilBattle.CombatBurstJob");
        private static readonly ProfilerMarker SnapshotBuildMarker =
            new ProfilerMarker("RecoilBattle.BuildSnapshotAndGrid");
        public float LastSnapshotBuildMilliseconds { get; private set; }
        private static readonly ProfilerMarker LifecycleMarker = new("RecoilBattle.Lifecycle");
        private static readonly ProfilerMarker DeparturesMarker = new("RecoilBattle.Departures");
        private static readonly ProfilerMarker RunCombatMarker = new("RecoilBattle.ScheduleCombat");
        private static readonly ProfilerMarker ResolveMarker = new("RecoilBattle.ResolveCombat");
        private static readonly ProfilerMarker PresentationMarker = new("RecoilBattle.Rendering.PrefabAndDot");
        private const float PrefabRenderRadius = 10f;
        [SerializeField] private Terrain terrain = null;
        [SerializeField] private RecoilTerrainMapData terrainMap = null;
        [SerializeField] private RecoilNavMeshPathService pathService = null;
        [SerializeField] private GameObject blueUnitTemplate = null;
        [SerializeField] private GameObject redUnitTemplate = null;
        [SerializeField] private GameObject[] blueUnitVariants = System.Array.Empty<GameObject>();
        [SerializeField] private GameObject[] redUnitVariants = System.Array.Empty<GameObject>();
        [SerializeField] private GameObject[] blueUnitsByType = new GameObject[CharacterTypeCount];
        [SerializeField] private GameObject[] redUnitsByType = new GameObject[CharacterTypeCount];
        [SerializeField] private RecoilUnitCombatStats[] lobbyUnitStats = new RecoilUnitCombatStats[CharacterTypeCount];
        [SerializeField] private Texture2D lobbyBackgroundImage;
        [SerializeField] private Texture2D lobbyTitleImage;
        [SerializeField] private Texture2D[] lobbyDurationImages = new Texture2D[4];
        [SerializeField] private Texture2D lobbyStartButtonImage;
        [SerializeField] private Texture2D lobbySettingsButtonImage;
        [SerializeField] private Texture2D[] lobbyUnitImages = new Texture2D[CharacterTypeCount];
        [SerializeField, Min(1), Tooltip("Số prefab lính tối đa được tạo mới mỗi frame; prefab đã có trong pool không bị giới hạn.")]
        private int maxNewUnitsPerFrame = DefaultNewUnitsPerFrame;
        [SerializeField, Min(1), Tooltip("Số frame để rã hết hàng đợi spawn hiện tại, kiểu UNIT_SLOWUPDATE_RATE của Recoil: " +
            "mỗi frame xử lý khoảng (hàng đợi / giá trị này) lính, nên quân đông thì mỗi frame tự xử lý nhiều hơn theo tỉ lệ " +
            "thay vì bị ghim cứng ở một số lính cố định mỗi frame.")]
        private int spawnQueueRateFrames = DefaultSpawnQueueRateFrames;
        [SerializeField, Min(1), Tooltip("Số thực thể tối đa tạo trong một frame; tạo nhiều hơn để rút ngắn hàng đợi.")]
        private int maxSpawnsPerFrame = DefaultMaxSpawnsPerFrame;
        [SerializeField, Min(1)] private int unitsPerVariantAtStart = 50;
        [SerializeField] private Transform blueSpawnTarget;
        [SerializeField] private Transform redSpawnTarget;
        [SerializeField] private Vector3 battleCenter = Vector3.zero;
        [SerializeField, Min(1f)] private float teamStartDistance = 18f;
        [SerializeField, Min(0.5f)] private float spawnSpacing = 0.7f;
        [SerializeField, Min(1)] private int spawnColumns = 20;
        [SerializeField, Min(1)] private int departuresPerTeamPerTick = 16;
        [SerializeField, Min(0.5f)] private float attackRange = 1.5f;
        [SerializeField, Min(1f)] private float archerAttackRange = 36f;
        [SerializeField, Min(1f)] private float siegeAttackRange = 54f;
        [SerializeField, Min(0.05f)] private float attackInterval = 0.65f;
        [SerializeField, Min(1)] private int attackDamage = 20;
        [SerializeField, Min(0.1f)] private float retargetDistance = 2f;
        [SerializeField, Min(1f)] private float combatUpdatesPerSecond = 10f;
        [SerializeField, Min(1f)] private float detectionRange = 24f;
        [SerializeField, Min(0.1f)] private float targetSearchInterval = 0.5f;
        [SerializeField, Min(1)] private int maxCombatUpdatesPerFrame = 512;
        [SerializeField, Min(2)] private int maxTargetSearchesPerFrame = 128;
        [SerializeField, Min(1)] private int maxTargetCandidatesPerSearch = 128;

        private readonly List<Soldier> _blueTeam = new();
        private readonly List<Soldier> _redTeam = new();
        private readonly Dictionary<GameObject, Queue<GameObject>> _blueUnitPools = new();
        private readonly Dictionary<GameObject, Queue<GameObject>> _redUnitPools = new();
        private bool _templatesValidated;
        private bool _templateValidationFailed;
        private bool _lobbyStarted;
        private bool _lobbyStarting;
        private bool _settingsOpen;
        private bool _matchFinished;
        private int _selectedMatchDurationMinutes = 10;
        private int _selectedLobbyUnitType;
        private float _matchElapsedSeconds;
        private string _lobbyError;
        private string _matchResult;
        private int _templatesValidatedCount;
        private int _templateValidationTotal;
        private string _templateValidationError;
        private int _newUnitsCreatedThisFrame;
        private int _fullDetailSoldierCount;
        private RecoilDotCrowdRenderer _dotRenderer;
        private readonly Queue<SelectedSpawnBatch> _blueSelectedSpawnQueue = new();
        private readonly Queue<SelectedSpawnBatch> _redSelectedSpawnQueue = new();
        private readonly List<Entity> _ownedSoldierEntities = new();
        private readonly List<Soldier> _combatSoldiers = new();
        private readonly List<Soldier> _scheduledSoldiers = new();
        private readonly Dictionary<Entity, int> _combatIndicesByEntity = new();
        private bool _combatRosterDirty = true;
        private System.Predicate<Soldier> _deadSoldierPredicate;
        private System.Predicate<Soldier> _retiredSoldierPredicate;
        private bool _hasRetirements;
        private NativeArray<byte> _combatAliveFlags;
        private EntityQuery _aliveQuery;
        private bool _hasAliveQuery;
        private bool _useAliveFlags;
        public float LastRosterPruneMilliseconds { get; private set; }
        public float LastCombatResolveMilliseconds { get; private set; }
        public int CombatRosterBuildCount { get; private set; }
        private NativeArray<RecoilBattleCombatSnapshot> _combatSnapshots;
        private NativeArray<byte> _combatRangedUnits;
        private NativeArray<int> _scheduledCombatIndices;
        private NativeArray<byte> _scheduledDepartureReleased;
        private NativeList<int> _ecsPathRequests;
        private NativeArray<RecoilBattleCombatResult> _combatResults;
        private NativeArray<int> _damageBySoldier;
        private NativeParallelMultiHashMap<int3, int> _combatCells;
        private NativeArray<int3> _combatPreviousCells;
        private NativeArray<byte> _combatPreviousActive;
        private NativeArray<int> _combatGridMetadata;
        public int LastCombatGridMutations => _combatGridMetadata.IsCreated ? _combatGridMetadata[1] : 0;
        private NativeParallelHashMap<Entity, int> _combatEntityIndices;
        private NativeArray<Entity> _combatSnapshotEntities;
        private NativeArray<RecoilContactAttack> _contactAttacks;
        private NativeList<int> _combatDeaths;
        private EntityQuery _damageQuery;
        private EntityQuery _closeTargetQuery;
        private EntityQuery _combatSnapshotQuery;
        private bool _hasCombatSnapshotQuery;
        private JobHandle _combatJobHandle;
        private int _combatBufferCapacity;
        private int _blueSpawnQueue;
        private int _redSpawnQueue;
        private int _blueSelectedSpawnTotal;
        private int _redSelectedSpawnTotal;
        private int _blueSpawnedTotal;
        private int _redSpawnedTotal;
        private bool _spawnBlueNext = true;
        private bool _spawnFailureReported;
        private float _nextStatusUpdate;
        private float _nextSpawnAttemptTime;
        private float _blueCombatUpdateAccumulator;
        private float _redCombatUpdateAccumulator;
        private int _blueCombatUpdateCursor;
        private int _redCombatUpdateCursor;
        private int _combatUpdatesLastFrame;
        private int _targetCandidateChecksLastFrame;
        private bool _combatJobPending;
        private bool _resultReported;
        private World _world;
        private EntityManager _entityManager;
        private EntityArchetype _soldierArchetype;
        private bool _hasEntityManager;

        private sealed class Soldier
        {
            public bool DepartureReleased;
            public Vector3 FormationDestination;
            public bool FormationAssigned;
            public bool FormationArrived;
            public bool Retired;
            public bool FullDetailCounted;
            public byte CharacterType;
            public byte RangedUnitType;
            public bool IsBlue;
            public float VisualHeight;
            public GameObject Template;
            public RecoilNavMeshPathAgent Agent;
            public RecoilCharacterPresentation Presentation;
            public Collider[] Colliders;
            public RecoilUnitSimulation Simulation;
            public Entity Entity;
            public float AttackStartedAt = float.NegativeInfinity;
            public Soldier AttackTarget;
        }

        private sealed class SelectedSpawnBatch
        {
            public int CharacterType;
            public int Remaining;
        }
        public int TargetCandidateChecksLastFrame => _targetCandidateChecksLastFrame;
        public int CombatUpdatesLastFrame => _combatUpdatesLastFrame;
        public int EcsSoldierEntityCount => _ownedSoldierEntities.Count;
        public int AliveSoldierCount => _blueTeam.Count + _redTeam.Count;
        public int FullDetailSoldierCount => _fullDetailSoldierCount;
        public int VatSoldierCount => 0;
        public int DyingVatSoldierCount => 0;
        public RecoilTerrainMapData TerrainMap => terrainMap;
        public bool CanControlCamera => _lobbyStarted && !_matchFinished;
        public bool ShouldShowGameplayHud => _lobbyStarted && !_matchFinished;

        public void CopyMinimapUnitPositions(List<Vector3> bluePositions, List<Vector3> redPositions)
        {
            if (bluePositions == null) throw new System.ArgumentNullException(nameof(bluePositions));
            if (redPositions == null) throw new System.ArgumentNullException(nameof(redPositions));
            bluePositions.Clear();
            redPositions.Clear();
            CopyMinimapTeamPositions(_blueTeam, bluePositions);
            CopyMinimapTeamPositions(_redTeam, redPositions);
        }

        private static void CopyMinimapTeamPositions(List<Soldier> team, List<Vector3> positions)
        {
            foreach (var soldier in team)
                if (soldier != null && !soldier.Retired &&
                    soldier.Simulation != null && soldier.Simulation.Exists)
                    positions.Add(soldier.Simulation.Position);
        }

        private bool _combatStarted = false;
        private readonly Queue<Soldier> _blueDepartures = new();
        private readonly Queue<Soldier> _redDepartures = new();
        private bool _clockDriven;
        private bool _hasStarted;
        private float _tickDelta = RecoilFixedTickAccumulator.Step;
        private float _tickTime;
        private float SimulationNow => _clockDriven ? _tickTime : Time.time;
        private float SimulationDelta => _clockDriven ? _tickDelta : Time.deltaTime;

        private void Awake()
        {
            DeactivateSceneTemplate(blueUnitTemplate);
            DeactivateSceneTemplate(redUnitTemplate);
            foreach (var template in blueUnitVariants) DeactivateSceneTemplate(template);
            foreach (var template in redUnitVariants) DeactivateSceneTemplate(template);
            foreach (var template in blueUnitsByType) DeactivateSceneTemplate(template);
            foreach (var template in redUnitsByType) DeactivateSceneTemplate(template);
        }

        private void DeactivateSceneTemplate(GameObject template)
        {
            // A scene template is a source, not a combatant. Hide it before agent Start can
            // create a standalone navigation entity with no team/health combat membership.
            if (template == null || !template.scene.IsValid()) return;
            if (template == gameObject || transform.IsChildOf(template.transform))
            {
                Debug.LogError("A soldier template cannot be the battle controller or its parent.", this);
                return;
            }
            template.SetActive(false);
        }

        private void Start()
        {
            if (terrainMap != null && !terrainMap.HasValidData)
            {
                Debug.LogError($"{nameof(RecoilAutoBattleDemo)} has invalid baked map data.", this);
                enabled = false;
                return;
            }
            if (terrain == null && terrainMap == null)
                terrain = FindFirstObjectByType<Terrain>();

            if (terrainMap == null && (terrain == null || terrain.terrainData == null))
            {
                Debug.LogError($"{nameof(RecoilAutoBattleDemo)} requires valid baked map data or a Terrain.", this);
                enabled = false;
                return;
            }

            if (pathService == null || !pathService.IsReady)
            {
                Debug.LogError($"{nameof(RecoilAutoBattleDemo)} requires an initialized path service.", this);
                enabled = false;
                return;
            }

            if (blueUnitTemplate == null || redUnitTemplate == null)
            {
                Debug.LogError($"{nameof(RecoilAutoBattleDemo)} requires both blue and red unit templates.", this);
                enabled = false;
                return;
            }
            InitializeLobbyUnitStats();

            _world = World.DefaultGameObjectInjectionWorld;
            if (_world == null || !_world.IsCreated)
            {
                Debug.LogError($"{nameof(RecoilAutoBattleDemo)} requires an initialized Entities world.", this);
                enabled = false;
                return;
            }

            _entityManager = _world.EntityManager;
            EnsureSpawnTargets();
            _soldierArchetype = _entityManager.CreateArchetype(
                typeof(RecoilBattleEcsState),
                typeof(RecoilUnitPose),
                typeof(RecoilUnitMovementSettings),
                typeof(RecoilUnitOrder),
                typeof(RecoilUnitNavigation),
                typeof(RecoilUnitCombatTiming),
                typeof(RecoilUnitWaypoint));
            _hasEntityManager = true;
            _combatSnapshotQuery = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<RecoilBattleEcsState>(), ComponentType.ReadOnly<RecoilUnitPose>(),
                ComponentType.ReadOnly<RecoilUnitCombatTiming>());
            _hasCombatSnapshotQuery = true;
            CreateCloseTargetQuery();
            _clockDriven = true;
            _hasStarted = true;
            RecoilSimulationClock.Register(this);
            StartCoroutine(ValidateUnitTemplates());
        }

        private void OnEnable()
        {
            if (!_hasStarted || _world == null || !_world.IsCreated) return;
            _clockDriven = true;
            RecoilSimulationClock.Register(this);
        }

        private void OnDisable()
        {
            RecoilSimulationClock.Unregister(this);
            _clockDriven = false;
            DisposeAliveQuery();
            DisposeCombatBuffers();
            if (_hasCombatSnapshotQuery && _world != null && _world.IsCreated)
            {
                _combatSnapshotQuery.Dispose();
                _closeTargetQuery.Dispose();
                _damageQuery.Dispose();
            }
            _hasCombatSnapshotQuery = false;
        }

        private void OnDestroy()
        {
            DisposeAliveQuery();
            if (_combatJobPending)
            {
                _combatJobHandle.Complete();
                _combatJobPending = false;
            }

            DisposeCombatBuffers();
            if (!_hasEntityManager || _world == null || !_world.IsCreated)
                return;
            if (_hasCombatSnapshotQuery)
            {
                _combatSnapshotQuery.Dispose();
                _closeTargetQuery.Dispose();
                _damageQuery.Dispose();
                _hasCombatSnapshotQuery = false;
            }

            foreach (var soldier in _blueTeam)
            {
                soldier.Simulation.SetActive(false);
                if (soldier.Agent != null)
                    soldier.Agent.Bind(null);
            }
            foreach (var soldier in _redTeam)
            {
                soldier.Simulation.SetActive(false);
                if (soldier.Agent != null)
                    soldier.Agent.Bind(null);
            }

            for (var i = 0; i < _ownedSoldierEntities.Count; i++)
            {
                var entity = _ownedSoldierEntities[i];
                if (_entityManager.Exists(entity))
                    _entityManager.DestroyEntity(entity);
            }

            _ownedSoldierEntities.Clear();
        }

        private void Update()
        {
            using (BattleUpdateMarker.Auto())
                UpdateBattle();
        }

        private void UpdateBattle()
        {
            if (!_hasEntityManager || _world == null || !_world.IsCreated)
            {
                Debug.LogError("Battle stopped because its Entities world is no longer available.", this);
                enabled = false;
                return;
            }

            if (!_templatesValidated)
                return;
            if (!_lobbyStarted || _matchFinished)
                return;

            if (!_combatStarted && _lobbyStarting && _formationReady &&
                !_formationDirty && !HasQueuedSpawns)
            {
                var keyboard = UnityEngine.InputSystem.Keyboard.current;
                if (keyboard != null && keyboard.lKey.wasPressedThisFrame)
                    TryStartBattle();
            }

            _newUnitsCreatedThisFrame = 0;
            UpdatePresentationTiers();
            _combatUpdatesLastFrame = 0;
            _targetCandidateChecksLastFrame = 0;
            if (_combatJobPending && !_combatJobHandle.IsCompleted)
                return;

            CompletePendingCombatJob();
            if (!_clockDriven) PruneCombatRoster();
            ProcessSpawnQueue();
            if (!_clockDriven && !_combatStarted) UpdateAssembly();
            if (_clockDriven) return;
            if (!_combatStarted) return;
            _scheduledSoldiers.Clear();
            ScheduleTeamUpdates(
                _blueTeam,
                ref _blueCombatUpdateCursor,
                ref _blueCombatUpdateAccumulator);
            ScheduleTeamUpdates(
                _redTeam,
                ref _redCombatUpdateCursor,
                ref _redCombatUpdateAccumulator);
            if (_blueTeam.Count + _redTeam.Count > 0)
                RunCombatJob();

            if (Time.time >= _nextStatusUpdate)
            {
                _nextStatusUpdate = Time.time + 0.5f;
                if (_blueTeam.Count == 0 || _redTeam.Count == 0)
                {
                    if (_resultReported)
                        return;

                    if (_blueTeam.Count == 0 && _redTeam.Count == 0)
                        Debug.Log("Battle ended in a draw.");
                    else
                        Debug.Log($"Battle result: {(_blueTeam.Count > 0 ? "Blue" : "Red")} wins.");

                    _resultReported = true;
                }
            }
        }

        internal void SimulateFixedTick(float delta, float now)
        {
            if (!_hasEntityManager || _world == null || !_world.IsCreated) return;
            if (!_templatesValidated) return;
            if (!_lobbyStarted || _matchFinished) return;
            _tickDelta = delta;
            _tickTime = now;
            CompletePendingCombatJob();
            if (!_combatStarted)
            {
                if (!_lobbyStarting) return;
                UpdateAssembly();
                return;
            }
            _matchElapsedSeconds += delta;
            if (_matchElapsedSeconds >= _selectedMatchDurationMinutes * 60f)
            {
                FinishMatch("HẾT GIỜ — TRẬN ĐẤU HÒA");
                return;
            }
            using (LifecycleMarker.Auto())
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.Lifecycle)) PruneCombatRoster();
            if (_blueTeam.Count == 0 || _redTeam.Count == 0)
            {
                FinishMatch(_blueTeam.Count == 0 && _redTeam.Count == 0
                    ? "HAI ĐỘI ĐỀU ĐÃ BỊ TIÊU DIỆT — HÒA"
                    : _blueTeam.Count > 0 ? "ĐỘI XANH CHIẾN THẮNG" : "ĐỘI ĐỎ CHIẾN THẮNG");
                return;
            }
            using (DeparturesMarker.Auto())
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.Departures))
            {
                ReleaseDepartures(_blueDepartures);
                ReleaseDepartures(_redDepartures);
            }
            _scheduledSoldiers.Clear();
            ScheduleTeamUpdates(_blueTeam, ref _blueCombatUpdateCursor, ref _blueCombatUpdateAccumulator);
            ScheduleTeamUpdates(_redTeam, ref _redCombatUpdateCursor, ref _redCombatUpdateAccumulator);
            using (RunCombatMarker.Auto())
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.ScheduleCombat))
                if (_blueTeam.Count + _redTeam.Count > 0) RunCombatJob();
            // Resolve damage before the next fixed step, so all phases observe the same tick state.
            using (ResolveMarker.Auto())
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.ResolveCombat)) CompletePendingCombatJob();
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.RetireCombat)) CompactRetiredSoldiers();
        }

        private void InitializeLobbyUnitStats()
        {
            System.Array.Resize(ref lobbyUnitStats, CharacterTypeCount);
            System.Array.Resize(ref lobbyUnitImages, CharacterTypeCount);
            System.Array.Resize(ref lobbyDurationImages, MatchDurationsMinutes.Length);
            for (var type = 0; type < CharacterTypeCount; type++)
            {
                if (lobbyUnitStats[type].IsConfigured) continue;
                var blueTemplate = FindTemplateForType(true, type);
                var redTemplate = FindTemplateForType(false, type);
                var blueStats = blueTemplate != null ? blueTemplate.GetComponent<RecoilUnitStats>() : null;
                var redStats = redTemplate != null ? redTemplate.GetComponent<RecoilUnitStats>() : null;
                if (blueStats != null && blueStats.Stats.IsConfigured)
                    lobbyUnitStats[type] = blueStats.Stats;
                else if (redStats != null && redStats.Stats.IsConfigured)
                    lobbyUnitStats[type] = redStats.Stats;
                else
                    lobbyUnitStats[type] = new RecoilUnitCombatStats
                    {
                        MaxHealth = 100,
                        Damage = 20,
                        Range = 1.5f,
                        AttackSpeed = 1f,
                        MoveSpeed = 3.5f
                    };
            }
        }

        private void ScheduleTeamUpdates(
            List<Soldier> team,
            ref int cursor,
            ref float updateAccumulator)
        {
            var count = team.Count;
            if (count == 0)
            {
                cursor = 0;
                updateAccumulator = 0f;
                return;
            }

            var total = _blueTeam.Count + _redTeam.Count;
            // Proportional per-team budgets preserve fairness even when one army is much larger.
            var budget = Mathf.Max(0, maxCombatUpdatesPerFrame);
            var blueBudget = total > 0 ? (int)((long)budget * _blueTeam.Count / total) : 0;
            var teamBudget = ReferenceEquals(team, _blueTeam) ? blueBudget : budget - blueBudget;
            var updateCount = RecoilBattleScheduling.TakeUpdates(count, SimulationDelta,
                combatUpdatesPerSecond, teamBudget, ref updateAccumulator);
            if (updateCount == 0)
                return;

            for (var i = 0; i < updateCount; i++)
            {
                if (cursor >= count)
                    cursor = 0;

                _scheduledSoldiers.Add(team[cursor]);
                cursor++;
            }

            _combatUpdatesLastFrame += updateCount;
        }

        private void RunCombatJob()
        {
            if (!_hasCombatSnapshotQuery)
            {
                _combatSnapshotQuery = _entityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<RecoilBattleEcsState>(), ComponentType.ReadOnly<RecoilUnitPose>(),
                    ComponentType.ReadOnly<RecoilUnitCombatTiming>());
                _hasCombatSnapshotQuery = true;
                CreateCloseTargetQuery();
            }
            RefreshCombatRoster();
            if (_combatSoldiers.Count == 0)
                return;
            // Chunk iteration reads each component column directly and builds the spatial grid in parallel.
            var snapshotJob = new RecoilBuildCombatSnapshotJob
            {
                Entities = _entityManager.GetEntityTypeHandle(),
                States = _entityManager.GetComponentTypeHandle<RecoilBattleEcsState>(true),
                Poses = _entityManager.GetComponentTypeHandle<RecoilUnitPose>(true),
                Timings = _entityManager.GetComponentTypeHandle<RecoilUnitCombatTiming>(true),
                Indices = _combatEntityIndices, Snapshots = _combatSnapshots, Cells = _combatCells.AsParallelWriter(),
                Now = SimulationNow, DeltaTime = SimulationDelta,
                InitialDelta = 1f / Mathf.Max(1f, combatUpdatesPerSecond), CellSize = TargetGridCellSize,
                SkipGridBuild = 1
            };
            var snapshotStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using (SnapshotBuildMarker.Auto())
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.SnapshotCombat))
            {
                var snapshotHandle = snapshotJob.ScheduleParallel(_combatSnapshotQuery, default);
                var gridJob = new RecoilUpdateCombatGridJob
                {
                    Snapshots = _combatSnapshots, Cells = _combatCells, PreviousCells = _combatPreviousCells,
                    PreviousActive = _combatPreviousActive, Metadata = _combatGridMetadata,
                    Count = _combatSoldiers.Count, CellSize = TargetGridCellSize
                };
                gridJob.Schedule(snapshotHandle).Complete();
            }
            var closeTargets = new RecoilCloseTargetJob
            {
                Entities = _entityManager.GetEntityTypeHandle(),
                Poses = _entityManager.GetComponentTypeHandle<RecoilUnitPose>(false),
                States = _entityManager.GetComponentTypeHandle<RecoilBattleEcsState>(false),
                Navigations = _entityManager.GetComponentTypeHandle<RecoilUnitNavigation>(false),
                Waypoints = _entityManager.GetBufferTypeHandle<RecoilUnitWaypoint>(false),
                Indices = _combatEntityIndices, SnapshotEntities = _combatSnapshotEntities,
                Soldiers = _combatSnapshots, Cells = _combatCells, CellSize = TargetGridCellSize,
                RangeSquared = Mathf.Max(attackRange, 4f) * Mathf.Max(attackRange, 4f),
                MeleeAttackRangeSquared = attackRange * attackRange,
                ArcherAttackRangeSquared = archerAttackRange * archerAttackRange,
                SiegeAttackRangeSquared = siegeAttackRange * siegeAttackRange,
                RangedUnits = _combatRangedUnits,
                Attacks = _contactAttacks, CombatDelta = SimulationDelta,
                AttackInterval = attackInterval, AttackDamage = attackDamage
            };
            closeTargets.ScheduleParallel(_closeTargetQuery, default).Complete();

            // Refresh only the budgeted combatants: close acquisition must survive result application.
            foreach (var soldier in _scheduledSoldiers)
            {
                if (!_combatIndicesByEntity.TryGetValue(soldier.Entity, out var index)) continue;
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
                var snapshot = _combatSnapshots[index];
                snapshot.CurrentTargetIndex = _combatIndicesByEntity.TryGetValue(state.Target, out var target) ? target : -1;
                if (snapshot.CurrentTargetIndex >= 0) snapshot.SearchTarget = 0;
                _combatSnapshots[index] = snapshot;
            }
            LastSnapshotBuildMilliseconds = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - snapshotStart) *
                (1000.0 / System.Diagnostics.Stopwatch.Frequency));
            var blueSearches = 0;
            var redSearches = 0;
            var searchBudget = Mathf.Max(2, maxTargetSearchesPerFrame);
            for (var i = 0; i < _scheduledSoldiers.Count; i++)
            {
                if (!_combatIndicesByEntity.TryGetValue(_scheduledSoldiers[i].Entity, out var combatIndex))
                {
                    _scheduledSoldiers.RemoveAt(i--);
                    continue;
                }

                var snapshot = _combatSnapshots[combatIndex];
                if (snapshot.SearchTarget != 0)
                {
                    if (snapshot.Team == 0)
                    {
                        snapshot.SearchTarget = (byte)(blueSearches < searchBudget / 2 ? 1 : 0);
                        blueSearches += snapshot.SearchTarget;
                    }
                    else
                    {
                        snapshot.SearchTarget = (byte)(redSearches < searchBudget - searchBudget / 2 ? 1 : 0);
                        redSearches += snapshot.SearchTarget;
                    }
                    _combatSnapshots[combatIndex] = snapshot;
                }
                _scheduledCombatIndices[i] = combatIndex;
                _scheduledDepartureReleased[i] = (byte)(_scheduledSoldiers[i].DepartureReleased ? 1 : 0);
            }

            var combatJob = new RecoilBattleCombatJob
            {
                Soldiers = _combatSnapshots,
                ScheduledSoldiers = _scheduledCombatIndices,
                Cells = _combatCells,
                Results = _combatResults,
                MinCellX = -1000000000,
                MaxCellX = 1000000000,
                MinCellZ = -1000000000,
                MaxCellZ = 1000000000,
                CellSize = TargetGridCellSize,
                DetectionRangeSquared = Mathf.Max(
                    detectionRange, Mathf.Max(archerAttackRange, siegeAttackRange)) *
                    Mathf.Max(detectionRange, Mathf.Max(archerAttackRange, siegeAttackRange)),
                MaxCandidateChecks = Mathf.Max(1, maxTargetCandidatesPerSearch),
                AttackRangeSquared = attackRange * attackRange,
                AttackInterval = attackInterval,
                AttackDamage = 0, // Contact damage has its own all-unit pass.
                BlueCount = _blueTeam.Count,
                RedCount = _redTeam.Count
            };
            // Finish every main-thread snapshot edit before either reader is scheduled.
            var aggregate = new RecoilAggregateDamageJob
            {
                Attacks = _contactAttacks, Damage = _damageBySoldier, Count = _combatSoldiers.Count,
                Soldiers = _combatSnapshots, Cells = _combatCells, CellSize = TargetGridCellSize
            }.Schedule();
            _combatJobHandle = aggregate;
            _combatJobPending = true;
            using (CombatJobMarker.Auto())
                _combatJobHandle = _scheduledSoldiers.Count > 0
                    ? combatJob.Schedule(_scheduledSoldiers.Count, 32, aggregate) : aggregate;
        }

        private void CreateCloseTargetQuery()
        {
            _damageQuery = _entityManager.CreateEntityQuery(ComponentType.ReadWrite<RecoilBattleEcsState>());
            _closeTargetQuery = _entityManager.CreateEntityQuery(ComponentType.ReadWrite<RecoilBattleEcsState>(),
                ComponentType.ReadWrite<RecoilUnitNavigation>(), ComponentType.ReadWrite<RecoilUnitWaypoint>(), ComponentType.ReadWrite<RecoilUnitPose>());
        }

        private void CompletePendingCombatJob()
        {
            if (!_combatJobPending) return;
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            try { CompletePendingCombatJobCore(); }
            finally
            {
                LastCombatResolveMilliseconds = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - start) *
                    (1000.0 / System.Diagnostics.Stopwatch.Frequency));
            }
        }

        private void CompletePendingCombatJobCore()
        {
            if (!_combatJobPending)
                return;

            _combatJobHandle.Complete();
            _combatJobPending = false;

            if (pathService.UsingTerrainMap)
            {
                _ecsPathRequests.Clear();
                var resolve = new RecoilResolveCombatJob
                {
                    Entities = _combatSnapshotEntities, Scheduled = _scheduledCombatIndices,
                    Released = _scheduledDepartureReleased, Snapshots = _combatSnapshots, Results = _combatResults,
                    Heights = pathService.TerrainHeightField, PathRequests = _ecsPathRequests.AsParallelWriter(),
                    Now = SimulationNow, SearchInterval = targetSearchInterval,
                    AttackRange = attackRange, ArcherAttackRange = archerAttackRange,
                    SiegeAttackRange = siegeAttackRange, RangedUnits = _combatRangedUnits,
                    RetargetDistance = retargetDistance, BlueGoal = GetSpawnTargetPosition(false),
                    BlueMarchTarget = GetCastleMarchTarget(true), RedMarchTarget = GetCastleMarchTarget(false),
                    RedGoal = GetSpawnTargetPosition(true), SoldierCount = _combatSoldiers.Count
                };
                _world.GetOrCreateSystemManaged<RecoilCombatResolveSystem>()
                    .ScheduleResolve(resolve, _scheduledSoldiers.Count).Complete();
                for (var i = 0; i < _scheduledSoldiers.Count; i++)
                    _targetCandidateChecksLastFrame += _combatResults[i].CandidateChecks;
                // The callback-based path service is still a managed boundary. Only new
                // path requests cross it; state, orders and stop decisions were applied in ECS.
                for (var i = 0; i < _ecsPathRequests.Length; i++)
                    _combatSoldiers[_ecsPathRequests[i]].Simulation.RequestPathFromEcs();
            }
            else for (var i = 0; i < _scheduledSoldiers.Count; i++)
            {
                var combatIndex = _scheduledCombatIndices[i];
                var soldier = _combatSoldiers[combatIndex];
                var result = _combatResults[i];
                _targetCandidateChecksLastFrame += result.CandidateChecks;
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
                var timing = _entityManager.GetComponentData<RecoilUnitCombatTiming>(soldier.Entity);
                timing.LastCombatUpdateTime = SimulationNow;
                var target = result.TargetIndex >= 0
                    ? _combatSoldiers[result.TargetIndex]
                    : null;

                state.Target = target != null ? target.Entity : Entity.Null;
                _entityManager.SetComponentData(soldier.Entity, state);

                if (_combatSnapshots[combatIndex].SearchTarget != 0)
                    timing.NextTargetSearchTime = SimulationNow + targetSearchInterval;
                _entityManager.SetComponentData(soldier.Entity, timing);
                UpdateNavigationBridge(soldier, target);

            }

            _combatDeaths.Clear();
            new RecoilApplyDamageJob
            {
                Entities = _entityManager.GetEntityTypeHandle(),
                States = _entityManager.GetComponentTypeHandle<RecoilBattleEcsState>(false),
                Indices = _combatEntityIndices, Damage = _damageBySoldier, Deaths = _combatDeaths.AsParallelWriter()
            }.ScheduleParallel(_damageQuery, default).Complete();
            for (var i = 0; i < _combatSoldiers.Count; i++)
            {
                var attack = _contactAttacks[i];
                if (attack.TargetIndex < 0 || attack.TargetIndex >= _combatSoldiers.Count)
                    continue;

                var attacker = _combatSoldiers[i];
                var target = _combatSoldiers[attack.TargetIndex];
                attacker.AttackStartedAt = SimulationNow;
                attacker.AttackTarget = target;
                if (attacker.Agent != null)
                    attacker.Presentation?.PlayAttack(target.Agent != null ? target.Agent.transform : null);
            }
            for (var i = 0; i < _combatDeaths.Length; i++) KillSoldier(_combatSoldiers[_combatDeaths[i]]);
        }

        private void AddCombatSoldiers(List<Soldier> team)
        {
            // UpdateBattle already pruned dead/missing entities before spawning and scheduling.
            // Do not repeat per-entity EntityManager reads during snapshot list construction.
            for (var i = 0; i < team.Count; i++)
            {
                var soldier = team[i];
                _combatIndicesByEntity.Add(soldier.Entity, _combatSoldiers.Count);
                _combatSoldiers.Add(soldier);
            }
        }

        private void RefreshCombatRoster()
        {
            if (!_combatRosterDirty) return;
            _combatSoldiers.Clear();
            _combatIndicesByEntity.Clear();
            AddCombatSoldiers(_blueTeam);
            AddCombatSoldiers(_redTeam);
            if (_combatSoldiers.Count > 0)
            {
                EnsureCombatBuffers(_combatSoldiers.Count);
                _combatEntityIndices.Clear();
                foreach (var entry in _combatIndicesByEntity)
                {
                    _combatEntityIndices.Add(entry.Key, entry.Value);
                    _combatSnapshotEntities[entry.Value] = entry.Key;
                }
                for (var i = 0; i < _combatSoldiers.Count; i++)
                    _combatRangedUnits[i] = _combatSoldiers[i].RangedUnitType;
            }
            _combatRosterDirty = false;
            CombatRosterBuildCount++;
        }

        private void EnsureCombatBuffers(int soldierCount)
        {
            if (soldierCount <= _combatBufferCapacity)
                return;

            DisposeCombatBuffers();
            _combatBufferCapacity = Mathf.NextPowerOfTwo(Mathf.Max(4, soldierCount));
            _scheduledDepartureReleased = new NativeArray<byte>(_combatBufferCapacity, Allocator.Persistent);
            _ecsPathRequests = new NativeList<int>(_combatBufferCapacity, Allocator.Persistent);
            _combatSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(
                _combatBufferCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _combatRangedUnits = new NativeArray<byte>(_combatBufferCapacity, Allocator.Persistent);
            _scheduledCombatIndices = new NativeArray<int>(
                _combatBufferCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _combatResults = new NativeArray<RecoilBattleCombatResult>(
                _combatBufferCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _damageBySoldier = new NativeArray<int>(
                _combatBufferCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _combatCells = new NativeParallelMultiHashMap<int3, int>(
                _combatBufferCapacity,
                Allocator.Persistent);
            _combatPreviousCells = new NativeArray<int3>(_combatBufferCapacity, Allocator.Persistent);
            _combatPreviousActive = new NativeArray<byte>(_combatBufferCapacity, Allocator.Persistent);
            _combatGridMetadata = new NativeArray<int>(2, Allocator.Persistent);
            _combatEntityIndices = new NativeParallelHashMap<Entity, int>(_combatBufferCapacity, Allocator.Persistent);
            _combatSnapshotEntities = new NativeArray<Entity>(_combatBufferCapacity, Allocator.Persistent);
            _contactAttacks = new NativeArray<RecoilContactAttack>(_combatBufferCapacity, Allocator.Persistent);
            _combatAliveFlags = new NativeArray<byte>(_combatBufferCapacity, Allocator.Persistent);
            _combatDeaths = new NativeList<int>(_combatBufferCapacity, Allocator.Persistent);
        }

        private void DisposeCombatBuffers()
        {
            _combatRosterDirty = true;
            if (_combatJobPending)
            {
                _combatJobHandle.Complete();
                _combatJobPending = false;
            }

            if (_combatSnapshots.IsCreated)
                _combatSnapshots.Dispose();
            if (_combatRangedUnits.IsCreated)
                _combatRangedUnits.Dispose();
            if (_scheduledCombatIndices.IsCreated)
                _scheduledCombatIndices.Dispose();
            if (_scheduledDepartureReleased.IsCreated) _scheduledDepartureReleased.Dispose();
            if (_ecsPathRequests.IsCreated) _ecsPathRequests.Dispose();
            if (_combatResults.IsCreated)
                _combatResults.Dispose();
            if (_damageBySoldier.IsCreated)
                _damageBySoldier.Dispose();
            if (_combatCells.IsCreated)
                _combatCells.Dispose();
            if (_combatPreviousCells.IsCreated) _combatPreviousCells.Dispose();
            if (_combatPreviousActive.IsCreated) _combatPreviousActive.Dispose();
            if (_combatGridMetadata.IsCreated) _combatGridMetadata.Dispose();
            if (_combatEntityIndices.IsCreated)
                _combatEntityIndices.Dispose();
            if (_combatSnapshotEntities.IsCreated) _combatSnapshotEntities.Dispose();
            if (_contactAttacks.IsCreated) _contactAttacks.Dispose();
            if (_combatAliveFlags.IsCreated) _combatAliveFlags.Dispose();
            if (_combatDeaths.IsCreated) _combatDeaths.Dispose();

            _combatBufferCapacity = 0;
        }

        private bool HasVariantTeams =>
            blueUnitVariants != null && blueUnitVariants.Length > 0 &&
            redUnitVariants != null && redUnitVariants.Length > 0;

        private void ProcessSpawnQueue()
        {
            if (Time.time < _nextSpawnAttemptTime)
                return;

            var backlog = _blueSpawnQueue + _redSpawnQueue +
                _blueSelectedSpawnTotal + _redSelectedSpawnTotal;
            if (backlog <= 0)
                return;

            // Scale work with the backlog, then cap it to avoid a single frame monopolizing the
            // main thread. Formation slots are cached for the whole queue instead of rescanning
            // every soldier and pending spawn for each new unit.
            var spawnsThisFrame = Mathf.Min(maxSpawnsPerFrame, backlog / Mathf.Max(1, spawnQueueRateFrames) + 1);
            for (var i = 0; i < spawnsThisFrame && HasQueuedSpawns; i++)
            {
                var hasBlue = _blueSpawnQueue > 0 || _blueSelectedSpawnTotal > 0;
                var hasRed = _redSpawnQueue > 0 || _redSelectedSpawnTotal > 0;
                var spawnBlue = hasBlue && (!hasRed || _spawnBlueNext);
                var selectedQueue = spawnBlue ? _blueSelectedSpawnQueue : _redSelectedSpawnQueue;
                var hasSelectedType = selectedQueue.Count > 0;
                var requestedType = hasSelectedType ? selectedQueue.Peek().CharacterType : -1;
                var spawnIndex = spawnBlue ? _blueSpawnedTotal : _redSpawnedTotal;
                var template = requestedType >= 0
                    ? FindTemplateForType(spawnBlue, requestedType)
                    : GetUnitTemplate(spawnBlue, spawnIndex);
                if (!SpawnUnit(spawnBlue, requestedType))
                {
                    _nextSpawnAttemptTime = Time.time + 1f;
                    if (!_spawnFailureReported)
                    {
                        Debug.LogWarning(
                            "Lính tạm dừng spawn: không tìm thấy ô walkable nào trên baked terrain graph. " +
                            "Đơn vị đang chờ vẫn được giữ lại; hãy kiểm tra map bake và vùng walkable.",
                            this);
                        _spawnFailureReported = true;
                    }

                    return;
                }

                _spawnFailureReported = false;
                if (spawnBlue)
                {
                    if (hasSelectedType)
                    {
                        var batch = selectedQueue.Peek();
                        batch.Remaining--;
                        _blueSelectedSpawnTotal--;
                        if (batch.Remaining <= 0)
                            selectedQueue.Dequeue();
                    }
                    else
                        _blueSpawnQueue--;
                }
                else
                {
                    if (hasSelectedType)
                    {
                        var batch = selectedQueue.Peek();
                        batch.Remaining--;
                        _redSelectedSpawnTotal--;
                        if (batch.Remaining <= 0)
                            selectedQueue.Dequeue();
                    }
                    else
                        _redSpawnQueue--;
                }

                _spawnBlueNext = !spawnBlue;
            }

        }

        private IEnumerator ValidateUnitTemplates()
        {
            _templateValidationTotal = CharacterTypeCount * 2;
            for (var type = 0; type < CharacterTypeCount; type++)
            {
                if (FindTemplateForType(true, type) == null || FindTemplateForType(false, type) == null)
                {
                    _templateValidationError =
                        $"Thiếu mẫu lính loại {CharacterTypeLabels[type]} cho một trong hai phe. " +
                        "Hãy gán đủ 7 loại lính cho cả hai phe trong Game Tools/Công Thành/Set Up.";
                    _templateValidationFailed = true;
                    Debug.LogError(_templateValidationError, this);
                    yield break;
                }
                _templatesValidatedCount += 2;
            }

            _templatesValidated = true;
            RecoilSimulationClock.Register(this);
            yield break;
        }

        private bool HasQueuedSpawns =>
            _blueSpawnQueue > 0 || _redSpawnQueue > 0 ||
            _blueSelectedSpawnTotal > 0 || _redSelectedSpawnTotal > 0;

        private bool StartMatchFromLobby()
        {
            if (!_templatesValidated || _templateValidationFailed || _lobbyStarted)
                return false;

            for (var type = 0; type < CharacterTypeCount; type++)
            {
                if (FindTemplateForType(true, type) == null || FindTemplateForType(false, type) == null)
                {
                    _lobbyError = $"Thiếu prefab cho loại quân {CharacterTypeLabels[type]} ở một trong hai phe.";
                    return false;
                }
            }

            _lobbyError = null;
            _lobbyStarted = true;
            _settingsOpen = false;
            _matchElapsedSeconds = 0f;
            return true;
        }

        private void FinishMatch(string result)
        {
            if (_matchFinished) return;
            _matchFinished = true;
            _matchResult = result;
            foreach (var soldier in _blueTeam)
                if (soldier != null && soldier.Simulation != null)
                    soldier.Simulation.SetActive(false);
            foreach (var soldier in _redTeam)
                if (soldier != null && soldier.Simulation != null)
                    soldier.Simulation.SetActive(false);
        }

        private void QueueSelectedTypeSpawn(int characterType)
        {
            if (characterType < 0 || characterType >= CharacterTypeCount)
                return;
            if (FindTemplateForType(true, characterType) == null ||
                FindTemplateForType(false, characterType) == null)
            {
                Debug.LogError(
                    $"Chưa gán mẫu lính {CharacterTypeLabels[characterType]} cho đủ hai phe. Mở Game Tools/Công Thành/Set Up để gán.",
                    this);
                return;
            }

            _blueSelectedSpawnQueue.Enqueue(new SelectedSpawnBatch
            { CharacterType = characterType, Remaining = UnitsPerManualSpawn });
            _redSelectedSpawnQueue.Enqueue(new SelectedSpawnBatch
            { CharacterType = characterType, Remaining = UnitsPerManualSpawn });
            _blueSelectedSpawnTotal += UnitsPerManualSpawn;
            _redSelectedSpawnTotal += UnitsPerManualSpawn;
            if (!_combatStarted)
            {
                InvalidateSpawnFormationPlans();
                _lobbyStarting = true;
                InvalidateAssembly();
            }
        }

        private bool SpawnUnit(bool isBlue, int requestedType = -1)
        {
            var team = isBlue ? _blueTeam : _redTeam;
            var spawnIndex = isBlue ? _blueSpawnedTotal : _redSpawnedTotal;
            var template = requestedType >= 0
                ? FindTemplateForType(isBlue, requestedType)
                : GetUnitTemplate(isBlue, spawnIndex);
            if (template == null)
            {
                Debug.LogError(
                    $"No character prefab is configured for the {(isBlue ? "blue" : "red")} team.",
                    this);
                return false;
            }
            var characterType = (byte)Mathf.Clamp(requestedType >= 0 ? requestedType : ResolveCharacterType(template), 0, CharacterTypeCount - 1);
            var initialSpawn = !_combatStarted;
            var spawn = default(NavMeshHit);
            var hasSpawnPosition = initialSpawn
                ? TryFindInitialFormationSpawnPosition(isBlue, characterType, out spawn)
                : requestedType >= 0
                    ? TryFindSelectedTypeSpawnPosition(isBlue, out spawn)
                    : TryFindSpawnPosition(isBlue, spawnIndex, out spawn);
            if (!hasSpawnPosition)
                return false;

            var rotation = initialSpawn
                ? Quaternion.LookRotation(BattleAxis() * (isBlue ? 1f : -1f))
                : Quaternion.identity;
            var rangedUnitType = characterType == 4 ? (byte)2 : characterType == 0 ? (byte)1 : (byte)0;
            var configuredPresentation = template.GetComponent<RecoilCharacterPresentation>();
            if (configuredPresentation != null && configuredPresentation.HasRangedAttackOverride)
                rangedUnitType = configuredPresentation.IsRangedAttack
                    ? (characterType == 4 ? (byte)2 : (byte)1) : (byte)0;
            var statsSource = template.GetComponent<RecoilUnitStats>();
            var stats = statsSource != null ? statsSource.Stats : default;
            if (characterType < lobbyUnitStats.Length && lobbyUnitStats[characterType].IsConfigured)
                stats = lobbyUnitStats[characterType];
            var templateAgent = template.GetComponent<RecoilNavMeshPathAgent>();
            var movementSettings = templateAgent != null
                ? templateAgent.MovementSettings
                : new RecoilUnitMovementSettings
                {
                    MaxSpeed = 3.5f,
                    Acceleration = 12f,
                    TurnSpeedDegrees = 540f,
                    WaypointTolerance = 0.25f,
                    AvoidanceRadius = 1.2f,
                    AvoidanceStrength = 1f,
                    BodyRadius = 0.5f,
                    Mass = 1f,
                    VerticalOffset = 1f,
                    AreaMask = NavMesh.AllAreas
                };
            if (stats.IsConfigured) movementSettings.MaxSpeed = stats.MoveSpeed;

            var entity = _entityManager.CreateEntity(_soldierArchetype);
            _ownedSoldierEntities.Add(entity);
            _entityManager.SetComponentData(entity, new RecoilBattleEcsState
            {
                Team = isBlue ? 0 : 1,
                Health = stats.IsConfigured ? stats.MaxHealth : 100,
                Stats = stats,
                AttackCooldown = 0f,
                Active = 1,
                Target = Entity.Null
            });

            var simulation = new RecoilUnitSimulation(_world, entity, pathService,
                spawn.position + Vector3.up, rotation, movementSettings);
            simulation.SetAssemblyMovement(false);
            var soldier = new Soldier
            {
                Simulation = simulation,
                Entity = entity,
                IsBlue = isBlue,
                Template = template,
                VisualHeight = configuredPresentation != null
                    ? configuredPresentation.NormalizedVisualHeight : 3f,
                CharacterType = characterType,
                RangedUnitType = rangedUnitType
            };
            team.Add(soldier);
            if (!_combatStarted)
            {
                var formationIndices = isBlue
                    ? _blueSpawnFormationIndices
                    : _redSpawnFormationIndices;
                if (formationIndices != null)
                    formationIndices[characterType]++;
                InvalidateAssembly();
            }
            (isBlue ? _blueDepartures : _redDepartures).Enqueue(soldier);
            _combatRosterDirty = true;
            if (isBlue)
                _blueSpawnedTotal++;
            else
                _redSpawnedTotal++;
            _resultReported = false;
            return true;
        }

        private void UpdatePresentationTiers()
        {
            if (_dotRenderer == null)
            {
                _dotRenderer = GetComponent<RecoilDotCrowdRenderer>();
                if (_dotRenderer == null)
                    _dotRenderer = gameObject.AddComponent<RecoilDotCrowdRenderer>();
            }

            using (PresentationMarker.Auto())
            {
                var camera = Camera.main;
                var hasFocusPoint = TryGetScreenCenterGroundPoint(camera, out var focusPoint);
                var prefabRadiusSquared = PrefabRenderRadius * PrefabRenderRadius;

                _dotRenderer.BeginFrame();
                UpdatePresentationTeam(_blueTeam, focusPoint, hasFocusPoint, prefabRadiusSquared);
                UpdatePresentationTeam(_redTeam, focusPoint, hasFocusPoint, prefabRadiusSquared);
                _dotRenderer.EndFrame();
            }
        }

        private bool TryGetScreenCenterGroundPoint(Camera camera, out Vector3 point)
        {
            point = default;
            if (camera == null)
                return false;

            var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
            if (terrain != null)
            {
                var terrainCollider = terrain.GetComponent<TerrainCollider>();
                if (terrainCollider != null &&
                    terrainCollider.Raycast(ray, out var terrainHit, camera.farClipPlane))
                {
                    point = terrainHit.point;
                    return true;
                }
            }

            if (Physics.Raycast(ray, out var hit, camera.farClipPlane, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                point = hit.point;
                if (TryGetGroundHeight(point, out var height))
                    point.y = height;
                return true;
            }

            var groundHeight = terrain != null
                ? terrain.transform.position.y
                : battleCenter.y;
            if (!new Plane(Vector3.up, Vector3.up * groundHeight).Raycast(ray, out var distance) ||
                distance < 0f)
                return false;

            point = ray.GetPoint(distance);
            if (TryGetGroundHeight(point, out var sampledHeight))
                point.y = sampledHeight;
            return true;
        }

        private void UpdatePresentationTeam(
            List<Soldier> team,
            Vector3 focusPoint,
            bool hasFocusPoint,
            float prefabRadiusSquared)
        {
            for (var i = 0; i < team.Count; i++)
            {
                var soldier = team[i];
                if (soldier == null || soldier.Retired || soldier.Simulation == null ||
                    !soldier.Simulation.Exists)
                    continue;

                var offset = soldier.Simulation.Position - focusPoint;
                offset.y = 0f;
                var wantsPrefab = hasFocusPoint
                    ? offset.sqrMagnitude <= prefabRadiusSquared
                    : soldier.Agent != null;

                if (wantsPrefab)
                {
                    if (soldier.Agent == null)
                        TryAttachPrefabView(soldier);
                }
                else if (soldier.Agent != null)
                {
                    ReturnPrefabViewToPool(soldier);
                }

                if (soldier.Agent == null)
                    _dotRenderer.Submit(soldier.Simulation.Position, soldier.IsBlue);
            }
        }

        private bool TryAttachPrefabView(Soldier soldier)
        {
            var unitObject = GetOrCreatePooledUnit(soldier.Template, soldier.IsBlue);
            if (unitObject == null)
                return false;

            var agent = unitObject.GetComponent<RecoilNavMeshPathAgent>();
            if (agent == null)
            {
                agent = unitObject.AddComponent<RecoilNavMeshPathAgent>();
                if (pathService != null)
                    agent.ConfigurePathService(pathService);
            }
            if (agent == null)
            {
                Debug.LogError($"Prefab '{unitObject.name}' could not be given a {nameof(RecoilNavMeshPathAgent)}.", unitObject);
                GetUnitPool(soldier.IsBlue, soldier.Template).Enqueue(unitObject);
                return false;
            }

            unitObject.transform.SetPositionAndRotation(soldier.Simulation.Position, soldier.Simulation.Rotation);
            unitObject.name = $"{(soldier.IsBlue ? "Blue" : "Red")} Soldier {soldier.Entity.Index}";
            unitObject.SetActive(true);
            var presentations = unitObject.GetComponentsInChildren<RecoilCharacterPresentation>(true);
            foreach (var presentation in presentations)
                presentation.ResetForReuse();
            var colliders = unitObject.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < colliders.Length; i++)
                colliders[i].enabled = true;

            agent.Bind(soldier.Simulation);
            soldier.Agent = agent;
            soldier.Presentation = unitObject.GetComponent<RecoilCharacterPresentation>() ??
                (presentations.Length > 0 ? presentations[0] : null);
            soldier.Colliders = colliders;
            if (!soldier.FullDetailCounted)
            {
                soldier.FullDetailCounted = true;
                _fullDetailSoldierCount++;
            }

            var attackClip = soldier.Presentation != null ? soldier.Presentation.AttackClip : null;
            var attackDuration = attackClip != null ? attackClip.length : attackInterval;
            if (soldier.Presentation != null &&
                SimulationNow - soldier.AttackStartedAt < attackDuration)
            {
                var attackTarget = soldier.AttackTarget != null && soldier.AttackTarget.Agent != null
                    ? soldier.AttackTarget.Agent.transform
                    : null;
                soldier.Presentation.PlayAttack(attackTarget);
            }
            return true;
        }

        private void ReturnPrefabViewToPool(Soldier soldier)
        {
            var agent = soldier.Agent;
            if (agent == null)
                return;

            var unitObject = agent.gameObject;
            agent.Bind(null);
            soldier.Agent = null;
            soldier.Presentation = null;
            soldier.Colliders = null;
            if (soldier.FullDetailCounted)
            {
                soldier.FullDetailCounted = false;
                _fullDetailSoldierCount = Mathf.Max(0, _fullDetailSoldierCount - 1);
            }

            unitObject.SetActive(false);
            unitObject.transform.SetParent(transform, true);
            GetUnitPool(soldier.IsBlue, soldier.Template).Enqueue(unitObject);
        }

        private GameObject GetOrCreatePooledUnit(GameObject template, bool isBlue)
        {
            var pool = GetUnitPool(isBlue, template);
            while (pool.Count > 0)
            {
                var unit = pool.Dequeue();
                if (unit == null)
                    continue;
                unit.transform.SetParent(transform, true);
                return unit;
            }

            if (_newUnitsCreatedThisFrame >= Mathf.Max(1, maxNewUnitsPerFrame))
                return null;

            var created = Instantiate(template, transform);
            created.SetActive(false);
            _newUnitsCreatedThisFrame++;
            return created;
        }

        private Queue<GameObject> GetUnitPool(bool isBlue, GameObject template)
        {
            if (template == null)
                throw new System.ArgumentNullException(nameof(template));
            var pools = isBlue ? _blueUnitPools : _redUnitPools;
            if (!pools.TryGetValue(template, out var pool))
            {
                pool = new Queue<GameObject>();
                pools.Add(template, pool);
            }
            return pool;
        }

        private static byte GetRangedUnitType(GameObject unit)
        {
            var characterType = GetCharacterType(unit);
            return characterType == 4 ? (byte)2 : characterType == 0 ? (byte)1 : (byte)0;
        }

        private static int GetCharacterType(GameObject unit)
        {
            if (unit == null)
                return -1;
            var name = unit.name.Replace("_", "").Replace(" ", "").Replace("-", "");
            if (name.IndexOf("CUNGKY", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 0;
            if (name.IndexOf("DAOBINH", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("BOBINH", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("DANBINH", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 1;
            if (name.IndexOf("GIAOBINH", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;
            if (name.IndexOf("KYBINH", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 3;
            if (name.IndexOf("MAYDA", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("MAYBANDA", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 4;
            if (name.IndexOf("QUANSU", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("QUAN_SU", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 5;
            if (name.IndexOf("VOI", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return 6;
            return -1;
        }

        private GameObject FindTemplateForType(bool isBlue, int characterType)
        {
            var assigned = isBlue ? blueUnitsByType : redUnitsByType;
            if (assigned != null && characterType >= 0 && characterType < assigned.Length && assigned[characterType] != null)
                return assigned[characterType];
            var variants = isBlue ? blueUnitVariants : redUnitVariants;
            if (variants == null)
                return null;
            for (var i = 0; i < variants.Length; i++)
                if (GetCharacterType(variants[i]) == characterType)
                    return variants[i];
            var fallback = isBlue ? blueUnitTemplate : redUnitTemplate;
            return GetCharacterType(fallback) == characterType ? fallback : null;
        }

        public GameObject GetTemplateForType(bool isBlue, int characterType) => FindTemplateForType(isBlue, characterType);

        private int ResolveCharacterType(GameObject template)
        {
            for (var i = 0; i < CharacterTypeCount; i++)
            {
                if ((blueUnitsByType != null && i < blueUnitsByType.Length && blueUnitsByType[i] == template) ||
                    (redUnitsByType != null && i < redUnitsByType.Length && redUnitsByType[i] == template)) return i;
            }
            return GetCharacterType(template);
        }

        private GameObject GetUnitTemplate(bool isBlue, int spawnIndex)
        {
            var variants = isBlue ? blueUnitVariants : redUnitVariants;
            if (variants == null || variants.Length == 0)
                return isBlue ? blueUnitTemplate : redUnitTemplate;

            var initialFormationSize = Mathf.Max(1, unitsPerVariantAtStart);
            var variantIndex = (spawnIndex / initialFormationSize) % variants.Length;
            var template = variants[variantIndex];
            return template != null
                ? template
                : (isBlue ? blueUnitTemplate : redUnitTemplate);
        }

        private bool TryFindSpawnPosition(bool isBlue, int spawnIndex, out NavMeshHit spawn)
        {
            spawn = default;
            var variants = isBlue ? blueUnitVariants : redUnitVariants;
            if (variants != null && variants.Length > 0)
                return TryFindVariantSpawnPosition(isBlue, spawnIndex, variants.Length, out spawn);
            if (terrainMap != null && terrainMap.HasValidData)
                return TryFindMapSpawnPosition(isBlue, spawnIndex, out spawn);

            var origin = terrain.transform.position;
            var size = terrain.terrainData.size;
            var spacing = Mathf.Max(0.5f, spawnSpacing);
            var margin = Mathf.Min(spacing, Mathf.Min(size.x, size.z) * 0.1f);
            var minX = origin.x + margin;
            var maxX = origin.x + size.x - margin;
            var minZ = origin.z + margin;
            var maxZ = origin.z + size.z - margin;
            var side = isBlue ? -1f : 1f;
            var anchor = GetSpawnTargetPosition(isBlue);
            var startX = Mathf.Clamp(anchor.x, minX, maxX);
            var availableDepth = isBlue ? startX - minX : maxX - startX;
            var rows = Mathf.Max(1, Mathf.FloorToInt(availableDepth / spacing) + 1);
            var columns = Mathf.Clamp(Mathf.FloorToInt((maxZ - minZ) / spacing) + 1, 1, Mathf.Max(1, spawnColumns));
            var startZ = Mathf.Clamp(anchor.z - (columns - 1) * spacing * 0.5f,
                minZ, Mathf.Max(minZ, maxZ - (columns - 1) * spacing));
            var capacity = (long)rows * columns;
            // Reuse the finite deployment zone instead of extending rows beyond the terrain forever.
            // Try alternate slots when a hole or obstacle makes the preferred slot unwalkable.
            var attempts = (int)System.Math.Min(64L, capacity);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var slot = ((long)spawnIndex + attempt) % capacity;
                // Fill from the middle outward, alternating both flanks before adding another row.
                var ordinal = (int)(slot % columns);
                var column = (columns - 1) / 2 + (ordinal % 2 == 1 ? (ordinal + 1) / 2 : -ordinal / 2);
                var desired = new Vector3(startX + side * (slot / columns) * spacing, 0f,
                    startZ + column * spacing);
                desired.y = terrain.SampleHeight(desired) + origin.y;
                if (pathService.TrySamplePosition(desired, 5f, NavMesh.AllAreas, out var position))
                {
                    spawn = new NavMeshHit { position = position };
                    return true;
                }
            }
            return false;
        }

        private bool TryFindSelectedTypeSpawnPosition(bool isBlue, out NavMeshHit spawn)
        {
            spawn = default;
            var desired = GetSpawnTargetPosition(isBlue);
            if (terrainMap != null && terrainMap.HasValidData)
                return TryFindWalkableTerrainSpawn(desired, out spawn);
            if (!TryGetGroundHeight(desired, out var groundHeight))
                return false;

            desired.y = groundHeight;
            var projectionDistance = Mathf.Max(3f, spawnSpacing * 2f);
            if (!pathService.TrySamplePosition(
                    desired, projectionDistance, NavMesh.AllAreas, out var position))
                return false;
            spawn = new NavMeshHit { position = position };
            return true;
        }

        private bool TryFindVariantSpawnPosition(
            bool isBlue,
            int spawnIndex,
            int variantCount,
            out NavMeshHit spawn)
        {
            spawn = default;
            var unitsPerVariant = Mathf.Max(1, unitsPerVariantAtStart);
            var variantIndex = (spawnIndex / unitsPerVariant) % variantCount;
            var unitIndex = spawnIndex % unitsPerVariant;
            var columnCount = Mathf.Min(VariantFormationColumns, unitsPerVariant);
            var rowCount = Mathf.CeilToInt((float)unitsPerVariant / columnCount);
            var variantColumns = Mathf.CeilToInt(Mathf.Sqrt(variantCount));
            var variantRow = variantIndex / variantColumns;
            var variantColumn = variantIndex % variantColumns;

            var blueAnchor = GetSpawnTargetPosition(true);
            var redAnchor = GetSpawnTargetPosition(false);
            var axis = redAnchor - blueAnchor;
            axis.y = 0f;
            if (axis.sqrMagnitude < 0.001f)
                axis = Vector3.right;
            else
                axis.Normalize();
            var forward = isBlue ? axis : -axis;
            var lateral = new Vector3(-axis.z, 0f, axis.x);
            var anchor = isBlue ? blueAnchor : redAnchor;
            var spacing = Mathf.Max(1f, spawnSpacing);
            var formationWidth = columnCount * spacing + spacing;
            var formationDepth = rowCount * spacing + spacing;
            var lateralBlockOffset =
                (variantColumn - (variantColumns - 1) * 0.5f) * formationWidth;
            var forwardBlockOffset = 10f + variantRow * formationDepth;
            var row = unitIndex / columnCount;
            var column = unitIndex % columnCount;
            var desired = anchor +
                          forward * (forwardBlockOffset +
                                     (row - (rowCount - 1) * 0.5f) * spacing) +
                          lateral * (lateralBlockOffset +
                                     (column - (columnCount - 1) * 0.5f) * spacing);

            if (terrainMap != null && terrainMap.HasValidData)
            {
                return TryFindWalkableTerrainSpawn(desired, out spawn);
            }
            else if (!TryGetGroundHeight(desired, out var groundHeight))
            {
                return false;
            }
            else
            {
                desired.y = groundHeight;
            }

            var projectionDistance = terrainMap != null
                ? Mathf.Max(3f, Mathf.Max(terrainMap.CellSize.x, terrainMap.CellSize.y) * 2f)
                : 5f;
            if (!pathService.TrySamplePosition(
                    desired, projectionDistance, NavMesh.AllAreas, out var position))
                return false;
            spawn = new NavMeshHit { position = position };
            return true;
        }

        private bool TryFindWalkableTerrainSpawn(Vector3 desired, out NavMeshHit spawn)
        {
            spawn = default;
            var cellSize = terrainMap.CellSize;
            var centerX = Mathf.Clamp(
                Mathf.FloorToInt((desired.x - terrainMap.WorldOrigin.x) / cellSize.x),
                0, terrainMap.Columns - 2);
            var centerZ = Mathf.Clamp(
                Mathf.FloorToInt((desired.z - terrainMap.WorldOrigin.z) / cellSize.y),
                0, terrainMap.Rows - 2);
            var maxRing = Mathf.Max(terrainMap.Columns, terrainMap.Rows);
            var projectionDistance = Mathf.Max(3f, Mathf.Max(cellSize.x, cellSize.y) * 2f);

            for (var ring = 0; ring <= maxRing; ring++)
            {
                var nearestDistance = float.PositiveInfinity;
                var nearestPosition = Vector3.zero;
                var found = false;
                for (var dz = -ring; dz <= ring; dz++)
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != ring)
                        continue;

                    var cellX = centerX + dx;
                    var cellZ = centerZ + dz;
                    if (!terrainMap.IsWalkable(cellX, cellZ))
                        continue;

                    var candidate = new Vector3(
                        terrainMap.WorldOrigin.x + (cellX + 0.5f) * cellSize.x,
                        desired.y,
                        terrainMap.WorldOrigin.z + (cellZ + 0.5f) * cellSize.y);
                    var distance = (candidate - desired).sqrMagnitude;
                    if (distance >= nearestDistance ||
                        !terrainMap.TrySample(candidate, out var height, out var walkable) ||
                        !walkable)
                        continue;
                    candidate.y = height;
                    if (!pathService.TrySamplePosition(
                            candidate, projectionDistance, NavMesh.AllAreas, out var projected))
                        continue;

                    nearestDistance = distance;
                    nearestPosition = projected;
                    found = true;
                }

                if (!found)
                    continue;

                spawn = new NavMeshHit { position = nearestPosition };
                return true;
            }

            return false;
        }

        private bool TryFindMapSpawnPosition(bool isBlue, int spawnIndex, out NavMeshHit spawn)
        {
            spawn = default;
            var mapSize = terrainMap.WorldSize;
            var spacing = Mathf.Max(0.5f, spawnSpacing);
            var blueAnchor = GetSpawnTargetPosition(true);
            var redAnchor = GetSpawnTargetPosition(false);
            var axis = redAnchor - blueAnchor;
            axis.y = 0f;
            if (axis.sqrMagnitude < 0.001f)
                axis = Vector3.right;
            else
                axis.Normalize();
            var forward = isBlue ? axis : -axis;
            var lateral = new Vector3(-axis.z, 0f, axis.x);
            var anchor = isBlue ? blueAnchor : redAnchor;
            var centerDistance = Mathf.Abs(Vector3.Dot(battleCenter - anchor, forward));
            var rows = Mathf.Max(1, Mathf.FloorToInt(centerDistance / spacing));
            var lateralSize = Mathf.Abs(axis.x) * mapSize.y + Mathf.Abs(axis.z) * mapSize.x;
            var columns = Mathf.Clamp(
                Mathf.FloorToInt(lateralSize / spacing) + 1,
                1,
                Mathf.Max(1, spawnColumns));
            var capacity = (long)rows * columns;
            if (capacity <= 0)
                return false;

            var attempts = (int)System.Math.Min(64L, capacity);
            var projectionDistance = Mathf.Max(
                spacing,
                Mathf.Max(terrainMap.CellSize.x, terrainMap.CellSize.y) * 2f);
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                var slot = ((long)spawnIndex + attempt) % capacity;
                var ordinal = (int)(slot % columns);
                var column = (columns - 1) / 2 +
                             (ordinal % 2 == 1 ? (ordinal + 1) / 2 : -ordinal / 2);
                var lateralOffset = column * spacing;
                var desired = anchor + forward * (slot / columns * spacing) +
                              lateral * lateralOffset;
                if (!terrainMap.TrySample(desired, out var height, out var walkable) || !walkable)
                    continue;
                desired.y = height;
                if (pathService.TrySamplePosition(desired, projectionDistance, NavMesh.AllAreas, out var position))
                {
                    spawn = new NavMeshHit { position = position };
                    return true;
                }
            }
            return false;
        }

        private Vector3 GetSpawnTargetPosition(bool isBlue)
        {
            var target = isBlue ? blueSpawnTarget : redSpawnTarget;
            return target != null ? target.position : battleCenter +
                Vector3.right * ((isBlue ? -1f : 1f) * teamStartDistance);
        }

        private void EnsureSpawnTargets()
        {
            if (blueSpawnTarget == null) blueSpawnTarget = CreateSpawnTarget(true);
            if (redSpawnTarget == null) redSpawnTarget = CreateSpawnTarget(false);
        }

        private Transform CreateSpawnTarget(bool isBlue)
        {
            var position = GetSpawnTargetPosition(isBlue);
            if (TryGetGroundHeight(position, out var height))
                position.y = height;
            var projectionDistance = terrainMap != null
                ? Mathf.Max(5f, Mathf.Max(terrainMap.CellSize.x, terrainMap.CellSize.y) * 4f)
                : 5f;
            if (pathService.TrySamplePosition(position, projectionDistance, NavMesh.AllAreas, out var projected))
                position = projected;
            var target = new GameObject(isBlue ? "Blue Spawn Target" : "Red Spawn Target").transform;
            target.SetParent(transform, false);
            target.position = position;
            return target;
        }

        private bool TryGetGroundHeight(Vector3 position, out float height)
        {
            if (terrainMap != null && terrainMap.TrySample(position, out height, out _))
                return true;
            if (terrain != null && terrain.terrainData != null)
            {
                height = terrain.SampleHeight(position) + terrain.transform.position.y;
                return true;
            }
            height = 0f;
            return false;
        }

        private void UpdateNavigationBridge(Soldier soldier, Soldier target)
        {
            var simulation = soldier.Simulation;
            simulation.SetCombatApproach(target != null);
            if (!soldier.DepartureReleased)
            {
                // Waiting units can acquire/attack close enemies, but cannot start a strategic march or chase.
                if (simulation.HasPath || simulation.IsPathPending) simulation.StopMovement();
                return;
            }
            var timing = _entityManager.GetComponentData<RecoilUnitCombatTiming>(soldier.Entity);
            var targetEntity = target != null ? target.Entity : Entity.Null;
            if (target != null)
            {
                var targetPosition = target.Simulation.Position;
                var targetInRange = HorizontalDistance(simulation.Position, targetPosition) <=
                                    GetAttackRange(soldier);
                if (targetInRange)
                {
                    simulation.StopMovement();
                    var facingPose = simulation.Pose;
                    RecoilCombatFacing.FaceTarget(ref facingPose, targetPosition);
                    _entityManager.SetComponentData(soldier.Entity, facingPose);
                    timing.NavigationTarget = targetEntity;
                    _entityManager.SetComponentData(soldier.Entity, timing);
                    return;
                }

                var targetMoved = timing.NavigationTarget != targetEntity ||
                    HorizontalDistance(timing.LastDestination, targetPosition) >= retargetDistance;
                if (SimulationNow >= timing.NextPathRequestTime &&
                    (targetMoved || (!simulation.HasPath && !simulation.IsPathPending)))
                {
                    timing.NavigationTarget = targetEntity;
                    timing.LastDestination = targetPosition;
                    timing.NextPathRequestTime = SimulationNow + (targetMoved ? 0.35f : 1f);
                    simulation.SetDestination(targetPosition);
                }
                _entityManager.SetComponentData(soldier.Entity, timing);
                return;
            }
            _entityManager.SetComponentData(soldier.Entity, new RecoilUnitOrder
            {
                Kind = target != null ? RecoilUnitOrderKind.Attack : RecoilUnitOrderKind.Stop,
                Target = targetEntity,
                Destination = target != null ? target.Simulation.Pose.Position : simulation.Pose.Position
            });
            if (target == null)
            {
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
                var destination = GetSpawnTargetPosition(state.Team != 0);
                var bodyRadius = simulation.Settings.BodyRadius;
                var navigation = _entityManager.GetComponentData<RecoilUnitNavigation>(soldier.Entity);
                if (navigation.CastleApproach == 0 && RecoilCastleMarch.TryStraightDestination(
                    simulation.Position, destination, GetCastleMarchTarget(state.Team == 0), out var straight, simulation.Settings.WaypointTolerance))
                    destination = straight;
                else
                {
                    navigation.CastleApproach = 1;
                    _entityManager.SetComponentData(soldier.Entity, navigation);
                    var slot = soldier.Entity.Index % 1024;
                    var spacing = Mathf.Max(1.1f, bodyRadius * 2f + 0.1f);
                    destination += new Vector3(slot % 32 - 15.5f, 0f, slot / 32 - 15.5f) * spacing;
                }
                if (TryGetGroundHeight(destination, out var destinationHeight))
                    destination.y = destinationHeight;
                _entityManager.SetComponentData(soldier.Entity, new RecoilUnitOrder
                { Kind = RecoilUnitOrderKind.Move, Destination = destination });
                var destinationChanged = HorizontalDistance(timing.LastDestination, destination) >= retargetDistance;
                if (HorizontalDistance(simulation.Position, destination) <= Mathf.Max(0.5f, bodyRadius))
                    simulation.StopMovement();
                else if (SimulationNow >= timing.NextPathRequestTime &&
                         (destinationChanged || (!simulation.HasPath && !simulation.IsPathPending)))
                {
                    simulation.SetDestination(destination);
                    timing.LastDestination = destination;
                    timing.NextPathRequestTime = SimulationNow + 1f;
                }
                timing.NavigationTarget = Entity.Null;
            }
            _entityManager.SetComponentData(soldier.Entity, timing);
        }

        private float GetAttackRange(Soldier soldier)
        {
            var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
            if (state.Stats.IsConfigured) return state.Stats.Range;
            if (soldier.RangedUnitType == 2) return siegeAttackRange;
            return soldier.RangedUnitType == 1 ? archerAttackRange : attackRange;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private void KillSoldier(Soldier soldier)
        {
            soldier.Retired = true;
            _hasRetirements = true;
            if (soldier.Entity != Entity.Null && _entityManager.Exists(soldier.Entity))
            {
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
                state.Health = 0;
                state.Active = 0;
                state.Target = Entity.Null;
                _entityManager.SetComponentData(soldier.Entity, state);
            }

            soldier.Simulation.SetActive(false);
            if (soldier.Agent != null)
            {
                if (soldier.Colliders != null)
                    foreach (var collider in soldier.Colliders)
                        if (collider != null) collider.enabled = false;

                var unitObject = soldier.Agent.gameObject;
                if (soldier.Presentation != null &&
                    soldier.Presentation.PlayDeath(() => CompletePrefabSoldierRetirement(soldier, unitObject)))
                    return;

                CompletePrefabSoldierRetirement(soldier, unitObject);
            }
            else
            {
                if (_entityManager.Exists(soldier.Entity))
                    _entityManager.DestroyEntity(soldier.Entity);
                _ownedSoldierEntities.Remove(soldier.Entity);
            }
        }

        private void CompletePrefabSoldierRetirement(Soldier soldier, GameObject unitObject)
        {
            ReleaseFullDetailCount(soldier);
            if (soldier.Agent != null)
                soldier.Agent.Bind(null);
            soldier.Agent = null;
            soldier.Presentation = null;
            soldier.Colliders = null;
            if (_entityManager.Exists(soldier.Entity))
                _entityManager.DestroyEntity(soldier.Entity);
            _ownedSoldierEntities.Remove(soldier.Entity);
            if (unitObject == null)
                return;
            unitObject.SetActive(false);
            unitObject.transform.SetParent(transform, true);
            GetUnitPool(soldier.IsBlue, soldier.Template).Enqueue(unitObject);
        }

        private void ReleaseFullDetailCount(Soldier soldier)
        {
            if (!soldier.FullDetailCounted)
                return;
            soldier.FullDetailCounted = false;
            _fullDetailSoldierCount = Mathf.Max(0, _fullDetailSoldierCount - 1);
        }

        private bool IsAlive(Soldier soldier)
        {
            if (soldier == null || soldier.Entity == Entity.Null ||
                _world == null || !_world.IsCreated ||
                !_entityManager.Exists(soldier.Entity))
                return false;

            var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
            return state.Active != 0 && state.Health > 0;
        }

        private void RemoveDestroyedSoldiers(List<Soldier> team)
        {
            _deadSoldierPredicate ??= IsDeadSoldier;
            // Compact once instead of repeatedly shifting the list for every death.
            if (team.RemoveAll(_deadSoldierPredicate) > 0) _combatRosterDirty = true;
        }

        private bool IsDeadSoldier(Soldier soldier)
        {
            if (!_useAliveFlags) return !IsAlive(soldier);
            return soldier == null || !_combatIndicesByEntity.TryGetValue(soldier.Entity, out var index) ||
                _combatAliveFlags[index] == 0;
        }

        private void CompactRetiredSoldiers()
        {
            if (!_hasRetirements) return;
            _retiredSoldierPredicate ??= soldier => soldier.Retired;
            var removed = _blueTeam.RemoveAll(_retiredSoldierPredicate) + _redTeam.RemoveAll(_retiredSoldierPredicate);
            if (removed > 0) _combatRosterDirty = true;
            _hasRetirements = false;
        }

        private void PruneCombatRoster()
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            RefreshCombatRoster();
            if (_combatSoldiers.Count == 0) { LastRosterPruneMilliseconds = 0f; return; }
            if (!_hasAliveQuery)
            {
                _aliveQuery = _entityManager.CreateEntityQuery(ComponentType.ReadOnly<RecoilBattleEcsState>());
                _hasAliveQuery = true;
            }
            var reset = new RecoilResetAliveFlagsJob { Alive = _combatAliveFlags }.Schedule(_combatSoldiers.Count, 64);
            new RecoilCaptureAliveJob
            {
                Entities = _entityManager.GetEntityTypeHandle(),
                States = _entityManager.GetComponentTypeHandle<RecoilBattleEcsState>(true),
                Indices = _combatEntityIndices, Alive = _combatAliveFlags
            }.ScheduleParallel(_aliveQuery, reset).Complete();
            _useAliveFlags = true;
            try
            {
                RemoveDestroyedSoldiers(_blueTeam);
                RemoveDestroyedSoldiers(_redTeam);
            }
            finally { _useAliveFlags = false; }
            LastRosterPruneMilliseconds = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - start) *
                (1000.0 / System.Diagnostics.Stopwatch.Frequency));
        }

        private void DisposeAliveQuery()
        {
            if (_hasAliveQuery && _world != null && _world.IsCreated) _aliveQuery.Dispose();
            _hasAliveQuery = false;
        }

        private void OnGUI()
        {
            if (!_templatesValidated)
            {
                DrawTemplateValidationScreen();
                return;
            }

            if (_matchFinished)
            {
                DrawMatchResult();
                return;
            }
            if (!_lobbyStarted)
            {
                DrawLobby();
                return;
            }
            DrawBattleHud();
        }

        private void DrawLobby()
        {
            DrawLobbyBackdrop();
            var width = Mathf.Min(650f, Screen.width - 32f);
            var height = Mathf.Min(_settingsOpen ? 590f : 460f, Screen.height - 24f);
            var panel = new Rect((Screen.width - width) * 0.5f,
                (Screen.height - height) * 0.5f, width, height);
            GUI.Box(panel, GUIContent.none);
            var titleRect = new Rect(panel.x + 24f, panel.y + 22f, width - 48f, 44f);
            if (lobbyTitleImage == null)
                GUI.Label(titleRect, "CÔNG THÀNH");
            else
                DrawLobbyImage(titleRect, lobbyTitleImage, ScaleMode.ScaleToFit);

            if (_settingsOpen)
            {
                DrawLobbySettings(panel);
                return;
            }

            GUI.Label(new Rect(panel.x + 24f, panel.y + 82f, width - 48f, 28f),
                "Chọn thời lượng trận đấu");
            const float gap = 12f;
            var buttonWidth = (width - 72f - gap * 3f) / 4f;
            var oldBackground = GUI.backgroundColor;
            for (var i = 0; i < MatchDurationsMinutes.Length; i++)
            {
                var button = new Rect(panel.x + 36f + i * (buttonWidth + gap),
                    panel.y + 120f, buttonWidth, 54f);
                var selected = _selectedMatchDurationMinutes == MatchDurationsMinutes[i];
                GUI.backgroundColor = selected ? new Color(0.25f, 0.58f, 0.95f) : Color.white;
                var label = $"{MatchDurationsMinutes[i]} phút";
                if (GUI.Button(button, lobbyDurationImages[i] == null
                        ? new GUIContent(label)
                        : GUIContent.none))
                    _selectedMatchDurationMinutes = MatchDurationsMinutes[i];
                var previousColor = GUI.color;
                GUI.color = selected ? new Color(0.6f, 0.8f, 1f, 0.55f) : Color.white;
                DrawLobbyImage(button, lobbyDurationImages[i], ScaleMode.ScaleToFit);
                GUI.color = previousColor;
            }
            GUI.backgroundColor = oldBackground;

            GUI.Label(new Rect(panel.x + 36f, panel.y + 194f, width - 72f, 44f),
                $"Mỗi phe có {UnitsPerManualSpawn} quân cho từng loại (tổng {UnitsPerManualSpawn * CharacterTypeCount} quân). " +
                "Chọn thời lượng rồi bấm BẮT ĐẦU để vào trận.");
            var buttonY = panel.y + height - 108f;
            var startButton = new Rect(panel.x + 36f, buttonY, width - 72f, 48f);
            if (GUI.Button(startButton,
                    lobbyStartButtonImage == null ? new GUIContent("BẮT ĐẦU") : GUIContent.none))
                StartMatchFromLobby();
            DrawLobbyImage(startButton, lobbyStartButtonImage, ScaleMode.ScaleToFit);
            var settingsButton = new Rect(panel.x + 36f, buttonY + 60f, width - 72f, 42f);
            if (GUI.Button(settingsButton,
                    lobbySettingsButtonImage == null ? new GUIContent("CÀI ĐẶT CHỈ SỐ") : GUIContent.none))
                _settingsOpen = true;
            DrawLobbyImage(settingsButton, lobbySettingsButtonImage, ScaleMode.ScaleToFit);
            if (!string.IsNullOrEmpty(_lobbyError))
                GUI.Label(new Rect(panel.x + 36f, buttonY - 30f, width - 72f, 26f), _lobbyError);
        }

        private void DrawLobbySettings(Rect panel)
        {
            var width = panel.width;
            GUI.Label(new Rect(panel.x + 24f, panel.y + 72f, width - 48f, 30f),
                "Chỉ số áp dụng cho cả hai phe");

            for (var i = 0; i < CharacterTypeCount; i++)
            {
                var column = i % 4;
                var row = i / 4;
                var buttonWidth = (width - 56f) / 4f;
                var button = new Rect(panel.x + 24f + column * ((width - 48f) / 4f),
                    panel.y + 108f + row * 64f, buttonWidth, 58f);
                var oldBackground = GUI.backgroundColor;
                GUI.backgroundColor = _selectedLobbyUnitType == i
                    ? new Color(0.25f, 0.58f, 0.95f)
                    : Color.white;
                if (GUI.Button(button, lobbyUnitImages[i] == null
                        ? new GUIContent(CharacterTypeLabels[i])
                        : GUIContent.none))
                    _selectedLobbyUnitType = i;
                if (lobbyUnitImages[i] != null)
                {
                    DrawLobbyImage(new Rect(button.x + 4f, button.y + 5f, 48f, 48f),
                        lobbyUnitImages[i], ScaleMode.ScaleToFit);
                    GUI.Label(new Rect(button.x + 56f, button.y + 5f, button.width - 60f, 48f),
                        CharacterTypeLabels[i]);
                }
                GUI.backgroundColor = oldBackground;
            }

            var stats = lobbyUnitStats[_selectedLobbyUnitType];
            var labelX = panel.x + 32f;
            var rowY = panel.y + 242f;
            var rowWidth = width - 64f;
            const float rowHeight = 32f;
            stats.MaxHealth = Mathf.RoundToInt(DrawStatSlider(
                labelX, rowY, rowWidth, "Máu tối đa", stats.MaxHealth, 1f, 5000f, "0"));
            rowY += rowHeight;
            stats.Damage = Mathf.RoundToInt(DrawStatSlider(
                labelX, rowY, rowWidth, "Sát thương", stats.Damage, 0f, 500f, "0"));
            rowY += rowHeight;
            stats.Range = DrawStatSlider(
                labelX, rowY, rowWidth, "Tầm đánh", stats.Range, 0.1f, 100f, "0.0");
            rowY += rowHeight;
            stats.AttackSpeed = DrawStatSlider(
                labelX, rowY, rowWidth, "Tốc độ đánh (đòn/giây)", stats.AttackSpeed, 0.1f, 5f, "0.00");
            rowY += rowHeight;
            stats.MoveSpeed = DrawStatSlider(
                labelX, rowY, rowWidth, "Tốc độ di chuyển", stats.MoveSpeed, 0.1f, 20f, "0.0");
            rowY += rowHeight;
            stats.Armor = DrawStatSlider(
                labelX, rowY, rowWidth, "Giáp vật lý", stats.Armor, 0f, 1f, "P0");
            rowY += rowHeight;
            stats.SecondaryArmor = DrawStatSlider(
                labelX, rowY, rowWidth, "Kháng phép", stats.SecondaryArmor, 0f, 1f, "P0");
            rowY += rowHeight;
            stats.AreaRadius = DrawStatSlider(
                labelX, rowY, rowWidth, "Bán kính sát thương lan", stats.AreaRadius, 0f, 20f, "0.0");
            rowY += rowHeight + 4f;
            stats.SecondaryDamage = GUI.Toggle(
                new Rect(labelX, rowY, rowWidth, 24f), stats.SecondaryDamage, "Gây sát thương phép");
            lobbyUnitStats[_selectedLobbyUnitType] = stats;

            var backY = panel.y + panel.height - 50f;
            if (GUI.Button(new Rect(panel.x + 24f, backY, width - 48f, 36f), "QUAY LẠI"))
                _settingsOpen = false;
        }

        private static float DrawStatSlider(
            float x, float y, float width, string label, float value, float minimum, float maximum,
            string format)
        {
            const float labelWidth = 210f;
            const float valueWidth = 74f;
            GUI.Label(new Rect(x, y, labelWidth, 24f), label);
            value = GUI.HorizontalSlider(
                new Rect(x + labelWidth, y + 5f, Mathf.Max(80f, width - labelWidth - valueWidth), 18f),
                value, minimum, maximum);
            GUI.Label(new Rect(x + width - valueWidth, y, valueWidth, 24f),
                format == "P0" ? value.ToString("P0") : value.ToString(format));
            return value;
        }

        private void DrawLobbyBackdrop()
        {
            var oldColor = GUI.color;
            if (Event.current.type == EventType.Repaint)
            {
                if (lobbyBackgroundImage != null)
                {
                    GUI.color = Color.white;
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height),
                        lobbyBackgroundImage, ScaleMode.ScaleAndCrop, true);
                }
                else
                {
                    GUI.color = new Color(0.025f, 0.04f, 0.07f, 0.92f);
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                }
            }
            GUI.color = oldColor;
        }

        private static void DrawLobbyImage(Rect rect, Texture2D image, ScaleMode scaleMode)
        {
            if (Event.current.type == EventType.Repaint && image != null)
                GUI.DrawTexture(rect, image, scaleMode, true);
        }

        private void DrawBattleHud()
        {
            GUI.Box(new Rect(6f, 6f, 738f, 224f), GUIContent.none);
            var status = _combatStarted
                ? "ĐANG GIAO CHIẾN"
                : _lobbyStarting
                    ? _formationReady && !_formationDirty && !HasQueuedSpawns
                        ? "ĐỘI HÌNH SẴN SÀNG — NHẤN L ĐỂ BẮT ĐẦU"
                        : $"ĐANG XẾP ĐỘI HÌNH — BLUE: {_blueTeam.Count}  RED: {_redTeam.Count}"
                    : "CHƯA CÓ QUÂN — BẤM NÚT LOẠI QUÂN ĐỂ GỌI";
            GUI.Label(new Rect(12f, 12f, 480f, 24f), status);
            if (_combatStarted)
                GUI.Label(new Rect(500f, 12f, 238f, 24f),
                    $"CÒN LẠI  {FormatMatchTime(_selectedMatchDurationMinutes * 60f - _matchElapsedSeconds)}");
            GUI.Label(new Rect(12f, 38f, 720f, 24f),
                $"ALIVE  BLUE: {_blueTeam.Count}   RED: {_redTeam.Count}   TOTAL: {_blueTeam.Count + _redTeam.Count}");
            GUI.Label(new Rect(12f, 62f, 720f, 24f),
                $"SPAWNED  BLUE: {_blueSpawnedTotal}   RED: {_redSpawnedTotal}   TOTAL: {_blueSpawnedTotal + _redSpawnedTotal}");
            GUI.Label(new Rect(12f, 86f, 720f, 24f),
                $"QUEUED  BLUE: {_blueSpawnQueue + _blueSelectedSpawnTotal}   RED: {_redSpawnQueue + _redSelectedSpawnTotal}" +
                (_combatStarted ? "  |  bấm nút loại quân để gọi thêm" : string.Empty));
            GUI.Label(new Rect(12f, 112f, 720f, 20f),
                $"BẤM NÚT ĐỂ GỌI {UnitsPerManualSpawn:N0} LÍNH MỖI PHE");
            for (var i = 0; i < CharacterTypeCount; i++)
            {
                var row = i / 4;
                var column = i % 4;
                var rect = new Rect(12f + column * 178f, 136f + row * 30f, 170f, 26f);
                if (GUI.Button(rect, lobbyUnitImages[i] == null
                        ? new GUIContent(CharacterTypeLabels[i])
                        : GUIContent.none))
                    QueueSelectedTypeSpawn(i);
                if (lobbyUnitImages[i] != null)
                {
                    DrawLobbyImage(new Rect(rect.x + 2f, rect.y + 2f, 22f, 22f),
                        lobbyUnitImages[i], ScaleMode.ScaleToFit);
                    GUI.Label(new Rect(rect.x + 28f, rect.y, rect.width - 30f, rect.height),
                        CharacterTypeLabels[i]);
                }
            }
        }

        private void DrawMatchResult()
        {
            DrawLobbyBackdrop();
            const float width = 520f;
            const float height = 180f;
            var panel = new Rect((Screen.width - width) * 0.5f,
                (Screen.height - height) * 0.5f, width, height);
            GUI.Box(panel, "KẾT THÚC TRẬN ĐẤU");
            GUI.Label(new Rect(panel.x + 24f, panel.y + 66f, width - 48f, 44f), _matchResult);
            GUI.Label(new Rect(panel.x + 24f, panel.y + 112f, width - 48f, 28f),
                $"Thời gian đã chơi: {FormatMatchTime(_matchElapsedSeconds)}");
        }

        private static string FormatMatchTime(float seconds)
        {
            var totalSeconds = Mathf.Max(0, Mathf.CeilToInt(seconds));
            return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
        }

        private void DrawTemplateValidationScreen()
        {
            var screen = new Rect(0f, 0f, Screen.width, Screen.height);
            GUI.color = new Color(0.035f, 0.045f, 0.065f, 0.98f);
            GUI.DrawTexture(screen, Texture2D.whiteTexture);
            GUI.color = Color.white;

            var centerX = Screen.width * 0.5f;
            var centerY = Screen.height * 0.5f;
            GUI.Label(new Rect(centerX - 240f, centerY - 76f, 480f, 36f),
                _templateValidationFailed ? "KHÔNG THỂ NẠP QUÂN" : "ĐANG CHUẨN BỊ TRẬN ĐẤU",
                GUI.skin.label);
            GUI.Label(new Rect(centerX - 240f, centerY - 32f, 480f, 28f),
                _templateValidationFailed
                    ? _templateValidationError
                    : $"Đang kiểm tra mẫu quân: {_templatesValidatedCount:N0} / {_templateValidationTotal:N0}...",
                GUI.skin.label);

            var bar = new Rect(centerX - 240f, centerY + 8f, 480f, 22f);
            GUI.color = new Color(0.18f, 0.2f, 0.24f, 1f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            if (!_templateValidationFailed && _templateValidationTotal > 0)
            {
                var progress = Mathf.Clamp01((float)_templatesValidatedCount / _templateValidationTotal);
                GUI.color = new Color(0.2f, 0.62f, 0.95f, 1f);
                GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * progress, bar.height),
                    Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(centerX - 240f, centerY + 38f, 480f, 24f),
                    $"{progress * 100f:F1}%   |   Hiển thị bằng VAT và dot, không tạo prefab lính.");
            }
            else
            {
                GUI.color = Color.white;
                GUI.Label(new Rect(centerX - 240f, centerY + 38f, 480f, 24f),
                    _templateValidationFailed
                        ? "Hãy sửa cấu hình mẫu lính rồi tải lại scene."
                        : "Hiển thị gần bằng VAT, xa bằng dot.");
            }
            GUI.color = Color.white;
        }

        private void ReleaseDepartures(Queue<Soldier> queue)
        {
            var budget = Mathf.Max(1, departuresPerTeamPerTick);
            var inspected = 0;
            var released = 0;
            var maxInspections = Mathf.Max(32, budget > int.MaxValue / 4 ? int.MaxValue : budget * 4);
            while (queue.Count > 0 && released < budget && inspected++ < maxInspections)
            {
                var soldier = queue.Dequeue();
                // A pooled entity may already be alive again; the old queued Soldier must stay retired.
                if (soldier.Retired || soldier.DepartureReleased || !IsAlive(soldier)) continue;
                soldier.DepartureReleased = true;
                released++;
                if (_entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity).Target == Entity.Null)
                    UpdateNavigationBridge(soldier, null);
            }
        }
    }
}
