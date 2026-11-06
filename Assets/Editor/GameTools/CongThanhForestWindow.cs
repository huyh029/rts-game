using System;
using System.Collections.Generic;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameToolsEditor
{
    public sealed class CongThanhForestWindow : EditorWindow
    {
        private const string TreeFolder = "Assets/_Game/Anime Trees/Prefabs";
        [SerializeField] private GameObject mapRoot, plantingPlane, blueCastle, redCastle;
        [SerializeField] private GameObject[] prefabs = Array.Empty<GameObject>();
        [SerializeField] private float spacing = 8f, maxSlope = 60f;
        [SerializeField] private float trunkRadius = 0.7f, castleClearance = 12f;
        [SerializeField] private Vector2 scaleRange = new Vector2(0.8f, 1.3f);
        [SerializeField] private int maxTrees = 10000, seed = 12345;
        [SerializeField] private GameObject grassPrefab;
        [SerializeField] private float grassSpacing = 3f, grassMaxSlope = 35f;
        [SerializeField] private Vector2 grassScaleRange = new Vector2(0.2f, 0.35f);
        [SerializeField] private int maxGrass = 20000;
        [SerializeField] private float meshChunkSize = 32f;
        private bool showPrefabs, enableInstancing = true, operationQueued;
        private Vector2 scroll;
        private string status;
        private Texture2D preview;
        private float previewAspect = 1f;
        private readonly List<Bounds> castleBounds = new List<Bounds>();
        private enum PlantRegion { Trees, Grass }
        private struct Placement
        {
            public Vector3 Position, Up;
            public int Prefab;
            public float Scale, Yaw;
            public PlantRegion Region;
        }

        [MenuItem("Game Tools/Công Thành/Trồng cây và cỏ theo Plane")]
        public static void Open()
        {
            var window = GetWindow<CongThanhForestWindow>("Cây và cỏ theo Plane");
            window.minSize = new Vector2(420f, 480f);
            window.Show();
            window.Focus();
        }

        private void OnEnable()
        {
            if (prefabs == null) prefabs = Array.Empty<GameObject>();
            if ((prefabs == null || prefabs.Length == 0) && AssetDatabase.IsValidFolder(TreeFolder))
            {
                var trees = new List<GameObject>();
                foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { TreeFolder }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (System.IO.Path.GetFileName(path).StartsWith("Tree_", StringComparison.Ordinal))
                        trees.Add(AssetDatabase.LoadAssetAtPath<GameObject>(path));
                }
                trees.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                prefabs = trees.ToArray();
            }
            if (grassPrefab == null) grassPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(TreeFolder + "/Grass.prefab");
            if (mapRoot == null && Selection.activeGameObject != null) mapRoot = Selection.activeGameObject;
        }

        private void OnDisable()
        {
            if (preview != null) DestroyImmediate(preview);
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.HelpBox("Cây rải đều trên map, ngoài hình chiếu thẳng đứng của Plane. Cỏ chỉ nằm trong hình chiếu Plane. Cả hai bám mặt đất mesh/Terrain của map trong scene. Xem trước: xanh đậm = cây, xanh sáng = cỏ, nâu = vùng tránh thành.", MessageType.Info);
            EditorGUI.BeginChangeCheck();
            mapRoot = (GameObject)EditorGUILayout.ObjectField("Map trong scene", mapRoot, typeof(GameObject), true);
            plantingPlane = (GameObject)EditorGUILayout.ObjectField("Plane phân vùng cây / cỏ", plantingPlane, typeof(GameObject), true);
            blueCastle = (GameObject)EditorGUILayout.ObjectField("Thành xanh (tùy chọn tránh cây)", blueCastle, typeof(GameObject), true);
            redCastle = (GameObject)EditorGUILayout.ObjectField("Thành đỏ (tùy chọn tránh cây)", redCastle, typeof(GameObject), true);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Cây ngoài hình chiếu Plane", EditorStyles.boldLabel);
            spacing = Mathf.Max(1f, EditorGUILayout.FloatField("Khoảng cách cây", spacing));
            maxTrees = Mathf.Max(0, EditorGUILayout.IntField("Số cây tối đa", maxTrees));
            maxSlope = EditorGUILayout.Slider("Độ dốc tối đa của cây", maxSlope, 0f, 90f);
            scaleRange = ScaleField("Scale cây nhỏ / lớn", scaleRange);
            trunkRadius = Mathf.Max(0.05f, EditorGUILayout.FloatField("Bán kính gốc chặn đường", trunkRadius));
            castleClearance = Mathf.Max(0f, EditorGUILayout.FloatField("Khoảng tránh thành của cây", castleClearance));
            seed = EditorGUILayout.IntField("Seed", seed);
            showPrefabs = EditorGUILayout.Foldout(showPrefabs, $"Prefab cây ({prefabs.Length})", true);
            if (showPrefabs)
            {
                var count = Mathf.Clamp(EditorGUILayout.IntField("Số loại cây", prefabs.Length), 0, 128);
                if (count != prefabs.Length) Array.Resize(ref prefabs, count);
                for (var i = 0; i < prefabs.Length; i++)
                    prefabs[i] = (GameObject)EditorGUILayout.ObjectField($"Cây {i + 1}", prefabs[i], typeof(GameObject), false);
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Cỏ trong hình chiếu Plane", EditorStyles.boldLabel);
            grassPrefab = (GameObject)EditorGUILayout.ObjectField("Prefab cỏ", grassPrefab, typeof(GameObject), false);
            grassSpacing = Mathf.Max(0.1f, EditorGUILayout.FloatField("Khoảng cách cỏ", grassSpacing));
            maxGrass = Mathf.Max(0, EditorGUILayout.IntField("Số bụi cỏ tối đa", maxGrass));
            grassMaxSlope = EditorGUILayout.Slider("Độ dốc tối đa của cỏ", grassMaxSlope, 0f, 90f);
            grassScaleRange = ScaleField("Scale cỏ nhỏ / lớn", grassScaleRange);
            enableInstancing = EditorGUILayout.Toggle("Bật GPU instancing material", enableInstancing);
            if (EditorGUI.EndChangeCheck() && preview != null)
            {
                DestroyImmediate(preview);
                preview = null;
                status = "Thiết lập đã đổi; bấm xem trước để cập nhật.";
            }
            using (new EditorGUI.DisabledScope(operationQueued || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                using (new EditorGUI.DisabledScope(mapRoot == null || !mapRoot.scene.IsValid() || plantingPlane == null))
                {
                    if (GUILayout.Button("Xem trước vùng trồng")) Run(() => BuildPlan());
                    using (new EditorGUI.DisabledScope(mapRoot == null || !mapRoot.scene.IsValid()))
                    {
                        if (GUILayout.Button("Trồng / tạo lại cây và cỏ")) Run(() => Plant(BuildPlan()));
                    }
                }
                using (new EditorGUI.DisabledScope(mapRoot == null || !mapRoot.scene.IsValid()))
                {
                    meshChunkSize = EditorGUILayout.Slider("Kích thước ô instancing", meshChunkSize, 16f, 64f);
                    if (GUILayout.Button("Bake cây + cỏ bằng GPU instancing (giữ LOD)"))
                        Run(() => status = CongThanhForestInstancingBaker.Bake(mapRoot, meshChunkSize));
                }
            }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
            if (preview != null)
                GUI.DrawTexture(GUILayoutUtility.GetAspectRect(previewAspect), preview, ScaleMode.ScaleToFit);
            EditorGUILayout.EndScrollView();
        }

        private static Vector2 ScaleField(string label, Vector2 range)
        {
            range = EditorGUILayout.Vector2Field(label, range);
            range.x = Mathf.Max(0.01f, range.x);
            range.y = Mathf.Max(range.x, range.y);
            return range;
        }

        private void Run(Action action)
        {
            if (operationQueued) return;
            operationQueued = true;
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                try { action(); }
                catch (OperationCanceledException) { status = "Đã hủy thao tác."; }
                catch (InvalidOperationException e) { status = e.Message; Debug.LogWarning(e.Message); }
                catch (Exception e) { status = e.Message; Debug.LogException(e); }
                finally { operationQueued = false; EditorUtility.ClearProgressBar(); Repaint(); }
            };
        }

        private List<Placement> BuildPlan()
        {
            if (mapRoot == null || !mapRoot.scene.IsValid()) throw new InvalidOperationException("Chọn Map trong scene.");
            if (!mapRoot.activeInHierarchy) throw new InvalidOperationException("Bật Map trong Hierarchy trước khi lấy mặt đất.");
            if (plantingPlane == null || !plantingPlane.scene.IsValid())
                throw new InvalidOperationException("Chọn Plane trong scene để chia vùng cây và cỏ.");
            if (mapRoot != null && mapRoot.scene != plantingPlane.scene)
                throw new InvalidOperationException("Map và Plane phải ở cùng scene.");
            if (mapRoot == plantingPlane || mapRoot.transform.IsChildOf(plantingPlane.transform))
                throw new InvalidOperationException("Plane phân vùng phải là đối tượng riêng, không phải Map hoặc đối tượng cha của Map.");
            var available = new List<GameObject>();
            foreach (var prefab in prefabs) if (prefab != null) available.Add(prefab);
            if (maxTrees > 0 && available.Count == 0) throw new InvalidOperationException("Chọn ít nhất một prefab cây hoặc đặt số cây tối đa bằng 0.");
            if (maxGrass > 0 && grassPrefab == null) throw new InvalidOperationException("Chọn prefab cỏ hoặc đặt số bụi cỏ tối đa bằng 0.");
            prefabs = available.ToArray();
            CacheCastleBounds();
            var mask = new RecoilPlaneProjection(plantingPlane);
            using var surface = new RecoilForestPlantingSurface(mapRoot, plantingPlane, blueCastle, redCastle);
            DrawPreview(mask, surface);
            var plan = new List<Placement>();
            var random = new System.Random(seed);
            if (maxTrees > 0) AppendPlacements(plan, surface, mask, PlantRegion.Trees, spacing, random);
            ShuffleAndLimit(plan, maxTrees, random);
            var treeCount = plan.Count;
            var grass = new List<Placement>();
            // Independent random stream: tree density changes do not rearrange grass.
            var grassRandom = new System.Random(unchecked(seed + 104729));
            if (maxGrass > 0) AppendPlacements(grass, surface, mask, PlantRegion.Grass, grassSpacing, grassRandom);
            ShuffleAndLimit(grass, maxGrass, grassRandom);
            plan.AddRange(grass);
            status = $"Dự kiến {treeCount:N0} cây ngoài Plane và {grass.Count:N0} bụi cỏ trong Plane.";
            return plan;
        }

        private void AppendPlacements(List<Placement> plan, RecoilForestPlantingSurface surface, RecoilPlaneProjection mask, PlantRegion region,
            float step, System.Random random)
        {
            if (!float.IsFinite(step) || step <= 0f) throw new InvalidOperationException("Khoảng cách trồng phải lớn hơn 0.");
            var size = surface.Bounds.size;
            var cols = Mathf.CeilToInt(size.x / step);
            var rows = Mathf.CeilToInt(size.z / step);
            if ((long)cols * rows > 2000000) throw new InvalidOperationException("Quá 2 triệu điểm thử. Tăng khoảng cách cây / cỏ.");
            var isGrass = region == PlantRegion.Grass;
            for (var z = 0; z < rows; z++)
            {
                if (EditorUtility.DisplayCancelableProgressBar("Quét cây và cỏ", isGrass ? "Trong Plane" : "Ngoài Plane", (float)z / Mathf.Max(1, rows)))
                    throw new OperationCanceledException();
                for (var x = 0; x < cols; x++)
                {
                    // One lightly jittered point per cell gives even coverage, including partial edge cells.
                    var left = x * step; var bottom = z * step;
                    var width = Mathf.Min(step, size.x - left);
                    var depth = Mathf.Min(step, size.z - bottom);
                    var p = surface.Bounds.min + new Vector3(
                        left + width * (0.3f + (float)random.NextDouble() * 0.4f), 0f,
                        bottom + depth * (0.3f + (float)random.NextDouble() * 0.4f));
                    if (mask.Contains(p) != isGrass || NearCastle(p, isGrass ? 1f : castleClearance) ||
                        !surface.TrySample(p, out var height, out var normal)) continue;
                    if (Vector3.Angle(Vector3.up, normal) > (isGrass ? grassMaxSlope : maxSlope)) continue;
                    p.y = height;
                    var range = isGrass ? grassScaleRange : scaleRange;
                    plan.Add(new Placement { Position = p, Prefab = isGrass ? 0 : random.Next(prefabs.Length),
                        Scale = Mathf.Lerp(range.x, range.y, (float)random.NextDouble()),
                        Yaw = (float)random.NextDouble() * 360f, Region = region, Up = isGrass ? normal : Vector3.up });
                }
            }
        }

        private static void ShuffleAndLimit(List<Placement> plan, int limit, System.Random random)
        {
            for (var i = plan.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                var swap = plan[i]; plan[i] = plan[j]; plan[j] = swap;
            }
            if (plan.Count > limit) plan.RemoveRange(limit, plan.Count - limit);
        }

        private void CacheCastleBounds()
        {
            castleBounds.Clear();
            foreach (var castle in new[] { blueCastle, redCastle })
            {
                if (castle == null) continue;
                var renderers = castle.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) continue;
                var bounds = renderers[0].bounds;
                foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                castleBounds.Add(bounds);
            }
        }

        private bool NearCastle(Vector3 p, float clearance)
        {
            foreach (var b in castleBounds)
                if (p.x >= b.min.x - clearance && p.x <= b.max.x + clearance &&
                    p.z >= b.min.z - clearance && p.z <= b.max.z + clearance) return true;
            return false;
        }

        private void DrawPreview(RecoilPlaneProjection mask, RecoilForestPlantingSurface surface)
        {
            if (preview != null) DestroyImmediate(preview);
            preview = new Texture2D(256, 256) { hideFlags = HideFlags.HideAndDontSave };
            previewAspect = surface.Bounds.size.x / surface.Bounds.size.z;
            var pixels = new Color[256 * 256];
            for (var z = 0; z < 256; z++)
            for (var x = 0; x < 256; x++)
            {
                var p = surface.Bounds.min + new Vector3((x + 0.5f) / 256f * surface.Bounds.size.x, 0f,
                    (z + 0.5f) / 256f * surface.Bounds.size.z);
                var grass = mask.Contains(p);
                pixels[z * 256 + x] = !surface.TrySample(p, out _, out _) || NearCastle(p, grass ? 1f : castleClearance)
                    ? new Color(0.18f, 0.16f, 0.13f)
                    : grass ? new Color(0.4f, 0.8f, 0.3f) : new Color(0.1f, 0.4f, 0.15f);
            }
            preview.SetPixels(pixels); preview.Apply();
        }

        private void Plant(List<Placement> plan)
        {
            if (plan.Count == 0) throw new InvalidOperationException("Không có vị trí hợp lệ. Kiểm tra plane, khoảng cách hoặc độ dốc rồi xem trước lại.");
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Trồng cây và cỏ theo plane");
            var root = new GameObject("Map Forest");
            root.SetActive(false);
            Undo.RegisterCreatedObjectUndo(root, "Create Forest");
            root.transform.SetParent(mapRoot.transform, true);
            var area = root.AddComponent<RecoilForestArea>();
            var trunks = new List<RecoilForestArea.Trunk>();
            var treesRoot = new GameObject("Trees");
            treesRoot.transform.SetParent(root.transform, false);
            var grassRoot = new GameObject("Grass");
            grassRoot.transform.SetParent(root.transform, false);
            var materials = new Dictionary<Material, Material>();
            try
            {
                for (var i = 0; i < plan.Count; i++)
                {
                    if ((i & 63) == 0 && EditorUtility.DisplayCancelableProgressBar("Trồng rừng", $"{i:N0} / {plan.Count:N0} cây", (float)i / plan.Count))
                        throw new OperationCanceledException();
                    var p = plan[i];
                    var isGrass = p.Region == PlantRegion.Grass;
                    var tree = (GameObject)PrefabUtility.InstantiatePrefab(isGrass ? grassPrefab : prefabs[p.Prefab],
                        isGrass ? grassRoot.transform : treesRoot.transform);
                    tree.transform.SetPositionAndRotation(p.Position,
                        Quaternion.FromToRotation(Vector3.up, p.Up) * Quaternion.Euler(0f, p.Yaw, 0f) * tree.transform.rotation);
                    tree.transform.localScale *= p.Scale;
                    // Recoil uses the baked trunk occupancy rather than thousands of physics colliders.
                    foreach (var collider in tree.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
                    if (enableInstancing)
                        foreach (var renderer in tree.GetComponentsInChildren<Renderer>(true))
                        {
                            var slots = renderer.sharedMaterials;
                            for (var slot = 0; slot < slots.Length; slot++)
                                if (slots[slot] != null) slots[slot] = InstancedMaterial(slots[slot], materials);
                            renderer.sharedMaterials = slots;
                        }
                    if (isGrass) area.grassCount++;
                    else trunks.Add(new RecoilForestArea.Trunk
                    { LocalPosition = root.transform.InverseTransformPoint(p.Position), Radius = trunkRadius * p.Scale });
                }
            }
            catch
            {
                // Reverting creation discards the unfinished hierarchy without snapshotting it for redo.
                Undo.RevertAllDownToGroup(group);
                throw;
            }
            area.trunks = trunks.ToArray();
            // Replace only this tool's forest after generation completes, so cancelling preserves the old forest.
            foreach (var old in mapRoot.GetComponentsInChildren<RecoilForestArea>(true))
                if (old != area) CongThanhForestUndoUtility.DestroyHierarchy(old.gameObject);
            root.SetActive(true);
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(mapRoot.scene);
            Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = root;
            status = $"Đã trồng {area.TreeCount:N0} cây và {area.grassCount:N0} bụi cỏ. Lưu scene để giữ cây và cỏ.";
        }

        private static Material InstancedMaterial(Material source, Dictionary<Material, Material> cache)
        {
            if (cache.TryGetValue(source, out var result)) return result;
            const string folder = "Assets/Data/Environment/BowlForestMaterials";
            var parent = "Assets";
            foreach (var part in folder.Substring("Assets/".Length).Split('/'))
            {
                if (!AssetDatabase.IsValidFolder(parent + "/" + part)) AssetDatabase.CreateFolder(parent, part);
                parent += "/" + part;
            }
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
            if (string.IsNullOrEmpty(guid)) guid = source.GetInstanceID().ToString();
            var path = folder + "/" + guid + ".mat";
            result = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (result == null)
            {
                result = new Material(source) { name = source.name + " (Forest)", enableInstancing = true };
                AssetDatabase.CreateAsset(result, path);
            }
            cache.Add(source, result);
            return result;
        }

    }
}
