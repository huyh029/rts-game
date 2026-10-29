using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using RecoilPathfinding;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace RecoilPathfindingEditor
{
    public static class RecoilUnitSimulationChecks
    {
        [MenuItem("Tools/Recoil Pathfinding/Validate Fixed Simulation Clock")]
        public static void ValidateFixedSimulationClock()
        {
            foreach (var fps in new[] { 30, 60, 120 })
            {
                var clock = new RecoilFixedTickAccumulator();
                var ticks = 0;
                for (var i = 0; i < fps * 10; i++) ticks += clock.Advance(1.0 / fps, 2);
                Require(ticks == 300 && Math.Abs(clock.Time - 10.0) < 0.00001 && clock.DroppedSeconds == 0,
                    "Simulation ticks must be independent of rendering at 30, 60 and 120 FPS.");
            }
            var overloaded = new RecoilFixedTickAccumulator();
            Require(overloaded.Advance(1.0, 2) == 2 && overloaded.DroppedSeconds > 0.9,
                "A slow frame must respect the catch-up budget and report dropped time.");
            var before = overloaded.Tick;
            Require(overloaded.Advance(0, 2) == 0 && overloaded.Advance(-1, 2) == 0 &&
                overloaded.Advance(double.NaN, 2) == 0 && overloaded.Tick == before,
                "Paused or invalid frame times must not advance simulation.");
            Debug.Log("Fixed simulation clock checks passed: 30/60/120 FPS equivalence, fixed step, bounded catch-up and paused time.");
        }
        [MenuItem("Tools/Recoil Pathfinding/Validate Presentation Sync")]
        public static void ValidatePresentationSync()
        {
            RecoilUnitPresentationSync.Validate();
            Debug.Log("Presentation sync checks passed: incremental view registration/removal/reuse without full map rebuilds, shared-entity views, chunk poses, scale and inactive/destroyed entities.");
        }
#if SKGAMES_ANIMATED_IMPOSTOR
        [MenuItem("Tools/Recoil Pathfinding/Validate Unit Animations")]
        public static void ValidateUnitAnimations()
        {
            RecoilUnitPresentationSync.ValidateAnimations();
            Debug.Log("Unit animation checks passed: child impostor discovery, idle/run/attack mapping from ECS and pooled view reuse.");
        }
#endif

        [MenuItem("Tools/Recoil Pathfinding/Validate Path Service Scheduling")]
        public static void ValidatePathServiceScheduling()
        {
            RecoilNavMeshPathService.ValidatePathServiceScheduling();
            Debug.Log("Path service scheduling checks passed: bounded completion, retained result ownership, cancelled request versions, pinned fields, native buffer reuse and cached portals.");
        }

        [MenuItem("Tools/Recoil Pathfinding/Validate Path Funnel")]
        public static void ValidatePathFunnel()
        {
            var portals = new List<RecoilPathPortal>();
            for (var i = 1; i <= 40; i++)
                portals.Add(new RecoilPathPortal(new Vector3(-2f, 0f, i), new Vector3(2f, 0f, i)));
            var path = RecoilPathFunnel.Build(Vector3.zero, Vector3.forward * 50f, portals);
            Require(path.Length == 2, "A straight corridor must not retain portal midpoints.");
            portals.Clear();
            portals.Add(new RecoilPathPortal(new Vector3(4f, 0f, 2f), new Vector3(4f, 0f, 0f)));
            portals.Add(new RecoilPathPortal(new Vector3(8f, 0f, 2f), new Vector3(10f, 0f, 2f)));
            path = RecoilPathFunnel.Build(new Vector3(0f, 0f, 1f), new Vector3(9f, 0f, 10f), portals);
            Require(path.Length == 3 && Vector3.Distance(path[1], new Vector3(8f, 0f, 2f)) < 0.001f,
                "An L corridor must retain the inner corner instead of cutting across the obstacle.");
            Debug.Log("Path funnel checks passed: straight corridor and required corner.");
        }

        [MenuItem("Tools/Recoil Pathfinding/Validate Body Contacts")]
        public static void ValidateBodyContacts()
        {
            RecoilNavMeshPathService.ValidateBodyContacts();
            Debug.Log("Body contact checks passed: idle separation, body radii, mass, push resistance and coincident positions.");
        }
        [MenuItem("Tools/Recoil Pathfinding/Validate Crowd Surface Kernel")]
        public static void ValidateCrowdSurfaceKernel()
        {
            RecoilNavMeshPathService.ValidateCrowdSurfaceKernel();
            Debug.Log("Crowd surface kernel checks passed: native request projection and boundary clipping, Burst unsteered recovery, unknown-surface fallback, result collection, movement roster cache, incremental grid/radius reduction, small boundary steps, corner lookahead, chunk prepare/apply, stale-result guards, slopes and portal/vertex crossing.");
        }

        [MenuItem("Tools/Recoil Pathfinding/Validate Combat Targeting")]
        public static void ValidateCombatTargeting()
        {
            RecoilBattleCombatValidation.Run();
            ValidateRosterCache();
            ValidateSceneTemplates();
            ValidateAdvanceWithoutTarget();
            Debug.Log("Combat targeting checks passed: ECS target/order/navigation resolve, strategic advance, contact stop, staged departures and pending paths, damage aggregation/chunk application/death events, bulk lifecycle capture, roster cache, contact damage and cooldown, close acquisition and incremental grid.");
        }

        private static void ValidateRosterCache()
        {
            using var world = new World("Recoil roster cache checks");
            var manager = world.EntityManager;
            var first = manager.CreateEntity(typeof(RecoilBattleEcsState));
            var second = manager.CreateEntity(typeof(RecoilBattleEcsState));
            manager.SetComponentData(first, new RecoilBattleEcsState { Active = 1, Health = 100 });
            manager.SetComponentData(second, new RecoilBattleEcsState { Active = 1, Health = 100 });
            var owner = new GameObject("Temporary roster cache check");
            owner.SetActive(false);
            var battle = owner.AddComponent<RecoilAutoBattleDemo>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(RecoilAutoBattleDemo);
            var soldierType = type.GetNestedType("Soldier", BindingFlags.NonPublic);
            var team = (IList)type.GetField("_blueTeam", flags).GetValue(battle);
            var indices = (IDictionary)type.GetField("_combatIndicesByEntity", flags).GetValue(battle);
            void Add(Entity entity)
            {
                var soldier = Activator.CreateInstance(soldierType, true);
                soldierType.GetField("Entity").SetValue(soldier, entity);
                team.Add(soldier);
                type.GetField("_combatRosterDirty", flags).SetValue(battle, true);
            }
            void Refresh() => type.GetMethod("RefreshCombatRoster", flags).Invoke(battle, null);
            try
            {
                type.GetField("_world", flags).SetValue(battle, world);
                type.GetField("_entityManager", flags).SetValue(battle, manager);
                Add(first); Refresh();
                var builds = battle.CombatRosterBuildCount;
                Refresh();
                Require(battle.CombatRosterBuildCount == builds && indices.Count == 1,
                    "An unchanged army must reuse its entity-index mapping.");
                Add(second); Refresh();
                Require(indices.Count == 2 && battle.CombatRosterBuildCount == builds + 1,
                    "Spawning must invalidate and rebuild the roster.");
                manager.SetComponentData(first, new RecoilBattleEcsState());
                type.GetMethod("RemoveDestroyedSoldiers", flags).Invoke(battle, new object[] { team });
                Refresh();
                Require(indices.Count == 1 && !indices.Contains(first) && indices.Contains(second),
                    "Death compaction must remove old mappings without losing surviving units.");
                type.GetMethod("DisposeCombatBuffers", flags).Invoke(battle, null);
                Refresh();
                Require(indices.Contains(second), "Recreating native buffers must rebuild cached native mappings.");
                var remaining = team[0];
                soldierType.GetField("Retired").SetValue(remaining, true);
                type.GetField("_hasRetirements", flags).SetValue(battle, true);
                type.GetMethod("CompactRetiredSoldiers", flags).Invoke(battle, null);
                Refresh();
                Require(team.Count == 0 && indices.Count == 0,
                    "End-of-tick retirement must remove only explicitly retired soldiers without another ECS life scan.");
                manager.SetComponentData(first, new RecoilBattleEcsState { Active = 1, Health = 100 });
                Add(first); Refresh();
                type.GetMethod("CompactRetiredSoldiers", flags).Invoke(battle, null);
                Require(team.Count == 1 && indices.Contains(first),
                    "Cleared retirement events must not remove a later reused entity.");
            }
            finally
            {
                type.GetMethod("DisposeCombatBuffers", flags).Invoke(battle, null);
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private static void ValidateSceneTemplates()
        {
            var owner = new GameObject("Temporary template membership check");
            owner.SetActive(false);
            var blue = new GameObject("Blue source template");
            var red = new GameObject("Red source template");
            GameObject clone = null;
            try
            {
                blue.AddComponent<RecoilNavMeshPathAgent>();
                red.AddComponent<RecoilNavMeshPathAgent>();
                var battle = owner.AddComponent<RecoilAutoBattleDemo>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(RecoilAutoBattleDemo);
                type.GetField("blueUnitTemplate", flags).SetValue(battle, blue);
                type.GetField("redUnitTemplate", flags).SetValue(battle, red);
                type.GetMethod("Awake", flags).Invoke(battle, null);
                Require(!blue.activeSelf && !red.activeSelf,
                    "Scene templates must be inactive so they cannot become unregistered combatants.");
                clone = UnityEngine.Object.Instantiate(blue);
                Require(clone.GetComponent<RecoilNavMeshPathAgent>() != null,
                    "Hiding a template must preserve components for spawned clones.");
            }
            finally
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                UnityEngine.Object.DestroyImmediate(blue);
                UnityEngine.Object.DestroyImmediate(red);
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private static void ValidateAdvanceWithoutTarget()
        {
            using var world = new World("Recoil advance regression check");
            var manager = world.EntityManager;
            var own = manager.CreateEntity(typeof(RecoilBattleEcsState));
            var enemy = manager.CreateEntity(typeof(RecoilBattleEcsState));
            manager.SetComponentData(own, new RecoilBattleEcsState { Team = 0, Health = 100, Active = 1 });
            manager.SetComponentData(enemy, new RecoilBattleEcsState { Team = 1, Health = 100, Active = 1 });
            var ownSimulation = new RecoilUnitSimulation(world, own, null, Vector3.zero,
                Quaternion.identity, default);
            var enemySimulation = new RecoilUnitSimulation(world, enemy, null, Vector3.right * 100f,
                Quaternion.identity, default);
            var owner = new GameObject("Temporary advance regression check");
            owner.SetActive(false);
            var battle = owner.AddComponent<RecoilAutoBattleDemo>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(RecoilAutoBattleDemo);
            var soldierType = type.GetNestedType("Soldier", BindingFlags.NonPublic);
            object MakeSoldier(Entity entity, RecoilUnitSimulation simulation)
            {
                var soldier = Activator.CreateInstance(soldierType, true);
                soldierType.GetField("Entity").SetValue(soldier, entity);
                soldierType.GetField("Simulation").SetValue(soldier, simulation);
                return soldier;
            }
            try
            {
                type.GetField("_world", flags).SetValue(battle, world);
                type.GetField("_entityManager", flags).SetValue(battle, manager);
                var redSpawn = new GameObject("Red Spawn Target").transform;
                redSpawn.SetParent(owner.transform);
                redSpawn.position = Vector3.right * 80f;
                type.GetField("redSpawnTarget", flags).SetValue(battle, redSpawn);
                var ownSoldier = MakeSoldier(own, ownSimulation);
                type.GetMethod("UpdateNavigationBridge", flags).Invoke(battle, new[] { ownSoldier, null });
                Require(manager.GetComponentData<RecoilUnitOrder>(own).Kind == RecoilUnitOrderKind.Stop && !ownSimulation.HasPath,
                    "Waiting soldiers must not start marching before their departure is released.");
                soldierType.GetField("DepartureReleased").SetValue(ownSoldier, true);
                ((IList)type.GetField("_redTeam", flags).GetValue(battle)).Add(MakeSoldier(enemy, enemySimulation));
                type.GetMethod("UpdateNavigationBridge", flags).Invoke(battle, new[] { ownSoldier, null });
                var order = manager.GetComponentData<RecoilUnitOrder>(own);
                Require(order.Kind == RecoilUnitOrderKind.Move &&
                    math.distance(order.Destination, (float3)redSpawn.position) < 25f && order.Destination.x > 0f,
                    "A unit must advance to its stopping slot around the opposing spawn target.");
                var stoppingSlot = order.Destination;
                ((IList)type.GetField("_redTeam", flags).GetValue(battle)).Clear();
                type.GetMethod("UpdateNavigationBridge", flags).Invoke(battle, new[] { ownSoldier, null });
                order = manager.GetComponentData<RecoilUnitOrder>(own);
                Require(order.Kind == RecoilUnitOrderKind.Move &&
                    math.distance(order.Destination, stoppingSlot) < 0.01f,
                    "An empty opposing army must not cancel movement to its spawn target.");
                var redSoldier = MakeSoldier(enemy, enemySimulation);
                soldierType.GetField("DepartureReleased").SetValue(redSoldier, true);
                type.GetMethod("UpdateNavigationBridge", flags).Invoke(battle, new[] { redSoldier, null });
                Require(manager.GetComponentData<RecoilUnitOrder>(enemy).Destination.x < 0f,
                    "Red units must advance to the blue spawn target.");
                var retired = MakeSoldier(own, ownSimulation);
                soldierType.GetField("Retired").SetValue(retired, true);
                soldierType.GetField("DepartureReleased").SetValue(ownSoldier, false);
                soldierType.GetField("DepartureReleased").SetValue(redSoldier, false);
                var queue = type.GetField("_blueDepartures", flags).GetValue(battle);
                var enqueue = queue.GetType().GetMethod("Enqueue");
                enqueue.Invoke(queue, new[] { retired });
                enqueue.Invoke(queue, new[] { ownSoldier });
                enqueue.Invoke(queue, new[] { redSoldier });
                type.GetField("departuresPerTeamPerTick", flags).SetValue(battle, 1);
                type.GetMethod("ReleaseDepartures", flags).Invoke(battle, new[] { queue });
                Require((bool)soldierType.GetField("DepartureReleased").GetValue(ownSoldier) &&
                    !(bool)soldierType.GetField("DepartureReleased").GetValue(redSoldier) &&
                    !(bool)soldierType.GetField("DepartureReleased").GetValue(retired),
                    "Departure budget must release one live soldier and skip retired pooled references.");
                type.GetMethod("ReleaseDepartures", flags).Invoke(battle, new[] { queue });
                Require((bool)soldierType.GetField("DepartureReleased").GetValue(redSoldier),
                    "Later ticks must release the remaining queued soldiers.");
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); }
        }

        [MenuItem("Tools/Recoil Pathfinding/Enable Leak Stack Traces")]
        public static void EnableLeakStackTraces()
        {
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
            Debug.Log("Native leak stack traces enabled. Reproduce the reload or Play Mode transition; existing allocations may not have stack traces.");
        }

        [MenuItem("Tools/Recoil Pathfinding/Validate ECS Separation")]
        public static void Run()
        {
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
            RunChecks();
            // RunChecks returns only after its using declaration has disposed the test World.
            Debug.Log("Recoil ECS separation checks passed: pose, path, commands, detached view, stop, pooling and destruction. Test World disposed.");
        }

        private static void RunChecks()
        {
            using var world = new World("Recoil ECS separation checks");
            var manager = world.EntityManager;
            var entity = manager.CreateEntity();
            var settings = new RecoilUnitMovementSettings
            {
                MaxSpeed = 3.5f, Acceleration = 12f, TurnSpeedDegrees = 540f,
                WaypointTolerance = 0.25f, AvoidanceRadius = 1.2f, VerticalOffset = 1f, AreaMask = -1
            };
            // No view, Terrain or NavMesh is needed to own and read simulation state.
            var unit = new RecoilUnitSimulation(world, entity, null, Vector3.zero, Quaternion.identity, settings);
            var pose = unit.Pose;
            pose.Position = new float3(10f, 1f, 2f);
            pose.Velocity = new float3(2f, 0f, 0f);
            pose.Speed = 2f;
            manager.SetComponentData(entity, pose);
            Require(unit.Position == new Vector3(10f, 1f, 2f), "Position must come from ECS.");
            var path = manager.GetBuffer<RecoilUnitWaypoint>(entity);
            path.Add(new RecoilUnitWaypoint { Position = pose.Position });
            path.Add(new RecoilUnitWaypoint { Position = new float3(20f, 1f, 2f) });
            Require(unit.PrepareMovement() && unit.CurrentWaypoint == new Vector3(20f, 1f, 2f),
                "Waypoint progress must use ECS pose and buffer.");
            unit.SetDestination(new Vector3(20f, 1f, 2f));
            Require(manager.GetComponentData<RecoilUnitOrder>(entity).Kind == RecoilUnitOrderKind.Move,
                "Move command must be stored in ECS.");

            var view = new GameObject("Temporary ECS separation check");
            try
            {
                var agent = view.AddComponent<RecoilNavMeshPathAgent>();
                agent.Bind(unit);
                view.SetActive(false);
                Require(unit.IsActive, "Disabling a battle view must not deactivate its entity.");
                view.transform.position = Vector3.one * 999f;
                Require(unit.Position == new Vector3(10f, 1f, 2f), "View movement must not change simulation.");
            }
            finally { UnityEngine.Object.DestroyImmediate(view); }
            Require(unit.IsActive, "Destroying a battle view must not destroy its entity.");

            unit.Stop();
            Require(!unit.HasPath && !unit.IsPathPending && unit.CurrentSpeed == 0f &&
                math.lengthsq(unit.Pose.Velocity) == 0f, "Stop must clear navigation and velocity.");
            Require(manager.GetComponentData<RecoilUnitOrder>(entity).Kind == RecoilUnitOrderKind.Stop,
                "Stop command must be stored in ECS.");

            var staleVersion = unit.MovementVersion;
            unit.SetActive(false);
            var reused = new RecoilUnitSimulation(world, entity, null, Vector3.one, Quaternion.identity, settings);
            unit.ApplyMovement(Vector3.one * 100f, Vector3.zero, Vector3.zero, 10f,
                Quaternion.identity, staleVersion, 0.1f);
            Require(reused.Position == Vector3.one && reused.CurrentSpeed == 0f,
                "A stale movement result must not overwrite a pooled entity.");
            manager.DestroyEntity(entity);
            Require(!reused.Exists && !reused.IsActive, "Destroyed entities must invalidate simulation handles.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        [MenuItem("Tools/Recoil Pathfinding/Validate Slope Movement")]
        public static void ValidateSlopeMovement()
        {
            Require(NavMesh.GetSettingsCount() > 0, "A NavMesh agent type is required.");
            var mesh = new Mesh();
            NavMeshData data = null;
            var instance = default(NavMeshDataInstance);
            try
            {
                mesh.vertices = new[] { new Vector3(-10f, -2.5f, -10f), new Vector3(-10f, -2.5f, 10f),
                    new Vector3(10f, 2.5f, 10f), new Vector3(10f, 2.5f, -10f) };
                mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                mesh.RecalculateBounds();
                var origin = new Vector3(10000f, 0f, 10000f);
                var sources = new List<NavMeshBuildSource> { new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Mesh, sourceObject = mesh,
                    transform = Matrix4x4.Translate(origin), area = 0
                } };
                data = NavMeshBuilder.BuildNavMeshData(NavMesh.GetSettingsByIndex(0), sources,
                    new Bounds(origin, Vector3.one * 40f), Vector3.zero, Quaternion.identity);
                Require(data != null, "Could not build the temporary slope NavMesh.");
                instance = NavMesh.AddNavMeshData(data);
                using var world = new World("Recoil slope movement check");
                var entity = world.EntityManager.CreateEntity();
                Require(NavMesh.SamplePosition(origin + Vector3.left * 4f, out var start, 5f, -1),
                    "Could not locate the temporary slope.");
                var unit = new RecoilUnitSimulation(world, entity, null, start.position + Vector3.up,
                    Quaternion.identity, new RecoilUnitMovementSettings { VerticalOffset = 1f, AreaMask = -1 });
                for (var i = 0; i < 8; i++)
                {
                    unit.ApplyMovement(unit.Position + Vector3.right, Vector3.right, Vector3.zero,
                        1f, Quaternion.identity, unit.MovementVersion, 1f);
                    Require(NavMesh.SamplePosition(unit.Position - Vector3.up, out var ground, 0.1f, -1) &&
                        Vector3.Distance(unit.Position - Vector3.up, ground.position) < 0.1f,
                        "Movement must follow the slope's surface height.");
                }
                Require(unit.Position.x > start.position.x + 6f, "The unit must advance up the slope.");
                var before = unit.Position;
                unit.ApplyMovement(before + Vector3.right * 100f, Vector3.right * 100f, Vector3.zero,
                    1f, Quaternion.identity, unit.MovementVersion, 1f);
                Require(unit.Position == before && unit.CurrentSpeed == 0f,
                    "An unprojectable movement must leave the unit at its previous position.");
            }
            finally
            {
                if (instance.valid) instance.Remove();
                if (data != null) UnityEngine.Object.DestroyImmediate(data);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
            Debug.Log("Slope movement checks passed: surface height tracking and invalid-step rejection.");
        }

        [MenuItem("Tools/Recoil Pathfinding/Validate Native Cleanup")]
        public static void ValidateNativeCleanup()
        {
            var owner = new GameObject("Temporary native cleanup check");
            owner.SetActive(false);
            var service = owner.AddComponent<RecoilNavMeshPathService>();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(RecoilNavMeshPathService);
            void Invoke(string method, params object[] args) => type.GetMethod(method, flags).Invoke(service, args);
            try
            {
                Invoke("EnsureCrowdJobCapacity", 32);
                Invoke("EnsureProjectionCapacity", 32);
                var fields = (IList)type.GetField("_fieldOrder", flags).GetValue(service);
                var fieldType = type.GetNestedType("FlowField", BindingFlags.NonPublic);
                var keyType = type.GetNestedType("FieldKey", BindingFlags.NonPublic);
                for (var i = 0; i < 16; i++)
                    fields.Add(Activator.CreateInstance(fieldType, Activator.CreateInstance(keyType), 8, 0f));

                // Exercise the actual reload/disable path, followed by destruction and another cleanup.
                Invoke("OnDisable");
                Require(fields.Count == 0, "Cached flow fields must be released on disable.");
                foreach (var name in new[] { "_crowdMovementInputs", "_crowdMovementOutputs",
                    "_crowdAgentsByCell", "_projectionInputs", "_projectionResults" })
                {
                    var container = type.GetField(name, flags).GetValue(service);
                    Require(!(bool)container.GetType().GetProperty("IsCreated").GetValue(container),
                        name + " must be disposed and reset.");
                }
                Invoke("OnDestroy");
                Invoke("OnDisable");
                // Capacity counters must reset so a resumed service allocates valid buffers again.
                Invoke("EnsureCrowdJobCapacity", 32);
                Invoke("EnsureProjectionCapacity", 32);
            }
            finally
            {
                try { Invoke("DisposeNativeResources"); }
                finally { UnityEngine.Object.DestroyImmediate(owner); }
            }
            Debug.Log("Native cleanup checks passed: 16 flow fields, crowd/projection buffers, repeated disposal and reallocation.");
        }
    }
}
