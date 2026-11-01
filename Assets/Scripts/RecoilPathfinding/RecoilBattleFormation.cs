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
            new[] { 0, 0, 0 }, new[] { 4, 4, 4 }
        };

        public static Vector3 ToWorld(Vector3 spawn, Vector3 forward, Vector2 slot) =>
            spawn + Vector3.Cross(Vector3.up, forward) * slot.x + forward * slot.y;

        public static Vector2[][] Build(int[] counts, float[] radii)
        {
            var result = new Vector2[7][];
            for (var type = 0; type < 7; type++) result[type] = new Vector2[counts[type]];
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
                    if (counts[type] == 0) continue;
                    var blocks = 0;
                    foreach (var item in row) if (item == type) blocks++;
                    var capacity = (counts[type] + blocks - 1) / blocks;
                    capacities[block] = capacity;
                    var radius = Mathf.Max(.05f, radii[type]);
                    var pitch = Mathf.Max(1f, radius * 2f + .1f);
                    // Grow both axes instead of extending a fixed five-column strip.
                    // Prefer a block roughly twice as wide as it is deep.
                    var cols = Mathf.Min(capacity, Mathf.CeilToInt(Mathf.Sqrt(capacity * 2f)));
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
        private bool _formationDirty = true;
        private bool _formationReady;
        private bool _formationBlocked;
        private float _nextFormationCheck;
        private string _assemblyStatus = "Đang sinh lính để vào vị trí";
        private Vector2[][] _blueSpawnFormation;
        private Vector2[][] _redSpawnFormation;
        private int[] _blueSpawnFormationIndices;
        private int[] _redSpawnFormationIndices;

        private void InvalidateAssembly()
        {
            _formationDirty = true;
            _formationReady = false;
            _formationBlocked = false;
            _assemblyStatus = "Đang sinh lính để vào vị trí";
        }

        private Vector3 BattleAxis()
        {
            var direction = GetSpawnTargetPosition(false) - GetSpawnTargetPosition(true);
            direction.y = 0f;
            return direction.sqrMagnitude > .001f ? direction.normalized : Vector3.right;
        }

        private bool TryFindInitialFormationSpawnPosition(bool blue, byte characterType, out NavMeshHit hit)
        {
            hit = default;
            EnsureSpawnFormationPlans();
            var formation = blue ? _blueSpawnFormation : _redSpawnFormation;
            var indices = blue ? _blueSpawnFormationIndices : _redSpawnFormationIndices;
            if (characterType >= formation.Length ||
                indices[characterType] >= formation[characterType].Length)
                return false;

            var axis = BattleAxis();
            var forward = axis * (blue ? 1f : -1f);
            var midpoint = (GetSpawnTargetPosition(true) + GetSpawnTargetPosition(false)) * .5f;
            midpoint.y = 0f;
            var formationOrigin = midpoint - forward * (RecoilBattleFormation.ForwardDistance * 2f);
            var desired = RecoilBattleFormation.ToWorld(
                formationOrigin, forward, formation[characterType][indices[characterType]]);
            if (!TryGetGroundHeight(desired, out var groundHeight)) return false;
            desired.y = groundHeight;

            var projectionDistance = terrainMap != null && terrainMap.HasValidData
                ? Mathf.Max(.5f, Mathf.Max(terrainMap.CellSize.x, terrainMap.CellSize.y) * .5f)
                : Mathf.Max(.5f, spawnSpacing * .5f);
            if (!pathService.TrySamplePosition(desired, projectionDistance, NavMesh.AllAreas, out var position))
                return false;

            hit = new NavMeshHit { position = position };
            return true;
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

            formation = RecoilBattleFormation.Build(counts, radii);
            nextIndices = existingCounts;
        }

        private void CountTeamFormationUnits(List<Soldier> team, int[] counts, float[] radii)
        {
            foreach (var soldier in team)
            {
                if (!IsAlive(soldier)) continue;
                counts[soldier.CharacterType]++;
                radii[soldier.CharacterType] = Mathf.Max(
                    radii[soldier.CharacterType], soldier.Simulation.Settings.BodyRadius);
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

        private bool TryStartBattle()
        {
            if (_combatStarted) return true;
            UpdateAssembly();
            if (!_formationReady || _formationDirty || HasQueuedSpawns)
            {
                Debug.Log("Chưa thể bắt đầu trận: " + _assemblyStatus, this);
                return false;
            }
            _combatStarted = true;
            _lobbyStarting = false;
            _matchElapsedSeconds = 0f;
            return true;
        }

        private void UpdateAssembly()
        {
            if (_combatStarted) return;
            if (HasQueuedSpawns)
            {
                _formationReady = false;
                _assemblyStatus = "Đang sinh lính để vào vị trí";
                return;
            }
            if (_formationDirty)
            {
                _formationDirty = false;
                _formationReady = false;
                _formationBlocked = false;
                AssignFormation(_blueTeam, _redTeam);
                AssignFormation(_redTeam, _blueTeam);
                if (_formationBlocked)
                {
                    _assemblyStatus = "Không tìm thấy đủ vị trí đi được để xếp đội hình";
                    Debug.LogWarning(_assemblyStatus, this);
                    return;
                }
            }
            if (_formationBlocked || _formationReady || Time.unscaledTime < _nextFormationCheck) return;
            _nextFormationCheck = Time.unscaledTime + .2f;
            var remaining = CheckFormation(_blueTeam, true) + CheckFormation(_redTeam, false);
            _formationReady = remaining == 0 &&
                              HasAliveSoldier(_blueTeam) && HasAliveSoldier(_redTeam);
            if (_formationReady)
            {
                SetAssemblyMovement(_blueTeam, false);
                SetAssemblyMovement(_redTeam, false);
            }
            _assemblyStatus = _formationReady
                ? "Đội hình đã ổn định — bấm L để giao chiến"
                : remaining == 0
                    ? "Chưa có đủ quân — chọn loại lính để thêm"
                    : $"Đang tự xếp đội hình — còn {remaining} lính";
        }

        private void AssignFormation(List<Soldier> team, List<Soldier> opposingTeam)
        {
            var counts = new int[CharacterTypeCount];
            var opposingCounts = new int[CharacterTypeCount];
            var radii = new float[CharacterTypeCount];
            foreach (var soldier in team)
            {
                if (!IsAlive(soldier)) continue;
                counts[soldier.CharacterType]++;
                radii[soldier.CharacterType] = Mathf.Max(
                    radii[soldier.CharacterType], soldier.Simulation.Settings.BodyRadius);
            }
            foreach (var soldier in opposingTeam)
            {
                if (!IsAlive(soldier)) continue;
                opposingCounts[soldier.CharacterType]++;
                radii[soldier.CharacterType] = Mathf.Max(
                    radii[soldier.CharacterType], soldier.Simulation.Settings.BodyRadius);
            }
            for (var type = 0; type < CharacterTypeCount; type++)
                counts[type] = Mathf.Max(counts[type], opposingCounts[type]);

            var slots = RecoilBattleFormation.Build(counts, radii);
            var indices = new int[CharacterTypeCount];
            var axis = BattleAxis();
            var forward = axis * (team == _blueTeam ? 1f : -1f);
            var facing = Quaternion.LookRotation(forward);
            var midpoint = (GetSpawnTargetPosition(true) + GetSpawnTargetPosition(false)) * .5f;
            midpoint.y = 0f;
            var origin = midpoint - forward * (RecoilBattleFormation.ForwardDistance * 2f);
            foreach (var soldier in team)
            {
                if (!IsAlive(soldier)) continue;
                var type = soldier.CharacterType;
                var desired = RecoilBattleFormation.ToWorld(origin, forward, slots[type][indices[type]++]);
                soldier.FormationArrived = false;
                soldier.FormationAssigned = false;
                if (!TryGetGroundHeight(desired, out var height))
                {
                    _formationBlocked = true;
                    continue;
                }
                desired.y = height;
                var projectionDistance = terrainMap != null && terrainMap.HasValidData
                    ? Mathf.Max(.5f, Mathf.Max(terrainMap.CellSize.x, terrainMap.CellSize.y) * .5f)
                    : Mathf.Max(.5f, spawnSpacing * .5f);
                if (!pathService.TrySamplePosition(
                        desired, projectionDistance, NavMesh.AllAreas, out var ground) ||
                    HorizontalDistance(desired, ground) > projectionDistance)
                {
                    _formationBlocked = true;
                    continue;
                }

                var destination = ground + Vector3.up * soldier.Simulation.Settings.VerticalOffset;
                soldier.FormationDestination = destination;
                soldier.FormationAssigned = true;
                soldier.FormationArrived = true;
                soldier.Simulation.SetCombatApproach(false);
                soldier.Simulation.SetAssemblyMovement(false);
                soldier.Simulation.Stop();
                var pose = soldier.Simulation.Pose;
                pose.Position = destination;
                pose.Rotation = facing;
                pose.Speed = 0f;
                pose.Velocity = Vector3.zero;
                _entityManager.SetComponentData(soldier.Entity, pose);
            }
        }

        private int CheckFormation(List<Soldier> team, bool blue)
        {
            var remaining = 0;
            var facing = Quaternion.LookRotation(BattleAxis() * (blue ? 1f : -1f));
            foreach (var soldier in team)
            {
                if (!IsAlive(soldier)) continue;
                var simulation = soldier.Simulation;
                var tolerance = Mathf.Max(.25f, simulation.Settings.WaypointTolerance + .05f);
                if (!soldier.FormationAssigned ||
                    HorizontalDistance(simulation.Position, soldier.FormationDestination) > tolerance)
                {
                    if (!soldier.FormationAssigned)
                    {
                        remaining++;
                        continue;
                    }

                    simulation.Stop();
                    var snappedPose = simulation.Pose;
                    snappedPose.Position = soldier.FormationDestination;
                    snappedPose.Rotation = facing;
                    snappedPose.Speed = 0f;
                    snappedPose.Velocity = Vector3.zero;
                    _entityManager.SetComponentData(soldier.Entity, snappedPose);
                    soldier.FormationArrived = true;
                    continue;
                }

                if (soldier.FormationArrived) continue;
                simulation.StopMovement();
                var pose = simulation.Pose;
                pose.Rotation = facing;
                _entityManager.SetComponentData(soldier.Entity, pose);
                soldier.FormationArrived = true;
            }
            return remaining;
        }

        private bool HasAliveSoldier(List<Soldier> team)
        {
            foreach (var soldier in team)
                if (IsAlive(soldier)) return true;
            return false;
        }

        private static void SetAssemblyMovement(List<Soldier> team, bool assembling)
        {
            foreach (var soldier in team)
                soldier.Simulation?.SetAssemblyMovement(assembling);
        }

    }
}
