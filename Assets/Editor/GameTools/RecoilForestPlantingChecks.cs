using System;
using RecoilPathfinding;
using UnityEditor;
using UnityEngine;

namespace GameToolsEditor
{
    internal static class RecoilForestPlantingChecks
    {
        [MenuItem("Game Tools/Công Thành/Kiểm tra trồng cây theo Plane")]
        public static void Validate()
        {
            var map = new GameObject("Temporary planting map");
            var plane = new GameObject("Temporary planting plane");
            var mesh = new Mesh();
            try
            {
                // A triangle is deliberately used: its bounds include a corner outside its projection.
                mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 10f), new Vector3(10f, 0f, 0f) };
                mesh.triangles = new[] { 0, 1, 2 };
                mesh.RecalculateBounds();
                plane.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mask = new RecoilPlaneProjection(plane);
                Require(mask.Contains(new Vector3(1f, 500f, 1f)), "Projection must ignore height.");
                Require(!mask.Contains(new Vector3(9f, 0f, 9f)), "Projection must use mesh triangles, not rectangular bounds.");
                Require(mask.Contains(new Vector3(5f, 0f, 5f)), "The shared boundary must belong to grass, excluding trees.");
                plane.transform.SetPositionAndRotation(new Vector3(30f, 100f, -12f), Quaternion.Euler(15f, 37f, 0f));
                plane.transform.localScale = new Vector3(-2f, 1f, 3f);
                mask = new RecoilPlaneProjection(plane);
                Require(mask.Contains(plane.transform.TransformPoint(new Vector3(1f, 0f, 1f))),
                    "Translated, tilted, rotated and mirrored planes must preserve interior coverage.");
                Require(!mask.Contains(plane.transform.TransformPoint(new Vector3(9f, 0f, 9f))),
                    "Rotating or mirroring a plane must not fill the missing triangle.");

                map.AddComponent<MeshFilter>().sharedMesh = mesh;
                map.transform.position = new Vector3(0f, 7f, 0f);
                plane.transform.SetParent(map.transform, true);
                var forest = new GameObject("Existing forest must be excluded");
                forest.transform.SetParent(map.transform, false);
                forest.transform.localPosition = Vector3.up * 20f;
                forest.AddComponent<RecoilForestArea>();
                forest.AddComponent<MeshFilter>().sharedMesh = mesh;
                var childrenBefore = map.transform.childCount;
                using (var surface = new RecoilForestPlantingSurface(map, plane, null, null))
                {
                    Require(surface.TrySample(new Vector3(1f, 0f, 1f), out var height, out var normal) &&
                        Mathf.Abs(height - 7f) < 0.001f && normal.y > 0.99f,
                        "Planting must hit the map surface, excluding the elevated plane and existing forest.");
                    Require(!surface.TrySample(new Vector3(9f, 0f, 9f), out _, out _),
                        "Map holes must never receive plants.");
                }
                Require(map.transform.childCount == childrenBefore, "Sampling colliders must be removed after planning.");
                Debug.Log("Forest planting checks passed: mesh projection, boundary, transforms, map height, holes, excluded geometry and temporary collider cleanup.");
                // Open after temporary test objects are destroyed; OnEnable may use the current selection.
                EditorApplication.delayCall += CongThanhForestWindow.Open;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(plane);
                UnityEngine.Object.DestroyImmediate(map);
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
