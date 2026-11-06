using System;
using System.Collections.Generic;
using RecoilPathfinding;
using UnityEngine;

namespace GameToolsEditor
{
    // Temporary, hidden sampling colliders. Only these colliders are queried, so scene props,
    // the mask plane and an existing forest cannot be mistaken for the ground.
    internal sealed class RecoilForestPlantingSurface : IDisposable
    {
        private const int GridSize = 32;
        private readonly List<GameObject> temporary = new List<GameObject>();
        private readonly List<Collider> colliders = new List<Collider>();
        private readonly List<Collider>[] cells = new List<Collider>[GridSize * GridSize];
        public Bounds Bounds { get; private set; }

        public RecoilForestPlantingSurface(GameObject map, GameObject plane, GameObject blueCastle, GameObject redCastle)
        {
            try
            {
                bool Excluded(Transform t) => Under(t, plane) || Under(t, blueCastle) || Under(t, redCastle) ||
                    t.GetComponentInParent<RecoilForestArea>(true) != null;
                var sampled = new HashSet<Transform>();
                foreach (var filter in map.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (Excluded(filter.transform) || filter.sharedMesh == null) continue;
                    var source = NewColliderObject(filter.transform);
                    source.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
                    colliders.Add(source.GetComponent<Collider>());
                    sampled.Add(filter.transform);
                }
                foreach (var original in map.GetComponentsInChildren<MeshCollider>(true))
                {
                    if (temporary.Contains(original.gameObject) || Excluded(original.transform) ||
                        sampled.Contains(original.transform) || original.sharedMesh == null) continue;
                    var source = NewColliderObject(original.transform);
                    source.AddComponent<MeshCollider>().sharedMesh = original.sharedMesh;
                    colliders.Add(source.GetComponent<Collider>());
                }
                foreach (var terrain in map.GetComponentsInChildren<Terrain>(true))
                {
                    if (Excluded(terrain.transform) || terrain.terrainData == null) continue;
                    var source = NewColliderObject(terrain.transform);
                    source.AddComponent<TerrainCollider>().terrainData = terrain.terrainData;
                    colliders.Add(source.GetComponent<Collider>());
                }
                if (colliders.Count == 0) throw new InvalidOperationException("Map cần có MeshFilter, MeshCollider hoặc Terrain để lấy mặt đất.");
                Physics.SyncTransforms();
                var bounds = colliders[0].bounds;
                foreach (var collider in colliders) bounds.Encapsulate(collider.bounds);
                if (bounds.size.x <= 0f || bounds.size.z <= 0f)
                    throw new InvalidOperationException("Map không có diện tích mặt đất hợp lệ.");
                Bounds = bounds;
                foreach (var collider in colliders)
                {
                    var lower = Cell(collider.bounds.min);
                    var upper = Cell(collider.bounds.max);
                    for (var z = lower.y; z <= upper.y; z++)
                    for (var x = lower.x; x <= upper.x; x++)
                    {
                        var i = z * GridSize + x;
                        if (cells[i] == null) cells[i] = new List<Collider>();
                        cells[i].Add(collider);
                    }
                }
            }
            catch { Dispose(); throw; }
        }

        private GameObject NewColliderObject(Transform parent)
        {
            var source = new GameObject("Forest surface sampling") { hideFlags = HideFlags.HideAndDontSave, layer = 2 };
            source.transform.SetParent(parent, false);
            temporary.Add(source);
            return source;
        }

        public bool TrySample(Vector3 p, out float height, out Vector3 normal)
        {
            height = 0f; normal = Vector3.up;
            var cell = Cell(p);
            var candidates = cells[cell.y * GridSize + cell.x];
            if (candidates == null) return false;
            var ray = new Ray(new Vector3(p.x, Bounds.max.y + 10f, p.z), Vector3.down);
            var nearest = Bounds.size.y + 20f;
            var found = false;
            foreach (var collider in candidates)
            {
                var bounds = collider.bounds;
                if (p.x < bounds.min.x || p.x > bounds.max.x || p.z < bounds.min.z || p.z > bounds.max.z) continue;
                if (!collider.Raycast(ray, out var hit, nearest)) continue;
                nearest = hit.distance;
                height = hit.point.y;
                normal = hit.normal;
                found = true;
            }
            return found;
        }

        private Vector2Int Cell(Vector3 p) => new Vector2Int(
            Mathf.Clamp(Mathf.FloorToInt((p.x - Bounds.min.x) / Bounds.size.x * GridSize), 0, GridSize - 1),
            Mathf.Clamp(Mathf.FloorToInt((p.z - Bounds.min.z) / Bounds.size.z * GridSize), 0, GridSize - 1));
        private static bool Under(Transform t, GameObject root) => root != null && (t == root.transform || t.IsChildOf(root.transform));
        public void Dispose()
        {
            foreach (var source in temporary) if (source != null) UnityEngine.Object.DestroyImmediate(source);
            temporary.Clear();
        }
    }
}
