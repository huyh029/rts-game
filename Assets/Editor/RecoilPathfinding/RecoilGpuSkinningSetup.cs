using System;
using System.Collections.Generic;
using RecoilPathfinding;
using UnityEditor;
using UnityEngine;

namespace RecoilPathfindingEditor
{
    [InitializeOnLoad]
    internal static class RecoilGpuSkinningSetup
    {
        private const string Path = "Assets/Resources/RecoilGpuSkinningMeshes.asset";
        static RecoilGpuSkinningSetup()
        {
            EditorApplication.delayCall += PrepareIfMissing;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) PrepareIfMissing();
            };
        }
        private static void PrepareIfMissing()
        {
            if (EditorApplication.isPlaying) return;
            var existing = AssetDatabase.LoadAssetAtPath<RecoilGpuSkinningLibrary>(Path);
            if (existing != null && Array.TrueForAll(existing.Entries, entry => entry.UsedBoneIndices != null)) return;
            try { Build(); } catch (Exception e) { Debug.LogException(e); }
        }
        [MenuItem("Game Tools/Công Thành/Chuẩn bị GPU instancing xương lính")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Dừng Play Mode trước khi chuẩn bị mesh GPU.");
            var entries = new List<RecoilGpuSkinningLibrary.Entry>();
            var seen = new HashSet<Mesh>();
            var skipped = 0;
            try
            {
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Data/Characters/Prefabs" }))
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                    foreach (var renderer in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        var source = renderer.sharedMesh;
                        if (source == null || !seen.Add(source)) continue;
                        if (source.blendShapeCount != 0) { skipped++; continue; }
                        using var influences = source.GetBonesPerVertex();
                        var supported = influences.Length == source.vertexCount;
                        foreach (var count in influences) if (count == 0 || count > 4) supported = false;
                        if (!supported) { skipped++; continue; }
                        var weights = source.boneWeights;
                        var bindposes = source.bindposes;
                        if (weights.Length != source.vertexCount || bindposes.Length == 0) { skipped++; continue; }
                        var indices = new List<Vector4>(weights.Length);
                        var usedBones = new SortedSet<int>();
                        var values = new List<Vector4>(weights.Length);
                        foreach (var weight in weights)
                        {
                            if (weight.weight0 > 0f) usedBones.Add(weight.boneIndex0);
                            if (weight.weight1 > 0f) usedBones.Add(weight.boneIndex1);
                            if (weight.weight2 > 0f) usedBones.Add(weight.boneIndex2);
                            if (weight.weight3 > 0f) usedBones.Add(weight.boneIndex3);
                            indices.Add(new Vector4(weight.boneIndex0, weight.boneIndex1, weight.boneIndex2, weight.boneIndex3));
                            var value = new Vector4(weight.weight0, weight.weight1, weight.weight2, weight.weight3);
                            var total = value.x + value.y + value.z + value.w;
                            if (total <= 0f) throw new InvalidOperationException($"Mesh {source.name} có vertex không có trọng số xương.");
                            values.Add(value / total);
                        }
                        var mesh = UnityEngine.Object.Instantiate(source);
                        mesh.name = source.name + " GPU Bones";
                        mesh.SetUVs(3, indices);
                        mesh.SetUVs(4, values);
                        var bounds = renderer.localBounds;
                        bounds.Encapsulate(source.bounds);
                        bounds.Expand(bounds.size + Vector3.one * 0.5f);
                        mesh.bounds = bounds;
                        var usedBoneIndices = new int[usedBones.Count];
                        usedBones.CopyTo(usedBoneIndices);
                        entries.Add(new RecoilGpuSkinningLibrary.Entry { Source = source, Mesh = mesh, BindPoses = bindposes, UsedBoneIndices = usedBoneIndices });
                    }
                }
                if (entries.Count == 0) throw new InvalidOperationException("Không tìm thấy mesh lính hỗ trợ tối đa 4 xương/vertex.");
                var library = AssetDatabase.LoadAssetAtPath<RecoilGpuSkinningLibrary>(Path);
                if (library == null)
                {
                    library = ScriptableObject.CreateInstance<RecoilGpuSkinningLibrary>();
                    AssetDatabase.CreateAsset(library, Path);
                }
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(Path))
                    if (asset is Mesh oldMesh) UnityEngine.Object.DestroyImmediate(oldMesh, true);
                foreach (var entry in entries) AssetDatabase.AddObjectToAsset(entry.Mesh, library);
                library.Entries = entries.ToArray();
                EditorUtility.SetDirty(library);
                AssetDatabase.SaveAssets();
                RecoilGpuSkinningChecks.Validate();
                Debug.Log($"GPU bone instancing: prepared {entries.Count} shared meshes; {skipped} unsupported meshes keep native rendering. No animation textures.");
            }
            catch
            {
                foreach (var entry in entries)
                    if (entry.Mesh != null && !AssetDatabase.Contains(entry.Mesh)) UnityEngine.Object.DestroyImmediate(entry.Mesh);
                throw;
            }
        }
    }
}
