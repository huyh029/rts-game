using System.Collections.Generic;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using Unity.Profiling;

namespace RecoilPathfinding
{
    public struct RecoilVatState : IComponentData
    {
        public byte State;
        public int Version;
        public float Elapsed, AttackRemaining;
    }

    // One Burst pass per world/tick; views receive snapshots without individual ECS reads.
    public sealed class RecoilVatEcsBridge : MonoBehaviour
    {
        private struct Sample { public Entity Entity; public RecoilVatState State; }
        private sealed class Group
        {
            public EntityQuery Query;
            public NativeList<Sample> Samples;
            public readonly Dictionary<Entity, RecoilVatAnimation> Views = new();
        }
        private static RecoilVatEcsBridge _instance;
        private static readonly ProfilerMarker TickMarker = new("RecoilVAT.ScheduleAndWait");
        private static readonly ProfilerMarker ApplyMarker = new("RecoilVAT.ApplyChangedViews");
        private readonly Dictionary<World, Group> _groups = new();

        [BurstCompile]
        private struct TickJob : IJobChunk
        {
            public EntityTypeHandle Entities;
            public ComponentTypeHandle<RecoilVatState> Animations;
            [ReadOnly] public ComponentTypeHandle<RecoilUnitPose> Poses;
            [ReadOnly] public ComponentTypeHandle<RecoilBattleEcsState> Battles;
            public NativeList<Sample>.ParallelWriter Output;
            public float Delta;
            public void Execute(in ArchetypeChunk chunk, int index, bool useEnabledMask, in v128 enabledMask)
            {
                var entities = chunk.GetNativeArray(Entities);
                var animations = chunk.GetNativeArray(ref Animations);
                var poses = chunk.GetNativeArray(ref Poses);
                var battles = chunk.GetNativeArray(ref Battles);
                var iterator = new ChunkEntityEnumerator(useEnabledMask, enabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out var i))
                {
                    var animation = animations[i];
                    var dead = battles[i].Health <= 0 || battles[i].Active == 0;
                    var state = dead ? (byte)3 : animation.AttackRemaining > 0f ? (byte)2 :
                        poses[i].Speed > .05f ? (byte)1 : (byte)0;
                    var changed = animation.State != state;
                    if (changed) { animation.State = state; animation.Version++; animation.Elapsed = 0; }
                    else animation.Elapsed += Delta;
                    animation.AttackRemaining = Unity.Mathematics.math.max(0, animation.AttackRemaining - Delta);
                    animations[i] = animation;
                    // Idle/Run continue in the shader. Only transitions and timed attack/death
                    // work need to cross to managed views, rather than every unit every tick.
                    if (changed || state >= 2)
                        Output.AddNoResize(new Sample { Entity = entities[i], State = animation });
                }
            }
        }

        internal static void Register(World world, Entity entity, RecoilVatAnimation view)
        {
            if (_instance == null) _instance = new GameObject("VAT ECS Presentation").AddComponent<RecoilVatEcsBridge>();
            if (!_instance._groups.TryGetValue(world, out var group))
            {
                group = new Group { Query = world.EntityManager.CreateEntityQuery(typeof(RecoilVatState),
                    ComponentType.ReadOnly<RecoilUnitPose>(), ComponentType.ReadOnly<RecoilBattleEcsState>()),
                    Samples = new NativeList<Sample>(16, Allocator.Persistent) };
                _instance._groups.Add(world, group);
            }
            var manager = world.EntityManager;
            if (!manager.HasComponent<RecoilVatState>(entity)) manager.AddComponentData(entity, new RecoilVatState());
            else manager.SetComponentData(entity, new RecoilVatState());
            group.Views[entity] = view;
        }
        internal static void Unregister(World world, Entity entity)
        {
            if (_instance != null && world != null && _instance._groups.TryGetValue(world, out var group))
                group.Views.Remove(entity);
        }
        internal static void Attack(World world, Entity entity, float duration)
        {
            if (world == null || !world.IsCreated || !world.EntityManager.Exists(entity)) return;
            var manager = world.EntityManager;
            if (!manager.HasComponent<RecoilVatState>(entity)) return;
            var state = manager.GetComponentData<RecoilVatState>(entity);
            state.State = 2; state.Elapsed = 0; state.AttackRemaining = duration; state.Version++;
            manager.SetComponentData(entity, state);
        }
        internal static void TickAll(float delta)
        {
            if (_instance == null) return;
            foreach (var pair in _instance._groups)
            {
                if (!pair.Key.IsCreated || pair.Value.Views.Count == 0) continue;
                var group = pair.Value;
                var manager = pair.Key.EntityManager;
                group.Samples.Clear();
                group.Samples.Capacity = Mathf.Max(group.Samples.Capacity, group.Query.CalculateEntityCount());
                using (TickMarker.Auto())
                new TickJob { Entities = manager.GetEntityTypeHandle(), Animations = manager.GetComponentTypeHandle<RecoilVatState>(false),
                    Poses = manager.GetComponentTypeHandle<RecoilUnitPose>(true), Battles = manager.GetComponentTypeHandle<RecoilBattleEcsState>(true),
                    Delta = delta, Output = group.Samples.AsParallelWriter() }.ScheduleParallel(group.Query, default).Complete();
                using (ApplyMarker.Auto())
                for (var i = 0; i < group.Samples.Length; i++)
                {
                    var sample = group.Samples[i];
                    if (group.Views.TryGetValue(sample.Entity, out var view) && view != null)
                        view.ApplyEcsState(sample.State);
                }
            }
        }
#if UNITY_EDITOR
        public static void ValidateStateTransitions()
        {
            using var world = new World("VAT state transition validation");
            var manager = world.EntityManager;
            var entity = manager.CreateEntity(typeof(RecoilVatState), typeof(RecoilUnitPose), typeof(RecoilBattleEcsState));
            manager.SetComponentData(entity, new RecoilBattleEcsState { Health = 100, Active = 1 });
            using var query = manager.CreateEntityQuery(typeof(RecoilVatState), typeof(RecoilUnitPose), typeof(RecoilBattleEcsState));
            using var output = new NativeList<Sample>(1, Allocator.TempJob);
            void Tick(byte expected)
            {
                output.Clear();
                new TickJob { Entities = manager.GetEntityTypeHandle(), Animations = manager.GetComponentTypeHandle<RecoilVatState>(false),
                    Poses = manager.GetComponentTypeHandle<RecoilUnitPose>(true), Battles = manager.GetComponentTypeHandle<RecoilBattleEcsState>(true),
                    Delta = 1f / 30f, Output = output.AsParallelWriter() }.ScheduleParallel(query, default).Complete();
                if (manager.GetComponentData<RecoilVatState>(entity).State != expected ||
                    (output.Length > 0 && output[0].State.State != expected))
                    throw new System.InvalidOperationException("Unexpected VAT state, expected " + expected);
            }
            Tick(0);
            manager.SetComponentData(entity, new RecoilUnitPose { Speed = 2 }); Tick(1);
            Attack(world, entity, .05f); Tick(2); Tick(2); Tick(1);
            manager.SetComponentData(entity, new RecoilBattleEcsState { Health = 0, Active = 0 }); Tick(3);
            var version = manager.GetComponentData<RecoilVatState>(entity).Version;
            Tick(3);
            if (manager.GetComponentData<RecoilVatState>(entity).Version != version)
                throw new System.InvalidOperationException("Death animation restarted on the next tick.");
            Debug.Log("VAT ECS validation passed: Idle, Run, Attack, locomotion resume, Death and stable death version.");
        }
#endif
        private void OnDestroy()
        {
            foreach (var pair in _groups)
            {
                if (pair.Value.Samples.IsCreated) pair.Value.Samples.Dispose();
                if (pair.Key.IsCreated) pair.Value.Query.Dispose();
            }
            _groups.Clear(); if (_instance == this) _instance = null;
        }
    }
}
