using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameToolsEditor
{
    // World-space XZ triangle coverage, independent of the mask's height or renderer visibility.
    internal sealed class RecoilPlaneProjection
    {
        private const int GridSize = 32;
        private readonly List<Triangle>[] cells = new List<Triangle>[GridSize * GridSize];
        private Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        private Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);

        private struct Triangle
        {
            public Vector2 A, B, C;
            public float Area;
            public Vector2 Min => Vector2.Min(A, Vector2.Min(B, C));
            public Vector2 Max => Vector2.Max(A, Vector2.Max(B, C));
            public bool Contains(Vector2 p)
            {
                var u = Cross(B - p, C - p) / Area;
                var v = Cross(C - p, A - p) / Area;
                var w = 1f - u - v;
                return u >= -0.000001f && v >= -0.000001f && w >= -0.000001f;
            }
        }

        public RecoilPlaneProjection(GameObject plane)
        {
            var triangles = new List<Triangle>();
            foreach (var filter in plane.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = filter.sharedMesh;
                if (mesh == null) continue;
                if (!mesh.isReadable)
                    throw new InvalidOperationException($"Mesh '{mesh.name}' của Plane cần bật Read/Write trong Import Settings.");
                var vertices = mesh.vertices;
                for (var i = 0; i < vertices.Length; i++) vertices[i] = filter.transform.TransformPoint(vertices[i]);
                for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                    var indices = mesh.GetTriangles(submesh);
                    for (var i = 0; i < indices.Length; i += 3)
                    {
                        var a = XZ(vertices[indices[i]]);
                        var b = XZ(vertices[indices[i + 1]]);
                        var c = XZ(vertices[indices[i + 2]]);
                        var area = Cross(b - a, c - a);
                        if (Mathf.Abs(area) <= 0.00000001f) continue;
                        var triangle = new Triangle { A = a, B = b, C = c, Area = area };
                        triangles.Add(triangle);
                        min = Vector2.Min(min, triangle.Min);
                        max = Vector2.Max(max, triangle.Max);
                    }
                }
            }
            if (triangles.Count == 0)
                throw new InvalidOperationException("Plane cần có MeshFilter với diện tích hình chiếu XZ lớn hơn 0.");
            foreach (var triangle in triangles)
            {
                var lower = Cell(triangle.Min);
                var upper = Cell(triangle.Max);
                for (var z = lower.y; z <= upper.y; z++)
                for (var x = lower.x; x <= upper.x; x++)
                {
                    var index = z * GridSize + x;
                    if (cells[index] == null) cells[index] = new List<Triangle>();
                    cells[index].Add(triangle);
                }
            }
        }

        public bool Contains(Vector3 worldPosition)
        {
            var p = XZ(worldPosition);
            if (p.x < min.x || p.x > max.x || p.y < min.y || p.y > max.y) return false;
            var cell = Cell(p);
            var candidates = cells[cell.y * GridSize + cell.x];
            if (candidates == null) return false;
            foreach (var triangle in candidates) if (triangle.Contains(p)) return true;
            return false;
        }

        private Vector2Int Cell(Vector2 p) => new Vector2Int(
            Mathf.Clamp(Mathf.FloorToInt((p.x - min.x) / Mathf.Max(0.000001f, max.x - min.x) * GridSize), 0, GridSize - 1),
            Mathf.Clamp(Mathf.FloorToInt((p.y - min.y) / Mathf.Max(0.000001f, max.y - min.y) * GridSize), 0, GridSize - 1));
        private static Vector2 XZ(Vector3 p) => new Vector2(p.x, p.z);
        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    }
}
