// Adapted from RecoilEngine ground movement concepts.
// RecoilEngine is GPL-2.0-or-later; this adapted file is distributed under the same terms.
// See Assets/_Game/RecoilEngine/LICENSE and its GPL license texts.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace RecoilPathfinding
{
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
        [SerializeField, Min(0f)] private float navMeshVerticalOffset = 1f;
        [SerializeField] private int areaMask = NavMesh.AllAreas;

        private IReadOnlyList<Vector3> _path;
        private int _waypointIndex;
        private int _requestVersion;
        private Vector3 _destination;
        private Vector3 _requestedDestination;
        private float _currentSpeed;
        private bool _hasDestination;
        private bool _pathPending;
        private bool _started;
        private int _movementVersion;

        public float CurrentSpeed => _currentSpeed;
        public bool HasPath => _path != null && _waypointIndex < _path.Count;
        public bool IsPathPending => _pathPending;
        public float AvoidanceRadius => avoidanceRadius;
        internal float MaxSpeed => maxSpeed;
        internal float Acceleration => acceleration;
        internal float TurnSpeedDegrees => turnSpeedDegrees;
        internal float AvoidanceStrength => avoidanceStrength;
        internal Vector3 CurrentWaypoint => HasPath ? _path[_waypointIndex] : transform.position;
        internal int MovementVersion => _movementVersion;

        private void Start()
        {
            if (pathService == null)
                pathService = FindFirstObjectByType<RecoilNavMeshPathService>();

            if (pathService == null)
            {
                Debug.LogError(
                    $"{nameof(RecoilNavMeshPathAgent)} on '{name}' requires a {nameof(RecoilNavMeshPathService)} in the scene.",
                    this);
                enabled = false;
                return;
            }

            _started = true;
            pathService.RegisterAgent(this);
            if (_hasDestination)
                RequestPath();
        }

        private void OnEnable()
        {
            if (_started)
            {
                pathService.RegisterAgent(this);
                if (_hasDestination)
                    RequestPath();
            }
        }

        private void OnDisable()
        {
            _requestVersion++;
            _movementVersion++;
            _pathPending = false;
            if (pathService != null)
                pathService.UnregisterAgent(this);
        }

        internal bool PrepareMovement()
        {
            while (HasPath &&
                   Vector3.Distance(transform.position, _path[_waypointIndex]) <= waypointTolerance)
            {
                _waypointIndex++;
            }

            return HasPath;
        }

        internal void ApplyMovement(
            Vector3 candidatePosition,
            Vector3 pathMovement,
            Vector3 separation,
            float nextSpeed,
            Quaternion nextRotation,
            int movementVersion)
        {
            if (!isActiveAndEnabled || movementVersion != _movementVersion)
                return;

            _currentSpeed = nextSpeed;
            transform.rotation = nextRotation;
            var navMeshStart = transform.position - Vector3.up * navMeshVerticalOffset;
            var navMeshCandidate = candidatePosition - Vector3.up * navMeshVerticalOffset;
            var blocked = NavMesh.Raycast(
                navMeshStart,
                navMeshCandidate,
                out var boundaryHit,
                areaMask);
            if (blocked && separation.sqrMagnitude > 0.0001f)
            {
                candidatePosition = transform.position + pathMovement;
                navMeshCandidate = candidatePosition - Vector3.up * navMeshVerticalOffset;
                if (NavMesh.Raycast(
                    navMeshStart,
                    navMeshCandidate,
                    out boundaryHit,
                    areaMask))
                {
                    candidatePosition.x = boundaryHit.position.x;
                    candidatePosition.z = boundaryHit.position.z;
                    candidatePosition.y = boundaryHit.position.y + navMeshVerticalOffset;
                }
            }
            else if (blocked)
            {
                candidatePosition.x = boundaryHit.position.x;
                candidatePosition.z = boundaryHit.position.z;
                candidatePosition.y = boundaryHit.position.y + navMeshVerticalOffset;
            }

            transform.position = candidatePosition;
        }

        internal void ResetForSpawn()
        {
            _requestVersion++;
            _movementVersion++;
            _destination = default;
            _requestedDestination = default;
            _currentSpeed = 0f;
            _hasDestination = false;
            _pathPending = false;
            _path = null;
            _waypointIndex = 0;
        }


        public void SetDestination(Vector3 destination)
        {
            if (_hasDestination && HorizontalDistanceSquared(_destination, destination) <= 0.0001f)
                return;

            _movementVersion++;
            _destination = destination;
            _hasDestination = true;

            if (!isActiveAndEnabled || pathService == null)
                return;

            if (_pathPending)
                return;

            RequestPath();
        }

        private void RequestPath()
        {
            var requestVersion = ++_requestVersion;
            _requestedDestination = _destination;
            _pathPending = true;
            pathService.RequestPath(transform.position, _requestedDestination, areaMask, result =>
            {
                if (!this || !isActiveAndEnabled || requestVersion != _requestVersion)
                    return;

                _pathPending = false;
                if (HorizontalDistanceSquared(_requestedDestination, _destination) >
                    Mathf.Max(waypointTolerance, avoidanceRadius) *
                    Mathf.Max(waypointTolerance, avoidanceRadius))
                {
                    RequestPath();
                    return;
                }

                _movementVersion++;
                if (!result.Succeeded)
                {
                    _path = null;
                    _waypointIndex = 0;
                    _currentSpeed = 0f;
                    Debug.LogWarning(
                        $"{nameof(RecoilNavMeshPathAgent)} on '{name}' could not find a path: {result.Error}",
                        this);
                    return;
                }

                _path = result.Points;
                _waypointIndex = _path.Count > 1 ? 1 : _path.Count;
            });
        }

        public void Stop()
        {
            _movementVersion++;
            _hasDestination = false;
            _requestVersion++;
            _pathPending = false;
            _path = null;
            _waypointIndex = 0;
            _movementVersion++;
        }

        private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
        {
            var x = a.x - b.x;
            var z = a.z - b.z;
            return x * x + z * z;
        }
    }
}
