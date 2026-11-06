using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.Rendering;

namespace RecoilPathfinding
{
    [ExecuteAlways]
    public sealed class RecoilForestInstancedRenderer : MonoBehaviour
    {
        public RecoilForestInstanceData data;
        [SerializeField] private bool useIndirect;
        [SerializeField] private ComputeShader indirectCulling;
        [SerializeField] private RecoilForestIndirectShader[] indirectShaders = Array.Empty<RecoilForestIndirectShader>();
        private RecoilForestIndirectBackend indirectBackend;
        private bool indirectDirty = true, indirectFailed;
        [SerializeField] private bool logToConsole = true;
        [SerializeField, Min(0.5f)] private float logIntervalSeconds = 2f;
        [SerializeField] private bool logSceneCamera;
        private double nextGameLog, nextSceneLog;

        public struct ProfileSample
        {
            public int Frame, TotalChunks, FrustumRejected, LodRejected, VisibleChunks, Lod0Chunks, OtherLodChunks;
            public int SourceBatches, DrawCalls, Instances, MatrixCopies;
            public long Triangles, CopiedBytes;
            public bool Rebuilt;
            public bool Playing;
            public double Timestamp;
            public bool Indirect, GpuCountsAvailable;
            public float GpuCountAge;
            public float TotalMs, RebuildMs, PrepareMs, FrustumMs, LodMs, GatherMs, CopyMs, SubmitMs;
        }

        public ProfileSample LastGameSample { get; private set; }
        public ProfileSample LastSceneSample { get; private set; }
        public Camera LastGameCamera { get; private set; }
        public Camera LastSceneCamera { get; private set; }
        public float LastRebuildMilliseconds { get; private set; }
        public int RebuildCount { get; private set; }

        private RecoilForestInstanceData cachedData;
        private Matrix4x4 cachedTransform;
        private readonly Plane[] planes = new Plane[6];
        private Bounds[] bounds;
        private Matrix4x4[][][] matrices;
        private Bucket[][] chunkBuckets;
        private int[] visibleIndices, selectedLods;
        private int frustumCount;
        private Vector3 cameraPosition;
        private float sizeScale, perspective, orthoDenominator, lodBias;
        private bool orthographic;
        private readonly List<Bucket> drawBuckets = new();

        private static readonly RecoilCpuPhases.Marker GameMarker = new("RecoilForest.GameCamera");
        private static readonly RecoilCpuPhases.Marker SceneMarker = new("RecoilForest.SceneCamera");
        private static readonly RecoilCpuPhases.Marker OtherMarker = new("RecoilForest.OtherCamera");
        private static readonly RecoilCpuPhases.Marker RebuildMarker = new("RecoilForest.RebuildCache");
        private static readonly RecoilCpuPhases.Marker PrepareMarker = new("RecoilForest.PrepareCamera");
        private static readonly RecoilCpuPhases.Marker CollectMarker = new("RecoilForest.CullAndBatch");
        private static readonly RecoilCpuPhases.Marker FrustumMarker = new("RecoilForest.FrustumCulling");
        private static readonly RecoilCpuPhases.Marker LodMarker = new("RecoilForest.SelectLOD");
        private static readonly RecoilCpuPhases.Marker GatherMarker = new("RecoilForest.GatherBatches");
        private static readonly RecoilCpuPhases.Marker CopyMarker = new("RecoilForest.CopyMatrices");
        private static readonly RecoilCpuPhases.Marker SubmitMarker = new("RecoilForest.SubmitInstanced");
        private static readonly RecoilCpuPhases.Marker BuildMarker = new("RecoilForest.Rebuild.TransformAndGroup");
        private static readonly RecoilCpuPhases.Marker SortMarker = new("RecoilForest.Rebuild.SortBuckets");
        private static readonly RecoilCpuPhases.Marker BoundsMarker = new("RecoilForest.TransformBounds");
        private static readonly RecoilCpuPhases.Marker IndirectMarker = new("RecoilForest.IndirectDispatchAndDraw");

        private sealed class Packet
        {
            public readonly Matrix4x4[] Matrices = new Matrix4x4[511];
            public int Count;
        }

        private sealed class Bucket
        {
            public RecoilForestInstanceData.Batch Template;
            public readonly List<Matrix4x4[]> Sources = new();
            public readonly List<Packet> Packets = new();
            public int ActivePackets;
            public long TrianglesPerInstance;
            public Bounds Bounds;
        }

        private void OnEnable()
        {
            cachedData = null;
            indirectDirty = true; indirectFailed = false;
            nextGameLog = nextSceneLog = 0;
            RenderPipelineManager.beginCameraRendering += BeginCamera;
            Camera.onPreCull += BuiltinCamera;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= BeginCamera;
            Camera.onPreCull -= BuiltinCamera;
            bounds = null; matrices = null; cachedData = null;
            chunkBuckets = null; visibleIndices = selectedLods = null; drawBuckets.Clear();
            LastGameSample = LastSceneSample = default;
            LastGameCamera = LastSceneCamera = null;
            LastRebuildMilliseconds = 0f; RebuildCount = 0;
            indirectBackend?.Dispose(); indirectBackend = null;
        }

        private void OnValidate() { indirectDirty = true; indirectFailed = false; }

        public void ConfigureIndirect(ComputeShader culling, RecoilForestIndirectShader[] bindings)
        {
            indirectCulling = culling; indirectShaders = bindings; useIndirect = true;
            indirectDirty = true; indirectFailed = false;
        }

        private void BeginCamera(ScriptableRenderContext context, Camera camera) => Draw(camera);

        private void BuiltinCamera(Camera camera)
        {
            if (GraphicsSettings.currentRenderPipeline == null) Draw(camera);
        }

        private static float Milliseconds(long start) =>
            (float)((RecoilCpuPhases.Timestamp() - start) * (1000.0 / Stopwatch.Frequency));

        private void Draw(Camera camera)
        {
            if (!isActiveAndEnabled || data == null || camera.cameraType == CameraType.Preview || !SystemInfo.supportsInstancing) return;
            var total = RecoilCpuPhases.Timestamp();
            var sample = new ProfileSample { Frame = Time.frameCount, TotalChunks = data.chunks.Length,
                Playing = Application.isPlaying, Timestamp = Time.realtimeSinceStartupAsDouble };
            var marker = camera.cameraType == CameraType.Game ? GameMarker
                : camera.cameraType == CameraType.SceneView ? SceneMarker : OtherMarker;
            using (marker.Auto())
            {
                var root = transform.localToWorldMatrix;
                if (useIndirect && TryDrawIndirect(camera, root, ref sample)) { }
                else
                {
                if (!useIndirect && indirectBackend != null) { indirectBackend.Dispose(); indirectBackend = null; }
                if (cachedData != data || root != cachedTransform)
                {
                    var start = RecoilCpuPhases.Timestamp();
                    using (RebuildMarker.Auto()) Rebuild(root);
                    sample.RebuildMs = Milliseconds(start);
                    sample.Rebuilt = true;
                    LastRebuildMilliseconds = sample.RebuildMs; RebuildCount++;
                }

                var phase = RecoilCpuPhases.Timestamp();
                using (PrepareMarker.Auto()) PrepareCamera(camera);
                sample.PrepareMs = Milliseconds(phase);

                using (CollectMarker.Auto())
                {
                    phase = RecoilCpuPhases.Timestamp();
                    using (FrustumMarker.Auto()) CullChunks(ref sample);
                    sample.FrustumMs = Milliseconds(phase);

                    phase = RecoilCpuPhases.Timestamp();
                    using (LodMarker.Auto()) SelectLods(ref sample);
                    sample.LodMs = Milliseconds(phase);

                    phase = RecoilCpuPhases.Timestamp();
                    using (GatherMarker.Auto()) GatherBatches(camera, ref sample);
                    sample.GatherMs = Milliseconds(phase);
                }

                phase = RecoilCpuPhases.Timestamp();
                using (CopyMarker.Auto()) PackMatrices(ref sample);
                sample.CopyMs = Milliseconds(phase);

                phase = RecoilCpuPhases.Timestamp();
                using (SubmitMarker.Auto()) Submit(camera, ref sample);
                sample.SubmitMs = Milliseconds(phase);
                }
            }
            sample.TotalMs = Milliseconds(total);
            if (camera.cameraType == CameraType.Game)
            {
                LastGameSample = sample; LastGameCamera = camera;
                if (logToConsole && sample.Playing && sample.Timestamp >= nextGameLog)
                {
                    nextGameLog = sample.Timestamp + Mathf.Max(0.5f, logIntervalSeconds);
                    WriteProfile(camera, sample);
                }
            }
            else if (camera.cameraType == CameraType.SceneView)
            {
                LastSceneSample = sample; LastSceneCamera = camera;
                if (logToConsole && logSceneCamera && sample.Timestamp >= nextSceneLog)
                {
                    nextSceneLog = sample.Timestamp + Mathf.Max(0.5f, logIntervalSeconds);
                    WriteProfile(camera, sample);
                }
            }
        }

        private bool TryDrawIndirect(Camera camera, Matrix4x4 root, ref ProfileSample sample)
        {
            if (indirectFailed) return false;
            try
            {
                if (!SystemInfo.supportsComputeShaders || indirectCulling == null || indirectShaders.Length == 0)
                    throw new InvalidOperationException("Chưa chuẩn bị shader/compute cho indirect hoặc GPU không hỗ trợ compute shaders.");
                if (indirectDirty || indirectBackend == null || indirectBackend.Data != data || indirectBackend.Root != root)
                {
                    var start = RecoilCpuPhases.Timestamp();
                    using (RebuildMarker.Auto())
                    {
                        indirectBackend?.Dispose(); indirectBackend = null;
                        indirectBackend = new RecoilForestIndirectBackend(data, root, indirectCulling, indirectShaders);
                    }
                    indirectDirty = false;
                    sample.RebuildMs = Milliseconds(start); sample.Rebuilt = true;
                    LastRebuildMilliseconds = sample.RebuildMs; RebuildCount++;
                }
                var submitStart = RecoilCpuPhases.Timestamp();
                using (IndirectMarker.Auto()) indirectBackend.Draw(camera, ref sample);
                sample.SubmitMs = Milliseconds(submitStart);
                return true;
            }
            catch (Exception e)
            {
                indirectFailed = true; indirectBackend?.Dispose(); indirectBackend = null;
                UnityEngine.Debug.LogWarning("[RecoilForest] Indirect chưa sẵn sàng, dùng renderer instancing hiện tại: " + e.Message, this);
                return false;
            }
        }

        [ContextMenu("Log forest profile now")]
        private void LogProfileNow()
        {
            if (LastGameCamera != null) WriteProfile(LastGameCamera, LastGameSample);
            else if (LastSceneCamera != null) WriteProfile(LastSceneCamera, LastSceneSample);
        }

        private void WriteProfile(Camera camera, ProfileSample s)
        {
            var hottest = "RebuildCache";
            var max = s.RebuildMs;
            void Check(string name, float value) { if (value > max) { hottest = name; max = value; } }
            Check("PrepareCamera", s.PrepareMs); Check("FrustumCulling", s.FrustumMs);
            Check("SelectLOD", s.LodMs); Check("GatherBatches", s.GatherMs);
            Check("CopyMatrices", s.CopyMs); Check("SubmitInstanced", s.SubmitMs);
            UnityEngine.Debug.Log(
                $"[RecoilForest CPU] {camera.name} ({camera.cameraType}) | {(s.Playing ? "PLAY" : "EDIT")} | {(s.Indirect ? "INDIRECT / GPU culling" : "INSTANCED / CPU culling")} | frame {s.Frame}\n" +
                $"Total {s.TotalMs:F3} ms | HOTTEST {hottest}: {max:F3} ms\n" +
                $"RebuildCache {s.RebuildMs:F3} | PrepareCamera {s.PrepareMs:F3} | FrustumCulling {s.FrustumMs:F3} | " +
                $"SelectLOD {s.LodMs:F3} | GatherBatches {s.GatherMs:F3} | CopyMatrices {s.CopyMs:F3} | SubmitInstanced {s.SubmitMs:F3} ms\n" +
                $"Chunks total/visible/frustum rejected/LOD rejected: {s.TotalChunks}/{s.VisibleChunks}/{s.FrustumRejected}/{s.LodRejected} | " +
                $"LOD0/lower: {s.Lod0Chunks}/{s.OtherLodChunks}\n" +
                $"Source batches/draw calls: {s.SourceBatches}/{s.DrawCalls} | Submesh instances: {s.Instances:N0} | Triangles submitted: {s.Triangles:N0}\n" +
                $"Matrix copies: {s.MatrixCopies:N0} / {s.CopiedBytes / (1024f * 1024f):F2} MB | " +
                $"Cache rebuilds: {RebuildCount}, last {LastRebuildMilliseconds:F3} ms. GPU time is not measured here." +
                (s.Indirect ? $" GPU counters: {(s.GpuCountsAvailable ? "async, age " + s.GpuCountAge.ToString("F1") + "s" : "pending")}; Source batches = potential batches before GPU culling." : ""), this);
        }

        private void PrepareCamera(Camera camera)
        {
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
            var scale = transform.lossyScale;
            sizeScale = Mathf.Max(Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)), Mathf.Abs(scale.z));
            cameraPosition = camera.transform.position;
            perspective = 2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            orthoDenominator = camera.orthographicSize * 2f;
            orthographic = camera.orthographic;
            lodBias = QualitySettings.lodBias;
            frustumCount = 0;
            foreach (var bucket in drawBuckets)
            {
                bucket.Sources.Clear();
                bucket.ActivePackets = 0;
            }
        }

        private void CullChunks(ref ProfileSample sample)
        {
            for (var i = 0; i < data.chunks.Length; i++)
                if (GeometryUtility.TestPlanesAABB(planes, bounds[i])) visibleIndices[frustumCount++] = i;
            sample.FrustumRejected = data.chunks.Length - frustumCount;
        }

        private void SelectLods(ref ProfileSample sample)
        {
            for (var n = 0; n < frustumCount; n++)
            {
                var i = visibleIndices[n];
                var chunk = data.chunks[i];
                var denominator = orthographic ? orthoDenominator
                    : Mathf.Max(0.01f, Vector3.Distance(cameraPosition, bounds[i].center)) * perspective;
                var relativeSize = chunk.referenceSize * sizeScale * lodBias / Mathf.Max(0.001f, denominator);
                var lod = -1;
                for (var level = 0; level < chunk.thresholds.Length; level++)
                    if (relativeSize >= chunk.thresholds[level]) { lod = level; break; }
                selectedLods[i] = lod;
                if (lod < 0) sample.LodRejected++;
                else
                {
                    sample.VisibleChunks++;
                    if (lod == 0) sample.Lod0Chunks++;
                    else sample.OtherLodChunks++;
                }
            }
        }

        private void GatherBatches(Camera camera, ref ProfileSample sample)
        {
            var mask = camera.cullingMask;
            for (var n = 0; n < frustumCount; n++)
            {
                var i = visibleIndices[n];
                var lod = selectedLods[i];
                if (lod < 0) continue;
                var chunk = data.chunks[i];
                for (var j = 0; j < chunk.batches.Length; j++)
                {
                    var batch = chunk.batches[j];
                    if (batch.lod != lod || batch.mesh == null || batch.material == null ||
                        !batch.material.enableInstancing || (mask & (1 << batch.layer)) == 0) continue;
                    var bucket = chunkBuckets[i][j];
                    if (bucket.Sources.Count == 0) bucket.Bounds = bounds[i];
                    else bucket.Bounds.Encapsulate(bounds[i]);
                    bucket.Sources.Add(matrices[i][j]);
                    sample.SourceBatches++;
                }
            }
        }

        private void PackMatrices(ref ProfileSample sample)
        {
            foreach (var bucket in drawBuckets)
            {
                Packet packet = null;
                foreach (var source in bucket.Sources)
                {
                    var offset = 0;
                    while (offset < source.Length)
                    {
                        if (packet == null || packet.Count == packet.Matrices.Length)
                        {
                            if (bucket.ActivePackets == bucket.Packets.Count) bucket.Packets.Add(new Packet());
                            packet = bucket.Packets[bucket.ActivePackets++];
                            packet.Count = 0;
                        }
                        var take = Mathf.Min(packet.Matrices.Length - packet.Count, source.Length - offset);
                        Array.Copy(source, offset, packet.Matrices, packet.Count, take);
                        offset += take; packet.Count += take;
                        sample.MatrixCopies++;
                        sample.CopiedBytes += (long)take * 64;
                    }
                }
            }
        }

        private void Submit(Camera camera, ref ProfileSample sample)
        {
            foreach (var bucket in drawBuckets)
            {
                if (bucket.ActivePackets == 0) continue;
                var batch = bucket.Template;
                var parameters = new RenderParams(batch.material)
                {
                    camera = camera, worldBounds = bucket.Bounds, layer = batch.layer,
                    renderingLayerMask = batch.renderingLayer, shadowCastingMode = batch.shadows,
                    receiveShadows = batch.receiveShadows, lightProbeUsage = LightProbeUsage.Off
                };
                for (var i = 0; i < bucket.ActivePackets; i++)
                {
                    var packet = bucket.Packets[i];
                    Graphics.RenderMeshInstanced(parameters, batch.mesh, batch.subMesh, packet.Matrices, packet.Count);
                    sample.DrawCalls++;
                    sample.Instances += packet.Count;
                    sample.Triangles += bucket.TrianglesPerInstance * packet.Count;
                }
            }
        }

        private void Rebuild(Matrix4x4 root)
        {
            bounds = new Bounds[data.chunks.Length];
            matrices = new Matrix4x4[data.chunks.Length][][];
            chunkBuckets = new Bucket[data.chunks.Length][];
            visibleIndices = new int[data.chunks.Length];
            selectedLods = new int[data.chunks.Length];
            drawBuckets.Clear();
            var buckets = new Dictionary<(Mesh, Material, int, int, uint, ShadowCastingMode, bool), Bucket>();
            using (BuildMarker.Auto())
            for (var i = 0; i < data.chunks.Length; i++)
            {
                var chunk = data.chunks[i];
                bounds[i] = TransformBounds(root, chunk.localBounds);
                matrices[i] = new Matrix4x4[chunk.batches.Length][];
                chunkBuckets[i] = new Bucket[chunk.batches.Length];
                for (var j = 0; j < chunk.batches.Length; j++)
                {
                    var batch = chunk.batches[j];
                    var local = batch.matrices;
                    var world = new Matrix4x4[local.Length];
                    for (var k = 0; k < local.Length; k++) world[k] = root * local[k];
                    matrices[i][j] = world;
                    var key = (batch.mesh, batch.material, batch.subMesh, batch.layer, batch.renderingLayer,
                        batch.shadows, batch.receiveShadows);
                    if (!buckets.TryGetValue(key, out var bucket))
                    {
                        bucket = new Bucket { Template = batch,
                            TrianglesPerInstance = batch.mesh != null ? (long)batch.mesh.GetIndexCount(batch.subMesh) / 3 : 0 };
                        buckets.Add(key, bucket); drawBuckets.Add(bucket);
                    }
                    chunkBuckets[i][j] = bucket;
                }
            }
            using (SortMarker.Auto())
            drawBuckets.Sort((a, b) =>
            {
                var materialA = a.Template.material == null ? 0 : a.Template.material.GetInstanceID();
                var materialB = b.Template.material == null ? 0 : b.Template.material.GetInstanceID();
                var compare = materialA.CompareTo(materialB);
                if (compare != 0) return compare;
                var meshA = a.Template.mesh == null ? 0 : a.Template.mesh.GetInstanceID();
                var meshB = b.Template.mesh == null ? 0 : b.Template.mesh.GetInstanceID();
                compare = meshA.CompareTo(meshB);
                return compare != 0 ? compare : a.Template.subMesh.CompareTo(b.Template.subMesh);
            });
            cachedData = data; cachedTransform = root;
        }

        public static Bounds TransformBounds(Matrix4x4 matrix, Bounds local)
        {
            using (BoundsMarker.Auto())
            {
                var x = matrix.MultiplyVector(new Vector3(local.extents.x, 0f, 0f));
                var y = matrix.MultiplyVector(new Vector3(0f, local.extents.y, 0f));
                var z = matrix.MultiplyVector(new Vector3(0f, 0f, local.extents.z));
                return new Bounds(matrix.MultiplyPoint3x4(local.center), new Vector3(
                    Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x),
                    Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y),
                    Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z)) * 2f);
            }
        }
    }
}
