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
using Unity.Entities;
using Unity.Burst.Intrinsics;
using Unity.Collections.LowLevel.Unsafe;

namespace RecoilPathfinding
{
    [DefaultExecutionOrder(-100)]
    public sealed class RecoilNavMeshPathService : MonoBehaviour
    {
        private static readonly ProfilerMarker CrowdMovementMarker =
            new ProfilerMarker("RecoilPathfinding.CrowdMovement");
        private static readonly ProfilerMarker ChunkApplyMarker =
            new ProfilerMarker("RecoilPathfinding.ApplyMovementChunks");
        private static readonly ProfilerMarker ChunkPrepareMarker =
            new ProfilerMarker("RecoilPathfinding.PrepareMovementChunks");
        private static readonly ProfilerMarker ProjectionMarker = new("RecoilPathfinding.Projection");
        private static readonly ProfilerMarker FieldMarker = new("RecoilPathfinding.FieldMaintenance");
        private static readonly ProfilerMarker DeliveryMarker = new("RecoilPathfinding.PathDelivery");
        public float LastProjectionMilliseconds { get; private set; }
        public float LastPathDeliveryMilliseconds { get; private set; }

        [SerializeField, Min(0.1f)] private float maxProjectionDistance = 5f;
        [SerializeField] private RecoilTerrainMapData terrainMap = null;
        [SerializeField] private bool useProjectTerrainMap = true;
        private RecoilTerrainMapData _activeTerrainMap;
        private RecoilTerrainHeightField _terrainHeightField;
        public bool UsingTerrainMap => _activeTerrainMap != null;
        internal RecoilTerrainHeightField TerrainHeightField => _terrainHeightField;
        public int TerrainLeafCount { get; private set; }
        public string SurfaceBackend => UsingTerrainMap ? "Terrain quadtree" : "NavMesh triangles";
        [SerializeField, Min(1)] private int maxCachedFields = 16;
        [SerializeField, Min(1)] private int maxPathCompletionsPerFrame = 64;
        [SerializeField, Min(0.1f)] private float pathCompletionBudgetMilliseconds = 2f;
        [SerializeField, Min(1)] private int maxProjectionRequestsPerFrame = 128;
        private readonly List<Vector3> _pathMidpoints = new(128);
        private readonly List<RecoilPathPortal> _pathPortals = new(128);
        private readonly List<Vector3> _pathResult = new(32);
        private readonly Queue<ReadyRequest> _readyPaths = new();
        private readonly Stack<FlowField> _spareFields = new();
        private NativeArray<RecoilPathPortal> _nativePortals;
        private NativeArray<byte> _nativePortalValid;
        public int LastPathCompletions { get; private set; }
        public int PendingPathCompletions => _readyPaths.Count;
        public int FlowFieldAllocationCount { get; private set; }
        public int FlowFieldReuseCount { get; private set; }
        public int LastCancelledPaths { get; private set; }
        public int SharedCorridorHits { get; private set; }
        public int SharedCorridorSuffixHits { get; private set; }
        [SerializeField, Min(0.1f)] private float crowdCellSize = 2.5f;
        [SerializeField, Min(1)] private int maxAvoidanceNeighbors = 8;
        [SerializeField, Min(1f)] private float crowdSimulationRate = 30f;
        [SerializeField] private bool showSamplePaths = true;
        [SerializeField, Range(1, 24)] private int debugPathSamples = 8;
        [SerializeField, Min(0)] private int debugSampleOffset = 0;
        [SerializeField] private bool showSampleTriangles = true;
        private Vector3 _lastRejectedFrom;
        private Vector3 _lastRejectedTo;
        private bool _hasRejectedFunnel;

        private readonly List<Triangle> _triangles = new();
        private List<Edge>[] _edgesByTriangle = Array.Empty<List<Edge>>();
        private NativeArray<NavTriangleData> _nativeTriangles;
        private NativeArray<float> _nativeAreaCosts;
        private NativeParallelMultiHashMap<int2, int> _trianglesByCell;
        private readonly Queue<PendingProjectionRequest> _queuedProjectionRequests = new();
        private readonly List<PendingProjectionRequest> _activeProjectionRequests = new();
        private NativeArray<ProjectionInput> _projectionInputs;
        private NativeArray<int> _projectionResults;
        private NativeArray<float3> _projectedPositions;
        private NativeArray<byte> _projectionDirect;
        private JobHandle _projectionHandle;
        private int _projectionCapacity;
        private readonly List<RecoilUnitSimulation> _agents = new();
        private readonly HashSet<RecoilUnitSimulation> _registeredAgents = new();
        private readonly List<RecoilUnitSimulation> _pendingCrowdAgents = new();
        private readonly List<RecoilUnitSimulation> _movementRoster = new();
        private bool _movementRosterDirty = true;
        private bool _movementRosterSingleWorld;
        private float _nextMovementRosterAudit;
        public int MovementRosterBuildCount { get; private set; }
        public float LastMovementScheduleMilliseconds { get; private set; }
        public float LastMovementWaitMilliseconds { get; private set; }
        public float LastMovementApplyMilliseconds { get; private set; }
        private NativeArray<float> _crowdMaxBodyRadius;
        private NativeArray<CrowdMovementInput> _crowdMovementInputs;
        private NativeArray<CrowdMovementOutput> _crowdMovementOutputs;
        private NativeList<int> _crowdFallbackIndices;
        private NativeArray<int> _crowdResultSummary;
        private NativeParallelMultiHashMap<int2, int> _crowdAgentsByCell;
        private NativeArray<int2> _crowdPreviousCells;
        private NativeArray<byte> _crowdPreviousActive;
        private NativeArray<int> _crowdGridMetadata;
        public int LastCrowdGridMutations { get; private set; }
        private JobHandle _crowdMovementHandle;
        private int _crowdCapacity;
        private NativeParallelHashMap<Entity, int> _crowdEntityIndices;
        private World _movementWorld;
        private EntityQuery _movementApplyQuery;
        private EntityQuery _movementPrepareQuery;
        private bool _hasMovementApplyQuery;
        private bool _useChunkApply;
        private readonly Dictionary<FieldKey, FlowField> _fields = new();
        private readonly List<FlowField> _fieldOrder = new();
        private readonly Dictionary<FieldKey, List<PendingRequest>> _waitingRequests = new();
        private readonly Queue<FieldKey> _waitingOrder = new();
        private float _cellSize = 8f;
        private float _crowdSimulationAccumulator;
        private bool _crowdMovementPending;
        private bool _initialized;
        private bool _clockDriven;
        private bool _hasStarted;
        private float _fixedStepDelta;

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
            public RecoilTerrainHeightField TerrainHeights;
            [ReadOnly] public NativeArray<ProjectionInput> Inputs;
            [ReadOnly] public NativeArray<NavTriangleData> Triangles;
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> TrianglesByCell;
            [WriteOnly] public NativeArray<int> Results;
            [WriteOnly] public NativeArray<float3> Positions;
            public float CellSize;
            public float MaxDistance;

            public void Execute(int index)
            {
                Find(Inputs[index], out var bestTriangle, out var bestPosition);
                Results[index] = bestTriangle;
                Positions[index] = bestPosition;
            }

            public void Find(ProjectionInput input, out int bestTriangle, out float3 bestPosition)
            {
                if (TerrainHeights.TryGetTriangleRange(input.Position.xz, out var first, out var end))
                {
                    for (var i = first; i < end; i++)
                    {
                        var t = Triangles[i];
                        if (!IsAreaAllowed(input.AreaMask, t.Area)) continue;
                        var p = ClosestPointOnTriangle(new float3(input.Position.x, 0f, input.Position.z),
                            new float3(t.A.x, 0f, t.A.z), new float3(t.B.x, 0f, t.B.z), new float3(t.C.x, 0f, t.C.z));
                        if (math.distancesq(p.xz, input.Position.xz) > 0.00000001f) continue;
                        p.y = TerrainHeights.Height(p.xz);
                        if (math.distancesq(p, input.Position) > MaxDistance * MaxDistance) break;
                        bestTriangle = i; bestPosition = p;
                        return;
                    }
                }
                var cell = (int2)math.floor(input.Position.xz / CellSize);
                var bestDistance = MaxDistance * MaxDistance;
                bestTriangle = -1;
                bestPosition = input.Position;
                var radius = math.max(1, (int)math.ceil(MaxDistance / CellSize));
                for (var x = cell.x - radius; x <= cell.x + radius; x++)
                {
                    for (var z = cell.y - radius; z <= cell.y + radius; z++)
                    {
                        if (!TrianglesByCell.TryGetFirstValue(new int2(x, z), out var candidateIndex, out var iterator))
                            continue;

                        do
                        {
                            var triangle = Triangles[candidateIndex];
                            if (!IsAreaAllowed(input.AreaMask, triangle.Area))
                                continue;

                            var closest = TerrainHeights.IsCreated
                                ? ClosestPointOnTriangle(new float3(input.Position.x, 0f, input.Position.z),
                                    new float3(triangle.A.x, 0f, triangle.A.z), new float3(triangle.B.x, 0f, triangle.B.z),
                                    new float3(triangle.C.x, 0f, triangle.C.z))
                                : ClosestPointOnTriangle(input.Position, triangle.A, triangle.B, triangle.C);
                            if (TerrainHeights.IsCreated) closest.y = TerrainHeights.Height(closest.xz);
                            var distance = math.lengthsq(closest - input.Position);
                            if (distance >= bestDistance)
                                continue;

                            bestDistance = distance;
                            bestTriangle = candidateIndex;
                            bestPosition = closest;
                        }
                        while (TrianglesByCell.TryGetNextValue(out candidateIndex, ref iterator));
                    }
                }

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
        private struct CheckDirectPathsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ProjectionInput> Inputs;
            [ReadOnly] public NativeArray<int> TriangleIndices;
            [ReadOnly] public NativeArray<float3> Positions;
            [ReadOnly] public NativeArray<NavTriangleData> Triangles;
            public RecoilTerrainHeightField TerrainHeights;
            [WriteOnly] public NativeArray<byte> Direct;
            public byte TestConnectedSegment;
            public void Execute(int index)
            {
                var start = TriangleIndices[index * 2];
                var goal = TriangleIndices[index * 2 + 1];
                Direct[index] = 0;
                if (start < 0 || goal < 0) return;
                if (start == goal) { Direct[index] = 1; return; }
                if (TestConnectedSegment == 0) return;
                var trace = new SimulateCrowdMovementJob { Triangles = Triangles, TerrainHeights = TerrainHeights };
                if (trace.TryProjectStep(start, Positions[index * 2], Positions[index * 2 + 1],
                    Inputs[index * 2].AreaMask, out _, out _)) Direct[index] = 1;
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
            public byte Inactive;
            public int MovementVersion;
            public float BodyRadius;
            public float Mass;
            public byte PushResistant;
            public int SurfaceTriangle;
            public int AreaMask;
            public float VerticalOffset;
            public float3 Position;
            public float3 Waypoint;
            public float3 NextWaypoint;
            public int WaypointIndex;
            public byte HasNextWaypoint;
            public byte NextHasNextWaypoint;
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

        [BurstCompile]
        private struct ResetCrowdInputsJob : IJobParallelFor
        {
            public NativeArray<CrowdMovementInput> Inputs;
            public void Execute(int index) => Inputs[index] = new CrowdMovementInput { Inactive = 1 };
        }

        [BurstCompile]
        private struct PrepareCrowdMovementJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle Entities;
            [ReadOnly] public ComponentTypeHandle<RecoilUnitPose> Poses;
            [ReadOnly] public ComponentTypeHandle<RecoilUnitMovementSettings> Settings;
            public ComponentTypeHandle<RecoilUnitNavigation> Navigations;
            [ReadOnly] public BufferTypeHandle<RecoilUnitWaypoint> Waypoints;
            [ReadOnly] public NativeParallelHashMap<Entity, int> Indices;
            [NativeDisableParallelForRestriction] public NativeArray<CrowdMovementInput> Inputs;
            public NativeParallelMultiHashMap<int2, int>.ParallelWriter Cells;
            public float DeltaTime;
            public float CellSize;
            public byte SkipGridBuild;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetNativeArray(Entities);
                var poses = chunk.GetNativeArray(ref Poses);
                var settings = chunk.GetNativeArray(ref Settings);
                var navigations = chunk.GetNativeArray(ref Navigations);
                var paths = chunk.GetBufferAccessor(ref Waypoints);
                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out var i))
                {
                    if (!Indices.TryGetValue(entities[i], out var index)) continue;
                    var navigation = navigations[i];
                    if (navigation.Active == 0) { Inputs[index] = new CrowdMovementInput { Inactive = 1 }; continue; }
                    var pose = poses[i];
                    var config = settings[i];
                    var path = paths[i];
                    var oldIndex = navigation.WaypointIndex;
                    while (navigation.WaypointIndex < path.Length &&
                        math.distancesq(pose.Position, path[navigation.WaypointIndex].Position) <=
                        config.WaypointTolerance * config.WaypointTolerance)
                        navigation.WaypointIndex++;
                    if (oldIndex != navigation.WaypointIndex) navigations[i] = navigation;
                    var hasPath = navigation.WaypointIndex < path.Length;
                    Inputs[index] = new CrowdMovementInput
                    {
                        MovementVersion = navigation.MovementVersion, BodyRadius = config.BodyRadius,
                        Mass = config.Mass, PushResistant = config.PushResistant,
                        SurfaceTriangle = navigation.SurfaceTriangle, AreaMask = config.AreaMask,
                        VerticalOffset = config.VerticalOffset, Position = pose.Position,
                        Waypoint = hasPath ? path[navigation.WaypointIndex].Position : pose.Position,
                        WaypointIndex = navigation.WaypointIndex,
                        NextWaypoint = navigation.WaypointIndex + 1 < path.Length ? path[navigation.WaypointIndex + 1].Position : pose.Position,
                        HasNextWaypoint = (byte)(navigation.WaypointIndex + 1 < path.Length ? 1 : 0),
                        NextHasNextWaypoint = (byte)(navigation.WaypointIndex + 2 < path.Length ? 1 : 0),
                        Rotation = pose.Rotation, CurrentSpeed = pose.Speed,
                        MaxSpeed = config.MaxSpeed, Acceleration = config.Acceleration,
                        TurnSpeedDegrees = config.TurnSpeedDegrees, AvoidanceStrength = config.AvoidanceStrength,
                        AvoidanceRadius = config.AvoidanceRadius, DeltaTime = DeltaTime,
                        InstanceId = entities[i].Index, HasPath = (byte)(hasPath ? 1 : 0)
                    };
                    if (SkipGridBuild == 0) Cells.Add((int2)math.floor(pose.Position.xz / CellSize), index);
                }
            }
        }

        [BurstCompile]
        private struct UpdateCrowdGridJob : IJob
        {
            [ReadOnly] public NativeArray<CrowdMovementInput> Inputs;
            public NativeParallelMultiHashMap<int2, int> Cells;
            public NativeArray<int2> PreviousCells;
            public NativeArray<byte> PreviousActive;
            public NativeArray<int> Metadata;
            public NativeArray<float> MaxBodyRadius;
            public int Count;
            public float CellSize;

            public void Execute()
            {
                var mutations = 0;
                var maxRadius = 0f;
                for (var i = 0; i < math.max(Count, Metadata[0]); i++)
                {
                    var active = i < Count && Inputs[i].Inactive == 0;
                    if (active) maxRadius = math.max(maxRadius, Inputs[i].BodyRadius);
                    var next = active ? (int2)math.floor(Inputs[i].Position.xz / CellSize) : int2.zero;
                    var wasActive = PreviousActive[i] != 0;
                    if (active && wasActive && math.all(next == PreviousCells[i])) continue;
                    if (wasActive) { Cells.Remove(PreviousCells[i], i); mutations++; }
                    if (active) { Cells.Add(next, i); PreviousCells[i] = next; mutations++; }
                    PreviousActive[i] = (byte)(active ? 1 : 0);
                }
                Metadata[0] = Count;
                Metadata[1] = mutations;
                if (MaxBodyRadius.IsCreated) MaxBodyRadius[0] = maxRadius;
            }
        }

        private struct CrowdMovementOutput
        {
            public byte AdvanceCorner;
            public float PlannedDistance;
            public float ContactDistance;
            public byte CrossedPortal;
            public int SurfaceTriangle;
            public byte SurfaceProjected;
            public byte TerrainRecovery; // 1 = recovered in worker, 2 = held because no valid surface exists.
            public float3 CandidatePosition;
            public float3 PathMovement;
            public float3 Separation;
            public quaternion Rotation;
            public float Speed;
        }

        [BurstCompile]
        private struct CollectCrowdResultsJob : IJob
        {
            [ReadOnly] public NativeArray<CrowdMovementInput> Inputs;
            [ReadOnly] public NativeArray<CrowdMovementOutput> Outputs;
            public NativeList<int> Fallback;
            public NativeArray<int> Summary;
            public int Count;
            public void Execute()
            {
                Fallback.Clear();
                var fast = 0;
                var crossed = 0;
                var recovered = 0;
                var held = 0;
                for (var i = 0; i < Count; i++)
                {
                    if (Inputs[i].Inactive != 0) continue;
                    var output = Outputs[i];
                    crossed += output.CrossedPortal;
                    if (output.TerrainRecovery == 1) recovered++;
                    if (output.TerrainRecovery == 2) held++;
                    if (output.SurfaceProjected != 0) fast++;
                    else Fallback.AddNoResize(i);
                }
                Summary[0] = fast; Summary[1] = Fallback.Length; Summary[2] = crossed;
                if (Summary.Length >= 5) { Summary[3] = recovered; Summary[4] = held; }
            }
        }

        [BurstCompile]
        private struct ApplyCrowdMovementJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle Entities;
            public ComponentTypeHandle<RecoilUnitPose> Poses;
            public ComponentTypeHandle<RecoilUnitNavigation> Navigations;
            [ReadOnly] public NativeParallelHashMap<Entity, int> Indices;
            [ReadOnly] public NativeArray<CrowdMovementInput> Inputs;
            [ReadOnly] public NativeArray<CrowdMovementOutput> Outputs;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetNativeArray(Entities);
                var poses = chunk.GetNativeArray(ref Poses);
                var navigations = chunk.GetNativeArray(ref Navigations);
                var iterator = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (iterator.NextEntityIndex(out var i))
                {
                    if (!Indices.TryGetValue(entities[i], out var index)) continue;
                    var input = Inputs[index];
                    if (input.Inactive != 0) continue;
                    var output = Outputs[index];
                    var navigation = navigations[i];
                    if (output.SurfaceProjected == 0 || navigation.Active == 0 ||
                        navigation.MovementVersion != input.MovementVersion) continue;
                    var pose = poses[i];
                    var velocity = input.DeltaTime > 0f ? (output.CandidatePosition - pose.Position) / input.DeltaTime : float3.zero;
                    if (math.any(pose.Position != output.CandidatePosition) || math.any(pose.Velocity != velocity) ||
                        math.any(pose.Rotation.value != output.Rotation.value) || pose.Speed != output.Speed)
                    {
                        pose.Position = output.CandidatePosition; pose.Velocity = velocity;
                        pose.Rotation = output.Rotation; pose.Speed = output.Speed;
                        poses[i] = pose;
                    }
                    if (navigation.SurfaceTriangle != output.SurfaceTriangle)
                    {
                        navigation.SurfaceTriangle = output.SurfaceTriangle;
                        navigations[i] = navigation;
                    }
                    if (output.AdvanceCorner != 0 && navigation.WaypointIndex == input.WaypointIndex)
                    {
                        navigation.WaypointIndex++;
                        navigations[i] = navigation;
                    }
                }
            }
        }

        [BurstCompile]
        private struct SimulateCrowdMovementJob : IJobParallelFor
        {
            public RecoilTerrainHeightField TerrainHeights;
            [ReadOnly] public NativeArray<NavTriangleData> Triangles;
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> TrianglesByCell;
            public float TriangleCellSize;
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> AgentsByCell;
            [ReadOnly] public NativeArray<CrowdMovementInput> Inputs;
            [WriteOnly] public NativeArray<CrowdMovementOutput> Outputs;
            public float CellSize;
            public int MaxNeighbors;
            public float MaxBodyRadius;
            [ReadOnly] public NativeArray<float> BodyRadiusLimit;

            public void Execute(int index)
            {
                var input = Inputs[index];
                if (input.Inactive != 0) { Outputs[index] = default; return; }
                var advanceCorner = (byte)0;
                if (input.HasPath != 0 && input.HasNextWaypoint != 0 && CanSkipCorner(input))
                {
                    input.Waypoint = input.NextWaypoint;
                    input.HasNextWaypoint = input.NextHasNextWaypoint;
                    advanceCorner = 1;
                }
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
                    var desiredSpeed = input.HasNextWaypoint == 0 && distance <= brakingDistance
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

                // Positional contact resolution is independent of steering and applies to idle units too.
                var plannedDistance = math.distance(input.Position, candidatePosition);
                var correction = ResolveContacts(index, input, candidatePosition);
                candidatePosition += correction;
                var contactDistance = math.distance(input.Position, candidatePosition);
                var triangleIndex = input.SurfaceTriangle;
                var projected = (byte)0;
                var crossedPortal = (byte)0;
                var terrainRecovery = (byte)0;
                if (input.HasPath == 0 && math.lengthsq(correction) < 0.00000001f)
                {
                    // Idle units remain obstacles, but need no surface query or rotation update.
                    projected = 1;
                    speed = 0f;
                }
                else
                {
                    var groundStart = input.Position - new float3(0f, input.VerticalOffset, 0f);
                    if (!ContainsSurface(triangleIndex, groundStart, input.AreaMask, out _))
                    {
                        triangleIndex = FindStartingTriangle(groundStart, input.AreaMask);
                        var cell = (int2)math.floor(groundStart.xz / TriangleCellSize);
                        if (triangleIndex < 0 && TrianglesByCell.TryGetFirstValue(cell, out var candidate, out var iterator))
                        {
                            do
                            {
                                if (!ContainsSurface(candidate, groundStart, input.AreaMask, out _)) continue;
                                triangleIndex = candidate;
                                break;
                            } while (TrianglesByCell.TryGetNextValue(out candidate, ref iterator));
                        }
                    }
                    var groundCandidate = candidatePosition - new float3(0f, input.VerticalOffset, 0f);
                    if (TryProjectStep(triangleIndex, groundStart, groundCandidate, input.AreaMask,
                        out var destinationTriangle, out var surface))
                    {
                        crossedPortal = (byte)(destinationTriangle != triangleIndex ? 1 : 0);
                        triangleIndex = destinationTriangle;
                        candidatePosition = surface + new float3(0f, input.VerticalOffset, 0f);
                        projected = 1;
                    }
                    else if (input.HasPath != 0 && math.lengthsq(separation + correction) > 0.0001f &&
                        TryProjectStep(triangleIndex, groundStart, groundStart + pathMovement, input.AreaMask,
                            out destinationTriangle, out surface))
                    {
                        // Avoidance may push a valid route outside the surface. Resolve the unsteered
                        // route in the worker before asking the managed NavMesh bridge to do it again.
                        crossedPortal = (byte)(destinationTriangle != triangleIndex ? 1 : 0);
                        triangleIndex = destinationTriangle;
                        candidatePosition = surface + new float3(0f, input.VerticalOffset, 0f);
                        projected = 1;
                    }
                    else if (TryClampStep(triangleIndex, groundStart, groundCandidate, input.AreaMask,
                        out destinationTriangle, out surface))
                    {
                        // A real surface boundary is resolved in native data, without a managed raycast.
                        crossedPortal = (byte)(destinationTriangle != triangleIndex ? 1 : 0);
                        triangleIndex = destinationTriangle;
                        candidatePosition = surface + new float3(0f, input.VerticalOffset, 0f);
                        projected = 1;
                    }
                }
                if (projected == 0 && TerrainHeights.IsCreated)
                {
                    var groundStart = input.Position - new float3(0f, input.VerticalOffset, 0f);
                    var projection = new FindProjectedTrianglesJob
                    { TerrainHeights = TerrainHeights, Triangles = Triangles, TrianglesByCell = TrianglesByCell,
                        CellSize = TriangleCellSize, MaxDistance = 1f };
                    projection.Find(new ProjectionInput { Position = groundStart, AreaMask = input.AreaMask },
                        out var startTriangle, out var startSurface);
                    var goal = candidatePosition - new float3(0f, input.VerticalOffset, 0f);
                    var valid = TryProjectStep(startTriangle, startSurface, goal, input.AreaMask, out var next, out var surface);
                    if (!valid && math.lengthsq(separation + correction) > 0.0001f)
                        valid = TryProjectStep(startTriangle, startSurface, startSurface + pathMovement, input.AreaMask, out next, out surface);
                    if (!valid) valid = TryClampStep(startTriangle, startSurface, goal, input.AreaMask, out next, out surface);
                    // A failed recovery holds position; the main thread must not repeat the same search.
                    candidatePosition = valid ? surface + new float3(0f, input.VerticalOffset, 0f) : input.Position;
                    speed = valid ? speed : 0f;
                    triangleIndex = valid ? next : input.SurfaceTriangle;
                    terrainRecovery = (byte)(valid ? 1 : 2);
                    projected = 1;
                }
                Outputs[index] = new CrowdMovementOutput
                {
                    AdvanceCorner = advanceCorner,
                    PlannedDistance = plannedDistance,
                    ContactDistance = contactDistance,
                    CrossedPortal = crossedPortal,
                    SurfaceTriangle = triangleIndex,
                    SurfaceProjected = projected,
                    TerrainRecovery = terrainRecovery,
                    CandidatePosition = candidatePosition,
                    PathMovement = pathMovement,
                    Separation = separation + correction,
                    Rotation = rotation,
                    Speed = speed
                };
            }

            private int FindStartingTriangle(float3 start, int mask)
            {
                if (TerrainHeights.TryGetTriangleRange(start.xz, out var first, out var end))
                    for (var i = first; i < end; i++)
                        if (ContainsSurface(i, start, mask, out _)) return i;
                return -1;
            }

            public bool TryProjectStep(int triangle, float3 start, float3 goal, int mask,
                out int destinationTriangle, out float3 surface)
            {
                destinationTriangle = triangle;
                // Convexity guarantees that a segment inside one triangle cannot cross a boundary.
                if (ContainsSurface(triangle, goal, mask, out surface, false, false)) return true;
                return TryCrossPortal(triangle, start, goal, mask, out destinationTriangle, out surface);
            }

            public bool CanSkipCorner(CrowdMovementInput input)
            {
                var lookahead = math.max(0.75f, math.max(input.BodyRadius * 2f, input.CurrentSpeed * 0.5f));
                if (math.distancesq(input.Position.xz, input.Waypoint.xz) > lookahead * lookahead) return false;
                var offset = new float3(0f, input.VerticalOffset, 0f);
                var start = input.Position - offset;
                var goal = input.NextWaypoint - offset;
                var triangle = input.SurfaceTriangle;
                if (!ContainsSurface(triangle, start, input.AreaMask, out _))
                {
                    triangle = FindStartingTriangle(start, input.AreaMask);
                    var cell = (int2)math.floor(start.xz / TriangleCellSize);
                    if (triangle >= 0)
                        return ContainsSurface(triangle, goal, input.AreaMask, out _, false, false) ||
                            TryCrossPortal(triangle, start, goal, input.AreaMask, out _, out _);
                    if (!TrianglesByCell.TryGetFirstValue(cell, out var candidate, out var iterator)) return false;
                    do
                    {
                        if (!ContainsSurface(candidate, start, input.AreaMask, out _)) continue;
                        triangle = candidate;
                        break;
                    } while (TrianglesByCell.TryGetNextValue(out candidate, ref iterator));
                }
                if (triangle < 0) return false;
                return ContainsSurface(triangle, goal, input.AreaMask, out _, false, false) ||
                    TryCrossPortal(triangle, start, goal, input.AreaMask, out _, out _);
            }

            private struct SurfaceVisit
            {
                public int Triangle;
                public float Enter;
                public float Exit;
            }

            private bool TryCrossPortal(int current, float3 start, float3 goal, int mask,
                out int next, out float3 surface)
                => TraceSurface(current, start, goal, mask, false, out next, out surface);

            public bool TryClampStep(int current, float3 start, float3 goal, int mask,
                out int next, out float3 surface)
                => TraceSurface(current, start, goal, mask, true, out next, out surface);

            private bool TraceSurface(int current, float3 start, float3 goal, int mask, bool clampBoundary,
                out int next, out float3 surface)
            {
                next = -1;
                surface = goal;
                if (!ContainsSurface(current, start, mask, out _) ||
                    !ClipSegment(Triangles[current], start, goal, out var enter, out var exit)) return false;
                // At a shared vertex a step may traverse several triangles, including zero-length
                // intervals. Walk connected triangles whose segment intervals overlap, never jump
                // directly to an arbitrary triangle containing the destination.
                var visits = new FixedList4096Bytes<SurfaceVisit>();
                var furthest = new SurfaceVisit { Triangle = current, Enter = enter, Exit = exit };
                var truncated = false;
                visits.Add(new SurfaceVisit { Triangle = current, Enter = enter, Exit = exit });
                for (var head = 0; head < visits.Length; head++)
                {
                    var visit = visits[head];
                    if (visit.Exit > furthest.Exit) furthest = visit;
                    if (visit.Exit >= 0.99999f && ContainsSurface(visit.Triangle, goal, mask, out surface, false, false))
                    {
                        next = visit.Triangle;
                        return true;
                    }
                    var source = Triangles[visit.Triangle];
                    for (var edge = 0; edge < 3; edge++)
                    {
                        var neighbor = source.GetNeighbor(edge);
                        if (neighbor < 0 || neighbor >= Triangles.Length) continue;
                        var seen = false;
                        for (var k = 0; k < visits.Length; k++)
                            if (visits[k].Triangle == neighbor) { seen = true; break; }
                        if (seen) continue;
                        var target = Triangles[neighbor];
                        if ((mask & (1 << target.Area)) == 0 ||
                            !ClipSegment(target, start, goal, out enter, out exit)) continue;
                        if (enter > visit.Exit + 0.00001f || exit < visit.Enter - 0.00001f) continue;
                        if (visits.Length >= 64) { truncated = true; continue; }
                        visits.Add(new SurfaceVisit { Triangle = neighbor, Enter = enter, Exit = exit });
                    }
                }
                if (clampBoundary && !truncated)
                {
                    var boundary = math.lerp(start, goal, math.saturate(furthest.Exit - 0.000001f));
                    if (ContainsSurface(furthest.Triangle, boundary, mask, out surface, false))
                    {
                        next = furthest.Triangle;
                        return true;
                    }
                }
                return false;
            }

            private static bool ClipSegment(NavTriangleData triangle, float3 start, float3 goal,
                out float enter, out float exit)
            {
                enter = 0f;
                exit = 1f;
                var ab = triangle.B.xz - triangle.A.xz;
                var ac = triangle.C.xz - triangle.A.xz;
                var determinant = ab.x * ac.y - ab.y * ac.x;
                if (math.abs(determinant) < 0.000001f) return false;
                var s = start.xz - triangle.A.xz;
                var g = goal.xz - triangle.A.xz;
                var u0 = (s.x * ac.y - s.y * ac.x) / determinant;
                var v0 = (ab.x * s.y - ab.y * s.x) / determinant;
                var u1 = (g.x * ac.y - g.y * ac.x) / determinant;
                var v1 = (ab.x * g.y - ab.y * g.x) / determinant;
                var tolerance = SurfaceTolerance(ab, ac, determinant);
                return ClipWeight(u0, u1, tolerance, ref enter, ref exit) &&
                    ClipWeight(v0, v1, tolerance, ref enter, ref exit) &&
                    ClipWeight(1f - u0 - v0, 1f - u1 - v1, tolerance, ref enter, ref exit);
            }

            private static float SurfaceTolerance(float2 ab, float2 ac, float determinant) =>
                math.clamp(0.002f * math.max(math.length(ab), math.max(math.length(ac), math.length(ac - ab))) /
                    math.abs(determinant), 0.000001f, 0.001f);

            private static bool ClipWeight(float start, float end, float tolerance, ref float enter, ref float exit)
            {
                var delta = end - start;
                if (math.abs(delta) < 0.0000001f) return start >= -tolerance;
                var crossing = (-tolerance - start) / delta;
                if (delta > 0f) enter = math.max(enter, crossing);
                else exit = math.min(exit, crossing);
                return enter <= exit;
            }
            public float3 ResolveContacts(int index, CrowdMovementInput input, float3 candidate)
            {
                if (input.BodyRadius <= 0f || input.PushResistant != 0) return float3.zero;
                var cell = (int2)math.floor(candidate.xz / CellSize);
                var maxRadius = BodyRadiusLimit.IsCreated ? BodyRadiusLimit[0] : MaxBodyRadius;
                var extent = (int)math.ceil((input.BodyRadius + maxRadius + 0.05f) / CellSize);
                var correction = float3.zero;
                var checks = 0;
                for (var x = cell.x - extent; x <= cell.x + extent; x++)
                for (var z = cell.y - extent; z <= cell.y + extent; z++)
                {
                    if (!AgentsByCell.TryGetFirstValue(new int2(x, z), out var otherIndex, out var iterator)) continue;
                    do
                    {
                        if (otherIndex == index) continue;
                        var other = Inputs[otherIndex];
                        checks++;
                        var offset = candidate - other.Position;
                        var sameLevel = math.abs(offset.y) <= math.max(1f, input.BodyRadius + other.BodyRadius);
                        offset.y = 0f;
                        var minimum = input.BodyRadius + other.BodyRadius + 0.05f;
                        var distance = math.length(offset);
                        if (sameLevel && other.BodyRadius > 0f && distance < minimum)
                        {
                            if (distance < 0.0001f)
                            {
                                var angle = ((input.InstanceId ^ other.InstanceId) & 1023) * (2f * math.PI / 1024f);
                                offset = new float3(math.cos(angle), 0f, math.sin(angle));
                                if (input.InstanceId > other.InstanceId) offset = -offset;
                            }
                            else offset /= distance;
                            var share = other.PushResistant != 0 ? 1f :
                                math.max(0.01f, other.Mass) / (math.max(0.01f, input.Mass) + math.max(0.01f, other.Mass));
                            correction += offset * ((minimum - distance) * share);
                        }
                        // Bound work in dense spawn piles, rather than turning contacts into an all-pairs scan.
                        if (checks >= 64) return ClampCorrection(correction, input);
                    } while (AgentsByCell.TryGetNextValue(out otherIndex, ref iterator));
                }
                return ClampCorrection(correction, input);
            }

            private static float3 ClampCorrection(float3 correction, CrowdMovementInput input)
            {
                var limit = math.min(input.BodyRadius * 0.5f, math.max(0f, input.DeltaTime) * 3f);
                var length = math.length(correction);
                return length > limit && length > 0f ? correction * (limit / length) : correction;
            }

            private bool ContainsSurface(int index, float3 position, int mask, out float3 surface,
                bool checkHeight = true, bool allowBoundaryTolerance = true)
            {
                surface = position;
                if (index < 0 || index >= Triangles.Length) return false;
                var t = Triangles[index];
                if ((mask & (1 << t.Area)) == 0) return false;
                var ab = t.B.xz - t.A.xz;
                var ac = t.C.xz - t.A.xz;
                var ap = position.xz - t.A.xz;
                var determinant = ab.x * ac.y - ab.y * ac.x;
                if (math.abs(determinant) < 0.000001f) return false;
                var u = (ap.x * ac.y - ap.y * ac.x) / determinant;
                var v = (ab.x * ap.y - ab.y * ap.x) / determinant;
                var tolerance = allowBoundaryTolerance ? SurfaceTolerance(ab, ac, determinant) : 0f;
                if (u < -tolerance || v < -tolerance || u + v > 1f + tolerance) return false;
                // Tolerate tiny classification errors but keep the accepted point on the triangle.
                u = math.max(0f, u);
                v = math.max(0f, v);
                var sum = u + v;
                if (sum > 1f) { u /= sum; v /= sum; }
                surface = t.A + u * (t.B - t.A) + v * (t.C - t.A);
                if (TerrainHeights.IsCreated) surface.y = TerrainHeights.Height(surface.xz);
                // Height disambiguates the starting surface. Once connectivity is proven,
                // the target height comes from the surface, not from a guessed candidate Y.
                return !checkHeight || math.abs(surface.y - position.y) <= 1f;
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
            public FieldKey Key;
            public readonly NativeArray<float> Costs;
            public readonly NativeArray<int> NextTriangle;
            public readonly NativeArray<int> Heap;
            public readonly NativeArray<int> HeapPositions;
            public readonly List<PendingRequest> Requests = new();
            public JobHandle Handle;
            public float LastUsedTime;
            public bool Complete;
            public int PendingDeliveries;
            public readonly RecoilSharedCorridorCache Corridors = new();

            public FlowField(FieldKey key, int triangleCount, float time)
            {
                Key = key;
                Costs = new NativeArray<float>(triangleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                NextTriangle = new NativeArray<int>(triangleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                Heap = new NativeArray<int>(triangleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                HeapPositions = new NativeArray<int>(triangleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
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
            public RequestOwner Owner;
        }

        private struct RequestOwner
        {
            public World World;
            public Entity Entity;
            public int Version;
            public bool IsCurrent()
            {
                if (World == null) return true;
                if (!World.IsCreated) return false;
                var manager = World.EntityManager;
                if (!manager.Exists(Entity) || !manager.HasComponent<RecoilUnitNavigation>(Entity)) return false;
                var navigation = manager.GetComponentData<RecoilUnitNavigation>(Entity);
                return navigation.Active != 0 && navigation.RequestVersion == Version;
            }
        }

        private readonly struct ReadyRequest
        {
            public readonly FlowField Field;
            public readonly PendingRequest Request;
            public ReadyRequest(FlowField field, PendingRequest request) { Field = field; Request = request; }
        }

        private sealed class PendingProjectionRequest
        {
            public Vector3 Start;
            public Vector3 Goal;
            public int AreaMask;
            public Action<RecoilPathResult> Callback;
            public RequestOwner Owner;
        }

        public bool IsReady => _initialized;
        public int LastSurfaceFastCount { get; private set; }
        public int LastSurfaceFallbackCount { get; private set; }
        public int LastTerrainRecoveryCount { get; private set; }
        public int LastTerrainHeldCount { get; private set; }
        public int LastPortalCrossingCount { get; private set; }
        public int FunnelPathCount { get; private set; }
        public int MidpointFallbackCount { get; private set; }
        public int DirectPathCount { get; private set; }

        private void OnDrawGizmosSelected()
        {
#if UNITY_EDITOR
            if (UnityEditor.Selection.activeGameObject != gameObject) return;
#endif
            if (!showSamplePaths || _agents.Count == 0) return;
            var previous = Gizmos.color;
            var samples = Mathf.Min(Mathf.Clamp(debugPathSamples, 1, 24), _agents.Count);
            for (var i = 0; i < samples; i++)
            {
                var index = (int)(((long)i * _agents.Count / samples + Mathf.Max(0, debugSampleOffset)) % _agents.Count);
                var agent = _agents[index];
                if (agent == null || !agent.IsActive) continue;
                agent.DrawDebugPath();
                var triangle = agent.DebugSurfaceTriangle;
                if (!showSampleTriangles || !_nativeTriangles.IsCreated || triangle < 0 || triangle >= _nativeTriangles.Length) continue;
                var t = _nativeTriangles[triangle];
                Gizmos.color = Color.magenta;
                var offset = Vector3.up * 0.1f;
                Gizmos.DrawLine(ToVector3(t.A) + offset, ToVector3(t.B) + offset);
                Gizmos.DrawLine(ToVector3(t.B) + offset, ToVector3(t.C) + offset);
                Gizmos.DrawLine(ToVector3(t.C) + offset, ToVector3(t.A) + offset);
            }
            if (_hasRejectedFunnel)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawLine(_lastRejectedFrom + Vector3.up, _lastRejectedTo + Vector3.up);
            }
            Gizmos.color = previous;
        }

        public static void ValidateCrowdSurfaceKernel()
        {
            ValidateDirectPathChecks();
            ValidateNativeRequestProjection();
            ValidateBurstBoundaryRecovery();
            ValidateCrowdResultCollection();
            ValidateMovementRosterCache();
            ValidateSmallBoundarySteps();
            ValidateIncrementalCrowdGrid();
            ValidateCornerFollowing();
            ValidateMovementChunkPrepare();
            ValidateMovementChunkApply();
            using var ownedInputs = new NativeArray<CrowdMovementInput>(1, Allocator.TempJob);
            using var outputs = new NativeArray<CrowdMovementOutput>(1, Allocator.TempJob);
            using var ownedTriangles = new NativeArray<NavTriangleData>(4, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int2, int>(1, Allocator.TempJob);
            using var triangleCells = new NativeParallelMultiHashMap<int2, int>(1, Allocator.TempJob);
            var inputs = ownedInputs;
            var triangles = ownedTriangles;
            triangles[0] = new NavTriangleData
            { A = float3.zero, B = new float3(10f, 5f, 0f), C = new float3(0f, 0f, 10f), Area = 0 };
            var input = new CrowdMovementInput
            {
                Position = new float3(0.2f, 1.1f, 0.2f), Waypoint = new float3(5f, 1.1f, 0.2f),
                Rotation = quaternion.identity, CurrentSpeed = 1f, MaxSpeed = 1f, Acceleration = 100f,
                DeltaTime = 1f, HasPath = 1, SurfaceTriangle = 0, AreaMask = -1, VerticalOffset = 1f
            };
            inputs[0] = input;
            var job = new SimulateCrowdMovementJob
            { Inputs = inputs, Outputs = outputs, Triangles = triangles, TrianglesByCell = triangleCells,
                AgentsByCell = cells, TriangleCellSize = 4f, CellSize = 4f, MaxNeighbors = 8 };
            job.Execute(0);
            if (outputs[0].SurfaceProjected == 0 || math.abs(outputs[0].CandidatePosition.y - 1.6f) > 0.001f)
                throw new InvalidOperationException("Same-triangle movement must follow the slope height.");
            var recoveryStart = new float3(0.2f, 0.1f, 0.2f);
            if (job.TryProjectStep(0, recoveryStart, new float3(-1f, 0f, 0.2f), -1, out _, out _))
                throw new InvalidOperationException("Boundary recovery must reject the off-surface avoidance step.");
            if (!job.TryProjectStep(0, recoveryStart, new float3(1.2f, 0f, 0.2f), -1,
                out var recoveryTriangle, out var recoverySurface) || recoveryTriangle != 0 ||
                math.abs(recoverySurface.y - 0.6f) > 0.001f)
                throw new InvalidOperationException("Unsteered recovery must use the verified surface height.");
            input.MaxSpeed = 30f; input.CurrentSpeed = 30f; input.Waypoint = new float3(50f, 1.1f, 0.2f);
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].SurfaceProjected == 0 || outputs[0].CandidatePosition.x > 9.802f ||
                outputs[0].CandidatePosition.x < 9.79f)
                throw new InvalidOperationException("A step outside the surface must stop at its native boundary.");
            input.HasPath = 0;
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].SurfaceProjected == 0 || math.any(outputs[0].CandidatePosition != input.Position) || outputs[0].Speed != 0f)
                throw new InvalidOperationException("Idle units must remain stationary without surface queries.");

            triangles[0] = new NavTriangleData
            { A = float3.zero, B = new float3(2f, 0f, 0f), C = new float3(0f, 0f, 2f),
                Neighbor0 = 1, Neighbor1 = -1, Neighbor2 = -1 };
            triangles[1] = new NavTriangleData
            { A = new float3(2f, 0f, 0f), B = new float3(2f, 1f, 2f), C = new float3(0f, 0f, 2f),
                Neighbor0 = 0, Neighbor1 = -1, Neighbor2 = -1 };
            input.HasPath = 1; input.SurfaceTriangle = 0;
            input.Position = new float3(0.8f, 1f, 0.8f);
            input.Waypoint = new float3(1.2f, 1f, 1.2f);
            input.CurrentSpeed = input.MaxSpeed = math.sqrt(0.32f);
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].CrossedPortal == 0 || outputs[0].SurfaceTriangle != 1 ||
                math.distance(outputs[0].CandidatePosition, new float3(1.2f, 1.2f, 1.2f)) > 0.001f)
                throw new InvalidOperationException("A unit must cross a shared diagonal into an uphill triangle.");
            input.SurfaceTriangle = 1;
            input.Position = new float3(1.2f, 1.2f, 1.2f);
            input.Waypoint = new float3(0.8f, 1.2f, 0.8f);
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].CrossedPortal == 0 || outputs[0].SurfaceTriangle != 0 ||
                math.abs(outputs[0].CandidatePosition.y - 1f) > 0.001f)
                throw new InvalidOperationException("A unit must cross the same diagonal downhill.");
            var disconnected = triangles[1];
            disconnected.Neighbor0 = -1;
            triangles[1] = disconnected;
            job.Execute(0);
            if (outputs[0].SurfaceProjected == 0 || outputs[0].SurfaceTriangle != 1 ||
                outputs[0].CandidatePosition.x < 0.999f || outputs[0].CandidatePosition.z < 0.999f)
                throw new InvalidOperationException("A disconnected diagonal must stop at the starting region boundary.");

            var center = new float3(1f, 0f, 1f);
            triangles[0] = new NavTriangleData
            { A = center, B = new float3(0f, 0f, 1f), C = new float3(1f, 0f, 0f),
                Neighbor0 = 1, Neighbor1 = 3, Neighbor2 = -1 };
            triangles[1] = new NavTriangleData
            { A = center, B = new float3(1f, 0f, 0f), C = new float3(2f, 0f, 1f),
                Neighbor0 = 0, Neighbor1 = 2, Neighbor2 = -1 };
            triangles[2] = new NavTriangleData
            { A = center, B = new float3(2f, 0f, 1f), C = new float3(1f, 0f, 2f),
                Neighbor0 = 1, Neighbor1 = 3, Neighbor2 = -1 };
            triangles[3] = new NavTriangleData
            { A = center, B = new float3(1f, 0f, 2f), C = new float3(0f, 0f, 1f),
                Neighbor0 = 0, Neighbor1 = 2, Neighbor2 = -1 };
            input.SurfaceTriangle = 0;
            input.Position = new float3(0.8f, 1f, 0.8f);
            input.Waypoint = new float3(1.2f, 1f, 1.2f);
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].SurfaceProjected == 0 || outputs[0].SurfaceTriangle != 2)
                throw new InvalidOperationException("A step through a shared vertex must reach a non-adjacent fan triangle.");
            input.SurfaceTriangle = 2;
            input.Position = new float3(1.2f, 1f, 1.2f);
            input.Waypoint = new float3(0.8f, 1f, 0.8f);
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].SurfaceProjected == 0 || outputs[0].SurfaceTriangle != 0)
                throw new InvalidOperationException("Shared-vertex crossing must work in both directions.");
            disconnected = triangles[2];
            disconnected.Neighbor0 = disconnected.Neighbor1 = -1;
            triangles[2] = disconnected;
            job.Execute(0);
            if (outputs[0].SurfaceProjected == 0 || outputs[0].SurfaceTriangle != 2 ||
                outputs[0].CandidatePosition.x < 0.999f || outputs[0].CandidatePosition.z < 0.999f)
                throw new InvalidOperationException("A shared vertex must not allow entering a disconnected region.");

            var restored = triangles[2];
            restored.Neighbor0 = 1; restored.Neighbor1 = 3;
            triangles[2] = restored;
            for (var directionIndex = 0; directionIndex < 8; directionIndex++)
            {
                var angle = directionIndex * math.PI * 0.25f;
                input.SurfaceTriangle = 0;
                input.Position = new float3(1f, 1f, 1f);
                input.Waypoint = input.Position + new float3(math.cos(angle), 0f, math.sin(angle)) * 0.3f;
                input.CurrentSpeed = input.MaxSpeed = 0.3f;
                inputs[0] = input;
                job.Execute(0);
                if (outputs[0].SurfaceProjected == 0 ||
                    math.distance(outputs[0].CandidatePosition, input.Waypoint) > 0.005f)
                    throw new InvalidOperationException("Starting exactly on a shared vertex must allow movement in all eight directions.");
            }
            var origin = new float3(1000f, 0f, 1000f);
            for (var i = 0; i < 4; i++)
            {
                var triangle = triangles[i];
                triangle.A += origin; triangle.B += origin; triangle.C += origin;
                triangles[i] = triangle;
            }
            // Simulate a cached triangle disagreeing by a fraction of a millimetre at its vertex.
            input.SurfaceTriangle = 0;
            input.Position = origin + new float3(1.0003f, 1f, 0.9997f);
            input.Waypoint = origin + new float3(1.2f, 1f, 1.2f);
            input.CurrentSpeed = input.MaxSpeed = math.distance(input.Position, input.Waypoint);
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].SurfaceProjected == 0 || outputs[0].SurfaceTriangle != 2 ||
                math.distance(outputs[0].CandidatePosition, input.Waypoint) > 0.005f)
                throw new InvalidOperationException("Vertex rounding at large world coordinates must not stop a connected step.");

            triangles[0] = new NavTriangleData
            { A = float3.zero, B = new float3(10f, 20f, 0f), C = new float3(0f, 0f, 10f),
                Neighbor0 = -1, Neighbor1 = -1, Neighbor2 = -1 };
            input.SurfaceTriangle = 0;
            input.Position = new float3(0.2f, 1.4f, 0.2f);
            input.Waypoint = new float3(2.2f, 1.4f, 0.2f);
            input.CurrentSpeed = input.MaxSpeed = 2f;
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].SurfaceProjected == 0 || math.abs(outputs[0].CandidatePosition.y - 5.4f) > 0.001f)
                throw new InvalidOperationException("A verified surface step must derive height from geometry rather than the candidate height limit.");
        }

        public static void ValidateDirectPathChecks()
        {
            using var ownedInputs = new NativeArray<ProjectionInput>(2, Allocator.TempJob);
            using var ownedIndices = new NativeArray<int>(2, Allocator.TempJob);
            using var ownedPositions = new NativeArray<float3>(2, Allocator.TempJob);
            using var ownedTriangles = new NativeArray<NavTriangleData>(2, Allocator.TempJob);
            using var direct = new NativeArray<byte>(1, Allocator.TempJob);
            var inputs = ownedInputs; var indices = ownedIndices; var positions = ownedPositions; var triangles = ownedTriangles;
            inputs[0] = new ProjectionInput { AreaMask = -1 };
            indices[0] = 0; indices[1] = 1;
            positions[0] = new float3(0.5f, 0f, 0.5f); positions[1] = new float3(1.5f, 0f, 1.5f);
            triangles[0] = new NavTriangleData
            { A = float3.zero, B = new float3(2,0,0), C = new float3(0,0,2), Neighbor0 = 1, Neighbor1 = -1, Neighbor2 = -1 };
            triangles[1] = new NavTriangleData
            { A = new float3(2,0,0), B = new float3(2,0,2), C = new float3(0,0,2), Neighbor0 = 0, Neighbor1 = -1, Neighbor2 = -1 };
            var job = new CheckDirectPathsJob
            { Inputs = inputs, TriangleIndices = indices, Positions = positions, Triangles = triangles,
                Direct = direct, TestConnectedSegment = 1 };
            job.Execute(0);
            if (direct[0] != 1) throw new InvalidOperationException("A connected unobstructed segment must bypass the flow field.");
            var triangle = triangles[0]; triangle.Neighbor0 = -1; triangles[0] = triangle;
            job.Execute(0);
            if (direct[0] != 0) throw new InvalidOperationException("Direct checks must not cross disconnected triangles.");
            triangle.Neighbor0 = 1; triangles[0] = triangle;
            triangle = triangles[1]; triangle.Area = 1; triangles[1] = triangle;
            inputs[0] = new ProjectionInput { AreaMask = 1 };
            job.Execute(0);
            if (direct[0] != 0) throw new InvalidOperationException("Direct checks must honor area restrictions.");
            indices[1] = -1;
            job.Execute(0);
            if (direct[0] != 0) throw new InvalidOperationException("Invalid endpoints cannot produce direct paths.");
        }

        private static void ValidateNativeRequestProjection()
        {
            using var ownedInputs = new NativeArray<ProjectionInput>(4, Allocator.TempJob);
            using var ownedTriangles = new NativeArray<NavTriangleData>(2, Allocator.TempJob);
            using var results = new NativeArray<int>(4, Allocator.TempJob);
            using var positions = new NativeArray<float3>(4, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int2, int>(2, Allocator.TempJob);
            var inputs = ownedInputs;
            var triangles = ownedTriangles;
            triangles[0] = new NavTriangleData
            { A = float3.zero, B = new float3(4f, 0f, 0f), C = new float3(0f, 0f, 4f), Area = 0 };
            triangles[1] = new NavTriangleData
            { A = new float3(0f, 5f, 0f), B = new float3(4f, 5f, 0f), C = new float3(0f, 5f, 4f), Area = 1 };
            cells.Add(int2.zero, 0);
            cells.Add(int2.zero, 1);
            inputs[0] = new ProjectionInput { Position = new float3(1f, 0.5f, 1f), AreaMask = -1 };
            inputs[1] = new ProjectionInput { Position = new float3(1f, 4.5f, 1f), AreaMask = -1 };
            inputs[2] = new ProjectionInput { Position = new float3(1f, 4.5f, 1f), AreaMask = 1 };
            inputs[3] = new ProjectionInput { Position = new float3(20f, 0f, 20f), AreaMask = -1 };
            var job = new FindProjectedTrianglesJob
            { Inputs = inputs, Triangles = triangles, TrianglesByCell = cells, Results = results,
                Positions = positions, CellSize = 4f, MaxDistance = 1f };
            for (var i = 0; i < 4; i++) job.Execute(i);
            if (results[0] != 0 || math.distance(positions[0], new float3(1f, 0f, 1f)) > 0.001f ||
                results[1] != 1 || math.distance(positions[1], new float3(1f, 5f, 1f)) > 0.001f ||
                results[2] != -1 || results[3] != -1)
                throw new InvalidOperationException("Native projection must preserve layers, area masks and maximum distance.");
        }

        private static void ValidateBurstBoundaryRecovery()
        {
            using var ownedInputs = new NativeArray<CrowdMovementInput>(2, Allocator.TempJob);
            using var outputs = new NativeArray<CrowdMovementOutput>(2, Allocator.TempJob);
            using var ownedTriangles = new NativeArray<NavTriangleData>(1, Allocator.TempJob);
            using var triangleCells = new NativeParallelMultiHashMap<int2, int>(1, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int2, int>(2, Allocator.TempJob);
            var inputs = ownedInputs;
            var triangles = ownedTriangles;
            triangles[0] = new NavTriangleData
            {
                A = float3.zero, B = new float3(10f, 0f, 0f), C = new float3(0f, 0f, 10f),
                Neighbor0 = -1, Neighbor1 = -1, Neighbor2 = -1
            };
            inputs[0] = new CrowdMovementInput
            {
                Position = new float3(0.1f, 1f, 1f), Waypoint = new float3(0.1f, 1f, 5f),
                Rotation = quaternion.identity, CurrentSpeed = 1f, MaxSpeed = 1f, Acceleration = 100f,
                DeltaTime = 1f, HasPath = 1, SurfaceTriangle = 0, AreaMask = -1, VerticalOffset = 1f,
                AvoidanceRadius = 2f, AvoidanceStrength = 10f
            };
            inputs[1] = new CrowdMovementInput { Position = new float3(0.3f, 1f, 1f) };
            cells.Add(int2.zero, 1);
            var job = new SimulateCrowdMovementJob
            {
                Inputs = inputs, Outputs = outputs, Triangles = triangles, TrianglesByCell = triangleCells,
                AgentsByCell = cells, TriangleCellSize = 4f, CellSize = 4f, MaxNeighbors = 8
            };
            job.Execute(0);
            var result = outputs[0];
            if (result.SurfaceProjected == 0 || result.Separation.x >= 0f ||
                math.distance(result.CandidatePosition, new float3(0.1f, 1f, 2f)) > 0.001f)
                throw new InvalidOperationException("Off-surface avoidance must recover the unsteered route inside the worker.");
            var input = inputs[0];
            input.Waypoint = new float3(-5f, 1f, 1f);
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].SurfaceProjected == 0 || math.abs(outputs[0].CandidatePosition.x) > 0.002f)
                throw new InvalidOperationException("An invalid unsteered route must clamp at the verified native boundary.");
            input.Position = new float3(-3f, 1f, 1f);
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].SurfaceProjected != 0)
                throw new InvalidOperationException("An unknown starting surface must retain the managed fallback.");
        }

        private static void ValidateCrowdResultCollection()
        {
            using var ownedInputs = new NativeArray<CrowdMovementInput>(3, Allocator.TempJob);
            using var ownedOutputs = new NativeArray<CrowdMovementOutput>(3, Allocator.TempJob);
            using var fallback = new NativeList<int>(3, Allocator.TempJob);
            using var summary = new NativeArray<int>(5, Allocator.TempJob);
            var inputs = ownedInputs;
            var outputs = ownedOutputs;
            outputs[0] = new CrowdMovementOutput { SurfaceProjected = 1, CrossedPortal = 1 };
            outputs[1] = new CrowdMovementOutput { SurfaceProjected = 0 };
            inputs[2] = new CrowdMovementInput { Inactive = 1 };
            var job = new CollectCrowdResultsJob
            { Inputs = inputs, Outputs = outputs, Fallback = fallback, Summary = summary, Count = 3 };
            job.Schedule().Complete();
            if (summary[0] != 1 || summary[1] != 1 || summary[2] != 1 || fallback.Length != 1 || fallback[0] != 1)
                throw new InvalidOperationException("Result collection must keep only active fallback units and count fast/crossing results.");
            outputs[1] = new CrowdMovementOutput { SurfaceProjected = 1 };
            job.Schedule().Complete();
            if (fallback.Length != 0 || summary[0] != 2 || summary[1] != 0)
                throw new InvalidOperationException("Result collection must clear stale fallback entries on the next step.");
            outputs[0] = new CrowdMovementOutput { SurfaceProjected = 1, TerrainRecovery = 1 };
            outputs[1] = new CrowdMovementOutput { SurfaceProjected = 1, TerrainRecovery = 2 };
            job.Schedule().Complete();
            if (summary[3] != 1 || summary[4] != 1 || fallback.Length != 0)
                throw new InvalidOperationException("Worker recovery and held units must be counted separately from managed fallback.");
        }

        private static void ValidateMovementRosterCache()
        {
            using var world = new World("Recoil movement roster cache checks");
            var manager = world.EntityManager;
            var first = manager.CreateEntity();
            var second = manager.CreateEntity();
            var a = new RecoilUnitSimulation(world, first, null, Vector3.zero, Quaternion.identity, default);
            var b = new RecoilUnitSimulation(world, second, null, Vector3.one, Quaternion.identity, default);
            var owner = new GameObject("Temporary movement roster cache check");
            owner.SetActive(false);
            var service = owner.AddComponent<RecoilNavMeshPathService>();
            try
            {
                service._movementWorld = world;
                service.EnsureCrowdJobCapacity(2);
                service.RegisterSimulation(a);
                service.RefreshMovementRoster();
                var builds = service.MovementRosterBuildCount;
                service.RefreshMovementRoster();
                if (service.MovementRosterBuildCount != builds || service._crowdEntityIndices.Count() != 1)
                    throw new InvalidOperationException("An unchanged movement roster must reuse its entity-index map.");
                service.RegisterSimulation(b);
                service.RefreshMovementRoster();
                if (service._crowdEntityIndices.Count() != 2)
                    throw new InvalidOperationException("Registering a unit must rebuild movement membership.");
                service.UnregisterSimulation(a);
                service.RefreshMovementRoster();
                if (service._crowdEntityIndices.ContainsKey(first) || !service._crowdEntityIndices.ContainsKey(second))
                    throw new InvalidOperationException("Removing a unit must drop its cached movement index.");
                manager.DestroyEntity(second);
                service._nextMovementRosterAudit = 0f;
                service.RefreshMovementRoster();
                if (service._movementRoster.Count != 0 || service._crowdEntityIndices.Count() != 0)
                    throw new InvalidOperationException("External deletion must be reconciled without stale movement handles.");
                service.RegisterSimulation(a);
                service.EnsureCrowdJobCapacity(32);
                service.RefreshMovementRoster();
                if (!service._crowdEntityIndices.ContainsKey(first))
                    throw new InvalidOperationException("Buffer growth must invalidate and rebuild the movement index map.");
            }
            finally
            {
                service.DisposeNativeResources();
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private static void ValidateSmallBoundarySteps()
        {
            using var ownedInputs = new NativeArray<CrowdMovementInput>(1, Allocator.TempJob);
            using var outputs = new NativeArray<CrowdMovementOutput>(1, Allocator.TempJob);
            using var ownedTriangles = new NativeArray<NavTriangleData>(2, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int2, int>(1, Allocator.TempJob);
            using var triangleCells = new NativeParallelMultiHashMap<int2, int>(1, Allocator.TempJob);
            var inputs = ownedInputs;
            var triangles = ownedTriangles;
            triangles[0] = new NavTriangleData
            { A = float3.zero, B = new float3(10f, 0f, 0f), C = new float3(0f, 0f, 10f),
                Neighbor0 = 1, Neighbor1 = -1, Neighbor2 = -1 };
            triangles[1] = new NavTriangleData
            { A = new float3(10f, 0f, 0f), B = new float3(10f, 0f, 10f), C = new float3(0f, 0f, 10f),
                Neighbor0 = 0, Neighbor1 = -1, Neighbor2 = -1 };
            var input = new CrowdMovementInput
            {
                Position = new float3(4.9999f, 1f, 5f), Waypoint = new float3(6f, 1f, 5f),
                HasPath = 1, SurfaceTriangle = 0, AreaMask = -1, VerticalOffset = 1f,
                Rotation = quaternion.identity, CurrentSpeed = 0.0005f, MaxSpeed = 0.0005f,
                Acceleration = 1f, DeltaTime = 1f
            };
            inputs[0] = input;
            var job = new SimulateCrowdMovementJob
            { Inputs = inputs, Outputs = outputs, Triangles = triangles, TrianglesByCell = triangleCells,
                AgentsByCell = cells, TriangleCellSize = 4f, CellSize = 4f, MaxNeighbors = 8 };
            job.Execute(0);
            var output = outputs[0];
            if (output.SurfaceProjected == 0 || output.SurfaceTriangle != 1 ||
                output.CandidatePosition.x - input.Position.x < 0.00045f ||
                math.abs(output.CandidatePosition.z - input.Position.z) > 0.00002f)
                throw new InvalidOperationException("Small steps across a shared edge must retain their displacement without snapping back.");
            input.Position = output.CandidatePosition;
            input.SurfaceTriangle = 1;
            input.Waypoint = new float3(4f, 1f, 5f);
            inputs[0] = input;
            job.Execute(0);
            output = outputs[0];
            if (output.SurfaceProjected == 0 || output.SurfaceTriangle != 0 ||
                input.Position.x - output.CandidatePosition.x < 0.00045f)
                throw new InvalidOperationException("Small boundary steps must preserve displacement in the reverse direction too.");
        }

        private static void ValidateIncrementalCrowdGrid()
        {
            using var owned = new NativeArray<CrowdMovementInput>(3, Allocator.TempJob);
            var inputs = owned;
            using var cells = new NativeParallelMultiHashMap<int2, int>(3, Allocator.TempJob);
            using var previous = new NativeArray<int2>(3, Allocator.TempJob);
            using var active = new NativeArray<byte>(3, Allocator.TempJob);
            using var metadata = new NativeArray<int>(2, Allocator.TempJob);
            using var radius = new NativeArray<float>(1, Allocator.TempJob);
            inputs[0] = new CrowdMovementInput { Position = new float3(1f, 0f, 1f), HasPath = 0, BodyRadius = 0.5f };
            inputs[1] = new CrowdMovementInput { Position = new float3(8f, 0f, 1f), HasPath = 1, BodyRadius = 1.5f };
            var job = new UpdateCrowdGridJob
            { Inputs = inputs, Cells = cells, PreviousCells = previous, PreviousActive = active,
                Metadata = metadata, Count = 2, CellSize = 2.5f, MaxBodyRadius = radius };
            job.Schedule().Complete();
            if (cells.Count() != 2 || metadata[1] != 2)
                throw new InvalidOperationException("Moving and idle units must both enter the crowd grid.");
            if (radius[0] != 1.5f)
                throw new InvalidOperationException("Grid reduction must publish the largest active body radius for dependent movement jobs.");
            var unit = inputs[0];
            unit.Position.x += 0.1f; inputs[0] = unit;
            job.Schedule().Complete();
            if (metadata[1] != 0) throw new InvalidOperationException("Within-cell movement must not modify the crowd grid.");
            unit.Position.x = 3f; inputs[0] = unit;
            job.Schedule().Complete();
            if (metadata[1] != 2 || cells.ContainsKey(int2.zero))
                throw new InvalidOperationException("Cell crossings must remove the old crowd entry and add the new one.");
            var other = inputs[1];
            inputs[0] = other; inputs[1] = unit;
            job.Schedule().Complete();
            if (!cells.TryGetFirstValue(new int2(3, 0), out var index, out _) || index != 0)
                throw new InvalidOperationException("Reordering crowd indices must not keep stale occupants.");
            unit.Inactive = 1; inputs[1] = unit;
            job.Schedule().Complete();
            if (cells.Count() != 1 || cells.ContainsKey(new int2(1, 0)))
                throw new InvalidOperationException("Inactive units must leave the crowd grid.");
            unit.Inactive = 0; unit.Position.x = 12f; inputs[1] = unit;
            job.Schedule().Complete();
            if (cells.Count() != 2 || !cells.ContainsKey(new int2(4, 0)))
                throw new InvalidOperationException("Respawned units must enter at their new crowd cell.");
            job.Count = 0;
            job.Schedule().Complete();
            if (cells.Count() != 0) throw new InvalidOperationException("Removed indices must be cleared from the crowd grid.");
            if (radius[0] != 0f) throw new InvalidOperationException("An empty crowd must reset the shared maximum body radius.");
        }

        private static void ValidateCornerFollowing()
        {
            using var ownedInputs = new NativeArray<CrowdMovementInput>(1, Allocator.TempJob);
            using var outputs = new NativeArray<CrowdMovementOutput>(1, Allocator.TempJob);
            using var ownedTriangles = new NativeArray<NavTriangleData>(1, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int2, int>(1, Allocator.TempJob);
            using var triangleCells = new NativeParallelMultiHashMap<int2, int>(1, Allocator.TempJob);
            var inputs = ownedInputs;
            var triangles = ownedTriangles;
            triangles[0] = new NavTriangleData
            { A = float3.zero, B = new float3(10f, 0f, 0f), C = new float3(0f, 0f, 10f),
                Neighbor0 = -1, Neighbor1 = -1, Neighbor2 = -1 };
            var input = new CrowdMovementInput
            {
                Position = new float3(1f, 1f, 1f), Waypoint = new float3(1.1f, 1f, 1f),
                NextWaypoint = new float3(5f, 1f, 1f), HasNextWaypoint = 1, HasPath = 1,
                Rotation = quaternion.identity, CurrentSpeed = 2f, MaxSpeed = 2f, Acceleration = 12f,
                DeltaTime = 0.05f, SurfaceTriangle = 0, AreaMask = -1, VerticalOffset = 1f
            };
            var job = new SimulateCrowdMovementJob
            { Inputs = inputs, Outputs = outputs, Triangles = triangles, TrianglesByCell = triangleCells,
                AgentsByCell = cells, TriangleCellSize = 4f, CellSize = 4f, MaxNeighbors = 8 };
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].AdvanceCorner == 0 || math.abs(outputs[0].Speed - 2f) > 0.001f)
                throw new InvalidOperationException("An unobstructed intermediate corner must advance without braking.");
            input.NextWaypoint = new float3(50f, 1f, 50f);
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].AdvanceCorner != 0 || math.abs(outputs[0].Speed - 2f) > 0.001f)
                throw new InvalidOperationException("An unverified corner shortcut must be rejected while preserving intermediate speed.");
            input.HasNextWaypoint = 0;
            inputs[0] = input;
            job.Execute(0);
            if (outputs[0].Speed >= 2f)
                throw new InvalidOperationException("The final destination must still apply braking.");
        }

        private static void ValidateMovementChunkPrepare()
        {
            using var world = new World("Recoil movement chunk prepare checks");
            var manager = world.EntityManager;
            var archetype = manager.CreateArchetype(typeof(RecoilUnitPose), typeof(RecoilUnitNavigation),
                typeof(RecoilUnitMovementSettings), typeof(RecoilUnitWaypoint));
            var active = manager.CreateEntity(archetype);
            var idle = manager.CreateEntity(archetype);
            var inactive = manager.CreateEntity(archetype);
            manager.CreateEntity(archetype); // Unregistered entity must not write to our arrays.
            manager.SetComponentData(active, new RecoilUnitPose
            { Position = new float3(1f, 1f, 1f), Rotation = quaternion.identity, Speed = 2f });
            manager.SetComponentData(active, new RecoilUnitMovementSettings
            { WaypointTolerance = 0.25f, BodyRadius = 0.5f, Mass = 2f, MaxSpeed = 4f, AreaMask = -1 });
            manager.SetComponentData(active, new RecoilUnitNavigation
            { Active = 1, MovementVersion = 7, SurfaceTriangle = 3 });
            manager.SetComponentData(idle, new RecoilUnitNavigation { Active = 1 });
            var path = manager.GetBuffer<RecoilUnitWaypoint>(active);
            path.Add(new RecoilUnitWaypoint { Position = new float3(1f, 1f, 1f) });
            path.Add(new RecoilUnitWaypoint { Position = new float3(5f, 1f, 1f) });
            using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<RecoilUnitPose>(),
                ComponentType.ReadOnly<RecoilUnitMovementSettings>(), ComponentType.ReadWrite<RecoilUnitNavigation>(),
                ComponentType.ReadOnly<RecoilUnitWaypoint>());
            using var indices = new NativeParallelHashMap<Entity, int>(3, Allocator.TempJob);
            indices.Add(active, 1); indices.Add(idle, 0); indices.Add(inactive, 2);
            using var inputs = new NativeArray<CrowdMovementInput>(3, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int2, int>(3, Allocator.TempJob);
            var job = new PrepareCrowdMovementJob
            {
                Entities = manager.GetEntityTypeHandle(),
                Poses = manager.GetComponentTypeHandle<RecoilUnitPose>(true),
                Settings = manager.GetComponentTypeHandle<RecoilUnitMovementSettings>(true),
                Navigations = manager.GetComponentTypeHandle<RecoilUnitNavigation>(false),
                Waypoints = manager.GetBufferTypeHandle<RecoilUnitWaypoint>(true),
                Indices = indices, Inputs = inputs, Cells = cells.AsParallelWriter(), DeltaTime = 0.1f, CellSize = 4f
            };
            job.ScheduleParallel(query, default).Complete();
            var input = inputs[1];
            if (input.HasPath == 0 || input.Waypoint.x != 5f || input.MovementVersion != 7 ||
                input.BodyRadius != 0.5f || input.Mass != 2f || input.SurfaceTriangle != 3 ||
                manager.GetComponentData<RecoilUnitNavigation>(active).WaypointIndex != 1)
                throw new InvalidOperationException("Chunk prepare must advance reached waypoints and preserve movement settings and versions.");
            if (inputs[0].Inactive != 0 || inputs[0].HasPath != 0 || inputs[2].Inactive == 0 || cells.Count() != 2)
                throw new InvalidOperationException("Idle units must remain obstacles; inactive and unregistered units must not enter the grid.");
        }

        private static void ValidateMovementChunkApply()
        {
            using var world = new World("Recoil movement chunk apply checks");
            var manager = world.EntityManager;
            var archetype = manager.CreateArchetype(typeof(RecoilUnitPose), typeof(RecoilUnitNavigation));
            var active = manager.CreateEntity(archetype);
            var stale = manager.CreateEntity(archetype);
            var inactive = manager.CreateEntity(archetype);
            manager.SetComponentData(active, new RecoilUnitNavigation { Active = 1, MovementVersion = 5 });
            manager.SetComponentData(stale, new RecoilUnitNavigation { Active = 1, MovementVersion = 6 });
            manager.SetComponentData(inactive, new RecoilUnitNavigation { Active = 0, MovementVersion = 5 });
            using var query = manager.CreateEntityQuery(ComponentType.ReadWrite<RecoilUnitPose>(),
                ComponentType.ReadWrite<RecoilUnitNavigation>());
            using var indices = new NativeParallelHashMap<Entity, int>(3, Allocator.TempJob);
            indices.Add(active, 0); indices.Add(stale, 1); indices.Add(inactive, 2);
            using var ownedInputs = new NativeArray<CrowdMovementInput>(3, Allocator.TempJob);
            using var ownedOutputs = new NativeArray<CrowdMovementOutput>(3, Allocator.TempJob);
            var inputs = ownedInputs;
            var outputs = ownedOutputs;
            for (var i = 0; i < 3; i++)
            {
                inputs[i] = new CrowdMovementInput { MovementVersion = 5, DeltaTime = 0.5f };
                outputs[i] = new CrowdMovementOutput { SurfaceProjected = 1, SurfaceTriangle = 3,
                    CandidatePosition = new float3(10f, 0f, 0f), Rotation = quaternion.identity, Speed = 2f };
            }
            var job = new ApplyCrowdMovementJob
            {
                Entities = manager.GetEntityTypeHandle(),
                Poses = manager.GetComponentTypeHandle<RecoilUnitPose>(false),
                Navigations = manager.GetComponentTypeHandle<RecoilUnitNavigation>(false),
                Indices = indices, Inputs = inputs, Outputs = outputs
            };
            job.ScheduleParallel(query, default).Complete();
            var pose = manager.GetComponentData<RecoilUnitPose>(active);
            if (pose.Position.x != 10f || pose.Velocity.x != 20f ||
                manager.GetComponentData<RecoilUnitNavigation>(active).SurfaceTriangle != 3)
                throw new InvalidOperationException("Chunk apply must write position, velocity and surface triangle.");
            if (manager.GetComponentData<RecoilUnitPose>(stale).Position.x != 0f ||
                manager.GetComponentData<RecoilUnitPose>(inactive).Position.x != 0f)
                throw new InvalidOperationException("Chunk apply must reject stale and inactive movement results.");
            outputs[0] = new CrowdMovementOutput { SurfaceProjected = 0, CandidatePosition = new float3(50f, 0f, 0f) };
            job.ScheduleParallel(query, default).Complete();
            if (manager.GetComponentData<RecoilUnitPose>(active).Position.x != 10f)
                throw new InvalidOperationException("Chunk apply must leave NavMesh fallback results to the main thread.");
        }

        public static void ValidateBodyContacts()
        {
            using var owned = new NativeArray<CrowdMovementInput>(2, Allocator.TempJob);
            using var cells = new NativeParallelMultiHashMap<int2, int>(2, Allocator.TempJob);
            var inputs = owned;
            inputs[0] = new CrowdMovementInput
            { Position = new float3(1f, 1f, 1f), BodyRadius = 0.5f, Mass = 1f, DeltaTime = 1f, InstanceId = 1 };
            inputs[1] = new CrowdMovementInput
            { Position = new float3(1.9f, 1f, 1f), BodyRadius = 0.5f, Mass = 3f, DeltaTime = 1f, InstanceId = 2 };
            cells.Add(new int2(0, 0), 0);
            cells.Add(new int2(0, 0), 1);
            var job = new SimulateCrowdMovementJob
            { Inputs = inputs, AgentsByCell = cells, CellSize = 2.5f, MaxBodyRadius = 0.5f };
            var light = job.ResolveContacts(0, inputs[0], inputs[0].Position);
            var heavy = job.ResolveContacts(1, inputs[1], inputs[1].Position);
            if (light.x >= 0f || heavy.x <= 0f || math.length(light) <= math.length(heavy))
                throw new InvalidOperationException("Idle overlapping units must separate according to mass.");
            if (math.distance(inputs[0].Position + light, inputs[1].Position + heavy) < 1.049f)
                throw new InvalidOperationException("A simple pair must resolve to its combined body radius and gap.");
            var other = inputs[1];
            other.PushResistant = 1;
            inputs[1] = other;
            if (math.lengthsq(job.ResolveContacts(1, other, other.Position)) != 0f)
                throw new InvalidOperationException("Push-resistant units must stay in place.");
            other.PushResistant = 0; other.Mass = 1f; other.Position = inputs[0].Position;
            inputs[1] = other;
            light = job.ResolveContacts(0, inputs[0], inputs[0].Position);
            heavy = job.ResolveContacts(1, other, other.Position);
            if (math.lengthsq(light) == 0f || math.lengthsq(light + heavy) > 0.00001f)
                throw new InvalidOperationException("Coincident units must receive opposite, finite corrections.");
        }
        public int TriangleCount => _nativeTriangles.IsCreated ? _nativeTriangles.Length : 0;

        public static void ValidateTerrainBackend(RecoilTerrainMapData map)
        {
            if (map == null || !map.HasValidData) throw new ArgumentException("Select a valid terrain map.");
            var root = new GameObject("Terrain backend validation") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var service = root.AddComponent<RecoilNavMeshPathService>();
                service.terrainMap = map;
                service.useProjectTerrainMap = false;
                service.InitializeGraph();
                if (!service.IsReady || !service.UsingTerrainMap) throw new InvalidOperationException("Terrain graph did not initialize.");
                var source = Vector3.zero;
                var goal = Vector3.zero;
                var found = false;
                for (var z = 0; z < map.Rows - 1 && !found; z++)
                    for (var x = 0; x < map.Columns - 1 && !found; x++)
                    {
                        if (!map.IsWalkable(x, z)) continue;
                        source = map.WorldOrigin + new Vector3((x + 0.25f) * map.CellSize.x, 0f, (z + 0.25f) * map.CellSize.y);
                        goal = map.WorldOrigin + new Vector3((x + 0.75f) * map.CellSize.x, 0f, (z + 0.75f) * map.CellSize.y);
                        map.TrySample(source, out var sy, out _); source.y = sy;
                        map.TrySample(goal, out var gy, out _); goal.y = gy;
                        found = true;
                    }
                if (!found) throw new InvalidOperationException("Map contains no walkable cells.");
                if (!service.TrySamplePosition(source, 1f, -1, out var projected) ||
                    math.distance(projected, source) > 0.002f)
                    throw new InvalidOperationException("Terrain projection must return the baked height.");
                RecoilPathResult result = null;
                service.RequestPath(source, goal, -1, r => result = r);
                service.ScheduleProjectionJob();
                service._projectionHandle.Complete();
                service.CompleteProjectionJob();
                foreach (var field in service._fieldOrder) field.Handle.Complete();
                service.ProcessFlowFields();
                if (result == null || !result.Succeeded)
                    throw new InvalidOperationException("Terrain route failed: " + result?.Error);
                foreach (var point in result.Points)
                {
                    map.TrySample(point, out var y, out _);
                    if (math.abs(point.y - y) > 0.002f) throw new InvalidOperationException("Route waypoint must use baked height.");
                }
                using var ownedInputs = new NativeArray<CrowdMovementInput>(1, Allocator.TempJob);
                using var outputs = new NativeArray<CrowdMovementOutput>(1, Allocator.TempJob);
                using var cells = new NativeParallelMultiHashMap<int2, int>(1, Allocator.TempJob);
                using var radius = new NativeArray<float>(1, Allocator.TempJob);
                var inputs = ownedInputs;
                inputs[0] = new CrowdMovementInput
                {
                    Position = source + Vector3.up, Waypoint = goal + Vector3.up,
                    HasPath = 1, SurfaceTriangle = -1, AreaMask = -1, VerticalOffset = 1f,
                    CurrentSpeed = 0.25f, MaxSpeed = 0.25f, Acceleration = 10f, DeltaTime = 0.1f,
                    Rotation = quaternion.identity
                };
                var movementJob = new SimulateCrowdMovementJob
                {
                    TerrainHeights = service._terrainHeightField, Triangles = service._nativeTriangles,
                    TrianglesByCell = service._trianglesByCell, TriangleCellSize = service._cellSize,
                    Inputs = inputs, Outputs = outputs, AgentsByCell = cells, CellSize = 2.5f,
                    MaxNeighbors = 8, BodyRadiusLimit = radius
                };
                movementJob.Schedule(1, 1).Complete();
                map.TrySample(outputs[0].CandidatePosition, out var surfaceHeight, out _);
                if (outputs[0].SurfaceProjected == 0 || math.abs(outputs[0].CandidatePosition.y - surfaceHeight - 1f) > 0.002f)
                    throw new InvalidOperationException("Scheduled terrain movement must follow exact baked heights.");
                var outside = inputs[0];
                outside.Position.x = map.WorldOrigin.x - 2f;
                inputs[0] = outside;
                movementJob.Schedule(1, 1).Complete();
                if (outputs[0].TerrainRecovery != 2 || outputs[0].SurfaceProjected == 0 || outputs[0].Speed != 0f ||
                    math.any(outputs[0].CandidatePosition != outside.Position))
                    throw new InvalidOperationException("Unknown terrain positions must hold inside the worker without managed retry or teleportation.");
            }
            finally { DestroyImmediate(root); }
        }

        public static void ValidateSharedCorridors()
        {
            var cache = new RecoilSharedCorridorCache(2, 4);
            var portals = new List<RecoilPathPortal>
            {
                new(new Vector3(-2,0,1), new Vector3(2,0,1)),
                new(new Vector3(-2,0,2), new Vector3(2,0,2))
            };
            cache.Store(7, portals);
            var original = portals[0];
            portals[0] = new RecoilPathPortal(Vector3.zero, Vector3.zero);
            if (!cache.TryGet(7, out var shared) || shared.Count != 2 || shared[0].Left != original.Left)
                throw new InvalidOperationException("Corridors must own their portal data independently of scratch buffers.");
            var a = RecoilPathFunnel.Build(Vector3.zero, new Vector3(0,0,3), shared);
            var b = RecoilPathFunnel.Build(new Vector3(0.5f,0,0), new Vector3(0.5f,0,3), shared);
            if (a[0] == b[0] || a[a.Length - 1] == b[b.Length - 1])
                throw new InvalidOperationException("Shared corridors must retain individual unit endpoints.");
            cache.Clear();
            if (cache.TryGet(7, out _) || cache.StoredPortalCount != 0)
                throw new InvalidOperationException("Invalidated fields must not retain old corridor entries.");
            portals[0] = original;
            cache.Store(1, portals); cache.Store(2, portals); cache.Store(3, portals);
            if (cache.Count != 1 || cache.StoredPortalCount != 2 || cache.TryGet(1, out _) || !cache.TryGet(3, out _))
                throw new InvalidOperationException("Corridor capacity must reset without growing memory.");
            var oversized = new RecoilPathPortal[5];
            if (cache.Store(4, oversized) || !cache.TryGet(3, out _))
                throw new InvalidOperationException("Oversized corridors must bypass the cache without evicting useful entries.");
        }

        public static void ValidatePathServiceScheduling()
        {
            using var world = new World("Path service scheduling checks");
            var owner = world.EntityManager.CreateEntity(typeof(RecoilUnitNavigation));
            world.EntityManager.SetComponentData(owner, new RecoilUnitNavigation { Active = 1, RequestVersion = 1 });
            var root = new GameObject("Temporary path service scheduling checks");
            root.SetActive(false);
            var service = root.AddComponent<RecoilNavMeshPathService>();
            try
            {
                service.maxCachedFields = 1;
                service.maxPathCompletionsPerFrame = 1;
                service.pathCompletionBudgetMilliseconds = 1000f;
                service._nativeTriangles = new NativeArray<NavTriangleData>(2, Allocator.Persistent);
                service._nativeTriangles[0] = new NavTriangleData
                { A = float3.zero, B = new float3(2,0,0), C = new float3(0,0,2), Center = new float3(0.666f,0,0.666f),
                    Neighbor0 = 1, Neighbor1 = -1, Neighbor2 = -1, Portal0 = new float3(1,0,1) };
                service._nativeTriangles[1] = new NavTriangleData
                { A = new float3(2,0,0), B = new float3(2,0,2), C = new float3(0,0,2), Center = new float3(1.333f,0,1.333f),
                    Neighbor0 = 0, Neighbor1 = -1, Neighbor2 = -1, Portal0 = new float3(1,0,1) };
                service._nativeAreaCosts = new NativeArray<float>(32, Allocator.Persistent);
                for (var i = 0; i < 32; i++) service._nativeAreaCosts[i] = 1f;
                var field = service.GetOrCreateField(new FieldKey(0, -1));
                field.Corridors.Store(0, new[] { new RecoilPathPortal(Vector3.left, Vector3.right) });
                field.Handle.Complete();
                service.CompleteField(field);
                var callbacks = new List<RecoilPathResult>();
                service.QueueReady(field, new PendingRequest
                {
                    Callback = callbacks.Add, Owner = new RequestOwner { World = world, Entity = owner, Version = 1 }
                });
                if (service.GetOrCreateField(new FieldKey(1, -1)) != null)
                    throw new InvalidOperationException("Fields awaiting delivery must remain pinned in the cache.");
                world.EntityManager.SetComponentData(owner, new RecoilUnitNavigation { Active = 1, RequestVersion = 2 });
                service.ProcessFlowFields();
                if (callbacks.Count != 0 || service.LastCancelledPaths != 1 || field.PendingDeliveries != 0)
                    throw new InvalidOperationException("Stale requests must release their field without invoking callbacks.");
                var reused = service.GetOrCreateField(new FieldKey(1, -1));
                reused.Handle.Complete();
                if (!ReferenceEquals(field, reused) || service.FlowFieldAllocationCount != 1 || service.FlowFieldReuseCount != 1 ||
                    reused.Costs[1] != 0f || reused.NextTriangle[0] != 1 || reused.Corridors.Count != 0)
                    throw new InvalidOperationException("Evicted field buffers must be reused and recomputed for the new goal.");
                for (var i = 0; i < 3; i++)
                    service.QueueReady(null, new PendingRequest { Start = Vector3.zero, Goal = Vector3.right * (i + 1), Callback = callbacks.Add });
                service.ProcessFlowFields();
                if (callbacks.Count != 1 || service.PendingPathCompletions != 2 || service.LastPathCompletions != 1)
                    throw new InvalidOperationException("Completion budgets must retain excess requests for later frames.");
                service.ProcessFlowFields(); service.ProcessFlowFields();
                if (callbacks.Count != 3 || service.PendingPathCompletions != 0 || callbacks[0].Points[1].x != 1f || callbacks[2].Points[1].x != 3f)
                    throw new InvalidOperationException("Queued paths must finish in order and retain independent result arrays.");
                if (!service.ComputePortalEdges(0, 1, out var expected))
                    throw new InvalidOperationException("Test corridor must have a shared portal.");
                service._nativePortals = new NativeArray<RecoilPathPortal>(6, Allocator.Persistent);
                service._nativePortalValid = new NativeArray<byte>(6, Allocator.Persistent);
                service._nativePortals[0] = expected; service._nativePortalValid[0] = 1;
                if (!service.TryGetPortalEdges(0, 1, out var cached) || cached.Left != expected.Left || cached.Right != expected.Right)
                    throw new InvalidOperationException("Cached portals must preserve corridor orientation.");
            }
            finally { service.DisposeNativeResources(); DestroyImmediate(root); }
        }

        private void Start()
        {
            InitializeGraph();
            _clockDriven = true;
            _hasStarted = true;
            RecoilSimulationClock.Register(this);
        }

        private void Update()
        {
            if (!_initialized)
                InitializeGraph();
            if (!_initialized)
                return;
            LastCancelledPaths = 0;
            var projectionStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using (ProjectionMarker.Auto())
            {
                CompleteProjectionJob();
                ScheduleProjectionJob();
            }
            LastProjectionMilliseconds = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - projectionStart) *
                (1000.0 / System.Diagnostics.Stopwatch.Frequency));
            ProcessFlowFields();
        }

        private void LateUpdate()
        {
            if (_clockDriven) return;
            if (!_initialized)
                return;
            using (CrowdMovementMarker.Auto())
                UpdateCrowdMovement();
        }

        // Script reload calls OnDisable before managed references to native allocations are lost.
        private void OnEnable()
        {
            if (!_hasStarted) return;
            _clockDriven = true;
            RecoilSimulationClock.Register(this);
        }

        private void OnDisable()
        {
            RecoilSimulationClock.Unregister(this);
            _clockDriven = false;
            DisposeNativeResources();
        }

        private void OnDestroy() => DisposeNativeResources();

        private void DisposeNativeResources()
        {
            _initialized = false;
            // Complete even a finished job to release its safety/dependency ownership.
            _projectionHandle.Complete();
            _projectionHandle = default;
            CompletePendingCrowdMovement(false);
            if (_hasMovementApplyQuery && _movementWorld != null && _movementWorld.IsCreated)
            {
                _movementApplyQuery.Dispose();
                _movementPrepareQuery.Dispose();
            }
            _hasMovementApplyQuery = false;
            _movementWorld = null;
            for (var i = 0; i < _fieldOrder.Count; i++)
                _fieldOrder[i].Handle.Complete();

            if (_projectionInputs.IsCreated)
                _projectionInputs.Dispose();
            if (_projectionResults.IsCreated)
                _projectionResults.Dispose();
            if (_projectedPositions.IsCreated) _projectedPositions.Dispose();
            if (_projectionDirect.IsCreated) _projectionDirect.Dispose();
            _projectionDirect = default;
            _projectionInputs = default;
            _projectionResults = default;
            _projectedPositions = default;
            _projectionCapacity = 0;
            DisposeCrowdJobData();
            for (var i = 0; i < _fieldOrder.Count; i++)
            {
                var field = _fieldOrder[i];
                field.Dispose();
            }
            _fieldOrder.Clear();
            while (_spareFields.Count > 0) _spareFields.Pop().Dispose();
            _readyPaths.Clear();
            _fields.Clear();
            _activeProjectionRequests.Clear();
            _queuedProjectionRequests.Clear();
            _waitingRequests.Clear();
            _waitingOrder.Clear();
            // Invalidate paths and in-flight results; callers can request fresh paths on resume.
            foreach (var agent in _agents)
                if (agent.Exists)
                    agent.StopMovement();
            _triangles.Clear();
            _edgesByTriangle = Array.Empty<List<Edge>>();
            _crowdSimulationAccumulator = 0f;
            _movementRoster.Clear();
            _movementRosterDirty = true;

            if (_nativeTriangles.IsCreated)
                _nativeTriangles.Dispose();
            if (_nativePortals.IsCreated) _nativePortals.Dispose();
            if (_nativePortalValid.IsCreated) _nativePortalValid.Dispose();
            _nativePortals = default;
            _nativePortalValid = default;
            if (_nativeAreaCosts.IsCreated)
                _nativeAreaCosts.Dispose();
            if (_trianglesByCell.IsCreated)
                _trianglesByCell.Dispose();
            _terrainHeightField.Dispose();
            _nativeTriangles = default;
            _nativeAreaCosts = default;
            _trianglesByCell = default;
            _activeTerrainMap = null;
            TerrainLeafCount = 0;
        }

        private void DisposeCrowdJobData()
        {
            if (_crowdFallbackIndices.IsCreated) _crowdFallbackIndices.Dispose();
            if (_crowdResultSummary.IsCreated) _crowdResultSummary.Dispose();
            if (_crowdMaxBodyRadius.IsCreated) _crowdMaxBodyRadius.Dispose();
            _crowdMaxBodyRadius = default;
            _movementRosterDirty = true;
            if (_crowdPreviousCells.IsCreated) _crowdPreviousCells.Dispose();
            if (_crowdPreviousActive.IsCreated) _crowdPreviousActive.Dispose();
            if (_crowdGridMetadata.IsCreated) _crowdGridMetadata.Dispose();
            _crowdPreviousCells = default;
            _crowdPreviousActive = default;
            _crowdGridMetadata = default;
            LastCrowdGridMutations = 0;
            if (_crowdEntityIndices.IsCreated) _crowdEntityIndices.Dispose();
            _crowdEntityIndices = default;
            if (_crowdMovementInputs.IsCreated)
                _crowdMovementInputs.Dispose();
            if (_crowdMovementOutputs.IsCreated)
                _crowdMovementOutputs.Dispose();
            if (_crowdAgentsByCell.IsCreated)
                _crowdAgentsByCell.Dispose();
            _crowdMovementInputs = default;
            _crowdMovementOutputs = default;
            _crowdAgentsByCell = default;

            _crowdCapacity = 0;
        }

        private void EnsureCrowdJobCapacity(int count)
        {
            if (count <= _crowdCapacity)
                return;

            DisposeCrowdJobData();
            _crowdCapacity = Mathf.NextPowerOfTwo(Mathf.Max(4, count));
            _crowdFallbackIndices = new NativeList<int>(_crowdCapacity, Allocator.Persistent);
            _crowdResultSummary = new NativeArray<int>(5, Allocator.Persistent);
            _crowdMaxBodyRadius = new NativeArray<float>(1, Allocator.Persistent);
            _crowdPreviousCells = new NativeArray<int2>(_crowdCapacity, Allocator.Persistent);
            _crowdPreviousActive = new NativeArray<byte>(_crowdCapacity, Allocator.Persistent);
            _crowdGridMetadata = new NativeArray<int>(2, Allocator.Persistent);
            _crowdEntityIndices = new NativeParallelHashMap<Entity, int>(_crowdCapacity, Allocator.Persistent);
            var options = NativeArrayOptions.UninitializedMemory;
            _crowdMovementInputs = new NativeArray<CrowdMovementInput>(_crowdCapacity, Allocator.Persistent, options);
            _crowdMovementOutputs = new NativeArray<CrowdMovementOutput>(_crowdCapacity, Allocator.Persistent, options);
            _crowdAgentsByCell = new NativeParallelMultiHashMap<int2, int>(_crowdCapacity, Allocator.Persistent);
        }

        public void RegisterSimulation(RecoilUnitSimulation agent)
        {
            if (agent != null && _registeredAgents.Add(agent))
            {
                _agents.Add(agent);
                _movementRosterDirty = true;
            }
        }

        public void UnregisterSimulation(RecoilUnitSimulation agent)
        {
            if (_registeredAgents.Remove(agent))
            {
                _agents.Remove(agent);
                _movementRosterDirty = true;
            }
        }

        private void RefreshMovementRoster()
        {
            // Registration handles normal spawn/death. Periodic reconciliation catches external entity deletion.
            if (Time.unscaledTime >= _nextMovementRosterAudit)
            {
                _nextMovementRosterAudit = Time.unscaledTime + 1f;
                for (var i = _agents.Count - 1; i >= 0; i--)
                {
                    var agent = _agents[i];
                    if (agent != null && agent.Exists) continue;
                    _registeredAgents.Remove(agent);
                    _agents.RemoveAt(i);
                    _movementRosterDirty = true;
                }
            }
            if (!_movementRosterDirty) return;
            _movementRoster.Clear();
            _crowdEntityIndices.Clear();
            _movementRosterSingleWorld = _hasMovementApplyQuery;
            for (var i = _agents.Count - 1; i >= 0; i--)
            {
                var agent = _agents[i];
                var index = _movementRoster.Count;
                _movementRoster.Add(agent);
                if (agent.SimulationWorld != _movementWorld || !_crowdEntityIndices.TryAdd(agent.Entity, index))
                    _movementRosterSingleWorld = false;
            }
            _movementRosterDirty = false;
            MovementRosterBuildCount++;
        }

        public bool TrySamplePosition(Vector3 desired, float distance, int areaMask, out Vector3 position)
        {
            position = desired;
            if (!_initialized || !math.all(math.isfinite((float3)desired)) || !float.IsFinite(distance) || distance <= 0f)
                return false;
            var projection = new FindProjectedTrianglesJob
            { TerrainHeights = _terrainHeightField, Triangles = _nativeTriangles, TrianglesByCell = _trianglesByCell, CellSize = _cellSize, MaxDistance = distance };
            projection.Find(new ProjectionInput { Position = desired, AreaMask = areaMask }, out var triangle, out var point);
            if (triangle < 0) return false;
            position = point;
            return true;
        }

        public bool RequestPath(
            Vector3 start,
            Vector3 destination,
            int areaMask,
            Action<RecoilPathResult> callback, World ownerWorld = null, Entity ownerEntity = default, int ownerRequestVersion = 0)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            if (!_initialized)
            {
                callback(RecoilPathResult.Failure("The NavMesh path service is not initialized."));
                return false;
            }

            if (!math.all(math.isfinite((float3)start)) || !math.all(math.isfinite((float3)destination)))
            {
                callback(RecoilPathResult.Failure("Path endpoints must be finite."));
                return false;
            }

            _queuedProjectionRequests.Enqueue(new PendingProjectionRequest
            {
                Start = start,
                Goal = destination,
                AreaMask = areaMask,
                Callback = callback,
                Owner = new RequestOwner { World = ownerWorld, Entity = ownerEntity, Version = ownerRequestVersion }
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

            if (_projectedPositions.IsCreated) _projectedPositions.Dispose();
            if (_projectionDirect.IsCreated) _projectionDirect.Dispose();
            _projectionCapacity = Mathf.NextPowerOfTwo(Mathf.Max(4, count));
            _projectionDirect = new NativeArray<byte>(_projectionCapacity / 2, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _projectedPositions = new NativeArray<float3>(_projectionCapacity, Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
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

            var requestCount = math.min(_queuedProjectionRequests.Count, math.max(1, maxProjectionRequestsPerFrame));
            EnsureProjectionCapacity(requestCount * 2);
            _activeProjectionRequests.Clear();
            var accepted = 0;
            var inspected = 0;
            while (accepted < requestCount && _queuedProjectionRequests.Count > 0 && inspected++ < requestCount * 4)
            {
                var request = _queuedProjectionRequests.Dequeue();
                if (!request.Owner.IsCurrent()) { LastCancelledPaths++; continue; }
                var i = accepted++;
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

            if (accepted == 0) return;

            _projectionHandle = new FindProjectedTrianglesJob
            {
                TerrainHeights = _terrainHeightField,
                Inputs = _projectionInputs,
                Triangles = _nativeTriangles,
                TrianglesByCell = _trianglesByCell,
                Results = _projectionResults,
                Positions = _projectedPositions,
                CellSize = _cellSize,
                MaxDistance = maxProjectionDistance
            }.Schedule(accepted * 2, 32);
            _projectionHandle = new CheckDirectPathsJob
            {
                Inputs = _projectionInputs, TriangleIndices = _projectionResults, Positions = _projectedPositions,
                Triangles = _nativeTriangles, TerrainHeights = _terrainHeightField, Direct = _projectionDirect,
                TestConnectedSegment = (byte)(UsingTerrainMap ? 1 : 0)
            }.Schedule(accepted, 16, _projectionHandle);
        }

        private void CompleteProjectionJob()
        {
            if (_activeProjectionRequests.Count == 0 || !_projectionHandle.IsCompleted)
                return;

            _projectionHandle.Complete();
            for (var i = 0; i < _activeProjectionRequests.Count; i++)
            {
                var request = _activeProjectionRequests[i];
                if (!request.Owner.IsCurrent()) { LastCancelledPaths++; continue; }
                var startTriangle = _projectionResults[i * 2];
                var goalTriangle = _projectionResults[i * 2 + 1];
                if (startTriangle < 0 || goalTriangle < 0)
                {
                    request.Callback(RecoilPathResult.Failure(
                        "Could not find a NavMesh triangle for the projected start or destination."));
                    continue;
                }
                var start = (Vector3)_projectedPositions[i * 2];
                var goal = (Vector3)_projectedPositions[i * 2 + 1];

                var pathRequest = new PendingRequest
                {
                    StartTriangle = startTriangle,
                    Start = start,
                    Goal = goal,
                    AreaMask = request.AreaMask,
                    Callback = request.Callback,
                    Owner = request.Owner
                };
                if (_projectionDirect[i] != 0)
                {
                    QueueReady(null, pathRequest);
                    continue;
                }
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
                QueueReady(field, request);
            else
                field.Requests.Add(request);
        }

        private void InitializeGraph()
        {
            if (_initialized)
                return;
            _terrainHeightField.Dispose();
            maxCachedFields = Mathf.Max(1, maxCachedFields);
            crowdCellSize = Mathf.Max(0.1f, crowdCellSize);
            maxAvoidanceNeighbors = Mathf.Max(1, maxAvoidanceNeighbors);
            crowdSimulationRate = Mathf.Max(1f, crowdSimulationRate);
            var settings = useProjectTerrainMap ? Resources.Load<RecoilTerrainBackendSettings>("RecoilTerrainBackend") : null;
            _activeTerrainMap = terrainMap != null ? terrainMap : settings != null ? settings.terrainMap : null;
            NavMeshTriangulation triangulation;
            if (_activeTerrainMap != null)
            {
                if (!_activeTerrainMap.HasValidData)
                {
                    Debug.LogError("Selected terrain map data is invalid. Rebake the asset.", this);
                    return;
                }
                var heights = new float[_activeTerrainMap.Columns * _activeTerrainMap.Rows];
                var cells = new byte[(_activeTerrainMap.Columns - 1) * (_activeTerrainMap.Rows - 1)];
                _activeTerrainMap.CopyHeightsTo(heights);
                _activeTerrainMap.CopyWalkabilityTo(cells);
                var graph = RecoilTerrainQuadtree.Build(_activeTerrainMap.Columns, _activeTerrainMap.Rows,
                    _activeTerrainMap.WorldOrigin, _activeTerrainMap.WorldSize, heights, cells,
                    0.15f, settings != null ? settings.maxLeafCells : 32, false);
                _terrainHeightField = new RecoilTerrainHeightField
                {
                    Heights = new NativeArray<float>(heights, Allocator.Persistent),
                    CellLeaves = new NativeArray<int>(graph.CellLeaves, Allocator.Persistent),
                    LeafTriangleStarts = new NativeArray<int>(graph.LeafTriangleStarts, Allocator.Persistent),
                    Columns = _activeTerrainMap.Columns, Rows = _activeTerrainMap.Rows,
                    Origin = _activeTerrainMap.WorldOrigin,
                    Size = new float2(_activeTerrainMap.WorldSize.x, _activeTerrainMap.WorldSize.y)
                };
                TerrainLeafCount = graph.LeafCount;
                triangulation = new NavMeshTriangulation { vertices = graph.Vertices, indices = graph.Indices };
            }
            else
            {
                // Jobs require valid containers even when the optional heightfield is unused.
                _terrainHeightField = new RecoilTerrainHeightField
                { Heights = new NativeArray<float>(0, Allocator.Persistent), CellLeaves = new NativeArray<int>(0, Allocator.Persistent),
                    LeafTriangleStarts = new NativeArray<int>(0, Allocator.Persistent) };
                triangulation = NavMesh.CalculateTriangulation();
            }
            if (triangulation.vertices == null || triangulation.indices == null ||
                triangulation.indices.Length < 3)
            {
                Debug.LogError($"{nameof(RecoilNavMeshPathService)} found no walkable triangles in {SurfaceBackend}.", this);
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
                _nativeAreaCosts[area] = UsingTerrainMap ? 1f : Mathf.Max(0.001f, NavMesh.GetAreaCost(area));
            _initialized = true;
            if (UsingTerrainMap)
                Debug.Log($"Terrain backend ready: {_activeTerrainMap.name}, {TerrainLeafCount:N0} quadtree leaves, {TriangleCount:N0} triangles. Path and movement surface use baked terrain data.", this);
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
            _nativePortals = new NativeArray<RecoilPathPortal>(_nativeTriangles.Length * 3, Allocator.Persistent);
            _nativePortalValid = new NativeArray<byte>(_nativeTriangles.Length * 3, Allocator.Persistent);
            for (var i = 0; i < _nativeTriangles.Length; i++)
                for (var edge = 0; edge < 3; edge++)
                {
                    var next = _nativeTriangles[i].GetNeighbor(edge);
                    if (next < 0 || !ComputePortalEdges(i, next, out var portal)) continue;
                    _nativePortals[i * 3 + edge] = portal;
                    _nativePortalValid[i * 3 + edge] = 1;
                }
            _triangles.TrimExcess();
            _edgesByTriangle = Array.Empty<List<Edge>>();
        }

        private static float3 ToFloat3(Vector3 value) =>
            new(value.x, value.y, value.z);

        internal void SimulateFixedTick(float delta)
        {
            if (!_initialized) return;
            CompletePendingCrowdMovement(true);
            _fixedStepDelta = delta;
            try
            {
                using (RecoilCpuPhases.Measure(RecoilCpuPhase.PrepareMovement)) UpdateCrowdMovement();
                CompletePendingCrowdMovement(true);
            }
            finally { _fixedStepDelta = 0f; }
        }

        private void UpdateCrowdMovement()
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            try { UpdateCrowdMovementCore(); }
            finally
            {
                LastMovementScheduleMilliseconds = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - start) *
                    (1000.0 / System.Diagnostics.Stopwatch.Frequency));
            }
        }

        private void UpdateCrowdMovementCore()
        {
            if (_crowdMovementPending && _crowdMovementHandle.IsCompleted)
                CompletePendingCrowdMovement(true);

            _crowdSimulationAccumulator += _fixedStepDelta > 0f ? _fixedStepDelta : Time.deltaTime;
            var simulationInterval = _fixedStepDelta > 0f ? _fixedStepDelta : 1f / crowdSimulationRate;
            if (_crowdSimulationAccumulator < simulationInterval)
                return;

            if (_crowdMovementPending)
            {
                _crowdSimulationAccumulator = Mathf.Min(_crowdSimulationAccumulator, simulationInterval);
                return;
            }

            var deltaTime = _fixedStepDelta > 0f ? _fixedStepDelta : _crowdSimulationAccumulator;
            _crowdSimulationAccumulator = 0f;

            if (_agents.Count == 0) return;
            EnsureCrowdJobCapacity(_agents.Count);
            _pendingCrowdAgents.Clear();
            _useChunkApply = true;
            if (!_hasMovementApplyQuery)
            {
                _movementWorld = World.DefaultGameObjectInjectionWorld;
                if (_movementWorld != null && _movementWorld.IsCreated)
                {
                    _movementApplyQuery = _movementWorld.EntityManager.CreateEntityQuery(
                        ComponentType.ReadWrite<RecoilUnitPose>(), ComponentType.ReadWrite<RecoilUnitNavigation>());
                    _movementPrepareQuery = _movementWorld.EntityManager.CreateEntityQuery(
                        ComponentType.ReadOnly<RecoilUnitPose>(), ComponentType.ReadOnly<RecoilUnitMovementSettings>(),
                        ComponentType.ReadWrite<RecoilUnitNavigation>(), ComponentType.ReadOnly<RecoilUnitWaypoint>());
                    _hasMovementApplyQuery = true;
                    _movementRosterDirty = true;
                }
            }
            RefreshMovementRoster();
            _pendingCrowdAgents.AddRange(_movementRoster);
            _useChunkApply = _movementRosterSingleWorld;
            if (_pendingCrowdAgents.Count == 0) return;
            var resetInputs = new ResetCrowdInputsJob { Inputs = _crowdMovementInputs }
                .Schedule(_pendingCrowdAgents.Count, 64);
            var preparation = default(JobHandle);
            if (_useChunkApply)
            {
                var manager = _movementWorld.EntityManager;
                var prepare = new PrepareCrowdMovementJob
                {
                    Entities = manager.GetEntityTypeHandle(),
                    Poses = manager.GetComponentTypeHandle<RecoilUnitPose>(true),
                    Settings = manager.GetComponentTypeHandle<RecoilUnitMovementSettings>(true),
                    Navigations = manager.GetComponentTypeHandle<RecoilUnitNavigation>(false),
                    Waypoints = manager.GetBufferTypeHandle<RecoilUnitWaypoint>(true),
                    Indices = _crowdEntityIndices, Inputs = _crowdMovementInputs,
                    Cells = _crowdAgentsByCell.AsParallelWriter(), DeltaTime = deltaTime, CellSize = crowdCellSize,
                    SkipGridBuild = 1
                };
                using (ChunkPrepareMarker.Auto())
                    preparation = prepare.ScheduleParallel(_movementPrepareQuery, resetInputs);
            }
            else
            {
                resetInputs.Complete();
                // Retain compatibility for a service used with units from several ECS Worlds.
                for (var index = 0; index < _pendingCrowdAgents.Count; index++)
                {
                    var agent = _pendingCrowdAgents[index];
                    if (!agent.CaptureMovement(out var pose, out var settings, out var navigation,
                        out var waypoint, out var hasPath)) continue;
                    var hasNext = agent.GetNextWaypoint(out var nextWaypoint, out var hasAnother);
                    _crowdMovementInputs[index] = new CrowdMovementInput
                    {
                        MovementVersion = navigation.MovementVersion, BodyRadius = settings.BodyRadius,
                        Mass = settings.Mass, PushResistant = settings.PushResistant,
                        SurfaceTriangle = navigation.SurfaceTriangle, AreaMask = settings.AreaMask,
                        VerticalOffset = settings.VerticalOffset, Position = pose.Position,
                        Waypoint = waypoint, Rotation = pose.Rotation, CurrentSpeed = pose.Speed,
                        WaypointIndex = navigation.WaypointIndex, NextWaypoint = nextWaypoint,
                        HasNextWaypoint = (byte)(hasNext ? 1 : 0), NextHasNextWaypoint = (byte)(hasAnother ? 1 : 0),
                        MaxSpeed = settings.MaxSpeed, Acceleration = settings.Acceleration,
                        TurnSpeedDegrees = settings.TurnSpeedDegrees,
                        AvoidanceStrength = settings.AvoidanceStrength, AvoidanceRadius = settings.AvoidanceRadius,
                        DeltaTime = deltaTime, InstanceId = agent.Entity.Index, HasPath = (byte)(hasPath ? 1 : 0)
                    };
                }
            }
            var gridUpdate = new UpdateCrowdGridJob
            {
                Inputs = _crowdMovementInputs, Cells = _crowdAgentsByCell, PreviousCells = _crowdPreviousCells,
                PreviousActive = _crowdPreviousActive, Metadata = _crowdGridMetadata,
                Count = _pendingCrowdAgents.Count, CellSize = crowdCellSize, MaxBodyRadius = _crowdMaxBodyRadius
            }.Schedule(preparation);
            _crowdMovementHandle = new SimulateCrowdMovementJob
            {
                TerrainHeights = _terrainHeightField,
                Triangles = _nativeTriangles,
                TrianglesByCell = _trianglesByCell,
                TriangleCellSize = _cellSize,
                BodyRadiusLimit = _crowdMaxBodyRadius,
                AgentsByCell = _crowdAgentsByCell,
                Inputs = _crowdMovementInputs,
                Outputs = _crowdMovementOutputs,
                CellSize = crowdCellSize,
                MaxNeighbors = maxAvoidanceNeighbors
            }.Schedule(_pendingCrowdAgents.Count, 64, gridUpdate);
            _crowdMovementHandle = new CollectCrowdResultsJob
            {
                Inputs = _crowdMovementInputs, Outputs = _crowdMovementOutputs,
                Fallback = _crowdFallbackIndices, Summary = _crowdResultSummary, Count = _pendingCrowdAgents.Count
            }.Schedule(_crowdMovementHandle);
            _crowdMovementPending = true;
            // Release chunk/buffer access before other systems issue ECS commands or render reads.
            // Grid and native-only movement remain scheduled as one dependency chain.
            preparation.Complete();
        }

        private void CompletePendingCrowdMovement(bool applyResults)
        {
            if (!_crowdMovementPending) return;
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            try { CompletePendingCrowdMovementCore(applyResults); }
            finally
            {
                LastMovementApplyMilliseconds = math.max(0f, (float)((System.Diagnostics.Stopwatch.GetTimestamp() - start) *
                    (1000.0 / System.Diagnostics.Stopwatch.Frequency)) - LastMovementWaitMilliseconds);
            }
        }

        private void CompletePendingCrowdMovementCore(bool applyResults)
        {
            if (!_crowdMovementPending)
                return;

            var waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using (RecoilCpuPhases.Measure(RecoilCpuPhase.WaitMovement)) _crowdMovementHandle.Complete();
            LastMovementWaitMilliseconds = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - waitStart) *
                (1000.0 / System.Diagnostics.Stopwatch.Frequency));
            LastCrowdGridMutations = _crowdGridMetadata.IsCreated ? _crowdGridMetadata[1] : 0;
            if (applyResults)
            {
                if (_useChunkApply && _movementWorld != null && _movementWorld.IsCreated)
                {
                    var manager = _movementWorld.EntityManager;
                    var job = new ApplyCrowdMovementJob
                    {
                        Entities = manager.GetEntityTypeHandle(),
                        Poses = manager.GetComponentTypeHandle<RecoilUnitPose>(false),
                        Navigations = manager.GetComponentTypeHandle<RecoilUnitNavigation>(false),
                        Indices = _crowdEntityIndices, Inputs = _crowdMovementInputs, Outputs = _crowdMovementOutputs
                    };
                    using (ChunkApplyMarker.Auto())
                    using (RecoilCpuPhases.Measure(RecoilCpuPhase.ApplyMovement))
                        job.ScheduleParallel(_movementApplyQuery, default).Complete();
                }
                else _useChunkApply = false;
                LastSurfaceFastCount = _crowdResultSummary[0];
                LastSurfaceFallbackCount = _crowdResultSummary[1];
                LastTerrainRecoveryCount = _crowdResultSummary[3];
                LastTerrainHeldCount = _crowdResultSummary[4];
                LastPortalCrossingCount = _crowdResultSummary[2];
                using (RecoilCpuPhases.Measure(RecoilCpuPhase.FallbackMovement))
                {
                if (_useChunkApply)
                {
                    for (var i = 0; i < _crowdFallbackIndices.Length; i++)
                        ApplyManagedCrowdResult(_crowdFallbackIndices[i]);
                }
                else
                {
                    // Compatibility path for mixed Worlds: normal World uses only the fallback list above.
                    for (var i = 0; i < _pendingCrowdAgents.Count; i++) ApplyManagedCrowdResult(i);
                }
                }
                var observed = RecoilUnitSimulation.DebugObserved;
                if (observed != null && _crowdEntityIndices.TryGetValue(observed.Entity, out var observedIndex) &&
                    observedIndex < _pendingCrowdAgents.Count && ReferenceEquals(_pendingCrowdAgents[observedIndex], observed) &&
                    _crowdMovementInputs[observedIndex].Inactive == 0)
                {
                    var input = _crowdMovementInputs[observedIndex];
                    var output = _crowdMovementOutputs[observedIndex];
                    observed.RecordMovementDebug(input.Position, output.CandidatePosition, output.Speed,
                        output.SurfaceProjected != 0, input.HasPath != 0, input.MovementVersion,
                        output.PlannedDistance, output.ContactDistance, input.DeltaTime, input.Waypoint, Time.time);
                }
            }
            _pendingCrowdAgents.Clear();
            _crowdMovementPending = false;
        }

        private void ApplyManagedCrowdResult(int index)
        {
            var agent = _pendingCrowdAgents[index];
            var input = _crowdMovementInputs[index];
            if (agent == null || input.Inactive != 0) return;
            var output = _crowdMovementOutputs[index];
            if (output.AdvanceCorner != 0)
                using (RecoilCpuPhases.Measure(RecoilCpuPhase.FallbackCorner))
                    agent.AdvanceCorner(input.MovementVersion, input.WaypointIndex);
            if (output.SurfaceProjected != 0)
                agent.ApplySurfaceMovement(output.CandidatePosition, output.Rotation, output.Speed,
                    output.SurfaceTriangle, input.MovementVersion, input.DeltaTime);
            else if (UsingTerrainMap)
            {
                // Terrain recovery belongs to the worker. Never repeat a search on the main thread.
                using (RecoilCpuPhases.Measure(RecoilCpuPhase.FallbackWrite))
                    agent.ApplySurfaceMovement(input.Position, output.Rotation, 0f, input.SurfaceTriangle,
                        input.MovementVersion, input.DeltaTime);
            }
            else
                agent.ApplyMovement(output.CandidatePosition, output.PathMovement, output.Separation,
                    output.Speed, output.Rotation, input.MovementVersion, input.DeltaTime);
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

            FlowField field;
            if (_spareFields.Count > 0)
            {
                field = _spareFields.Pop();
                field.Key = key; field.Complete = false; field.Handle = default;
                field.LastUsedTime = Time.time; field.PendingDeliveries = 0;
                field.Corridors.Clear();
                FlowFieldReuseCount++;
            }
            else
            {
                field = new FlowField(key, TriangleCount, Time.time);
                FlowFieldAllocationCount++;
            }
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
                if (!candidate.Complete || candidate.Requests.Count != 0 || candidate.PendingDeliveries != 0)
                    continue;
                if (oldest == null || candidate.LastUsedTime < oldest.LastUsedTime)
                    oldest = candidate;
            }

            if (oldest == null)
                return false;

            _fields.Remove(oldest.Key);
            _fieldOrder.Remove(oldest);
            if (_spareFields.Count < maxCachedFields) _spareFields.Push(oldest);
            else oldest.Dispose();
            return true;
        }

        private void ProcessFlowFields()
        {
            LastPathCompletions = 0;
            using (FieldMarker.Auto())
            {
            StartQueuedFields();
            for (var i = 0; i < _fieldOrder.Count; i++)
            {
                var field = _fieldOrder[i];
                if (!field.Complete && field.Handle.IsCompleted)
                    CompleteField(field);
            }
            }

            var deliveryStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using (DeliveryMarker.Auto()) DeliverReadyPaths();
            LastPathDeliveryMilliseconds = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - deliveryStart) *
                (1000.0 / System.Diagnostics.Stopwatch.Frequency));
            using (FieldMarker.Auto()) StartQueuedFields();
        }

        private void QueueReady(FlowField field, PendingRequest request)
        {
            if (!request.Owner.IsCurrent()) { LastCancelledPaths++; return; }
            if (field != null) field.PendingDeliveries++;
            _readyPaths.Enqueue(new ReadyRequest(field, request));
        }

        private void DeliverReadyPaths()
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            var budget = math.max(0.1f, pathCompletionBudgetMilliseconds);
            var limit = math.max(1, maxPathCompletionsPerFrame);
            var inspected = 0;
            while (_readyPaths.Count > 0 && inspected < limit)
            {
                if (inspected > 0 && (System.Diagnostics.Stopwatch.GetTimestamp() - start) *
                    (1000.0 / System.Diagnostics.Stopwatch.Frequency) >= budget) break;
                var ready = _readyPaths.Dequeue();
                inspected++;
                var cancelled = false;
                try
                {
                    if (!ready.Request.Owner.IsCurrent()) { cancelled = true; LastCancelledPaths++; continue; }
                    if (ready.Field == null)
                    {
                        DirectPathCount++;
                        ready.Request.Callback(RecoilPathResult.Success(new[] { ready.Request.Start, ready.Request.Goal }));
                    }
                    else CompleteRequest(ready.Field, ready.Request);
                }
                finally
                {
                    if (ready.Field != null) ready.Field.PendingDeliveries--;
                    if (!cancelled) LastPathCompletions++;
                }
            }
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
            for (var i = 0; i < field.Requests.Count; i++) QueueReady(field, field.Requests[i]);
            field.Requests.Clear();
        }

        private void CompleteRequest(FlowField field, PendingRequest request)
        {
            if (float.IsPositiveInfinity(field.Costs[request.StartTriangle]))
            {
                request.Callback(RecoilPathResult.Failure("No route exists between the requested NavMesh regions."));
                return;
            }

            var points = UsingTerrainMap ? null : _pathMidpoints;
            if (points != null) { points.Clear(); points.Add(request.Start); }
            IReadOnlyList<RecoilPathPortal> portals = null;
            var cacheHit = UsingTerrainMap && field.Corridors.TryGet(request.StartTriangle, out portals);
            if (!cacheHit) portals = _pathPortals;
            else SharedCorridorHits++;
            _pathPortals.Clear();
            var current = request.StartTriangle;
            var traversed = 0;
            while (!cacheHit && current != field.Key.GoalTriangle && traversed++ < TriangleCount)
            {
                if (UsingTerrainMap && current != request.StartTriangle && field.Corridors.TryGet(current, out var suffix))
                {
                    for (var i = 0; i < suffix.Count; i++) _pathPortals.Add(suffix[i]);
                    SharedCorridorSuffixHits++;
                    current = field.Key.GoalTriangle;
                    break;
                }
                var next = field.NextTriangle[current];
                if (next < 0)
                {
                    request.Callback(RecoilPathResult.Failure("The shared path field contains no route to the destination."));
                    return;
                }

                if (points != null && !TryGetPortal(current, next, out _))
                {
                    request.Callback(RecoilPathResult.Failure("The NavMesh corridor contains disconnected triangles."));
                    return;
                }

                if (points != null) { TryGetPortal(current, next, out var midpoint); points.Add(midpoint); }
                if (!TryGetPortalEdges(current, next, out var edges))
                {
                    request.Callback(RecoilPathResult.Failure("Could not reconstruct the shared NavMesh portal."));
                    return;
                }
                _pathPortals.Add(edges);
                current = next;
            }

            if (!cacheHit && current != field.Key.GoalTriangle)
            {
                request.Callback(RecoilPathResult.Failure("The NavMesh route exceeded the triangle traversal limit."));
                return;
            }

            if (points != null) points.Add(request.Goal);
            RecoilPathFunnel.BuildInto(request.Start, request.Goal, portals, _pathResult);
            if (UsingTerrainMap && !cacheHit) field.Corridors.Store(request.StartTriangle, _pathPortals);
            var path = _pathResult.ToArray();
            var kind = RecoilPathKind.Funnel;
            // Unity connectivity guards apply only to the NavMesh backend. Terrain corridors
            // already come from the conforming quadtree graph and have no stacked surfaces.
            for (var i = 1; !UsingTerrainMap && i < path.Length; i++)
            {
                if (!NavMesh.Raycast(path[i - 1], path[i], out _, request.AreaMask)) continue;
                _lastRejectedFrom = path[i - 1];
                _lastRejectedTo = path[i];
                _hasRejectedFunnel = true;
                kind = RecoilPathKind.MidpointFallback;
                path = SmoothPath(points, request.AreaMask, 12);
                break;
            }
            if (UsingTerrainMap)
                for (var i = 0; i < path.Length; i++)
                {
                    var point = path[i];
                    point.y = _terrainHeightField.Height(new float2(point.x, point.z));
                    path[i] = point;
                }
            if (kind == RecoilPathKind.Funnel) FunnelPathCount++;
            else MidpointFallbackCount++;
            request.Callback(RecoilPathResult.Success(path, kind));
        }

        private bool TryGetPortalEdges(int from, int to, out RecoilPathPortal portal)
        {
            if (!_nativePortals.IsCreated) return ComputePortalEdges(from, to, out portal);
            var triangle = _nativeTriangles[from];
            for (var edge = 0; edge < 3; edge++)
                if (triangle.GetNeighbor(edge) == to && _nativePortalValid[from * 3 + edge] != 0)
                { portal = _nativePortals[from * 3 + edge]; return true; }
            portal = default;
            return false;
        }

        private bool ComputePortalEdges(int from, int to, out RecoilPathPortal portal)
        {
            var a = _nativeTriangles[from];
            var b = _nativeTriangles[to];
            var first = float3.zero;
            var second = float3.zero;
            var count = 0;
            for (var i = 0; i < 3; i++)
            {
                var vertex = i == 0 ? a.A : i == 1 ? a.B : a.C;
                var key = new VertexKey(ToVector3(vertex));
                if (!key.Equals(new VertexKey(ToVector3(b.A))) &&
                    !key.Equals(new VertexKey(ToVector3(b.B))) &&
                    !key.Equals(new VertexKey(ToVector3(b.C)))) continue;
                if (count++ == 0) first = vertex;
                else second = vertex;
            }
            portal = default;
            if (count != 2) return false;
            var direction = b.Center.xz - a.Center.xz;
            var side = first.xz - (first.xz + second.xz) * 0.5f;
            var firstIsLeft = direction.x * side.y - direction.y * side.x >= 0f;
            portal = new RecoilPathPortal(ToVector3(firstIsLeft ? first : second), ToVector3(firstIsLeft ? second : first));
            return true;
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

    public enum RecoilPathKind : byte { Direct, Funnel, MidpointFallback }

    public sealed class RecoilPathResult
    {
        public bool Succeeded { get; }
        public IReadOnlyList<Vector3> Points { get; }
        public string Error { get; }
        public RecoilPathKind Kind { get; }

        private RecoilPathResult(bool succeeded, IReadOnlyList<Vector3> points, string error, RecoilPathKind kind = RecoilPathKind.Direct)
        {
            Succeeded = succeeded;
            Points = points;
            Error = error;
            Kind = kind;
        }

        public static RecoilPathResult Success(IReadOnlyList<Vector3> points, RecoilPathKind kind = RecoilPathKind.Direct) =>
            new(true, points, null, kind);

        public static RecoilPathResult Failure(string error) =>
            new(false, Array.Empty<Vector3>(), error);
    }
}
