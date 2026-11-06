using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Rendering;

namespace RecoilPathfinding
{
    [DefaultExecutionOrder(20000)]
    public sealed class RecoilGpuSkinnedCrowdRenderer : MonoBehaviour
    {
        // Temporary global override so every renderer uses the native path during comparison.
        public static bool SuspendedForProfiling { get; set; } = true;
        private const int BatchSize = 256;
        private static readonly int BonesId = Shader.PropertyToID("_SkinBones");
        private static readonly int OffsetId = Shader.PropertyToID("_BoneOffset");
        private static readonly int ImpostorFrameId = Shader.PropertyToID("_ImpostorFrame");
        private readonly Dictionary<Mesh, RecoilGpuSkinningLibrary.Entry> meshes = new();
        private readonly Dictionary<GameObject, View> views = new();
        private readonly HashSet<GameObject> rejected = new();
        private readonly Dictionary<(Mesh, Material, int, int), Batch> batches = new();
        private readonly Dictionary<Material, Material> materials = new();
        private readonly Plane[] planes = new Plane[6];
        private readonly List<GameObject> stale = new();
        private readonly Dictionary<Transform, BoneSlot> boneLookup = new();
        private readonly List<BoneSlot> boneSlots = new();
        private TransformAccessArray boneTransforms;
        private NativeArray<float4x4> capturedBones;
        private NativeArray<int> usedFrames;
        private NativeList<BoneWork> boneWork;
        private NativeList<BoneBinding> bindings;
        private NativeList<int> boneIndexMap;
        private readonly Stack<int> freeBoneIds = new();
        private readonly Dictionary<int, Stack<int>> freeBindingRanges = new();
        private NativeList<float4x4> skinPalette;
        private NativeList<VisibilityInput> visibilityInputs;
        private NativeList<float> lodThresholds;
        private NativeArray<float4> frustumPlanes;
        private NativeArray<byte> visibilityResults;
        private readonly List<Part> visibilityParts = new();
        private readonly List<View> visibilityViews = new();
        private ComputeBuffer skinBuffer;
        private bool nativeFallback;
        private RecoilAutoBattleDemo battle;
        private static readonly RecoilCpuPhases.Marker GatherMarker = new("Recoil.GpuSkin.GatherVisible");
        private static readonly RecoilCpuPhases.Marker LodMarker = new("Recoil.GpuSkin.SelectLod");
        private static readonly RecoilCpuPhases.Marker PartsMarker = new("Recoil.GpuSkin.CollectParts");
        private static readonly RecoilCpuPhases.Marker BonesMarker = new("Recoil.GpuSkin.CaptureAndSkinJobs");
        private static readonly RecoilCpuPhases.Marker UploadMarker = new("Recoil.GpuSkin.UploadPalette");
        private static readonly RecoilCpuPhases.Marker DrawMarker = new("Recoil.GpuSkin.DrawBatches");
        private Shader shader;
        private bool loaded;
        public int RegisteredRenderers { get; private set; }
        public int DrawnInstances { get; private set; }
        public int DrawCalls { get; private set; }
        public int FallbackRenderers { get; private set; }
        public int BonesUploadedLastFrame { get; private set; }

        private sealed class BoneSlot
        {
            public Transform Transform;
            public int Index, Id, References;
        }
        private struct BoneBinding { public int BoneId; public float4x4 BindPose; }
        private struct VisibilityInput
        {
            public float3 Center, Extents, GroupCenter;
            public float Size;
            public int LodStart, LodCount, LodMask;
        }
        [BurstCompile]
        private struct VisibilityJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<VisibilityInput> Inputs;
            [ReadOnly] public NativeArray<float> Thresholds;
            [ReadOnly] public NativeArray<float4> Planes;
            [WriteOnly] public NativeArray<byte> Results;
            public float3 CameraPosition;
            public float ProjectionScale, LodBias;
            public int MaximumLod;
            public bool Orthographic;

            public void Execute(int index)
            {
                var input = Inputs[index];
                Results[index] = 0;
                var selectedLod = 0;
                if (input.LodCount > 0)
                {
                    var height = input.Size * ProjectionScale * LodBias;
                    if (!Orthographic) height /= math.max(0.01f, math.distance(CameraPosition, input.GroupCenter));
                    var lod = -1;
                    for (var i = math.clamp(MaximumLod, 0, input.LodCount - 1); i < input.LodCount; i++)
                        if (height >= Thresholds[input.LodStart + i]) { lod = i; break; }
                    if (lod < 0 || (input.LodMask & (1 << lod)) == 0) return;
                    selectedLod = lod;
                }
                for (var i = 0; i < Planes.Length; i++)
                {
                    var plane = Planes[i];
                    if (math.dot(plane.xyz, input.Center) + plane.w + math.dot(math.abs(plane.xyz), input.Extents) < 0f)
                        return;
                }
                Results[index] = (byte)(selectedLod + 1);
            }
        }
        private struct BoneWork
        {
            public int BindingStart, Count, OutputStart;
            public float4x4 World;
        }
        [BurstCompile]
        private struct MarkUsedBonesJob : IJob
        {
            [ReadOnly] public NativeArray<BoneWork> Work;
            [ReadOnly] public NativeArray<BoneBinding> Bindings;
            [ReadOnly] public NativeArray<int> BoneIndexMap;
            public NativeArray<int> UsedFrames;
            public int Frame;
            public void Execute()
            {
                for (var part = 0; part < Work.Length; part++)
                {
                    var work = Work[part];
                    for (var i = 0; i < work.Count; i++)
                    {
                        var boneId = Bindings[work.BindingStart + i].BoneId;
                        if (boneId >= 0) UsedFrames[BoneIndexMap[boneId]] = Frame;
                    }
                }
            }
        }
        [BurstCompile]
        private struct CaptureBonesJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<int> UsedFrames;
            public int Frame;
            public NativeArray<float4x4> Output;
            public void Execute(int index, TransformAccess transform)
            {
                if (UsedFrames[index] == Frame && transform.isValid) Output[index] = transform.localToWorldMatrix;
            }
        }
        [BurstCompile]
        private struct SkinPaletteJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<BoneWork> Work;
            [ReadOnly] public NativeArray<BoneBinding> Bindings;
            [ReadOnly] public NativeArray<int> BoneIndexMap;
            [ReadOnly] public NativeArray<float4x4> Captured;
            [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<float4x4> Output;
            public void Execute(int index)
            {
                var work = Work[index];
                var inverse = math.inverse(work.World);
                for (var i = 0; i < work.Count; i++)
                {
                    var binding = Bindings[work.BindingStart + i];
                    if (binding.BoneId < 0) { Output[work.OutputStart + i] = float4x4.identity; continue; }
                    Output[work.OutputStart + i] = math.mul(math.mul(inverse,
                        Captured[BoneIndexMap[binding.BoneId]]), binding.BindPose);
                }
            }
        }

        private sealed class Part
        {
            public Renderer Renderer;
            public RecoilCharacterPresentation Presentation;
            public bool Impostor;
            public Transform UnitRoot;
            public float FootHeight, GroundOffset;
            public RecoilGpuSkinningLibrary.Entry Entry;
            public BoneSlot[] Slots;
            public int BindingStart;
            public Transform Transform;
            public GameObject Object;
            public Bounds LocalBounds;
            public int LodMask, Layer;
            public Material[] Materials;
            public Batch[] Batches;
            public bool OriginalForceOff, OriginalUpdateOffscreen;
        }
        private sealed class View
        {
            public readonly List<Part> Parts = new();
            public Animator[] Animators;
            public AnimatorCullingMode[] OriginalCulling;
            public LODGroup Group;
            public LOD[] Lods;
            public Transform GroupTransform;
            public Vector3 LocalCenter;
            public float Size;
        }
        private sealed class Batch : IDisposable
        {
            public Mesh Mesh;
            public Material Material;
            public int Submesh, Layer, Count;
            public readonly Matrix4x4[] Transforms = new Matrix4x4[BatchSize];
            public readonly float[] Offsets = new float[BatchSize];
            public readonly float[] AnimationFrames = new float[BatchSize];
            public readonly MaterialPropertyBlock Properties = new();
            public Batch Next;
            public Batch Current;
            public void Dispose() { Next?.Dispose(); }
        }

        private bool Load()
        {
            if (loaded) return shader != null && meshes.Count > 0;
            loaded = true;
            battle = GetComponent<RecoilAutoBattleDemo>();
            if (!SystemInfo.supportsInstancing || SystemInfo.graphicsShaderLevel < 45) return false;
            shader = Shader.Find("Recoil/GPU Skinned Crowd");
            var library = Resources.Load<RecoilGpuSkinningLibrary>("RecoilGpuSkinningMeshes");
            if (shader == null || !shader.isSupported || library == null)
            {
                Debug.LogWarning("GPU bone instancing chưa có mesh/shader hợp lệ. Dùng renderer gốc. Chạy Game Tools/Công Thành/Chuẩn bị GPU instancing xương lính trước khi Play.", this);
                return false;
            }
            foreach (var entry in library.Entries)
                if (entry.Source != null && entry.Mesh != null && entry.BindPoses != null && entry.BindPoses.Length > 0)
                    meshes[entry.Source] = entry;
            return meshes.Count > 0;
        }

        public void Register(GameObject unit)
        {
            if (SuspendedForProfiling) return;
            if (unit == null || views.ContainsKey(unit) || rejected.Contains(unit) || !isActiveAndEnabled) return;
            if (!Load()) { rejected.Add(unit); return; }
            var view = new View { Group = unit.GetComponentInChildren<LODGroup>(true) };
            if (view.Group != null)
            {
                // Prototype supports one non-crossfading LODGroup per unit.
                if (unit.GetComponentsInChildren<LODGroup>(true).Length != 1 || view.Group.fadeMode != LODFadeMode.None || view.Group.GetLODs().Length == 0)
                { rejected.Add(unit); return; }
                view.Lods = view.Group.GetLODs();
                view.GroupTransform = view.Group.transform;
                view.LocalCenter = view.Group.localReferencePoint;
                view.Size = view.Group.size;
            }
            foreach (var renderer in unit.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!renderer.enabled) continue;
                if (renderer.sharedMesh == null || !meshes.TryGetValue(renderer.sharedMesh, out var entry) ||
                    renderer.HasPropertyBlock() || renderer.sharedMesh.blendShapeCount != 0)
                { FallbackRenderers++; continue; }
                var bones = renderer.bones;
                var sourceMaterials = renderer.sharedMaterials;
                var valid = bones.Length == entry.BindPoses.Length && sourceMaterials.Length == entry.Mesh.subMeshCount;
                foreach (var bone in bones) if (bone == null) valid = false;
                foreach (var material in sourceMaterials)
                    if (material == null || material.renderQueue >= 3000 ||
                        (!material.HasProperty("_BaseMap") && !material.HasProperty("_MainTex"))) valid = false;
                if (!valid) { FallbackRenderers++; continue; }
                var part = new Part { Renderer = renderer, Entry = entry,
                    Transform = renderer.transform, Object = renderer.gameObject, LocalBounds = entry.Mesh.bounds,
                    Layer = renderer.gameObject.layer, Slots = new BoneSlot[bones.Length],
                    Materials = new Material[sourceMaterials.Length], Batches = new Batch[sourceMaterials.Length], OriginalForceOff = renderer.forceRenderingOff,
                    OriginalUpdateOffscreen = renderer.updateWhenOffscreen };
                for (var i = 0; i < sourceMaterials.Length; i++)
                {
                    part.Materials[i] = GetMaterial(sourceMaterials[i]);
                    var key = (entry.Mesh, part.Materials[i], i, part.Layer);
                    if (!batches.TryGetValue(key, out var batch))
                    {
                        batch = CreateBatch(entry.Mesh, part.Materials[i], i, part.Layer);
                        batches.Add(key, batch);
                    }
                    part.Batches[i] = batch;
                }
                if (entry.UsedBoneIndices == null)
                    for (var i = 0; i < bones.Length; i++) part.Slots[i] = RegisterBone(bones[i]);
                else
                    foreach (var i in entry.UsedBoneIndices) part.Slots[i] = RegisterBone(bones[i]);
                part.BindingStart = AllocateBindings(part.Slots, entry.BindPoses);
                if (view.Lods != null)
                    for (var i = 0; i < view.Lods.Length; i++)
                        if (Array.IndexOf(view.Lods[i].renderers, renderer) >= 0) part.LodMask |= 1 << i;
                view.Parts.Add(part);
            }
            foreach (var renderer in unit.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (!renderer.enabled) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                var mesh = filter != null ? filter.sharedMesh : null;
                var sourceMaterials = renderer.sharedMaterials;
                var valid = mesh != null && sourceMaterials.Length == mesh.subMeshCount && !renderer.HasPropertyBlock();
                foreach (var material in sourceMaterials)
                    if (material == null || material.renderQueue >= 3000 ||
                        (!material.HasProperty("_BaseMap") && !material.HasProperty("_MainTex"))) valid = false;
                if (!valid) { FallbackRenderers++; continue; }
                var part = new Part { Renderer = renderer, Transform = renderer.transform, Object = renderer.gameObject,
                    Presentation = unit.GetComponentInChildren<RecoilCharacterPresentation>(true),
                    Impostor = sourceMaterials.Length > 0 && sourceMaterials[0].shader.name == "Recoil/Upper Hemisphere Impostor",
                    UnitRoot = unit.transform,
                    FootHeight = sourceMaterials.Length > 0 && sourceMaterials[0].HasProperty("_FootHeight") ? sourceMaterials[0].GetFloat("_FootHeight") : 0f,
                    GroundOffset = unit.GetComponent<RecoilNavMeshPathAgent>() != null ? unit.GetComponent<RecoilNavMeshPathAgent>().MovementSettings.VerticalOffset : 0f,
                    LocalBounds = mesh.bounds, Layer = renderer.gameObject.layer, Slots = Array.Empty<BoneSlot>(),
                    Materials = new Material[sourceMaterials.Length], Batches = new Batch[sourceMaterials.Length],
                    OriginalForceOff = renderer.forceRenderingOff };
                for (var i = 0; i < sourceMaterials.Length; i++)
                {
                    part.Materials[i] = GetMaterial(sourceMaterials[i]);
                    var key = (mesh, part.Materials[i], i, part.Layer);
                    if (!batches.TryGetValue(key, out var batch))
                    {
                        batch = CreateBatch(mesh, part.Materials[i], i, part.Layer);
                        batches.Add(key, batch);
                    }
                    part.Batches[i] = batch;
                }
                if (view.Lods != null)
                    for (var i = 0; i < view.Lods.Length; i++)
                        if (Array.IndexOf(view.Lods[i].renderers, renderer) >= 0) part.LodMask |= 1 << i;
                view.Parts.Add(part);
            }
            if (view.Parts.Count == 0) { rejected.Add(unit); return; }
            view.Animators = unit.GetComponentsInChildren<Animator>(true);
            view.OriginalCulling = new AnimatorCullingMode[view.Animators.Length];
            for (var i = 0; i < view.Animators.Length; i++)
            {
                view.OriginalCulling[i] = view.Animators[i].cullingMode;
                // Hidden native renderers must not cause the Animator to stop writing bones.
                view.Animators[i].cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            foreach (var part in view.Parts)
            {
                part.Renderer.forceRenderingOff = nativeFallback ? part.OriginalForceOff : true;
            }
            views.Add(unit, view);
            RegisteredRenderers += view.Parts.Count;
        }

        public void Unregister(GameObject unit)
        {
            if (!ReferenceEquals(unit, null)) rejected.Remove(unit);
            if (ReferenceEquals(unit, null) || !views.TryGetValue(unit, out var view)) return;
            foreach (var part in view.Parts)
            {
                foreach (var slot in part.Slots) if (slot != null) UnregisterBone(slot);
                if (part.Slots.Length > 0) FreeBindings(part.BindingStart, part.Slots.Length);
                if (part.Renderer != null)
                {
                    part.Renderer.forceRenderingOff = part.OriginalForceOff;
                    if (part.Renderer is SkinnedMeshRenderer skinned) skinned.updateWhenOffscreen = part.OriginalUpdateOffscreen;
                }
            }
            for (var i = 0; i < view.Animators.Length; i++)
                if (view.Animators[i] != null) view.Animators[i].cullingMode = view.OriginalCulling[i];
            RegisteredRenderers -= view.Parts.Count;
            views.Remove(unit);
        }

        private Material GetMaterial(Material source)
        {
            if (source.shader != null && source.shader.name == "Recoil/Upper Hemisphere Impostor") return source;
            if (materials.TryGetValue(source, out var result)) return result;
            result = new Material(shader) { name = source.name + " (GPU bones)", enableInstancing = true };
            var texture = source.HasProperty("_BaseMap") && source.GetTexture("_BaseMap") != null ? "_BaseMap" :
                source.HasProperty("_MainTex") ? "_MainTex" : "_BaseMap";
            result.SetTexture("_BaseMap", source.GetTexture(texture));
            result.SetTextureScale("_BaseMap", source.GetTextureScale(texture));
            result.SetTextureOffset("_BaseMap", source.GetTextureOffset(texture));
            result.SetColor("_BaseColor", source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") :
                source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white);
            result.SetFloat("_Cutoff", source.HasProperty("_Cutoff") ? source.GetFloat("_Cutoff") : 0.5f);
            result.SetFloat("_AlphaClip", source.IsKeywordEnabled("_ALPHATEST_ON") ||
                (source.HasProperty("_AlphaClip") && source.GetFloat("_AlphaClip") > 0f) ? 1f : 0f);
            result.SetFloat("_Cull", source.HasProperty("_Cull") ? source.GetFloat("_Cull") : 2f);
            materials.Add(source, result);
            return result;
        }

        private BoneSlot RegisterBone(Transform bone)
        {
            if (!boneLookup.TryGetValue(bone, out var slot))
            {
                if (!boneTransforms.isCreated) boneTransforms = new TransformAccessArray(1024);
                if (!boneIndexMap.IsCreated) boneIndexMap = new NativeList<int>(1024, Allocator.Persistent);
                var id = freeBoneIds.Count > 0 ? freeBoneIds.Pop() : boneIndexMap.Length;
                if (id == boneIndexMap.Length) boneIndexMap.Add(0);
                slot = new BoneSlot { Transform = bone, Index = boneSlots.Count, Id = id };
                boneIndexMap[id] = slot.Index;
                boneLookup.Add(bone, slot);
                boneSlots.Add(slot);
                boneTransforms.Add(bone);
            }
            slot.References++;
            return slot;
        }

        private void UnregisterBone(BoneSlot slot)
        {
            if (--slot.References != 0) return;
            var last = boneSlots.Count - 1;
            boneLookup.Remove(slot.Transform);
            boneIndexMap[slot.Id] = -1;
            freeBoneIds.Push(slot.Id);
            boneTransforms.RemoveAtSwapBack(slot.Index);
            if (slot.Index != last)
            {
                var moved = boneSlots[last];
                boneSlots[slot.Index] = moved;
                moved.Index = slot.Index;
                boneIndexMap[moved.Id] = moved.Index;
            }
            boneSlots.RemoveAt(last);
        }

        private int AllocateBindings(BoneSlot[] slots, Matrix4x4[] bindPoses)
        {
            if (!bindings.IsCreated) bindings = new NativeList<BoneBinding>(1024, Allocator.Persistent);
            var start = bindings.Length;
            if (freeBindingRanges.TryGetValue(slots.Length, out var ranges) && ranges.Count > 0)
                start = ranges.Pop();
            else bindings.ResizeUninitialized(start + slots.Length);
            for (var i = 0; i < slots.Length; i++)
                bindings[start + i] = new BoneBinding { BoneId = slots[i] != null ? slots[i].Id : -1, BindPose = bindPoses[i] };
            return start;
        }

        private void FreeBindings(int start, int count)
        {
            if (!freeBindingRanges.TryGetValue(count, out var ranges))
            {
                ranges = new Stack<int>();
                freeBindingRanges.Add(count, ranges);
            }
            ranges.Push(start);
        }

        private void EnsureJobStorage()
        {
            if (!capturedBones.IsCreated || capturedBones.Length < boneSlots.Count)
            {
                if (capturedBones.IsCreated) capturedBones.Dispose();
                if (usedFrames.IsCreated) usedFrames.Dispose();
                var capacity = Mathf.NextPowerOfTwo(Mathf.Max(16, boneSlots.Count));
                capturedBones = new NativeArray<float4x4>(capacity, Allocator.Persistent);
                usedFrames = new NativeArray<int>(capacity, Allocator.Persistent);
            }
            if (!boneWork.IsCreated) boneWork = new NativeList<BoneWork>(1024, Allocator.Persistent);
            if (!skinPalette.IsCreated) skinPalette = new NativeList<float4x4>(1024, Allocator.Persistent);
            boneWork.Clear();
        }

        private void SetNativeFallback(bool value)
        {
            if (nativeFallback == value) return;
            nativeFallback = value;
            foreach (var view in views.Values)
                foreach (var part in view.Parts)
                    if (part.Renderer != null) part.Renderer.forceRenderingOff = value ? part.OriginalForceOff : true;
        }

        private void LateUpdate()
        {
            if (SuspendedForProfiling)
            {
                SetRenderingEnabled(false);
                return;
            }
            if (battle == null) battle = GetComponent<RecoilAutoBattleDemo>();
            if (battle != null && !battle.UsesGpuBoneInstancing)
            {
                SetRenderingEnabled(false);
                return;
            }
            DrawCalls = DrawnInstances = BonesUploadedLastFrame = 0;
            var camera = Camera.main;
            SetNativeFallback(camera == null);
            if (camera == null || views.Count == 0) return;
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
            var cameraPosition = camera.transform.position;
            var projectionScale = camera.orthographic ? 1f / (2f * camera.orthographicSize) :
                1f / (2f * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f));
            var orthographic = camera.orthographic;
            var cullingMask = camera.cullingMask;
            var lodBias = QualitySettings.lodBias;
            var maximumLod = QualitySettings.maximumLODLevel;
            var frame = Time.frameCount;
            var paletteCount = 0;
            // Prune before collecting work: swap-removing a bone changes its captured index.
            stale.Clear();
            foreach (var pair in views) if (pair.Key == null) stale.Add(pair.Key);
            foreach (var unit in stale) Unregister(unit);
            EnsureJobStorage();
            using (GatherMarker.Auto())
            {
                if (!visibilityInputs.IsCreated) visibilityInputs = new NativeList<VisibilityInput>(1024, Allocator.Persistent);
                if (!lodThresholds.IsCreated) lodThresholds = new NativeList<float>(128, Allocator.Persistent);
                if (!frustumPlanes.IsCreated) frustumPlanes = new NativeArray<float4>(6, Allocator.Persistent);
                visibilityInputs.Clear();
                lodThresholds.Clear();
                visibilityParts.Clear();
                visibilityViews.Clear();
                for (var i = 0; i < planes.Length; i++)
                    frustumPlanes[i] = new float4((float3)planes[i].normal, planes[i].distance);
                // Capture Unity object state here; numerical LOD and culling run in Burst.
                foreach (var pair in views)
                {
                    if (!pair.Key.activeInHierarchy) continue;
                    var view = pair.Value;
                    var groupEnabled = view.Group != null && view.Group.enabled;
                    var groupCenter = Vector3.zero;
                    var size = 0f;
                    var lodStart = lodThresholds.Length;
                    if (groupEnabled)
                    {
                        var matrix = view.GroupTransform.localToWorldMatrix;
                        groupCenter = matrix.MultiplyPoint3x4(view.LocalCenter);
                        size = view.Size * MaxWorldScale(matrix);
                        foreach (var lod in view.Lods) lodThresholds.Add(lod.screenRelativeTransitionHeight);
                        visibilityInputs.Add(new VisibilityInput { Center = groupCenter,
                            Extents = new float3(size + 1.5f), GroupCenter = groupCenter, Size = size,
                            LodStart = lodStart, LodCount = view.Lods.Length, LodMask = -1 });
                        visibilityParts.Add(null);
                        visibilityViews.Add(view);
                        continue;
                    }
                    foreach (var part in view.Parts)
                    {
                        if (part.OriginalForceOff || (cullingMask & (1 << part.Layer)) == 0 ||
                            part.Renderer == null || !part.Renderer.enabled || !part.Object.activeInHierarchy) continue;
                        var bounds = groupEnabled ? new Bounds(groupCenter, Vector3.one * (size * 2f + 3f)) :
                            TransformBounds(part.LocalBounds, part.Transform.localToWorldMatrix);
                        visibilityInputs.Add(new VisibilityInput { Center = bounds.center,
                            Extents = bounds.extents, GroupCenter = groupCenter, Size = size,
                            LodStart = lodStart, LodCount = groupEnabled && part.LodMask != 0 ? view.Lods.Length : 0, LodMask = part.LodMask });
                        visibilityParts.Add(part);
                        visibilityViews.Add(null);
                    }
                }
                var count = visibilityInputs.Length;
                if (count > 0)
                {
                    if (!visibilityResults.IsCreated || visibilityResults.Length < count)
                    {
                        if (visibilityResults.IsCreated) visibilityResults.Dispose();
                        visibilityResults = new NativeArray<byte>(Mathf.NextPowerOfTwo(count), Allocator.Persistent);
                    }
                    using (LodMarker.Auto())
                        new VisibilityJob { Inputs = visibilityInputs.AsArray(), Thresholds = lodThresholds.AsArray(),
                            Planes = frustumPlanes, Results = visibilityResults, CameraPosition = cameraPosition,
                            ProjectionScale = projectionScale, LodBias = lodBias, MaximumLod = maximumLod,
                            Orthographic = orthographic }.Schedule(count, 64).Complete();
                    using (PartsMarker.Auto())
                    for (var i = 0; i < count; i++)
                    {
                        if (visibilityResults[i] == 0) continue;
                        var view = visibilityViews[i];
                        if (view != null)
                        {
                            var lod = visibilityResults[i] - 1;
                            foreach (var part in view.Parts)
                            {
                                if (part.LodMask != 0 && (part.LodMask & (1 << lod)) == 0) continue;
                                CollectVisiblePart(part, cullingMask, ref paletteCount);
                            }
                        }
                        else CollectVisiblePart(visibilityParts[i], cullingMask, ref paletteCount);
                    }
                }
            }
            if (visibilityInputs.Length == 0) return;
            skinPalette.ResizeUninitialized(paletteCount);
            using (BonesMarker.Auto())
            if (boneWork.Length > 0)
            {
                var mark = new MarkUsedBonesJob { Work = boneWork.AsArray(), Bindings = bindings.AsArray(),
                    BoneIndexMap = boneIndexMap.AsArray(), UsedFrames = usedFrames, Frame = frame }.Schedule();
                var capture = new CaptureBonesJob { UsedFrames = usedFrames, Frame = frame, Output = capturedBones }
                    .ScheduleReadOnly(boneTransforms, 64, mark);
                new SkinPaletteJob { Work = boneWork.AsArray(), Bindings = bindings.AsArray(),
                    BoneIndexMap = boneIndexMap.AsArray(), Captured = capturedBones, Output = skinPalette.AsArray() }
                    .Schedule(boneWork.Length, 8, capture).Complete();
            }
            using (UploadMarker.Auto())
            {
                if (skinBuffer == null || skinBuffer.count < paletteCount)
                {
                    skinBuffer?.Release();
                    skinBuffer = new ComputeBuffer(Mathf.NextPowerOfTwo(Mathf.Max(1, paletteCount)), 64, ComputeBufferType.Structured);
                }
                if (paletteCount > 0) skinBuffer.SetData(skinPalette.AsArray(), 0, 0, paletteCount);
                BonesUploadedLastFrame = paletteCount;
            }
            using (DrawMarker.Auto())
            foreach (var batch in batches.Values)
            {
                for (var page = batch; page != null; page = page.Next) Flush(page, camera);
                batch.Current = batch;
            }
        }

        private void CollectVisiblePart(Part part, int cullingMask, ref int paletteCount)
        {
            if (part.OriginalForceOff || (cullingMask & (1 << part.Layer)) == 0 ||
                part.Renderer == null || !part.Renderer.enabled || !part.Object.activeInHierarchy) return;
            var world = part.Transform.localToWorldMatrix;
            if (part.Impostor && part.UnitRoot != null)
            {
                var rootPosition = part.UnitRoot.position;
                var ground = rootPosition.y - part.GroundOffset;
                var heights = battle != null ? battle.PresentationHeightField : default;
                if (heights.TryHeight(new float2(rootPosition.x, rootPosition.z), out var sampledGround)) ground = sampledGround;
                // Keep the billboard's ground anchor at the unit's XZ position;
                // simulation poses include a navigation height offset above terrain.
                world.m03 = rootPosition.x;
                world.m13 = ground + part.FootHeight * MaxWorldScale(world);
                world.m23 = rootPosition.z;
            }
            var offset = part.Slots.Length > 0 ? paletteCount : -1;
            paletteCount += part.Slots.Length;
            if (part.Slots.Length > 0)
                boneWork.Add(new BoneWork { BindingStart = part.BindingStart, Count = part.Slots.Length,
                    OutputStart = offset, World = world });
            for (var submesh = 0; submesh < part.Materials.Length; submesh++) Submit(part, submesh, world, offset);
        }

        private static float MaxWorldScale(Matrix4x4 matrix)
        {
            // Mathf.Max(a,b,c) allocates a params array per unit. Keep every Max binary.
            var scaleSquared = Mathf.Max(
                matrix.m00 * matrix.m00 + matrix.m10 * matrix.m10 + matrix.m20 * matrix.m20,
                Mathf.Max(matrix.m01 * matrix.m01 + matrix.m11 * matrix.m11 + matrix.m21 * matrix.m21,
                    matrix.m02 * matrix.m02 + matrix.m12 * matrix.m12 + matrix.m22 * matrix.m22));
            return Mathf.Sqrt(scaleSquared);
        }

        private static Bounds TransformBounds(Bounds bounds, Matrix4x4 matrix)
        {
            var e = bounds.extents;
            var x = matrix.MultiplyVector(new Vector3(e.x, 0f, 0f));
            var y = matrix.MultiplyVector(new Vector3(0f, e.y, 0f));
            var z = matrix.MultiplyVector(new Vector3(0f, 0f, e.z));
            var extents = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
            return new Bounds(matrix.MultiplyPoint3x4(bounds.center), extents * 2f);
        }

        private void Submit(Part part, int submesh, Matrix4x4 world, int offset)
        {
            var root = part.Batches[submesh];
            var batch = root.Current ?? root;
            while (batch.Count == BatchSize)
            {
                if (batch.Next == null) batch.Next = CreateBatch(batch.Mesh, batch.Material, submesh, batch.Layer);
                batch = batch.Next;
            }
            root.Current = batch;
            batch.Offsets[batch.Count] = offset;
            batch.AnimationFrames[batch.Count] = part.Impostor && part.Presentation != null ? part.Presentation.GetImpostorFrame(4) : 0f;
            batch.Transforms[batch.Count] = world;
            batch.Count++;
        }

        private static Batch CreateBatch(Mesh mesh, Material material, int submesh, int layer) =>
            new Batch { Mesh = mesh, Material = material, Submesh = submesh, Layer = layer };

        private void Flush(Batch batch, Camera camera)
        {
            if (batch.Count == 0) return;
            batch.Properties.SetBuffer(BonesId, skinBuffer);
            batch.Properties.SetFloatArray(OffsetId, batch.Offsets);
            if (batch.Material.shader.name == "Recoil/Upper Hemisphere Impostor")
                batch.Properties.SetFloatArray(ImpostorFrameId, batch.AnimationFrames);
            Graphics.DrawMeshInstanced(batch.Mesh, batch.Submesh, batch.Material, batch.Transforms, batch.Count,
                batch.Properties, ShadowCastingMode.Off, true, batch.Layer, camera, LightProbeUsage.Off);
            DrawCalls++;
            DrawnInstances += batch.Count;
            batch.Count = 0;
        }

        public void SetRenderingEnabled(bool value)
        {
            value = value && !SuspendedForProfiling;
            // Non-ExecuteAlways components do not receive normal lifecycle callbacks in
            // Edit Mode. Explicit toggles must restore views there as well as in Play Mode.
            if (!value) ReleaseResources();
            enabled = value;
        }

#if UNITY_EDITOR
        public static void ValidateBoneJobs()
        {
            ValidateVisibilityJob();
            var owner = new GameObject("Temporary GPU bone job check");
            var a = new GameObject("Bone A");
            var b = new GameObject("Bone B");
            var renderer = owner.AddComponent<RecoilGpuSkinnedCrowdRenderer>();
            JobHandle handle = default;
            try
            {
                b.transform.SetPositionAndRotation(new Vector3(13f, 2f, -9f), Quaternion.Euler(10f, 30f, 20f));
                var first = renderer.RegisterBone(a.transform);
                var shared = renderer.RegisterBone(a.transform);
                var second = renderer.RegisterBone(b.transform);
                if (!ReferenceEquals(first, shared) || renderer.boneSlots.Count != 2)
                    throw new InvalidOperationException("Bone transforms must be captured once across shared LOD/submesh bindings.");
                renderer.EnsureJobStorage();
                renderer.capturedBones[first.Index] = float4x4.identity;
                var world = Matrix4x4.TRS(new Vector3(100f, 0f, 0f), Quaternion.identity, Vector3.one);
                var inverse = (float4x4)world.inverse;
                var bindingStart = renderer.AllocateBindings(new[] { second, second }, new[] { Matrix4x4.identity, Matrix4x4.identity });
                renderer.boneWork.Add(new BoneWork { BindingStart = bindingStart, Count = 2, OutputStart = 0, World = world });
                renderer.skinPalette.ResizeUninitialized(2);
                handle = new MarkUsedBonesJob { Work = renderer.boneWork.AsArray(), Bindings = renderer.bindings.AsArray(),
                    BoneIndexMap = renderer.boneIndexMap.AsArray(), UsedFrames = renderer.usedFrames, Frame = 17 }.Schedule();
                handle = new CaptureBonesJob { UsedFrames = renderer.usedFrames, Frame = 17, Output = renderer.capturedBones }
                    .ScheduleReadOnly(renderer.boneTransforms, 64, handle);
                handle = new SkinPaletteJob { Work = renderer.boneWork.AsArray(), Bindings = renderer.bindings.AsArray(),
                    BoneIndexMap = renderer.boneIndexMap.AsArray(), Captured = renderer.capturedBones,
                    Output = renderer.skinPalette.AsArray() }.Schedule(1, 64, handle);
                handle.Complete();
                var expected = math.mul(inverse, (float4x4)b.transform.localToWorldMatrix);
                if (math.cmax(math.abs(renderer.skinPalette[0].c3 - expected.c3)) > 0.0001f ||
                    math.cmax(math.abs(renderer.skinPalette[1].c3 - expected.c3)) > 0.0001f ||
                    math.cmax(math.abs(renderer.capturedBones[first.Index].c3 - float4x4.identity.c3)) > 0.0001f)
                    throw new InvalidOperationException("The bone jobs must build the correct palette and skip unused transforms.");
                renderer.UnregisterBone(first);
                renderer.UnregisterBone(shared);
                if (second.Index != 0 || renderer.boneSlots.Count != 1)
                    throw new InvalidOperationException("Removing pooled bones must remap surviving transform slots.");
                renderer.EnsureJobStorage();
                renderer.RegisterBone(a.transform); // Recycle A's stable ID without changing B's cached bindings.
                renderer.boneWork.Add(new BoneWork { BindingStart = bindingStart, Count = 2, OutputStart = 0, World = world });
                handle = new MarkUsedBonesJob { Work = renderer.boneWork.AsArray(), Bindings = renderer.bindings.AsArray(),
                    BoneIndexMap = renderer.boneIndexMap.AsArray(), UsedFrames = renderer.usedFrames, Frame = 18 }.Schedule();
                handle = new CaptureBonesJob { UsedFrames = renderer.usedFrames, Frame = 18, Output = renderer.capturedBones }
                    .ScheduleReadOnly(renderer.boneTransforms, 64, handle);
                handle = new SkinPaletteJob { Work = renderer.boneWork.AsArray(), Bindings = renderer.bindings.AsArray(),
                    BoneIndexMap = renderer.boneIndexMap.AsArray(), Captured = renderer.capturedBones,
                    Output = renderer.skinPalette.AsArray() }.Schedule(1, 64, handle);
                handle.Complete();
                if (math.cmax(math.abs(renderer.skinPalette[1].c3 - expected.c3)) > 0.0001f)
                    throw new InvalidOperationException("Bone capture must remain correct after swap removal.");
                renderer.FreeBindings(bindingStart, 2);
                if (renderer.AllocateBindings(new[] { second, second }, new[] { Matrix4x4.identity, Matrix4x4.identity }) != bindingStart)
                    throw new InvalidOperationException("Pooled binding ranges must be reused rather than grow every spawn.");
                renderer.EnsureJobStorage();
                var sparseBinding = renderer.AllocateBindings(new BoneSlot[] { second, null },
                    new[] { Matrix4x4.identity, Matrix4x4.identity });
                renderer.boneWork.Add(new BoneWork { BindingStart = sparseBinding, Count = 2, OutputStart = 0, World = world });
                handle = new MarkUsedBonesJob { Work = renderer.boneWork.AsArray(), Bindings = renderer.bindings.AsArray(),
                    BoneIndexMap = renderer.boneIndexMap.AsArray(), UsedFrames = renderer.usedFrames, Frame = 19 }.Schedule();
                handle = new CaptureBonesJob { UsedFrames = renderer.usedFrames, Frame = 19, Output = renderer.capturedBones }
                    .ScheduleReadOnly(renderer.boneTransforms, 64, handle);
                handle = new SkinPaletteJob { Work = renderer.boneWork.AsArray(), Bindings = renderer.bindings.AsArray(),
                    BoneIndexMap = renderer.boneIndexMap.AsArray(), Captured = renderer.capturedBones,
                    Output = renderer.skinPalette.AsArray() }.Schedule(1, 64, handle);
                handle.Complete();
                if (math.cmax(math.abs(renderer.skinPalette[0].c3 - expected.c3)) > 0.0001f ||
                    !math.all(renderer.skinPalette[1].c0 == float4x4.identity.c0) ||
                    !math.all(renderer.skinPalette[1].c3 == float4x4.identity.c3))
                    throw new InvalidOperationException("Unused mesh bones must not enter transform capture or corrupt the skin palette.");
                var scaled = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(10f, 20f, 30f), new Vector3(-2f, 3f, 1f));
                if (Mathf.Abs(MaxWorldScale(scaled) - 3f) > 0.0001f)
                    throw new InvalidOperationException("LOD world scale must support rotation and mirrored nonuniform scale.");
                for (var i = 0; i < 16; i++) MaxWorldScale(scaled);
                var allocated = GC.GetAllocatedBytesForCurrentThread();
                var scaleSum = 0f;
                for (var i = 0; i < 10000; i++) scaleSum += MaxWorldScale(scaled);
                if (GC.GetAllocatedBytesForCurrentThread() != allocated || scaleSum <= 0f)
                    throw new InvalidOperationException("Per-unit LOD scale calculation must not allocate params arrays.");
            }
            finally
            {
                handle.Complete();
                renderer.SetRenderingEnabled(false);
                DestroyImmediate(owner);
                DestroyImmediate(a);
                DestroyImmediate(b);
            }
        }
#endif

        #if UNITY_EDITOR
        private static void ValidateVisibilityJob()
        {
            using var ownedInputs = new NativeArray<VisibilityInput>(3, Allocator.TempJob);
            using var ownedThresholds = new NativeArray<float>(2, Allocator.TempJob);
            using var ownedPlanes = new NativeArray<float4>(1, Allocator.TempJob);
            using var results = new NativeArray<byte>(3, Allocator.TempJob);
            var inputs = ownedInputs;
            var thresholds = ownedThresholds;
            var testPlanes = ownedPlanes;
            thresholds[0] = 0.5f;
            thresholds[1] = 0.1f;
            testPlanes[0] = new float4(1f, 0f, 0f, 0f);
            var input = new VisibilityInput { Center = new float3(2f, 0f, 0f), Extents = new float3(0.5f),
                GroupCenter = new float3(2f, 0f, 0f), Size = 0.25f, LodCount = 2, LodMask = 2 };
            inputs[0] = input; // Visible at LOD 1.
            input.LodMask = 1;
            inputs[1] = input; // LOD 0 must be excluded.
            input.LodCount = 0;
            input.Center.x = -2f;
            inputs[2] = input; // Outside the frustum, without a LODGroup.
            new VisibilityJob { Inputs = inputs, Thresholds = thresholds, Planes = testPlanes,
                Results = results, Orthographic = true, ProjectionScale = 1f, LodBias = 1f }.Schedule(3, 1).Complete();
            if (results[0] != 2 || results[1] != 0 || results[2] != 0)
                throw new InvalidOperationException("Visibility jobs must preserve LOD selection and frustum rejection.");
        }
        #endif

        private void OnDisable() => ReleaseResources();
        private void OnDestroy() => ReleaseResources();

        private void ReleaseResources()
        {
            stale.Clear(); stale.AddRange(views.Keys);
            foreach (var unit in stale) Unregister(unit);
            foreach (var batch in batches.Values) batch.Dispose();
            batches.Clear();
            skinBuffer?.Release();
            skinBuffer = null;
            if (boneTransforms.isCreated) boneTransforms.Dispose();
            if (capturedBones.IsCreated) capturedBones.Dispose();
            if (usedFrames.IsCreated) usedFrames.Dispose();
            if (boneWork.IsCreated) boneWork.Dispose();
            if (bindings.IsCreated) bindings.Dispose();
            if (boneIndexMap.IsCreated) boneIndexMap.Dispose();
            freeBoneIds.Clear();
            freeBindingRanges.Clear();
            if (skinPalette.IsCreated) skinPalette.Dispose();
            if (visibilityInputs.IsCreated) visibilityInputs.Dispose();
            if (lodThresholds.IsCreated) lodThresholds.Dispose();
            if (frustumPlanes.IsCreated) frustumPlanes.Dispose();
            if (visibilityResults.IsCreated) visibilityResults.Dispose();
            visibilityParts.Clear();
            visibilityViews.Clear();
            boneLookup.Clear();
            boneSlots.Clear();
            nativeFallback = false;
            foreach (var material in materials.Values)
                if (material != null)
                {
                    if (Application.isPlaying) Destroy(material);
                    else DestroyImmediate(material);
                }
            materials.Clear();
            rejected.Clear();
            RegisteredRenderers = DrawnInstances = DrawCalls = FallbackRenderers = BonesUploadedLastFrame = 0;
        }
    }
}
