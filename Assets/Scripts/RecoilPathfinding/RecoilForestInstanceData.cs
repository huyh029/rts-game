using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecoilPathfinding
{
    [PreferBinarySerialization]
    public sealed class RecoilForestInstanceData : ScriptableObject
    {
        [Serializable]
        public sealed class Batch
        {
            public Mesh mesh;
            public Material material;
            public int subMesh, lod, layer;
            public uint renderingLayer;
            public ShadowCastingMode shadows;
            public bool receiveShadows;
            public Matrix4x4[] matrices;
        }

        [Serializable]
        public sealed class Chunk
        {
            public Bounds localBounds;
            public float referenceSize;
            public float[] thresholds;
            public Batch[] batches;
        }

        public Chunk[] chunks = Array.Empty<Chunk>();
    }
}
