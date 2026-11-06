using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RecoilPathfinding;
using UnityEditor;
using UnityEngine;

namespace GameToolsEditor
{
    public static class CongThanhMeshLodSetup
    {
        public const string DotChildName = "LOD4_Dot";
        private const string Folder = "Assets/Data/Characters/Prefabs";
        private const string Report = "Assets/Data/Characters/LodSetupReport.txt";

        public static string SetupMeshLods()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Hãy dừng chế độ chạy trước khi thiết lập LOD.";
            var report = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { Folder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                GameObject root = null;
                try
                {
                    root = PrefabUtility.LoadPrefabContents(path);
                    RemoveChild(root, DotChildName);
                    var description = Configure(root, new[] { 0.08f, 0.02f, 0f });
                    PrefabUtility.SaveAsPrefabAsset(root, path, out var success);
                    if (!success) throw new InvalidOperationException("Không thể lưu prefab.");
                    var saved = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    var lods = saved.GetComponent<LODGroup>().GetLODs();
                    if (lods.Length != 2 ||
                        !Mathf.Approximately(lods[0].screenRelativeTransitionHeight, 0.56f) ||
                        !Mathf.Approximately(lods[1].screenRelativeTransitionHeight, 0f) ||
                        lods.Any(lod => lod.renderers.Length == 0) ||
                        saved.transform.Find(DotChildName) != null)
                        throw new InvalidOperationException("Kiểm tra sau lưu thất bại: prefab phải có đúng 3 LOD mesh 3D và không có dot.");
                    report.Add(root.name + ": " + description);
                }
                catch (Exception exception)
                {
                    report.Add(Path.GetFileNameWithoutExtension(path) + ": LỖI — " + exception.Message);
                    Debug.LogException(exception);
                }
                finally
                {
                    if (root != null) PrefabUtility.UnloadPrefabContents(root);
                }
            }
            File.WriteAllLines(Report, report);
            AssetDatabase.ImportAsset(Report);
            return string.Join("\n", report);
        }

        private static void RemoveChild(GameObject root, string childName)
        {
            var child = root.transform.Find(childName);
            if (child != null) UnityEngine.Object.DestroyImmediate(child.gameObject);
        }

        public static string Configure(GameObject root, float[] thresholds, bool save = true)
        {
            var levels = new[] { new List<Renderer>(), new List<Renderer>(), new List<Renderer>() };
            var shared = new List<Renderer>();
            var ignored = new List<Renderer>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                var level = 0;
                for (var current = renderer.transform; current != root.transform; current = current.parent)
                {
                    var match = Regex.Match(current.name, @"(?i)(?:^|[^a-z0-9])lod[\s_.-]*0*([1-9][0-9]*)(?![a-z0-9])");
                    if (match.Success) { level = int.Parse(match.Groups[1].Value); break; }
                }
                if (level == 0) shared.Add(renderer);
                else if (level <= 3) levels[level - 1].Add(renderer);
                else ignored.Add(renderer);
            }
            // Some imported models leave the first mesh unsuffixed (BODY_linh.001)
            // while naming its alternatives BODY_linh.001_LOD2 and _LOD3.
            // Promote only exact hierarchy counterparts, preserving unrelated shared parts.
            string FamilyPath(Renderer renderer) => Regex.Replace(
                AnimationUtility.CalculateTransformPath(renderer.transform, root.transform),
                @"(?i)[\s_.-]*lod[\s_.-]*0*[1-9][0-9]*(?=$|/)", string.Empty);
            var secondFamilies = new HashSet<string>(levels[1].Select(FamilyPath), StringComparer.OrdinalIgnoreCase);
            var thirdFamilies = new HashSet<string>(levels[2].Select(FamilyPath), StringComparer.OrdinalIgnoreCase);
            var explicitFirst = new HashSet<Renderer>(levels[0]);
            foreach (var renderer in shared.ToArray())
            {
                var family = FamilyPath(renderer);
                if (!secondFamilies.Contains(family) || !thirdFamilies.Contains(family)) continue;
                levels[0].Add(renderer);
                shared.Remove(renderer);
            }
            // A base mesh and its explicit _LOD1 copy must not both draw at LOD1 (double skinning,
            // double draws, z-fighting). Keep the base, which has more detail. Bases renamed on the
            // other team (Cylinder.003 vs Cylinder.009_LOD1.001) are found by type, parent and size.
            var duplicates = new List<Renderer>();
            foreach (var copy in explicitFirst)
            {
                var family = FamilyPath(copy);
                var baseRenderer = levels[0].FirstOrDefault(r => r != copy && !explicitFirst.Contains(r) &&
                    string.Equals(FamilyPath(r), family, StringComparison.OrdinalIgnoreCase)) ??
                    shared.FirstOrDefault(r => r.GetType() == copy.GetType() && r.transform.parent == copy.transform.parent &&
                        VertexCount(r) > VertexCount(copy));
                if (baseRenderer == null) continue;
                levels[0].Remove(copy);
                duplicates.Add(copy);
                if (shared.Remove(baseRenderer)) levels[0].Add(baseRenderer);
            }
            if (levels.Any(level => level.Count == 0))
                throw new InvalidOperationException("Thiếu mesh LOD1/2/3; không lưu thay đổi.");
            var description = $"LOD1/2/3: {levels[0].Count}/{levels[1].Count}/{levels[2].Count}; " +
                "mesh 3D; LOD3 không bị cull" +
                (duplicates.Count > 0 ? $"; tắt bản trùng LOD1: {string.Join(", ", duplicates.Select(r => r.name))}" : "");
            if (!save) return description;
            foreach (var old in root.GetComponentsInChildren<LODGroup>(true)) UnityEngine.Object.DestroyImmediate(old);
            foreach (var renderer in ignored.Concat(duplicates)) renderer.enabled = false;
            // Distant levels need fewer skin influences; shared parts keep their LOD1 quality.
            foreach (var renderer in levels[1].Except(shared).OfType<SkinnedMeshRenderer>()) renderer.quality = SkinQuality.Bone2;
            foreach (var renderer in levels[2].Except(shared).OfType<SkinnedMeshRenderer>()) renderer.quality = SkinQuality.Bone1;
            // Off-screen soldiers keep advancing their state machine (attack/death timing stays
            // correct) but skip bone writes and skinning.
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            foreach (var renderer in levels[0].Except(shared)) renderer.enabled = false;
            var activeRenderers = levels.Skip(1).SelectMany(level => level).Concat(shared);
            foreach (var renderer in activeRenderers)
            {
                renderer.enabled = true;
                for (var current = renderer.transform; current != root.transform; current = current.parent)
                    current.gameObject.SetActive(true);
            }
            var lods = levels.Skip(1).Select((level, index) => new LOD(index == 0 ? 0.56f : 0f,
                level.Concat(shared).Distinct().ToArray())).ToList();
            var group = root.AddComponent<LODGroup>();
            group.SetLODs(lods.ToArray());
            group.fadeMode = LODFadeMode.None;
            group.RecalculateBounds();
            return description;
        }

        private static int VertexCount(Renderer renderer)
        {
            Mesh mesh;
            if (renderer is SkinnedMeshRenderer skinned) mesh = skinned.sharedMesh;
            else
            {
                var filter = renderer.GetComponent<MeshFilter>();
                mesh = filter != null ? filter.sharedMesh : null;
            }
            return mesh != null ? mesh.vertexCount : 0;
        }

    }
}
