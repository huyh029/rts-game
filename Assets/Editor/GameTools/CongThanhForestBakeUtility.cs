using RecoilPathfinding;
using UnityEngine;

namespace GameToolsEditor
{
    public static class CongThanhForestBakeUtility
    {
        public static bool IsForestGeometry(Transform source) =>
            source.GetComponentInParent<RecoilForestArea>(true) != null;

        // Rasterize trunk circles onto base terrain occupancy; foliage is never a ground surface.
        public static void Apply(GameObject root, Vector3 origin, float stepX, float stepZ,
            int columns, int rows, byte[] cells)
        {
            foreach (var forest in root.GetComponentsInChildren<RecoilForestArea>())
            {
                if (forest.trunks == null) continue;
                var scale = forest.transform.lossyScale;
                var radiusScale = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                foreach (var trunk in forest.trunks)
                {
                    var center = forest.transform.TransformPoint(trunk.LocalPosition);
                    var radius = Mathf.Max(0.01f, trunk.Radius * radiusScale);
                    var minX = Mathf.Max(0, Mathf.FloorToInt((center.x - radius - origin.x) / stepX));
                    var maxX = Mathf.Min(columns - 2, Mathf.FloorToInt((center.x + radius - origin.x) / stepX));
                    var minZ = Mathf.Max(0, Mathf.FloorToInt((center.z - radius - origin.z) / stepZ));
                    var maxZ = Mathf.Min(rows - 2, Mathf.FloorToInt((center.z + radius - origin.z) / stepZ));
                    for (var z = minZ; z <= maxZ; z++)
                    for (var x = minX; x <= maxX; x++)
                    {
                        var dx = center.x - Mathf.Clamp(center.x, origin.x + x * stepX, origin.x + (x + 1) * stepX);
                        var dz = center.z - Mathf.Clamp(center.z, origin.z + z * stepZ, origin.z + (z + 1) * stepZ);
                        if (dx * dx + dz * dz <= radius * radius)
                            cells[z * (columns - 1) + x] = 0;
                    }
                }
            }
        }
    }
}
