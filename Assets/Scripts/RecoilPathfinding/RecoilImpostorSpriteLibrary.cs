using System;
using UnityEngine;

namespace RecoilPathfinding
{
    [CreateAssetMenu(menuName = "Recoil Pathfinding/Impostor Sprite Library")]
    public sealed class RecoilImpostorSpriteLibrary : ScriptableObject
    {
        [Serializable]
        public sealed class AnimationSheet
        {
            public string State;
            public string SourceClip;
            public Texture2D Atlas;
            public int Frames;
            public float Duration;
            public bool Loop;
        }

        public GameObject SourcePrefab;
        public int FrameWidth = 128;
        public int FrameHeight = 256;
        public int FramesPerState = 5;
        public float WorldWidth = 1f;
        public float WorldHeight = 1.8f;
        [Tooltip("Atlas columns: 8 horizontal views, then 8 elevated views. Rows are animation frames.")]
        public int HorizontalDirections = 8;
        public int ElevatedDirections = 8;
        [Range(0f, 85f)] public float ElevatedViewAngle = 55f;
        public Vector3 CaptureOffset;
        [Min(0.1f)] public float OrthographicScale = 1f;
        public AnimationSheet[] Animations = Array.Empty<AnimationSheet>();
    }
}
