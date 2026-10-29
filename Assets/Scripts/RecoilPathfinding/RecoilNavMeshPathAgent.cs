// Adapted from RecoilEngine ground movement concepts; GPL-2.0-or-later.
using Unity.Entities;
using UnityEngine;
using UnityEngine.AI;

namespace RecoilPathfinding
{
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class RecoilNavMeshPathAgent : MonoBehaviour
    {
        [SerializeField] private RecoilNavMeshPathService pathService;
        [SerializeField, Min(0.01f)] private float maxSpeed = 3.5f;
        [SerializeField, Min(0.01f)] private float acceleration = 12f;
        [SerializeField, Min(0.01f)] private float turnSpeedDegrees = 540f;
        [SerializeField, Min(0.01f)] private float waypointTolerance = 0.25f;
        [SerializeField, Min(0f)] private float avoidanceRadius = 1.2f;
        [SerializeField, Min(0f)] private float avoidanceStrength = 1f;
        [SerializeField, Min(0.05f)] private float bodyRadius = 0.5f;
        [SerializeField, Min(0.01f)] private float mass = 1f;
        [SerializeField] private bool pushResistant = false;
        [SerializeField, Min(0f)] private float navMeshVerticalOffset = 1f;
        [SerializeField] private int areaMask = NavMesh.AllAreas;

        private RecoilUnitSimulation _simulation;
        private World _ownedWorld;
        private bool _ownsEntity;
        private Vector3? _queuedDestination;
        public RecoilUnitSimulation Simulation => _simulation;
        public float CurrentSpeed => _simulation != null && _simulation.Exists ? _simulation.CurrentSpeed : 0f;
        public bool HasPath => _simulation != null && _simulation.Exists && _simulation.HasPath;
        public bool IsPathPending => _simulation != null && _simulation.Exists && _simulation.IsPathPending;
        public float AvoidanceRadius => avoidanceRadius;
        public RecoilUnitMovementSettings MovementSettings => new RecoilUnitMovementSettings
        {
            MaxSpeed = maxSpeed, Acceleration = acceleration, TurnSpeedDegrees = turnSpeedDegrees,
            WaypointTolerance = waypointTolerance, AvoidanceRadius = avoidanceRadius,
            AvoidanceStrength = avoidanceStrength, VerticalOffset = navMeshVerticalOffset, AreaMask = areaMask,
            BodyRadius = bodyRadius, Mass = mass, PushResistant = (byte)(pushResistant ? 1 : 0)
        };

        public void ConfigurePathService(RecoilNavMeshPathService service)
        {
            if (service == null)
                throw new System.ArgumentNullException(nameof(service));
            pathService = service;
        }

        private void Start()
        {
            if (_simulation != null) return;
            if (pathService == null) pathService = FindFirstObjectByType<RecoilNavMeshPathService>();
            _ownedWorld = World.DefaultGameObjectInjectionWorld;
            if (pathService == null || _ownedWorld == null || !_ownedWorld.IsCreated)
            {
                Debug.LogError("Path agent requires a path service and an Entities world.", this);
                enabled = false;
                return;
            }
            var entity = _ownedWorld.EntityManager.CreateEntity();
            _simulation = new RecoilUnitSimulation(_ownedWorld, entity, pathService,
                transform.position, transform.rotation, MovementSettings);
            _ownsEntity = true;
            if (_queuedDestination.HasValue) _simulation.SetDestination(_queuedDestination.Value);
            _queuedDestination = null;
            RecoilUnitPresentationSync.Register(this);
        }

        // Externally owned units continue simulating when their view is disabled or destroyed.
        public void Bind(RecoilUnitSimulation simulation)
        {
            RecoilUnitPresentationSync.Unregister(this);
            ReleaseOwnedEntity();
            _simulation = simulation;
            _queuedDestination = null;
            RecoilUnitPresentationSync.Register(this);
        }

        private void OnEnable()
        {
            if (_ownsEntity && _simulation != null) _simulation.SetActive(true);
            RecoilUnitPresentationSync.Register(this);
        }
        private void OnDisable()
        {
            RecoilUnitPresentationSync.Unregister(this);
            if (_ownsEntity && _simulation != null) _simulation.SetActive(false);
        }
        private void OnDestroy()
        {
            RecoilUnitPresentationSync.Unregister(this);
            ReleaseOwnedEntity();
        }
        private void OnDrawGizmosSelected()
        {
#if UNITY_EDITOR
            // Unity also invokes this for children when their parent is selected.
            if (UnityEditor.Selection.activeGameObject != gameObject) return;
#endif
            if (_simulation != null) _simulation.DrawDebugPath();
#if UNITY_EDITOR
            if (_simulation != null && _simulation.Exists)
                UnityEditor.Handles.Label(_simulation.Position + Vector3.up * 2f, _simulation.DebugCombatStatus());
#endif
        }
        private void ReleaseOwnedEntity()
        {
            if (!_ownsEntity) return;
            if (_simulation != null) _simulation.SetActive(false);
            if (_ownedWorld != null && _ownedWorld.IsCreated && _simulation != null && _simulation.Exists)
                _ownedWorld.EntityManager.DestroyEntity(_simulation.Entity);
            _ownsEntity = false;
        }
        public void SetDestination(Vector3 destination)
        {
            if (_simulation != null && _simulation.Exists) _simulation.SetDestination(destination);
            else _queuedDestination = destination;
        }
        public void Stop()
        {
            _queuedDestination = null;
            if (_simulation != null) _simulation.Stop();
        }
    }
}
