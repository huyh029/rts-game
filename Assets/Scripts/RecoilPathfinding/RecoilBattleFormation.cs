using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace RecoilPathfinding
{
    // Positions are local: x across the line, y toward the opponent. Front edge is 50m from the midpoint.
    public static class RecoilBattleFormation
    {
        public const float ForwardDistance = 50f;
        public const float BlockGap = 1f;
        private static readonly int[][] Rows =
        {
            new[] { 3, 1, 6, 1, 3 }, new[] { 5, 5, 5 }, new[] { 2, 2, 2 },
            new[] { 7, 7, 7 }, new[] { 0, 0, 0 }, new[] { 4, 4, 4 }
        };

        public static Vector3 ToWorld(Vector3 spawn, Vector3 forward, Vector2 slot) =>
            spawn + Vector3.Cross(Vector3.up, forward) * slot.x + forward * slot.y;

        public static Vector2[][] Build(int[] counts, float[] radii, int maxDepthRows)
        {
            var result = new Vector2[counts.Length][];
            for (var type = 0; type < counts.Length; type++) result[type] = new Vector2[counts[type]];
            maxDepthRows = Mathf.Max(1, maxDepthRows);
            var front = ForwardDistance;
            foreach (var row in Rows)
            {
                var widths = new float[row.Length];
                var pitches = new float[row.Length];
                var columns = new int[row.Length];
                var capacities = new int[row.Length];
                var width = 0f;
                var depth = 0f;
                var occupied = 0;
                for (var block = 0; block < row.Length; block++)
                {
                    var type = row[block];
                    if (type >= counts.Length) continue;
                    if (counts[type] == 0) continue;
                    var blocks = 0;
                    foreach (var item in row) if (item == type) blocks++;
                    var capacity = (counts[type] + blocks - 1) / blocks;
                    capacities[block] = capacity;
                    var radius = Mathf.Max(.05f, radii[type]);
                    var pitch = Mathf.Max(1f, radius * 2f + .1f);
                    // Keep the assembled force within the configured maximum depth.
                    // SpawnColumns is the requested depth in rows; width grows as needed.
                    var cols = Mathf.Min(capacity, Mathf.CeilToInt((float)capacity / maxDepthRows));
                    columns[block] = cols;
                    pitches[block] = pitch;
                    widths[block] = (cols - 1) * pitch + radius * 2f;
                    width += widths[block];
                    depth = Mathf.Max(depth, ((capacity + cols - 1) / cols - 1) * pitch + radius * 2f);
                    occupied++;
                }
                if (occupied == 0) continue;
                width += (occupied - 1) * BlockGap;
                var left = -width * .5f;
                for (var block = 0; block < row.Length; block++)
                {
                    if (capacities[block] == 0) continue;
                    var type = row[block];
                    if (type >= counts.Length) continue;
                    var ordinal = 0;
                    var blocks = 0;
                    for (var i = 0; i < row.Length; i++)
                        if (row[i] == type) { if (i < block) ordinal++; blocks++; }
                    var center = left + widths[block] * .5f;
                    var count = counts[type] <= ordinal ? 0 : (counts[type] - 1 - ordinal) / blocks + 1;
                    for (var local = 0; local < count; local++)
                    {
                        var line = local / columns[block];
                        var lineCount = Mathf.Min(columns[block], count - line * columns[block]);
                        result[type][local * blocks + ordinal] = new Vector2(
                            center + (local % columns[block] - (lineCount - 1) * .5f) * pitches[block],
                            front - Mathf.Max(.05f, radii[type]) - line * pitches[block]);
                    }
                    left += widths[block] + BlockGap;
                }
                front -= depth + BlockGap;
            }
            return result;
        }
    }

    public sealed partial class RecoilAutoBattleDemo
    {
        private bool _formationReady;
        private string _assemblyStatus = "Đang xuất quân từ điểm spawn";
        private Vector2[][] _blueSpawnFormation;
        private Vector2[][] _redSpawnFormation;
        private int[] _blueSpawnFormationIndices;
        private int[] _redSpawnFormationIndices;
        private readonly List<Vector3> _preparedBluePositions = new();
        private readonly List<Vector3> _preparedRedPositions = new();
        private bool _preparedPositionsBuilt;

        private bool TryGetPreparedSpawnPosition(bool blue, out NavMeshHit hit)
        {
            hit = default;
            if (!_preparedPositionsBuilt)
            {
                _preparedPositionsBuilt = true;
                var middle = (GetSpawnTargetPosition(true) + GetSpawnTargetPosition(false)) * .5f;
                var axis = BattleAxis();
                var lateral = Vector3.Cross(Vector3.up, axis);
                var targetBlue = PreparedTeamTarget(true);
                BuildPreparedPositions(_preparedBluePositions, true, targetBlue, middle, axis, lateral);
                BuildPreparedPositions(_preparedRedPositions, false, PreparedTeamTarget(false),
                    middle, -axis, lateral);
                if (_preparedBluePositions.Count < targetBlue || _preparedRedPositions.Count < PreparedArmyTarget - targetBlue)
                    Debug.LogError($"Prepared formation does not fit walkable terrain: blue={_preparedBluePositions.Count}/{targetBlue}, red={_preparedRedPositions.Count}/{PreparedArmyTarget - targetBlue}. Reduce army size or spacing.", this);
            }
            var positions = blue ? _preparedBluePositions : _preparedRedPositions;
            var index = blue ? _blueSpawnedTotal : _redSpawnedTotal;
            if (index >= positions.Count) return false;
            hit.position = positions[index];
            return true;
        }

        private void BuildPreparedPositions(List<Vector3> result, bool blue, int count, Vector3 middle,
            Vector3 forward, Vector3 lateral)
        {
            var extent = Mathf.Sqrt((SpawnMaxX - SpawnMinX) * (SpawnMaxX - SpawnMinX) +
                (SpawnMaxZ - SpawnMinZ) * (SpawnMaxZ - SpawnMinZ));
            var depth = 10f;
            var previousRadius = 0f;
            for (var type = 0; type < CharacterTypeCount; type++)
            {
                var radius = .5f;
                var template = FindTemplateForType(blue, type);
                if (template != null && template.TryGetComponent<RecoilNavMeshPathAgent>(out var agent))
                    radius = Mathf.Max(.15f, agent.MovementSettings.BodyRadius);
                var pitch = radius * 2f + .1f;
                // Each type gets its own compact rows, with clearance at type boundaries.
                depth += radius - previousRadius;
                previousRadius = radius;
                var typeCount = PreparedTypeCount(blue, type);
                var target = result.Count + typeCount;
                var halfWidth = Mathf.Min(extent * .5f, Mathf.Max(pitch, preparedFormationHalfWidth));
                var columns = Mathf.FloorToInt(halfWidth / pitch) * 2 + 1;
                for (; depth < extent && result.Count < target; depth += pitch)
                    for (var column = 0; column < columns && result.Count < target; column++)
                    {
                        var ordinal = (column + 1) / 2;
                        var cross = ordinal * pitch * (column % 2 == 0 ? -1f : 1f);
                        var point = middle - forward * depth + lateral * cross;
                        if (point.x < SpawnMinX + radius || point.x > SpawnMaxX - radius ||
                            point.z < SpawnMinZ + radius || point.z > SpawnMaxZ - radius) continue;
                        if (!TryGetGroundHeight(point, out var height)) continue;
                        point.y = height;
                        if (!pathService.TrySamplePosition(point, .25f, GetSpawnAreaMask(blue), out var projected)) continue;
                        if ((projected - point).sqrMagnitude > .01f) continue;
                        result.Add(projected);
                    }
                if (result.Count < target) return;
            }
        }
        private void InvalidateAssembly()
        {
            _formationReady = false;
            _assemblyStatus = "Đang xuất quân từ điểm spawn";
        }

        private Vector3 BattleAxis()
        {
            var direction = GetSpawnTargetPosition(false) - GetSpawnTargetPosition(true);
            direction.y = 0f;
            return direction.sqrMagnitude > .001f ? direction.normalized : Vector3.right;
        }

        private bool TryFindInitialSpawnPosition(bool blue, out NavMeshHit hit)
        {
            return TryFindSpawnInFixedArea(blue, out hit);
        }

        private void EnsureSpawnFormationPlans()
        {
            if (_blueSpawnFormation != null && _redSpawnFormation != null)
                return;

            BuildSpawnFormationPlan(true, out _blueSpawnFormation, out _blueSpawnFormationIndices);
            BuildSpawnFormationPlan(false, out _redSpawnFormation, out _redSpawnFormationIndices);
        }

        private void BuildSpawnFormationPlan(
            bool blue,
            out Vector2[][] formation,
            out int[] nextIndices)
        {
            var team = blue ? _blueTeam : _redTeam;
            var opposingTeam = blue ? _redTeam : _blueTeam;
            var counts = new int[CharacterTypeCount];
            var existingCounts = new int[CharacterTypeCount];
            var opposingCounts = new int[CharacterTypeCount];
            var radii = new float[CharacterTypeCount];
            CountTeamFormationUnits(team, existingCounts, radii);
            CountTeamFormationUnits(opposingTeam, opposingCounts, radii);
            System.Array.Copy(existingCounts, counts, CharacterTypeCount);

            var selectedQueue = blue ? _blueSelectedSpawnQueue : _redSelectedSpawnQueue;
            foreach (var batch in selectedQueue)
                AddPendingFormationUnit(blue, batch.CharacterType, batch.Remaining, counts, radii);

            var pendingDefault = blue ? _blueSpawnQueue : _redSpawnQueue;
            var spawnedTotal = blue ? _blueSpawnedTotal : _redSpawnedTotal;
            for (var i = 0; i < pendingDefault; i++)
                AddPendingFormationUnit(
                    blue,
                    Mathf.Clamp(ResolveCharacterType(GetUnitTemplate(blue, spawnedTotal + i)), 0, CharacterTypeCount - 1),
                    1,
                    counts,
                    radii);

            var opposingSelectedQueue = blue ? _redSelectedSpawnQueue : _blueSelectedSpawnQueue;
            foreach (var batch in opposingSelectedQueue)
                AddPendingFormationUnit(!blue, batch.CharacterType, batch.Remaining, opposingCounts, radii);

            var opposingPendingDefault = blue ? _redSpawnQueue : _blueSpawnQueue;
            var opposingSpawnedTotal = blue ? _redSpawnedTotal : _blueSpawnedTotal;
            for (var i = 0; i < opposingPendingDefault; i++)
                AddPendingFormationUnit(
                    !blue,
                    Mathf.Clamp(ResolveCharacterType(GetUnitTemplate(!blue, opposingSpawnedTotal + i)), 0, CharacterTypeCount - 1),
                    1,
                    opposingCounts,
                    radii);

            for (var type = 0; type < CharacterTypeCount; type++)
                counts[type] = Mathf.Max(counts[type], opposingCounts[type]);

            formation = RecoilBattleFormation.Build(counts, radii, spawnColumns);
            nextIndices = existingCounts;
        }

        private void CountTeamFormationUnits(List<Soldier> team, int[] counts, float[] radii)
        {
            foreach (var soldier in team)
            {
                if (!IsAlive(soldier) || !TryGetMovementSettings(soldier, out var movementSettings)) continue;
                counts[soldier.CharacterType]++;
                radii[soldier.CharacterType] = Mathf.Max(
                    radii[soldier.CharacterType], movementSettings.BodyRadius);
            }
        }

        private void InvalidateSpawnFormationPlans()
        {
            _blueSpawnFormation = null;
            _redSpawnFormation = null;
            _blueSpawnFormationIndices = null;
            _redSpawnFormationIndices = null;
        }

        private void AddPendingFormationUnit(
            bool blue,
            int type,
            int amount,
            int[] counts,
            float[] radii)
        {
            if (type < 0 || type >= CharacterTypeCount || amount <= 0) return;
            counts[type] += amount;
            var template = FindTemplateForType(blue, type);
            if (template != null && template.TryGetComponent<RecoilNavMeshPathAgent>(out var agent))
                radii[type] = Mathf.Max(radii[type], agent.MovementSettings.BodyRadius);
        }

        private double _startCountdownEndsAt;
        private readonly RecoilCountdownVisual _startCountdownVisual = new();

        private void UpdateStartCountdown()
        {
            if (_startCountdownEndsAt <= 0 || Time.realtimeSinceStartupAsDouble < _startCountdownEndsAt) return;
            _startCountdownEndsAt = 0;
            if (!_combatStarted && !_matchFinished) TryStartBattle(true);
        }

        private void DrawStartCountdown()
        {
            _startCountdownVisual.Draw(_startCountdownEndsAt - Time.realtimeSinceStartupAsDouble);
        }

        private bool TryStartBattle(bool finishCountdown = false)
        {
            if (_combatStarted) return true;
            if (_startCountdownEndsAt > 0) return false;
            if (prepareArmyBeforeCombat && !IsPreparedArmyRendered())
            {
                Debug.Log("Waiting for all " + PreparedArmyTarget + " soldier models before starting combat.", this);
                return false;
            }
            if (!HasQueuedSpawns)
                UpdateAssembly();
            if (!HasAliveSoldier(_blueTeam) || !HasAliveSoldier(_redTeam))
            {
                Debug.Log("Chưa thể bắt đầu trận: " + _assemblyStatus, this);
                return false;
            }
            if (!finishCountdown)
            {
                _startCountdownEndsAt = Time.realtimeSinceStartupAsDouble + 3.0;
                return false;
            }
            _combatStarted = true;
            InitializeCastleCombatState();
            IssueInitialCombatMovement(_blueTeam);
            IssueInitialCombatMovement(_redTeam);
            _lobbyStarting = false;
            _matchElapsedSeconds = 0f;
            return true;
        }

        private bool IsPreparedArmyRendered()
        {
            if (HasQueuedSpawns) return false;
            var rendered = 0;
            foreach (var team in new[] { _blueTeam, _redTeam })
                foreach (var soldier in team)
                {
                    if (soldier == null || soldier.Retired) continue;
                    if (soldier.Agent == null || !soldier.Agent.gameObject.activeInHierarchy) return false;
                    rendered++;
                }
            return rendered >= Mathf.Max(2, PreparedArmyTarget);
        }

        private void IssueInitialCombatMovement(List<Soldier> team)
        {
            for (var i = 0; i < team.Count; i++)
            {
                var soldier = team[i];
                if (soldier == null || soldier.Retired || soldier.Simulation == null ||
                    !soldier.Simulation.Exists || !IsAlive(soldier))
                    continue;
                soldier.LastMovementCommandTime = SimulationNow;
                if (useFightCorridors && pathService != null && pathService.UsingTerrainMap)
                {
                    var timing = _entityManager.GetComponentData<RecoilUnitCombatTiming>(soldier.Entity);
                    if (timing.Fight.Enabled != 0)
                    {
                        var settings = soldier.Simulation.Settings;
                        if (pathService.TerrainHeightField.TryFindUnitApproach(soldier.Simulation.Position,
                            timing.Fight.End, settings.AreaMask, out var destination, settings.BodyRadius + .05f))
                            soldier.Simulation.SetDestination(destination);
                        continue;
                    }
                }
                UpdateNavigationBridge(soldier, null);
            }
        }

        private void UpdateAssembly()
        {
            if (_combatStarted) return;
            if (prepareArmyBeforeCombat)
            {
                _formationReady = !HasQueuedSpawns && _fullDetailSoldierCount >= Mathf.Max(2, PreparedArmyTarget);
                _assemblyStatus = _formationReady
                    ? "Ready: " + PreparedArmyTarget + " soldier models - press L to start combat"
                    : "Preparing soldier models: " + _fullDetailSoldierCount + "/" + PreparedArmyTarget;
                return;
            }
            // Units already have an exit-lane movement order. Never teleport/repack them
            // when a spawn batch completes or a new type is added.
            _formationReady = !HasQueuedSpawns && HasAliveSoldier(_blueTeam) && HasAliveSoldier(_redTeam);
            _assemblyStatus = HasAliveSoldier(_blueTeam) && HasAliveSoldier(_redTeam)
                ? "Lính đang đi ra từ điểm spawn — bấm L để giao chiến"
                : "Đang xuất quân từ điểm spawn";
        }

        private bool HasAliveSoldier(List<Soldier> team)
        {
            foreach (var soldier in team)
                if (soldier != null && !soldier.Retired && IsAlive(soldier)) return true;
            return false;
        }

    }
}
