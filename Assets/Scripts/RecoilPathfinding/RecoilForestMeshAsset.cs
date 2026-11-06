using UnityEngine;

namespace RecoilPathfinding
{
    // Mesh subassets share one binary file, avoiding enormous YAML vertex/index arrays.
    [PreferBinarySerialization]
    public sealed class RecoilForestMeshAsset : ScriptableObject
    {
        public int meshCount;
    }
}
