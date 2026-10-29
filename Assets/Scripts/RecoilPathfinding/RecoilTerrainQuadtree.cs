using System;
using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    // Adaptive walkable regions with conforming portal edges, including unequal leaf sizes.
    public static class RecoilTerrainQuadtree
    {
        public sealed class Graph
        {
            public Vector3[] Vertices;
            public int[] Indices;
            public int LeafCount;
            public int WalkableCellCount;
            public int[] CellLeaves;
            public int[] LeafTriangleStarts;
        }

        public static Graph Build(int columns, int rows, Vector3 origin, Vector2 size,
            float[] heights, byte[] walkable, float maxHeightError = 0.15f, int maxLeafCells = 32, bool respectHeight = true)
        {
            if (columns < 2 || rows < 2 || heights == null || walkable == null ||
                heights.LongLength != (long)columns * rows || walkable.LongLength != (long)(columns - 1) * (rows - 1) ||
                !float.IsFinite(size.x) || !float.IsFinite(size.y) || size.x <= 0 || size.y <= 0 ||
                !float.IsFinite(maxHeightError) || maxHeightError < 0 || maxLeafCells < 1)
                throw new ArgumentException("Invalid quadtree input.");
            return new Builder(columns, rows, origin, size, heights, walkable, maxHeightError, maxLeafCells, respectHeight).Build();
        }

        private sealed class Builder
        {
            private readonly int _columns, _rows, _width, _depth, _maxLeaf;
            private readonly Vector3 _origin;
            private readonly Vector2 _spacing;
            private readonly float[] _heights;
            private readonly float _error;
            private readonly bool _respectHeight;
            private readonly int[] _prefix, _owners;
            private readonly List<RectInt> _leaves = new();
            private readonly List<Vector3> _vertices = new();
            private readonly List<int> _indices = new();
            private readonly Dictionary<long, int> _vertexIndices = new();

            public Builder(int columns, int rows, Vector3 origin, Vector2 size, float[] heights,
                byte[] walkable, float error, int maxLeaf, bool respectHeight)
            {
                _columns = columns; _rows = rows; _width = columns - 1; _depth = rows - 1;
                _origin = origin; _spacing = new Vector2(size.x / _width, size.y / _depth);
                _heights = heights; _error = error; _maxLeaf = maxLeaf;
                _respectHeight = respectHeight;
                _prefix = new int[columns * rows];
                _owners = new int[_width * _depth];
                Array.Fill(_owners, -1);
                for (var z = 0; z < _depth; z++)
                    for (var x = 0; x < _width; x++)
                        _prefix[(z + 1) * columns + x + 1] = (walkable[z * _width + x] != 0 ? 1 : 0) +
                            _prefix[z * columns + x + 1] + _prefix[(z + 1) * columns + x] - _prefix[z * columns + x];
            }

            public Graph Build()
            {
                Split(0, 0, _width, _depth);
                var starts = new int[_leaves.Count + 1];
                for (var i = 0; i < _leaves.Count; i++)
                {
                    starts[i] = _indices.Count / 3;
                    Triangulate(_leaves[i]);
                }
                starts[_leaves.Count] = _indices.Count / 3;
                return new Graph { Vertices = _vertices.ToArray(), Indices = _indices.ToArray(),
                    LeafCount = _leaves.Count, WalkableCellCount = _prefix[_rows * _columns - 1],
                    CellLeaves = _owners, LeafTriangleStarts = starts };
            }

            private void Split(int x, int z, int width, int depth)
            {
                var count = _prefix[(z + depth) * _columns + x + width] - _prefix[z * _columns + x + width] -
                    _prefix[(z + depth) * _columns + x] + _prefix[z * _columns + x];
                if (count == 0) return;
                if (count == width * depth && width <= _maxLeaf && depth <= _maxLeaf &&
                    (!_respectHeight || (width == 1 && depth == 1) || FitsHeight(x, z, width, depth)))
                {
                    var id = _leaves.Count;
                    _leaves.Add(new RectInt(x, z, width, depth));
                    for (var dz = z; dz < z + depth; dz++)
                        for (var dx = x; dx < x + width; dx++) _owners[dz * _width + dx] = id;
                    return;
                }
                var left = width > 1 ? width / 2 : width;
                var bottom = depth > 1 ? depth / 2 : depth;
                Split(x, z, left, bottom);
                if (width > left) Split(x + left, z, width - left, bottom);
                if (depth > bottom) Split(x, z + bottom, left, depth - bottom);
                if (width > left && depth > bottom) Split(x + left, z + bottom, width - left, depth - bottom);
            }

            private bool FitsHeight(int x, int z, int width, int depth)
            {
                var a = _heights[z * _columns + x];
                var b = _heights[z * _columns + x + width];
                var c = _heights[(z + depth) * _columns + x + width];
                var d = _heights[(z + depth) * _columns + x];
                var center = SampleHeight(x + width * 0.5f, z + depth * 0.5f);
                // A planar leaf is safe to subdivide along neighbor portal endpoints without
                // changing its surface. Non-planar leaves split down to the sampled cells.
                if (Mathf.Abs(c - (b + d - a)) > _error || Mathf.Abs(center - (b + d) * 0.5f) > _error) return false;
                for (var dz = 0; dz <= depth; dz++)
                    for (var dx = 0; dx <= width; dx++)
                    {
                        var plane = a + (b - a) * dx / width + (d - a) * dz / depth;
                        if (Mathf.Abs(_heights[(z + dz) * _columns + x + dx] - plane) > _error) return false;
                    }
                return true;
            }

            private int Owner(int x, int z) => x < 0 || z < 0 || x >= _width || z >= _depth ? -1 : _owners[z * _width + x];

            private void Triangulate(RectInt leaf)
            {
                var center = Vertex(leaf.xMin + leaf.xMax, leaf.yMin + leaf.yMax);
                // Split a larger leaf's edge at every neighbor transition: both sides then
                // expose the same portal endpoints; no T-junction is left disconnected.
                var start = leaf.xMin;
                for (var x = leaf.xMin + 1; x <= leaf.xMax; x++)
                    if (x == leaf.xMax || Owner(x - 1, leaf.yMin - 1) != Owner(x, leaf.yMin - 1))
                    { Triangle(center, start * 2, leaf.yMin * 2, x * 2, leaf.yMin * 2); start = x; }
                start = leaf.yMin;
                for (var z = leaf.yMin + 1; z <= leaf.yMax; z++)
                    if (z == leaf.yMax || Owner(leaf.xMax, z - 1) != Owner(leaf.xMax, z))
                    { Triangle(center, leaf.xMax * 2, start * 2, leaf.xMax * 2, z * 2); start = z; }
                start = leaf.xMax;
                for (var x = leaf.xMax - 1; x >= leaf.xMin; x--)
                    if (x == leaf.xMin || Owner(x, leaf.yMax) != Owner(x - 1, leaf.yMax))
                    { Triangle(center, start * 2, leaf.yMax * 2, x * 2, leaf.yMax * 2); start = x; }
                start = leaf.yMax;
                for (var z = leaf.yMax - 1; z >= leaf.yMin; z--)
                    if (z == leaf.yMin || Owner(leaf.xMin - 1, z) != Owner(leaf.xMin - 1, z - 1))
                    { Triangle(center, leaf.xMin * 2, start * 2, leaf.xMin * 2, z * 2); start = z; }
            }

            private void Triangle(int center, int ax, int az, int bx, int bz)
            { _indices.Add(center); _indices.Add(Vertex(ax, az)); _indices.Add(Vertex(bx, bz)); }

            private int Vertex(int twiceX, int twiceZ)
            {
                var key = ((long)twiceX << 32) | (uint)twiceZ;
                if (_vertexIndices.TryGetValue(key, out var index)) return index;
                index = _vertices.Count;
                _vertexIndices.Add(key, index);
                var x = twiceX * 0.5f; var z = twiceZ * 0.5f;
                _vertices.Add(_origin + new Vector3(x * _spacing.x, SampleHeight(x, z), z * _spacing.y));
                return index;
            }

            private float SampleHeight(float x, float z)
            {
                var ix = Mathf.Min((int)x, _width - 1); var iz = Mathf.Min((int)z, _depth - 1);
                return Mathf.Lerp(Mathf.Lerp(_heights[iz * _columns + ix], _heights[iz * _columns + ix + 1], x - ix),
                    Mathf.Lerp(_heights[(iz + 1) * _columns + ix], _heights[(iz + 1) * _columns + ix + 1], x - ix), z - iz);
            }
        }
    }
}
