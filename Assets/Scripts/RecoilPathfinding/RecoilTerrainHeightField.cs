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
        public int Columns;
        public int Rows;
        public float3 Origin;
        public float2 Size;
        public bool IsCreated => Heights.IsCreated && Columns >= 2 && Rows >= 2;

        public bool TryGetTriangleRange(float2 xz, out int first, out int end)
        {
            first = end = 0;
            if (!IsCreated || !CellLeaves.IsCreated || !LeafTriangleStarts.IsCreated) return false;
            var uv = (xz - Origin.xz) / Size;
            if (math.any(uv < 0f) || math.any(uv > 1f)) return false;
            var cell = math.min((int2)math.floor(uv * new float2(Columns - 1, Rows - 1)), new int2(Columns - 2, Rows - 2));
            var leaf = CellLeaves[cell.y * (Columns - 1) + cell.x];
            if (leaf < 0) return false;
            first = LeafTriangleStarts[leaf]; end = LeafTriangleStarts[leaf + 1];
            return true;
        }

        public void Dispose()
        {
            if (Heights.IsCreated) Heights.Dispose();
            if (CellLeaves.IsCreated) CellLeaves.Dispose();
            if (LeafTriangleStarts.IsCreated) LeafTriangleStarts.Dispose();
            this = default;
        }

        public float Height(float2 xz)
        {
            var grid = math.saturate((xz - Origin.xz) / Size) * new float2(Columns - 1, Rows - 1);
            var x = math.min((int)grid.x, Columns - 2);
            var z = math.min((int)grid.y, Rows - 2);
            return Origin.y + math.lerp(
                math.lerp(Heights[z * Columns + x], Heights[z * Columns + x + 1], grid.x - x),
                math.lerp(Heights[(z + 1) * Columns + x], Heights[(z + 1) * Columns + x + 1], grid.x - x), grid.y - z);
        }
    }
}
