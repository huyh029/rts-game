using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.AI;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        [Header("Lính đi ra từ điểm spawn")]
        [SerializeField, Tooltip("Các loại quân dùng chung vùng xuất quân quanh trung tâm map.")]
        private bool fillSpawnFrontage = false;
        [SerializeField, Range(0.05f, 1f), Tooltip("Tỷ lệ chiều dài vùng xuất quân quanh trung tâm map.")]
        private float spawnFrontageFraction = 0.25f;
        [SerializeField, Min(0f)] private float spawnLaneGap = 0.02f;
        [SerializeField, Min(1)] private int soldiersPerSpawnRow = 10;
        [SerializeField, Min(1f)] private float spawnExitDistance = 25f;
        [SerializeField, Min(0f)] private float spawnRearSearchDistance = 20f;
        [SerializeField, Min(0.1f)] private float spawnWorkBudgetMilliseconds = 2f;
        private NativeList<SpawnFootprint> _spawnFootprints;
        private NativeArray<byte> _spawnOccupiedSlots;
        private static readonly RecoilCpuPhases.Marker SpawnOccupancyMarker = new("RecoilBattle.SpawnOccupancyJob");
        private static readonly RecoilCpuPhases.Marker SpawnFootprintsMarker = new("RecoilBattle.SpawnFootprints");
        private static readonly RecoilCpuPhases.Marker SpawnQueueMarker = new("RecoilBattle.SpawnQueue");
        [BurstCompile]
        private struct SpawnOccupancyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<SpawnFootprint> Footprints;
            [WriteOnly] public NativeArray<byte> Occupied;
            public float3 Anchor, Forward, Right;
            public float CurrentDepth, MaximumDepth, Pitch, Minimum, Maximum, LaneSpacing, Radius, Gap;
            public int Width;
            public void Execute(int index)
            {
                var row = index / Width;
                var lane = index % Width;
                var depth = row == 0 ? CurrentDepth : math.min(MaximumDepth, (row - 1) * Pitch);
                var position = Anchor - Forward * depth + Right * (Width > 1 ? Minimum + lane * LaneSpacing : (Minimum + Maximum) * 0.5f);
                Occupied[index] = 0;
                for (var i = 0; i < Footprints.Length; i++)
                {
                    var footprint = Footprints[i];
                    var clearance = math.max(0f, Radius + footprint.Radius + Gap - 0.0001f);
                    if (math.distancesq(position.xz, ((float3)footprint.Position).xz) >= clearance * clearance) continue;
                    Occupied[index] = 1;
                    return;
                }
            }
        }

        private void EnsureSpawnFootprints()
        {
            if (!_spawnFootprints.IsCreated) _spawnFootprints = new NativeList<SpawnFootprint>(512, Allocator.Persistent);
        }

        private void DisposeSpawnJobs()
        {
            if (_spawnFootprints.IsCreated) _spawnFootprints.Dispose();
            if (_spawnOccupiedSlots.IsCreated) _spawnOccupiedSlots.Dispose();
            _spawnFootprintFrame = -1;
        }
        private int _spawnFootprintFrame = -1;
        private struct SpawnFootprint { public Vector3 Position; public float Radius; }
        private int _pendingBlueExitLane, _pendingRedExitLane;
        private bool _spawnExitBlocked;
        private float _spawnExitPitch;
        private readonly SpawnRow _blueSpawnRow = new(), _redSpawnRow = new();
        private sealed class SpawnRow
        {
            public int Count, Ordinal;
            public int Type = -1;
            public bool Selected;
            public bool[] Used;
            public Vector3[] Candidates;
            public float Depth;
            public double BlockedSince = -1d, NextBlockedReport;

            public void Reset()
            {
                Count = 0; Type = -1; Ordinal++; Depth = 0f; BlockedSince = -1d;
                if (Used != null) System.Array.Clear(Used, 0, Used.Length);
            }
        }

        private bool TryFindStreamSpawnPosition(bool blue, float radius, out NavMeshHit hit)
        {
            hit = default;
            _spawnExitBlocked = false;
            // Every row has one troop type: use that type's diameter, not the largest model in the army.
            _spawnExitPitch = Mathf.Max(0.3f, radius * 2f + spawnLaneGap);
            var forward = BattleAxis() * (blue ? 1f : -1f);
            var right = new Vector3(forward.z, 0f, -forward.x);
            var anchor = GetSpawnTargetPosition(blue);
            anchor.x = Mathf.Clamp(anchor.x, SpawnMinX, SpawnMaxX);
            anchor.z = Mathf.Clamp(anchor.z, SpawnMinZ, SpawnMaxZ);
            var minimum = -(Mathf.Clamp(soldiersPerSpawnRow, 1, 64) - 1) * _spawnExitPitch * 0.5f;
            var maximum = -minimum;
            if (fillSpawnFrontage && !TryGetSpawnFrontage(anchor, right, _spawnExitPitch * 0.5f, out minimum, out maximum)) return false;
            if (fillSpawnFrontage)
            {
                var center = (minimum + maximum) * 0.5f;
                var halfLength = (maximum - minimum) * 0.5f * Mathf.Clamp(spawnFrontageFraction, 0.05f, 1f);
                minimum = center - halfLength;
                maximum = center + halfLength;
            }
            var width = fillSpawnFrontage ? Mathf.Clamp(Mathf.FloorToInt((maximum - minimum) / _spawnExitPitch) + 1, 1, 2048)
                : Mathf.Clamp(soldiersPerSpawnRow, 1, 64);
            var row = blue ? _blueSpawnRow : _redSpawnRow;
            if (row.Used == null || row.Used.Length != width || row.Candidates == null || row.Candidates.Length != width)
            { row.Used = new bool[width]; row.Candidates = new Vector3[width]; row.Count = 0; row.Depth = 0f; }
            var laneSpacing = width > 1 ? (maximum - minimum) / (width - 1) : _spawnExitPitch;
            RefreshSpawnFootprints();
            var projection = terrainMap != null && terrainMap.HasValidData
                ? Mathf.Max(0.5f, Mathf.Max(terrainMap.CellSize.x, terrainMap.CellSize.y) * 0.5f) : 1f;
            var activeHeights = pathService.TerrainHeightField;
            if (activeHeights.IsCreated)
                projection = Mathf.Max(0.5f, Mathf.Max(activeHeights.Size.x / (activeHeights.Columns - 1),
                    activeHeights.Size.y / (activeHeights.Rows - 1)) * 0.5f);
            var searchDistance = Mathf.Max(0f, spawnRearSearchDistance);
            var steps = Mathf.Clamp(Mathf.CeilToInt(searchDistance / _spawnExitPitch), 0, 32);
            var slotCount = (steps + 2) * width;
            if (!_spawnOccupiedSlots.IsCreated || _spawnOccupiedSlots.Length < slotCount)
            {
                if (_spawnOccupiedSlots.IsCreated) _spawnOccupiedSlots.Dispose();
                _spawnOccupiedSlots = new NativeArray<byte>(Mathf.NextPowerOfTwo(slotCount), Allocator.Persistent);
            }
            using (SpawnOccupancyMarker.Auto())
                new SpawnOccupancyJob { Footprints = _spawnFootprints.AsArray(), Occupied = _spawnOccupiedSlots,
                    Anchor = anchor, Forward = forward, Right = right, CurrentDepth = row.Depth,
                    MaximumDepth = searchDistance, Pitch = _spawnExitPitch, Minimum = minimum, Maximum = maximum,
                    LaneSpacing = laneSpacing, Radius = radius, Gap = spawnLaneGap, Width = width }
                    .Schedule(slotCount, 32).Complete();
            // Select one valid lane at a time. Requiring every remaining lane to be
            // walkable would block a wide row when just one edge touches a hole or boundary.
            for (var depthStep = -1; depthStep <= steps; depthStep++)
            {
                var depth = depthStep < 0 ? row.Depth : Mathf.Min(searchDistance, depthStep * _spawnExitPitch);
                if (depthStep >= 0 && Mathf.Abs(depth - row.Depth) < 0.0001f) continue;
                var rowAnchor = anchor - forward * depth;
                for (var attempt = 0; attempt < width; attempt++)
                {
                    var lane = SpreadSpawnLane(row.Count + attempt, width);
                    if (row.Used[lane]) continue;
                    if (_spawnOccupiedSlots[(depthStep + 1) * width + lane] != 0)
                    { _spawnExitBlocked = true; continue; }
                    var desired = rowAnchor + right * (width > 1 ? minimum + lane * laneSpacing : (minimum + maximum) * 0.5f);
                    if (TryGetGroundHeight(desired, out var height)) desired.y = height;
                    if (!IsWithinSpawnBounds(desired) ||
                        !pathService.TrySamplePosition(desired, projection, GetSpawnAreaMask(blue), out var ground) ||
                        HorizontalDistance(desired, ground) > projection || !IsTerrainPositionWalkableForTeam(ground, blue)) continue;
                    if (HorizontalDistance(desired, ground) > 0.00001f && IsSpawnOccupied(ground, radius))
                    { _spawnExitBlocked = true; continue; }
                    row.Depth = depth;
                    row.Candidates[lane] = ground;
                    if (blue) _pendingBlueExitLane = lane;
                    else _pendingRedExitLane = lane;
                    row.BlockedSince = -1d;
                    _spawnExitBlocked = false;
                    hit = new NavMeshHit { position = ground };
                    return true;
                }
            }
            if (_spawnExitBlocked)
            {
                var now = Time.unscaledTimeAsDouble;
                if (row.BlockedSince < 0d) row.BlockedSince = now;
                if (now - row.BlockedSince >= 3d && now >= row.NextBlockedReport)
                {
                    row.NextBlockedReport = now + 5d;
                    Debug.LogWarning($"[Spawn occupied] Team={(blue ? "Blue" : "Red")}, type={row.Type}, row={row.Count}/{width}, " +
                        $"nearby bodies={_spawnFootprints.Length}, anchor={anchor}, rear search={searchDistance:F1}m. Waiting for physical space; queued troops are preserved.", this);
                }
            }
            return false;
        }

        private void RefreshSpawnFootprints()
        {
            using var marker = SpawnFootprintsMarker.Auto();
            EnsureSpawnFootprints();
            if (_spawnFootprintFrame == Time.frameCount) return;
            _spawnFootprintFrame = Time.frameCount;
            _spawnFootprints.Clear();
            var blueOrigin = GetSpawnTargetPosition(true);
            var redOrigin = GetSpawnTargetPosition(false);
            var reach = Mathf.Max(32f, _spawnExitPitch * Mathf.Clamp(soldiersPerSpawnRow, 1, 64)) + Mathf.Max(0f, spawnRearSearchDistance);
            CollectSpawnFootprints(_blueTeam, blueOrigin, redOrigin, reach);
            CollectSpawnFootprints(_redTeam, blueOrigin, redOrigin, reach);
        }

        private void CollectSpawnFootprints(List<Soldier> team, Vector3 blueOrigin, Vector3 redOrigin, float reach)
        {
            foreach (var soldier in team)
            {
                if (soldier == null || soldier.Retired || soldier.Simulation == null) continue;
                Vector3 position;
                if (TryGetCombatSnapshot(soldier, out _, out var snapshot)) position = snapshot.Position;
                else if (soldier.Simulation.TryGetPose(out var pose)) position = pose.Position;
                else continue;
                var range = reach + soldier.BodyRadius;
                var blueOffset = position - blueOrigin; blueOffset.y = 0f;
                var redOffset = position - redOrigin; redOffset.y = 0f;
                if (!fillSpawnFrontage && blueOffset.sqrMagnitude > range * range && redOffset.sqrMagnitude > range * range) continue;
                _spawnFootprints.Add(new SpawnFootprint { Position = position, Radius = soldier.BodyRadius });
            }
        }

        private bool IsSpawnOccupied(Vector3 position, float radius)
        {
            foreach (var footprint in _spawnFootprints)
                if (!HasClearedSpawn(footprint.Position, position, radius + footprint.Radius + spawnLaneGap)) return true;
            return false;
        }

        private static bool HasClearedSpawn(Vector3 position, Vector3 origin, float clearance)
        {
            var x = position.x - origin.x;
            var z = position.z - origin.z;
            var distance = Mathf.Max(0f, clearance - 0.0001f);
            return x * x + z * z >= distance * distance;
        }

        private bool TryGetSpawnFrontage(Vector3 anchor, Vector3 right, float padding, out float minimum, out float maximum)
        {
            var minX = SpawnMinX + padding; var maxX = SpawnMaxX - padding;
            var minZ = SpawnMinZ + padding; var maxZ = SpawnMaxZ - padding;
            var heights = pathService.TerrainHeightField;
            if (heights.IsCreated)
            {
                minX = Mathf.Max(minX, heights.Origin.x + padding);
                maxX = Mathf.Min(maxX, heights.Origin.x + heights.Size.x - padding);
                minZ = Mathf.Max(minZ, heights.Origin.z + padding);
                maxZ = Mathf.Min(maxZ, heights.Origin.z + heights.Size.y - padding);
            }
            minimum = float.NegativeInfinity; maximum = float.PositiveInfinity;
            return minX <= maxX && minZ <= maxZ &&
                ClipSpawnLine(anchor.x, right.x, minX, maxX, ref minimum, ref maximum) &&
                ClipSpawnLine(anchor.z, right.z, minZ, maxZ, ref minimum, ref maximum) && minimum <= maximum;
        }

        private static bool ClipSpawnLine(float origin, float direction, float lower, float upper, ref float minimum, ref float maximum)
        {
            if (Mathf.Abs(direction) < 0.00001f) return origin >= lower && origin <= upper;
            var a = (lower - origin) / direction;
            var b = (upper - origin) / direction;
            minimum = Mathf.Max(minimum, Mathf.Min(a, b));
            maximum = Mathf.Min(maximum, Mathf.Max(a, b));
            return minimum <= maximum;
        }

        private static int SpreadSpawnLane(int ordinal, int count)
        {
            if (count <= 1) return 0;
            // All unit types share the same center-out sequence; no type owns a fixed road.
            var local = ordinal % count;
            return (count - 1) / 2 + (local % 2 == 1 ? (local + 1) / 2 : -local / 2);
        }

        private static void EnqueueSpawnType(Queue<SelectedSpawnBatch> queue, int type, int amount)
        {
            foreach (var batch in queue)
                if (batch.CharacterType == type) { batch.Remaining += amount; return; }
            queue.Enqueue(new SelectedSpawnBatch { CharacterType = type, Remaining = amount });
        }

        private static void ConsumeSpawnType(Queue<SelectedSpawnBatch> queue, bool finishRow = true)
        {
            var batch = queue.Peek();
            batch.Remaining--;
            if (batch.Remaining <= 0) queue.Dequeue();
            else if (finishRow) queue.Enqueue(queue.Dequeue());
        }

        private void ConsumeSpawnRow(bool blue, Queue<SelectedSpawnBatch> queue)
        {
            var row = blue ? _blueSpawnRow : _redSpawnRow;
            ConsumeSpawnRow(row, queue);
        }

        private static void ConsumeSpawnRow(SpawnRow row, Queue<SelectedSpawnBatch> queue)
        {
            var finish = row.Count >= row.Used.Length || queue.Peek().Remaining <= 1;
            ConsumeSpawnType(queue, finish);
            if (finish) row.Reset();
        }

        private void FinishDefaultSpawnRow(bool blue)
        {
            var row = blue ? _blueSpawnRow : _redSpawnRow;
            if (row.Used != null && (row.Count >= row.Used.Length || (blue ? _blueSpawnQueue : _redSpawnQueue) <= 0)) row.Reset();
        }

        private void FinishUnavailableSpawnRow(bool blue, Queue<SelectedSpawnBatch> queue)
        {
            var row = blue ? _blueSpawnRow : _redSpawnRow;
            if (_spawnExitBlocked || row.Count == 0) return;
            if (queue.Count > 1) queue.Enqueue(queue.Dequeue());
            row.Reset();
            _spawnExitBlocked = true;
        }

#if UNITY_EDITOR
        public static void ValidateSpawnTypeDistribution()
        {
            var queue = new Queue<SelectedSpawnBatch>();
            EnqueueSpawnType(queue, 1, 3);
            EnqueueSpawnType(queue, 2, 2);
            EnqueueSpawnType(queue, 1, 1);
            if (queue.Count != 2) throw new System.InvalidOperationException("Repeated requests must merge by type.");
            foreach (var expected in new[] { 1, 2, 1, 2, 1, 1 })
            {
                if (queue.Count == 0 || queue.Peek().CharacterType != expected)
                    throw new System.InvalidOperationException("Spawn types must alternate without dropping queued units.");
                ConsumeSpawnType(queue);
            }
            if (queue.Count != 0) throw new System.InvalidOperationException("Spawn counts changed while alternating types.");
            EnqueueSpawnType(queue, 1, 7);
            EnqueueSpawnType(queue, 2, 5);
            var row = new SpawnRow { Used = new bool[3] };
            foreach (var expected in new[] { 1, 1, 1, 2, 2, 2, 1, 1, 1, 2, 2, 1 })
            {
                if (queue.Count == 0 || queue.Peek().CharacterType != expected)
                    throw new System.InvalidOperationException("Each horizontal row must use one troop type; types alternate only between rows.");
                row.Count++;
                ConsumeSpawnRow(row, queue);
            }
            if (queue.Count != 0 || row.Count != 0)
                throw new System.InvalidOperationException("Partial final rows must finish without losing troop requests.");
            var origin = new Vector3(42f, 3f, 3.3f);
            if (!HasClearedSpawn(origin + Vector3.forward * 2f, origin, 1f) ||
                !HasClearedSpawn(origin + Vector3.right * 2f, origin, 1f) ||
                !HasClearedSpawn(origin - Vector3.right * 2f, origin, 1f) ||
                HasClearedSpawn(origin + Vector3.up * 5f, origin, 1f) ||
                HasClearedSpawn(origin + Vector3.right * 0.5f, origin, 1f))
                throw new System.InvalidOperationException("Spawn clearance must support sideways/backward departure but reject occupied footprints.");
            for (var count = 1; count <= 256; count++)
            {
                var visited = new bool[count];
                for (var ordinal = 0; ordinal < count; ordinal++)
                {
                    var lane = SpreadSpawnLane(ordinal, count);
                    if (lane < 0 || lane >= count || visited[lane])
                        throw new System.InvalidOperationException("Even lane distribution must visit every slot once per cycle.");
                    visited[lane] = true;
                }
            }
            var minimum = float.NegativeInfinity; var maximum = float.PositiveInfinity;
            if (!ClipSpawnLine(0f, 0f, -100f, 100f, ref minimum, ref maximum) ||
                !ClipSpawnLine(50f, 1f, -250f, 250f, ref minimum, ref maximum) || minimum != -300f || maximum != 200f)
                throw new System.InvalidOperationException("Spawn frontage must span the full map length even with an offset anchor.");
            ValidateSpawnOccupancy();
            Debug.Log("Spawn distribution checks passed: alternating types, merged requests, preserved quantities, full lane coverage and map-length frontage.");
        }

        private static void ValidateSpawnOccupancy()
        {
            var owner = new GameObject("Temporary spawn occupancy check");
            owner.SetActive(false);
            try
            {
                var battle = owner.AddComponent<RecoilAutoBattleDemo>();
                battle.EnsureSpawnFootprints();
                battle._spawnFootprints.Add(new SpawnFootprint { Position = Vector3.zero, Radius = 0.5f });
                if (!battle.IsSpawnOccupied(Vector3.zero, 0.5f) ||
                    battle.IsSpawnOccupied(Vector3.left * 2f, 0.5f) || battle.IsSpawnOccupied(Vector3.right * 2f, 0.5f))
                    throw new System.InvalidOperationException("An occupied front slot must block overlap but leave rear slots available to both teams.");
                using var occupied = new NativeArray<byte>(6, Allocator.TempJob);
                foreach (var direction in new[] { Vector3.forward, Vector3.back })
                {
                    new SpawnOccupancyJob { Footprints = battle._spawnFootprints.AsArray(), Occupied = occupied,
                        Anchor = Vector3.zero, Forward = direction, Right = Vector3.right,
                        CurrentDepth = 2f, MaximumDepth = 2f, Pitch = 1f, Minimum = -2f, Maximum = 2f,
                        LaneSpacing = 2f, Radius = 0.5f, Gap = battle.spawnLaneGap, Width = 3 }.Schedule(6, 1).Complete();
                    for (var slot = 0; slot < 6; slot++)
                    {
                        var desired = -direction * (slot < 3 ? 2f : 0f) + Vector3.right * (-2f + (slot % 3) * 2f);
                        if ((occupied[slot] != 0) != battle.IsSpawnOccupied(desired, 0.5f))
                            throw new System.InvalidOperationException("Spawn occupancy jobs must match physical clearance for both team directions and rear rows.");
                    }
                }
                // Empty live rosters must clear old reservations on the next occupancy refresh.
                battle._spawnFootprintFrame = -1;
                battle.RefreshSpawnFootprints();
                if (battle.IsSpawnOccupied(Vector3.zero, 0.5f))
                    throw new System.InvalidOperationException("Stale/dead spawn reservations must not block future waves.");
            }
            finally { owner.GetComponent<RecoilAutoBattleDemo>()?.DisposeSpawnJobs(); DestroyImmediate(owner); }
        }
#endif

        private void RecordStreamSpawn(Soldier soldier)
        {
            var lane = soldier.IsBlue ? _pendingBlueExitLane : _pendingRedExitLane;
            var row = soldier.IsBlue ? _blueSpawnRow : _redSpawnRow;
            row.Used[lane] = true;
            row.Count++;
            // Include new units immediately; the ECS snapshot is refreshed on a fixed tick.
            _spawnFootprints.Add(new SpawnFootprint { Position = soldier.Simulation.Position, Radius = soldier.BodyRadius });
            if (soldier.IsBlue) _blueCombatSpawnSlotIndex = (lane + 1) % row.Used.Length;
            else _redCombatSpawnSlotIndex = (lane + 1) % row.Used.Length;
            if (_combatStarted) return;
            var destination = soldier.Simulation.Position + BattleAxis() * (soldier.IsBlue ? 1f : -1f) * spawnExitDistance;
            destination.x = Mathf.Clamp(destination.x, SpawnMinX, SpawnMaxX);
            destination.z = Mathf.Clamp(destination.z, SpawnMinZ, SpawnMaxZ);
            if (TryGetGroundHeight(destination, out var height)) destination.y = height;
            if (pathService.TrySamplePosition(destination, 2f, GetSpawnAreaMask(soldier.IsBlue), out var projected))
                destination = projected;
            soldier.Simulation.SetDestination(destination);
        }
    }
}
