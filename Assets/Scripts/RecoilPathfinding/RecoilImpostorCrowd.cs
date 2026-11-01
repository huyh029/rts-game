using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecoilPathfinding
{
    // Distant soldiers are drawn as instanced sprite impostors from their baked atlases, the way
    // Recoil switches far units to icons. Their meshes stop rendering, so the Animator (culled with
    // CullUpdateTransforms) only advances its state machine: attack, projectile and death timing
    // keep working, and the impostor shows the same state and frame.
    [DefaultExecutionOrder(10002)]
    public sealed class RecoilImpostorCrowd : MonoBehaviour
    {
        private const int MaxInstancesPerDraw = 1023;
        private static readonly ProfilerMarker UpdateMarker = new("RecoilBattle.Rendering.Impostors");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int ViewCountId = Shader.PropertyToID("_ViewCount");
        private static readonly int FrameCountId = Shader.PropertyToID("_FrameCount");
        private static readonly int FrameViewId = Shader.PropertyToID("_FrameView");
        private static RecoilImpostorCrowd _instance;

        [Tooltip("Vẽ lính ở xa bằng sprite impostor thay cho mesh có xương.")]
        [SerializeField] private bool impostorsEnabled = true;
        [Tooltip("Khoảng cách tới camera (m) bắt đầu chuyển sang impostor.")]
        [SerializeField, Min(1f)] private float impostorDistance = 60f;
        [Tooltip("Tỉ lệ khoảng cách phải lại gần hơn trước khi đổi về mesh, tránh nhấp nháy ở ranh giới.")]
        [SerializeField, Range(0f, 0.5f)] private float hysteresis = 0.1f;
        [Tooltip("Không vẽ impostor xa hơn khoảng này (m). 0 = không giới hạn.")]
        [SerializeField, Min(0f)] private float maxRenderDistance = 0f;

        private readonly List<Entry> _entries = new();
        private readonly Dictionary<RecoilCharacterPresentation, Entry> _byView = new();
        private readonly Dictionary<RecoilImpostorSpriteLibrary, Template> _templates = new();
        private readonly List<Sheet> _sheets = new();
        private readonly Plane[] _planes = new Plane[6];
        private Mesh _quad;
        private Shader _shader;

        public int RegisteredCount => _entries.Count;
        public int ImpostorCount { get; private set; }
        public int LastVisibleImpostors { get; private set; }
        public int LastDrawCallCount { get; private set; }
        public bool ImpostorsEnabled { get => impostorsEnabled; set => impostorsEnabled = value; }
        public float ImpostorDistance { get => impostorDistance; set => impostorDistance = Mathf.Max(1f, value); }

        private sealed class Entry
        {
            public RecoilCharacterPresentation View;
            public Transform Root;
            public Renderer[] Renderers;
            public Animator Animator;
            public Template Template;
            public bool Impostor;
        }

        private sealed class Template
        {
            public RecoilImpostorSpriteLibrary Library;
            public Sheet[] Sheets;
            public readonly Dictionary<int, Sheet> ByState = new();
            public Sheet Fallback;
            public int Horizontal;
            public int Elevated;
            public float ElevationThreshold;
            public Vector3 Center;
            public Vector2[] Sizes;
        }

        private sealed class Sheet
        {
            public Material Material;
            public int Frames;
            public bool Loop;
            public float Duration;
            public int Layer;
            public int Count;
            public readonly Matrix4x4[] Matrices = new Matrix4x4[MaxInstancesPerDraw];
            public readonly Vector4[] FrameViews = new Vector4[MaxInstancesPerDraw];
            public readonly MaterialPropertyBlock Block = new();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _instance = null;

        internal static void Register(RecoilCharacterPresentation view)
        {
            if (!Application.isPlaying || view == null || view.ImpostorLibrary == null) return;
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<RecoilImpostorCrowd>();
                if (_instance == null) _instance = new GameObject("Recoil Impostor Crowd").AddComponent<RecoilImpostorCrowd>();
            }
            _instance.Add(view);
        }

        internal static void Unregister(RecoilCharacterPresentation view)
        {
            if (_instance != null) _instance.Remove(view);
        }

        private void Add(RecoilCharacterPresentation view)
        {
            if (_byView.ContainsKey(view)) return;
            var template = GetTemplate(view);
            if (template == null) return;
            var root = view.VisualRoot != null ? view.VisualRoot : view.transform;
            var entry = new Entry
            {
                View = view, Root = view.transform, Template = template,
                Renderers = root.GetComponentsInChildren<Renderer>(true), Animator = view.Animator
            };
            _byView.Add(view, entry);
            _entries.Add(entry);
        }

        private void Remove(RecoilCharacterPresentation view)
        {
            if (ReferenceEquals(view, null) || !_byView.TryGetValue(view, out var entry)) return;
            SetImpostor(entry, false);
            _byView.Remove(view);
            _entries.Remove(entry);
        }

        private void LateUpdate()
        {
            using (UpdateMarker.Auto())
            {
                LastDrawCallCount = 0;
                LastVisibleImpostors = 0;
                var camera = Camera.main;
                var active = impostorsEnabled && camera != null && _entries.Count > 0;
                var impostors = 0;
                if (!active)
                {
                    for (var i = 0; i < _entries.Count; i++) SetImpostor(_entries[i], false);
                    ImpostorCount = 0;
                    return;
                }
                EnsureResources();
                GeometryUtility.CalculateFrustumPlanes(camera, _planes);
                var cameraTransform = camera.transform;
                var cameraPosition = cameraTransform.position;
                var cameraRotation = cameraTransform.rotation;
                var enter = impostorDistance * impostorDistance;
                var exit = impostorDistance * (1f - hysteresis);
                exit *= exit;
                var maxDistance = maxRenderDistance > 0f ? maxRenderDistance * maxRenderDistance : float.PositiveInfinity;
                for (var i = _entries.Count - 1; i >= 0; i--)
                {
                    var entry = _entries[i];
                    if (entry.View == null) { _byView.Remove(entry.View); _entries.RemoveAt(i); continue; }
                    if (!entry.View.isActiveAndEnabled) continue;
                    var root = entry.Root;
                    var distance = (root.position - cameraPosition).sqrMagnitude;
                    SetImpostor(entry, entry.Impostor ? distance > exit : distance > enter);
                    if (!entry.Impostor) continue;
                    impostors++;
                    if (distance > maxDistance) continue;
                    var template = entry.Template;
                    var center = root.TransformPoint(template.Center);
                    var scale = root.lossyScale.y;
                    var radius = 0.5f * scale * Mathf.Max(template.Sizes[0].x, template.Sizes[0].y) * 1.5f;
                    if (!InFrustum(center, radius)) continue;
                    var view = SelectView(template, root, cameraPosition - center);
                    var sheet = SelectSheet(entry, out var frame);
                    if (sheet == null) continue;
                    var size = template.Sizes[view] * scale;
                    sheet.Matrices[sheet.Count] = Matrix4x4.TRS(center, cameraRotation, new Vector3(size.x, size.y, 1f));
                    sheet.FrameViews[sheet.Count] = new Vector4(frame, view, 0f, 0f);
                    sheet.Layer = entry.View.gameObject.layer;
                    if (++sheet.Count == MaxInstancesPerDraw) Flush(sheet);
                    LastVisibleImpostors++;
                }
                for (var i = 0; i < _sheets.Count; i++) Flush(_sheets[i]);
                ImpostorCount = impostors;
            }
        }

        private void Flush(Sheet sheet)
        {
            if (sheet.Count == 0) return;
            sheet.Block.SetVectorArray(FrameViewId, sheet.FrameViews);
            Graphics.DrawMeshInstanced(_quad, 0, sheet.Material, sheet.Matrices, sheet.Count, sheet.Block,
                ShadowCastingMode.Off, false, sheet.Layer);
            LastDrawCallCount++;
            sheet.Count = 0;
        }

        private static void SetImpostor(Entry entry, bool impostor)
        {
            if (entry.Impostor == impostor) return;
            entry.Impostor = impostor;
            for (var i = 0; i < entry.Renderers.Length; i++)
                if (entry.Renderers[i] != null) entry.Renderers[i].forceRenderingOff = impostor;
        }

        private bool InFrustum(Vector3 center, float radius)
        {
            for (var i = 0; i < _planes.Length; i++)
                if (_planes[i].GetDistanceToPoint(center) < -radius) return false;
            return true;
        }

        // The capture camera sits at azimuth (view % H) * 360 / H around the unit, either level or
        // raised to the library's elevation; pick the bake closest to the real camera direction.
        private static int SelectView(Template template, Transform root, Vector3 toCamera)
        {
            var local = Quaternion.Inverse(root.rotation) * toCamera;
            var horizontal = Mathf.Sqrt(local.x * local.x + local.z * local.z);
            var pitch = Mathf.Atan2(local.y, horizontal) * Mathf.Rad2Deg;
            var elevated = template.Elevated > 0 && pitch > template.ElevationThreshold;
            var count = elevated ? template.Elevated : template.Horizontal;
            var azimuth = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            if (azimuth < 0f) azimuth += 360f;
            var index = Mathf.RoundToInt(azimuth * count / 360f) % count;
            return elevated ? template.Horizontal + index : index;
        }

        private static Sheet SelectSheet(Entry entry, out int frame)
        {
            frame = 0;
            var template = entry.Template;
            var animator = entry.Animator;
            Sheet sheet = null;
            var normalizedTime = 0f;
            if (animator != null && animator.isActiveAndEnabled && animator.runtimeAnimatorController != null)
            {
                var state = animator.IsInTransition(0) ? animator.GetNextAnimatorStateInfo(0) : animator.GetCurrentAnimatorStateInfo(0);
                if (template.ByState.TryGetValue(state.shortNameHash, out sheet)) normalizedTime = state.normalizedTime;
            }
            if (sheet == null)
            {
                // Unknown state or no animator: loop the idle bake on its own clock.
                sheet = template.Fallback;
                if (sheet == null) return null;
                normalizedTime = Time.time / Mathf.Max(0.01f, sheet.Duration);
            }
            // Frames were baked at k / F for loops and at k / (F - 1) for one-shot clips.
            frame = sheet.Loop || normalizedTime < 0f
                ? (int)((normalizedTime - Mathf.Floor(normalizedTime)) * sheet.Frames) % sheet.Frames
                : Mathf.RoundToInt(Mathf.Clamp01(normalizedTime) * (sheet.Frames - 1));
            return sheet;
        }

        private Template GetTemplate(RecoilCharacterPresentation view)
        {
            var library = view.ImpostorLibrary;
            if (_templates.TryGetValue(library, out var template)) return template;
            EnsureResources();
            if (_shader == null || library.Animations == null || library.Animations.Length == 0) return null;
            var horizontal = Mathf.Max(1, library.HorizontalDirections);
            var elevated = Mathf.Max(0, library.ElevatedDirections);
            template = new Template
            {
                Library = library, Horizontal = horizontal, Elevated = elevated,
                ElevationThreshold = library.ElevatedViewAngle * 0.5f
            };
            var sheets = new List<Sheet>();
            foreach (var animation in library.Animations)
            {
                if (animation == null || animation.Atlas == null || string.IsNullOrEmpty(animation.State)) continue;
                var frames = animation.Frames > 0 ? animation.Frames : Mathf.Max(1, library.FramesPerState);
                var material = new Material(_shader) { name = $"{library.name} {animation.State} impostor", enableInstancing = true };
                material.SetTexture(MainTexId, animation.Atlas);
                material.SetFloat(ViewCountId, horizontal + elevated);
                material.SetFloat(FrameCountId, frames);
                var sheet = new Sheet { Material = material, Frames = frames, Loop = animation.Loop, Duration = animation.Duration };
                template.ByState[Animator.StringToHash(animation.State)] = sheet;
                sheets.Add(sheet);
                _sheets.Add(sheet);
                if (string.Equals(animation.State, "Idle", System.StringComparison.OrdinalIgnoreCase)) template.Fallback = sheet;
            }
            if (sheets.Count == 0) return null;
            template.Sheets = sheets.ToArray();
            template.Fallback ??= sheets[0];
            // A library without an attack bake keeps showing its idle pose while attacking.
            var attackHash = Animator.StringToHash(view.AttackStateName ?? "Attack");
            if (!template.ByState.ContainsKey(attackHash)) template.ByState[attackHash] = template.Fallback;
            MeasureFraming(template, view, library);
            _templates.Add(library, template);
            return template;
        }

        // Rebuilds the capture framing: bounds of the LOD0 renderers in the unit's root space and
        // a camera centred on the hips (or root bone), sized as CongThanhImpostorCaptureWindow does.
        private static void MeasureFraming(Template template, RecoilCharacterPresentation view, RecoilImpostorSpriteLibrary library)
        {
            var root = view.transform;
            var group = root.GetComponentInChildren<LODGroup>(true);
            var lods = group != null ? group.GetLODs() : null;
            var renderers = lods != null && lods.Length > 0 ? lods[0].renderers : root.GetComponentsInChildren<Renderer>(true);
            var toRoot = root.worldToLocalMatrix;
            var min = Vector3.positiveInfinity;
            var max = Vector3.negativeInfinity;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var space = renderer is SkinnedMeshRenderer skinned && skinned.rootBone != null ? skinned.rootBone : renderer.transform;
                var matrix = toRoot * space.localToWorldMatrix;
                var bounds = renderer.localBounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var point = matrix.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents, new Vector3(
                        (corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f)));
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                }
            }
            if (!float.IsFinite(min.x)) { min = new Vector3(-0.5f, 0f, -0.5f); max = new Vector3(0.5f, 1.8f, 0.5f); }
            var boxCenter = (min + max) * 0.5f;
            var extents = (max - min) * 0.5f;

            Transform pivot = null;
            var animator = view.Animator;
            if (animator != null && animator.isHuman) pivot = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (pivot == null)
                foreach (var renderer in renderers)
                    if (renderer is SkinnedMeshRenderer skinned && skinned.rootBone != null) { pivot = skinned.rootBone; break; }
            if (pivot == null && animator != null) pivot = animator.transform;
            var center = (pivot != null ? root.InverseTransformPoint(pivot.position) : boxCenter) + library.CaptureOffset;
            template.Center = center;

            var aspect = library.FrameWidth / (float)Mathf.Max(1, library.FrameHeight);
            var scale = Mathf.Max(0.1f, library.OrthographicScale);
            var count = template.Horizontal + template.Elevated;
            template.Sizes = new Vector2[count];
            for (var viewIndex = 0; viewIndex < count; viewIndex++)
            {
                var raised = viewIndex >= template.Horizontal;
                var azimuth = (raised ? viewIndex - template.Horizontal : viewIndex) * 360f /
                    (raised ? Mathf.Max(1, template.Elevated) : template.Horizontal) * Mathf.Deg2Rad;
                var pitch = (raised ? library.ElevatedViewAngle : 0f) * Mathf.Deg2Rad;
                var direction = new Vector3(Mathf.Sin(azimuth) * Mathf.Cos(pitch), Mathf.Sin(pitch), Mathf.Cos(azimuth) * Mathf.Cos(pitch)).normalized;
                var rotation = Quaternion.LookRotation(-direction, Vector3.up);
                var right = rotation * Vector3.right;
                var up = rotation * Vector3.up;
                var horizontalExtent = 0f;
                var verticalExtent = 0f;
                for (var corner = 0; corner < 8; corner++)
                {
                    var relative = boxCenter + Vector3.Scale(extents, new Vector3(
                        (corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f)) - center;
                    horizontalExtent = Mathf.Max(horizontalExtent, Mathf.Abs(Vector3.Dot(relative, right)));
                    verticalExtent = Mathf.Max(verticalExtent, Mathf.Abs(Vector3.Dot(relative, up)));
                }
                var halfHeight = Mathf.Max(Mathf.Max(verticalExtent, horizontalExtent / aspect), 0.1f) * 1.15f / scale;
                template.Sizes[viewIndex] = new Vector2(2f * halfHeight * aspect, 2f * halfHeight);
            }
        }

        private void EnsureResources()
        {
            if (_shader == null) _shader = Shader.Find("Recoil/Impostor Crowd");
            if (_quad != null) return;
            _quad = new Mesh { name = "Recoil impostor quad" };
            _quad.SetVertices(new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f) });
            _quad.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) });
            _quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
            _quad.bounds = new Bounds(Vector3.zero, Vector3.one);
        }

        private void OnDisable()
        {
            for (var i = 0; i < _entries.Count; i++) SetImpostor(_entries[i], false);
        }

        private void OnDestroy()
        {
            for (var i = 0; i < _entries.Count; i++) SetImpostor(_entries[i], false);
            foreach (var sheet in _sheets) if (sheet.Material != null) Destroy(sheet.Material);
            if (_quad != null) Destroy(_quad);
            _entries.Clear(); _byView.Clear(); _templates.Clear(); _sheets.Clear();
            if (_instance == this) _instance = null;
        }
    }
}
