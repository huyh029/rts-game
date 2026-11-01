using System;
using UnityEngine;

namespace RecoilPathfinding
{
    // Baked vertex-animation-texture data for one character template: a combined static mesh plus
    // a texture holding each clip's posed vertex positions (vertices tiled across TextureWidth
    // columns and RowsPerFrame rows per baked frame — ClipEntry.RowOffset is a frame index, not a
    // texel row). Lets RecoilVatCrowdRenderer draw and animate soldiers with
    // Graphics.DrawMeshInstanced instead of a live Animator + SkinnedMeshRenderer per instance.
    [CreateAssetMenu(menuName = "Recoil Pathfinding/VAT Crowd Library")]
    public sealed class RecoilVatLibrary : ScriptableObject
    {
        [Serializable]
        public sealed class ClipEntry
        {
            public string State;
            [Min(0)] public int RowOffset;
            [Min(1)] public int FrameCount;
            [Min(0.01f)] public float Duration = 1f;
            public bool Loop;
        }

        public GameObject SourcePrefab;
        public Mesh BakedMesh;
        [Tooltip("RGBAHalf texture: vertices tile across TextureWidth columns, RowsPerFrame rows " +
            "per baked frame (Unity caps a single texture dimension at 16384, so a high-poly " +
            "character's vertices can't all fit in one row).")]
        public Texture2D PositionTexture;
        [Tooltip("Same layout as PositionTexture, but holding each frame's posed vertex normals, " +
            "so lighting follows the animation instead of staying frozen at the bind pose.")]
        public Texture2D NormalTexture;
        public Material[] SourceMaterials = Array.Empty<Material>();
        public int VertexCount;
        public int TextureWidth;
        public int RowsPerFrame;
        public ClipEntry[] Clips = Array.Empty<ClipEntry>();

        public bool TryGetClip(string state, out ClipEntry clip)
        {
            if (Clips != null)
                foreach (var entry in Clips)
                    if (entry != null && string.Equals(entry.State, state, StringComparison.OrdinalIgnoreCase))
                    {
                        clip = entry;
                        return true;
                    }
            clip = null;
            return false;
        }

        public bool IsValid => BakedMesh != null && PositionTexture != null && NormalTexture != null &&
            VertexCount > 0 && TextureWidth > 0 && RowsPerFrame > 0 && Clips != null && Clips.Length > 0;

        // Diagnostic only: names which field(s) make IsValid false, for logging at the call site
        // instead of guessing from the outside why a template silently fell back to the old path.
        public string DescribeInvalid()
        {
            if (IsValid) return "valid";
            var missing = new System.Collections.Generic.List<string>();
            if (BakedMesh == null) missing.Add(nameof(BakedMesh));
            if (PositionTexture == null) missing.Add(nameof(PositionTexture));
            if (NormalTexture == null) missing.Add(nameof(NormalTexture));
            if (VertexCount <= 0) missing.Add(nameof(VertexCount));
            if (TextureWidth <= 0) missing.Add(nameof(TextureWidth));
            if (RowsPerFrame <= 0) missing.Add(nameof(RowsPerFrame));
            if (Clips == null || Clips.Length == 0) missing.Add(nameof(Clips));
            return "missing: " + string.Join(", ", missing);
        }
    }
}
