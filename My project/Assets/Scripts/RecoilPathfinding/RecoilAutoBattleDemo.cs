using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;
using Unity.Profiling;
using Unity.Entities;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace RecoilPathfinding
{
    public struct RecoilBattleEcsState : IComponentData
    {
        public int Team;
        public int Health;
        public float AttackCooldown;
        public byte Active;
        public Entity Target;
    }

    [BurstCompile]
    internal struct RecoilBattleCombatJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<RecoilBattleCombatSnapshot> Soldiers;
        [ReadOnly] public NativeArray<int> ScheduledSoldiers;
        [ReadOnly] public NativeParallelMultiHashMap<int2, int> Cells;
        [WriteOnly] public NativeArray<RecoilBattleCombatResult> Results;
        public int MinCellX;
        public int MaxCellX;
        public int MinCellZ;
        public int MaxCellZ;
        public float CellSize;
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
            else if (soldier.SearchTarget != 0 || !IsValidEnemy(soldier, targetIndex))
                targetIndex = FindNearestEnemy(soldier, out candidateChecks);

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
            var maxRadius = math.max(
                math.max(math.abs(cell.x - MinCellX), math.abs(cell.x - MaxCellX)),
                math.max(math.abs(cell.y - MinCellZ), math.abs(cell.y - MaxCellZ)));
            var nearestIndex = -1;
            var nearestDistanceSquared = float.PositiveInfinity;

            for (var radius = 0; radius <= maxRadius; radius++)
            {
                var minX = math.max(cell.x - radius, MinCellX);
                var maxX = math.min(cell.x + radius, MaxCellX);
                var minZ = math.max(cell.y - radius, MinCellZ);
                var maxZ = math.min(cell.y + radius, MaxCellZ);
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
            if (!Cells.TryGetFirstValue(cell, out var candidateIndex, out var iterator))
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
            while (Cells.TryGetNextValue(out candidateIndex, ref iterator));
        }

        private bool IsValidEnemy(RecoilBattleCombatSnapshot soldier, int candidateIndex)
        {
            if (candidateIndex < 0 || candidateIndex >= Soldiers.Length)
                return false;

            var candidate = Soldiers[candidateIndex];
            return candidate.Active != 0 && candidate.Health > 0 && candidate.Team != soldier.Team;
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

    public sealed class RecoilAutoBattleDemo : MonoBehaviour
    {
        private const int StartingUnitsPerTeam = 2;
        private const int UnitsPerSpawnBatch = 500;
        private const int SpawnsPerFrame = 20;
        private const int SpawnColumns = 20;
        private const int MaxPooledUnitsPerTeam = 2000;
        private const float TargetGridCellSize = 4f;
        private static readonly ProfilerMarker BattleUpdateMarker =
            new ProfilerMarker("RecoilBattle.Update");
        private static readonly ProfilerMarker CombatJobMarker =
            new ProfilerMarker("RecoilBattle.CombatBurstJob");

        [SerializeField] private Terrain terrain = null;
        [SerializeField] private RecoilNavMeshPathService pathService = null;
        [SerializeField] private RecoilInstancedSoldierRenderer instancedRenderer = null;
        [SerializeField] private GameObject blueUnitTemplate = null;
        [SerializeField] private GameObject redUnitTemplate = null;
        [SerializeField] private Vector3 battleCenter = Vector3.zero;
        [SerializeField, Min(1f)] private float teamStartDistance = 18f;
        [SerializeField, Min(0.5f)] private float spawnSpacing = 1.8f;
        [SerializeField, Min(0.5f)] private float attackRange = 1.5f;
        [SerializeField, Min(0.05f)] private float attackInterval = 0.65f;
        [SerializeField, Min(1)] private int attackDamage = 20;
        [SerializeField, Min(0.1f)] private float retargetDistance = 2f;
        [SerializeField, Min(1f)] private float combatUpdatesPerSecond = 10f;

        private readonly List<Soldier> _blueTeam = new();
        private readonly List<Soldier> _redTeam = new();
        private readonly Queue<PooledSoldier> _blueUnitPool = new();
        private readonly Queue<PooledSoldier> _redUnitPool = new();
        private readonly List<Entity> _ownedSoldierEntities = new();
        private readonly List<Soldier> _combatSoldiers = new();
        private readonly List<Soldier> _scheduledSoldiers = new();
        private readonly Dictionary<Entity, int> _combatIndicesByEntity = new();
        private NativeArray<RecoilBattleCombatSnapshot> _combatSnapshots;
        private NativeArray<int> _scheduledCombatIndices;
        private NativeArray<RecoilBattleCombatResult> _combatResults;
        private NativeArray<int> _damageBySoldier;
        private NativeParallelMultiHashMap<int2, int> _combatCells;
        private JobHandle _combatJobHandle;
        private int _combatBufferCapacity;
        private int _blueSpawnQueue;
        private int _redSpawnQueue;
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
            public RecoilNavMeshPathAgent Agent;
            public Entity Entity;
            public float NextPathRequestTime;
            public float NextTargetSearchTime;
            public Soldier Target;
            public Soldier NavigationTarget;
            public Vector3 LastDestination;
            public float LastCombatUpdateTime;
        }

        private readonly struct PooledSoldier
        {
            public readonly GameObject GameObject;
            public readonly Entity Entity;

            public PooledSoldier(GameObject gameObject, Entity entity)
            {
                GameObject = gameObject;
                Entity = entity;
            }
        }

        private sealed class TargetSpatialIndex
        {
            private static readonly ProfilerMarker BuildMarker =
                new ProfilerMarker("RecoilBattle.TargetGrid.Build");
            private static readonly ProfilerMarker QueryMarker =
                new ProfilerMarker("RecoilBattle.TargetGrid.Query");

            private readonly float _cellSize;
            private readonly Dictionary<Vector2Int, List<Soldier>> _cells = new();
            private readonly List<List<Soldier>> _bucketPool = new();
            private int _bucketCount;
            private int _minX;
            private int _maxX;
            private int _minZ;
            private int _maxZ;
            private bool _hasBounds;

            public int CandidateChecks { get; private set; }

            public TargetSpatialIndex(float cellSize)
            {
                _cellSize = cellSize;
            }

            public void Build(List<Soldier> soldiers, EntityManager entityManager)
            {
                using (BuildMarker.Auto())
                {
                    for (var i = 0; i < _bucketCount; i++)
                        _bucketPool[i].Clear();

                    _cells.Clear();
                    _bucketCount = 0;
                    _hasBounds = false;

                    for (var i = 0; i < soldiers.Count; i++)
                    {
                        var soldier = soldiers[i];
                        if (soldier.Agent == null ||
                            !entityManager.Exists(soldier.Entity) ||
                            entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity).Active == 0)
                            continue;

                        var position = soldier.Agent.transform.position;
                        var cell = ToCell(position);
                        if (!_cells.TryGetValue(cell, out var bucket))
                        {
                            if (_bucketCount == _bucketPool.Count)
                                _bucketPool.Add(new List<Soldier>());

                            bucket = _bucketPool[_bucketCount++];
                            _cells.Add(cell, bucket);
                        }

                        bucket.Add(soldier);
                        if (!_hasBounds)
                        {
                            _minX = _maxX = cell.x;
                            _minZ = _maxZ = cell.y;
                            _hasBounds = true;
                        }
                        else
                        {
                            _minX = Mathf.Min(_minX, cell.x);
                            _maxX = Mathf.Max(_maxX, cell.x);
                            _minZ = Mathf.Min(_minZ, cell.y);
                            _maxZ = Mathf.Max(_maxZ, cell.y);
                        }
                    }

                    CandidateChecks = 0;
                }
            }

            public Soldier FindNearest(Vector3 position, EntityManager entityManager)
            {
                using (QueryMarker.Auto())
                {
                    if (!_hasBounds)
                        return null;

                    var center = ToCell(position);
                    var maxRadius = Mathf.Max(
                        Mathf.Max(Mathf.Abs(center.x - _minX), Mathf.Abs(center.x - _maxX)),
                        Mathf.Max(Mathf.Abs(center.y - _minZ), Mathf.Abs(center.y - _maxZ)));
                    var nearestDistanceSquared = float.PositiveInfinity;
                    Soldier nearest = null;

                    for (var radius = 0; radius <= maxRadius; radius++)
                    {
                        SearchRing(
                            center,
                            radius,
                            position,
                            entityManager,
                            ref nearest,
                            ref nearestDistanceSquared);

                        if (nearest != null &&
                            (radius == maxRadius ||
                             (IsCellInsideBounds(center) &&
                              IsNearestGuaranteed(center, radius, position, nearestDistanceSquared))))
                            break;
                    }

                    return nearest;
                }
            }

            private Vector2Int ToCell(Vector3 position) =>
                new(Mathf.FloorToInt(position.x / _cellSize), Mathf.FloorToInt(position.z / _cellSize));

            private bool IsCellInsideBounds(Vector2Int cell) =>
                cell.x >= _minX && cell.x <= _maxX && cell.y >= _minZ && cell.y <= _maxZ;

            private void SearchRing(
                Vector2Int center,
                int radius,
                Vector3 position,
                EntityManager entityManager,
                ref Soldier nearest,
                ref float nearestDistanceSquared)
            {
                var minX = Mathf.Max(center.x - radius, _minX);
                var maxX = Mathf.Min(center.x + radius, _maxX);
                var minZ = Mathf.Max(center.y - radius, _minZ);
                var maxZ = Mathf.Min(center.y + radius, _maxZ);
                if (minX > maxX || minZ > maxZ)
                    return;

                if (radius == 0)
                {
                    CheckCell(new Vector2Int(minX, minZ), position, entityManager, ref nearest, ref nearestDistanceSquared);
                    return;
                }

                for (var x = minX; x <= maxX; x++)
                {
                    CheckCell(new Vector2Int(x, minZ), position, entityManager, ref nearest, ref nearestDistanceSquared);
                    if (maxZ != minZ)
                        CheckCell(new Vector2Int(x, maxZ), position, entityManager, ref nearest, ref nearestDistanceSquared);
                }

                for (var z = minZ + 1; z < maxZ; z++)
                {
                    CheckCell(new Vector2Int(minX, z), position, entityManager, ref nearest, ref nearestDistanceSquared);
                    if (maxX != minX)
                        CheckCell(new Vector2Int(maxX, z), position, entityManager, ref nearest, ref nearestDistanceSquared);
                }
            }

            private void CheckCell(
                Vector2Int cell,
                Vector3 position,
                EntityManager entityManager,
                ref Soldier nearest,
                ref float nearestDistanceSquared)
            {
                if (!_cells.TryGetValue(cell, out var bucket))
                    return;

                for (var i = 0; i < bucket.Count; i++)
                {
                    var soldier = bucket[i];
                    CandidateChecks++;
                    if (soldier.Agent == null ||
                        !entityManager.Exists(soldier.Entity) ||
                        entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity).Active == 0)
                        continue;

                    var distanceSquared = HorizontalDistanceSquared(
                        position,
                        soldier.Agent.transform.position);
                    if (distanceSquared >= nearestDistanceSquared)
                        continue;

                    nearest = soldier;
                    nearestDistanceSquared = distanceSquared;
                }
            }

            private bool IsNearestGuaranteed(Vector2Int center, int radius, Vector3 position, float nearestDistanceSquared)
            {
                var left = (Mathf.Max(center.x - radius, _minX)) * _cellSize;
                var right = (Mathf.Min(center.x + radius, _maxX) + 1) * _cellSize;
                var bottom = (Mathf.Max(center.y - radius, _minZ)) * _cellSize;
                var top = (Mathf.Min(center.y + radius, _maxZ) + 1) * _cellSize;
                var distanceToUnsearchedArea = Mathf.Min(
                    position.x - left,
                    right - position.x,
                    position.z - bottom,
                    top - position.z);

                return nearestDistanceSquared <= distanceToUnsearchedArea * distanceToUnsearchedArea;
            }
        }

        public int TargetCandidateChecksLastFrame => _targetCandidateChecksLastFrame;
        public int CombatUpdatesLastFrame => _combatUpdatesLastFrame;
        public int PooledSoldierCount => _blueUnitPool.Count + _redUnitPool.Count;
        public int EcsSoldierEntityCount => _ownedSoldierEntities.Count;

        private void Start()
        {
            if (terrain == null)
                terrain = FindFirstObjectByType<Terrain>();

            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogError($"{nameof(RecoilAutoBattleDemo)} requires a Terrain to determine ground height.", this);
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

            _world = World.DefaultGameObjectInjectionWorld;
            if (_world == null || !_world.IsCreated)
            {
                Debug.LogError($"{nameof(RecoilAutoBattleDemo)} requires an initialized Entities world.", this);
                enabled = false;
                return;
            }

            _entityManager = _world.EntityManager;
            _soldierArchetype = _entityManager.CreateArchetype(
                typeof(RecoilBattleEcsState));
            _hasEntityManager = true;

            for (var i = 0; i < StartingUnitsPerTeam; i++)
            {
                if (!SpawnUnit(true))
                    Debug.LogWarning("Could not spawn an initial blue soldier on the NavMesh.", this);
                if (!SpawnUnit(false))
                    Debug.LogWarning("Could not spawn an initial red soldier on the NavMesh.", this);
            }
        }

        private void OnDestroy()
        {
            if (_combatJobPending)
            {
                _combatJobHandle.Complete();
                _combatJobPending = false;
            }

            DisposeCombatBuffers();
            if (!_hasEntityManager || _world == null || !_world.IsCreated)
                return;

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

            _combatUpdatesLastFrame = 0;
            _targetCandidateChecksLastFrame = 0;
            HandleSpawnInput();
            if (_combatJobPending && !_combatJobHandle.IsCompleted)
                return;

            CompletePendingCombatJob();
            RemoveDestroyedSoldiers(_blueTeam);
            RemoveDestroyedSoldiers(_redTeam);
            ProcessSpawnQueue();
            _scheduledSoldiers.Clear();
            ScheduleTeamUpdates(
                _blueTeam,
                ref _blueCombatUpdateCursor,
                ref _blueCombatUpdateAccumulator);
            ScheduleTeamUpdates(
                _redTeam,
                ref _redCombatUpdateCursor,
                ref _redCombatUpdateAccumulator);
            if (_scheduledSoldiers.Count > 0)
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

            updateAccumulator = Mathf.Min(
                count,
                updateAccumulator + count * Time.deltaTime * combatUpdatesPerSecond);
            var updateCount = Mathf.Min(count, Mathf.FloorToInt(updateAccumulator));
            if (updateCount == 0)
                return;

            updateAccumulator -= updateCount;
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
            _combatSoldiers.Clear();
            _combatIndicesByEntity.Clear();
            AddCombatSoldiers(_blueTeam);
            AddCombatSoldiers(_redTeam);
            if (_combatSoldiers.Count == 0)
                return;

            EnsureCombatBuffers(_combatSoldiers.Count);
            _combatCells.Clear();
            var minCellX = int.MaxValue;
            var maxCellX = int.MinValue;
            var minCellZ = int.MaxValue;
            var maxCellZ = int.MinValue;
            var blueCount = 0;
            var redCount = 0;

            for (var i = 0; i < _combatSoldiers.Count; i++)
            {
                var soldier = _combatSoldiers[i];
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
                var position = soldier.Agent.transform.position;
                var targetIndex = -1;
                var hasTarget = soldier.Target != null &&
                                _combatIndicesByEntity.TryGetValue(soldier.Target.Entity, out targetIndex);
                var currentTargetIndex = hasTarget ? targetIndex : -1;
                var searchTarget = !hasTarget || Time.time >= soldier.NextTargetSearchTime;
                var combatDeltaTime = soldier.LastCombatUpdateTime > 0f
                    ? Mathf.Max(Time.deltaTime, Time.time - soldier.LastCombatUpdateTime)
                    : 1f / combatUpdatesPerSecond;
                _combatSnapshots[i] = new RecoilBattleCombatSnapshot
                {
                    Position = new float3(position.x, position.y, position.z),
                    Team = state.Team,
                    Health = state.Health,
                    CurrentTargetIndex = currentTargetIndex,
                    AttackCooldown = state.AttackCooldown,
                    DeltaTime = combatDeltaTime,
                    Active = state.Active,
                    SearchTarget = (byte)(searchTarget ? 1 : 0)
                };

                if (state.Team == 0)
                    blueCount++;
                else
                    redCount++;
                var cell = (int2)math.floor(new float2(position.x, position.z) / TargetGridCellSize);
                _combatCells.Add(cell, i);
                minCellX = Mathf.Min(minCellX, cell.x);
                maxCellX = Mathf.Max(maxCellX, cell.x);
                minCellZ = Mathf.Min(minCellZ, cell.y);
                maxCellZ = Mathf.Max(maxCellZ, cell.y);
            }

            for (var i = 0; i < _scheduledSoldiers.Count; i++)
            {
                if (!_combatIndicesByEntity.TryGetValue(_scheduledSoldiers[i].Entity, out var combatIndex))
                    continue;

                _scheduledCombatIndices[i] = combatIndex;
            }

            var combatJob = new RecoilBattleCombatJob
            {
                Soldiers = _combatSnapshots,
                ScheduledSoldiers = _scheduledCombatIndices,
                Cells = _combatCells,
                Results = _combatResults,
                MinCellX = minCellX,
                MaxCellX = maxCellX,
                MinCellZ = minCellZ,
                MaxCellZ = maxCellZ,
                CellSize = TargetGridCellSize,
                AttackRangeSquared = attackRange * attackRange,
                AttackInterval = attackInterval,
                AttackDamage = attackDamage,
                BlueCount = blueCount,
                RedCount = redCount
            };
            using (CombatJobMarker.Auto())
                _combatJobHandle = combatJob.Schedule(_scheduledSoldiers.Count, 32);
            _combatJobPending = true;
        }

        private void CompletePendingCombatJob()
        {
            if (!_combatJobPending)
                return;

            _combatJobHandle.Complete();
            _combatJobPending = false;
            for (var i = 0; i < _combatSoldiers.Count; i++)
                _damageBySoldier[i] = 0;

            for (var i = 0; i < _scheduledSoldiers.Count; i++)
            {
                var combatIndex = _scheduledCombatIndices[i];
                var soldier = _combatSoldiers[combatIndex];
                var result = _combatResults[i];
                _targetCandidateChecksLastFrame += result.CandidateChecks;
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
                state.AttackCooldown = result.Cooldown;
                soldier.LastCombatUpdateTime = Time.time;
                var target = result.TargetIndex >= 0
                    ? _combatSoldiers[result.TargetIndex]
                    : null;
                UpdateNavigationBridge(soldier, target);
                state.Target = target != null ? target.Entity : Entity.Null;
                _entityManager.SetComponentData(soldier.Entity, state);
                soldier.Target = target;
                if (_combatSnapshots[combatIndex].SearchTarget != 0)
                    soldier.NextTargetSearchTime = Time.time + 0.5f;

                if (result.DamageTargetIndex >= 0)
                    _damageBySoldier[result.DamageTargetIndex] += result.Damage;
            }

            for (var i = 0; i < _combatSoldiers.Count; i++)
            {
                var damage = _damageBySoldier[i];
                if (damage <= 0)
                    continue;

                var target = _combatSoldiers[i];
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(target.Entity);
                state.Health = Mathf.Max(0, state.Health - damage);
                if (state.Health == 0)
                {
                    state.Active = 0;
                    state.Target = Entity.Null;
                }

                _entityManager.SetComponentData(target.Entity, state);
                if (state.Health == 0)
                    KillSoldier(target);
            }
        }

        private void AddCombatSoldiers(List<Soldier> team)
        {
            for (var i = 0; i < team.Count; i++)
            {
                var soldier = team[i];
                if (soldier.Agent == null || !IsAlive(soldier))
                    continue;

                _combatIndicesByEntity.Add(soldier.Entity, _combatSoldiers.Count);
                _combatSoldiers.Add(soldier);
            }
        }

        private void EnsureCombatBuffers(int soldierCount)
        {
            if (soldierCount <= _combatBufferCapacity)
                return;

            DisposeCombatBuffers();
            _combatBufferCapacity = Mathf.NextPowerOfTwo(Mathf.Max(4, soldierCount));
            _combatSnapshots = new NativeArray<RecoilBattleCombatSnapshot>(
                _combatBufferCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
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
            _combatCells = new NativeParallelMultiHashMap<int2, int>(
                _combatBufferCapacity,
                Allocator.Persistent);
        }

        private void DisposeCombatBuffers()
        {
            if (_combatJobPending)
            {
                _combatJobHandle.Complete();
                _combatJobPending = false;
            }

            if (_combatSnapshots.IsCreated)
                _combatSnapshots.Dispose();
            if (_scheduledCombatIndices.IsCreated)
                _scheduledCombatIndices.Dispose();
            if (_combatResults.IsCreated)
                _combatResults.Dispose();
            if (_damageBySoldier.IsCreated)
                _damageBySoldier.Dispose();
            if (_combatCells.IsCreated)
                _combatCells.Dispose();

            _combatBufferCapacity = 0;
        }

        private void HandleSpawnInput()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (keyboard.digit1Key.wasPressedThisFrame || keyboard.numpad1Key.wasPressedThisFrame)
                QueueSpawnBatch(true);
            if (keyboard.digit2Key.wasPressedThisFrame || keyboard.numpad2Key.wasPressedThisFrame)
                QueueSpawnBatch(false);
            if (keyboard.digit3Key.wasPressedThisFrame || keyboard.numpad3Key.wasPressedThisFrame)
            {
                QueueSpawnBatch(true);
                QueueSpawnBatch(false);
            }
        }

        private void QueueSpawnBatch(bool isBlue)
        {
            if (isBlue)
                _blueSpawnQueue += UnitsPerSpawnBatch;
            else
                _redSpawnQueue += UnitsPerSpawnBatch;
        }

        private void ProcessSpawnQueue()
        {
            if (Time.time < _nextSpawnAttemptTime)
                return;

            for (var i = 0; i < SpawnsPerFrame && (_blueSpawnQueue > 0 || _redSpawnQueue > 0); i++)
            {
                var spawnBlue = _blueSpawnQueue > 0 && (_redSpawnQueue == 0 || _spawnBlueNext);
                if (!SpawnUnit(spawnBlue))
                {
                    _nextSpawnAttemptTime = Time.time + 1f;
                    if (!_spawnFailureReported)
                    {
                        Debug.LogWarning(
                            "Soldier spawn paused: no nearby position was found on the NavMesh. " +
                            "The queued units are retained; check NavMesh coverage around the team formation.",
                            this);
                        _spawnFailureReported = true;
                    }

                    return;
                }

                _spawnFailureReported = false;
                if (spawnBlue)
                {
                    _blueSpawnQueue--;
                }
                else
                {
                    _redSpawnQueue--;
                }

                _spawnBlueNext = !spawnBlue;
            }
        }

        private bool SpawnUnit(bool isBlue)
        {
            var team = isBlue ? _blueTeam : _redTeam;
            var template = isBlue ? blueUnitTemplate : redUnitTemplate;
            var pool = isBlue ? _blueUnitPool : _redUnitPool;
            var side = isBlue ? -1f : 1f;
            var spawnIndex = isBlue ? _blueSpawnedTotal : _redSpawnedTotal;
            var row = spawnIndex / SpawnColumns;
            var column = spawnIndex % SpawnColumns;
            var columnOffset = column - (SpawnColumns - 1) * 0.5f;
            var desired = battleCenter +
                          Vector3.right * (side * (teamStartDistance + row * spawnSpacing)) +
                          Vector3.forward * (columnOffset * spawnSpacing);
            desired.y = terrain.SampleHeight(desired) + terrain.transform.position.y;

            if (!NavMesh.SamplePosition(desired, out var spawn, 5f, NavMesh.AllAreas))
                return false;

            var reused = pool.Count > 0;
            var pooled = reused ? pool.Dequeue() : default;
            var unitObject = reused
                ? pooled.GameObject
                : Instantiate(template, spawn.position + Vector3.up, Quaternion.identity, transform);
            var agent = unitObject.GetComponent<RecoilNavMeshPathAgent>();
            if (agent == null)
            {
                Debug.LogError($"Spawned soldier '{unitObject.name}' has no {nameof(RecoilNavMeshPathAgent)}.", unitObject);
                Destroy(unitObject);
                return false;
            }

            if (reused)
                agent.ResetForSpawn();

            var entity = reused ? pooled.Entity : _entityManager.CreateEntity(_soldierArchetype);
            if (!reused)
                _ownedSoldierEntities.Add(entity);
            _entityManager.SetComponentData(entity, new RecoilBattleEcsState
            {
                Team = isBlue ? 0 : 1,
                Health = 100,
                AttackCooldown = 0f,
                Active = 1,
                Target = Entity.Null
            });

            unitObject.transform.SetPositionAndRotation(spawn.position + Vector3.up, Quaternion.identity);
            unitObject.name = $"{(isBlue ? "Blue" : "Red")} Soldier {team.Count + 1:00}";
            unitObject.SetActive(true);

            var colliders = unitObject.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < colliders.Length; i++)
                colliders[i].enabled = false;

            if (!reused && instancedRenderer != null)
                instancedRenderer.RegisterUnit(unitObject, isBlue);

            team.Add(new Soldier { Agent = agent, Entity = entity });
            if (isBlue)
                _blueSpawnedTotal++;
            else
                _redSpawnedTotal++;
            _resultReported = false;
            return true;
        }

        private void UpdateNavigationBridge(Soldier soldier, Soldier target)
        {
            if (soldier.Agent == null)
                return;

            if (target == null || target.Agent == null)
            {
                soldier.Agent.Stop();
                soldier.NavigationTarget = null;
                return;
            }

            var ownPosition = soldier.Agent.transform.position;
            var targetPosition = target.Agent.transform.position;
            var distance = HorizontalDistance(ownPosition, targetPosition);
            if (distance <= attackRange)
            {
                soldier.Agent.Stop();
                soldier.NavigationTarget = target;
                return;
            }

            var targetMoved = soldier.NavigationTarget != target ||
                              HorizontalDistance(soldier.LastDestination, targetPosition) >= retargetDistance;
            if (targetMoved && Time.time >= soldier.NextPathRequestTime)
            {
                soldier.NavigationTarget = target;
                soldier.LastDestination = targetPosition;
                soldier.NextPathRequestTime = Time.time + 0.35f;
                soldier.Agent.SetDestination(targetPosition);
            }
            else if (!soldier.Agent.HasPath &&
                     !soldier.Agent.IsPathPending &&
                     Time.time >= soldier.NextPathRequestTime)
            {
                soldier.NavigationTarget = target;
                soldier.LastDestination = targetPosition;
                soldier.NextPathRequestTime = Time.time + 1f;
                soldier.Agent.SetDestination(targetPosition);
            }
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return (a - b).sqrMagnitude;
        }

        private void KillSoldier(Soldier soldier)
        {
            if (soldier.Entity != Entity.Null && _entityManager.Exists(soldier.Entity))
            {
                var state = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity);
                state.Health = 0;
                state.Active = 0;
                state.Target = Entity.Null;
                _entityManager.SetComponentData(soldier.Entity, state);
            }

            if (soldier.Agent != null)
            {
                soldier.Agent.Stop();
                var unitObject = soldier.Agent.gameObject;
                var isBlue = _entityManager.GetComponentData<RecoilBattleEcsState>(soldier.Entity).Team == 0;
                var pool = isBlue
                    ? _blueUnitPool
                    : _redUnitPool;
                unitObject.SetActive(false);
                if (pool.Count < MaxPooledUnitsPerTeam)
                    pool.Enqueue(new PooledSoldier(unitObject, soldier.Entity));
                else
                {
                    Destroy(unitObject);
                    if (_entityManager.Exists(soldier.Entity))
                        _entityManager.DestroyEntity(soldier.Entity);
                    _ownedSoldierEntities.Remove(soldier.Entity);
                }
            }
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
            for (var i = team.Count - 1; i >= 0; i--)
            {
                if (team[i].Agent == null || !IsAlive(team[i]))
                    team.RemoveAt(i);
            }
        }

        private void OnGUI()
        {
            GUI.Box(new Rect(6f, 6f, 738f, 82f), GUIContent.none);
            GUI.Label(new Rect(12f, 12f, 560f, 24f),
                $"ALIVE  BLUE: {_blueTeam.Count}   RED: {_redTeam.Count}   TOTAL: {_blueTeam.Count + _redTeam.Count}");
            GUI.Label(new Rect(12f, 34f, 720f, 24f),
                $"SPAWNED  BLUE: {_blueSpawnedTotal}   RED: {_redSpawnedTotal}   TOTAL: {_blueSpawnedTotal + _redSpawnedTotal}");
            GUI.Label(new Rect(12f, 56f, 720f, 24f),
                $"QUEUED  BLUE: {_blueSpawnQueue}   RED: {_redSpawnQueue}   |   1: +500 Blue   2: +500 Red   3: +500 each");
        }
    }
}
