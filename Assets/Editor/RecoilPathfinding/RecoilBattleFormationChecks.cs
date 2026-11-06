using System;
using System.Collections;
using System.Reflection;
using RecoilPathfinding;
using Unity.Entities;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RecoilPathfindingEditor
{
    public static class RecoilBattleFormationChecks
    {
        [MenuItem("Tools/Recoil Pathfinding/Validate Battle Formation")]
        public static void Run()
        {
            var counts = new[] { 30, 40, 30, 40, 30, 30, 20 };
            var radii = new[] { .3f, .5f, .6f, .7f, .8f, .4f, 1.2f };
            var slots = RecoilBattleFormation.Build(counts, radii, 20);
            var points = new System.Collections.Generic.List<(Vector2 point, float radius)>();
            for (var type = 0; type < counts.Length; type++)
            {
                if (slots[type].Length != counts[type]) throw new Exception("Formation lost units.");
                foreach (var slot in slots[type])
                {
                    points.Add((slot, radii[type]));
                    var blue = RecoilBattleFormation.ToWorld(Vector3.left * 120f, Vector3.right, slot);
                    var red = RecoilBattleFormation.ToWorld(Vector3.right * 120f, Vector3.left, slot);
                    if ((blue + red).sqrMagnitude > .00001f) throw new Exception("Teams are not mirrored.");
                }
            }
            for (var i = 0; i < points.Count; i++)
                for (var j = i + 1; j < points.Count; j++)
                    if (Vector2.Distance(points[i].point, points[j].point) < points[i].radius + points[j].radius + .049f)
                        throw new Exception("Body footprints overlap in formation.");
            Bounds BoundsFor(int type, int block, int blocks)
            {
                var bounds = new Bounds();
                var first = true;
                for (var i = block; i < slots[type].Length; i += blocks)
                {
                    var point = slots[type][i];
                    var item = new Bounds(new Vector3(point.x, point.y, 0f), new Vector3(radii[type] * 2f, radii[type] * 2f, 0f));
                    if (first) { bounds = item; first = false; } else bounds.Encapsulate(item);
                }
                return bounds;
            }
            var front = new[] { BoundsFor(3, 0, 2), BoundsFor(1, 0, 2), BoundsFor(6, 0, 1), BoundsFor(1, 1, 2), BoundsFor(3, 1, 2) };
            for (var i = 1; i < front.Length; i++)
                if (Mathf.Abs(front[i].min.x - front[i - 1].max.x - 1f) > .0001f)
                    throw new Exception("Front blocks must have exactly 1 world unit edge clearance.");
            var rear = float.PositiveInfinity;
            foreach (var block in front)
            {
                rear = Mathf.Min(rear, block.min.y);
                if (Mathf.Abs(block.max.y - 50f) > .0001f) throw new Exception("Front edge must be 50 world units ahead.");
            }
            foreach (var type in new[] { 5, 2, 0, 4 })
            {
                var bounds = BoundsFor(type, 0, 3);
                if (Mathf.Abs(rear - bounds.max.y - 1f) > .0001f) throw new Exception("Rows must have exactly 1 world unit edge clearance.");
                rear = bounds.min.y;
                for (var i = 1; i < 3; i++)
                    if (Mathf.Abs(BoundsFor(type, i, 3).min.x - BoundsFor(type, i - 1, 3).max.x - 1f) > .0001f)
                        throw new Exception("Rear blocks must have exactly 1 world unit edge clearance.");
            }
            // Partial blocks and missing types must retain every unit without invalid arithmetic.
            foreach (var count in new[] { 0, 1, 2, 7, 51 })
            {
                var sparse = new[] { count, 0, count, count, 0, 0, count };
                var layout = RecoilBattleFormation.Build(sparse, radii, 20);
                for (var type = 0; type < sparse.Length; type++)
                    foreach (var point in layout[type])
                        if (!float.IsFinite(point.x) || !float.IsFinite(point.y)) throw new Exception("Partial block has invalid coordinates.");
            }
            ValidateCombatGate();
            var smaller = RecoilBattleFormation.Build(new[] { 0, 0, 0, 0, 0, 0, 100 }, radii, 20)[6];
            var larger = RecoilBattleFormation.Build(new[] { 0, 0, 0, 0, 0, 0, 1000 }, radii, 20)[6];
            Bounds BlockBounds(Vector2[] positions)
            {
                var bounds = new Bounds(new Vector3(positions[0].x, positions[0].y, 0f), Vector3.zero);
                foreach (var point in positions) bounds.Encapsulate(new Vector3(point.x, point.y, 0f));
                return bounds;
            }
            if (BlockBounds(larger).size.x <= BlockBounds(smaller).size.x ||
                BlockBounds(larger).size.y > BlockBounds(smaller).size.y + .001f)
                throw new Exception("Growing a block must add width while respecting the configured maximum depth.");
            var depthLimited = RecoilBattleFormation.Build(new[] { 0, 0, 0, 0, 0, 0, 1000 }, radii, 20)[6];
            var depthRows = new System.Collections.Generic.HashSet<float>();
            foreach (var point in depthLimited) depthRows.Add(point.y);
            if (depthRows.Count != 20)
                throw new Exception($"Configured formation depth should be 20 rows, got {depthRows.Count}.");
            ValidateImmediateSpawnPlacement();
            Debug.Log("Battle formation passed: mirrored teams, 50m offset, 1m gaps, body clearance, sparse blocks and pre-battle combat gate.");
        }

        private static void ValidateCombatGate()
        {
            using var world = new World("Pre-battle formation gate checks");
            var scene = EditorSceneManager.NewPreviewScene();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            RecoilAutoBattleDemo battle = null;
            var type = typeof(RecoilAutoBattleDemo);
            void Set(string name, object value) => type.GetField(name, flags).SetValue(battle, value);
            object Get(string name) => type.GetField(name, flags).GetValue(battle);
            try
            {
                var root = new GameObject("Formation combat gate check");
                SceneManager.MoveGameObjectToScene(root, scene);
                battle = root.AddComponent<RecoilAutoBattleDemo>();
                Set("_world", world); Set("_entityManager", world.EntityManager); Set("_hasEntityManager", true);
                var soldierType = type.GetNestedType("Soldier", BindingFlags.NonPublic);
                var entities = new Entity[2];
                for (var i = 0; i < 2; i++)
                {
                    entities[i] = world.EntityManager.CreateEntity(typeof(RecoilBattleEcsState));
                    world.EntityManager.SetComponentData(entities[i], new RecoilBattleEcsState { Team = i, Active = 1, Health = 100 });
                    var soldier = Activator.CreateInstance(soldierType, true);
                    soldierType.GetField("Entity").SetValue(soldier, entities[i]);
                    ((IList)Get(i == 0 ? "_blueTeam" : "_redTeam")).Add(soldier);
                }
                var tick = type.GetMethod("SimulateFixedTick", flags);
                for (var i = 0; i < 30; i++) tick.Invoke(battle, new object[] { 1f / 30f, i / 30f });
                foreach (var entity in entities)
                    if (world.EntityManager.GetComponentData<RecoilBattleEcsState>(entity).Health != 100)
                        throw new Exception("Pre-battle tick applied damage.");
                Set("_blueSpawnQueue", 1);
                var start = type.GetMethod("TryStartBattle", flags);
                if ((bool)start.Invoke(battle, null) || (bool)Get("_combatStarted"))
                    throw new Exception("L must wait until queued soldiers have spawned.");
                Set("_blueSpawnQueue", 0);
                if (!(bool)start.Invoke(battle, null) || !(bool)Get("_combatStarted")) throw new Exception("Ready formation must start with L.");
                Set("_combatStarted", false);
                Set("_blueSpawnQueue", 1);
                if ((bool)start.Invoke(battle, null)) throw new Exception("Queued reinforcements must block early start.");
            }
            finally
            {
                if (battle != null) Set("_hasEntityManager", false);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static void ValidateImmediateSpawnPlacement()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var map = ScriptableObject.CreateInstance<RecoilTerrainMapData>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(RecoilAutoBattleDemo);
            RecoilAutoBattleDemo battle = null;
            void Set(string name, object value) => type.GetField(name, flags).SetValue(battle, value);
            object Get(string name) => type.GetField(name, flags).GetValue(battle);
            try
            {
                var cells = new byte[231]; Array.Fill(cells, (byte)1);
                map.SetBakedData(new Vector3(-200f, 0f, -100f), new Vector2(400f, 200f), 21, 11, 45f, new float[231], cells);
                var root = new GameObject("Immediate formation spawn check");
                SceneManager.MoveGameObjectToScene(root, scene);
                var service = root.AddComponent<RecoilNavMeshPathService>();
                var serviceType = service.GetType();
                serviceType.GetField("terrainMap", flags).SetValue(service, map);
                serviceType.GetField("useProjectTerrainMap", flags).SetValue(service, false);
                serviceType.GetMethod("InitializeGraph", flags).Invoke(service, null);
                battle = root.AddComponent<RecoilAutoBattleDemo>();
                Set("terrainMap", map); Set("pathService", service); Set("teamStartDistance", 120f);

                var blueQueue = (System.Collections.Generic.Queue<int>)Get("_blueSelectedSpawnQueue");
                var redQueue = (System.Collections.Generic.Queue<int>)Get("_redSelectedSpawnQueue");
                foreach (var characterType in new[] { 0, 1 })
                {
                    blueQueue.Enqueue(characterType);
                    redQueue.Enqueue(characterType);
                }
                var spawnMethod = type.GetMethod("TryFindInitialFormationSpawnPosition", flags);
                var blueFrontArgs = new object[] { true, (byte)1, null };
                var blueRearArgs = new object[] { true, (byte)0, null };
                var redFrontArgs = new object[] { false, (byte)1, null };
                var redRearArgs = new object[] { false, (byte)0, null };
                if (!(bool)spawnMethod.Invoke(battle, blueFrontArgs) ||
                    !(bool)spawnMethod.Invoke(battle, blueRearArgs) ||
                    !(bool)spawnMethod.Invoke(battle, redFrontArgs) ||
                    !(bool)spawnMethod.Invoke(battle, redRearArgs))
                    throw new Exception("Immediate formation positions could not be projected.");
                var blue = ((UnityEngine.AI.NavMeshHit)blueFrontArgs[2]).position;
                var blueRear = ((UnityEngine.AI.NavMeshHit)blueRearArgs[2]).position;
                var red = ((UnityEngine.AI.NavMeshHit)redFrontArgs[2]).position;
                var redRear = ((UnityEngine.AI.NavMeshHit)redRearArgs[2]).position;
                if (blue.x >= 0f || red.x <= 0f ||
                    Mathf.Abs(Mathf.Abs(blue.x) - 50f) > 20f ||
                    Mathf.Abs(Mathf.Abs(red.x) - 50f) > 20f ||
                    Mathf.Abs(blueRear.x) <= Mathf.Abs(blue.x) ||
                    Mathf.Abs(redRear.x) <= Mathf.Abs(red.x))
                    throw new Exception("Initial teams must spawn in mirrored rows about 50m from the midpoint.");

                blueQueue.Clear();
                redQueue.Clear();
                type.GetMethod("UpdateAssembly", flags).Invoke(battle, null);
                if ((bool)Get("_formationReady"))
                    throw new Exception("Combat must wait until both teams have spawned.");

                var previous = RecoilBattleFormation.Build(new[] { 0, 2, 0, 0, 0, 0, 0 }, new float[7], 20)[1];
                var expanded = RecoilBattleFormation.Build(new[] { 0, 3, 0, 0, 0, 0, 0 }, new float[7], 20)[1];
                if (previous.Length != 2 || expanded.Length != 3 ||
                    Vector2.Distance(previous[0], expanded[0]) < .1f)
                    throw new Exception("Adding soldiers must recalculate existing formation slots.");
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                UnityEngine.Object.DestroyImmediate(map);
            }
        }
    }
}
