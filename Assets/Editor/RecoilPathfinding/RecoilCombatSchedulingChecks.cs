using System;
using System.Collections;
using System.Reflection;
using RecoilPathfinding;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RecoilPathfindingEditor
{
    public static class RecoilCombatSchedulingChecks
    {
        [MenuItem("Tools/Recoil Pathfinding/Validate Combat Scheduling")]
        public static void Run()
        {
            using var world = new World("Battle snapshot scheduling checks");
            var scene = EditorSceneManager.NewPreviewScene();
            RecoilAutoBattleDemo battle = null;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var type = typeof(RecoilAutoBattleDemo);
            void Set(string name, object value) => type.GetField(name, flags).SetValue(battle, value);
            object Get(string name) => type.GetField(name, flags).GetValue(battle);
            try
            {
                var root = new GameObject("Isolated battle scheduling check");
                SceneManager.MoveGameObjectToScene(root, scene);
                battle = root.AddComponent<RecoilAutoBattleDemo>();
                var manager = world.EntityManager;
                Set("_world", world); Set("_entityManager", manager); Set("_hasEntityManager", true);
                Set("_clockDriven", true); Set("_tickDelta", 1f / 30f);
                var archetype = manager.CreateArchetype(typeof(RecoilBattleEcsState), typeof(RecoilUnitPose),
                    typeof(RecoilUnitMovementSettings), typeof(RecoilUnitNavigation), typeof(RecoilUnitOrder),
                    typeof(RecoilUnitCombatTiming), typeof(RecoilUnitWaypoint));
                var soldierType = type.GetNestedType("Soldier", BindingFlags.NonPublic);
                var units = new object[2];
                for (var i = 0; i < 2; i++)
                {
                    var entity = manager.CreateEntity(archetype);
                    manager.SetComponentData(entity, new RecoilBattleEcsState
                    {
                        Team = i, Active = 1, Health = 1000,
                        Stats = new RecoilUnitCombatStats { MaxHealth = 1000, Damage = 10, Range = 3f, AttackSpeed = 1f, AreaRadius = 3f }
                    });
                    manager.SetComponentData(entity, new RecoilUnitPose { Position = new float3(i * 2f, 0f, 0f) });
                    manager.SetComponentData(entity, new RecoilUnitNavigation { Active = 1 });
                    var soldier = Activator.CreateInstance(soldierType, true);
                    soldierType.GetField("Entity").SetValue(soldier, entity);
                    units[i] = soldier;
                    ((IList)Get(i == 0 ? "_blueTeam" : "_redTeam")).Add(soldier);
                }
                var run = type.GetMethod("RunCombatJob", flags);
                var scheduled = (IList)Get("_scheduledSoldiers");
                for (var tick = 0; tick < 30; tick++)
                {
                    scheduled.Clear();
                    // Cover both the aggregate-only path and snapshot edits for budgeted units.
                    if (tick % 3 != 0) { scheduled.Add(units[0]); scheduled.Add(units[1]); }
                    Set("_tickTime", tick / 30f);
                    run.Invoke(battle, null);
                    ((JobHandle)Get("_combatJobHandle")).Complete();
                    Set("_combatJobPending", false);
                }
                RecoilBattleCombatValidation.ValidateUnitStats();
                Debug.Log("Combat scheduling passed: 30 consecutive ticks, snapshot edits, AoE aggregation and empty/non-empty combat budgets.");
            }
            finally
            {
                if (battle != null)
                {
                    ((JobHandle)Get("_combatJobHandle")).Complete();
                    Set("_combatJobPending", false);
                    Set("_hasEntityManager", false);
                }
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
