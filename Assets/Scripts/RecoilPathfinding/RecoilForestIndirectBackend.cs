using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecoilPathfinding
{
    [Serializable]
    public struct RecoilForestIndirectShader
    {
        public Shader source, indirect;
    }

    internal sealed class RecoilForestIndirectBackend : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ChunkInfo
        {
            public Vector4 Center, Extents;
            public float Size;
            public int Offset, Count, Padding;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct InstanceInfo { public int Chunk, Lod; }

        private sealed class Group
        {
            public RecoilForestInstanceData.Batch Batch;
            public Material Material;
            public readonly List<Matrix4x4> Matrices = new();
            public readonly List<InstanceInfo> Metadata = new();
            public ComputeBuffer ObjectToWorld, WorldToObject, Info, Visible, Args;
            public MaterialPropertyBlock Properties;
        }

        private readonly List<Group> groups = new();
        private readonly Plane[] planes = new Plane[6];
        private readonly Vector4[] planeVectors = new Vector4[6];
        private ComputeShader compute;
        private ComputeBuffer chunks, thresholds, selected, counts;
        private int chunkKernel, instanceKernel, chunkCount;
        private Bounds worldBounds;
        private bool disposed, readbackPending;
        private double nextReadback, lastReadback;
        private int visibleChunks, rejected, lodRejected, lod0, lowerLod, instances;
        private long triangles;
        public readonly RecoilForestInstanceData Data;
        public readonly Matrix4x4 Root;
        public int SourceBatches { get; private set; }

        public RecoilForestIndirectBackend(RecoilForestInstanceData data, Matrix4x4 root,
            ComputeShader sourceCompute, RecoilForestIndirectShader[] shaderBindings)
        {
            Data = data; Root = root;
            try { Build(data, root, sourceCompute, shaderBindings); }
            catch { Dispose(); throw; }
        }

        private void Build(RecoilForestInstanceData data, Matrix4x4 root, ComputeShader sourceCompute,
            RecoilForestIndirectShader[] shaderBindings)
        {
            compute = UnityEngine.Object.Instantiate(sourceCompute);
            compute.hideFlags = HideFlags.HideAndDontSave;
            chunkKernel = compute.FindKernel("SelectChunks"); instanceKernel = compute.FindKernel("SelectInstances");
            var shaderMap = new Dictionary<Shader, Shader>();
            foreach (var binding in shaderBindings)
                if (binding.source != null && binding.indirect != null) shaderMap[binding.source] = binding.indirect;
            var lookup = new Dictionary<(Mesh, Material, int, int, uint, ShadowCastingMode, bool), Group>();
            var chunkData = new ChunkInfo[data.chunks.Length];
            var lodThresholds = new List<float>();
            var scale = root.lossyScale;
            var sizeScale = Mathf.Max(Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)), Mathf.Abs(scale.z));
            for (var i = 0; i < data.chunks.Length; i++)
            {
                var chunk = data.chunks[i];
                var bounds = RecoilForestInstancedRenderer.TransformBounds(root, chunk.localBounds);
                if (i == 0) worldBounds = bounds; else worldBounds.Encapsulate(bounds);
                chunkData[i] = new ChunkInfo { Center = bounds.center, Extents = bounds.extents,
                    Size = chunk.referenceSize * sizeScale, Offset = lodThresholds.Count, Count = chunk.thresholds.Length };
                lodThresholds.AddRange(chunk.thresholds);
                foreach (var batch in chunk.batches)
                {
                    if (batch.mesh == null || batch.material == null || batch.matrices.Length == 0) continue;
                    SourceBatches++;
                    var key = (batch.mesh, batch.material, batch.subMesh, batch.layer, batch.renderingLayer, batch.shadows, batch.receiveShadows);
                    if (!lookup.TryGetValue(key, out var group))
                    {
                        if (!shaderMap.TryGetValue(batch.material.shader, out var shader) || !shader.isSupported)
                            throw new InvalidOperationException("Chưa có shader indirect hợp lệ cho " + batch.material.shader.name);
                        group = new Group { Batch = batch, Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave } };
                        groups.Add(group); lookup.Add(key, group);
                        group.Material.CopyPropertiesFromMaterial(batch.material);
                        // Include importer-provided default textures and graph properties, not only serialized overrides.
                        CopyProperties(batch.material, group.Material);
                        group.Material.enableInstancing = true;
                    }
                    foreach (var matrix in batch.matrices)
                    {
                        group.Matrices.Add(root * matrix);
                        group.Metadata.Add(new InstanceInfo { Chunk = i, Lod = batch.lod });
                    }
                }
            }
            if (groups.Count == 0 || chunkData.Length == 0) throw new InvalidOperationException("Không có instance để render indirect.");
            chunkCount = chunkData.Length;
            chunks = new ComputeBuffer(chunkCount, Marshal.SizeOf<ChunkInfo>()); chunks.SetData(chunkData);
            thresholds = new ComputeBuffer(Mathf.Max(1, lodThresholds.Count), 4);
            if (lodThresholds.Count > 0) thresholds.SetData(lodThresholds);
            selected = new ComputeBuffer(chunkCount, 4);
            counts = new ComputeBuffer(groups.Count, 4, ComputeBufferType.Raw);
            foreach (var group in groups)
            {
                var count = group.Matrices.Count;
                group.ObjectToWorld = new ComputeBuffer(count, 64); group.ObjectToWorld.SetData(group.Matrices);
                var inverse = new Matrix4x4[count];
                for (var i = 0; i < count; i++) inverse[i] = group.Matrices[i].inverse;
                group.WorldToObject = new ComputeBuffer(count, 64); group.WorldToObject.SetData(inverse);
                group.Info = new ComputeBuffer(count, 8); group.Info.SetData(group.Metadata);
                group.Visible = new ComputeBuffer(count, 4, ComputeBufferType.Append);
                group.Args = new ComputeBuffer(1, 20, ComputeBufferType.IndirectArguments);
                group.Args.SetData(new uint[] { group.Batch.mesh.GetIndexCount(group.Batch.subMesh), 0,
                    group.Batch.mesh.GetIndexStart(group.Batch.subMesh), (uint)group.Batch.mesh.GetBaseVertex(group.Batch.subMesh), 0 });
                group.Properties = new MaterialPropertyBlock();
                group.Properties.SetBuffer("_RecoilForestObjectToWorld", group.ObjectToWorld);
                group.Properties.SetBuffer("_RecoilForestWorldToObject", group.WorldToObject);
                group.Properties.SetBuffer("_RecoilForestVisibleIndices", group.Visible);
                group.Matrices.Clear(); group.Matrices.TrimExcess();
                group.Metadata.Clear(); group.Metadata.TrimExcess();
            }
        }

        private static void CopyProperties(Material source, Material target)
        {
            for (var i = 0; i < source.shader.GetPropertyCount(); i++)
            {
                var name = source.shader.GetPropertyName(i);
                if (!target.HasProperty(name)) continue;
                switch (source.shader.GetPropertyType(i))
                {
                    case ShaderPropertyType.Texture:
                        target.SetTexture(name, source.GetTexture(name)); target.SetTextureScale(name, source.GetTextureScale(name));
                        target.SetTextureOffset(name, source.GetTextureOffset(name)); break;
                    case ShaderPropertyType.Color: target.SetColor(name, source.GetColor(name)); break;
                    case ShaderPropertyType.Vector: target.SetVector(name, source.GetVector(name)); break;
                    case ShaderPropertyType.Int: target.SetInteger(name, source.GetInteger(name)); break;
                    default: target.SetFloat(name, source.GetFloat(name)); break;
                }
            }
        }

        public void Draw(Camera camera, ref RecoilForestInstancedRenderer.ProfileSample sample)
        {
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
            for (var i = 0; i < 6; i++) planeVectors[i] = new Vector4(planes[i].normal.x, planes[i].normal.y, planes[i].normal.z, planes[i].distance);
            compute.SetVectorArray("_Planes", planeVectors); compute.SetVector("_CameraPosition", camera.transform.position);
            compute.SetFloat("_Perspective", 2f * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f));
            compute.SetFloat("_OrthoSize", camera.orthographicSize * 2f); compute.SetFloat("_LodBias", QualitySettings.lodBias);
            compute.SetInt("_Orthographic", camera.orthographic ? 1 : 0); compute.SetInt("_ChunkCount", chunkCount);
            compute.SetBuffer(chunkKernel, "_Chunks", chunks); compute.SetBuffer(chunkKernel, "_Thresholds", thresholds);
            compute.SetBuffer(chunkKernel, "_SelectedLods", selected);
            compute.Dispatch(chunkKernel, (chunkCount + 63) / 64, 1, 1);
            compute.SetBuffer(instanceKernel, "_SelectedLods", selected);
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                group.Visible.SetCounterValue(0);
                if ((camera.cullingMask & (1 << group.Batch.layer)) != 0)
                {
                    compute.SetInt("_InstanceCount", group.ObjectToWorld.count);
                    compute.SetBuffer(instanceKernel, "_InstanceMetadata", group.Info);
                    compute.SetBuffer(instanceKernel, "_VisibleIndices", group.Visible);
                    compute.Dispatch(instanceKernel, (group.ObjectToWorld.count + 63) / 64, 1, 1);
                }
                ComputeBuffer.CopyCount(group.Visible, group.Args, 4);
                ComputeBuffer.CopyCount(group.Visible, counts, i * 4);
                #pragma warning disable CS0618
                Graphics.DrawMeshInstancedIndirect(group.Batch.mesh, group.Batch.subMesh, group.Material,
                    worldBounds, group.Args, 0, group.Properties, group.Batch.shadows, group.Batch.receiveShadows,
                    group.Batch.layer, camera, LightProbeUsage.Off);
                #pragma warning restore CS0618
                sample.DrawCalls++;
            }
            sample.Indirect = true; sample.SourceBatches = SourceBatches;
            if (camera.cameraType == CameraType.Game)
            {
                sample.VisibleChunks = visibleChunks; sample.FrustumRejected = rejected; sample.LodRejected = lodRejected;
                sample.Lod0Chunks = lod0; sample.OtherLodChunks = lowerLod; sample.Instances = instances; sample.Triangles = triangles;
                sample.GpuCountsAvailable = lastReadback > 0;
                sample.GpuCountAge = lastReadback > 0 ? (float)(Time.realtimeSinceStartupAsDouble - lastReadback) : -1;
            }
            if (camera.cameraType == CameraType.Game && SystemInfo.supportsAsyncGPUReadback && !readbackPending && Time.realtimeSinceStartupAsDouble >= nextReadback)
            {
                readbackPending = true; nextReadback = Time.realtimeSinceStartupAsDouble + 1.0;
                AsyncGPUReadback.Request(selected, request =>
                {
                    if (disposed || request.hasError) return;
                    var lods = request.GetData<int>(); visibleChunks = rejected = lodRejected = lod0 = lowerLod = 0;
                    foreach (var lod in lods)
                        if (lod == -2) rejected++; else if (lod < 0) lodRejected++;
                        else { visibleChunks++; if (lod == 0) lod0++; else lowerLod++; }
                });
                AsyncGPUReadback.Request(counts, request =>
                {
                    readbackPending = false;
                    if (disposed || request.hasError) return;
                    var values = request.GetData<uint>(); instances = 0; triangles = 0;
                    for (var i = 0; i < values.Length; i++)
                    {
                        instances += (int)values[i];
                        triangles += (long)values[i] * groups[i].Batch.mesh.GetIndexCount(groups[i].Batch.subMesh) / 3;
                    }
                    lastReadback = Time.realtimeSinceStartupAsDouble;
                });
            }
        }

        public void Dispose()
        {
            disposed = true;
            foreach (var group in groups)
            {
                group.ObjectToWorld?.Release(); group.WorldToObject?.Release(); group.Info?.Release();
                group.Visible?.Release(); group.Args?.Release();
                if (group.Material != null) Destroy(group.Material);
            }
            chunks?.Release(); thresholds?.Release(); selected?.Release(); counts?.Release();
            if (compute != null) Destroy(compute);
        }

        private static void Destroy(UnityEngine.Object obj)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(obj); else UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
