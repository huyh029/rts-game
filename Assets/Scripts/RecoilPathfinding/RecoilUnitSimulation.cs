// Adapted from RecoilEngine ground movement concepts; GPL-2.0-or-later.
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;

namespace RecoilPathfinding
{
    public struct RecoilUnitPose : IComponentData
    {
        public float3 Position;
        public float3 Velocity;
        public quaternion Rotation;
        public float Speed;
    }

    public struct RecoilUnitMovementSettings : IComponentData
    {
        public float MaxSpeed;
        public float Acceleration;
        public float TurnSpeedDegrees;
        public float WaypointTolerance;
        public float AvoidanceRadius;
        public float AvoidanceStrength;
        public float BodyRadius;
        public float Mass;
        public byte PushResistant;
        public float VerticalOffset;
        public int AreaMask;
    }

    public enum RecoilUnitOrderKind : byte { Stop, Move, Attack }

    public struct RecoilUnitOrder : IComponentData
    {
        public RecoilUnitOrderKind Kind;
        public float3 Destination;
        public Entity Target;
    }

    public struct RecoilUnitNavigation : IComponentData
    {
        public float3 Destination;
        public float3 RequestedDestination;
        public int WaypointIndex;
        public int RequestVersion;
        public int MovementVersion;
        public byte HasDestination;
        public byte PathPending;
        public byte Active;
        public int SurfaceTriangle;
        public RecoilPathKind PathKind;
    }

    public struct RecoilUnitCombatTiming : IComponentData
    {
        public float NextPathRequestTime;
        public float NextTargetSearchTime;
        public float LastCombatUpdateTime;
        public float3 LastDestination;
        public Entity NavigationTarget;
    }

    [InternalBufferCapacity(8)]
    public struct RecoilUnitWaypoint : IBufferElementData
    {
        public float3 Position;
    }

    // Main-thread bridge to the NavMesh API. All mutable unit state belongs to the entity.
    // No Transform or GameObject is needed to request a path or advance movement.
    public sealed class RecoilUnitSimulation
    {
        private readonly World _world;
        internal static RecoilUnitSimulation DebugObserved { get; private set; }
        private readonly RecoilNavMeshPathService _service;
        private Vector3 _debugStepStart;
        private Vector3 _debugStepCandidate;
        private float _debugStepTime = -1f;
        private float _debugStepSpeed;
        private bool _debugStepProjected;
        private bool _debugStepHasPath;
        private int _debugStepVersion;
        private float _debugPlanned;
        private float _debugContacts;
        private float _debugDelta;
        private float3 _debugWaypoint;
        public Entity Entity { get; }
        internal World SimulationWorld => _world;
        private EntityManager Manager => _world.EntityManager;
        public bool Exists => _world != null && _world.IsCreated && Manager.Exists(Entity);
        public bool IsActive => Exists && Navigation.Active != 0;
        public RecoilUnitPose Pose => Manager.GetComponentData<RecoilUnitPose>(Entity);
        public RecoilUnitMovementSettings Settings => Manager.GetComponentData<RecoilUnitMovementSettings>(Entity);
        private RecoilUnitNavigation Navigation
        {
            get => Manager.GetComponentData<RecoilUnitNavigation>(Entity);
            set => Manager.SetComponentData(Entity, value);
        }
        public Vector3 Position => Pose.Position;
        public Quaternion Rotation => Pose.Rotation;
        public float CurrentSpeed => Pose.Speed;
        public int MovementVersion => Navigation.MovementVersion;
        public bool IsPathPending => Navigation.PathPending != 0;
        public bool HasPath => Exists && Navigation.WaypointIndex < Manager.GetBuffer<RecoilUnitWaypoint>(Entity).Length;
        public Vector3 CurrentWaypoint => HasPath
            ? (Vector3)Manager.GetBuffer<RecoilUnitWaypoint>(Entity)[Navigation.WaypointIndex].Position
            : Position;

        public RecoilUnitSimulation(World world, Entity entity, RecoilNavMeshPathService service,
            Vector3 position, Quaternion rotation, RecoilUnitMovementSettings settings)
        {
            _world = world;
            Entity = entity;
            _service = service;
            SetOrAdd(new RecoilUnitPose { Position = position, Rotation = rotation });
            SetOrAdd(settings);
            SetOrAdd(new RecoilUnitOrder());
            SetOrAdd(new RecoilUnitCombatTiming());
            // Preserve generations when a pooled entity is reused: old callbacks/jobs must stay stale.
            var generation = Manager.HasComponent<RecoilUnitNavigation>(entity)
                ? Navigation.RequestVersion + Navigation.MovementVersion + 1 : 1;
            SetOrAdd(new RecoilUnitNavigation { RequestVersion = generation, MovementVersion = generation, SurfaceTriangle = -1 });
            if (!Manager.HasBuffer<RecoilUnitWaypoint>(entity))
                Manager.AddBuffer<RecoilUnitWaypoint>(entity);
            Manager.GetBuffer<RecoilUnitWaypoint>(entity).Clear();
            SetActive(true);
        }

        private void SetOrAdd<T>(T component) where T : unmanaged, IComponentData
        {
            if (Manager.HasComponent<T>(Entity)) Manager.SetComponentData(Entity, component);
            else Manager.AddComponentData(Entity, component);
        }

        public void SetActive(bool active)
        {
            if (!active && ReferenceEquals(DebugObserved, this)) DebugObserved = null;
            if (!Exists) return;
            if (!active) Stop();
            var navigation = Navigation;
            navigation.Active = (byte)(active ? 1 : 0);
            Navigation = navigation;
            if (_service == null) return;
            if (active) _service.RegisterSimulation(this);
            else _service.UnregisterSimulation(this);
        }

        public bool PrepareMovement()
        {
            var navigation = Navigation;
            var path = Manager.GetBuffer<RecoilUnitWaypoint>(Entity);
            var position = Pose.Position;
            var tolerance = Settings.WaypointTolerance;
            while (navigation.WaypointIndex < path.Length &&
                   math.distance(position, path[navigation.WaypointIndex].Position) <= tolerance)
                navigation.WaypointIndex++;
            Navigation = navigation;
            return navigation.WaypointIndex < path.Length;
        }

        internal bool CaptureMovement(out RecoilUnitPose pose, out RecoilUnitMovementSettings settings,
            out RecoilUnitNavigation navigation, out float3 waypoint, out bool hasPath)
        {
            pose = default; settings = default; navigation = default; waypoint = default; hasPath = false;
            if (!Exists) return false;
            navigation = Navigation;
            if (navigation.Active == 0) return false;
            pose = Pose;
            settings = Settings;
            var path = Manager.GetBuffer<RecoilUnitWaypoint>(Entity);
            var previousIndex = navigation.WaypointIndex;
            while (navigation.WaypointIndex < path.Length &&
                math.distancesq(pose.Position, path[navigation.WaypointIndex].Position) <=
                settings.WaypointTolerance * settings.WaypointTolerance)
                navigation.WaypointIndex++;
            if (previousIndex != navigation.WaypointIndex) Navigation = navigation;
            hasPath = navigation.WaypointIndex < path.Length;
            waypoint = hasPath ? path[navigation.WaypointIndex].Position : pose.Position;
            return true;
        }

        internal bool GetNextWaypoint(out float3 next, out bool hasAnother)
        {
            var navigation = Navigation;
            var path = Manager.GetBuffer<RecoilUnitWaypoint>(Entity);
            var index = navigation.WaypointIndex + 1;
            next = index < path.Length ? path[index].Position : Pose.Position;
            hasAnother = index + 1 < path.Length;
            return index < path.Length;
        }

        internal void AdvanceCorner(int version, int index)
        {
            if (!IsActive) return;
            var navigation = Navigation;
            if (navigation.MovementVersion != version || navigation.WaypointIndex != index ||
                index + 1 >= Manager.GetBuffer<RecoilUnitWaypoint>(Entity).Length) return;
            navigation.WaypointIndex++;
            Navigation = navigation;
        }

        internal void ApplySurfaceMovement(float3 position, quaternion rotation, float speed,
            int triangle, int version, float deltaTime)
        {
            if (!Exists) return;
            var navigation = Navigation;
            if (navigation.Active == 0 || navigation.MovementVersion != version) return;
            var pose = Pose;
            var velocity = deltaTime > 0f ? (position - pose.Position) / deltaTime : float3.zero;
            if (math.any(pose.Position != position) || math.any(pose.Velocity != velocity) ||
                math.any(pose.Rotation.value != rotation.value) || pose.Speed != speed)
            {
                pose.Position = position; pose.Velocity = velocity; pose.Rotation = rotation; pose.Speed = speed;
                Manager.SetComponentData(Entity, pose);
            }
            if (navigation.SurfaceTriangle != triangle)
            {
                navigation.SurfaceTriangle = triangle;
                Navigation = navigation;
            }
        }

        public void ApplyMovement(Vector3 candidatePosition, Vector3 pathMovement, Vector3 separation,
            float nextSpeed, Quaternion nextRotation, int movementVersion, float deltaTime)
        {
            RecoilUnitPose pose;
            RecoilUnitMovementSettings settings;
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.FallbackRead))
            {
                if (!Exists) return;
                var navigation = Navigation;
                if (navigation.Active == 0 || movementVersion != navigation.MovementVersion) return;
                pose = Pose;
                settings = Settings;
            }
            var position = (Vector3)pose.Position;
            var projected = position;
            var valid = false;
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.FallbackProjection))
            {
                // Both attempts start at the same pose: sample its surface only once.
                var offset = Vector3.up * settings.VerticalOffset;
                if (NavMesh.SamplePosition(position - offset, out var startHit, 1f, settings.AreaMask))
                {
                    valid = TryProjectMovement(startHit.position, candidatePosition, settings, out projected, out var blocked);
                    if ((!valid || blocked) && separation.sqrMagnitude > 0.0001f &&
                        TryProjectMovement(startHit.position, position + pathMovement, settings, out var fallback, out _))
                    {
                        projected = fallback;
                        valid = true;
                    }
                }
            }
            pose.Position = valid ? (float3)projected : pose.Position;
            pose.Velocity = deltaTime > 0f ? (pose.Position - (float3)position) / deltaTime : float3.zero;
            pose.Speed = valid ? nextSpeed : 0f;
            pose.Rotation = nextRotation;
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.FallbackWrite))
                Manager.SetComponentData(Entity, pose);
        }

        private static bool TryProjectMovement(Vector3 surfaceStart, Vector3 candidate,
            RecoilUnitMovementSettings settings, out Vector3 projected, out bool blocked)
        {
            projected = surfaceStart + Vector3.up * settings.VerticalOffset;
            blocked = true;
            var offset = Vector3.up * settings.VerticalOffset;
            // Keep projection local: do not teleport to a distant region to hide an invalid step.
            const float surfaceTolerance = 1f;
            if (!NavMesh.SamplePosition(candidate - offset, out var candidateHit, surfaceTolerance, settings.AreaMask))
                return false;

            blocked = NavMesh.Raycast(surfaceStart, candidateHit.position, out var boundaryHit, settings.AreaMask);
            var surfacePosition = blocked ? boundaryHit.position : candidateHit.position;
            if (!float.IsFinite(surfacePosition.x) || !float.IsFinite(surfacePosition.y) ||
                !float.IsFinite(surfacePosition.z))
                return false;
            projected = surfacePosition + offset;
            return true;
        }

        public void SetDestination(Vector3 destination)
        {
            if (!IsActive) return;
            var navigation = Navigation;
            if (navigation.HasDestination != 0 && (HasPath || IsPathPending) &&
                HorizontalDistanceSquared(navigation.Destination, destination) <= 0.0001f)
                return;
            navigation.MovementVersion++;
            navigation.Destination = destination;
            navigation.HasDestination = 1;
            Navigation = navigation;
            var order = Manager.GetComponentData<RecoilUnitOrder>(Entity);
            order.Destination = destination;
            if (order.Kind != RecoilUnitOrderKind.Attack) order.Kind = RecoilUnitOrderKind.Move;
            Manager.SetComponentData(Entity, order);
            if (navigation.PathPending == 0) RequestPath();
        }

        internal void RequestPathFromEcs()
        {
            if (!IsActive) return;
            var navigation = Navigation;
            if (navigation.HasDestination != 0 && navigation.PathPending == 0) RequestPath();
        }

        private void RequestPath()
        {
            if (_service == null) return;
            var navigation = Navigation;
            var requestVersion = ++navigation.RequestVersion;
            navigation.RequestedDestination = navigation.Destination;
            navigation.PathPending = 1;
            Navigation = navigation;
            _service.RequestPath(Position, navigation.RequestedDestination, Settings.AreaMask, result =>
            {
                if (!IsActive || Navigation.RequestVersion != requestVersion) return;
                var current = Navigation;
                current.PathPending = 0;
                Navigation = current;
                var tolerance = math.max(Settings.WaypointTolerance, Settings.AvoidanceRadius);
                if (HorizontalDistanceSquared(current.RequestedDestination, current.Destination) > tolerance * tolerance)
                {
                    RequestPath();
                    return;
                }
                current.MovementVersion++;
                current.PathKind = result.Kind;
                current.WaypointIndex = 0;
                var path = Manager.GetBuffer<RecoilUnitWaypoint>(Entity);
                path.Clear();
                if (result.Succeeded)
                {
                    for (var i = 0; i < result.Points.Count; i++)
                        path.Add(new RecoilUnitWaypoint
                        { Position = (float3)result.Points[i] + new float3(0f, Settings.VerticalOffset, 0f) });
                    current.WaypointIndex = path.Length > 1 ? 1 : path.Length;
                }
                else
                {
                    var pose = Pose;
                    pose.Speed = 0f;
                    pose.Velocity = float3.zero;
                    Manager.SetComponentData(Entity, pose);
                    Debug.LogWarning($"Unit {Entity} could not find a path: {result.Error}");
                }
                Navigation = current;
            }, _world, Entity, requestVersion);
        }

        // Halting movement need not cancel an attack order when the target is in range.
        public void StopMovement()
        {
            if (!Exists) return;
            var navigation = Navigation;
            navigation.MovementVersion++;
            navigation.RequestVersion++;
            navigation.HasDestination = 0;
            navigation.PathPending = 0;
            navigation.WaypointIndex = 0;
            Navigation = navigation;
            Manager.GetBuffer<RecoilUnitWaypoint>(Entity).Clear();
            var pose = Pose;
            pose.Speed = 0f;
            pose.Velocity = float3.zero;
            Manager.SetComponentData(Entity, pose);
        }

        public void Stop()
        {
            if (!Exists) return;
            StopMovement();
            Manager.SetComponentData(Entity, new RecoilUnitOrder());
        }

        private static float HorizontalDistanceSquared(float3 a, float3 b) => math.lengthsq(a.xz - b.xz);

        internal int DebugSurfaceTriangle => Exists ? Navigation.SurfaceTriangle : -1;

        internal string DebugCombatStatus()
        {
            DebugObserved = this;
            if (!Exists) return "Entity missing";
            var movement = _debugStepTime < 0f ? "\nNo crowd update yet" :
                $"\nUpdate age {Time.time - _debugStepTime:F2}s | HasPath {_debugStepHasPath} | Speed {_debugStepSpeed:F2}" +
                $"\nCandidate {Vector3.Distance(_debugStepStart, _debugStepCandidate):F4} | Actual {Vector3.Distance(_debugStepStart, Position):F4} | Surface {(_debugStepProjected ? "Burst" : "NavMesh fallback")}" +
                $"\nMovement version {_debugStepVersion} -> {Navigation.MovementVersion}" +
                $"\nPlanned {_debugPlanned:F4} -> Contacts {_debugContacts:F4} -> Surface {Vector3.Distance(_debugStepStart, _debugStepCandidate):F4}" +
                $"\ndt {_debugDelta:F4} | Waypoint XZ {math.distance(_debugWaypoint.xz, ((float3)_debugStepStart).xz):F3} | Y {_debugWaypoint.y - _debugStepStart.y:F3}";
            if (!Manager.HasComponent<RecoilBattleEcsState>(Entity))
                return $"{Entity}: navigation only, NOT in combat" + movement;
            var state = Manager.GetComponentData<RecoilBattleEcsState>(Entity);
            return $"{Entity} | Team {state.Team} | HP {state.Health} | Active {state.Active} | Target {state.Target}" + movement;
        }

        internal void RecordMovementDebug(float3 start, float3 candidate, float speed, bool projected, bool hasPath, int version,
            float planned, float contacts, float deltaTime, float3 waypoint, float sampleTime)
        {
            _debugStepStart = start;
            _debugStepCandidate = candidate;
            _debugStepSpeed = speed;
            _debugStepProjected = projected;
            _debugStepHasPath = hasPath;
            _debugStepTime = sampleTime;
            _debugStepVersion = version;
            _debugPlanned = planned;
            _debugContacts = contacts;
            _debugDelta = deltaTime;
            _debugWaypoint = waypoint;
        }

        internal void DrawDebugPath()
        {
            if (!IsActive) return;
            var navigation = Navigation;
            var path = Manager.GetBuffer<RecoilUnitWaypoint>(Entity);
            var previousColor = Gizmos.color;
            Gizmos.color = navigation.PathKind == RecoilPathKind.Funnel ? Color.cyan :
                navigation.PathKind == RecoilPathKind.MidpointFallback ? new Color(1f, 0.5f, 0f) : Color.green;
            var previous = Position;
            var end = math.min(path.Length, navigation.WaypointIndex + 32);
            for (var i = navigation.WaypointIndex; i < end; i++)
            {
                var point = (Vector3)path[i].Position;
                Gizmos.DrawLine(previous, point);
                Gizmos.DrawWireSphere(point, 0.3f);
                previous = point;
            }
            if (navigation.WaypointIndex < path.Length)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(path[navigation.WaypointIndex].Position, 0.6f);
            }
            Gizmos.color = previousColor;
        }
    }
}
