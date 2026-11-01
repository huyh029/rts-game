using UnityEngine;

namespace RecoilPathfinding
{
    // Optional project default, loaded by the path service in Editor and player builds.
    public sealed class RecoilTerrainBackendSettings : ScriptableObject
    {
        public RecoilTerrainMapData terrainMap;
        [Range(1, 128)] public int maxLeafCells = 32;
    }
}
