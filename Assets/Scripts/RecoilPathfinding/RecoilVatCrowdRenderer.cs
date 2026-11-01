using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecoilPathfinding
{
    // GPU-instanced animated crowd renderer: draws soldiers using a baked vertex-animation-texture
    // (RecoilVatLibrary) instead of a per-instance Animator + SkinnedMeshRenderer. These soldiers
    // have no GameObject at all — RecoilAutoBattleDemo submits position/rotation/animation state
    // straight from the simulation every frame, mirroring how RecoilEngine's LocalModelPiece drives
    // a unit's appearance from flat per-instance data with no per-instance Animator scheduling cost.
    public enum RecoilVatAnimationState : byte
    {
        Idle = 0,
        Run = 1,
        Attack = 2,
        Death = 3
    }

    public sealed class RecoilVatCrowdRenderer : MonoBehaviour
    {
        public const int MaxInstancesPerDraw = 1023;
        private static readonly ProfilerMarker SubmitMarker = new("RecoilBattle.Rendering.VatSubmit");
        private static readonly ProfilerMarker DrawMarker = new("RecoilBattle.Rendering.VatDraw");
        private static readonly int PositionTexId = Shader.PropertyToID("_PositionTex");
        private static readonly int NormalTexId = Shader.PropertyToID("_NormalTex");
        private static readonly int VertexCountId = Shader.PropertyToID("_VertexCount");
        private static readonly int TextureWidthId = Shader.PropertyToID("_TextureWidth");
        private static readonly int RowsPerFrameId = Shader.PropertyToID("_RowsPerFrame");
        private static readonly int RowPhaseId = Shader.PropertyToID("_RowPhase");

        [Tooltip("Lính VAT xa hơn khoảng này (m) không được vẽ nữa. GPU phải đọc texture vị trí/" +
            "normal cho từng vertex của mỗi lính, nên không cắt theo khoảng cách/khung hình sẽ khiến " +
            "cả đội quân (kể cả ngoài màn hình) luôn tốn full chi tiết mỗi frame.")]
        [SerializeField, Min(1f)] private float maxRenderDistance = 150f;

        private readonly Dictionary<RecoilVatLibrary, Batch> _batches = new();
        private readonly Plane[] _frustumPlanes = new Plane[6];
        private Shader _shader;
        private Camera _cullingCamera;
#if UNITY_EDITOR
        private float _nextDebugLogTime;
#endif

        public int SubmittedCount { get; private set; }
        public int CulledCount { get; private set; }
        public int LastDrawCallCount { get; private set; }

        private sealed class Batch
        {
            public Mesh Mesh;
            // One material per submesh (body/weapon/shield parts baked into separate submeshes by
            // RecoilVatBakeWindow) — DrawMeshInstanced draws a single submesh per call, so one Flush
            // issues Materials.Length draw calls, all sharing the same instance buffers below.
            public Material[] Materials;
            public readonly Matrix4x4[] Matrices = new Matrix4x4[MaxInstancesPerDraw];
            public readonly Vector4[] RowPhases = new Vector4[MaxInstancesPerDraw];
            public readonly MaterialPropertyBlock Block = new();
            public int Count;
        }

        // Call once per frame before any Submit calls. camera may be null (e.g. no active camera
        // yet), in which case Submit draws everything without culling.
        public void BeginFrame(Camera camera)
        {
            SubmittedCount = 0;
            CulledCount = 0;
            LastDrawCallCount = 0;
            _cullingCamera = camera;
            if (camera != null) GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanes);
        }

        // state/stateStartTime/now mirror RecoilCharacterPresentation's Idle/Run/Attack/Death
        // state machine, but sourced directly from simulation data instead of a live Animator.
        public void Submit(RecoilVatLibrary library, Vector3 position, Quaternion rotation,
            RecoilVatAnimationState state, float stateStartTime, float now)
        {
            if (library == null || !library.IsValid) return;
            using (SubmitMarker.Auto())
            {
                var batch = GetOrCreateBatch(library);
                if (batch == null) return;
                // Same frustum + distance cull RecoilInstancedSoldierRenderer applies to the static-
                // mesh soldier tier: a VAT soldier's vertex count (tens of thousands for some
                // character types) makes drawing every instance regardless of visibility the
                // dominant render-thread cost once the army gets large.
                if (_cullingCamera != null && !IsVisible(library.BakedMesh.bounds, position, rotation))
                {
                    CulledCount++;
                    return;
                }
                if (!library.TryGetClip(state.ToString(), out var clip) &&
                    !library.TryGetClip(nameof(RecoilVatAnimationState.Idle), out clip))
                    return;

                var elapsed = Mathf.Max(0f, now - stateStartTime);
                var frame = ComputeFrame(clip, elapsed);

#if UNITY_EDITOR
                // Throttled diagnostic: confirms whether the computed frame/elapsed are actually
                // advancing over time (static pose vs. a real animation bug) without flooding the
                // Console — one line per second, whichever soldier happens to be mid-batch then.
                if (Time.unscaledTime >= _nextDebugLogTime)
                {
                    _nextDebugLogTime = Time.unscaledTime + 1f;
                    Debug.Log($"[VAT debug] library={library.name} state={state} stateStartTime={stateStartTime:F2} " +
                        $"now={now:F2} elapsed={elapsed:F2} clip='{clip.State}' duration={clip.Duration:F2} " +
                        $"frameCount={clip.FrameCount} loop={clip.Loop} rowOffset={clip.RowOffset} " +
                        $"computedFrame={frame} texWidth={library.TextureWidth} rowsPerFrame={library.RowsPerFrame}");
                }
#endif

                // y carries seconds-since-death for the shader's sink effect, or -1 while alive so
                // the sink formula (which can go positive for any sufficiently large elapsed value)
                // never applies to a living soldier.
                var deathElapsed = state == RecoilVatAnimationState.Death ? elapsed : -1f;
                batch.Matrices[batch.Count] = Matrix4x4.TRS(position, rotation, Vector3.one);
                batch.RowPhases[batch.Count] = new Vector4(clip.RowOffset + frame, deathElapsed, 0f, 0f);
                batch.Count++;
                SubmittedCount++;
                if (batch.Count == MaxInstancesPerDraw) Flush(batch);
            }
        }

        // Loops sample a full cycle (elapsed wraps modulo duration); one-shot clips (e.g. Death)
        // clamp to the final baked frame instead of wrapping back to the first.
        internal static int ComputeFrame(RecoilVatLibrary.ClipEntry clip, float elapsed)
        {
            var duration = Mathf.Max(0.01f, clip.Duration);
            var frameCount = Mathf.Max(1, clip.FrameCount);
            if (clip.Loop)
            {
                var normalized = (elapsed / duration) % 1f;
                if (normalized < 0f) normalized += 1f;
                return Mathf.FloorToInt(normalized * frameCount) % frameCount;
            }
            var clamped = Mathf.Clamp01(elapsed / duration);
            return Mathf.Min(frameCount - 1, Mathf.FloorToInt(clamped * frameCount));
        }

        private bool IsVisible(Bounds localBounds, Vector3 position, Quaternion rotation)
        {
            var worldCenter = position + rotation * localBounds.center;
            var radius = localBounds.extents.magnitude;
            var reach = maxRenderDistance + radius;
            if ((worldCenter - _cullingCamera.transform.position).sqrMagnitude > reach * reach)
                return false;
            for (var i = 0; i < _frustumPlanes.Length; i++)
                if (_frustumPlanes[i].GetDistanceToPoint(worldCenter) < -radius)
                    return false;
            return true;
        }

        private Batch GetOrCreateBatch(RecoilVatLibrary library)
        {
            if (_batches.TryGetValue(library, out var batch)) return batch;
            if (_shader == null) _shader = Shader.Find("Recoil/VAT Crowd");
            if (_shader == null || !_shader.isSupported) return null;
            var sources = library.SourceMaterials != null && library.SourceMaterials.Length > 0
                ? library.SourceMaterials : new Material[] { null };
            var materials = new Material[sources.Length];
            for (var i = 0; i < sources.Length; i++)
            {
                var material = new Material(_shader) { name = library.name + " VAT " + i, enableInstancing = true };
                material.SetTexture(PositionTexId, library.PositionTexture);
                material.SetTexture(NormalTexId, library.NormalTexture);
                material.SetFloat(VertexCountId, library.VertexCount);
                material.SetFloat(TextureWidthId, library.TextureWidth);
                material.SetFloat(RowsPerFrameId, library.RowsPerFrame);
                if (sources[i] != null)
                {
                    // Material.mainTexture/.color read whichever property the source shader tags as
                    // the main texture/color (works for both legacy _MainTex and URP _BaseMap), so
                    // this transfers correctly regardless of the source renderer's shader family.
                    if (sources[i].mainTexture != null) material.SetTexture("_MainTex", sources[i].mainTexture);
                    material.SetColor("_Color", sources[i].color);
                }
                materials[i] = material;
            }
            batch = new Batch { Mesh = library.BakedMesh, Materials = materials };
            _batches.Add(library, batch);
            return batch;
        }

        // Call once per frame after all Submit calls, to draw whatever didn't already flush.
        public void EndFrame()
        {
            using (DrawMarker.Auto())
                foreach (var batch in _batches.Values)
                    Flush(batch);
        }

        private void Flush(Batch batch)
        {
            if (batch.Count == 0 || batch.Mesh == null) return;
            batch.Block.SetVectorArray(RowPhaseId, batch.RowPhases);
            var submeshCount = Mathf.Min(batch.Materials.Length, batch.Mesh.subMeshCount);
            for (var submesh = 0; submesh < submeshCount; submesh++)
            {
                // Off/false matches RecoilInstancedSoldierRenderer and RecoilImpostorCrowd: a crowd
                // this size casting shadows (which doubles/triples the draw work via shadow-map
                // passes) is the single most expensive toggle on this call.
                Graphics.DrawMeshInstanced(batch.Mesh, submesh, batch.Materials[submesh], batch.Matrices,
                    batch.Count, batch.Block, ShadowCastingMode.Off, false, gameObject.layer);
                LastDrawCallCount++;
            }
            batch.Count = 0;
        }

        private void OnDestroy()
        {
            foreach (var batch in _batches.Values)
                if (batch.Materials != null)
                    foreach (var material in batch.Materials)
                        if (material != null) Destroy(material);
            _batches.Clear();
        }

#if UNITY_EDITOR
        public static void ValidateVatCrowd()
        {
            var loopClip = new RecoilVatLibrary.ClipEntry { FrameCount = 4, Duration = 1f, Loop = true };
            if (ComputeFrame(loopClip, 0f) != 0)
                throw new InvalidOperationException("Loop clip must start at frame 0.");
            if (ComputeFrame(loopClip, 0.5f) != 2)
                throw new InvalidOperationException("Loop clip must reach the mid frame at half duration.");
            if (ComputeFrame(loopClip, 1f) != 0)
                throw new InvalidOperationException("Loop clip must wrap back to frame 0 after one full cycle.");
            if (ComputeFrame(loopClip, 1.25f) != 1)
                throw new InvalidOperationException("Loop clip must continue wrapping past multiple cycles.");

            var oneShotClip = new RecoilVatLibrary.ClipEntry { FrameCount = 4, Duration = 1f, Loop = false };
            if (ComputeFrame(oneShotClip, 0f) != 0)
                throw new InvalidOperationException("One-shot clip must start at frame 0.");
            if (ComputeFrame(oneShotClip, 1f) != 3)
                throw new InvalidOperationException("One-shot clip must reach its last frame exactly at its duration.");
            if (ComputeFrame(oneShotClip, 5f) != 3)
                throw new InvalidOperationException(
                    "One-shot clip (e.g. Death) must hold its last frame past its duration instead of wrapping.");

            var library = ScriptableObject.CreateInstance<RecoilVatLibrary>();
            try
            {
                library.Clips = new[]
                {
                    new RecoilVatLibrary.ClipEntry { State = "Idle", FrameCount = 2, Duration = 1f, Loop = true },
                    new RecoilVatLibrary.ClipEntry { State = "Attack", FrameCount = 3, Duration = 0.5f, Loop = false }
                };
                if (!library.TryGetClip("attack", out var found) || found.FrameCount != 3)
                    throw new InvalidOperationException("TryGetClip must match state names case-insensitively.");
                if (library.TryGetClip("Run", out _))
                    throw new InvalidOperationException("TryGetClip must report a missing state as not found.");
            }
            finally
            {
                DestroyImmediate(library);
            }
        }
#endif
    }
}
