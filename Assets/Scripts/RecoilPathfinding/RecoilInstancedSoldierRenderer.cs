using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecoilPathfinding
{
    public sealed class RecoilInstancedSoldierRenderer : MonoBehaviour
    {
        private const int MaxInstancesPerDraw = 1023;
        private static readonly ProfilerMarker CullingMarker =
            new ProfilerMarker("RecoilBattle.Rendering.Culling");
        private static readonly ProfilerMarker DrawMarker =
            new ProfilerMarker("RecoilBattle.Rendering.Draw");

        [SerializeField] private GameObject blueUnitTemplate = null;
        [SerializeField] private GameObject redUnitTemplate = null;
        [SerializeField, Min(1f)] private float lowDetailDistance = 100f;
        [SerializeField, Min(1f)] private float maxRenderDistance = 300f;
#if SKGAMES_ANIMATED_IMPOSTOR
        [SerializeField] private bool gpuCullAnimatedImpostors = true;
        private RecoilGpuImpostorRenderer _gpuImpostors;
        public int GpuImpostorCount => _gpuImpostors != null ? _gpuImpostors.RegisteredCount : 0;
        public int GpuIndirectDrawCalls => _gpuImpostors != null ? _gpuImpostors.LastDrawCallCount : 0;
#else
        // Animated impostor metrics stay available while the optional plugin is absent.
        public int GpuImpostorCount => 0;
        public int GpuIndirectDrawCalls => 0;
#endif

        private readonly List<Transform> _blueUnits = new();
        private readonly List<Transform> _redUnits = new();
        private readonly List<Transform> _blueVisibleUnits = new();
        private readonly List<Transform> _redVisibleUnits = new();
        private readonly List<Transform> _blueLowDetailUnits = new();
        private readonly List<Transform> _redLowDetailUnits = new();
        private readonly List<RenderBatch> _blueBatches = new();
        private readonly List<RenderBatch> _redBatches = new();
        private RenderBatch _blueLowDetailBatch;
        private RenderBatch _redLowDetailBatch;
        private readonly Matrix4x4[] _matrices = new Matrix4x4[MaxInstancesPerDraw];
        private readonly Plane[] _frustumPlanes = new Plane[6];
        private Camera _renderCamera;
        private bool _initialized;

        public int InstancedSoldierCount => _blueUnits.Count + _redUnits.Count;
        public int LastDrawCallCount { get; private set; }
        public int LastVisibleSoldierCount { get; private set; }
        public int LastCulledSoldierCount { get; private set; }

        private sealed class RenderBatch
        {
            public Mesh Mesh;
            public Material Material;
            public Bounds LocalBounds;
            public bool OwnsMesh;
        }

        public void ConfigureTemplates(GameObject blue, GameObject red)
        {
            if (blueUnitTemplate == blue && redUnitTemplate == red) return;
            _initialized = false;
            DestroyBatches(_blueBatches);
            DestroyBatches(_redBatches);
            DestroyBatch(_blueLowDetailBatch);
            DestroyBatch(_redLowDetailBatch);
            _blueLowDetailBatch = null;
            _redLowDetailBatch = null;
            blueUnitTemplate = blue;
            redUnitTemplate = red;
        }

        public bool RegisterUnit(GameObject unit, bool isBlue)
        {
            if (unit == null)
                return false;

#if SKGAMES_ANIMATED_IMPOSTOR
            if (gpuCullAnimatedImpostors && unit.GetComponentInChildren<SKGames.AnimatedImpostor.AnimatedImpostorPlayer>(true) != null)
            {
                if (_gpuImpostors == null)
                    _gpuImpostors = GetComponent<RecoilGpuImpostorRenderer>();
                if (_gpuImpostors == null)
                    _gpuImpostors = gameObject.AddComponent<RecoilGpuImpostorRenderer>();
                if (_gpuImpostors.TryRegister(unit)) return true;
            }
#endif

            // This batcher draws static meshes with shared materials. Preserve skinned meshes and
            // per-unit property blocks on their own renderers.
            if (unit.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
                return false;
            var renderers = unit.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
                if (renderers[i].HasPropertyBlock()) return false;

            if (!_initialized && !Initialize())
                return false;

            for (var i = 0; i < renderers.Length; i++)
                renderers[i].enabled = false;

            (isBlue ? _blueUnits : _redUnits).Add(unit.transform);
            return true;
        }

        private bool Initialize()
        {
            if (!SystemInfo.supportsInstancing)
            {
                Debug.LogWarning("GPU instancing is not supported by this graphics device; using regular soldier renderers.", this);
                return false;
            }

            if (blueUnitTemplate == null || redUnitTemplate == null)
            {
                Debug.LogError($"{nameof(RecoilInstancedSoldierRenderer)} requires both team templates.", this);
                return false;
            }

            BuildBatches(blueUnitTemplate, _blueBatches);
            BuildBatches(redUnitTemplate, _redBatches);
            _blueLowDetailBatch = BuildLowDetailBatch(blueUnitTemplate);
            _redLowDetailBatch = BuildLowDetailBatch(redUnitTemplate);
            if (_blueBatches.Count == 0 || _redBatches.Count == 0 ||
                _blueLowDetailBatch == null || _redLowDetailBatch == null)
            {
                DestroyBatches(_blueBatches);
                DestroyBatches(_redBatches);
                DestroyBatch(_blueLowDetailBatch);
                DestroyBatch(_redLowDetailBatch);
                _blueLowDetailBatch = null;
                _redLowDetailBatch = null;
                Debug.LogError("Could not create instanced meshes from the soldier templates.", this);
                return false;
            }

            _initialized = true;
            return true;
        }

        private static RenderBatch BuildLowDetailBatch(GameObject template)
        {
            var meshFilter = template.GetComponent<MeshFilter>();
            var meshRenderer = template.GetComponent<MeshRenderer>();
            if (meshFilter == null || meshFilter.sharedMesh == null ||
                meshRenderer == null || meshRenderer.sharedMaterial == null)
                return null;

            var lowDetailMesh = Instantiate(meshFilter.sharedMesh);
            lowDetailMesh.name = $"{template.name} Low Detail Instanced Mesh";
            meshRenderer.sharedMaterial.enableInstancing = true;
            return new RenderBatch
            {
                Mesh = lowDetailMesh,
                Material = meshRenderer.sharedMaterial,
                LocalBounds = lowDetailMesh.bounds,
                OwnsMesh = true
            };
        }

        private static void BuildBatches(GameObject template, List<RenderBatch> batches)
        {
            var root = template.transform;
            var meshFilters = template.GetComponentsInChildren<MeshFilter>(true);
            var combinesByMaterial = new Dictionary<Material, List<CombineInstance>>();

            for (var i = 0; i < meshFilters.Length; i++)
            {
                var filter = meshFilters[i];
                var renderer = filter.GetComponent<MeshRenderer>();
                var sourceMesh = filter.sharedMesh;
                if (renderer == null || sourceMesh == null || renderer.sharedMaterial == null)
                    continue;

                if (!combinesByMaterial.TryGetValue(renderer.sharedMaterial, out var combines))
                {
                    combines = new List<CombineInstance>();
                    combinesByMaterial.Add(renderer.sharedMaterial, combines);
                }

                for (var subMesh = 0; subMesh < sourceMesh.subMeshCount; subMesh++)
                {
                    combines.Add(new CombineInstance
                    {
                        mesh = sourceMesh,
                        subMeshIndex = subMesh,
                        transform = root.worldToLocalMatrix * filter.transform.localToWorldMatrix
                    });
                }
            }

            foreach (var entry in combinesByMaterial)
            {
                var combinedMesh = new Mesh
                {
                    name = $"{template.name} {entry.Key.name} Instanced Mesh",
                    indexFormat = IndexFormat.UInt32
                };
                combinedMesh.CombineMeshes(entry.Value.ToArray(), true, true);
                entry.Key.enableInstancing = true;
                batches.Add(new RenderBatch
                {
                    Mesh = combinedMesh,
                    Material = entry.Key,
                    LocalBounds = combinedMesh.bounds,
                    OwnsMesh = true
                });
            }
        }

        private void LateUpdate()
        {
            if (!_initialized)
                return;

            LastDrawCallCount = 0;
            LastVisibleSoldierCount = 0;
            LastCulledSoldierCount = 0;
            if (_renderCamera == null)
                _renderCamera = Camera.main;

            var useCulling = _renderCamera != null;
            if (useCulling)
                GeometryUtility.CalculateFrustumPlanes(_renderCamera, _frustumPlanes);

            using (CullingMarker.Auto())
            {
                CollectVisibleUnits(
                    _blueUnits,
                    _blueVisibleUnits,
                    _blueLowDetailUnits,
                    _blueBatches[0],
                    _blueLowDetailBatch,
                    useCulling);
                CollectVisibleUnits(
                    _redUnits,
                    _redVisibleUnits,
                    _redLowDetailUnits,
                    _redBatches[0],
                    _redLowDetailBatch,
                    useCulling);
            }

            using (DrawMarker.Auto())
            {
                DrawVisibleUnits(_blueVisibleUnits, _blueBatches);
                DrawVisibleUnits(_redVisibleUnits, _redBatches);
                if (useCulling)
                {
                    DrawVisibleUnits(_blueLowDetailUnits, _blueLowDetailBatch);
                    DrawVisibleUnits(_redLowDetailUnits, _redLowDetailBatch);
                }
            }
        }

        private void CollectVisibleUnits(
            List<Transform> units,
            List<Transform> visibleUnits,
            List<Transform> lowDetailUnits,
            RenderBatch fullDetailBatch,
            RenderBatch lowDetailBatch,
            bool useCulling)
        {
            RemoveDestroyedUnits(units);
            visibleUnits.Clear();
            lowDetailUnits.Clear();
            var cameraPosition = useCulling ? _renderCamera.transform.position : Vector3.zero;
            var lowDetailDistanceSquared = lowDetailDistance * lowDetailDistance;
            for (var i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (!unit.gameObject.activeInHierarchy)
                    continue;

                var isFar = useCulling &&
                            (unit.position - cameraPosition).sqrMagnitude >= lowDetailDistanceSquared;
                var renderBatch = isFar ? lowDetailBatch : fullDetailBatch;
                if (useCulling && !IsVisible(unit, renderBatch, cameraPosition))
                {
                    LastCulledSoldierCount++;
                    continue;
                }

                LastVisibleSoldierCount++;
                (isFar ? lowDetailUnits : visibleUnits).Add(unit);
            }
        }

        private void DrawVisibleUnits(List<Transform> units, List<RenderBatch> batches)
        {
            for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
                DrawVisibleUnits(units, batches[batchIndex]);
        }

        private void DrawVisibleUnits(List<Transform> units, RenderBatch batch)
        {
            var unitIndex = 0;
            while (unitIndex < units.Count)
            {
                var instanceCount = 0;
                while (instanceCount < MaxInstancesPerDraw && unitIndex < units.Count)
                    _matrices[instanceCount++] = units[unitIndex++].localToWorldMatrix;

                DrawBatch(batch, instanceCount);
            }
        }

        private bool IsVisible(Transform unit, RenderBatch batch, Vector3 cameraPosition)
        {
            var localBounds = batch.LocalBounds;
            var worldCenter = unit.TransformPoint(localBounds.center);
            var scale = unit.lossyScale;
            var scaleMagnitude = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            var radius = localBounds.extents.magnitude * scaleMagnitude;
            if ((worldCenter - cameraPosition).sqrMagnitude >
                (maxRenderDistance + radius) * (maxRenderDistance + radius))
                return false;

            for (var i = 0; i < _frustumPlanes.Length; i++)
            {
                if (_frustumPlanes[i].GetDistanceToPoint(worldCenter) < -radius)
                    return false;
            }

            return true;
        }

        private void DrawBatch(RenderBatch batch, int instanceCount = MaxInstancesPerDraw)
        {
            Graphics.DrawMeshInstanced(
                batch.Mesh,
                0,
                batch.Material,
                _matrices,
                instanceCount,
                null,
                ShadowCastingMode.Off,
                false,
                gameObject.layer);
            LastDrawCallCount++;
        }

        private static void RemoveDestroyedUnits(List<Transform> units)
        {
            for (var i = units.Count - 1; i >= 0; i--)
            {
                if (units[i] == null)
                    units.RemoveAt(i);
            }
        }

        private void OnDestroy()
        {
            DestroyBatches(_blueBatches);
            DestroyBatches(_redBatches);
            DestroyBatch(_blueLowDetailBatch);
            DestroyBatch(_redLowDetailBatch);
        }

        private static void DestroyBatches(List<RenderBatch> batches)
        {
            for (var i = 0; i < batches.Count; i++)
            {
                if (batches[i].OwnsMesh && batches[i].Mesh != null)
                    Destroy(batches[i].Mesh);
            }

            batches.Clear();
        }

        private static void DestroyBatch(RenderBatch batch)
        {
            if (batch != null && batch.OwnsMesh && batch.Mesh != null)
                Destroy(batch.Mesh);
        }
    }
}
