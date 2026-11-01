using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecoilPathfinding
{
    // Low-detail crowd renderer: soldiers beyond the prefab animation range are shown as flat,
    // team-colored dots without per-unit GameObjects or animation.
    public sealed class RecoilDotCrowdRenderer : MonoBehaviour
    {
        private const int MaxInstancesPerDraw = 1023;
        private static readonly ProfilerMarker DrawMarker = new("RecoilBattle.Rendering.DotDraw");
        private static readonly int TeamId = Shader.PropertyToID("_Team");

        [SerializeField, Min(0.05f)] private float radius = 0.6f;

        private readonly Matrix4x4[] _matrices = new Matrix4x4[MaxInstancesPerDraw];
        private readonly Vector4[] _teams = new Vector4[MaxInstancesPerDraw];
        // Not initialized inline: MaterialPropertyBlock's native allocation isn't allowed from a
        // field initializer (Unity throws "CreateImpl is not allowed to be called from a
        // MonoBehaviour constructor"), so it's created lazily in EnsureResources instead.
        private MaterialPropertyBlock _block;
        private int _count;
        private Mesh _quad;
        private Material _material;

        public int SubmittedCount { get; private set; }
        public int LastDrawCallCount { get; private set; }

        public void BeginFrame()
        {
            SubmittedCount = 0;
            LastDrawCallCount = 0;
            _count = 0;
            EnsureResources();
        }

        public void Submit(Vector3 position, bool isBlue)
        {
            if (_material == null) return;
            _matrices[_count] = Matrix4x4.TRS(position, Quaternion.identity, new Vector3(radius, radius, radius));
            _teams[_count] = new Vector4(isBlue ? 0f : 1f, 0f, 0f, 0f);
            _count++;
            SubmittedCount++;
            if (_count == MaxInstancesPerDraw) Flush();
        }

        public void EndFrame()
        {
            using (DrawMarker.Auto()) Flush();
        }

        private void Flush()
        {
            // Check every resource Flush touches, not just _material: if EnsureResources bailed out
            // early (shader not found yet) or a resource was destroyed by a reload, skip the draw
            // instead of risking a null/destroyed-reference exception.
            if (_count == 0 || _material == null || _quad == null || _block == null)
            {
                _count = 0;
                return;
            }
            _block.SetVectorArray(TeamId, _teams);
            Graphics.DrawMeshInstanced(_quad, 0, _material, _matrices, _count, _block,
                ShadowCastingMode.Off, false, gameObject.layer);
            LastDrawCallCount++;
            _count = 0;
        }

        private void EnsureResources()
        {
            if (_material == null)
            {
                var shader = Shader.Find("Recoil/Dot Crowd");
                if (shader == null || !shader.isSupported) return;
                _material = new Material(shader) { name = "Recoil Dot Crowd", enableInstancing = true };
            }
            if (_block == null) _block = new MaterialPropertyBlock();
            if (_quad != null) return;
            // Flat quad lying on the ground (top-down RTS camera); swap to a camera-facing billboard
            // in the vertex stage if the camera angle needs to vary more.
            _quad = new Mesh { name = "Recoil dot quad" };
            _quad.SetVertices(new[]
            {
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                new Vector3(0.5f, 0f, 0.5f), new Vector3(-0.5f, 0f, 0.5f)
            });
            _quad.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
            _quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            _quad.bounds = new Bounds(Vector3.zero, Vector3.one * 4f);
        }

        private void OnDestroy()
        {
            if (_material != null) Destroy(_material);
            if (_quad != null) Destroy(_quad);
        }
    }
}
