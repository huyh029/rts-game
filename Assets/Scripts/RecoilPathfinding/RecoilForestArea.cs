using System;
using UnityEngine;

namespace RecoilPathfinding
{
    // Editor-generated forest metadata. No per-tree runtime updates or physics are needed.
    [DisallowMultipleComponent]
    public sealed class RecoilForestArea : MonoBehaviour
    {
        [Serializable]
        public struct Trunk
        {
            public Vector3 LocalPosition;
            public float Radius;
        }

        [HideInInspector] public Trunk[] trunks = Array.Empty<Trunk>();
        [HideInInspector] public int grassCount;
        [HideInInspector] public bool meshBaked;
        [HideInInspector] public string meshAssetFolder;
        public int TreeCount => trunks == null ? 0 : trunks.Length;
    }
}
