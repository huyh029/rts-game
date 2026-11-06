using System;
using System.Collections.Generic;
using System.IO;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameToolsEditor
{
    public sealed class CongThanhSetupWindow : EditorWindow
    {
        private const long MaxCells = 4_000_000;
        private const string DataFolder = "Assets/Data";
        private const string DefaultAssetName = "CongThanhMap";
        private static readonly Vector3 DefaultBattleCameraPosition =
            new Vector3(3.722656f, 205.2302f, -440.6f);
        private const float DefaultBattleCameraPitch = 16.387f;
        private const float DefaultBattleCameraYaw = 0f;

        private GameObject _mapRoot;
        [SerializeField] private MeshFilter _bakePlane;
        private GameObject _blueCastle;
        private GameObject _redCastle;
        private float _cellSize = 1f;
        [SerializeField] private float _maxSlopeDegrees = 45f;
        private string _assetName = DefaultAssetName;
        private string _result = string.Empty;
        private Vector2 _scrollPosition;
        [SerializeField] private GameObject[] _blueUnits = new GameObject[8];
        [SerializeField] private GameObject[] _redUnits = new GameObject[8];
        private static readonly string[] UnitLabels =
            { "Cung kỵ", "Đao binh", "Giáo binh", "Kỵ binh", "Máy bắn đá", "Quân sư", "Voi chiến", "Bộ binh" };

        private void OnEnable()
        {
            Array.Resize(ref _blueUnits, UnitLabels.Length);
            Array.Resize(ref _redUnits, UnitLabels.Length);
            var battle = FindSceneBattle(SceneManager.GetActiveScene());
            if (battle == null) return;
            for (var i = 0; i < UnitLabels.Length; i++)
            {
                _blueUnits[i] = battle.GetTemplateForType(true, i);
                _redUnits[i] = battle.GetTemplateForType(false, i);
            }
        }

        [MenuItem("Game Tools/Công Thành/Set Up")]
        private static void Open()
        {
            var window = GetWindow<CongThanhSetupWindow>();
            window.titleContent = new GUIContent("Công Thành Set Up");
            window.minSize = new Vector2(360f, 360f);
            window.Show();
        }

        private void OnGUI()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scrollPosition))
            {
                EditorGUILayout.LabelField("Công Thành map setup", EditorStyles.boldLabel);
                _mapRoot = (GameObject)EditorGUILayout.ObjectField("Map", _mapRoot, typeof(GameObject), true);
                _bakePlane = (MeshFilter)EditorGUILayout.ObjectField("Bake projection Plane", _bakePlane, typeof(MeshFilter), true);
                if (GUILayout.Button("Create bake projection Plane") && _mapRoot != null)
                {
                    var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    plane.name = "Bake Projection Plane";
                    Undo.RegisterCreatedObjectUndo(plane, "Create bake projection Plane");
                    if (TryGetWorldBounds(_mapRoot, out var mapBounds))
                    {
                        plane.transform.position = new Vector3(mapBounds.center.x, mapBounds.max.y + 1f, mapBounds.center.z);
                        plane.transform.localScale = new Vector3(mapBounds.size.x / 10f, 1f, mapBounds.size.z / 10f);
                    }
                    DestroyImmediate(plane.GetComponent<Collider>());
                    plane.GetComponent<Renderer>().enabled = false;
                    _bakePlane = plane.GetComponent<MeshFilter>();
                    Selection.activeGameObject = plane;
                    EditorSceneManager.MarkSceneDirty(plane.scene);
                }
                if (_bakePlane != null)
                    EditorGUILayout.HelpBox("Bake only the Plane XZ projection and castles. Keeps map heights; excludes forest obstacles. Move/scale/rotate Plane to select the region.", MessageType.Info);
                _blueCastle = (GameObject)EditorGUILayout.ObjectField("Thành xanh", _blueCastle, typeof(GameObject), true);
                _redCastle = (GameObject)EditorGUILayout.ObjectField("Thành đỏ", _redCastle, typeof(GameObject), true);

                EditorGUILayout.HelpBox(
                    "Map nhận MeshFilter, SkinnedMeshRenderer, MeshCollider và Terrain trong các object con. Mesh tắt Read/Write được lấy mẫu qua collider tạm mà không đổi import settings. Không dùng NavMesh.",
                    MessageType.Info);
                EditorGUILayout.HelpBox(
                    "Bake tạo đường đi riêng theo phe: quân đi xuyên thành đồng minh, thành địch là vật cản và có thể bị công kích. Bake lại sau khi đổi vị trí hoặc kích thước thành.",
                    MessageType.Info);
                _cellSize = EditorGUILayout.FloatField("Cell size (world units)", _cellSize);
                _maxSlopeDegrees = EditorGUILayout.Slider("Maximum walkable slope", _maxSlopeDegrees, 0f, 89f);
                _assetName = EditorGUILayout.TextField("Asset name", _assetName);

                var valid = ValidateInputs(out var error);
                if (!valid)
                    EditorGUILayout.HelpBox(error, MessageType.Warning);

                using (new EditorGUI.DisabledScope(!valid || EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button("Bake and save to Assets/Data", GUILayout.Height(32f)))
                        BakeAndSave();
                }

                DrawBattleUnits();
                var hasBakedMap = TryGetBakedMap(out var minimapMap);
                if (!hasBakedMap)
                    EditorGUILayout.HelpBox(
                        $"Bake '{DataFolder}/{_assetName}.asset' with both castle bounds before setting up the battle demo.",
                        MessageType.Info);
                using (new EditorGUI.DisabledScope(
                           !hasBakedMap || EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button("Thiết lập / cập nhật battle", GUILayout.Height(32f)))
                        SetupBattleScene();
                }
                var sceneBattle = FindSceneBattle(SceneManager.GetActiveScene());
                using (new EditorGUI.DisabledScope(
                           sceneBattle == null || !hasBakedMap ||
                           EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button("Tạo / cập nhật minimap", GUILayout.Height(28f)))
                        CreateOrUpdateMinimap(sceneBattle, minimapMap);
                }
                EditorGUILayout.HelpBox(
                    "Minimap hiển thị ở góc trên bên phải: chấm tròn xanh/đỏ là lính, biểu tượng SVG là thành.",
                    MessageType.None);
                EditorGUILayout.HelpBox(
                    "Khi chạy scene sẽ mở sảnh: chọn thời lượng 5/10/15/30 phút, chỉnh chỉ số trong Cài đặt, rồi bấm Bắt đầu. Mỗi phe tự triển khai 10 lính cho mỗi loại và trận bắt đầu sau khi đội hình ổn định. Trong trận, bảy nút thêm 10 lính cùng loại mỗi phe; phím 1/2/3 thêm đợt quân. Chuột phải xoay camera, chuột trái/giữa di chuyển, cuộn để zoom.",
                    MessageType.None);
                EditorGUILayout.HelpBox(
                    "Chỉ số trong Cài đặt áp dụng cho cả hai phe trong trận hiện tại; thay đổi này không ghi đè prefab.",
                    MessageType.None);

                if (!string.IsNullOrEmpty(_result))
                    EditorGUILayout.HelpBox(_result, MessageType.Info);
                _scrollPosition = scroll.scrollPosition;
            }
        }

        private static RecoilAutoBattleDemo FindSceneBattle(Scene scene)
        {
            foreach (var battle in UnityEngine.Object.FindObjectsByType<RecoilAutoBattleDemo>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (battle.gameObject.scene == scene) return battle;
            return null;
        }

        [MenuItem("Game Tools/Công Thành/Tạo / cập nhật minimap")]
        private static void CreateMinimapFromMenu()
        {
            var scene = SceneManager.GetActiveScene();
            var battle = FindSceneBattle(scene);
            if (battle == null)
            {
                EditorUtility.DisplayDialog("Tạo minimap", "Scene hiện tại chưa có battle.", "Đóng");
                return;
            }

            var mapData = battle.TerrainMap;
            if (mapData == null || !mapData.HasValidData || mapData.CastleBlockCount != 2)
            {
                EditorUtility.DisplayDialog("Tạo minimap",
                    "Battle cần terrain map hợp lệ có dữ liệu hai thành.", "Đóng");
                return;
            }
            CreateOrUpdateMinimap(battle, mapData);
        }

        [MenuItem("Game Tools/Công Thành/Tạo / cập nhật minimap", true)]
        private static bool ValidateCreateMinimapFromMenu() =>
            !EditorApplication.isPlayingOrWillChangePlaymode;

        private static void CreateOrUpdateMinimap(
            RecoilAutoBattleDemo battle, RecoilTerrainMapData mapData)
        {
            if (battle == null || mapData == null)
                throw new ArgumentNullException(battle == null ? nameof(battle) : nameof(mapData));

            const string iconPath = "Assets/Data/CongThanhCastleMinimap.png";
            AssetDatabase.ImportAsset(iconPath, ImportAssetOptions.ForceSynchronousImport);
            var castleIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
            if (castleIcon == null)
            {
                EditorUtility.DisplayDialog("Tạo minimap",
                    $"Không tìm thấy biểu tượng thành tại {iconPath}.", "Đóng");
                return;
            }

            var scene = battle.gameObject.scene;
            RecoilBattleMinimap minimap = null;
            foreach (var candidate in UnityEngine.Object.FindObjectsByType<RecoilBattleMinimap>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (candidate.gameObject.scene == scene)
                {
                    minimap = candidate;
                    break;
                }

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Create Công Thành Minimap");
            if (minimap == null)
                minimap = Undo.AddComponent<RecoilBattleMinimap>(battle.gameObject);

            Undo.RecordObject(minimap, "Configure Công Thành Minimap");
            var serialized = new SerializedObject(minimap);
            serialized.FindProperty("battle").objectReferenceValue = battle;
            serialized.FindProperty("castleIcon").objectReferenceValue = castleIcon;
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(minimap);
            EditorSceneManager.MarkSceneDirty(scene);
            Undo.CollapseUndoOperations(undoGroup);
            Selection.activeGameObject = battle.gameObject;
            EditorGUIUtility.PingObject(minimap);
        }

        private void DrawBattleUnits()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Danh sách lính cho battle", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Kéo đối tượng từ Hierarchy hoặc Project vào đúng loại và phe. Tên đối tượng có thể tùy ý. " +
                "Khi đổi ô, battle hiện tại được cập nhật ngay; không quét thư mục prefab. " +
                "Đối tượng trong scene được dùng làm mẫu và sẽ ẩn khi chạy.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                EditorGUI.BeginChangeCheck();
                for (var i = 0; i < UnitLabels.Length; i++)
                {
                    EditorGUILayout.LabelField(UnitLabels[i], EditorStyles.boldLabel);
                    _blueUnits[i] = (GameObject)EditorGUILayout.ObjectField("Phe xanh", _blueUnits[i], typeof(GameObject), true);
                    _redUnits[i] = (GameObject)EditorGUILayout.ObjectField("Phe đỏ", _redUnits[i], typeof(GameObject), true);
                }
                if (EditorGUI.EndChangeCheck())
                {
                    var battle = FindSceneBattle(SceneManager.GetActiveScene());
                    if (battle != null)
                    {
                        try
                        {
                            ValidateUnitTargets(battle.gameObject.scene);
                            ApplyUnitList(battle);
                            EditorSceneManager.MarkSceneDirty(battle.gameObject.scene);
                            _result = "Đã thay danh sách lính cho battle trong scene. Lưu scene để giữ cấu hình.";
                        }
                        catch (InvalidOperationException exception) { _result = exception.Message; }
                    }
                }
            }
        }

        private List<GameObject> GetAssignedUnits(bool blue)
        {
            var result = new List<GameObject>();
            foreach (var unit in blue ? _blueUnits : _redUnits)
                if (unit != null && !result.Contains(unit)) result.Add(unit);
            return result;
        }

        private void ApplyUnitList(RecoilAutoBattleDemo battle)
        {
            var blue = GetAssignedUnits(true);
            var red = GetAssignedUnits(false);
            var serialized = new SerializedObject(battle);
            SetPrefabArray(serialized.FindProperty("blueUnitsByType"), new List<GameObject>(_blueUnits));
            SetPrefabArray(serialized.FindProperty("redUnitsByType"), new List<GameObject>(_redUnits));
            SetPrefabArray(serialized.FindProperty("blueUnitVariants"), blue);
            SetPrefabArray(serialized.FindProperty("redUnitVariants"), red);
            serialized.FindProperty("blueUnitTemplate").objectReferenceValue = blue.Count > 0 ? blue[0] : null;
            serialized.FindProperty("redUnitTemplate").objectReferenceValue = red.Count > 0 ? red[0] : null;
            serialized.ApplyModifiedProperties();
        }

        private void ValidateUnitTargets(Scene scene)
        {
            var battle = FindSceneBattle(scene);
            foreach (var team in new[] { _blueUnits, _redUnits })
                foreach (var unit in team)
                {
                    if (unit == null) continue;
                    if (unit.scene.IsValid() && unit.scene != scene)
                        throw new InvalidOperationException("Mẫu lính trong Hierarchy phải thuộc scene battle đang mở.");
                    if (battle != null && (unit == battle.gameObject || battle.transform.IsChildOf(unit.transform)))
                        throw new InvalidOperationException("Không dùng controller battle hoặc object cha của battle làm mẫu lính.");
                }
        }

        private bool ValidateInputs(out string error)
        {
            if (_mapRoot == null || _blueCastle == null || _redCastle == null)
            {
                error = "Assign the map and both castles from the open scene hierarchy.";
                return false;
            }
            if (!_mapRoot.scene.IsValid() || !_blueCastle.scene.IsValid() || !_redCastle.scene.IsValid())
            {
                error = "Map and castles must be scene objects selected from the Hierarchy, not project assets.";
                return false;
            }
            if (_blueCastle == _redCastle)
            {
                error = "Blue and red castles must be different objects.";
                return false;
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                error = "Stop Play mode before baking.";
                return false;
            }
            if (!float.IsFinite(_cellSize) || _cellSize < 0.1f)
            {
                error = "Cell size must be a finite value of at least 0.1.";
                return false;
            }
            if (!float.IsFinite(_maxSlopeDegrees) || _maxSlopeDegrees < 0f || _maxSlopeDegrees >= 90f)
            {
                error = "Maximum walkable slope must be between 0 and 89 degrees.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(_assetName) || _assetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                _assetName.IndexOf('/') >= 0 || _assetName.IndexOf('\\') >= 0)
            {
                error = "Enter a valid asset name without a path.";
                return false;
            }
            if (!HasMapGeometry(_mapRoot))
            {
                error = "The Map hierarchy must contain a mesh or Terrain surface.";
                return false;
            }
            if (!TryGetWorldBounds(_blueCastle, out _) || !TryGetWorldBounds(_redCastle, out _))
            {
                error = "Both castle hierarchies need a Renderer, Collider, or mesh with non-zero bounds.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool HasMapGeometry(GameObject root)
        {
            return root.GetComponentInChildren<MeshFilter>(true) != null ||
                   root.GetComponentInChildren<SkinnedMeshRenderer>(true) != null ||
                   root.GetComponentInChildren<MeshCollider>(true) != null ||
                   root.GetComponentInChildren<Terrain>(true) != null;
        }

        private bool TryGetBakedMap(out RecoilTerrainMapData mapData)
        {
            mapData = null;
            if (string.IsNullOrWhiteSpace(_assetName) ||
                _assetName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                _assetName.IndexOf('/') >= 0 || _assetName.IndexOf('\\') >= 0)
                return false;

            mapData = AssetDatabase.LoadAssetAtPath<RecoilTerrainMapData>(
                $"{DataFolder}/{_assetName}.asset");
            return mapData != null && mapData.HasValidData && mapData.CastleBlockCount == 2;
        }

        private void SetupBattleScene()
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Set Up Công Thành Battle Scene");
            try
            {
                if (!TryGetBakedMap(out var mapData))
                    throw new InvalidOperationException(
                        $"'{DataFolder}/{_assetName}.asset' must contain valid baked map data and both castles.");
                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded)
                    throw new InvalidOperationException("There is no active, loaded scene.");
                ValidateUnitTargets(scene);

                var blueCharacterPrefabs = GetAssignedUnits(true);
                var redCharacterPrefabs = GetAssignedUnits(false);
                if (blueCharacterPrefabs.Count == 0 || redCharacterPrefabs.Count == 0)
                    throw new InvalidOperationException(
                        "Hãy gán ít nhất một đối tượng lính cho mỗi phe trong danh sách battle.");

                var pathService = FindScenePathService(scene);
                if (pathService == null)
                {
                    var serviceObject = new GameObject("Recoil Pathfinding Service");
                    Undo.RegisterCreatedObjectUndo(serviceObject, "Create Recoil Pathfinding Service");
                    SceneManager.MoveGameObjectToScene(serviceObject, scene);
                    pathService = Undo.AddComponent<RecoilNavMeshPathService>(serviceObject);
                }
                var serializedService = new SerializedObject(pathService);
                serializedService.FindProperty("terrainMap").objectReferenceValue = mapData;
                serializedService.FindProperty("useProjectTerrainMap").boolValue = false;
                serializedService.ApplyModifiedProperties();
                EditorUtility.SetDirty(pathService);

                var battle = FindSceneBattle(scene);
                var demoRoot = battle != null ? battle.gameObject : new GameObject("Cong Thanh Battle Demo");
                if (battle == null)
                {
                    Undo.RegisterCreatedObjectUndo(demoRoot, "Create Công Thành Battle Demo");
                    SceneManager.MoveGameObjectToScene(demoRoot, scene);
                }

                var blueBounds = GetCastleBounds(mapData, RecoilCastleTeam.Blue);
                var redBounds = GetCastleBounds(mapData, RecoilCastleTeam.Red);
                var blueSpawn = CreateSpawnTarget(demoRoot.transform, "Blue Spawn Target", blueBounds.center,
                    mapData, RecoilCastleTeam.Blue);
                var redSpawn = CreateSpawnTarget(demoRoot.transform, "Red Spawn Target", redBounds.center,
                    mapData, RecoilCastleTeam.Red);
                var center = GetMapCenter(mapData, blueBounds, redBounds);

                if (battle == null) battle = Undo.AddComponent<RecoilAutoBattleDemo>(demoRoot);
                var serializedBattle = new SerializedObject(battle);
                serializedBattle.FindProperty("terrainMap").objectReferenceValue = mapData;
                serializedBattle.FindProperty("pathService").objectReferenceValue = pathService;
                serializedBattle.FindProperty("instancedRenderer").objectReferenceValue = null;
                serializedBattle.FindProperty("blueUnitTemplate").objectReferenceValue = blueCharacterPrefabs[0];
                serializedBattle.FindProperty("redUnitTemplate").objectReferenceValue = redCharacterPrefabs[0];
                SetPrefabArray(
                    serializedBattle.FindProperty("blueUnitVariants"), blueCharacterPrefabs);
                SetPrefabArray(
                    serializedBattle.FindProperty("redUnitVariants"), redCharacterPrefabs);
                serializedBattle.FindProperty("unitsPerVariantAtStart").intValue = 50;
                serializedBattle.FindProperty("blueSpawnTarget").objectReferenceValue = blueSpawn;
                serializedBattle.FindProperty("redSpawnTarget").objectReferenceValue = redSpawn;
                serializedBattle.FindProperty("battleCenter").vector3Value = center;
                serializedBattle.FindProperty("teamStartDistance").floatValue =
                    Mathf.Max(1f, Vector3.Distance(blueSpawn.position, redSpawn.position) * 0.5f);
                serializedBattle.FindProperty("spawnSpacing").floatValue = 1.8f;
                serializedBattle.ApplyModifiedProperties();
                ApplyUnitList(battle);
                if (demoRoot.GetComponent<RecoilBattlePerformanceMonitor>() == null)
                    Undo.AddComponent<RecoilBattlePerformanceMonitor>(demoRoot);

                SetupDemoCamera(scene, center, mapData);
                EditorSceneManager.MarkSceneDirty(scene);
                Selection.activeGameObject = demoRoot;
                EditorGUIUtility.PingObject(demoRoot);
                _result =
                    $"Đã cập nhật battle với {blueCharacterPrefabs.Count} mẫu lính xanh và {redCharacterPrefabs.Count} mẫu lính đỏ. Nhấn L để triển khai 50 lính mỗi mẫu.";
                Debug.Log(_result, demoRoot);
                Undo.CollapseUndoOperations(group);
            }
            catch (Exception exception)
            {
                Undo.RevertAllDownToGroup(group);
                _result = $"Battle scene setup failed: {exception.Message}";
                Debug.LogException(exception);
            }
        }

        private static RecoilNavMeshPathService FindScenePathService(Scene scene)
        {
            foreach (var service in UnityEngine.Object.FindObjectsByType<RecoilNavMeshPathService>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (service.gameObject.scene == scene)
                    return service;
            }
            return null;
        }

        private static GameObject FindSceneDemoRoot(Scene scene, GameObject except)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root != except &&
                    (root.name == "Cong Thanh Battle Demo" ||
                     root.name == "Pathfinding Capsule Soldiers Demo"))
                    return root;
            }
            return null;
        }

        private static void SetPrefabArray(SerializedProperty property, List<GameObject> prefabs)
        {
            if (property == null || !property.isArray)
                throw new InvalidOperationException("Battle setup is missing a character prefab array.");
            property.arraySize = prefabs.Count;
            for (var i = 0; i < prefabs.Count; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue = prefabs[i];
        }

        private static Transform CreateSpawnTarget(
            Transform parent,
            string objectName,
            Vector3 position,
            RecoilTerrainMapData mapData,
            RecoilCastleTeam team)
        {
            position = FindNearestWalkablePosition(position, mapData, team);
            var existing = parent.Find(objectName);
            var target = existing != null ? existing.gameObject : new GameObject(objectName);
            if (existing == null)
            {
                Undo.RegisterCreatedObjectUndo(target, $"Create {objectName}");
                target.transform.SetParent(parent, true);
            }
            else Undo.RecordObject(target.transform, "Cập nhật điểm sinh quân");
            target.transform.position = position;
            return target.transform;
        }

        private static Vector3 FindNearestWalkablePosition(
            Vector3 position,
            RecoilTerrainMapData mapData,
            RecoilCastleTeam team)
        {
            var cellSize = mapData.CellSize;
            var centerX = Mathf.Clamp(
                Mathf.FloorToInt((position.x - mapData.WorldOrigin.x) / cellSize.x),
                0, mapData.Columns - 2);
            var centerZ = Mathf.Clamp(
                Mathf.FloorToInt((position.z - mapData.WorldOrigin.z) / cellSize.y),
                0, mapData.Rows - 2);
            var maxRing = Mathf.Max(mapData.Columns, mapData.Rows);
            for (var ring = 0; ring < maxRing; ring++)
            {
                var nearestDistance = float.PositiveInfinity;
                var nearest = Vector3.zero;
                var found = false;
                for (var dz = -ring; dz <= ring; dz++)
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) != ring)
                        continue;
                    var cellX = centerX + dx;
                    var cellZ = centerZ + dz;
                    if (!mapData.IsWalkableForTeam(cellX, cellZ, team))
                        continue;
                    var candidate = new Vector3(
                        mapData.WorldOrigin.x + (cellX + 0.5f) * cellSize.x,
                        0f,
                        mapData.WorldOrigin.z + (cellZ + 0.5f) * cellSize.y);
                    if (!mapData.TrySample(candidate, out var height, out _))
                        continue;
                    candidate.y = height;
                    var distance = (candidate - position).sqrMagnitude;
                    if (distance >= nearestDistance)
                        continue;
                    nearestDistance = distance;
                    nearest = candidate;
                    found = true;
                }
                if (found)
                    return nearest;
            }
            throw new InvalidOperationException(
                $"Could not find a walkable spawn cell near '{position}'. Check the baked map data and castle bounds.");
        }

        private static Bounds GetCastleBounds(RecoilTerrainMapData mapData, RecoilCastleTeam team)
        {
            for (var i = 0; i < mapData.CastleBlockCount; i++)
            {
                var castle = mapData.GetCastleBlock(i);
                if (castle.Team == team)
                    return castle.WorldBounds;
            }
            throw new InvalidOperationException($"Baked map data is missing the {team} castle.");
        }

        private static Vector3 GetMapCenter(
            RecoilTerrainMapData mapData,
            Bounds blueCastle,
            Bounds redCastle)
        {
            var center = (blueCastle.center + redCastle.center) * 0.5f;
            if (mapData.TrySample(center, out var height, out _))
                center.y = height;
            return center;
        }

        private static void SetupDemoCamera(
            Scene scene,
            Vector3 center,
            RecoilTerrainMapData mapData)
        {
            Camera camera = null;
            foreach (var candidate in UnityEngine.Object.FindObjectsByType<Camera>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (candidate.gameObject.scene == scene && candidate.CompareTag("MainCamera"))
                {
                    camera = candidate;
                    break;
                }
                if (camera == null && candidate.gameObject.scene == scene)
                    camera = candidate;
            }
            if (camera == null)
            {
                Debug.LogWarning("No Camera was found in the active scene; battle setup completed without camera framing.");
                return;
            }

            Undo.RecordObject(camera.transform, "Frame Công Thành Battle");
            Undo.RecordObject(camera, "Set Công Thành Camera Clipping");
            var controller = camera.GetComponent<RecoilDemoCameraController>();
            if (controller == null)
                controller = Undo.AddComponent<RecoilDemoCameraController>(camera.gameObject);
            var serializedController = new SerializedObject(controller);
            serializedController.FindProperty("terrainMap").objectReferenceValue = mapData;
            serializedController.FindProperty("minHeightAboveMap").floatValue = 10f;
            serializedController.FindProperty("maxHeightAboveMap").floatValue = 200f;
            serializedController.FindProperty("pitchDegrees").floatValue =
                DefaultBattleCameraPitch;
            serializedController.FindProperty("yawDegrees").floatValue =
                DefaultBattleCameraYaw;
            serializedController.ApplyModifiedProperties();

            camera.transform.SetPositionAndRotation(
                DefaultBattleCameraPosition,
                Quaternion.Euler(DefaultBattleCameraPitch, DefaultBattleCameraYaw, 0f));
            camera.transform.localScale = Vector3.one;
            camera.farClipPlane = 1000f;
            EditorUtility.SetDirty(camera);
            EditorUtility.SetDirty(controller);
        }

        private void BakeAndSave()
        {
            RecoilTerrainMapData baked = null;
            try
            {
                baked = Bake(_mapRoot, _blueCastle, _redCastle, _cellSize, progress =>
                    EditorUtility.DisplayCancelableProgressBar(
                        "Công Thành map bake",
                        "Sampling heightfield, walkability and castle footprints",
                        progress), _maxSlopeDegrees, _bakePlane);
                EnsureDataFolder();
                var path = $"{DataFolder}/{_assetName}.asset";
                var existing = AssetDatabase.LoadMainAssetAtPath(path);
                if (existing != null && !(existing is RecoilTerrainMapData))
                    throw new InvalidOperationException($"'{path}' exists and is not a Recoil terrain map asset.");

                if (existing != null)
                {
                    Undo.RecordObject(existing, "Bake Công Thành map");
                    EditorUtility.CopySerialized(baked, existing);
                    EditorUtility.SetDirty(existing);
                    DestroyImmediate(baked);
                    baked = null;
                }
                else
                {
                    AssetDatabase.CreateAsset(baked, path);
                    baked = null;
                }

                AssetDatabase.SaveAssets();
                var saved = AssetDatabase.LoadAssetAtPath<RecoilTerrainMapData>(path);
                Selection.activeObject = saved;
                EditorGUIUtility.PingObject(saved);
                _result = $"Saved reusable map data: {path}";
                Debug.Log(_result, saved);
            }
            catch (OperationCanceledException)
            {
                _result = "Bake cancelled; no asset was changed.";
            }
            catch (Exception exception)
            {
                _result = $"Bake failed: {exception.Message}";
                Debug.LogException(exception);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (baked != null)
                    DestroyImmediate(baked);
            }
        }

        public static RecoilTerrainMapData Bake(
            GameObject mapRoot,
            GameObject blueCastle,
            GameObject redCastle,
            float cellSize,
            Func<float, bool> cancel = null,
            float maxSlopeDegrees = 45f,
            MeshFilter bakePlane = null)
        {
            if (mapRoot == null || blueCastle == null || redCastle == null ||
                blueCastle == redCastle || !float.IsFinite(cellSize) || cellSize < 0.1f ||
                !float.IsFinite(maxSlopeDegrees) || maxSlopeDegrees < 0f || maxSlopeDegrees >= 90f)
                throw new ArgumentException("Select a map, two different castles, a valid cell size and slope limit.");
            if (!mapRoot.scene.IsValid() || !blueCastle.scene.IsValid() || !redCastle.scene.IsValid())
                throw new ArgumentException("Map and castles must be scene objects from the Hierarchy.");

            var triangles = new List<SurfaceTriangle>();
            var unreadableMeshes = new List<PendingCollider>();
            var terrains = new List<Terrain>();
            var bounds = new Bounds();
            var hasBounds = false;
            CollectMapGeometry(mapRoot, blueCastle, redCastle,
                triangles, unreadableMeshes, terrains, ref bounds, ref hasBounds, bakePlane != null ? bakePlane.transform : null);
            if (!hasBounds || bounds.size.x <= 0f || bounds.size.z <= 0f)
                throw new InvalidOperationException("The selected Map has no usable horizontal surface bounds.");

            var projectionTriangles = new List<SurfaceTriangle>();
            if (bakePlane != null)
            {
                if (!bakePlane.gameObject.scene.IsValid() || bakePlane.sharedMesh == null)
                    throw new ArgumentException("Select a scene Plane with a mesh.");
                var projectionBounds = new Bounds();
                var hasProjectionBounds = false;
                AddMesh(bakePlane.sharedMesh, bakePlane.transform, projectionTriangles, ref projectionBounds, ref hasProjectionBounds);
                var min = Vector3.Max(bounds.min, projectionBounds.min);
                var max = Vector3.Min(bounds.max, projectionBounds.max);
                min.y = bounds.min.y;
                max.y = bounds.max.y;
                if (!hasProjectionBounds || projectionTriangles.Count == 0 || max.x <= min.x || max.z <= min.z)
                    throw new InvalidOperationException("Plane projection does not overlap the map or is vertical.");
                bounds.SetMinMax(min, max);
            }

            if (!TryGetWorldBounds(blueCastle, out var blueBounds) ||
                !TryGetWorldBounds(redCastle, out var redBounds))
                throw new InvalidOperationException("Could not determine world bounds for both castles.");
            if (!OverlapsFootprint(blueBounds.min.x, blueBounds.max.x, blueBounds.min.z, blueBounds.max.z, bounds) ||
                !OverlapsFootprint(redBounds.min.x, redBounds.max.x, redBounds.min.z, redBounds.max.z, bounds))
                throw new InvalidOperationException("Both castle footprints must overlap the baked map bounds.");

            var cellColumns = Math.Ceiling(bounds.size.x / cellSize);
            var cellRows = Math.Ceiling(bounds.size.z / cellSize);
            var vertexCount = (cellColumns + 1) * (cellRows + 1);
            if (cellColumns < 1 || cellRows < 1 || vertexCount > MaxCells)
                throw new InvalidOperationException("Map grid exceeds 4 million cells. Increase cell size.");

            var columns = (int)cellColumns + 1;
            var rows = (int)cellRows + 1;
            var stepX = bounds.size.x / (columns - 1);
            var stepZ = bounds.size.z / (rows - 1);
            var heights = new float[columns * rows];
            var sampled = new bool[heights.Length];

            SampleUnreadableMeshes(
                unreadableMeshes, bounds, stepX, stepZ, columns, rows, heights, sampled, cancel);

            for (var i = 0; i < triangles.Count; i++)
            {
                if ((i & 255) == 0 && cancel != null && cancel(0.35f * i / Mathf.Max(1, triangles.Count)))
                    throw new OperationCanceledException();
                var triangle = triangles[i];
                RasterizeTriangle(triangle, bounds.min, stepX, stepZ, columns, rows, heights, sampled);
            }

            for (var z = 0; z < rows; z++)
            {
                if ((z & 31) == 0 && cancel != null &&
                    cancel(0.35f + 0.3f * z / Mathf.Max(1, rows)))
                    throw new OperationCanceledException();
                var worldZ = bounds.min.z + z * stepZ;
                for (var x = 0; x < columns; x++)
                {
                    var worldX = bounds.min.x + x * stepX;
                    foreach (var terrain in terrains)
                    {
                        var terrainPosition = terrain.GetPosition();
                        var terrainSize = terrain.terrainData.size;
                        var u = (worldX - terrainPosition.x) / terrainSize.x;
                        var v = (worldZ - terrainPosition.z) / terrainSize.z;
                        if (u < 0f || u > 1f || v < 0f || v > 1f)
                            continue;
                        var height = terrainPosition.y + terrain.terrainData.GetInterpolatedHeight(u, v);
                        SetHighestHeight(x, z, height, columns, heights, sampled);
                    }
                }
            }

            if (bakePlane != null)
            {
                var projectionMask = new bool[heights.Length];
                var unusedHeights = new float[heights.Length];
                foreach (var triangle in projectionTriangles)
                    RasterizeTriangle(triangle, bounds.min, stepX, stepZ, columns, rows, unusedHeights, projectionMask);
                for (var i = 0; i < sampled.Length; i++)
                    sampled[i] &= projectionMask[i];
            }
            var fallbackHeight = bounds.min.y;
            for (var i = 0; i < heights.Length; i++)
                if (!sampled[i])
                    heights[i] = fallbackHeight;

            var cells = new byte[(columns - 1) * (rows - 1)];
            for (var z = 0; z < rows - 1; z++)
            {
                if ((z & 31) == 0 && cancel != null &&
                    cancel(0.65f + 0.35f * z / Mathf.Max(1, rows - 1)))
                    throw new OperationCanceledException();
                for (var x = 0; x < columns - 1; x++)
                {
                    var i = z * columns + x;
                    var hasSurface = sampled[i] && sampled[i + 1] &&
                                     sampled[i + columns] && sampled[i + columns + 1];
                    var h00 = heights[i];
                    var h10 = heights[i + 1];
                    var h01 = heights[i + columns];
                    var h11 = heights[i + columns + 1];
                    var slopeX = ((h10 + h11) - (h00 + h01)) * 0.5f / stepX;
                    var slopeZ = ((h01 + h11) - (h00 + h10)) * 0.5f / stepZ;
                    var slopeDegrees = Mathf.Atan(Mathf.Sqrt(slopeX * slopeX + slopeZ * slopeZ)) * Mathf.Rad2Deg;
                    // Castle cells are team-dependent: friendly forces may cross their own
                    // castle, while the opposing castle is a siege obstacle. Store base terrain
                    // here; RecoilNavMeshPathService applies the opposing castle mask per team.
                    cells[z * (columns - 1) + x] = (byte)(hasSurface && slopeDegrees <= maxSlopeDegrees ? 1 : 0);
                }
            }

            if (bakePlane == null)
                CongThanhForestBakeUtility.Apply(mapRoot, bounds.min, stepX, stepZ, columns, rows, cells);
            var data = CreateInstance<RecoilTerrainMapData>();
            try
            {
                data.SetBakedData(
                    new Vector3(bounds.min.x, 0f, bounds.min.z),
                    new Vector2(bounds.size.x, bounds.size.z),
                    columns,
                    rows,
                    maxSlopeDegrees,
                    heights,
                    cells);
                data.SetCastleBlocks(new[]
                {
                    new RecoilCastleBlockData(RecoilCastleTeam.Blue, blueBounds),
                    new RecoilCastleBlockData(RecoilCastleTeam.Red, redBounds)
                });
            }
            catch
            {
                DestroyImmediate(data);
                throw;
            }
            return data;
        }

        private static void CollectMapGeometry(
            GameObject root,
            GameObject blueCastle,
            GameObject redCastle,
            List<SurfaceTriangle> triangles,
            List<PendingCollider> unreadableMeshes,
            List<Terrain> terrains,
            ref Bounds bounds,
            ref bool hasBounds,
            Transform excludedProjection = null)
        {
            var meshFilters = root.GetComponentsInChildren<MeshFilter>(true);
            foreach (var filter in meshFilters)
            {
                if (filter.sharedMesh == null || IsCastleGeometry(filter.transform, blueCastle, redCastle) || (excludedProjection != null && (filter.transform == excludedProjection || filter.transform.IsChildOf(excludedProjection))) ||
                    CongThanhForestBakeUtility.IsForestGeometry(filter.transform))
                    continue;
                if (filter.sharedMesh.isReadable)
                    AddMesh(filter.sharedMesh, filter.transform, triangles, ref bounds, ref hasBounds);
                else
                {
                    unreadableMeshes.Add(new PendingCollider(filter.sharedMesh, filter.transform));
                    EncapsulateTransformedBounds(
                        filter.sharedMesh.bounds, filter.transform, ref bounds, ref hasBounds);
                }
            }

            var skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var renderer in skinnedRenderers)
            {
                if (renderer.sharedMesh == null || IsCastleGeometry(renderer.transform, blueCastle, redCastle) || (excludedProjection != null && (renderer.transform == excludedProjection || renderer.transform.IsChildOf(excludedProjection))) ||
                    CongThanhForestBakeUtility.IsForestGeometry(renderer.transform))
                    continue;
                var bakedMesh = new Mesh();
                try
                {
                    renderer.BakeMesh(bakedMesh);
                    AddMesh(bakedMesh, renderer.transform, triangles, ref bounds, ref hasBounds);
                }
                finally
                {
                    DestroyImmediate(bakedMesh);
                }
            }

            var meshColliders = root.GetComponentsInChildren<MeshCollider>(true);
            foreach (var meshCollider in meshColliders)
            {
                if (meshCollider.sharedMesh == null || IsCastleGeometry(meshCollider.transform, blueCastle, redCastle) || (excludedProjection != null && (meshCollider.transform == excludedProjection || meshCollider.transform.IsChildOf(excludedProjection))) ||
                    CongThanhForestBakeUtility.IsForestGeometry(meshCollider.transform))
                    continue;
                var filter = meshCollider.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh == meshCollider.sharedMesh)
                    continue;
                if (meshCollider.sharedMesh.isReadable)
                    AddMesh(meshCollider.sharedMesh, meshCollider.transform, triangles, ref bounds, ref hasBounds);
                else
                {
                    unreadableMeshes.Add(new PendingCollider(meshCollider.sharedMesh, meshCollider.transform));
                    EncapsulateTransformedBounds(
                        meshCollider.sharedMesh.bounds, meshCollider.transform, ref bounds, ref hasBounds);
                }
            }

            var terrainComponents = root.GetComponentsInChildren<Terrain>(true);
            foreach (var terrain in terrainComponents)
            {
                if (terrain.terrainData == null || IsCastleGeometry(terrain.transform, blueCastle, redCastle) || (excludedProjection != null && (terrain.transform == excludedProjection || terrain.transform.IsChildOf(excludedProjection))))
                    continue;
                if (Quaternion.Angle(terrain.transform.rotation, Quaternion.identity) > 0.01f ||
                    Vector3.Distance(terrain.transform.lossyScale, Vector3.one) > 0.001f)
                    throw new InvalidOperationException("Terrain sources must have identity rotation and unit world scale.");
                terrains.Add(terrain);
                var position = terrain.GetPosition();
                var size = terrain.terrainData.size;
                EncapsulateBounds(new Bounds(position + size * 0.5f, size), ref bounds, ref hasBounds);
            }

            if (triangles.Count == 0 && terrains.Count == 0)
            {
                if (unreadableMeshes.Count == 0)
                    throw new InvalidOperationException("The selected Map contains no supported mesh or Terrain data.");
            }
        }

        private static bool IsCastleGeometry(Transform candidate, GameObject blueCastle, GameObject redCastle) =>
            candidate == blueCastle.transform || candidate.IsChildOf(blueCastle.transform) ||
            candidate == redCastle.transform || candidate.IsChildOf(redCastle.transform);

        private static void SampleUnreadableMeshes(
            List<PendingCollider> meshes,
            Bounds mapBounds,
            float stepX,
            float stepZ,
            int columns,
            int rows,
            float[] heights,
            bool[] sampled,
            Func<float, bool> cancel)
        {
            if (meshes.Count == 0)
                return;

            var temporaryObjects = new List<GameObject>(meshes.Count);
            try
            {
                const int temporaryLayer = 31;
                var layerMask = 1 << temporaryLayer;
                foreach (var source in meshes)
                {
                    if (!source.Transform.gameObject.activeInHierarchy)
                        throw new InvalidOperationException(
                            $"Mesh '{source.Mesh.name}' is inactive. Activate it before baking.");
                    var temporaryObject = new GameObject("Temporary unreadable mesh bake collider")
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                        layer = temporaryLayer
                    };
                    temporaryObjects.Add(temporaryObject);
                    temporaryObject.transform.SetParent(source.Transform, false);
                    var collider = temporaryObject.AddComponent<MeshCollider>();
                    collider.sharedMesh = source.Mesh;
                    if (collider.sharedMesh == null)
                        throw new InvalidOperationException(
                            $"Could not create a temporary collider for unreadable mesh '{source.Mesh.name}'.");
                }

                Physics.SyncTransforms();
                var rayStartOffset = Mathf.Max(1f, mapBounds.size.y * 0.01f);
                var rayDistance = mapBounds.size.y + rayStartOffset + 1f;
                if (!float.IsFinite(rayDistance))
                    throw new InvalidOperationException("The map is too large to sample safely.");

                for (var z = 0; z < rows; z++)
                {
                    if ((z & 31) == 0 && cancel != null &&
                        cancel(0.35f * z / Mathf.Max(1, rows)))
                        throw new OperationCanceledException();

                    var worldZ = mapBounds.min.z + z * stepZ;
                    for (var x = 0; x < columns; x++)
                    {
                        var worldX = mapBounds.min.x + x * stepX;
                        var origin = new Vector3(worldX, mapBounds.max.y + rayStartOffset, worldZ);
                        if (!Physics.Raycast(
                                origin, Vector3.down, out var hit, rayDistance, layerMask, QueryTriggerInteraction.Ignore))
                            continue;
                        SetHighestHeight(x, z, hit.point.y, columns, heights, sampled);
                    }
                }
            }
            finally
            {
                foreach (var temporaryObject in temporaryObjects)
                    if (temporaryObject != null)
                        DestroyImmediate(temporaryObject);
                Physics.SyncTransforms();
            }
        }

        private static void AddMesh(
            Mesh mesh,
            Transform meshTransform,
            List<SurfaceTriangle> triangles,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            if (!mesh.isReadable)
                throw new InvalidOperationException(
                    $"Mesh '{mesh.name}' is not readable. Enable Read/Write in its import settings before baking.");
            var vertices = mesh.vertices;
            var indices = mesh.triangles;
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var ia = indices[i];
                var ib = indices[i + 1];
                var ic = indices[i + 2];
                if (ia < 0 || ib < 0 || ic < 0 ||
                    ia >= vertices.Length || ib >= vertices.Length || ic >= vertices.Length)
                    continue;

                var a = meshTransform.TransformPoint(vertices[ia]);
                var b = meshTransform.TransformPoint(vertices[ib]);
                var c = meshTransform.TransformPoint(vertices[ic]);
                if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c))
                    continue;

                var triangle = new SurfaceTriangle(a, b, c);
                if (Mathf.Abs(triangle.Denominator) <= 0.000001f)
                    continue;
                triangles.Add(triangle);
                EncapsulateBounds(a, ref bounds, ref hasBounds);
                EncapsulateBounds(b, ref bounds, ref hasBounds);
                EncapsulateBounds(c, ref bounds, ref hasBounds);
            }
        }

        private static void RasterizeTriangle(
            SurfaceTriangle triangle,
            Vector3 origin,
            float stepX,
            float stepZ,
            int columns,
            int rows,
            float[] heights,
            bool[] sampled)
        {
            var minX = Mathf.Max(0, Mathf.FloorToInt((triangle.MinX - origin.x) / stepX));
            var maxX = Mathf.Min(columns - 1, Mathf.CeilToInt((triangle.MaxX - origin.x) / stepX));
            var minZ = Mathf.Max(0, Mathf.FloorToInt((triangle.MinZ - origin.z) / stepZ));
            var maxZ = Mathf.Min(rows - 1, Mathf.CeilToInt((triangle.MaxZ - origin.z) / stepZ));

            for (var z = minZ; z <= maxZ; z++)
            {
                var worldZ = origin.z + z * stepZ;
                for (var x = minX; x <= maxX; x++)
                {
                    var worldX = origin.x + x * stepX;
                    if (triangle.TryGetHeight(worldX, worldZ, out var height))
                        SetHighestHeight(x, z, height, columns, heights, sampled);
                }
            }
        }

        private static void SetHighestHeight(
            int x,
            int z,
            float height,
            int columns,
            float[] heights,
            bool[] sampled)
        {
            var index = z * columns + x;
            if (!sampled[index] || height > heights[index])
            {
                heights[index] = height;
                sampled[index] = true;
            }
        }

        private static bool OverlapsFootprint(
            float minX,
            float maxX,
            float minZ,
            float maxZ,
            Bounds bounds)
        {
            return maxX > bounds.min.x && minX < bounds.max.x &&
                   maxZ > bounds.min.z && minZ < bounds.max.z;
        }

        private static bool TryGetWorldBounds(GameObject root, out Bounds bounds)
        {
            bounds = default;
            var hasBounds = false;

            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                if (!collider.enabled || collider.isTrigger || !collider.gameObject.activeInHierarchy)
                    continue;
                EncapsulateBounds(collider.bounds, ref bounds, ref hasBounds);
            }
            if (hasBounds)
                return bounds.size.x > 0f && bounds.size.y > 0f && bounds.size.z > 0f;

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                if (renderer.enabled && renderer.gameObject.activeInHierarchy)
                    EncapsulateBounds(renderer.bounds, ref bounds, ref hasBounds);
            foreach (var terrain in root.GetComponentsInChildren<Terrain>(true))
                if (terrain.enabled && terrain.gameObject.activeInHierarchy && terrain.terrainData != null)
                    EncapsulateTransformedBounds(
                        new Bounds(Vector3.zero, terrain.terrainData.size),
                        terrain.transform,
                        ref bounds,
                        ref hasBounds);

            if (!hasBounds)
            {
                foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null || !filter.gameObject.activeInHierarchy)
                        continue;
                    EncapsulateTransformedBounds(filter.sharedMesh.bounds, filter.transform, ref bounds, ref hasBounds);
                }
                foreach (var skinnedRenderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (skinnedRenderer.enabled && skinnedRenderer.gameObject.activeInHierarchy)
                        EncapsulateBounds(skinnedRenderer.bounds, ref bounds, ref hasBounds);
            }

            return hasBounds && bounds.size.x > 0f && bounds.size.y > 0f && bounds.size.z > 0f;
        }

        private static void EncapsulateTransformedBounds(
            Bounds localBounds,
            Transform target,
            ref Bounds bounds,
            ref bool hasBounds)
        {
            var min = localBounds.min;
            var max = localBounds.max;
            for (var x = 0; x < 2; x++)
            for (var y = 0; y < 2; y++)
            for (var z = 0; z < 2; z++)
            {
                var corner = target.TransformPoint(new Vector3(
                    x == 0 ? min.x : max.x,
                    y == 0 ? min.y : max.y,
                    z == 0 ? min.z : max.z));
                EncapsulateBounds(corner, ref bounds, ref hasBounds);
            }
        }

        private static void EncapsulateBounds(Bounds value, ref Bounds bounds, ref bool hasBounds)
        {
            if (!hasBounds)
            {
                bounds = value;
                hasBounds = true;
                return;
            }
            bounds.Encapsulate(value);
        }

        private static void EncapsulateBounds(Vector3 value, ref Bounds bounds, ref bool hasBounds)
        {
            if (!hasBounds)
            {
                bounds = new Bounds(value, Vector3.zero);
                hasBounds = true;
                return;
            }
            bounds.Encapsulate(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        }

        private static void EnsureDataFolder()
        {
            if (AssetDatabase.IsValidFolder(DataFolder))
                return;
            if (!AssetDatabase.IsValidFolder("Assets"))
                throw new DirectoryNotFoundException("The Unity Assets folder is unavailable.");
            AssetDatabase.CreateFolder("Assets", "Data");
            if (!AssetDatabase.IsValidFolder(DataFolder))
                throw new IOException($"Could not create '{DataFolder}'.");
        }

        private readonly struct SurfaceTriangle
        {
            private readonly Vector3 _a;
            private readonly Vector3 _b;
            private readonly Vector3 _c;
            public readonly float Denominator;
            public readonly float MinX;
            public readonly float MaxX;
            public readonly float MinZ;
            public readonly float MaxZ;

            public SurfaceTriangle(Vector3 a, Vector3 b, Vector3 c)
            {
                _a = a;
                _b = b;
                _c = c;
                Denominator = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                MinX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
                MaxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
                MinZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z));
                MaxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
            }

            public bool TryGetHeight(float x, float z, out float height)
            {
                var first = ((_b.z - _c.z) * (x - _c.x) + (_c.x - _b.x) * (z - _c.z)) / Denominator;
                var second = ((_c.z - _a.z) * (x - _c.x) + (_a.x - _c.x) * (z - _c.z)) / Denominator;
                var third = 1f - first - second;
                const float epsilon = 0.00001f;
                if (first < -epsilon || second < -epsilon || third < -epsilon)
                {
                    height = 0f;
                    return false;
                }

                height = first * _a.y + second * _b.y + third * _c.y;
                return float.IsFinite(height);
            }

        }

        private readonly struct PendingCollider
        {
            public readonly Mesh Mesh;
            public readonly Transform Transform;

            public PendingCollider(Mesh mesh, Transform transform)
            {
                Mesh = mesh;
                Transform = transform;
            }
        }
    }
}
