using UnityEngine;

namespace GameToolsEditor
{
    // Preserve existing callers while avoiding geometry duplication in the former mesh baker.
    public static class CongThanhForestMeshBaker
    {
        public static string Bake(GameObject mapRoot, float cellSize) =>
            CongThanhForestInstancingBaker.Bake(mapRoot, cellSize);
    }
}