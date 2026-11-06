using Unity.Collections;
using Unity.Mathematics;

namespace RecoilPathfinding
{
    // Height sampling is independent of the coarser topology used to find a route.
    public struct RecoilTerrainHeightField
    {
        [ReadOnly] public NativeArray<float> Heights;
        [ReadOnly] public NativeArray<int> CellLeaves;
        [ReadOnly] public NativeArray<int> LeafTriangleStarts;
        [ReadOnly] public NativeArray<int> BlueCellLeaves;
        [ReadOnly] public NativeArray<int> BlueLeafTriangleStarts;
        [ReadOnly] public NativeArray<int> RedCellLeaves;
        [ReadOnly] public NativeArray<int> RedLeafTriangleStarts;
        public int Columns;
        public int Rows;
        public float3 Origin;
        public float2 Size;
        public bool IsCreated => Heights.IsCreated && Columns >= 2 && Rows >= 2;

        public bool TryGetTriangleRange(float2 xz, out int first, out int end)
            => TryGetTriangleRange(xz, -1, out first, out end);

        public bool TryGetTriangleRange(float2 xz, int areaMask, out int first, out int end)
        {
            first = end = 0;
            var leaves = areaMask == 1 && BlueCellLeaves.IsCreated ? BlueCellLeaves :
                areaMask == 2 && RedCellLeaves.IsCreated ? RedCellLeaves : CellLeaves;
            var starts = areaMask == 1 && BlueLeafTriangleStarts.IsCreated ? BlueLeafTriangleStarts :
                areaMask == 2 && RedLeafTriangleStarts.IsCreated ? RedLeafTriangleStarts : LeafTriangleStarts;
            if (!IsCreated || !leaves.IsCreated || !starts.IsCreated) return false;
            var uv = (xz - Origin.xz) / Size;
            if (math.any(uv < 0f) || math.any(uv > 1f)) return false;
            var cell = math.min((int2)math.floor(uv * new float2(Columns - 1, Rows - 1)), new int2(Columns - 2, Rows - 2));
            var leaf = leaves[cell.y * (Columns - 1) + cell.x];
            if (leaf < 0) return false;
            first = starts[leaf]; end = starts[leaf + 1];
            return true;
        }

        public void Dispose()
        {
            if (Heights.IsCreated) Heights.Dispose();
            if (CellLeaves.IsCreated) CellLeaves.Dispose();
            if (LeafTriangleStarts.IsCreated) LeafTriangleStarts.Dispose();
            if (BlueCellLeaves.IsCreated) BlueCellLeaves.Dispose();
            if (BlueLeafTriangleStarts.IsCreated) BlueLeafTriangleStarts.Dispose();
            if (RedCellLeaves.IsCreated) RedCellLeaves.Dispose();
            if (RedLeafTriangleStarts.IsCreated) RedLeafTriangleStarts.Dispose();
            this = default;
        }

        public float Height(float2 xz)
        {
            var grid = math.saturate((xz - Origin.xz) / Size) * new float2(Columns - 1, Rows - 1);
            return HeightFromGrid(grid);
        }

        private float HeightFromGrid(float2 grid)
        {
            var x = math.min((int)grid.x, Columns - 2);
            var z = math.min((int)grid.y, Rows - 2);
            return Origin.y + math.lerp(
                math.lerp(Heights[z * Columns + x], Heights[z * Columns + x + 1], grid.x - x),
                math.lerp(Heights[(z + 1) * Columns + x], Heights[(z + 1) * Columns + x + 1], grid.x - x), grid.y - z);
        }

        public bool TryFindUnitApproach(float3 from, float3 target, int areaMask, out float3 destination, float clearance = 0.5f)
        {
            destination = target;
            if (!IsCreated) return false;
            var delta = from.xz - target.xz;
            var distance = math.length(delta);
            var direction = math.normalizesafe(delta);
            var cell = math.min(Size.x / (Columns - 1), Size.y / (Rows - 1));
            var step = math.max(0.1f, math.max(cell * 0.5f, distance / 128f));
            // Search from the target back toward the pursuer. Never cross team/castle
            // restrictions merely because the target can stand on its own team's surface.
            for (var i = 0; i <= 128; i++)
            {
                var offset = math.min(distance, i * step);
                var point = target.xz + direction * offset;
                if (TryHeight(point, out var height) && TryGetTriangleRange(point, areaMask, out _, out _))
                {
                    destination = new float3(point.x, height, point.y);
                    if (i > 0)
                    {
                        var lower = math.max(0f, offset - step);
                        var upper = offset;
                        for (var refine = 0; refine < 10; refine++)
                        {
                            var middle = (lower + upper) * 0.5f;
                            if (TryGetTriangleRange(target.xz + direction * middle, areaMask, out _, out _)) upper = middle;
                            else lower = middle;
                        }
                        var inset = target.xz + direction * math.min(distance, upper + math.max(0.1f, clearance));
                        if (TryHeight(inset, out var insetHeight) && TryGetTriangleRange(inset, areaMask, out _, out _))
                            destination = new float3(inset.x, insetHeight, inset.y);
                    }
                    return true;
                }
                if (offset >= distance) break;
            }
            return false;
        }

        public bool TryHeight(float2 xz, out float height)
        {
            height = 0f;
            if (!IsCreated || math.any(Size <= 0f)) return false;
            var uv = (xz - Origin.xz) / Size;
            if (math.any(uv < 0f) || math.any(uv > 1f)) return false;
            // Keep the UV/grid transform shared with Height, but avoid recalculating it for
            // every terrain sample along projectile arcs and line-of-fire rays.
            height = HeightFromGrid(uv * new float2(Columns - 1, Rows - 1));
            return math.isfinite(height);
        }

        private bool TryMaximumHeight(float2 minXZ, float2 maxXZ, out float maximumHeight)
        {
            maximumHeight = float.NegativeInfinity;
            if (!IsCreated || math.any(Size <= 0f) ||
                maxXZ.x < Origin.x || minXZ.x > Origin.x + Size.x ||
                maxXZ.y < Origin.z || minXZ.y > Origin.z + Size.y)
                return false;

            var uvMin = math.saturate((minXZ - Origin.xz) / Size);
            var uvMax = math.saturate((maxXZ - Origin.xz) / Size);
            var gridScale = new float2(Columns - 1, Rows - 1);
            var min = math.clamp((int2)math.floor(uvMin * gridScale), int2.zero,
                new int2(Columns - 1, Rows - 1));
            var max = math.clamp((int2)math.ceil(uvMax * gridScale), int2.zero,
                new int2(Columns - 1, Rows - 1));
            for (var z = min.y; z <= max.y; z++)
            for (var x = min.x; x <= max.x; x++)
                maximumHeight = math.max(maximumHeight, Origin.y + Heights[z * Columns + x]);
            return math.isfinite(maximumHeight);
        }

        public bool HasLineOfFire(float3 start, float3 end, float arcHeight = 0f)
        {
            if (!IsCreated) return true;
            var horizontal = end.xz - start.xz;
            var distance = math.length(horizontal);
            if (distance <= 0.01f) return true;
            var sampleSpacing = math.max(0.25f, math.min(
                Size.x / (Columns - 1), Size.y / (Rows - 1)) * 0.5f);
            // Always include at least one interior sample; otherwise a short shot whose
            // endpoints straddle a narrow ridge can incorrectly be treated as clear.
            var sampleCount = math.clamp((int)math.ceil(distance / sampleSpacing), 2, 512);
            for (var i = 1; i < sampleCount; i++)
            {
                var t = (float)i / sampleCount;
                var rayPoint = math.lerp(start, end, t);
                rayPoint.y += 4f * math.max(0f, arcHeight) * t * (1f - t);
                if (TryHeight(rayPoint.xz, out var groundHeight) && groundHeight > rayPoint.y + 0.15f)
                    return false;
            }
            return true;
        }

        public bool TryFindProjectileTerrainHitTime(float3 start, float3 end, float arcHeight,
            float startTime, float endTime, out float hitTime)
        {
            hitTime = 1f;
            if (!IsCreated || endTime < startTime) return false;
            // Bound the terrain height over only the cells touched by this segment. A
            // bilinear terrain patch never rises above its largest corner. The ballistic y
            // curve is concave for non-negative arcs, so its minimum is at a segment end.
            if (arcHeight >= 0f)
            {
                var startY = start.y + (end.y - start.y) * startTime +
                    4f * arcHeight * startTime * (1f - startTime);
                var endY = start.y + (end.y - start.y) * endTime +
                    4f * arcHeight * endTime * (1f - endTime);
                var segmentStartXZ = math.lerp(start.xz, end.xz, startTime);
                var segmentEndXZ = math.lerp(start.xz, end.xz, endTime);
                var minXZ = math.min(segmentStartXZ, segmentEndXZ);
                var maxXZ = math.max(segmentStartXZ, segmentEndXZ);
                if (!TryMaximumHeight(minXZ, maxXZ, out var localMaximumHeight) ||
                    math.min(startY, endY) > localMaximumHeight + 0.1f)
                    return false;
            }
            var horizontalDistance = math.length(end.xz - start.xz) * (endTime - startTime);
            var sampleSpacing = math.max(0.25f, math.min(
                Size.x / (Columns - 1), Size.y / (Rows - 1)) * 0.5f);
            // A sub-cell segment still needs an interior point to catch a ridge between frames.
            var sampleCount = math.clamp((int)math.ceil(horizontalDistance / sampleSpacing), 2, 512);
            for (var i = 0; i <= sampleCount; i++)
            {
                var localT = (float)i / sampleCount;
                var t = math.lerp(startTime, endTime, localT);
                var point = RecoilCombatProjectileTrajectory.Position(start, end, t, arcHeight);
                if (!TryHeight(point.xz, out var groundHeight) || point.y > groundHeight + 0.1f) continue;
                hitTime = t;
                return true;
            }
            return false;
        }
    }
}
