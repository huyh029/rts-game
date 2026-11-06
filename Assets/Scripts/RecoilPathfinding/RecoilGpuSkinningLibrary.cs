using System;
using UnityEngine;

namespace RecoilPathfinding
{
    // Bind geometry only. Animation remains live bone transforms; no animation textures are baked.
    public sealed class RecoilGpuSkinningLibrary : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public Mesh Source;
            public Mesh Mesh;
            public Matrix4x4[] BindPoses;
            public int[] UsedBoneIndices;
        }
        public Entry[] Entries = Array.Empty<Entry>();
    }
}
