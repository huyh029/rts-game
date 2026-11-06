using System;
using System.Collections.Generic;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace GameToolsEditor
{
    public static class CongThanhForestInstancingBaker
    {
        private sealed class Chunk
        {
            public Bounds Bounds;
            public bool HasBounds;
            public float Size;
            public int Count;
            public float[] Thresholds;
            public readonly Dictionary<(Mesh mesh, Material mat, int sub, int lod, int layer, uint renderLayer,
                ShadowCastingMode shadows, bool receive), List<Matrix4x4>> Batches = new();
        }

        public static string Bake(GameObject mapRoot, float cellSize)
        {
            if (mapRoot == null || !mapRoot.scene.IsValid() || !float.IsFinite(cellSize) || cellSize < 8f)
                throw new InvalidOperationException("Chọn Map trong scene và kích thước ô ít nhất 8 m.");
            if (!SystemInfo.supportsInstancing) throw new InvalidOperationException("GPU hiện tại không hỗ trợ instancing.");
            var disk = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(Application.dataPath));
            if (disk.AvailableFreeSpace < 2L * 1024 * 1024 * 1024)
                throw new InvalidOperationException("Cần trống ít nhất 2 GB trước khi bake.");
            var count = 0;
            foreach (var forest in mapRoot.GetComponentsInChildren<RecoilForestArea>())
                if (!forest.meshBaked) { BakeForest(forest, cellSize); count++; }
            if (count == 0) throw new InvalidOperationException("Không còn rừng prefab để bake. Hãy tạo lại rừng trước khi đổi cách bake.");
            return $"Đã bake {count} vùng rừng bằng GPU instancing theo ô {cellSize:F0} m. Mesh dùng chung, LOD và gốc cây được giữ. Lưu scene để giữ kết quả.";
        }

        private static void BakeForest(RecoilForestArea forest, float cellSize)
        {
            var originals = new List<GameObject>();
            foreach (Transform child in forest.transform) originals.Add(child.gameObject);
            var chunks = new Dictionary<(int x, int z, string profile), Chunk>();
            var memberships = new Dictionary<Renderer, (LODGroup group, int level, float[] thresholds)>();
            foreach (var group in forest.GetComponentsInChildren<LODGroup>())
            {
                var lods = group.GetLODs();
                var thresholds = new float[lods.Length];
                for (var i = 0; i < lods.Length; i++) thresholds[i] = lods[i].screenRelativeTransitionHeight;
                for (var i = 0; i < lods.Length; i++)
                    foreach (var renderer in lods[i].renderers)
                        if (renderer != null) memberships[renderer] = (group, i, thresholds);
            }
            long instances = 0;
            var renderers = forest.GetComponentsInChildren<MeshRenderer>();
            var inverse = forest.transform.worldToLocalMatrix;
            var rootSize = Mathf.Max(0.001f, MaxScale(forest.transform));
            for (var i = 0; i < renderers.Length; i++)
            {
                if ((i & 127) == 0 && EditorUtility.DisplayCancelableProgressBar("Bake GPU instancing", "Thu thập cây và cỏ",
                    0.6f * i / Mathf.Max(1, renderers.Length))) throw new OperationCanceledException();
                var renderer = renderers[i];
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null) continue;
                var mesh = filter.sharedMesh;
                if (!AssetDatabase.Contains(mesh))
                    throw new InvalidOperationException("Mesh cây/cỏ phải là asset đã lưu trong Project.");
                var level = 0;
                var thresholds = new[] { 0.01f };
                var referenceSize = renderer.bounds.size.magnitude / rootSize;
                var anchor = renderer.transform.position;
                if (memberships.TryGetValue(renderer, out var member))
                {
                    level = member.level; thresholds = member.thresholds;
                    referenceSize = member.group.size * MaxScale(member.group.transform) / rootSize;
                    anchor = member.group.transform.position;
                }
                else if (!renderer.enabled) continue;
                var category = renderer.transform;
                while (category.parent != null && category.parent != forest.transform) category = category.parent;
                var profile = category.name + ":" + string.Join("_", Array.ConvertAll(thresholds,
                    t => t.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                var key = (Mathf.FloorToInt(anchor.x / cellSize), Mathf.FloorToInt(anchor.z / cellSize), profile);
                if (!chunks.TryGetValue(key, out var chunk))
                    chunks.Add(key, chunk = new Chunk { Thresholds = thresholds });
                chunk.Size += referenceSize; chunk.Count++;
                var matrix = inverse * renderer.localToWorldMatrix;
                var bounds = RecoilForestInstancedRenderer.TransformBounds(matrix, mesh.bounds);
                if (!chunk.HasBounds) { chunk.Bounds = bounds; chunk.HasBounds = true; }
                else chunk.Bounds.Encapsulate(bounds);
                var materials = renderer.sharedMaterials;
                for (var sub = 0; sub < materials.Length; sub++)
                {
                    if (materials[sub] == null || mesh.subMeshCount == 0) continue;
                    var batchKey = (mesh, materials[sub], Mathf.Min(sub, mesh.subMeshCount - 1), level,
                        renderer.gameObject.layer, renderer.renderingLayerMask, renderer.shadowCastingMode, renderer.receiveShadows);
                    if (!chunk.Batches.TryGetValue(batchKey, out var matrices))
                        chunk.Batches.Add(batchKey, matrices = new List<Matrix4x4>());
                    matrices.Add(matrix); instances++;
                }
            }
            if (instances == 0) throw new InvalidOperationException("Không có mesh cây/cỏ hợp lệ.");
            if (instances * 64 > 256L * 1024 * 1024)
                throw new InvalidOperationException("Dữ liệu vị trí vượt 256 MB. Hãy giảm số cây/cỏ.");

            var folder = "Assets/Data/Environment/BakedForests/Instances_" + Guid.NewGuid().ToString("N");
            EnsureFolder(folder);
            var data = ScriptableObject.CreateInstance<RecoilForestInstanceData>();
            var persisted = false;
            GameObject output = null;
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            try
            {
                var materialCache = new Dictionary<Material, Material>();
                var records = new List<RecoilForestInstanceData.Chunk>();
                foreach (var chunk in chunks.Values)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Bake GPU instancing", "Lưu dữ liệu vị trí, giữ mesh dùng chung",
                        0.6f + 0.4f * records.Count / Mathf.Max(1, chunks.Count))) throw new OperationCanceledException();
                    var batches = new List<RecoilForestInstanceData.Batch>();
                    foreach (var pair in chunk.Batches)
                    {
                        var key = pair.Key;
                        if (!materialCache.TryGetValue(key.mat, out var material))
                        {
                            material = key.mat;
                            if (!material.enableInstancing)
                            {
                                material = new Material(key.mat) { name = key.mat.name + " (Instanced)", enableInstancing = true };
                                AssetDatabase.CreateAsset(material, folder + "/Material_" + materialCache.Count + ".mat");
                            }
                            materialCache.Add(key.mat, material);
                        }
                        // 511 also accommodates shaders carrying both object/world transform matrices.
                        for (var start = 0; start < pair.Value.Count; start += 511)
                            batches.Add(new RecoilForestInstanceData.Batch
                            {
                                mesh = key.mesh, material = material, subMesh = key.sub, lod = key.lod, layer = key.layer,
                                renderingLayer = key.renderLayer, shadows = key.shadows, receiveShadows = key.receive,
                                matrices = pair.Value.GetRange(start, Mathf.Min(511, pair.Value.Count - start)).ToArray()
                            });
                    }
                    records.Add(new RecoilForestInstanceData.Chunk { localBounds = chunk.Bounds,
                        referenceSize = chunk.Size / Mathf.Max(1, chunk.Count), thresholds = chunk.Thresholds, batches = batches.ToArray() });
                }
                data.chunks = records.ToArray();
                AssetDatabase.CreateAsset(data, folder + "/Instances.asset");
                persisted = true;
                AssetDatabase.SaveAssets();
                output = new GameObject("Instanced Forest");
                output.SetActive(false);
                Undo.RegisterCreatedObjectUndo(output, "Bake Instanced Forest");
                output.transform.SetParent(forest.transform, false);
                output.AddComponent<RecoilForestInstancedRenderer>().data = data;
                Undo.RecordObject(forest, "Save Forest Instance Bake");
                forest.meshBaked = true; forest.meshAssetFolder = folder;
                foreach (var original in originals) CongThanhForestUndoUtility.DestroyHierarchy(original);
                output.SetActive(true);
                EditorSceneManager.MarkSceneDirty(forest.gameObject.scene);
                Undo.CollapseUndoOperations(undoGroup);
                Debug.Log($"Forest instancing: {renderers.Length:N0} source renderers, {instances:N0} transforms, {records.Count:N0} chunks; matrix data ~{instances * 64 / (1024f * 1024f):F1} MB. {folder}", forest);
            }
            catch
            {
                Undo.FlushUndoRecordObjects(); Undo.RevertAllDownToGroup(undoGroup);
                if (!persisted) UnityEngine.Object.DestroyImmediate(data);
                AssetDatabase.DeleteAsset(folder);
                throw;
            }
        }

        private static float MaxScale(Transform transform)
        {
            var s = transform.lossyScale;
            return Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        }

        private static void EnsureFolder(string path)
        {
            var parent = "Assets";
            foreach (var part in path.Substring(7).Split('/'))
            {
                if (!AssetDatabase.IsValidFolder(parent + "/" + part)) AssetDatabase.CreateFolder(parent, part);
                parent += "/" + part;
            }
        }
    }
}
