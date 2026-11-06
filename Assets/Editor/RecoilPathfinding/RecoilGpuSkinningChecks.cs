using System;
using System.Collections.Generic;
using RecoilPathfinding;
using UnityEditor;
using UnityEngine;

namespace RecoilPathfindingEditor
{
    internal static class RecoilGpuSkinningChecks
    {
        [MenuItem("Game Tools/Công Thành/Kiểm tra GPU instancing xương lính")]
        public static void Validate()
        {
            var shader = Shader.Find("Recoil/GPU Skinned Crowd");
            Require(shader != null && shader.isSupported && !ShaderUtil.ShaderHasError(shader), "GPU skinning shader has errors or is unsupported.");
            var library = Resources.Load<RecoilGpuSkinningLibrary>("RecoilGpuSkinningMeshes");
            Require(library != null && library.Entries.Length > 0, "Prepare the GPU skinning mesh library first.");
            var indices = new List<Vector4>();
            var weights = new List<Vector4>();
            var checkedVertices = 0;
            foreach (var entry in library.Entries)
            {
                Require(entry.Source != null && entry.Mesh != null && entry.BindPoses.Length > 0, "Incomplete mesh entry.");
                entry.Mesh.GetUVs(3, indices);
                entry.Mesh.GetUVs(4, weights);
                var usedBones = entry.UsedBoneIndices != null ? new HashSet<int>(entry.UsedBoneIndices) : null;
                if (usedBones != null)
                {
                    Require(usedBones.Count > 0 && usedBones.Count == entry.UsedBoneIndices.Length,
                        "Used-bone metadata must be nonempty and unique.");
                    foreach (var bone in usedBones) Require(bone >= 0 && bone < entry.BindPoses.Length, "Used-bone metadata index is out of range.");
                }
                Require(indices.Count == entry.Source.vertexCount && weights.Count == indices.Count &&
                    entry.Mesh.subMeshCount == entry.Source.subMeshCount, "Vertex/submesh layout changed during preparation.");
                for (var i = 0; i < indices.Count; i++)
                {
                    var sum = 0f;
                    for (var j = 0; j < 4; j++)
                    {
                        Require(indices[i][j] >= 0f && indices[i][j] < entry.BindPoses.Length &&
                            indices[i][j] == Mathf.Floor(indices[i][j]), "GPU bone index is out of range.");
                        Require(weights[i][j] >= 0f && float.IsFinite(weights[i][j]), "Invalid bone weight.");
                        if (weights[i][j] > 0f && usedBones != null)
                            Require(usedBones.Contains((int)indices[i][j]), "A weighted bone cannot be removed from transform capture.");
                        sum += weights[i][j];
                    }
                    Require(Mathf.Abs(sum - 1f) < 0.0001f, "Skin weights must sum to one.");
                }
                checkedVertices += indices.Count;
            }
            ValidateMatrixConvention();
            RecoilGpuSkinnedCrowdRenderer.ValidateBoneJobs();
            ValidateLifecycle();
            Debug.Log($"GPU bone instancing checks passed: {library.Entries.Length} meshes, {checkedVertices:N0} vertices, shader, indices, normalized weights, instance transforms, cached bone bindings, pool remapping and allocation-free LOD scale. Inspect Frame Debugger in Play Mode to verify actual GPU draws.");
        }

        private static void ValidateMatrixConvention()
        {
            var vertex = new Vector3(1f, 2f, -1f);
            var bindPose = Matrix4x4.TRS(new Vector3(0f, -2f, 0f), Quaternion.identity, Vector3.one);
            foreach (var translation in new[] { Vector3.zero, new Vector3(100f, 3f, -20f) })
            {
                var rendererWorld = Matrix4x4.TRS(translation, Quaternion.Euler(0f, 45f, 0f), Vector3.one * 2f);
                var boneWorld = rendererWorld * Matrix4x4.TRS(new Vector3(0f, 2f, 0f), Quaternion.Euler(20f, 0f, 30f), Vector3.one);
                var skin = rendererWorld.inverse * boneWorld * bindPose;
                var actual = rendererWorld.MultiplyPoint3x4(skin.MultiplyPoint3x4(vertex));
                var expected = (boneWorld * bindPose).MultiplyPoint3x4(vertex);
                Require(Vector3.Distance(actual, expected) < 0.0001f, "Renderer transforms must be applied exactly once per instance.");
            }
        }

        private static void ValidateLifecycle()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Data/Characters/Prefabs/BO_BINH_blue.prefab");
            Require(prefab != null, "The lifecycle check needs the blue infantry prefab.");
            var owner = new GameObject("Temporary GPU skin lifecycle check");
            var unit = UnityEngine.Object.Instantiate(prefab);
            try
            {
                var renderer = owner.AddComponent<RecoilGpuSkinnedCrowdRenderer>();
                var weapon = GameObject.CreatePrimitive(PrimitiveType.Cube);
                weapon.name = "Temporary rigid weapon";
                weapon.transform.SetParent(unit.transform, false);
                var weaponRenderer = weapon.GetComponent<MeshRenderer>();
                weaponRenderer.sharedMaterial = unit.GetComponentInChildren<SkinnedMeshRenderer>(true).sharedMaterial;
                var parts = unit.GetComponentsInChildren<Renderer>(true);
                var animators = unit.GetComponentsInChildren<Animator>(true);
                var forceOff = new bool[parts.Length];
                var modes = new AnimatorCullingMode[animators.Length];
                for (var i = 0; i < parts.Length; i++) forceOff[i] = parts[i].forceRenderingOff;
                for (var i = 0; i < animators.Length; i++) modes[i] = animators[i].cullingMode;
                renderer.Register(unit);
                Require(renderer.RegisteredRenderers > 0, "Infantry must register at least one GPU-skinned part.");
                Require(weaponRenderer.forceRenderingOff, "Rigid weapons must register for instancing and hide their native renderer.");
                var registered = renderer.RegisteredRenderers;
                renderer.Register(unit);
                Require(renderer.RegisteredRenderers == registered, "Re-registering a live unit must not draw it twice.");
                renderer.Unregister(unit);
                for (var i = 0; i < parts.Length; i++)
                    Require(parts[i].forceRenderingOff == forceOff[i], "Returning a unit to the pool must restore its native renderer.");
                for (var i = 0; i < animators.Length; i++)
                    Require(animators[i].cullingMode == modes[i], "Returning a unit to the pool must restore animator culling.");
                renderer.Register(unit);
                renderer.SetRenderingEnabled(false);
                Require(renderer.RegisteredRenderers == 0, "Disabling GPU instancing must release registrations.");
                renderer.SetRenderingEnabled(false);
                Require(renderer.RegisteredRenderers == 0, "Repeated cleanup must be safe.");
                for (var i = 0; i < parts.Length; i++)
                    Require(parts[i].forceRenderingOff == forceOff[i], "Disabling the backend must restore native visibility.");
                renderer.SetRenderingEnabled(true);
                renderer.Register(unit);
                Require(renderer.RegisteredRenderers == registered, "Re-enabling the backend must allow pooled units to register again.");
                renderer.SetRenderingEnabled(false);
            }
            finally
            {
                var renderer = owner.GetComponent<RecoilGpuSkinnedCrowdRenderer>();
                if (renderer != null) renderer.SetRenderingEnabled(false);
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(unit);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
