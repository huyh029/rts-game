using System.Collections.Generic;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;
// Animated impostor integration stays opt-in while its source plugin is unavailable.
#if SKGAMES_ANIMATED_IMPOSTOR
using SKGames.AnimatedImpostor;
#endif

namespace RecoilPathfinding
{
    [DefaultExecutionOrder(-25)]
    public sealed class RecoilUnitPresentationSync : MonoBehaviour
    {
        private static RecoilUnitPresentationSync _instance;
        public static float LastSyncMilliseconds { get; private set; }
        private readonly Dictionary<World, Group> _groups = new();
        private readonly Dictionary<RecoilNavMeshPathAgent, Group> _registrations = new();

        internal static void Register(RecoilNavMeshPathAgent view)
        {
            if (!Application.isPlaying || !view.isActiveAndEnabled || view.Simulation == null || !view.Simulation.Exists) return;
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<RecoilUnitPresentationSync>();
                if (_instance == null)
                    _instance = new GameObject("Recoil Unit Presentation Sync").AddComponent<RecoilUnitPresentationSync>();
            }
            Unregister(view);
            var world = view.Simulation.SimulationWorld;
            if (!_instance._groups.TryGetValue(world, out var group))
            {
                group = new Group(world);
                _instance._groups.Add(world, group);
            }
            group.Add(view, view.Simulation.Entity);
            _instance._registrations.Add(view, group);
        }

        internal static void Unregister(RecoilNavMeshPathAgent view)
        {
            if (_instance == null || !_instance._registrations.TryGetValue(view, out var group)) return;
            group.Remove(view);
            _instance._registrations.Remove(view);
        }

        private void LateUpdate()
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var group in _groups.Values) group.Sync();
            LastSyncMilliseconds = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - start) *
                (1000.0 / System.Diagnostics.Stopwatch.Frequency));
        }

        private void OnDisable()
        {
            foreach (var group in _groups.Values) group.Dispose();
            _groups.Clear();
            _registrations.Clear();
            if (_instance == this) _instance = null;
        }

        public static void Validate()
        {
            using var world = new World("Recoil presentation sync checks");
            var manager = world.EntityManager;
            var archetype = manager.CreateArchetype(typeof(RecoilUnitPose), typeof(RecoilUnitNavigation));
            var first = manager.CreateEntity(archetype);
            var second = manager.CreateEntity(archetype);
            manager.SetComponentData(first, new RecoilUnitNavigation { Active = 1 });
            manager.SetComponentData(second, new RecoilUnitNavigation { Active = 1 });
            manager.SetComponentData(first, new RecoilUnitPose { Position = new Vector3(10f, 1f, 0f), Rotation = Quaternion.identity });
            manager.SetComponentData(second, new RecoilUnitPose { Position = new Vector3(20f, 1f, 0f), Rotation = Quaternion.identity });
            var firstObject = new GameObject("Temporary presentation view A");
            var secondObject = new GameObject("Temporary presentation view B");
            var group = new Group(world);
            try
            {
                var a = firstObject.AddComponent<RecoilNavMeshPathAgent>();
                var b = secondObject.AddComponent<RecoilNavMeshPathAgent>();
                secondObject.transform.localScale = Vector3.one * 2f;
                group.Add(a, first); group.Add(b, second);
                group.Sync();
                Require(firstObject.transform.position.x == 10f && secondObject.transform.position.x == 20f,
                    "Grouped presentation must copy ECS poses.");
                var fullBuilds = group.FullMapBuilds;
                Require(secondObject.transform.localScale == Vector3.one * 2f, "Presentation must preserve model scale.");
                group.Remove(a); // Exercises TransformAccessArray swap-back and slot remapping.
                manager.SetComponentData(second, new RecoilUnitPose { Position = new Vector3(30f, 1f, 0f), Rotation = Quaternion.identity });
                group.Sync();
                Require(secondObject.transform.position.x == 30f, "Removing a view must preserve the swapped view's entity mapping.");
                Require(group.FullMapBuilds == fullBuilds, "Removing a view must not rebuild the whole presentation map.");
                group.Add(a, second);
                group.Sync();
                Require(firstObject.transform.position.x == 30f && group.FullMapBuilds == fullBuilds,
                    "Incremental registration must support multiple views of an entity without a full rebuild.");
                group.Remove(a);
                manager.SetComponentData(second, new RecoilUnitNavigation());
                manager.SetComponentData(second, new RecoilUnitPose { Position = new Vector3(99f, 1f, 0f), Rotation = Quaternion.identity });
                group.Sync();
                Require(secondObject.transform.position.x == 30f, "Inactive entities must not update views.");
                manager.DestroyEntity(second);
                group.Sync();
                Require(secondObject.transform.position.x == 30f, "Destroyed entities must not publish cached poses.");
                group.Remove(b);
                var reused = manager.CreateEntity(archetype);
                manager.SetComponentData(reused, new RecoilUnitNavigation { Active = 1 });
                manager.SetComponentData(reused, new RecoilUnitPose { Position = new Vector3(40f, 1f, 0f), Rotation = Quaternion.identity });
                group.Add(b, reused);
                group.Sync();
                Require(secondObject.transform.position.x == 40f, "Reused views must follow their newly bound entity.");
                Require(group.FullMapBuilds == fullBuilds, "View reuse within capacity must update only its presentation slot.");
            }
            finally
            {
                group.Dispose();
                DestroyImmediate(firstObject);
                DestroyImmediate(secondObject);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new System.InvalidOperationException(message);
        }

#if SKGAMES_ANIMATED_IMPOSTOR
        public static void ValidateAnimations()
        {
            using var world = new World("Recoil impostor animation checks");
            var manager = world.EntityManager;
            var entity = manager.CreateEntity(typeof(RecoilUnitPose), typeof(RecoilUnitNavigation), typeof(RecoilBattleEcsState));
            manager.SetComponentData(entity, new RecoilUnitNavigation { Active = 1 });
            manager.SetComponentData(entity, new RecoilBattleEcsState { Active = 1, Health = 100 });
            var root = new GameObject("Temporary animated soldier");
            var library = ScriptableObject.CreateInstance<AnimatedImpostorLibrary>();
            library.Clips = new[]
            {
                new AnimatedImpostorLibrary.Clip { Name = "BO BINH|idle", Frames = 12, Duration = 1f, Loop = true },
                new AnimatedImpostorLibrary.Clip { Name = "BO BINH|RUN", Frames = 12, Duration = 1f, Loop = true },
                new AnimatedImpostorLibrary.Clip { Name = "BO BINH|ATTACK", Frames = 12, Duration = 1f, Loop = true }
            };
            var group = new Group(world);
            try
            {
                var view = root.AddComponent<RecoilNavMeshPathAgent>();
                var model = new GameObject("Impostor child");
                model.transform.SetParent(root.transform, false);
                var player = model.AddComponent<AnimatedImpostorPlayer>();
                player.Library = library;
                group.Add(view, entity);
                manager.SetComponentData(entity, new RecoilUnitPose { Rotation = Quaternion.identity, Velocity = Vector3.right });
                group.Sync();
                Require(player.ClipIndex == 1, "Moving entities must select RUN on child impostors.");
                manager.SetComponentData(entity, new RecoilBattleEcsState { Active = 1, Health = 100, Target = entity, AttackCooldown = 0.5f });
                group.Sync();
                Require(player.ClipIndex == 2, "Contact attacks must select ATTACK.");
                manager.SetComponentData(entity, new RecoilBattleEcsState { Active = 1, Health = 100 });
                manager.SetComponentData(entity, new RecoilUnitPose { Rotation = Quaternion.identity });
                group.Sync();
                Require(player.ClipIndex == 0, "Stopped entities must return to idle.");
                group.Remove(view);
                manager.SetComponentData(entity, new RecoilUnitPose { Rotation = Quaternion.identity, Velocity = Vector3.forward });
                group.Add(view, entity);
                group.Sync();
                Require(player.ClipIndex == 1, "Reused views must receive their current animation state.");
            }
            finally { group.Dispose(); DestroyImmediate(root); DestroyImmediate(library); }
        }
#endif

        [BurstCompile]
        private struct ResetValidityJob : IJobParallelFor
        {
            public NativeArray<byte> Valid;
            public void Execute(int index) => Valid[index] = 0;
        }

#if SKGAMES_ANIMATED_IMPOSTOR
        private struct AnimationChange
        {
            public int Slot;
            public byte State;
        }
#endif

        [BurstCompile]
        private struct CapturePosesJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle Entities;
            [ReadOnly] public ComponentTypeHandle<RecoilUnitPose> Poses;
            [ReadOnly] public ComponentTypeHandle<RecoilUnitNavigation> Navigations;
#if SKGAMES_ANIMATED_IMPOSTOR
            [ReadOnly] public ComponentTypeHandle<RecoilBattleEcsState> Combat;
            [ReadOnly] public NativeArray<byte> AnimationEnabled;
            [NativeDisableParallelForRestriction] public NativeArray<byte> AnimationStates;
            public NativeQueue<AnimationChange>.ParallelWriter AnimationChanges;
#endif
            [ReadOnly] public NativeParallelMultiHashMap<Entity, int> Slots;
            [NativeDisableParallelForRestriction] public NativeArray<RecoilUnitPose> Output;
            [NativeDisableParallelForRestriction] public NativeArray<byte> Valid;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetNativeArray(Entities);
                var poses = chunk.GetNativeArray(ref Poses);
                var navigations = chunk.GetNativeArray(ref Navigations);
#if SKGAMES_ANIMATED_IMPOSTOR
                var hasCombat = chunk.Has(ref Combat);
                var combat = hasCombat ? chunk.GetNativeArray(ref Combat) : default;
#endif
                var enumerator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (enumerator.NextEntityIndex(out var i))
                {
                    if (navigations[i].Active == 0 || !Slots.TryGetFirstValue(entities[i], out var slot, out var cursor)) continue;
#if SKGAMES_ANIMATED_IMPOSTOR
                    var animation = (byte)(Unity.Mathematics.math.lengthsq(poses[i].Velocity.xz) > 0.01f
                        ? AnimatedImpostorUnitState.Move : AnimatedImpostorUnitState.Idle);
                    if (hasCombat && combat[i].Active != 0 && combat[i].Health > 0 &&
                        combat[i].Target != Entity.Null && combat[i].AttackCooldown > 0f && navigations[i].HasDestination == 0)
                        animation = (byte)AnimatedImpostorUnitState.Attack;
#endif
                    do
                    {
                        Output[slot] = poses[i]; Valid[slot] = 1;
#if SKGAMES_ANIMATED_IMPOSTOR
                        if (AnimationEnabled[slot] != 0 && AnimationStates[slot] != animation)
                        {
                            AnimationStates[slot] = animation;
                            AnimationChanges.Enqueue(new AnimationChange { Slot = slot, State = animation });
                        }
#endif
                    }
                    while (Slots.TryGetNextValue(out slot, ref cursor));
                }
            }
        }

        [BurstCompile]
        private struct ApplyTransformsJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<RecoilUnitPose> Poses;
            [ReadOnly] public NativeArray<byte> Valid;
            public void Execute(int index, TransformAccess transform)
            {
                if (Valid[index] == 0) return;
                var pose = Poses[index];
                var position = (Vector3)pose.Position;
                var rotation = (Quaternion)pose.Rotation;
                if (transform.position == position && transform.rotation == rotation) return;
                transform.SetPositionAndRotation(position, rotation);
            }
        }

        private sealed class Group
        {
            private readonly World _world;
            private readonly EntityQuery _query;
            private readonly List<RecoilNavMeshPathAgent> _views = new();
            private readonly List<Entity> _entities = new();
#if SKGAMES_ANIMATED_IMPOSTOR
            private readonly List<AnimatedImpostorPlayer[]> _animationPlayers = new();
#endif
            private readonly Dictionary<RecoilNavMeshPathAgent, int> _indices = new();
            private TransformAccessArray _transforms;
            private NativeParallelMultiHashMap<Entity, int> _slots;
            private NativeArray<RecoilUnitPose> _poses;
            private NativeArray<byte> _valid;
#if SKGAMES_ANIMATED_IMPOSTOR
            private NativeArray<byte> _animationEnabled;
            private NativeArray<byte> _animationStates;
            private NativeQueue<AnimationChange> _animationChanges;
#endif
            private bool _dirty = true;
            public int FullMapBuilds { get; private set; }

            public Group(World world)
            {
                _world = world;
                _query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<RecoilUnitPose>(),
                    ComponentType.ReadOnly<RecoilUnitNavigation>());
                _transforms = new TransformAccessArray(16);
#if SKGAMES_ANIMATED_IMPOSTOR
                _animationChanges = new NativeQueue<AnimationChange>(Allocator.Persistent);
#endif
            }

            public void Add(RecoilNavMeshPathAgent view, Entity entity)
            {
                _indices.Add(view, _views.Count);
                _views.Add(view); _entities.Add(entity); _transforms.Add(view.transform);
#if SKGAMES_ANIMATED_IMPOSTOR
                var players = view.GetComponentsInChildren<AnimatedImpostorPlayer>(true);
                _animationPlayers.Add(players);
                if (_animationStates.IsCreated && _views.Count <= _animationStates.Length)
                {
                    _animationStates[_views.Count - 1] = byte.MaxValue;
                    _animationEnabled[_views.Count - 1] = (byte)(players.Length > 0 ? 1 : 0);
                }
#endif
                if (!_dirty && _slots.IsCreated && _views.Count <= _slots.Capacity)
                    _slots.Add(entity, _views.Count - 1);
                else _dirty = true;
            }

            public void Remove(RecoilNavMeshPathAgent view)
            {
                if (!_indices.TryGetValue(view, out var index)) return;
                var last = _views.Count - 1;
                if (!_dirty && _slots.IsCreated)
                {
                    _slots.Remove(_entities[index], index);
                    if (index != last)
                    {
                        _slots.Remove(_entities[last], last);
                        _slots.Add(_entities[last], index);
                    }
                }
                _indices.Remove(view);
                if (index != last)
                {
                    _views[index] = _views[last]; _entities[index] = _entities[last];
#if SKGAMES_ANIMATED_IMPOSTOR
                    _animationPlayers[index] = _animationPlayers[last];
                    if (_animationStates.IsCreated && last < _animationStates.Length)
                    {
                        _animationStates[index] = _animationStates[last];
                        _animationEnabled[index] = _animationEnabled[last];
                    }
                    else if (_animationStates.IsCreated && index < _animationStates.Length)
                    {
                        _animationStates[index] = byte.MaxValue;
                        _animationEnabled[index] = (byte)(_animationPlayers[index].Length > 0 ? 1 : 0);
                    }
#endif
                    _indices[_views[index]] = index;
                }
                _views.RemoveAt(last); _entities.RemoveAt(last); _transforms.RemoveAtSwapBack(index);
#if SKGAMES_ANIMATED_IMPOSTOR
                _animationPlayers.RemoveAt(last);
#endif
            }

            public void Sync()
            {
                if (!_world.IsCreated || _views.Count == 0) return;
                if (!_poses.IsCreated || _poses.Length < _views.Count)
                {
                    DisposeBuffers();
                    var capacity = Mathf.NextPowerOfTwo(Mathf.Max(16, _views.Count));
                    _poses = new NativeArray<RecoilUnitPose>(capacity, Allocator.Persistent);
                    _valid = new NativeArray<byte>(capacity, Allocator.Persistent);
#if SKGAMES_ANIMATED_IMPOSTOR
                    _animationEnabled = new NativeArray<byte>(capacity, Allocator.Persistent);
                    _animationStates = new NativeArray<byte>(capacity, Allocator.Persistent);
                    for (var i = 0; i < _views.Count; i++)
                    {
                        _animationStates[i] = byte.MaxValue;
                        _animationEnabled[i] = (byte)(_animationPlayers[i].Length > 0 ? 1 : 0);
                    }
#endif
                    _slots = new NativeParallelMultiHashMap<Entity, int>(capacity, Allocator.Persistent);
                    _dirty = true;
                }
                if (_dirty)
                {
                    _slots.Clear();
                    for (var i = 0; i < _entities.Count; i++) _slots.Add(_entities[i], i);
                    _dirty = false;
                    FullMapBuilds++;
                }
                var manager = _world.EntityManager;
                var reset = new ResetValidityJob { Valid = _valid }.Schedule(_views.Count, 64);
                var capture = new CapturePosesJob
                {
                    Entities = manager.GetEntityTypeHandle(),
                    Poses = manager.GetComponentTypeHandle<RecoilUnitPose>(true),
                    Navigations = manager.GetComponentTypeHandle<RecoilUnitNavigation>(true),
#if SKGAMES_ANIMATED_IMPOSTOR
                    Combat = manager.GetComponentTypeHandle<RecoilBattleEcsState>(true),
                    AnimationEnabled = _animationEnabled, AnimationStates = _animationStates,
                    AnimationChanges = _animationChanges.AsParallelWriter(),
#endif
                    Slots = _slots, Output = _poses, Valid = _valid
                }.ScheduleParallel(_query, reset);
                // Finish before rendering and before any view can be pooled/destroyed next frame.
                new ApplyTransformsJob { Poses = _poses, Valid = _valid }.Schedule(_transforms, capture).Complete();
#if SKGAMES_ANIMATED_IMPOSTOR
                while (_animationChanges.TryDequeue(out var change))
                {
                    var players = _animationPlayers[change.Slot];
                    for (var i = 0; i < players.Length; i++)
                        if (players[i] != null) players[i].SetUnitState((AnimatedImpostorUnitState)change.State);
                }
#endif
            }

            private void DisposeBuffers()
            {
                if (_poses.IsCreated) _poses.Dispose();
                if (_valid.IsCreated) _valid.Dispose();
                if (_slots.IsCreated) _slots.Dispose();
#if SKGAMES_ANIMATED_IMPOSTOR
                if (_animationEnabled.IsCreated) _animationEnabled.Dispose();
                if (_animationStates.IsCreated) _animationStates.Dispose();
#endif
            }

            public void Dispose()
            {
                DisposeBuffers();
#if SKGAMES_ANIMATED_IMPOSTOR
                if (_animationChanges.IsCreated) _animationChanges.Dispose();
#endif
                if (_transforms.isCreated) _transforms.Dispose();
                if (_world.IsCreated) _query.Dispose();
            }
        }
    }
}
