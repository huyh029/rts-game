// Adapted from RecoilEngine QTPFS path-search concepts.
// RecoilEngine is GPL-2.0-or-later; this adapted file is distributed under the same terms.
// See Assets/_Game/RecoilEngine/LICENSE and its GPL license texts.

using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace RecoilPathfinding
{
    // QTPFS-style individual searches: a goal requested by few units is solved by a goal-directed
    // A* on worker threads instead of a whole-map flow field, and popular goals keep sharing one
    // field. Corridor reconstruction and funnel smoothing run in the same Burst job, so the main
    // thread only hands out finished waypoint arrays.
    public sealed partial class RecoilNavMeshPathService
    {
        private const int MaxSearchCorridor = 4096;
        private const int MaxSearchPoints = 256;
        private const int MaxSearchLanes = 8;
        private static readonly RecoilCpuPhases.Marker SearchMarker = new("RecoilPathfinding.PathSearch");

        [Tooltip("Goals requested at least this many times in one batch share a flow field; rarer goals use a per-request A* search.")]
        [SerializeField, Min(1)] private int sharedFieldMinRequests = 4;
        [SerializeField, Min(1)] private int maxSearchesPerBatch = 256;

        public int SearchPathCount { get; private set; }
        public int SearchFallbackCount { get; private set; }
        public int StaleFieldDeliveryCount { get; private set; }
        public int PendingSearches => _searchQueue.Count + _searchBatch.Count;
        public int PendingFieldDeliveries => _fieldDeliveryQueue.Count + _fieldDeliveryWave.Count;

        private readonly List<PendingRequest> _searchQueue = new();
        private readonly List<PendingRequest> _searchBatch = new();
        private readonly List<(FieldKey Key, PendingRequest Request)> _routedRequests = new();
        private readonly Dictionary<FieldKey, int> _routedGoalCounts = new();
        private JobHandle _searchHandle;
        private bool _searchInFlight;
        private int _searchLanes;
        private NativeArray<SearchRequest> _searchRequests;
        private NativeArray<float3> _searchPoints;
        private NativeArray<int> _searchPointCounts;
        private NativeArray<float> _searchCosts;
        private NativeArray<float> _searchScores;
        private NativeArray<int> _searchParents;
        private NativeArray<int> _searchStamps;
        private NativeArray<int> _searchHeap;
        private NativeArray<int> _searchHeapPositions;
        private NativeArray<int> _searchLaneStamps;
        private NativeArray<int> _searchCorridors;
        private NativeArray<RecoilPathPortal> _searchPortals;
        // Shared-field deliveries: one wave of corridor walks and funnels in flight at a time.
        private readonly List<(FlowField Field, PendingRequest Request)> _fieldDeliveryQueue = new();
        private readonly List<(FlowField Field, PendingRequest Request)> _fieldDeliveryWave = new();
        private readonly List<(FlowField Field, PendingRequest Request)> _groupedFieldDeliveries = new();
        private readonly List<PendingRequest> _staleFieldDeliveryRequests = new();
        private bool _reportedStaleFieldDelivery;
        private JobHandle _fieldDeliveryHandle;
        private bool _fieldDeliveryInFlight;
        private NativeArray<SearchRequest> _deliveryRequests;
        private NativeArray<float3> _deliveryPoints;
        private NativeArray<int> _deliveryCounts;
        private NativeArray<RecoilPathPortal> _deliveryPortals;

        // The NavMesh backend validates funnels with managed raycasts; it keeps the field path.
        private bool UsePerRequestSearch => UsingTerrainMap && _nativePortals.IsCreated && _nativeTriangles.IsCreated;

        private struct SearchRequest
        {
            public int Start;
            public int Goal;
            public int AreaMask;
            public float3 StartPosition;
            public float3 GoalPosition;
        }

        // Requests of one projection batch are counted per goal before choosing field or search.
        private void RoutePathRequest(FieldKey key, PendingRequest request)
        {
            if (!UsePerRequestSearch || _fields.ContainsKey(key) || _waitingRequests.ContainsKey(key))
            {
                EnqueuePathRequest(key, request);
                return;
            }
            _routedRequests.Add((key, request));
            _routedGoalCounts[key] = _routedGoalCounts.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        private void FlushRoutedRequests()
        {
            foreach (var (key, request) in _routedRequests)
            {
                if (_fields.ContainsKey(key) || _waitingRequests.ContainsKey(key) ||
                    _routedGoalCounts[key] >= sharedFieldMinRequests)
                    EnqueuePathRequest(key, request);
                else
                    _searchQueue.Add(request);
            }
            _routedRequests.Clear();
            _routedGoalCounts.Clear();
        }

        // One batch is in flight at a time: the lanes own the shared search scratch.
        private void ScheduleSearchBatch()
        {
            if (_searchInFlight || _searchQueue.Count == 0) return;
            if (!UsePerRequestSearch)
            {
                foreach (var request in _searchQueue)
                    EnqueuePathRequest(new FieldKey(request.GoalTriangle, request.AreaMask), request);
                _searchQueue.Clear();
                return;
            }
            using (SearchMarker.Auto())
            {
                EnsureSearchBuffers();
                var taken = 0;
                while (taken < _searchQueue.Count && _searchBatch.Count < maxSearchesPerBatch)
                {
                    var request = _searchQueue[taken++];
                    if (!request.Owner.IsCurrent()) { LastCancelledPaths++; continue; }
                    _searchRequests[_searchBatch.Count] = new SearchRequest
                    {
                        Start = request.StartTriangle, Goal = request.GoalTriangle, AreaMask = request.AreaMask,
                        StartPosition = request.Start, GoalPosition = request.Goal
                    };
                    _searchBatch.Add(request);
                }
                _searchQueue.RemoveRange(0, taken);
                if (_searchBatch.Count == 0) return;
                // Admissible: no edge is cheaper than its length times the smallest area cost
                // (areas outside the cost table cost 1).
                var heuristicScale = 1f;
                for (var i = 0; i < _nativeAreaCosts.Length; i++) heuristicScale = math.min(heuristicScale, _nativeAreaCosts[i]);
                _searchHandle = new FindCorridorsJob
                {
                    Triangles = _nativeTriangles, AreaCosts = _nativeAreaCosts,
                    Portals = _nativePortals, PortalValid = _nativePortalValid, TerrainHeights = _terrainHeightField,
                    Requests = _searchRequests, RequestCount = _searchBatch.Count, Lanes = _searchLanes,
                    TriangleCount = TriangleCount, HeuristicScale = math.max(0f, heuristicScale),
                    Costs = _searchCosts, Scores = _searchScores, Parents = _searchParents, Stamps = _searchStamps,
                    Heap = _searchHeap, HeapPositions = _searchHeapPositions, LaneStamps = _searchLaneStamps,
                    Corridors = _searchCorridors, PortalScratch = _searchPortals,
                    Points = _searchPoints, PointCounts = _searchPointCounts
                }.Schedule(_searchLanes, 1);
                JobHandle.ScheduleBatchedJobs();
                _searchInFlight = true;
            }
        }

        private void CompleteSearchBatch(bool wait = false)
        {
            if (!_searchInFlight || (!wait && !_searchHandle.IsCompleted)) return;
            using (SearchMarker.Auto())
            {
                _searchHandle.Complete();
                _searchInFlight = false;
                for (var i = 0; i < _searchBatch.Count; i++)
                {
                    var request = _searchBatch[i];
                    var count = _searchPointCounts[i];
                    if (count < 0)
                    {
                        // Corridor or waypoint capacity exceeded: the shared field has no such limit.
                        SearchFallbackCount++;
                        EnqueuePathRequest(new FieldKey(request.GoalTriangle, request.AreaMask), request);
                        continue;
                    }
                    if (!request.Owner.IsCurrent()) { LastCancelledPaths++; continue; }
                    if (count == 0)
                    {
                        _readyPaths.Enqueue(new ReadyRequest(null, request, null,
                            "No route exists between the requested NavMesh regions."));
                        continue;
                    }
                    var path = new Vector3[count];
                    for (var k = 0; k < count; k++) path[k] = _searchPoints[i * MaxSearchPoints + k];
                    SearchPathCount++;
                    _readyPaths.Enqueue(new ReadyRequest(null, request, path, null));
                }
                _searchBatch.Clear();
            }
        }

        private void EnsureSearchBuffers()
        {
            var lanes = math.clamp(JobsUtility.JobWorkerCount + 1, 1, MaxSearchLanes);
            var scratch = lanes * TriangleCount;
            if (_searchLanes != lanes || !_searchCosts.IsCreated || _searchCosts.Length != scratch)
            {
                DisposeSearchScratch();
                _searchLanes = lanes;
                _searchCosts = new NativeArray<float>(scratch, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _searchScores = new NativeArray<float>(scratch, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _searchParents = new NativeArray<int>(scratch, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _searchHeap = new NativeArray<int>(scratch, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _searchHeapPositions = new NativeArray<int>(scratch, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                // Stamps start cleared: a node is unseen until it carries the lane's current stamp.
                _searchStamps = new NativeArray<int>(scratch, Allocator.Persistent);
                _searchLaneStamps = new NativeArray<int>(lanes, Allocator.Persistent);
                _searchCorridors = new NativeArray<int>(lanes * MaxSearchCorridor, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _searchPortals = new NativeArray<RecoilPathPortal>(lanes * MaxSearchCorridor, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            if (!_searchRequests.IsCreated || _searchRequests.Length != maxSearchesPerBatch)
            {
                if (_searchRequests.IsCreated) _searchRequests.Dispose();
                if (_searchPoints.IsCreated) _searchPoints.Dispose();
                if (_searchPointCounts.IsCreated) _searchPointCounts.Dispose();
                _searchRequests = new NativeArray<SearchRequest>(maxSearchesPerBatch, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _searchPoints = new NativeArray<float3>(maxSearchesPerBatch * MaxSearchPoints, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _searchPointCounts = new NativeArray<int>(maxSearchesPerBatch, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
        }

        private void DisposeSearchResources()
        {
            _searchHandle.Complete();
            _searchHandle = default;
            _searchInFlight = false;
            DisposeSearchScratch();
            if (_searchRequests.IsCreated) _searchRequests.Dispose();
            if (_searchPoints.IsCreated) _searchPoints.Dispose();
            if (_searchPointCounts.IsCreated) _searchPointCounts.Dispose();
            _searchRequests = default; _searchPoints = default; _searchPointCounts = default;
            _searchQueue.Clear();
            _searchBatch.Clear();
            _routedRequests.Clear();
            _routedGoalCounts.Clear();
            _fieldDeliveryHandle.Complete();
            _fieldDeliveryHandle = default;
            _fieldDeliveryInFlight = false;
            if (_deliveryRequests.IsCreated) _deliveryRequests.Dispose();
            if (_deliveryPoints.IsCreated) _deliveryPoints.Dispose();
            if (_deliveryCounts.IsCreated) _deliveryCounts.Dispose();
            if (_deliveryPortals.IsCreated) _deliveryPortals.Dispose();
            _deliveryRequests = default; _deliveryPoints = default; _deliveryCounts = default; _deliveryPortals = default;
            _fieldDeliveryQueue.Clear();
            _fieldDeliveryWave.Clear();
            _groupedFieldDeliveries.Clear();
            _staleFieldDeliveryRequests.Clear();
        }

        private void DisposeSearchScratch()
        {
            if (_searchCosts.IsCreated) _searchCosts.Dispose();
            if (_searchScores.IsCreated) _searchScores.Dispose();
            if (_searchParents.IsCreated) _searchParents.Dispose();
            if (_searchStamps.IsCreated) _searchStamps.Dispose();
            if (_searchHeap.IsCreated) _searchHeap.Dispose();
            if (_searchHeapPositions.IsCreated) _searchHeapPositions.Dispose();
            if (_searchLaneStamps.IsCreated) _searchLaneStamps.Dispose();
            if (_searchCorridors.IsCreated) _searchCorridors.Dispose();
            if (_searchPortals.IsCreated) _searchPortals.Dispose();
            _searchCosts = default; _searchScores = default; _searchParents = default; _searchStamps = default;
            _searchHeap = default; _searchHeapPositions = default; _searchLaneStamps = default;
            _searchCorridors = default; _searchPortals = default;
            _searchLanes = 0;
        }

        // Requests of one wave are grouped per field; each field's job walks its NextTriangle chain.
        // The jobs share the portal scratch and output buffers, so they are chained one after another
        // and each is parallel over the lanes. Fields stay pinned by PendingDeliveries meanwhile.
        private void ScheduleFieldDeliveries()
        {
            if (_fieldDeliveryInFlight || _fieldDeliveryQueue.Count == 0) return;
            using (DeliveryMarker.Auto())
            {
                var lanes = math.clamp(JobsUtility.JobWorkerCount + 1, 1, MaxSearchLanes);
                if (!_deliveryRequests.IsCreated || _deliveryRequests.Length != maxSearchesPerBatch)
                {
                    if (_deliveryRequests.IsCreated) _deliveryRequests.Dispose();
                    if (_deliveryPoints.IsCreated) _deliveryPoints.Dispose();
                    if (_deliveryCounts.IsCreated) _deliveryCounts.Dispose();
                    _deliveryRequests = new NativeArray<SearchRequest>(maxSearchesPerBatch, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    _deliveryPoints = new NativeArray<float3>(maxSearchesPerBatch * MaxSearchPoints, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                    _deliveryCounts = new NativeArray<int>(maxSearchesPerBatch, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                }
                if (!_deliveryPortals.IsCreated || _deliveryPortals.Length != lanes * MaxSearchCorridor)
                {
                    if (_deliveryPortals.IsCreated) _deliveryPortals.Dispose();
                    _deliveryPortals = new NativeArray<RecoilPathPortal>(lanes * MaxSearchCorridor, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                }
                var taken = math.min(_fieldDeliveryQueue.Count, maxSearchesPerBatch);
                _fieldDeliveryWave.Clear();
                _staleFieldDeliveryRequests.Clear();
                for (var i = 0; i < taken; i++)
                {
                    var delivery = _fieldDeliveryQueue[i];
                    var key = new FieldKey(delivery.Request.GoalTriangle, delivery.Request.AreaMask);
                    if (_fields.TryGetValue(key, out var cached) && ReferenceEquals(cached, delivery.Field))
                        _fieldDeliveryWave.Add(delivery);
                    else
                        _staleFieldDeliveryRequests.Add(delivery.Request);
                }
                _fieldDeliveryQueue.RemoveRange(0, taken);
                if (_staleFieldDeliveryRequests.Count > 0)
                {
                    StaleFieldDeliveryCount += _staleFieldDeliveryRequests.Count;
                    if (!_reportedStaleFieldDelivery)
                    {
                        Debug.LogWarning(
                            $"Rerouting {_staleFieldDeliveryRequests.Count} path deliveries whose flow field is no longer cached. " +
                            "Further stale deliveries will be counted without repeating this warning.", this);
                        _reportedStaleFieldDelivery = true;
                    }
                    foreach (var request in _staleFieldDeliveryRequests)
                    {
                        if (!request.Owner.IsCurrent())
                        {
                            LastCancelledPaths++;
                            continue;
                        }
                        EnqueuePathRequest(new FieldKey(request.GoalTriangle, request.AreaMask), request);
                    }
                    _staleFieldDeliveryRequests.Clear();
                }
                if (_fieldDeliveryWave.Count == 0) return;
                // Group by field without allocating: there are at most maxCachedFields distinct fields.
                _groupedFieldDeliveries.Clear();
                for (var f = 0; f < _fieldOrder.Count; f++)
                    for (var i = 0; i < _fieldDeliveryWave.Count; i++)
                        if (ReferenceEquals(_fieldDeliveryWave[i].Field, _fieldOrder[f]))
                            _groupedFieldDeliveries.Add(_fieldDeliveryWave[i]);
                _fieldDeliveryWave.Clear();
                _fieldDeliveryWave.AddRange(_groupedFieldDeliveries);
                _groupedFieldDeliveries.Clear();

                // Populate the shared request buffer before scheduling any job that reads it.
                // NativeArray safety tracks the whole array, so even writing a later, non-overlapping
                // range while an earlier field job is scheduled is not allowed.
                for (var i = 0; i < _fieldDeliveryWave.Count; i++)
                {
                    var (field, request) = _fieldDeliveryWave[i];
                    _deliveryRequests[i] = new SearchRequest
                    {
                        Start = request.StartTriangle,
                        Goal = field.Key.GoalTriangle,
                        AreaMask = request.AreaMask,
                        StartPosition = request.Start,
                        GoalPosition = request.Goal
                    };
                }

                var handle = default(JobHandle);
                for (var first = 0; first < _fieldDeliveryWave.Count;)
                {
                    var field = _fieldDeliveryWave[first].Field;
                    var end = first;
                    for (; end < _fieldDeliveryWave.Count &&
                           ReferenceEquals(_fieldDeliveryWave[end].Field, field); end++) { }
                    handle = new FieldCorridorsJob
                    {
                        Triangles = _nativeTriangles, Portals = _nativePortals, PortalValid = _nativePortalValid,
                        TerrainHeights = _terrainHeightField, Costs = field.Costs, NextTriangle = field.NextTriangle,
                        Requests = _deliveryRequests, First = first, Count = end - first, Lanes = lanes,
                        TriangleCount = TriangleCount, PortalScratch = _deliveryPortals,
                        Points = _deliveryPoints, PointCounts = _deliveryCounts
                    }.Schedule(lanes, 1, handle);
                    first = end;
                }
                JobHandle.ScheduleBatchedJobs();
                _fieldDeliveryHandle = handle;
                _fieldDeliveryInFlight = true;
            }
        }

        private bool HasQueuedFieldDelivery(FlowField field)
        {
            for (var i = 0; i < _fieldDeliveryQueue.Count; i++)
                if (ReferenceEquals(_fieldDeliveryQueue[i].Field, field))
                    return true;
            for (var i = 0; i < _fieldDeliveryWave.Count; i++)
                if (ReferenceEquals(_fieldDeliveryWave[i].Field, field))
                    return true;
            return false;
        }

        private void CompleteFieldDeliveries(bool wait = false)
        {
            if (!_fieldDeliveryInFlight || (!wait && !_fieldDeliveryHandle.IsCompleted)) return;
            using (DeliveryMarker.Auto())
            {
                _fieldDeliveryHandle.Complete();
                _fieldDeliveryInFlight = false;
                for (var i = 0; i < _fieldDeliveryWave.Count; i++)
                {
                    var (field, request) = _fieldDeliveryWave[i];
                    var count = _deliveryCounts[i];
                    // Every entry still holds its field pin; DeliverReadyPaths releases it.
                    if (count < 0) _readyPaths.Enqueue(new ReadyRequest(field, request));
                    else if (count == 0)
                        _readyPaths.Enqueue(new ReadyRequest(field, request, null, "No route exists between the requested NavMesh regions."));
                    else
                    {
                        var path = new Vector3[count];
                        for (var k = 0; k < count; k++) path[k] = _deliveryPoints[i * MaxSearchPoints + k];
                        _readyPaths.Enqueue(new ReadyRequest(field, request, path));
                    }
                }
                _fieldDeliveryWave.Clear();
            }
        }

        // Returns the waypoint count, 0 when the field has no route from the start, and -1 for any
        // other case (capacity, broken chain or portal), which the managed CompleteRequest reports.
        [BurstCompile]
        private struct FieldCorridorsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<NavTriangleData> Triangles;
            [ReadOnly] public NativeArray<RecoilPathPortal> Portals;
            [ReadOnly] public NativeArray<byte> PortalValid;
            [ReadOnly] public NativeArray<float> Costs;
            [ReadOnly] public NativeArray<int> NextTriangle;
            [ReadOnly] public NativeArray<SearchRequest> Requests;
            public RecoilTerrainHeightField TerrainHeights;
            public int First;
            public int Count;
            public int Lanes;
            public int TriangleCount;
            [NativeDisableParallelForRestriction] public NativeArray<RecoilPathPortal> PortalScratch;
            [NativeDisableParallelForRestriction] public NativeArray<float3> Points;
            [NativeDisableParallelForRestriction] public NativeArray<int> PointCounts;

            public void Execute(int lane)
            {
                for (var r = First + lane; r < First + Count; r += Lanes)
                    PointCounts[r] = Solve(lane, Requests[r], r * MaxSearchPoints);
            }

            private int Solve(int lane, SearchRequest request, int pointOffset)
            {
                if (float.IsPositiveInfinity(Costs[request.Start])) return 0;
                var portalOffset = lane * MaxSearchCorridor;
                var portalCount = 0;
                var current = request.Start;
                var traversed = 0;
                while (current != request.Goal)
                {
                    if (traversed++ >= TriangleCount || portalCount == MaxSearchCorridor) return -1;
                    var next = NextTriangle[current];
                    if (next < 0) return -1;
                    var triangle = Triangles[current];
                    var edge = triangle.Neighbor0 == next ? 0 : triangle.Neighbor1 == next ? 1 : triangle.Neighbor2 == next ? 2 : -1;
                    if (edge < 0 || PortalValid[current * 3 + edge] == 0) return -1;
                    PortalScratch[portalOffset + portalCount++] = Portals[current * 3 + edge];
                    current = next;
                }
                return NativeFunnel.Build(request.StartPosition, request.GoalPosition, PortalScratch, portalOffset, portalCount,
                    Points, pointOffset, TerrainHeights);
            }
        }

        // RecoilPathFunnel.BuildInto on native storage, shared by the Burst path builders. Waypoints
        // take the baked terrain height. Returns the waypoint count, or -1 when they overflow.
        private static class NativeFunnel
        {
            public static int Build(float3 start, float3 goal, NativeArray<RecoilPathPortal> portals, int portalOffset,
                int portalCount, NativeArray<float3> points, int pointOffset, RecoilTerrainHeightField heights)
            {
                var count = 0;
                points[pointOffset + count++] = start;
                var apex = start;
                var left = start;
                var right = start;
                var apexIndex = -1;
                var leftIndex = -1;
                var rightIndex = -1;
                for (var i = 0; i <= portalCount; i++)
                {
                    var nextLeft = i == portalCount ? goal : (float3)portals[portalOffset + i].Left;
                    var nextRight = i == portalCount ? goal : (float3)portals[portalOffset + i].Right;
                    if (Area(apex, right, nextRight) <= 0f)
                    {
                        if (Same(apex, right) || Area(apex, left, nextRight) > 0f)
                        { right = nextRight; rightIndex = i; }
                        else
                        {
                            if (!Append(points, pointOffset, ref count, left)) return -1;
                            apex = left; apexIndex = leftIndex;
                            left = right = apex;
                            leftIndex = rightIndex = apexIndex;
                            i = apexIndex;
                            continue;
                        }
                    }
                    if (Area(apex, left, nextLeft) >= 0f)
                    {
                        if (Same(apex, left) || Area(apex, right, nextLeft) < 0f)
                        { left = nextLeft; leftIndex = i; }
                        else
                        {
                            if (!Append(points, pointOffset, ref count, right)) return -1;
                            apex = right; apexIndex = rightIndex;
                            left = right = apex;
                            leftIndex = rightIndex = apexIndex;
                            i = apexIndex;
                        }
                    }
                }
                if (!Append(points, pointOffset, ref count, goal)) return -1;
                if (heights.IsCreated)
                    for (var i = 0; i < count; i++)
                    {
                        var point = points[pointOffset + i];
                        point.y = heights.Height(point.xz);
                        points[pointOffset + i] = point;
                    }
                return count;
            }

            private static bool Append(NativeArray<float3> points, int pointOffset, ref int count, float3 point)
            {
                if (math.distancesq(points[pointOffset + count - 1], point) <= 0.00000001f) return true;
                if (count == MaxSearchPoints) return false;
                points[pointOffset + count++] = point;
                return true;
            }

            private static float Area(float3 a, float3 b, float3 c) =>
                (b.z - a.z) * (c.x - a.x) - (b.x - a.x) * (c.z - a.z);
            private static bool Same(float3 a, float3 b) =>
                (a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z) < 0.00000001f;
        }

        // Each lane solves requests lane, lane + Lanes, ... with its own scratch slice. Edge costs
        // match BuildFlowFieldJob (center distance times the area cost of the node nearer the goal),
        // so a search finds a route of the same cost as the shared field.
        [BurstCompile]
        private struct FindCorridorsJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<NavTriangleData> Triangles;
            [ReadOnly] public NativeArray<float> AreaCosts;
            [ReadOnly] public NativeArray<RecoilPathPortal> Portals;
            [ReadOnly] public NativeArray<byte> PortalValid;
            [ReadOnly] public NativeArray<SearchRequest> Requests;
            public RecoilTerrainHeightField TerrainHeights;
            public int RequestCount;
            public int Lanes;
            public int TriangleCount;
            public float HeuristicScale;
            [NativeDisableParallelForRestriction] public NativeArray<float> Costs;
            [NativeDisableParallelForRestriction] public NativeArray<float> Scores;
            [NativeDisableParallelForRestriction] public NativeArray<int> Parents;
            [NativeDisableParallelForRestriction] public NativeArray<int> Stamps;
            [NativeDisableParallelForRestriction] public NativeArray<int> Heap;
            [NativeDisableParallelForRestriction] public NativeArray<int> HeapPositions;
            [NativeDisableParallelForRestriction] public NativeArray<int> LaneStamps;
            [NativeDisableParallelForRestriction] public NativeArray<int> Corridors;
            [NativeDisableParallelForRestriction] public NativeArray<RecoilPathPortal> PortalScratch;
            [NativeDisableParallelForRestriction] public NativeArray<float3> Points;
            [NativeDisableParallelForRestriction] public NativeArray<int> PointCounts;

            public void Execute(int lane)
            {
                for (var r = lane; r < RequestCount; r += Lanes)
                    PointCounts[r] = Solve(lane, Requests[r], r * MaxSearchPoints);
            }

            // Returns the waypoint count, 0 when no route exists, -1 when a capacity was exceeded.
            private int Solve(int lane, SearchRequest request, int pointOffset)
            {
                var offset = lane * TriangleCount;
                var stamp = LaneStamps[lane] + 1;
                if (stamp == int.MaxValue)
                {
                    for (var i = 0; i < TriangleCount; i++) Stamps[offset + i] = 0;
                    stamp = 1;
                }
                LaneStamps[lane] = stamp;
                // The field never admits a start whose area is excluded by the mask.
                if (request.Start != request.Goal && !IsAreaAllowed(request.AreaMask, Triangles[request.Start].Area)) return 0;

                var goalCenter = Triangles[request.Goal].Center;
                var heapCount = 0;
                Stamps[offset + request.Start] = stamp;
                Costs[offset + request.Start] = 0f;
                Parents[offset + request.Start] = -1;
                Scores[offset + request.Start] = math.distance(Triangles[request.Start].Center, goalCenter) * HeuristicScale;
                HeapPositions[offset + request.Start] = -1;
                PushOrDecrease(offset, request.Start, ref heapCount);
                var found = false;
                while (heapCount > 0)
                {
                    var current = PopMinimum(offset, ref heapCount);
                    if (current == request.Goal) { found = true; break; }
                    HeapPositions[offset + current] = -2;
                    var triangle = Triangles[current];
                    for (var edge = 0; edge < 3; edge++)
                    {
                        var next = triangle.GetNeighbor(edge);
                        if (next < 0) continue;
                        var neighbor = Triangles[next];
                        if (next != request.Goal && !IsAreaAllowed(request.AreaMask, neighbor.Area)) continue;
                        var areaCost = neighbor.Area >= 0 && neighbor.Area < AreaCosts.Length ? AreaCosts[neighbor.Area] : 1f;
                        var candidate = Costs[offset + current] + math.distance(triangle.Center, neighbor.Center) * areaCost;
                        if (Stamps[offset + next] == stamp)
                        {
                            if (HeapPositions[offset + next] == -2 || candidate >= Costs[offset + next]) continue;
                        }
                        else
                        {
                            Stamps[offset + next] = stamp;
                            HeapPositions[offset + next] = -1;
                        }
                        Costs[offset + next] = candidate;
                        Parents[offset + next] = current;
                        Scores[offset + next] = candidate + math.distance(neighbor.Center, goalCenter) * HeuristicScale;
                        PushOrDecrease(offset, next, ref heapCount);
                    }
                }
                if (!found) return 0;

                var corridorOffset = lane * MaxSearchCorridor;
                var length = 0;
                for (var node = request.Goal; node >= 0; node = Parents[offset + node])
                {
                    if (length == MaxSearchCorridor) return -1;
                    Corridors[corridorOffset + length++] = node;
                }
                // The corridor is stored goal-first; portals are emitted in travel order.
                var portalCount = 0;
                for (var i = length - 1; i > 0; i--)
                {
                    var from = Corridors[corridorOffset + i];
                    var to = Corridors[corridorOffset + i - 1];
                    var fromTriangle = Triangles[from];
                    var edgeIndex = fromTriangle.Neighbor0 == to ? 0 : fromTriangle.Neighbor1 == to ? 1 : fromTriangle.Neighbor2 == to ? 2 : -1;
                    if (edgeIndex < 0 || PortalValid[from * 3 + edgeIndex] == 0) return -1;
                    PortalScratch[corridorOffset + portalCount++] = Portals[from * 3 + edgeIndex];
                }
                return NativeFunnel.Build(request.StartPosition, request.GoalPosition, PortalScratch, corridorOffset, portalCount,
                    Points, pointOffset, TerrainHeights);
            }

            private static bool IsAreaAllowed(int areaMask, int area) =>
                area >= 0 && area < 32 && (areaMask & (1 << area)) != 0;

            private void PushOrDecrease(int offset, int node, ref int heapCount)
            {
                var position = HeapPositions[offset + node];
                if (position < 0)
                {
                    position = heapCount++;
                    Heap[offset + position] = node;
                }
                var score = Scores[offset + node];
                while (position > 0)
                {
                    var parent = (position - 1) / 2;
                    var parentNode = Heap[offset + parent];
                    if (Scores[offset + parentNode] <= score) break;
                    Heap[offset + position] = parentNode;
                    HeapPositions[offset + parentNode] = position;
                    position = parent;
                }
                Heap[offset + position] = node;
                HeapPositions[offset + node] = position;
            }

            private int PopMinimum(int offset, ref int heapCount)
            {
                var minimum = Heap[offset];
                HeapPositions[offset + minimum] = -1;
                heapCount--;
                if (heapCount == 0) return minimum;
                var last = Heap[offset + heapCount];
                var lastScore = Scores[offset + last];
                var position = 0;
                while (true)
                {
                    var left = position * 2 + 1;
                    if (left >= heapCount) break;
                    var right = left + 1;
                    var child = right < heapCount && Scores[offset + Heap[offset + right]] < Scores[offset + Heap[offset + left]]
                        ? right : left;
                    var childNode = Heap[offset + child];
                    if (Scores[offset + childNode] >= lastScore) break;
                    Heap[offset + position] = childNode;
                    HeapPositions[offset + childNode] = position;
                    position = child;
                }
                Heap[offset + position] = last;
                HeapPositions[offset + last] = position;
                return minimum;
            }
        }
    }
}
