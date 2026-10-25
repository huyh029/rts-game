// Adapted from RecoilEngine QTPFS path-sharing concepts.
// RecoilEngine is GPL-2.0-or-later; this adapted file is distributed under the same terms.
// See Assets/_Game/RecoilEngine/LICENSE and its GPL license texts.

using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.AI;
using Unity.Profiling;

namespace RecoilPathfinding
{
    [DefaultExecutionOrder(-100)]
    public sealed class RecoilNavMeshPathService : MonoBehaviour
    {
        private static readonly ProfilerMarker CrowdMovementMarker =
            new ProfilerMarker("RecoilPathfinding.CrowdMovement");

        [SerializeField, Min(0.1f)] private float maxProjectionDistance = 5f;
        [SerializeField, Min(1)] private int maxCachedFields = 16;
        [SerializeField, Min(0.1f)] private float crowdCellSize = 2.5f;
        [SerializeField, Min(1)] private int maxAvoidanceNeighbors = 8;
        [SerializeField, Min(1f)] private float crowdSimulationRate = 30f;

        private readonly List<Triangle> _triangles = new();
        private List<Edge>[] _edgesByTriangle = Array.Empty<List<Edge>>();
        private NativeArray<NavTriangleData> _nativeTriangles;
        private NativeArray<float> _nativeAreaCosts;
        private NativeParallelMultiHashMap<int2, int> _trianglesByCell;
        private readonly Queue<PendingProjectionRequest> _queuedProjectionRequests = new();
        private readonly List<PendingProjectionRequest> _activeProjectionRequests = new();
        private NativeArray<ProjectionInput> _projectionInputs;
        private NativeArray<int> _projectionResults;
        private JobHandle _projectionHandle;
        private int _projectionCapacity;
        private readonly List<RecoilNavMeshPathAgent> _agents = new();
        private readonly HashSet<RecoilNavMeshPathAgent> _registeredAgents = new();
        private readonly List<RecoilNavMeshPathAgent> _pendingCrowdAgents = new();
        private int[] _crowdMovementVersions = Array.Empty<int>();
        private NativeArray<CrowdMovementInput> _crowdMovementInputs;
        private NativeArray<CrowdMovementOutput> _crowdMovementOutputs;
        private NativeParallelMultiHashMap<int2, int> _crowdAgentsByCell;
        private JobHandle _crowdMovementHandle;
        private int _crowdCapacity;
        private readonly Dictionary<FieldKey, FlowField> _fields = new();
        private readonly List<FlowField> _fieldOrder = new();
        private readonly Dictionary<FieldKey, List<PendingRequest>> _waitingRequests = new();
        private readonly Queue<FieldKey> _waitingOrder = new();
        private float _cellSize = 8f;
        private float _crowdSimulationAccumulator;
        private bool _crowdMovementPending;
        private bool _initialized;

        private readonly struct Triangle
        {
            public readonly Vector3 A;
            public readonly Vector3 B;
            public readonly Vector3 C;
            public readonly Vector3 Center;
            public readonly Vector3 Min;
            public readonly Vector3 Max;
            public readonly int Area;

            public Triangle(Vector3 a, Vector3 b, Vector3 c, int area)
            {
                A = a;
                B = b;
                C = c;
                Center = (a + b + c) / 3f;
                Min = Vector3.Min(a, Vector3.Min(b, c));
                Max = Vector3.Max(a, Vector3.Max(b, c));
                Area = area;
            }
        }

        private struct NavTriangleData
        {
            public float3 A;
            public float3 B;
            public float3 C;
            public float3 Center;
            public float3 Portal0;
            public float3 Portal1;
            public float3 Portal2;
            public int Area;
            public int Neighbor0;
            public int Neighbor1;
            public int Neighbor2;

            public int GetNeighbor(int index) =>
                index == 0 ? Neighbor0 : index == 1 ? Neighbor1 : Neighbor2;

            public float3 GetPortal(int index) =>
                index == 0 ? Portal0 : index == 1 ? Portal1 : Portal2;

            public void SetEdge(int index, int neighbor, float3 portal)
            {
                if (index == 0)
                {
                    Neighbor0 = neighbor;
                    Portal0 = portal;
                }
                else if (index == 1)
                {
                    Neighbor1 = neighbor;
                    Portal1 = portal;
                }
                else
                {
                    Neighbor2 = neighbor;
                    Portal2 = portal;
                }
            }
        }

        private struct ProjectionInput
        {
            public float3 Position;
            public int AreaMask;
        }

        [BurstCompile]
        private struct FindProjectedTrianglesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ProjectionInput> Inputs;
            [ReadOnly] public NativeArray<NavTriangleData> Triangles;
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> TrianglesByCell;
            [WriteOnly] public NativeArray<int> Results;
            public float CellSize;

            public void Execute(int index)
            {
                var input = Inputs[index];
                var cell = (int2)math.floor(input.Position.xz / CellSize);
                var bestDistance = float.PositiveInfinity;
                var bestTriangle = -1;
                for (var x = cell.x - 1; x <= cell.x + 1; x++)
                {
                    for (var z = cell.y - 1; z <= cell.y + 1; z++)
                    {
                        if (!TrianglesByCell.TryGetFirstValue(new int2(x, z), out var candidateIndex, out var iterator))
                            continue;

                        do
                        {
                            var triangle = Triangles[candidateIndex];
                            if (!IsAreaAllowed(input.AreaMask, triangle.Area))
                                continue;

                            var closest = ClosestPointOnTriangle(
                                input.Position,
                                triangle.A,
                                triangle.B,
                                triangle.C);
                            var distance = math.lengthsq(closest - input.Position);
                            if (distance >= bestDistance)
                                continue;

                            bestDistance = distance;
                            bestTriangle = candidateIndex;
                        }
                        while (TrianglesByCell.TryGetNextValue(out candidateIndex, ref iterator));
                    }
                }

                Results[index] = bestTriangle;
            }

            private static bool IsAreaAllowed(int areaMask, int area) =>
                area >= 0 && area < 32 && (areaMask & (1 << area)) != 0;

            private static float3 ClosestPointOnTriangle(float3 point, float3 a, float3 b, float3 c)
            {
                var ab = b - a;
                var ac = c - a;
                var ap = point - a;
                var d1 = math.dot(ab, ap);
                var d2 = math.dot(ac, ap);
                if (d1 <= 0f && d2 <= 0f)
                    return a;

                var bp = point - b;
                var d3 = math.dot(ab, bp);
                var d4 = math.dot(ac, bp);
                if (d3 >= 0f && d4 <= d3)
                    return b;

                var vc = d1 * d4 - d3 * d2;
                if (vc <= 0f && d1 >= 0f && d3 <= 0f)
                    return a + ab * (d1 / (d1 - d3));

                var cp = point - c;
                var d5 = math.dot(ab, cp);
                var d6 = math.dot(ac, cp);
                if (d6 >= 0f && d5 <= d6)
                    return c;

                var vb = d5 * d2 - d1 * d6;
                if (vb <= 0f && d2 >= 0f && d6 <= 0f)
                    return a + ac * (d2 / (d2 - d6));

                var va = d3 * d6 - d5 * d4;
                if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
                    return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));

                var denominator = 1f / (va + vb + vc);
                return a + ab * (vb * denominator) + ac * (vc * denominator);
            }
        }

        [BurstCompile]
        private struct BuildFlowFieldJob : IJob
        {
            [ReadOnly] public NativeArray<NavTriangleData> Triangles;
            [ReadOnly] public NativeArray<float> AreaCosts;
            [WriteOnly] public NativeArray<int> NextTriangle;
            public NativeArray<float> Costs;
            public NativeArray<int> Heap;
            public NativeArray<int> HeapPositions;
            public int GoalTriangle;
            public int AreaMask;

            public void Execute()
            {
                for (var i = 0; i < Triangles.Length; i++)
                {
                    Costs[i] = float.PositiveInfinity;
                    NextTriangle[i] = -1;
                    HeapPositions[i] = -1;
                }

                Costs[GoalTriangle] = 0f;
                var heapCount = 0;
                PushOrDecrease(GoalTriangle, ref heapCount);
                while (heapCount > 0)
                {
                    var currentIndex = PopMinimum(ref heapCount);
                    var current = Triangles[currentIndex];
                    var areaCost = current.Area >= 0 && current.Area < AreaCosts.Length
                        ? AreaCosts[current.Area]
                        : 1f;

                    for (var edgeIndex = 0; edgeIndex < 3; edgeIndex++)
                    {
                        var neighborIndex = current.GetNeighbor(edgeIndex);
                        if (neighborIndex < 0)
                            continue;

                        var neighbor = Triangles[neighborIndex];
                        if (!IsAreaAllowed(AreaMask, neighbor.Area))
                            continue;

                        var candidateCost = Costs[currentIndex] +
                                            math.distance(
                                                current.Center,
                                                neighbor.Center) * areaCost;
                        if (candidateCost >= Costs[neighborIndex])
                            continue;

                        Costs[neighborIndex] = candidateCost;
                        NextTriangle[neighborIndex] = currentIndex;
                        PushOrDecrease(neighborIndex, ref heapCount);
                    }
                }
            }

            private void PushOrDecrease(int triangleIndex, ref int heapCount)
            {
                var position = HeapPositions[triangleIndex];
                if (position < 0)
                {
                    position = heapCount++;
                    HeapPositions[triangleIndex] = position;
                    Heap[position] = triangleIndex;
                }

                while (position > 0)
                {
                    var parent = (position - 1) / 2;
                    var parentTriangle = Heap[parent];
                    if (Costs[parentTriangle] <= Costs[triangleIndex])
                        break;

                    Heap[position] = parentTriangle;
                    HeapPositions[parentTriangle] = position;
                    position = parent;
                }

                Heap[position] = triangleIndex;
                HeapPositions[triangleIndex] = position;
            }

            private int PopMinimum(ref int heapCount)
            {
                var minimum = Heap[0];
                HeapPositions[minimum] = -1;
                heapCount--;
                if (heapCount == 0)
                    return minimum;

                var last = Heap[heapCount];
                var position = 0;
                while (true)
                {
                    var left = position * 2 + 1;
                    if (left >= heapCount)
                        break;

                    var right = left + 1;
                    var child = right < heapCount &&
                                Costs[Heap[right]] < Costs[Heap[left]]
                        ? right
                        : left;
                    var childTriangle = Heap[child];
                    if (Costs[childTriangle] >= Costs[last])
                        break;

                    Heap[position] = childTriangle;
                    HeapPositions[childTriangle] = position;
                    position = child;
                }

                Heap[position] = last;
                HeapPositions[last] = position;
                return minimum;
            }

            private static bool IsAreaAllowed(int areaMask, int area) =>
                area >= 0 && area < 32 && (areaMask & (1 << area)) != 0;
        }

        private readonly struct Edge
        {
            public readonly int Neighbor;
            public readonly Vector3 Portal;

            public Edge(int neighbor, Vector3 portal)
            {
                Neighbor = neighbor;
                Portal = portal;
            }
        }

        private readonly struct VertexKey : IEquatable<VertexKey>
        {
            private readonly int _x;
            private readonly int _y;
            private readonly int _z;

            public VertexKey(Vector3 point)
            {
                _x = Mathf.RoundToInt(point.x * 1000f);
                _y = Mathf.RoundToInt(point.y * 1000f);
                _z = Mathf.RoundToInt(point.z * 1000f);
            }

            public int CompareTo(VertexKey other)
            {
                var comparison = _x.CompareTo(other._x);
                if (comparison != 0)
                    return comparison;
                comparison = _y.CompareTo(other._y);
                return comparison != 0 ? comparison : _z.CompareTo(other._z);
            }

            public bool Equals(VertexKey other) =>
                _x == other._x && _y == other._y && _z == other._z;

            public override bool Equals(object obj) => obj is VertexKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = _x;
                    hash = (hash * 397) ^ _y;
                    return (hash * 397) ^ _z;
                }
            }
        }

        private readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            private readonly VertexKey _a;
            private readonly VertexKey _b;

            public EdgeKey(VertexKey a, VertexKey b)
            {
                if (a.CompareTo(b) <= 0)
                {
                    _a = a;
                    _b = b;
                }
                else
                {
                    _a = b;
                    _b = a;
                }
            }

            public bool Equals(EdgeKey other) => _a.Equals(other._a) && _b.Equals(other._b);

            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (_a.GetHashCode() * 397) ^ _b.GetHashCode();
                }
            }
        }

        private readonly struct EdgeReference
        {
            public readonly int Triangle;
            public readonly Vector3 Midpoint;

            public EdgeReference(int triangle, Vector3 midpoint)
            {
                Triangle = triangle;
                Midpoint = midpoint;
            }
        }

        private readonly struct FieldKey : IEquatable<FieldKey>
        {
            public readonly int GoalTriangle;
            public readonly int AreaMask;

            public FieldKey(int goalTriangle, int areaMask)
            {
                GoalTriangle = goalTriangle;
                AreaMask = areaMask;
            }

            public bool Equals(FieldKey other) =>
                GoalTriangle == other.GoalTriangle && AreaMask == other.AreaMask;

            public override bool Equals(object obj) => obj is FieldKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (GoalTriangle * 397) ^ AreaMask;
                }
            }
        }

        private struct CrowdMovementInput
        {
            public float3 Position;
            public float3 Waypoint;
            public quaternion Rotation;
            public float CurrentSpeed;
            public float MaxSpeed;
            public float Acceleration;
            public float TurnSpeedDegrees;
            public float AvoidanceStrength;
            public float AvoidanceRadius;
            public float DeltaTime;
            public int InstanceId;
            public byte HasPath;
        }

        private struct CrowdMovementOutput
        {
            public float3 CandidatePosition;
            public float3 PathMovement;
            public float3 Separation;
            public quaternion Rotation;
            public float Speed;
        }

        [BurstCompile]
        private struct SimulateCrowdMovementJob : IJobParallelFor
        {
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> AgentsByCell;
            [ReadOnly] public NativeArray<CrowdMovementInput> Inputs;
            [WriteOnly] public NativeArray<CrowdMovementOutput> Outputs;
            public float CellSize;
            public int MaxNeighbors;

            public void Execute(int index)
            {
                var input = Inputs[index];
                var separation = CalculateSeparation(index, input);
                var speed = MoveTowards(
                    input.CurrentSpeed,
                    0f,
                    input.Acceleration * input.DeltaTime);
                var rotation = input.Rotation;
                var candidatePosition = input.Position;
                var pathMovement = float3.zero;

                if (input.HasPath != 0)
                {
                    var offset = input.Waypoint - input.Position;
                    var distance = math.length(offset);
                    var pathDirection = math.normalizesafe(offset);
                    var brakingDistance = input.CurrentSpeed * input.CurrentSpeed /
                                          (2f * input.Acceleration);
                    var desiredSpeed = distance <= brakingDistance
                        ? input.MaxSpeed * math.saturate(
                            distance / math.max(brakingDistance, 0.0001f))
                        : input.MaxSpeed;
                    speed = MoveTowards(
                        input.CurrentSpeed,
                        desiredSpeed,
                        input.Acceleration * input.DeltaTime);

                    var desiredDirection = pathDirection;
                    if (math.lengthsq(separation) > 0.0001f)
                    {
                        var steeredDirection = pathDirection +
                                               separation * input.AvoidanceStrength;
                        if (math.lengthsq(steeredDirection) > 0.0001f)
                            desiredDirection = math.normalizesafe(steeredDirection);
                    }

                    if (math.lengthsq(desiredDirection) > 0.0001f)
                    {
                        var desiredRotation = quaternion.LookRotationSafe(
                            desiredDirection,
                            new float3(0f, 1f, 0f));
                        var dot = math.clamp(math.abs(math.dot(rotation.value, desiredRotation.value)), 0f, 1f);
                        var angle = 2f * math.acos(dot);
                        var maxRadians = input.TurnSpeedDegrees * (math.PI / 180f) * input.DeltaTime;
                        rotation = angle <= 0.0001f
                            ? desiredRotation
                            : math.slerp(rotation, desiredRotation, math.min(1f, maxRadians / angle));
                    }

                    pathMovement = pathDirection * (speed * input.DeltaTime);
                    var movementDirection = desiredDirection;
                    candidatePosition = input.Position + movementDirection * (speed * input.DeltaTime);
                }

                Outputs[index] = new CrowdMovementOutput
                {
                    CandidatePosition = candidatePosition,
                    PathMovement = pathMovement,
                    Separation = separation,
                    Rotation = rotation,
                    Speed = speed
                };
            }

            private float3 CalculateSeparation(int index, CrowdMovementInput input)
            {
                var radius = input.AvoidanceRadius;
                if (input.HasPath == 0 || radius <= 0f)
                    return float3.zero;

                var cell = (int2)math.floor(input.Position.xz / CellSize);
                var searchRadius = (int)math.ceil(radius / CellSize);
                var radiusSquared = radius * radius;
                var neighborsFound = 0;
                var separation = float3.zero;

                for (var x = cell.x - searchRadius; x <= cell.x + searchRadius; x++)
                {
                    for (var z = cell.y - searchRadius; z <= cell.y + searchRadius; z++)
                    {
                        if (!AgentsByCell.TryGetFirstValue(new int2(x, z), out var otherIndex, out var iterator))
                            continue;

                        do
                        {
                            if (otherIndex == index)
                                continue;

                            var other = Inputs[otherIndex];
                            var offset = input.Position - other.Position;
                            offset.y = 0f;
                            var distanceSquared = math.lengthsq(offset);
                            if (distanceSquared >= radiusSquared)
                                continue;

                            var distance = math.sqrt(distanceSquared);
                            if (distance <= 0.0001f)
                            {
                                var angle = ((input.InstanceId ^ other.InstanceId) & 1023) *
                                            (math.PI * 2f / 1024f);
                                offset = new float3(math.cos(angle), 0f, math.sin(angle));
                                if (input.InstanceId > other.InstanceId)
                                    offset = -offset;
                                distance = 0f;
                            }

                            separation += math.normalizesafe(offset) * (1f - distance / radius);
                            neighborsFound++;
                            if (neighborsFound >= MaxNeighbors)
                            {
                                return math.lengthsq(separation) > 1f
                                    ? math.normalizesafe(separation)
                                    : separation;
                            }
                        }
                        while (AgentsByCell.TryGetNextValue(out otherIndex, ref iterator));
                    }
                }

                return math.lengthsq(separation) > 1f
                    ? math.normalizesafe(separation)
                    : separation;
            }

            private static float MoveTowards(float current, float target, float maxDelta)
            {
                var difference = target - current;
                if (math.abs(difference) <= maxDelta)
                    return target;
                return current + math.sign(difference) * maxDelta;
            }
        }

        private sealed class FlowField
        {
            public readonly FieldKey Key;
            public readonly NativeArray<float> Costs;
            public readonly NativeArray<int> NextTriangle;
            public readonly NativeArray<int> Heap;
            public readonly NativeArray<int> HeapPositions;
            public readonly List<PendingRequest> Requests = new();
            public JobHandle Handle;
            public float LastUsedTime;
            public bool Complete;

            public FlowField(FieldKey key, int triangleCount, float time)
            {
                Key = key;
                Costs = new NativeArray<float>(triangleCount, Allocator.Persistent);
                NextTriangle = new NativeArray<int>(triangleCount, Allocator.Persistent);
                Heap = new NativeArray<int>(triangleCount, Allocator.Persistent);
                HeapPositions = new NativeArray<int>(triangleCount, Allocator.Persistent);
                LastUsedTime = time;
            }

            public void Dispose()
            {
                if (Costs.IsCreated)
                    Costs.Dispose();
                if (NextTriangle.IsCreated)
                    NextTriangle.Dispose();
                if (Heap.IsCreated)
                    Heap.Dispose();
                if (HeapPositions.IsCreated)
                    HeapPositions.Dispose();
            }
        }

        private sealed class PendingRequest
        {
            public int StartTriangle;
            public Vector3 Start;
            public Vector3 Goal;
            public int AreaMask;
            public Action<RecoilPathResult> Callback;
        }

        private sealed class PendingProjectionRequest
        {
            public Vector3 Start;
            public Vector3 Goal;
            public int AreaMask;
            public Action<RecoilPathResult> Callback;
        }

        public bool IsReady => _initialized;
        public int TriangleCount => _nativeTriangles.IsCreated ? _nativeTriangles.Length : 0;

        private void Start()
        {
            InitializeGraph();
        }

        private void Update()
        {
            CompleteProjectionJob();
            ScheduleProjectionJob();
            ProcessFlowFields();
        }

        private void LateUpdate()
        {
            using (CrowdMovementMarker.Auto())
                UpdateCrowdMovement();
        }

        private void OnDestroy()
        {
            if (_projectionHandle.IsCompleted == false && _activeProjectionRequests.Count > 0)
                _projectionHandle.Complete();

            if (_projectionInputs.IsCreated)
                _projectionInputs.Dispose();
            if (_projectionResults.IsCreated)
                _projectionResults.Dispose();
            CompletePendingCrowdMovement(false);
            DisposeCrowdJobData();
            for (var i = 0; i < _fieldOrder.Count; i++)
            {
                var field = _fieldOrder[i];
                if (!field.Complete)
                    field.Handle.Complete();
                field.Dispose();
            }

            if (_nativeTriangles.IsCreated)
                _nativeTriangles.Dispose();
            if (_nativeAreaCosts.IsCreated)
                _nativeAreaCosts.Dispose();
            if (_trianglesByCell.IsCreated)
                _trianglesByCell.Dispose();
        }

        private void DisposeCrowdJobData()
        {
            if (_crowdMovementInputs.IsCreated)
                _crowdMovementInputs.Dispose();
            if (_crowdMovementOutputs.IsCreated)
                _crowdMovementOutputs.Dispose();
            if (_crowdAgentsByCell.IsCreated)
                _crowdAgentsByCell.Dispose();

            _crowdMovementVersions = Array.Empty<int>();
            _crowdCapacity = 0;
        }

        private void EnsureCrowdJobCapacity(int count)
        {
            if (count <= _crowdCapacity)
                return;

            DisposeCrowdJobData();
            _crowdCapacity = Mathf.NextPowerOfTwo(Mathf.Max(4, count));
            var options = NativeArrayOptions.UninitializedMemory;
            _crowdMovementVersions = new int[_crowdCapacity];
            _crowdMovementInputs = new NativeArray<CrowdMovementInput>(_crowdCapacity, Allocator.Persistent, options);
            _crowdMovementOutputs = new NativeArray<CrowdMovementOutput>(_crowdCapacity, Allocator.Persistent, options);
            _crowdAgentsByCell = new NativeParallelMultiHashMap<int2, int>(_crowdCapacity, Allocator.Persistent);
        }

        public void RegisterAgent(RecoilNavMeshPathAgent agent)
        {
            if (agent != null && _registeredAgents.Add(agent))
                _agents.Add(agent);
        }

        public void UnregisterAgent(RecoilNavMeshPathAgent agent)
        {
            if (_registeredAgents.Remove(agent))
                _agents.Remove(agent);
        }

        public bool RequestPath(
            Vector3 start,
            Vector3 destination,
            int areaMask,
            Action<RecoilPathResult> callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            if (!_initialized)
            {
                callback(RecoilPathResult.Failure("The NavMesh path service is not initialized."));
                return false;
            }

            if (!NavMesh.SamplePosition(start, out var startHit, maxProjectionDistance, areaMask))
            {
                callback(RecoilPathResult.Failure(
                    $"Could not project the path start ({start}) onto the NavMesh within {maxProjectionDistance:0.##} units. Check agent movement and NavMesh coverage."));
                return false;
            }

            if (!NavMesh.SamplePosition(destination, out var goalHit, maxProjectionDistance, areaMask))
            {
                callback(RecoilPathResult.Failure(
                    $"Could not project the path destination ({destination}) onto the NavMesh within {maxProjectionDistance:0.##} units. Check target movement and NavMesh coverage."));
                return false;
            }

            _queuedProjectionRequests.Enqueue(new PendingProjectionRequest
            {
                Start = startHit.position,
                Goal = goalHit.position,
                AreaMask = areaMask,
                Callback = callback
            });
            return true;
        }

        private void EnsureProjectionCapacity(int count)
        {
            if (count <= _projectionCapacity)
                return;

            if (_projectionInputs.IsCreated)
                _projectionInputs.Dispose();
            if (_projectionResults.IsCreated)
                _projectionResults.Dispose();

            _projectionCapacity = Mathf.NextPowerOfTwo(Mathf.Max(4, count));
            _projectionInputs = new NativeArray<ProjectionInput>(
                _projectionCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            _projectionResults = new NativeArray<int>(
                _projectionCapacity,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
        }

        private void ScheduleProjectionJob()
        {
            if (_activeProjectionRequests.Count > 0 || _queuedProjectionRequests.Count == 0)
                return;

            var requestCount = _queuedProjectionRequests.Count;
            EnsureProjectionCapacity(requestCount * 2);
            _activeProjectionRequests.Clear();
            for (var i = 0; i < requestCount; i++)
            {
                var request = _queuedProjectionRequests.Dequeue();
                _activeProjectionRequests.Add(request);
                _projectionInputs[i * 2] = new ProjectionInput
                {
                    Position = ToFloat3(request.Start),
                    AreaMask = request.AreaMask
                };
                _projectionInputs[i * 2 + 1] = new ProjectionInput
                {
                    Position = ToFloat3(request.Goal),
                    AreaMask = request.AreaMask
                };
            }

            _projectionHandle = new FindProjectedTrianglesJob
            {
                Inputs = _projectionInputs,
                Triangles = _nativeTriangles,
                TrianglesByCell = _trianglesByCell,
                Results = _projectionResults,
                CellSize = _cellSize
            }.Schedule(requestCount * 2, 32);
        }

        private void CompleteProjectionJob()
        {
            if (_activeProjectionRequests.Count == 0 || !_projectionHandle.IsCompleted)
                return;

            _projectionHandle.Complete();
            for (var i = 0; i < _activeProjectionRequests.Count; i++)
            {
                var request = _activeProjectionRequests[i];
                var startTriangle = _projectionResults[i * 2];
                var goalTriangle = _projectionResults[i * 2 + 1];
                if (startTriangle < 0 || goalTriangle < 0)
                {
                    request.Callback(RecoilPathResult.Failure(
                        "Could not find a NavMesh triangle for the projected start or destination."));
                    continue;
                }

                if (startTriangle == goalTriangle)
                {
                    request.Callback(RecoilPathResult.Success(new[] { request.Start, request.Goal }));
                    continue;
                }

                var pathRequest = new PendingRequest
                {
                    StartTriangle = startTriangle,
                    Start = request.Start,
                    Goal = request.Goal,
                    AreaMask = request.AreaMask,
                    Callback = request.Callback
                };
                EnqueuePathRequest(new FieldKey(goalTriangle, request.AreaMask), pathRequest);
            }

            _activeProjectionRequests.Clear();
        }

        private void EnqueuePathRequest(FieldKey key, PendingRequest request)
        {
            if (!_fields.TryGetValue(key, out var field))
                field = GetOrCreateField(key);

            if (field == null)
            {
                QueueRequest(key, request);
                return;
            }

            field.LastUsedTime = Time.time;
            if (field.Complete)
                CompleteRequest(field, request);
            else
                field.Requests.Add(request);
        }

        private void InitializeGraph()
        {
            maxCachedFields = Mathf.Max(1, maxCachedFields);
            crowdCellSize = Mathf.Max(0.1f, crowdCellSize);
            maxAvoidanceNeighbors = Mathf.Max(1, maxAvoidanceNeighbors);
            crowdSimulationRate = Mathf.Max(1f, crowdSimulationRate);
            var triangulation = NavMesh.CalculateTriangulation();
            if (triangulation.vertices == null || triangulation.indices == null ||
                triangulation.indices.Length < 3)
            {
                Debug.LogError($"{nameof(RecoilNavMeshPathService)} could not find baked NavMesh triangles.", this);
                return;
            }

            var triangleCount = triangulation.indices.Length / 3;
            _edgesByTriangle = new List<Edge>[triangleCount];
            for (var i = 0; i < triangleCount; i++)
                _edgesByTriangle[i] = new List<Edge>(3);

            for (var i = 0; i < triangleCount; i++)
            {
                var index = i * 3;
                var area = triangulation.areas != null && i < triangulation.areas.Length
                    ? triangulation.areas[i]
                    : 0;
                _triangles.Add(new Triangle(
                    triangulation.vertices[triangulation.indices[index]],
                    triangulation.vertices[triangulation.indices[index + 1]],
                    triangulation.vertices[triangulation.indices[index + 2]],
                    area));
            }

            BuildAdjacency(triangulation);
            if (!BuildSpatialIndex())
                return;

            BuildNativeGraph();
            _nativeAreaCosts = new NativeArray<float>(32, Allocator.Persistent);
            for (var area = 0; area < _nativeAreaCosts.Length; area++)
                _nativeAreaCosts[area] = Mathf.Max(0.001f, NavMesh.GetAreaCost(area));
            _initialized = true;
        }

        private void BuildNativeGraph()
        {
            _nativeTriangles = new NativeArray<NavTriangleData>(
                _triangles.Count,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            for (var i = 0; i < _triangles.Count; i++)
            {
                var triangle = _triangles[i];
                var data = new NavTriangleData
                {
                    A = ToFloat3(triangle.A),
                    B = ToFloat3(triangle.B),
                    C = ToFloat3(triangle.C),
                    Center = ToFloat3(triangle.Center),
                    Area = triangle.Area,
                    Neighbor0 = -1,
                    Neighbor1 = -1,
                    Neighbor2 = -1
                };
                var edges = _edgesByTriangle[i];
                for (var edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
                    data.SetEdge(edgeIndex, edges[edgeIndex].Neighbor, ToFloat3(edges[edgeIndex].Portal));
                _nativeTriangles[i] = data;
            }

            _triangles.Clear();
            _triangles.TrimExcess();
            _edgesByTriangle = Array.Empty<List<Edge>>();
        }

        private static float3 ToFloat3(Vector3 value) =>
            new(value.x, value.y, value.z);

        private void UpdateCrowdMovement()
        {
            if (_crowdMovementPending && _crowdMovementHandle.IsCompleted)
                CompletePendingCrowdMovement(true);

            _crowdSimulationAccumulator += Time.deltaTime;
            var simulationInterval = 1f / crowdSimulationRate;
            if (_crowdSimulationAccumulator < simulationInterval)
                return;

            if (_crowdMovementPending)
            {
                _crowdSimulationAccumulator = Mathf.Min(_crowdSimulationAccumulator, simulationInterval);
                return;
            }

            var deltaTime = _crowdSimulationAccumulator;
            _crowdSimulationAccumulator = 0f;

            for (var i = _agents.Count - 1; i >= 0; i--)
            {
                var agent = _agents[i];
                if (agent == null || !agent.isActiveAndEnabled)
                {
                    _registeredAgents.Remove(agent);
                    _agents.RemoveAt(i);
                    continue;
                }
            }

            if (_agents.Count == 0)
                return;

            EnsureCrowdJobCapacity(_agents.Count);
            _crowdAgentsByCell.Clear();
            _pendingCrowdAgents.Clear();
            for (var i = 0; i < _agents.Count; i++)
            {
                var agent = _agents[i];
                var position = agent.transform.position;
                var hasPath = agent.PrepareMovement();
                var waypoint = agent.CurrentWaypoint;
                var rotation = agent.transform.rotation;
                _crowdMovementInputs[i] = new CrowdMovementInput
                {
                    Position = new float3(position.x, position.y, position.z),
                    Waypoint = new float3(waypoint.x, waypoint.y, waypoint.z),
                    Rotation = new quaternion(rotation.x, rotation.y, rotation.z, rotation.w),
                    CurrentSpeed = agent.CurrentSpeed,
                    MaxSpeed = agent.MaxSpeed,
                    Acceleration = agent.Acceleration,
                    TurnSpeedDegrees = agent.TurnSpeedDegrees,
                    AvoidanceStrength = agent.AvoidanceStrength,
                    AvoidanceRadius = agent.AvoidanceRadius,
                    DeltaTime = deltaTime,
                    InstanceId = agent.GetInstanceID(),
                    HasPath = (byte)(hasPath ? 1 : 0)
                };
                _crowdMovementVersions[i] = agent.MovementVersion;
                _pendingCrowdAgents.Add(agent);

                var cell = (int2)math.floor(new float2(position.x, position.z) / crowdCellSize);
                _crowdAgentsByCell.Add(cell, i);
            }

            _crowdMovementHandle = new SimulateCrowdMovementJob
            {
                AgentsByCell = _crowdAgentsByCell,
                Inputs = _crowdMovementInputs,
                Outputs = _crowdMovementOutputs,
                CellSize = crowdCellSize,
                MaxNeighbors = maxAvoidanceNeighbors
            }.Schedule(_pendingCrowdAgents.Count, 64);
            _crowdMovementPending = true;
        }

        private void CompletePendingCrowdMovement(bool applyResults)
        {
            if (!_crowdMovementPending)
                return;

            _crowdMovementHandle.Complete();
            if (applyResults)
            {
                for (var i = 0; i < _pendingCrowdAgents.Count; i++)
                {
                    var agent = _pendingCrowdAgents[i];
                    if (agent == null)
                        continue;

                    var output = _crowdMovementOutputs[i];
                    var pathMovement = output.PathMovement;
                    var separation = output.Separation;
                    var outputRotation = output.Rotation;
                    agent.ApplyMovement(
                        new Vector3(output.CandidatePosition.x, output.CandidatePosition.y, output.CandidatePosition.z),
                        new Vector3(pathMovement.x, pathMovement.y, pathMovement.z),
                        new Vector3(separation.x, separation.y, separation.z),
                        output.Speed,
                        new Quaternion(
                            outputRotation.value.x,
                            outputRotation.value.y,
                            outputRotation.value.z,
                            outputRotation.value.w),
                        _crowdMovementVersions[i]);
                }
            }

            _pendingCrowdAgents.Clear();
            _crowdMovementPending = false;
        }

        private void BuildAdjacency(NavMeshTriangulation triangulation)
        {
            var sharedEdges = new Dictionary<EdgeKey, EdgeReference>();
            for (var triangleIndex = 0; triangleIndex < _triangles.Count; triangleIndex++)
            {
                var index = triangleIndex * 3;
                var a = triangulation.vertices[triangulation.indices[index]];
                var b = triangulation.vertices[triangulation.indices[index + 1]];
                var c = triangulation.vertices[triangulation.indices[index + 2]];
                AddEdge(triangleIndex, a, b, sharedEdges);
                AddEdge(triangleIndex, b, c, sharedEdges);
                AddEdge(triangleIndex, c, a, sharedEdges);
            }
        }

        private void AddEdge(
            int triangleIndex,
            Vector3 a,
            Vector3 b,
            Dictionary<EdgeKey, EdgeReference> sharedEdges)
        {
            var key = new EdgeKey(new VertexKey(a), new VertexKey(b));
            var midpoint = (a + b) * 0.5f;
            if (sharedEdges.TryGetValue(key, out var shared))
            {
                if (shared.Triangle != triangleIndex)
                {
                    _edgesByTriangle[triangleIndex].Add(new Edge(shared.Triangle, shared.Midpoint));
                    _edgesByTriangle[shared.Triangle].Add(new Edge(triangleIndex, shared.Midpoint));
                }
            }
            else
            {
                sharedEdges.Add(key, new EdgeReference(triangleIndex, midpoint));
            }
        }

        private bool BuildSpatialIndex()
        {
            var totalArea = 0f;
            foreach (var triangle in _triangles)
            {
                var a = triangle.A;
                var b = triangle.B;
                var c = triangle.C;
                totalArea += Mathf.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) * 0.5f;
            }

            _cellSize = Mathf.Clamp(Mathf.Sqrt(totalArea / Mathf.Max(1, _triangles.Count)) * 2f, 2f, 32f);
            long entryCount = 0;
            for (var triangleIndex = 0; triangleIndex < _triangles.Count; triangleIndex++)
            {
                var triangle = _triangles[triangleIndex];
                var minCell = ToCell(triangle.Min);
                var maxCell = ToCell(triangle.Max);
                entryCount += (long)(maxCell.x - minCell.x + 1) *
                              (maxCell.y - minCell.y + 1);
                if (entryCount > int.MaxValue)
                {
                    Debug.LogError(
                        $"{nameof(RecoilNavMeshPathService)} spatial index exceeds supported capacity.",
                        this);
                    return false;
                }
            }

            _trianglesByCell = new NativeParallelMultiHashMap<int2, int>(
                Mathf.Max(1, (int)entryCount),
                Allocator.Persistent);
            for (var triangleIndex = 0; triangleIndex < _triangles.Count; triangleIndex++)
            {
                var triangle = _triangles[triangleIndex];
                var minCell = ToCell(triangle.Min);
                var maxCell = ToCell(triangle.Max);
                for (var x = minCell.x; x <= maxCell.x; x++)
                {
                    for (var z = minCell.y; z <= maxCell.y; z++)
                        _trianglesByCell.Add(new int2(x, z), triangleIndex);
                }
            }

            return true;
        }

        private int2 ToCell(Vector3 position) =>
            (int2)math.floor(new float2(position.x, position.z) / _cellSize);

        private static Vector3 ToVector3(float3 value) =>
            new(value.x, value.y, value.z);

        private FlowField GetOrCreateField(FieldKey key)
        {
            if (_fields.TryGetValue(key, out var existing))
                return existing;

            MakeFieldRoom();
            if (_fields.Count >= maxCachedFields)
                return null;

            var field = new FlowField(key, TriangleCount, Time.time);
            _fields.Add(key, field);
            _fieldOrder.Add(field);
            field.Handle = new BuildFlowFieldJob
            {
                Triangles = _nativeTriangles,
                AreaCosts = _nativeAreaCosts,
                Costs = field.Costs,
                NextTriangle = field.NextTriangle,
                Heap = field.Heap,
                HeapPositions = field.HeapPositions,
                GoalTriangle = key.GoalTriangle,
                AreaMask = key.AreaMask
            }.Schedule();
            return field;
        }

        private void QueueRequest(FieldKey key, PendingRequest request)
        {
            if (!_waitingRequests.TryGetValue(key, out var requests))
            {
                requests = new List<PendingRequest>();
                _waitingRequests.Add(key, requests);
                _waitingOrder.Enqueue(key);
            }

            requests.Add(request);
        }

        private void TrimFieldCache()
        {
            while (_fields.Count > maxCachedFields && TryEvictLeastRecentlyUsedField())
            {
            }
        }

        private void MakeFieldRoom()
        {
            while (_fields.Count >= maxCachedFields && TryEvictLeastRecentlyUsedField())
            {
            }
        }

        private bool TryEvictLeastRecentlyUsedField()
        {
            FlowField oldest = null;
            for (var i = 0; i < _fieldOrder.Count; i++)
            {
                var candidate = _fieldOrder[i];
                if (!candidate.Complete || candidate.Requests.Count != 0)
                    continue;
                if (oldest == null || candidate.LastUsedTime < oldest.LastUsedTime)
                    oldest = candidate;
            }

            if (oldest == null)
                return false;

            _fields.Remove(oldest.Key);
            _fieldOrder.Remove(oldest);
            oldest.Dispose();
            return true;
        }

        private void ProcessFlowFields()
        {
            StartQueuedFields();
            for (var i = 0; i < _fieldOrder.Count; i++)
            {
                var field = _fieldOrder[i];
                if (!field.Complete && field.Handle.IsCompleted)
                    CompleteField(field);
            }

            StartQueuedFields();
        }

        private void StartQueuedFields()
        {
            if (_waitingOrder.Count == 0)
                return;

            TrimFieldCache();
            MakeFieldRoom();
            while (_fields.Count < maxCachedFields && _waitingOrder.Count > 0)
            {
                var key = _waitingOrder.Dequeue();
                if (!_waitingRequests.TryGetValue(key, out var requests))
                    continue;

                _waitingRequests.Remove(key);
                var field = GetOrCreateField(key);
                if (field == null)
                {
                    _waitingRequests.Add(key, requests);
                    _waitingOrder.Enqueue(key);
                    return;
                }

                field.Requests.AddRange(requests);
            }
        }

        private void CompleteField(FlowField field)
        {
            field.Handle.Complete();
            field.Complete = true;
            var requests = field.Requests.ToArray();
            field.Requests.Clear();
            for (var i = 0; i < requests.Length; i++)
                CompleteRequest(field, requests[i]);
        }

        private void CompleteRequest(FlowField field, PendingRequest request)
        {
            if (float.IsPositiveInfinity(field.Costs[request.StartTriangle]))
            {
                request.Callback(RecoilPathResult.Failure("No route exists between the requested NavMesh regions."));
                return;
            }

            var points = new List<Vector3> { request.Start };
            var current = request.StartTriangle;
            var traversed = 0;
            while (current != field.Key.GoalTriangle && traversed++ < TriangleCount)
            {
                var next = field.NextTriangle[current];
                if (next < 0)
                {
                    request.Callback(RecoilPathResult.Failure("The shared path field contains no route to the destination."));
                    return;
                }

                if (!TryGetPortal(current, next, out var portal))
                {
                    request.Callback(RecoilPathResult.Failure("The NavMesh corridor contains disconnected triangles."));
                    return;
                }

                points.Add(portal);
                current = next;
            }

            if (current != field.Key.GoalTriangle)
            {
                request.Callback(RecoilPathResult.Failure("The NavMesh route exceeded the triangle traversal limit."));
                return;
            }

            points.Add(request.Goal);
            request.Callback(RecoilPathResult.Success(SmoothPath(points, request.AreaMask, 12)));
        }

        private bool TryGetPortal(int from, int to, out Vector3 portal)
        {
            var triangle = _nativeTriangles[from];
            for (var i = 0; i < 3; i++)
            {
                if (triangle.GetNeighbor(i) != to)
                    continue;

                portal = ToVector3(triangle.GetPortal(i));
                return true;
            }

            portal = default;
            return false;
        }

        private static Vector3[] SmoothPath(List<Vector3> points, int areaMask, int lookahead)
        {
            if (points.Count <= 2)
                return points.ToArray();

            var smoothed = new List<Vector3> { points[0] };
            var anchor = 0;
            while (anchor < points.Count - 1)
            {
                var furthest = anchor + 1;
                var furthestCandidate = Mathf.Min(points.Count - 1, anchor + lookahead);
                for (var candidate = furthestCandidate; candidate > anchor + 1; candidate--)
                {
                    if (NavMesh.Raycast(points[anchor], points[candidate], out _, areaMask))
                        continue;

                    furthest = candidate;
                    break;
                }

                smoothed.Add(points[furthest]);
                anchor = furthest;
            }

            return smoothed.ToArray();
        }

    }

    public sealed class RecoilPathResult
    {
        public bool Succeeded { get; }
        public IReadOnlyList<Vector3> Points { get; }
        public string Error { get; }

        private RecoilPathResult(bool succeeded, IReadOnlyList<Vector3> points, string error)
        {
            Succeeded = succeeded;
            Points = points;
            Error = error;
        }

        public static RecoilPathResult Success(IReadOnlyList<Vector3> points) =>
            new(true, points, null);

        public static RecoilPathResult Failure(string error) =>
            new(false, Array.Empty<Vector3>(), error);
    }
}
