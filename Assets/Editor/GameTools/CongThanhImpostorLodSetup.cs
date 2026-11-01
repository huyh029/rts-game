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
    public static class CongThanhImpostorLodSetup
    {
        public const string ChildName = "LOD4_Impostor";
        public const string DotChildName = "LOD4_Dot";
        private const string Folder = "Assets/Data/Characters/Prefabs";
        private const string Bake = "Assets/_Game/GameResources/Bake";
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
                    RemoveChild(root, ChildName);
                    RemoveChild(root, DotChildName);
                    var description = Configure(root, new[] { 0.08f, 0.02f, 0f });
                    PrefabUtility.SaveAsPrefabAsset(root, path, out var success);
                    if (!success) throw new InvalidOperationException("Không thể lưu prefab.");
                    var saved = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    var lods = saved.GetComponent<LODGroup>().GetLODs();
                    if (lods.Length != 3 ||
                        !Mathf.Approximately(lods[0].screenRelativeTransitionHeight, 0.08f) ||
                        !Mathf.Approximately(lods[1].screenRelativeTransitionHeight, 0.02f) ||
                        !Mathf.Approximately(lods[2].screenRelativeTransitionHeight, 0f) ||
                        lods.Any(lod => lod.renderers.Length == 0) ||
                        saved.transform.Find(ChildName) != null ||
                        saved.transform.Find(DotChildName) != null)
                        throw new InvalidOperationException("Kiểm tra sau lưu thất bại: prefab phải có đúng 3 LOD mesh 3D và không có dot/impostor.");
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

        public static string AttachAll()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Hãy dừng chế độ chạy trước khi thiết lập LOD.";
            var report = new List<string>();
            var usedSources = new HashSet<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { Folder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var source = FindSource(Path.GetFileNameWithoutExtension(path));
                if (source == null)
                {
                    report.Add(Path.GetFileNameWithoutExtension(path) + ": chưa có bản bake xác định đúng loại/phe; giữ nguyên.");
                    continue;
                }
                GameObject root = null;
                try
                {
                    root = PrefabUtility.LoadPrefabContents(path);
                    var existing = root.transform.Find(ChildName);
                    if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);
                    var container = new GameObject(ChildName);
                    container.transform.SetParent(root.transform, false);
                    var child = (GameObject)PrefabUtility.InstantiatePrefab(source, container.transform);
                    foreach (var transform in child.GetComponentsInChildren<Transform>(true))
                        GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
                    child.transform.localPosition = Vector3.zero;
                    child.SetActive(true);
                    // Keep the bake's authored scale and orientation. Its mesh bounds are
                    // deliberately inflated for billboard culling, so they cannot size the art.
                    container.AddComponent<RecoilImpostorLodAnimationSync>();
                    foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    var description = Configure(root, new[] { 0.08f, 0.02f, 0.005f },
                        includeImpostor: true);
                    PrefabUtility.SaveAsPrefabAsset(root, path, out var success);
                    if (!success) throw new InvalidOperationException("Không thể lưu prefab.");
                    ValidateSaved(path);
                    usedSources.Add(AssetDatabase.GetAssetPath(source));
                    report.Add(root.name + ": " + AssetDatabase.GetAssetPath(source) + " — " + description);
                }
                catch (Exception exception)
                {
                    var missingLevels = exception.Message.StartsWith("Thiếu mesh", StringComparison.Ordinal);
                    report.Add(Path.GetFileNameWithoutExtension(path) +
                        (missingLevels ? ": bỏ qua — " : ": LỖI — ") + exception.Message);
                    if (missingLevels) Debug.LogWarning(Path.GetFileNameWithoutExtension(path) + ": " + exception.Message);
                    else Debug.LogException(exception);
                }
                finally
                {
                    if (root != null) PrefabUtility.UnloadPrefabContents(root);
                }
            }
            report.Add("\nBẢN BAKE CHƯA ĐƯỢC GHÉP:");
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { Bake }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!usedSources.Contains(path)) report.Add(path);
            }
            File.WriteAllLines(Report, report);
            AssetDatabase.ImportAsset(Report);
            Debug.Log("Gắn impostor LOD hoàn tất:\n" + string.Join("\n", report));
            return string.Join("\n", report);
        }

        private static GameObject FindSource(string name)
        {
            var team = name.EndsWith("_blue", StringComparison.OrdinalIgnoreCase) ? "blue" :
                name.EndsWith("_red", StringComparison.OrdinalIgnoreCase) ? "red" : null;
            if (team == null) return null;
            var type = name.Substring(0, name.Length - team.Length - 1).ToUpperInvariant();
            string kind;
            switch (type)
            {
                case "CUNG_KY": case "LOD_CUNGKY": kind = "Cung"; break;
                case "BO_BINH": case "DAN_BINH": case "LOD_DAO_BINH": kind = "DaoBinh"; break;
                case "GIAO_BINH": case "LOD_GIAO_BINH": kind = "GiaoBinh"; break;
                case "MAYBANDA_V02": case "LOD_MAYDA": kind = "MayBanDa"; break;
                case "QUAN_SU": case "LOD_QUANSU": kind = "QuanSu"; break;
                case "VOI": kind = "Voi"; break;
                case "KY_BINH": case "LOD_KYBINH":
                    return AssetDatabase.LoadAssetAtPath<GameObject>(
                        Bake + "/LOD_KYBINH_" + team + "_AnimatedImpostor/LOD_KYBINH_AnimatedImpostor.prefab");
                default: return null;
            }
            var assetName = kind + "_" + team + "_AnimatedImpostor";
            return AssetDatabase.LoadAssetAtPath<GameObject>(Bake + "/" + assetName + "/" + assetName + ".prefab");
        }

        public static string Configure(GameObject root, float[] thresholds, bool save = true,
            bool includeImpostor = false)
        {
            var impostor = root.transform.Find(ChildName);
            var levels = new[] { new List<Renderer>(), new List<Renderer>(), new List<Renderer>() };
            var shared = new List<Renderer>();
            var ignored = new List<Renderer>();
            var distant = new List<Renderer>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer)) continue;
                if (impostor != null &&
                    (renderer.transform == impostor || renderer.transform.IsChildOf(impostor)))
                {
                    distant.Add(renderer);
                    continue;
                }
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
            if (includeImpostor && impostor != null && distant.Count == 0)
                throw new InvalidOperationException("Nhánh impostor không có renderer.");
            var description = $"LOD1/2/3: {levels[0].Count}/{levels[1].Count}/{levels[2].Count}; " +
                (includeImpostor ? $"impostor: {distant.Count}" : "chỉ mesh 3D; LOD3 không bị cull") +
                (duplicates.Count > 0 ? $"; tắt bản trùng LOD1: {string.Join(", ", duplicates.Select(r => r.name))}" : "");
            if (!save) return description;
            foreach (var old in root.GetComponentsInChildren<LODGroup>(true)) UnityEngine.Object.DestroyImmediate(old);
            foreach (var renderer in ignored.Concat(duplicates)) renderer.enabled = false;
            // Distant levels need fewer skin influences; shared parts keep their LOD1 quality.
            foreach (var renderer in levels[1].Except(shared).OfType<SkinnedMeshRenderer>()) renderer.quality = SkinQuality.Bone2;
            foreach (var renderer in levels[2].Except(shared).OfType<SkinnedMeshRenderer>()) renderer.quality = SkinQuality.Bone1;
            // Off-screen soldiers keep advancing their state machine (attack/death timing stays
            // correct) but skip bone writes and skinning. The impostor branch reads the full pose.
            if (!includeImpostor)
                foreach (var animator in root.GetComponentsInChildren<Animator>(true))
                    animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            var activeRenderers = levels.SelectMany(level => level).Concat(shared)
                .Concat(includeImpostor ? distant : Enumerable.Empty<Renderer>());
            foreach (var renderer in activeRenderers)
            {
                renderer.enabled = true;
                for (var current = renderer.transform; current != root.transform; current = current.parent)
                    current.gameObject.SetActive(true);
            }
            var lods = levels.Select((level, index) => new LOD(thresholds[index], level.Concat(shared).Distinct().ToArray())).ToList();
            if (includeImpostor && distant.Count > 0) lods.Add(new LOD(0f, distant.ToArray()));
            if (!includeImpostor && impostor != null) impostor.gameObject.SetActive(false);
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

        private static void ValidateSaved(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var lods = prefab.GetComponent<LODGroup>().GetLODs();
            if (lods.Length != 4 || lods[3].screenRelativeTransitionHeight != 0f || lods[3].renderers.Length == 0)
                throw new InvalidOperationException("Kiểm tra sau lưu thất bại: cần 4 mức LOD và mức cuối không cull.");
            if (prefab.transform.Cast<Transform>().Count(child => child.name == ChildName) != 1)
                throw new InvalidOperationException("Nhánh impostor bị trùng hoặc thiếu.");
            if (lods.Take(3).Any(lod => lod.renderers.Any(renderer => lods[3].renderers.Contains(renderer))))
                throw new InvalidOperationException("Impostor bị hiển thị cùng mesh ở mức gần.");
        }
    }
}
