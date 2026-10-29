using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameToolsEditor
{
    public sealed class CongThanhCharacterSetupWindow : EditorWindow
    {
        private const string SourceFolder = "Assets/_Game/Lính";
        private const string RemadeCavalryFolder = SourceFolder + "/KYBINH";
        private const string CharacterMaterialPath = SourceFolder + "/char.mat";
        private const string OutputFolder = "Assets/Data/Characters";
        private const string PrefabFolder = OutputFolder + "/Prefabs";
        private const string ControllerFolder = OutputFolder + "/Controllers";
        private const string AnimationFolder = OutputFolder + "/Animations";
        private const string TestSceneFolder = OutputFolder + "/Scenes";
        private const string TestScenePath = TestSceneFolder + "/CongThanhCharacterAnimationTest.unity";
        private const string AttackParameter = "Attack";
        private const string AttackState = "Attack";
        private const string ArrowPrefabPath =
            "Assets/_Game/Mũi tên và máy bắn đá/FX FIX FIRE/Mui ten/ARROW.prefab";
        private const float DefaultProjectileReleaseTime = 0.55f;
        private const float DefaultProjectileSpeed = 24f;
        private const float DeathSinkDelay = 1f;
        private const float DeathSinkDistance = 3f;
        private const float DeathSinkSpeed = 1f;
        private const float TestLineSpacing = 5f;

        private float _infantryHeight = 1.8f;
        private float _cavalryHeight = 2.4f;
        private float _elephantHeight = 3.2f;
        private float _siegeHeight = 3f;
        private string _saddleBoneName = "spine_02.x";
        private Vector3 _riderSaddleOffset;
        private Material _characterMaterial;
        private string _result = string.Empty;
        private string _centerReport = string.Empty;
        private Vector2 _scrollPosition;

        private struct ProjectileSetupOverrides
        {
            public bool HasSettings;
            public Vector3 SpawnPointLocalPosition;
            public float ReleaseNormalizedTime;
            public float ProjectileSpeed;
            public float ProjectileScale;
            public GameObject ProjectilePrefab;
        }

        [MenuItem("Game Tools/Công Thành/Thiết lập nhân vật")]
        private static void Open()
        {
            var window = GetWindow<CongThanhCharacterSetupWindow>();
            window.titleContent = new GUIContent("Công Thành - Thiết lập nhân vật");
            window.minSize = new Vector2(400f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            if (_characterMaterial == null)
                _characterMaterial = AssetDatabase.LoadAssetAtPath<Material>(CharacterMaterialPath);
        }

        private void OnGUI()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scrollPosition))
            {
                EditorGUILayout.LabelField("Thiết lập nhân vật Công Thành", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Chỉ tạo prefab từ FBX đội Xanh và Đỏ. Model trong KYBINH đã có sẵn ngựa và được tạo nguyên mẫu; các loại lính khác được tạo độc lập. Model gốc không bị sửa.",
                    MessageType.Info);
                EditorGUILayout.HelpBox(
                    "Visual Offset được cấu hình tự động khi tạo prefab: model không phải kỵ binh dùng vị trí (0, 0, 0), máy bắn đá xanh/đỏ xoay Y 90°, cung thủ xanh chỉ xoay Y 90° trong Attack. Attack và Death chạy một lần; nhân vật thiếu clip Death nguồn sẽ được tạo hoạt ảnh chìm xuống. Hãy tạo lại prefab và cảnh thử để cập nhật các giá trị này.",
                    MessageType.Info);
                EditorGUILayout.ObjectField("Vật liệu nhân vật", _characterMaterial, typeof(Material), false);
                if (_characterMaterial == null)
                    EditorGUILayout.HelpBox(
                        $"Không thể tải vật liệu nhân vật bắt buộc tại '{CharacterMaterialPath}'.",
                        MessageType.Error);
                _saddleBoneName = EditorGUILayout.TextField("Tên xương yên ngựa", _saddleBoneName);
                _riderSaddleOffset = EditorGUILayout.Vector3Field(
                    "Độ lệch vị trí người cưỡi (đơn vị model)", _riderSaddleOffset);

                EditorGUILayout.LabelField("Chiều cao mục tiêu (đơn vị Unity)", EditorStyles.boldLabel);
                _infantryHeight = EditorGUILayout.FloatField("Bộ binh", _infantryHeight);
                _cavalryHeight = EditorGUILayout.FloatField("Kỵ binh", _cavalryHeight);
                _elephantHeight = EditorGUILayout.FloatField("Voi chiến", _elephantHeight);
                _siegeHeight = EditorGUILayout.FloatField("Máy công thành", _siegeHeight);

                var inputsValid = ValidateSettings(out var validationMessage);
                if (!inputsValid)
                    EditorGUILayout.HelpBox(validationMessage, MessageType.Warning);

                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button("Đo tâm theo từng hoạt ảnh", GUILayout.Height(28f)))
                        MeasureBoneCenters();
                }

                if (!string.IsNullOrEmpty(_centerReport))
                    EditorGUILayout.HelpBox(_centerReport, MessageType.None);

                using (new EditorGUI.DisabledScope(!inputsValid || EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button("Tạo / cập nhật toàn bộ prefab nhân vật", GUILayout.Height(34f)))
                        GenerateAll();
                }

                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    if (GUILayout.Button("Tạo / mở cảnh thử hoạt ảnh nhân vật", GUILayout.Height(34f)))
                    {
                        EditorApplication.delayCall += BuildAndRunAnimationTest;
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(_result))
                    EditorGUILayout.HelpBox(_result, MessageType.Info);
                _scrollPosition = scroll.scrollPosition;
            }
        }

        private bool ValidateSettings(out string message)
        {
            if (_characterMaterial == null)
            {
                message = $"Không thể tải vật liệu nhân vật bắt buộc tại '{CharacterMaterialPath}'.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(_saddleBoneName))
            {
                message = "Tên xương yên ngựa không được để trống.";
                return false;
            }
            if (!IsFinite(_riderSaddleOffset.x) || !IsFinite(_riderSaddleOffset.y) ||
                !IsFinite(_riderSaddleOffset.z))
            {
                message = "Độ lệch vị trí người cưỡi phải là các giá trị hữu hạn.";
                return false;
            }
            if (!IsValidHeight(_infantryHeight) || !IsValidHeight(_cavalryHeight) ||
                !IsValidHeight(_elephantHeight) || !IsValidHeight(_siegeHeight))
            {
                message = "Chiều cao mục tiêu phải là số hữu hạn trong khoảng từ 0,1 đến 100.";
                return false;
            }
            var modelPaths = GetSelectedModelPaths();
            if (modelPaths.Length > 0)
            {
                foreach (var modelPath in modelPaths.Where(IsMountedCavalryModel))
                {
                    var horsePath = GetHorseModelPath(modelPath);
                    if (AssetDatabase.LoadAssetAtPath<GameObject>(horsePath) == null)
                    {
                        message = $"Kỵ binh '{Path.GetFileNameWithoutExtension(modelPath)}' cần model ngựa tương ứng tại '{horsePath}'.";
                        return false;
                    }
                }
                message = string.Empty;
                return true;
            }
            message = $"Không tìm thấy model nhân vật FBX đội Xanh hoặc Đỏ trong '{SourceFolder}'.";
            return false;
        }

        private static bool IsValidHeight(float height) =>
            float.IsFinite(height) && height >= 0.1f && height <= 100f;

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private bool GenerateAll()
        {
            var modelPaths = GetSelectedModelPaths();
            if (modelPaths.Length == 0)
                throw new InvalidOperationException($"Không tìm thấy model nhân vật FBX đội Xanh hoặc Đỏ trong '{SourceFolder}'.");

            if (!EditorUtility.DisplayDialog(
                    "Tạo prefab nhân vật",
                    $"Tạo mới hoặc thay thế {modelPaths.Length} prefab nhân vật đội Xanh/Đỏ trong '{PrefabFolder}'? Model KYBINH đã làm lại có sẵn ngựa; các model nhân vật khác sẽ được tạo riêng. File FBX nguồn sẽ không bị chỉnh sửa.",
                    "Tạo",
                    "Hủy"))
                return false;

            var existingPrefabCount = modelPaths.Count(path =>
                AssetDatabase.LoadMainAssetAtPath(
                    $"{PrefabFolder}/{Path.GetFileNameWithoutExtension(path)}.prefab") != null);
            if (existingPrefabCount > 0 && !EditorUtility.DisplayDialog(
                    "Thay thế prefab đã tạo",
                    $"Đã có {existingPrefabCount} prefab được tạo trước đó và chúng sẽ bị thay thế. Bạn có muốn tiếp tục không?",
                    "Thay thế",
                    "Hủy"))
                return false;

            EnsureFolder(OutputFolder);
            EnsureFolder(PrefabFolder);
            EnsureFolder(ControllerFolder);
            EnsureFolder(AnimationFolder);
            var previewScene = EditorSceneManager.NewPreviewScene();
            var generated = 0;
            var failures = new List<string>();
            try
            {
                for (var i = 0; i < modelPaths.Length; i++)
                {
                    var path = modelPaths[i];
                    var modelName = Path.GetFileNameWithoutExtension(path);
                    EditorUtility.DisplayProgressBar(
                        "Thiết lập nhân vật Công Thành",
                        $"Đang xử lý {modelName}",
                        (float)i / modelPaths.Length);
                    try
                    {
                        ConfigureLoopingAnimationClips(path);
                        if (IsMountedCavalryModel(path))
                            GenerateMountedCavalryPrefab(path, modelName, previewScene);
                        else
                            GenerateCharacterPrefab(path, modelName, previewScene);
                        generated++;
                    }
                    catch (Exception exception)
                    {
                        failures.Add($"{modelName}: {exception.Message}");
                        Debug.LogException(exception);
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                EditorSceneManager.ClosePreviewScene(previewScene);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            _result = $"Đã tạo/cập nhật: {generated}; thất bại: {failures.Count}.";
            if (failures.Count > 0)
                _result += "\n" + string.Join("\n", failures);
            Debug.Log(_result);
            return failures.Count == 0;
        }

        private void BuildAndRunAnimationTest()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TestScenePath) != null &&
                !EditorUtility.DisplayDialog(
                    "Thay thế cảnh thử hoạt ảnh",
                    $"Thay thế '{TestScenePath}' và mở cảnh mới?",
                    "Thay thế",
                    "Hủy"))
                return;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            if (!GenerateAll())
                return;

            var modelPaths = GetSelectedModelPaths();
            var prefabPaths = modelPaths
                .Select(path => $"{PrefabFolder}/{Path.GetFileNameWithoutExtension(path)}.prefab")
                .ToArray();
            var missingPrefabs = prefabPaths
                .Where(path => AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                .ToArray();
            if (missingPrefabs.Length > 0)
            {
                var missingNames = string.Join("\n", missingPrefabs.Select(Path.GetFileNameWithoutExtension));
                EditorUtility.DisplayDialog(
                    "Thiếu prefab nhân vật",
                    $"Hãy tạo prefab nhân vật trước khi chạy cảnh thử. Các prefab còn thiếu:\n{missingNames}",
                    "Đóng");
                return;
            }

            EnsureFolder(OutputFolder);
            EnsureFolder(TestSceneFolder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var unitsRoot = new GameObject("Character Lineup");
            var blueTeam = new GameObject("Blue Team");
            blueTeam.transform.SetParent(unitsRoot.transform, false);
            var redTeam = new GameObject("Red Team");
            redTeam.transform.SetParent(unitsRoot.transform, false);
            var bluePrefabs = prefabPaths.Where(path => IsBlueOrRedModel(path) &&
                Path.GetFileNameWithoutExtension(path).EndsWith("_blue", StringComparison.OrdinalIgnoreCase)).ToArray();
            var redPrefabs = prefabPaths.Where(path => IsBlueOrRedModel(path) &&
                Path.GetFileNameWithoutExtension(path).EndsWith("_red", StringComparison.OrdinalIgnoreCase)).ToArray();
            var rowWidth = Mathf.Max(bluePrefabs.Length, redPrefabs.Length) * TestLineSpacing;
            var groundWidth = rowWidth + TestLineSpacing;

            CreateTestGround(groundWidth);
            CreateTestCamera(groundWidth);
            CreateTestLight();
            InstantiateTeamLineup(bluePrefabs, blueTeam.transform, -5f, false);
            InstantiateTeamLineup(redPrefabs, redTeam.transform, 5f, true);

            var controlsObject = new GameObject("Animation Test Controls");
            var controls = controlsObject.AddComponent<RecoilCharacterAnimationTestRig>();
            controls.CharactersRoot = unitsRoot.transform;

            if (!EditorSceneManager.SaveScene(scene, TestScenePath))
                throw new InvalidOperationException($"Không thể lưu cảnh thử hoạt ảnh tại '{TestScenePath}'.");
            _result = $"Đã tạo cảnh thử với {bluePrefabs.Length} đơn vị đội Xanh và {redPrefabs.Length} đơn vị đội Đỏ.";
            Debug.Log(_result);
        }

        private static void InstantiateTeamLineup(string[] prefabPaths, Transform teamRoot, float rowZ, bool faceBlue)
        {
            for (var i = 0; i < prefabPaths.Length; i++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPaths[i]);
                if (prefab == null)
                    throw new InvalidOperationException($"Không thể tải prefab nhân vật đã tạo '{prefabPaths[i]}'.");
                var instance = PrefabUtility.InstantiatePrefab(prefab, teamRoot) as GameObject;
                if (instance == null)
                    throw new InvalidOperationException($"Không thể khởi tạo prefab đã tạo '{prefabPaths[i]}'.");

                instance.transform.localPosition = new Vector3(
                    (i - (prefabPaths.Length - 1) * 0.5f) * TestLineSpacing,
                    0f,
                    rowZ);
                instance.transform.localRotation = faceBlue
                    ? Quaternion.Euler(0f, 180f, 0f)
                    : Quaternion.identity;
            }
        }

        private static void CreateTestGround(float width)
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Test Ground";
            ground.transform.position = new Vector3(0f, -0.1f, 0f);
            ground.transform.localScale = new Vector3(width / 10f, 1f, 2f);
        }

        private static void CreateTestCamera(float width)
        {
            var cameraObject = new GameObject("Test Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.orthographic = true;
            camera.orthographicSize = Mathf.Max(12f, width / 2f / (16f / 9f));
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.16f, 0.19f, 0.23f);
            cameraObject.transform.position = new Vector3(0f, 26f, -32f);
            cameraObject.transform.rotation = Quaternion.LookRotation(
                new Vector3(0f, 1f, 0f) - cameraObject.transform.position);
        }

        private static void CreateTestLight()
        {
            var lightObject = new GameObject("Test Directional Light");
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        private static string[] GetSelectedModelPaths()
        {
            return AssetDatabase.FindAssets("t:Model", new[] { SourceFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path =>
                    string.Equals(Path.GetExtension(path), ".fbx", StringComparison.OrdinalIgnoreCase) &&
                    IsBlueOrRedModel(path) &&
                    !IsHorseModel(path) &&
                    IsSelectedSourceModel(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool IsSelectedSourceModel(string path)
        {
            var directory = Path.GetDirectoryName(path)?.Replace('\\', '/');
            var modelName = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(directory, RemadeCavalryFolder, StringComparison.OrdinalIgnoreCase))
                return modelName.StartsWith("LOD_KYBINH_", StringComparison.OrdinalIgnoreCase);

            return modelName.IndexOf("KYBINH", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool IsBlueOrRedModel(string path)
        {
            var modelName = Path.GetFileNameWithoutExtension(path);
            return modelName.EndsWith("_blue", StringComparison.OrdinalIgnoreCase) ||
                   modelName.EndsWith("_red", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHorseModel(string path) =>
            Path.GetFileNameWithoutExtension(path).IndexOf("horse", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsMountedCavalryModel(string path)
        {
            var modelName = Path.GetFileNameWithoutExtension(path);
            var directory = Path.GetDirectoryName(path)?.Replace('\\', '/');
            return modelName.IndexOf("kybinh", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   !string.Equals(directory, RemadeCavalryFolder, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetHorseModelPath(string riderModelPath)
        {
            var modelName = Path.GetFileNameWithoutExtension(riderModelPath);
            var markerIndex = modelName.IndexOf("CUNGKY", StringComparison.OrdinalIgnoreCase);
            var riderMarker = "CUNGKY";
            if (markerIndex < 0)
            {
                riderMarker = "KYBINH";
                markerIndex = modelName.IndexOf(riderMarker, StringComparison.OrdinalIgnoreCase);
            }
            if (markerIndex < 0)
                throw new InvalidOperationException($"'{modelName}' is not a mounted cavalry model.");
            var horseName = modelName.Substring(0, markerIndex) + "HORSE" +
                            modelName.Substring(markerIndex + riderMarker.Length);
            return $"{SourceFolder}/{horseName}.fbx";
        }

        private Transform FindNamedBone(Transform modelRoot, string boneName, string modelDescription)
        {
            foreach (var bone in modelRoot.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(bone.name, boneName.Trim(), StringComparison.OrdinalIgnoreCase))
                    return bone;
            }
            throw new InvalidOperationException(
                $"Không tìm thấy xương yên ngựa '{boneName}' trong {modelDescription}.");
        }

        private static Transform FindRiderMountBone(Transform modelRoot, string modelDescription)
        {
            var bones = modelRoot.GetComponentsInChildren<Transform>(true);
            var mountBone = bones.FirstOrDefault(bone =>
                string.Equals(bone.name, "Hips", StringComparison.OrdinalIgnoreCase) ||
                bone.name.EndsWith(":Hips", StringComparison.OrdinalIgnoreCase));
            if (mountBone == null)
                mountBone = bones.FirstOrDefault(bone =>
                    bone.name.IndexOf("hip", StringComparison.OrdinalIgnoreCase) >= 0);
            if (mountBone == null)
                mountBone = bones.FirstOrDefault(bone =>
                    string.Equals(bone.name, "spine_01.x", StringComparison.OrdinalIgnoreCase));
            if (mountBone == null)
                mountBone = bones.FirstOrDefault(bone =>
                    string.Equals(bone.name, "root.x", StringComparison.OrdinalIgnoreCase));
            if (mountBone == null)
                mountBone = bones.FirstOrDefault(bone =>
                    string.Equals(bone.name, "root", StringComparison.OrdinalIgnoreCase));
            if (mountBone == null)
                throw new InvalidOperationException(
                    $"Không tìm thấy xương hông hoặc xương gốc để gắn người cưỡi trong {modelDescription}.");
            return mountBone;
        }

        private static Bounds GetCombinedBounds(IReadOnlyList<Renderer> renderers, string modelDescription)
        {
            if (renderers.Count == 0)
                throw new InvalidOperationException($"{modelDescription} không có Renderer.");

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Count; i++)
                bounds.Encapsulate(renderers[i].bounds);
            if (!float.IsFinite(bounds.size.y) || bounds.size.y <= 0.001f)
                throw new InvalidOperationException($"{modelDescription} có giới hạn chiều cao không hợp lệ.");
            return bounds;
        }

        private void MeasureBoneCenters()
        {
            var modelPaths = GetSelectedModelPaths();
            if (modelPaths.Length == 0)
                throw new InvalidOperationException(
                    $"Không tìm thấy model nhân vật FBX đội Xanh hoặc Đỏ trong '{SourceFolder}'.");

            var previewScene = EditorSceneManager.NewPreviewScene();
            var results = new List<string>(modelPaths.Length);
            try
            {
                foreach (var modelPath in modelPaths)
                {
                    var modelName = Path.GetFileNameWithoutExtension(modelPath);
                    GameObject model = null;
                    GameObject measurementRoot = null;
                    try
                    {
                        var source = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
                        if (source == null)
                            throw new InvalidOperationException($"Không thể tải model FBX '{modelPath}'.");

                        model = PrefabUtility.InstantiatePrefab(source, previewScene) as GameObject;
                        if (model == null)
                            throw new InvalidOperationException($"Không thể khởi tạo model FBX '{modelPath}'.");

                        measurementRoot = new GameObject($"{modelName} Measurement Root");
                        SceneManager.MoveGameObjectToScene(measurementRoot, previewScene);
                        model.transform.SetParent(measurementRoot.transform, false);

                        var baseBounds = GetCombinedBounds(
                            model.GetComponentsInChildren<Renderer>(true),
                            $"character '{modelName}'");
                        var targetHeight = GetTargetHeight(GetCharacterGroup(modelName));
                        var scale = targetHeight / baseBounds.size.y;
                        var pivotOffset = new Vector3(
                            -baseBounds.center.x,
                            -baseBounds.min.y,
                            -baseBounds.center.z);
                        model.transform.localScale = Vector3.one * scale;
                        model.transform.localPosition = pivotOffset * scale;

                        var centerBone = FindCenterBone(model.transform);
                        var clips = GetAnimationClips(modelPath).ToList();
                        var generatedDeathClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                            $"{AnimationFolder}/{modelName}_Death.anim");
                        if (generatedDeathClip != null)
                            clips.Add(generatedDeathClip);
                        if (clips.Count == 0)
                            throw new InvalidOperationException(
                                $"Không tìm thấy clip hoạt ảnh nào cho nhân vật '{modelName}'.");

                        var animationResults = new List<string>(clips.Count);
                        foreach (var clip in clips)
                        {
                            try
                            {
                                animationResults.Add(centerBone != null
                                    ? MeasureAnimationCenter(model, centerBone, clip, modelName)
                                    : MeasureAnimationBoundsOnly(model, clip, modelName));
                            }
                            catch (Exception exception)
                            {
                                Debug.LogException(exception);
                                animationResults.Add(
                                    $"{clip.name}: ĐO THẤT BẠI - {exception.Message}");
                            }
                        }

                        results.Add(
                            $"{modelName} | " +
                            (centerBone != null
                                ? $"xương tâm: {centerBone.name}"
                                : "KHÔNG TÌM THẤY XƯƠNG; chỉ báo cáo giới hạn hình học theo hoạt ảnh") +
                            "\n" +
                            string.Join("\n", animationResults));
                    }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                        results.Add($"{modelName}: ĐO THẤT BẠI - {exception.Message}");
                    }
                    finally
                    {
                        if (model != null)
                            DestroyImmediate(model);
                        if (measurementRoot != null)
                            DestroyImmediate(measurementRoot);
                    }
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(previewScene);
            }

            _centerReport = string.Join("\n\n", results);
            Repaint();
        }

        private static string MeasureAnimationBoundsOnly(
            GameObject model,
            AnimationClip clip,
            string modelName)
        {
            var samples = new[] { 0f, 0.5f, 0.99f };
            var report = new List<string>(samples.Length);
            foreach (var normalizedTime in samples)
            {
                var sampleTime = Mathf.Clamp(
                    clip.length * normalizedTime,
                    0f,
                    Mathf.Max(0f, clip.length - 0.0001f));
                AnimationMode.StartAnimationMode();
                try
                {
                    AnimationMode.SampleAnimationClip(model, clip, sampleTime);
                    var bounds = GetCombinedBounds(
                        model.GetComponentsInChildren<Renderer>(true),
                        $"character '{modelName}' in '{clip.name}'");
                    var desiredWorldPoint = new Vector3(
                        bounds.center.x,
                        bounds.min.y,
                        bounds.center.z);
                    var desiredLocalPoint = model.transform.parent.InverseTransformPoint(desiredWorldPoint);
                    report.Add(
                        $"  thời điểm={normalizedTime:F2}: mục tiêu (giới hạn hình học)={FormatVector(desiredLocalPoint)} " +
                        "không có tâm/hướng xương (model không có skeleton)");
                }
                finally
                {
                    AnimationMode.StopAnimationMode();
                }
            }

            return $"{clip.name}:\n{string.Join("\n", report)}";
        }

        private static string MeasureAnimationCenter(
            GameObject model,
            Transform centerBone,
            AnimationClip clip,
            string modelName)
        {
            var samples = new[] { 0f, 0.5f, 0.99f };
            var report = new List<string>(samples.Length);
            foreach (var normalizedTime in samples)
            {
                var sampleTime = Mathf.Clamp(
                    clip.length * normalizedTime,
                    0f,
                    Mathf.Max(0f, clip.length - 0.0001f));
                AnimationMode.StartAnimationMode();
                try
                {
                    AnimationMode.SampleAnimationClip(model, clip, sampleTime);
                    var renderers = model.GetComponentsInChildren<Renderer>(true);
                    var bounds = GetCombinedBounds(renderers, $"character '{modelName}' in '{clip.name}'");
                    var desiredWorldPoint = new Vector3(
                        bounds.center.x,
                        bounds.min.y,
                        bounds.center.z);
                    var desiredLocalPoint = model.transform.parent.InverseTransformPoint(desiredWorldPoint);
                    var actualLocalPoint = model.transform.parent.InverseTransformPoint(centerBone.position);
                    var delta = actualLocalPoint - desiredLocalPoint;
                    var localForward = model.transform.parent
                        .InverseTransformDirection(centerBone.forward)
                        .normalized;
                    var yawFromModelForward = Vector3.SignedAngle(
                        Vector3.forward,
                        localForward,
                        Vector3.up);
                    report.Add(
                        $"  thời điểm={normalizedTime:F2}: mục tiêu={FormatVector(desiredLocalPoint)} " +
                        $"xương={FormatVector(actualLocalPoint)} " +
                        $"độ lệch={FormatVector(delta)} hiệu chỉnh={FormatVector(-delta)} " +
                        $"hướng xương={FormatVector(localForward)} góc quay={yawFromModelForward:F2} độ");
                }
                finally
                {
                    AnimationMode.StopAnimationMode();
                }
            }

            return $"{clip.name}:\n{string.Join("\n", report)}";
        }

        private static Transform FindCenterBone(Transform modelRoot)
        {
            var skinnedRenderers = modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var rootBone = skinnedRenderers
                .Select(renderer => renderer.rootBone)
                .FirstOrDefault(bone => bone != null);
            if (rootBone != null)
                return rootBone;

            var skinBones = skinnedRenderers
                .SelectMany(renderer => renderer.bones)
                .Where(bone => bone != null)
                .Distinct()
                .ToArray();
            if (skinBones.Length > 0)
            {
                for (var candidate = skinBones[0];
                     candidate != null && candidate != modelRoot;
                     candidate = candidate.parent)
                {
                    if (skinBones.All(bone => bone == candidate || bone.IsChildOf(candidate)))
                        return candidate;
                }
            }

            var animator = modelRoot.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.isHuman)
            {
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (hips != null)
                    return hips;
            }

            var bones = modelRoot.GetComponentsInChildren<Transform>(true);
            var centerBone = FindBoneByName(bones, "Hips") ??
                             FindBoneByName(bones, "Pelvis") ??
                             FindBoneByName(bones, "root.x") ??
                             FindBoneByName(bones, "root") ??
                             FindBoneByName(bones, "Armature");
            return centerBone ?? bones.FirstOrDefault(bone =>
                bone.name.StartsWith("Armature_", StringComparison.OrdinalIgnoreCase));
        }

        private static Transform FindBoneByName(IEnumerable<Transform> bones, string boneName)
        {
            return bones.FirstOrDefault(bone =>
                string.Equals(bone.name, boneName, StringComparison.OrdinalIgnoreCase) ||
                bone.name.EndsWith($":{boneName}", StringComparison.OrdinalIgnoreCase));
        }

        private static string FormatVector(Vector3 value)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0:F4}, {1:F4}, {2:F4})",
                value.x,
                value.y,
                value.z);
        }

        private void ApplyCharacterMaterial(IEnumerable<Renderer> renderers)
        {
            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                if (materials.Length == 0)
                    materials = new Material[1];
                for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                    materials[materialIndex] = _characterMaterial;
                renderer.sharedMaterials = materials;
            }
        }

        private static AnimationClip[] GetAnimationClips(string modelPath)
        {
            return AssetDatabase.LoadAllAssetsAtPath(modelPath)
                .OfType<AnimationClip>()
                .Where(clip => !clip.name.StartsWith("__", StringComparison.Ordinal))
                .ToArray();
        }

        private static void ConfigureLoopingAnimationClips(string modelPath)
        {
            var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
            if (importer == null)
                throw new InvalidOperationException($"Không thể tải bộ nhập model cho '{modelPath}'.");

            var isSiege = GetCharacterGroup(Path.GetFileNameWithoutExtension(modelPath)) ==
                          CharacterGroup.Siege;
            var clips = importer.clipAnimations;
            if (clips.Length == 0)
                clips = importer.defaultClipAnimations;

            var changed = false;
            foreach (var clip in clips)
            {
                if (isSiege)
                {
                    if (clip.loopTime)
                    {
                        clip.loopTime = false;
                        changed = true;
                    }
                    if (clip.loopPose)
                    {
                        clip.loopPose = false;
                        changed = true;
                    }
                    continue;
                }

                if (clip.name.IndexOf("DEATH", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    clip.name.IndexOf("ATTACK", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (clip.loopTime)
                    {
                        clip.loopTime = false;
                        changed = true;
                    }
                    if (clip.loopPose)
                    {
                        clip.loopPose = false;
                        changed = true;
                    }
                    continue;
                }

                if (clip.name.IndexOf("IDLE", StringComparison.OrdinalIgnoreCase) < 0 &&
                    clip.name.IndexOf("RUN", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!clip.loopTime)
                {
                    clip.loopTime = true;
                    changed = true;
                }
                if (!clip.loopPose)
                {
                    clip.loopPose = true;
                    changed = true;
                }
            }

            if (!changed)
                return;

            importer.clipAnimations = clips;
            importer.SaveAndReimport();
        }

        private static AnimationClip CreatePendingDeathClip(
            string modelName,
            string suffix,
            AnimationClip sourceClip,
            Transform animatedRoot,
            float sinkDistance,
            float sinkSpeed,
            out string pendingPath,
            out string destinationPath)
        {
            pendingPath = null;
            destinationPath = null;

            var parentVerticalScale = animatedRoot.parent != null
                ? animatedRoot.parent.TransformVector(Vector3.up).magnitude
                : 1f;
            if (!float.IsFinite(parentVerticalScale) || parentVerticalScale <= 0.0001f)
                throw new InvalidOperationException(
                    $"Cannot create a sinking death clip for '{modelName}' with a zero vertical scale.");
            if (sinkDistance <= 0f || sinkSpeed <= 0f)
                throw new ArgumentOutOfRangeException(
                    nameof(sinkDistance), "Death sink distance and speed must be positive.");

            var sourceDuration = sourceClip != null ? sourceClip.length : 0f;
            var sinkStartTime = sourceDuration + DeathSinkDelay;
            var sinkEndTime = sinkStartTime + sinkDistance / sinkSpeed;
            var localSinkSpeed = sinkSpeed / parentVerticalScale;
            var deathClipName = string.IsNullOrEmpty(suffix)
                ? $"{modelName}_Death"
                : $"{modelName}_{suffix}_Death";
            var clip = new AnimationClip
            {
                name = deathClipName,
                frameRate = sourceClip != null ? sourceClip.frameRate : 30f
            };
            var hasRootYCurve = false;
            var sourceBindings = sourceClip != null
                ? AnimationUtility.GetCurveBindings(sourceClip)
                : Array.Empty<EditorCurveBinding>();
            foreach (var binding in sourceBindings)
            {
                var sourceCurve = AnimationUtility.GetEditorCurve(sourceClip, binding);
                if (binding.type == typeof(Transform) &&
                    string.IsNullOrEmpty(binding.path) &&
                    binding.propertyName == "m_LocalPosition.y")
                {
                    sourceCurve = CreateSinkingPositionCurve(
                        sourceCurve,
                        animatedRoot.localPosition.y,
                        sourceDuration,
                        sinkStartTime,
                        sinkEndTime,
                        localSinkSpeed);
                    hasRootYCurve = true;
                }
                else if (binding.type == typeof(Transform) &&
                         string.IsNullOrEmpty(binding.path) &&
                         binding.propertyName.StartsWith("m_LocalPosition.", StringComparison.Ordinal))
                {
                    var initialPosition = animatedRoot.localPosition;
                    var initialValue = binding.propertyName.EndsWith(".x", StringComparison.Ordinal)
                        ? initialPosition.x
                        : initialPosition.z;
                    sourceCurve = OffsetCurveStart(sourceCurve, initialValue);
                }

                AnimationUtility.SetEditorCurve(clip, binding, sourceCurve);
            }

            if (!hasRootYCurve)
            {
                var rootYBinding = EditorCurveBinding.FloatCurve(
                    string.Empty, typeof(Transform), "m_LocalPosition.y");
                AnimationUtility.SetEditorCurve(
                    clip,
                    rootYBinding,
                    CreateSinkingPositionCurve(
                        new AnimationCurve(),
                        animatedRoot.localPosition.y,
                        sourceDuration,
                        sinkStartTime,
                        sinkEndTime,
                        localSinkSpeed));
            }

            if (sourceClip != null)
            {
                foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(sourceClip))
                    AnimationUtility.SetObjectReferenceCurve(
                        clip, binding, AnimationUtility.GetObjectReferenceCurve(sourceClip, binding));
                AnimationUtility.SetAnimationEvents(clip, AnimationUtility.GetAnimationEvents(sourceClip));
            }

            var settings = sourceClip != null
                ? AnimationUtility.GetAnimationClipSettings(sourceClip)
                : AnimationUtility.GetAnimationClipSettings(clip);
            settings.stopTime = sinkEndTime;
            settings.loopTime = false;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            pendingPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{AnimationFolder}/{modelName}_{suffix}_Death_Pending.anim");
            destinationPath = $"{AnimationFolder}/{modelName}_{suffix}_Death.anim";
            AssetDatabase.CreateAsset(clip, pendingPath);
            return clip;
        }

        private static AnimationCurve CreateSinkingPositionCurve(
            AnimationCurve sourceCurve,
            float initialPosition,
            float sourceDuration,
            float sinkStartTime,
            float sinkEndTime,
            float localSinkSpeed)
        {
            var curve = sourceCurve.keys.Length == 0
                ? new AnimationCurve()
                : new AnimationCurve(sourceCurve.keys);
            var offset = sourceCurve.keys.Length == 0
                ? 0f
                : initialPosition - sourceCurve.Evaluate(0f);
            var sourceEndValue = sourceCurve.keys.Length == 0
                ? initialPosition
                : sourceCurve.Evaluate(sourceDuration) + offset;
            var sourceEndInTangent = GetCurveEndTangent(curve, sourceDuration);

            if (sourceCurve.keys.Length > 0)
            {
                for (var i = 0; i < curve.length; i++)
                {
                    var key = curve.keys[i];
                    key.value += offset;
                    curve.MoveKey(i, key);
                }
            }

            AddPositionKey(curve, sourceDuration, sourceEndValue, sourceEndInTangent, 0f);
            AddPositionKey(curve, sinkStartTime, sourceEndValue, 0f, -localSinkSpeed);
            AddPositionKey(
                curve,
                sinkEndTime,
                sourceEndValue - localSinkSpeed * (sinkEndTime - sinkStartTime),
                -localSinkSpeed,
                0f);
            curve.preWrapMode = WrapMode.ClampForever;
            curve.postWrapMode = WrapMode.ClampForever;
            return curve;
        }

        private static float GetCurveEndTangent(AnimationCurve curve, float time)
        {
            var keys = curve.keys;
            var endKeyIndex = Array.FindIndex(keys, key => Mathf.Approximately(key.time, time));
            if (endKeyIndex >= 0)
                return keys[endKeyIndex].inTangent;
            if (keys.Length == 0 || time <= 0f)
                return 0f;

            var sampleTime = Mathf.Max(0f, time - 0.001f);
            var sampleDuration = time - sampleTime;
            return sampleDuration > 0f
                ? (curve.Evaluate(time) - curve.Evaluate(sampleTime)) / sampleDuration
                : 0f;
        }

        private static AnimationCurve OffsetCurveStart(AnimationCurve sourceCurve, float initialValue)
        {
            if (sourceCurve.keys.Length == 0)
                return sourceCurve;

            var offset = initialValue - sourceCurve.Evaluate(0f);
            var curve = new AnimationCurve(sourceCurve.keys);
            for (var i = 0; i < curve.length; i++)
            {
                var key = curve.keys[i];
                key.value += offset;
                curve.MoveKey(i, key);
            }
            curve.preWrapMode = sourceCurve.preWrapMode;
            curve.postWrapMode = sourceCurve.postWrapMode;
            return curve;
        }

        private static void AddPositionKey(
            AnimationCurve curve,
            float time,
            float value,
            float inTangent,
            float outTangent)
        {
            var existingIndex = Array.FindIndex(curve.keys, key => Mathf.Approximately(key.time, time));
            var key = new Keyframe(time, value, inTangent, outTangent);
            if (existingIndex >= 0)
                curve.MoveKey(existingIndex, key);
            else
                curve.AddKey(key);
        }

        private static void MovePendingDeathClip(
            string pendingPath,
            string destinationPath)
        {
            if (pendingPath == null)
                return;
            if (AssetDatabase.LoadMainAssetAtPath(destinationPath) != null &&
                !AssetDatabase.DeleteAsset(destinationPath))
                throw new InvalidOperationException(
                    $"Không thể thay thế clip hoạt ảnh chết cũ tại '{destinationPath}'.");
            var moveError = AssetDatabase.MoveAsset(pendingPath, destinationPath);
            if (!string.IsNullOrEmpty(moveError))
                throw new InvalidOperationException(
                    $"Prefab was saved, but its death animation clip could not be moved to '{destinationPath}': {moveError}");
        }

        private static AnimationClip GetAttackClip(IEnumerable<AnimationClip> clips) =>
            FindAnimationClip(clips, "ATTACK");

        private static AnimationClip GetDefaultClip(
            IEnumerable<AnimationClip> clips,
            AnimationClip attackClip)
        {
            return FindAnimationClip(clips, "IDLE") ??
                   clips.FirstOrDefault(clip => clip != attackClip);
        }

        private AnimatorController CreatePendingController(
            string modelName,
            string suffix,
            AnimationClip[] clips,
            AnimationClip defaultClip,
            AnimationClip attackClip,
            out string pendingPath)
        {
            if (clips.Length == 0)
            {
                pendingPath = null;
                return null;
            }

            pendingPath = AssetDatabase.GenerateUniqueAssetPath(
                $"{ControllerFolder}/{modelName}_{suffix}_Pending.controller");
            return CreateAnimatorController(pendingPath, clips, defaultClip, attackClip);
        }

        private static Animator GetOrAddAnimator(GameObject model, AnimatorController controller)
        {
            var animator = model.GetComponentInChildren<Animator>(true);
            if (animator == null && controller != null)
                animator = model.AddComponent<Animator>();
            if (animator != null)
            {
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                if (controller != null)
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            return animator;
        }

        private static void ReplaceControllerAsset(string path, AnimatorController oldController)
        {
            if (oldController != null && !AssetDatabase.DeleteAsset(path))
                throw new InvalidOperationException($"Không thể thay thế Animator Controller cũ tại '{path}'.");
        }

        private static void MovePendingController(string pendingPath, string destinationPath)
        {
            if (pendingPath == null)
                return;
            var moveError = AssetDatabase.MoveAsset(pendingPath, destinationPath);
            if (!string.IsNullOrEmpty(moveError))
                throw new InvalidOperationException(
                    $"Prefab was saved, but its controller could not be moved to '{destinationPath}': {moveError}");
        }

        private void GenerateMountedCavalryPrefab(string riderModelPath, string modelName, Scene previewScene)
        {
            var prefabPath = $"{PrefabFolder}/{modelName}.prefab";
            var existingPrefab = AssetDatabase.LoadMainAssetAtPath(prefabPath);
            if (existingPrefab != null && !(existingPrefab is GameObject))
                throw new InvalidOperationException($"'{prefabPath}' exists and is not a prefab.");
            var projectileSetupOverrides = CaptureProjectileSetupOverrides(prefabPath);

            var horseModelPath = GetHorseModelPath(riderModelPath);
            var riderSource = AssetDatabase.LoadAssetAtPath<GameObject>(riderModelPath);
            ConfigureLoopingAnimationClips(horseModelPath);
            var horseSource = AssetDatabase.LoadAssetAtPath<GameObject>(horseModelPath);
            if (riderSource == null || horseSource == null)
                throw new InvalidOperationException(
                    $"Không thể tải cặp model kỵ binh và ngựa '{riderModelPath}' và '{horseModelPath}'.");

            var riderClips = GetAnimationClips(riderModelPath);
            var horseClips = GetAnimationClips(horseModelPath);
            var riderAttackClip = GetAttackClip(riderClips);
            var riderDefaultClip = GetDefaultClip(riderClips, riderAttackClip);
            var horseAttackClip = GetAttackClip(horseClips);
            var horseDefaultClip = GetDefaultClip(horseClips, horseAttackClip);

            var riderControllerPath = $"{ControllerFolder}/{modelName}_Rider.controller";
            var horseControllerPath = $"{ControllerFolder}/{modelName}_Horse.controller";
            var oldRiderController = AssetDatabase.LoadMainAssetAtPath(riderControllerPath) as AnimatorController;
            var oldHorseController = AssetDatabase.LoadMainAssetAtPath(horseControllerPath) as AnimatorController;
            if (AssetDatabase.LoadMainAssetAtPath(riderControllerPath) != null && oldRiderController == null)
                throw new InvalidOperationException($"'{riderControllerPath}' exists and is not an Animator Controller.");
            if (AssetDatabase.LoadMainAssetAtPath(horseControllerPath) != null && oldHorseController == null)
                throw new InvalidOperationException($"'{horseControllerPath}' exists and is not an Animator Controller.");

            string pendingRiderControllerPath = null;
            string pendingHorseControllerPath = null;
            GameObject root = null;
            var prefabSaved = false;
            try
            {
                var riderController = CreatePendingController(
                    modelName, "Rider", riderClips, riderDefaultClip, riderAttackClip,
                    out pendingRiderControllerPath);
                var horseController = CreatePendingController(
                    modelName, "Horse", horseClips, horseDefaultClip, horseAttackClip,
                    out pendingHorseControllerPath);

                root = new GameObject(modelName);
                SceneManager.MoveGameObjectToScene(root, previewScene);
                var mountedVisual = new GameObject("Visual Offset");
                mountedVisual.transform.SetParent(root.transform, false);

                var horseModel = (GameObject)PrefabUtility.InstantiatePrefab(horseSource, mountedVisual.transform);
                var riderModel = (GameObject)PrefabUtility.InstantiatePrefab(riderSource, mountedVisual.transform);
                if (horseModel == null || riderModel == null)
                    throw new InvalidOperationException("Không thể khởi tạo đồng thời model kỵ binh và ngựa.");

                horseModel.name = "Horse";
                riderModel.name = "Rider";
                var horseRenderers = horseModel.GetComponentsInChildren<Renderer>(true);
                var riderRenderers = riderModel.GetComponentsInChildren<Renderer>(true);
                ApplyCharacterMaterial(horseRenderers);
                ApplyCharacterMaterial(riderRenderers);

                var saddleBone = FindNamedBone(horseModel.transform, _saddleBoneName, $"horse model '{horseModelPath}'");
                var riderMountBone = FindRiderMountBone(
                    riderModel.transform, $"rider model '{riderModelPath}'");
                riderModel.transform.SetParent(saddleBone, true);
                riderModel.transform.position += saddleBone.position + _riderSaddleOffset -
                                                 riderMountBone.position;

                var allRenderers = horseRenderers.Concat(riderRenderers).ToArray();
                var bounds = GetCombinedBounds(allRenderers, $"mounted cavalry '{modelName}'");
                var pivotOffset = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);
                var animator = GetOrAddAnimator(riderModel, riderController);
                GetOrAddAnimator(horseModel, horseController);

                var spawnPointObject = new GameObject("Projectile Spawn Point");
                spawnPointObject.transform.SetParent(root.transform, false);
                spawnPointObject.transform.localPosition = projectileSetupOverrides.HasSettings
                    ? projectileSetupOverrides.SpawnPointLocalPosition
                    : new Vector3(
                        0f, GetTargetHeight(CharacterGroup.Cavalry) * 0.78f,
                        GetTargetHeight(CharacterGroup.Cavalry) * 0.42f);

                var presentation = root.AddComponent<RecoilCharacterPresentation>();
                var serializedPresentation = new SerializedObject(presentation);
                serializedPresentation.FindProperty("animator").objectReferenceValue = animator;
                serializedPresentation.FindProperty("visualRoot").objectReferenceValue = mountedVisual.transform;
                serializedPresentation.FindProperty("attackClip").objectReferenceValue = riderAttackClip;
                serializedPresentation.FindProperty("sourceModelHeight").floatValue = bounds.size.y;
                serializedPresentation.FindProperty("pivotOffset").vector3Value = pivotOffset;
                serializedPresentation.FindProperty("projectileSpawnPoint").objectReferenceValue =
                    spawnPointObject.transform;
                serializedPresentation.FindProperty("projectilePrefab").objectReferenceValue =
                    projectileSetupOverrides.ProjectilePrefab != null
                        ? projectileSetupOverrides.ProjectilePrefab
                        : GetProjectilePrefab(modelName);
                serializedPresentation.FindProperty("attackTrigger").stringValue = AttackParameter;
                serializedPresentation.FindProperty("attackStateName").stringValue = AttackState;
                serializedPresentation.FindProperty("normalizedVisualHeight").floatValue =
                    GetTargetHeight(CharacterGroup.Cavalry);
                serializedPresentation.FindProperty("forwardYawOffset").floatValue = 0f;
                serializedPresentation.FindProperty("projectileReleaseNormalizedTime").floatValue =
                    projectileSetupOverrides.HasSettings
                        ? projectileSetupOverrides.ReleaseNormalizedTime
                        : DefaultProjectileReleaseTime;
                serializedPresentation.FindProperty("projectileSpeed").floatValue =
                    projectileSetupOverrides.HasSettings
                        ? projectileSetupOverrides.ProjectileSpeed
                        : DefaultProjectileSpeed;
                serializedPresentation.FindProperty("projectileScale").floatValue =
                    projectileSetupOverrides.HasSettings
                        ? projectileSetupOverrides.ProjectileScale
                        : 1f;
                serializedPresentation.ApplyModifiedPropertiesWithoutUndo();
                presentation.ApplyVisualSettings();
                EditorUtility.SetDirty(presentation);

                var savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (savedPrefab == null)
                    throw new InvalidOperationException($"Unity không thể lưu '{prefabPath}'.");
                prefabSaved = true;

                ReplaceControllerAsset(riderControllerPath, oldRiderController);
                ReplaceControllerAsset(horseControllerPath, oldHorseController);
                MovePendingController(pendingRiderControllerPath, riderControllerPath);
                pendingRiderControllerPath = null;
                MovePendingController(pendingHorseControllerPath, horseControllerPath);
                pendingHorseControllerPath = null;
            }
            catch
            {
                if (!prefabSaved)
                {
                    if (pendingRiderControllerPath != null &&
                        AssetDatabase.LoadMainAssetAtPath(pendingRiderControllerPath) != null)
                        AssetDatabase.DeleteAsset(pendingRiderControllerPath);
                    if (pendingHorseControllerPath != null &&
                        AssetDatabase.LoadMainAssetAtPath(pendingHorseControllerPath) != null)
                        AssetDatabase.DeleteAsset(pendingHorseControllerPath);
                }
                throw;
            }
            finally
            {
                if (root != null)
                    DestroyImmediate(root);
            }
        }

        private void GenerateCharacterPrefab(string modelPath, string modelName, Scene previewScene)
        {
            var prefabPath = $"{PrefabFolder}/{modelName}.prefab";
            var existing = AssetDatabase.LoadMainAssetAtPath(prefabPath);
            if (existing != null && !(existing is GameObject))
                throw new InvalidOperationException($"'{prefabPath}' exists and is not a prefab.");
            var projectileSetupOverrides = CaptureProjectileSetupOverrides(prefabPath);

            var sourceModel = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (sourceModel == null)
                throw new InvalidOperationException($"Không thể tải model FBX '{modelPath}'.");

            var clips = GetAnimationClips(modelPath);
            var group = GetCharacterGroup(modelName);
            var attackClip = group == CharacterGroup.Siege && clips.Length == 1
                ? clips[0]
                : GetAttackClip(clips);
            var defaultClip = group == CharacterGroup.Siege
                ? null
                : GetDefaultClip(clips, attackClip);
            var targetHeight = GetTargetHeight(group);

            var controllerPath = $"{ControllerFolder}/{modelName}.controller";
            var oldController = AssetDatabase.LoadMainAssetAtPath(controllerPath);
            if (oldController != null && !(oldController is AnimatorController))
                throw new InvalidOperationException($"'{controllerPath}' exists and is not an Animator Controller.");
            var pendingControllerPath = clips.Length > 0
                ? AssetDatabase.GenerateUniqueAssetPath($"{ControllerFolder}/{modelName}_Pending.controller")
                : null;
            AnimatorController controller = null;
            GameObject root = null;
            var prefabSaved = false;
            string pendingDeathClipPath = null;
            string deathClipDestinationPath = null;
            try
            {
                root = new GameObject(modelName);
                SceneManager.MoveGameObjectToScene(root, previewScene);
                var visualOffset = new GameObject("Visual Offset");
                visualOffset.transform.SetParent(root.transform, false);
                var visualRoot = (GameObject)PrefabUtility.InstantiatePrefab(
                    sourceModel, visualOffset.transform);
                if (visualRoot == null)
                    throw new InvalidOperationException($"Không thể khởi tạo model '{modelPath}'.");
                visualRoot.name = "Visual";

                var renderers = visualRoot.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                    throw new InvalidOperationException($"Model '{modelPath}' không có Renderer.");

                foreach (var renderer in renderers)
                {
                    var materials = renderer.sharedMaterials;
                    if (materials.Length == 0)
                        materials = new Material[1];
                    for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                        materials[materialIndex] = _characterMaterial;
                    renderer.sharedMaterials = materials;
                }

                var worldBounds = renderers[0].bounds;
                for (var i = 1; i < renderers.Length; i++)
                    worldBounds.Encapsulate(renderers[i].bounds);
                if (!float.IsFinite(worldBounds.size.y) || worldBounds.size.y <= 0.001f)
                    throw new InvalidOperationException($"Model '{modelPath}' có giới hạn chiều cao không hợp lệ.");

                var scale = targetHeight / worldBounds.size.y;
                var pivotOffset = group == CharacterGroup.Cavalry
                    ? new Vector3(
                        -worldBounds.center.x,
                        -worldBounds.min.y,
                        -worldBounds.center.z)
                    : Vector3.zero;
                var forwardYawOffset = GetCharacterYawOffset(modelName, group);
                var attackForwardYawOffset = IsBlueArcher(modelName)
                    ? 90f
                    : forwardYawOffset;
                visualOffset.transform.localScale = Vector3.one * scale;
                visualOffset.transform.localPosition = pivotOffset * scale;
                visualOffset.transform.localRotation = Quaternion.Euler(0f, forwardYawOffset, 0f);

                var animator = visualRoot.GetComponentInChildren<Animator>(true);
                if (clips.Length > 0 && animator == null)
                    animator = visualRoot.AddComponent<Animator>();
                if (animator != null)
                {
                    var sourceDeathClip = FindAnimationClip(clips, "DEATH");
                    var deathClip = CreatePendingDeathClip(
                        modelName,
                        string.Empty,
                        sourceDeathClip,
                        animator.transform,
                        DeathSinkDistance,
                        DeathSinkSpeed,
                        out pendingDeathClipPath,
                        out deathClipDestinationPath);
                    if (pendingControllerPath != null)
                        controller = CreateAnimatorController(
                            pendingControllerPath, clips, defaultClip, attackClip, deathClip);
                    animator.runtimeAnimatorController = controller;
                    animator.applyRootMotion = false;
                    if (controller != null)
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                }

                var spawnPointObject = new GameObject("Projectile Spawn Point");
                spawnPointObject.transform.SetParent(root.transform, false);
                spawnPointObject.transform.localPosition = projectileSetupOverrides.HasSettings
                    ? projectileSetupOverrides.SpawnPointLocalPosition
                    : new Vector3(0f, targetHeight * 0.78f, targetHeight * 0.42f);
                var presentation = root.AddComponent<RecoilCharacterPresentation>();
                var serializedPresentation = new SerializedObject(presentation);
                serializedPresentation.FindProperty("animator").objectReferenceValue = animator;
                serializedPresentation.FindProperty("visualRoot").objectReferenceValue =
                    visualOffset.transform;
                serializedPresentation.FindProperty("attackClip").objectReferenceValue = attackClip;
                serializedPresentation.FindProperty("sourceModelHeight").floatValue = worldBounds.size.y;
                serializedPresentation.FindProperty("pivotOffset").vector3Value = pivotOffset;
                serializedPresentation.FindProperty("projectileSpawnPoint").objectReferenceValue =
                    spawnPointObject.transform;
                serializedPresentation.FindProperty("projectilePrefab").objectReferenceValue =
                    projectileSetupOverrides.ProjectilePrefab != null
                        ? projectileSetupOverrides.ProjectilePrefab
                        : GetProjectilePrefab(modelName);
                serializedPresentation.FindProperty("attackTrigger").stringValue = AttackParameter;
                serializedPresentation.FindProperty("attackStateName").stringValue = AttackState;
                serializedPresentation.FindProperty("normalizedVisualHeight").floatValue = targetHeight;
                serializedPresentation.FindProperty("forwardYawOffset").floatValue = forwardYawOffset;
                serializedPresentation.FindProperty("attackForwardYawOffset").floatValue =
                    attackForwardYawOffset;
                serializedPresentation.FindProperty("projectileReleaseNormalizedTime").floatValue =
                    projectileSetupOverrides.HasSettings
                        ? projectileSetupOverrides.ReleaseNormalizedTime
                        : DefaultProjectileReleaseTime;
                serializedPresentation.FindProperty("projectileSpeed").floatValue =
                    projectileSetupOverrides.HasSettings
                        ? projectileSetupOverrides.ProjectileSpeed
                        : DefaultProjectileSpeed;
                serializedPresentation.FindProperty("projectileScale").floatValue =
                    projectileSetupOverrides.HasSettings
                        ? projectileSetupOverrides.ProjectileScale
                        : 1f;
                serializedPresentation.ApplyModifiedPropertiesWithoutUndo();
                presentation.ApplyVisualSettings();
                EditorUtility.SetDirty(presentation);

                var savedPrefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (savedPrefab == null)
                    throw new InvalidOperationException($"Unity không thể lưu '{prefabPath}'.");
                prefabSaved = true;

                if (oldController != null && !AssetDatabase.DeleteAsset(controllerPath))
                    throw new InvalidOperationException($"Không thể thay thế Animator Controller cũ tại '{controllerPath}'.");
                if (pendingControllerPath != null)
                {
                    var moveError = AssetDatabase.MoveAsset(pendingControllerPath, controllerPath);
                    if (!string.IsNullOrEmpty(moveError))
                        throw new InvalidOperationException(
                            $"Prefab was saved, but its controller could not be moved to '{controllerPath}': {moveError}");
                    pendingControllerPath = null;
                }
                MovePendingDeathClip(pendingDeathClipPath, deathClipDestinationPath);
                pendingDeathClipPath = null;
            }
            catch
            {
                if (!prefabSaved)
                {
                    if (pendingControllerPath != null &&
                        AssetDatabase.LoadMainAssetAtPath(pendingControllerPath) != null)
                        AssetDatabase.DeleteAsset(pendingControllerPath);
                    if (pendingDeathClipPath != null &&
                        AssetDatabase.LoadMainAssetAtPath(pendingDeathClipPath) != null)
                        AssetDatabase.DeleteAsset(pendingDeathClipPath);
                }
                throw;
            }
            finally
            {
                if (root != null)
                    DestroyImmediate(root);
            }
        }

        private AnimatorController CreateAnimatorController(
            string path,
            AnimationClip[] clips,
            AnimationClip defaultClip,
            AnimationClip attackClip,
            AnimationClip deathClip = null)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var stateMachine = controller.layers[0].stateMachine;
            var idle = stateMachine.AddState("Idle");
            idle.motion = FindAnimationClip(clips, "IDLE") ?? defaultClip;
            stateMachine.defaultState = idle;

            AddAnimationState(stateMachine, clips, "Run", "RUN");
            var attackClipForState = attackClip ?? FindAnimationClip(clips, "ATTACK");
            var attack = AddAnimationState(stateMachine, attackClipForState, AttackState);
            AddAnimationState(
                stateMachine,
                deathClip ?? FindAnimationClip(clips, "DEATH"),
                "Death");
            if (attack == null)
                return controller;

            controller.AddParameter(AttackParameter, AnimatorControllerParameterType.Trigger);
            var toAttack = idle.AddTransition(attack);
            toAttack.hasExitTime = false;
            toAttack.duration = 0.05f;
            toAttack.AddCondition(AnimatorConditionMode.If, 0f, AttackParameter);
            var toIdle = attack.AddTransition(idle);
            toIdle.hasExitTime = true;
            toIdle.exitTime = 1f;
            toIdle.duration = 0.05f;
            toIdle.hasFixedDuration = true;
            return controller;
        }

        private static AnimatorState AddAnimationState(
            AnimatorStateMachine stateMachine,
            AnimationClip[] clips,
            string stateName,
            string clipToken)
        {
            return AddAnimationState(
                stateMachine,
                FindAnimationClip(clips, clipToken),
                stateName);
        }

        private static AnimatorState AddAnimationState(
            AnimatorStateMachine stateMachine,
            AnimationClip clip,
            string stateName)
        {
            if (clip == null)
                return null;
            var state = stateMachine.AddState(stateName);
            state.motion = clip;
            return state;
        }

        private static AnimationClip FindAnimationClip(IEnumerable<AnimationClip> clips, string token)
        {
            var matches = clips.Where(clip =>
                    clip.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();
            if (string.Equals(token, "ATTACK", StringComparison.OrdinalIgnoreCase))
            {
                var regularAttack = matches.FirstOrDefault(clip =>
                    clip.name.IndexOf("RUN", StringComparison.OrdinalIgnoreCase) < 0);
                if (regularAttack != null)
                    return regularAttack;
            }
            else if (string.Equals(token, "RUN", StringComparison.OrdinalIgnoreCase))
            {
                var regularRun = matches.FirstOrDefault(clip =>
                    clip.name.IndexOf("ATTACK", StringComparison.OrdinalIgnoreCase) < 0);
                if (regularRun != null)
                    return regularRun;
            }
            return matches.FirstOrDefault();
        }

        private static GameObject GetProjectilePrefab(string modelName)
        {
            if (modelName.IndexOf("cungky", StringComparison.OrdinalIgnoreCase) >= 0)
                return AssetDatabase.LoadAssetAtPath<GameObject>(ArrowPrefabPath);
            return null;
        }

        private static ProjectileSetupOverrides CaptureProjectileSetupOverrides(string prefabPath)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var presentation = prefab != null
                ? prefab.GetComponent<RecoilCharacterPresentation>()
                : null;
            if (presentation == null || presentation.ProjectileSpawnPoint == null)
                return default;

            return new ProjectileSetupOverrides
            {
                HasSettings = true,
                SpawnPointLocalPosition = presentation.ProjectileSpawnPoint.localPosition,
                ReleaseNormalizedTime = presentation.ProjectileReleaseNormalizedTime,
                ProjectileSpeed = presentation.ProjectileSpeed,
                ProjectileScale = presentation.ProjectileScale,
                ProjectilePrefab = presentation.ProjectilePrefab
            };
        }

        private float GetTargetHeight(CharacterGroup group)
        {
            switch (group)
            {
                case CharacterGroup.Cavalry: return _cavalryHeight;
                case CharacterGroup.Elephant: return _elephantHeight;
                case CharacterGroup.Siege: return _siegeHeight;
                default: return _infantryHeight;
            }
        }

        private static CharacterGroup GetCharacterGroup(string modelName)
        {
            var name = modelName.ToLowerInvariant();
            if (name.Contains("voi"))
                return CharacterGroup.Elephant;
            if (name.Contains("mayda"))
                return CharacterGroup.Siege;
            if (name.Contains("kybinh") || name.Contains("horse"))
                return CharacterGroup.Cavalry;
            return CharacterGroup.Infantry;
        }

        private static float GetCharacterYawOffset(string modelName, CharacterGroup group)
        {
            if (group != CharacterGroup.Siege)
                return 0f;
            if (modelName.EndsWith("_blue", StringComparison.OrdinalIgnoreCase) ||
                modelName.EndsWith("_red", StringComparison.OrdinalIgnoreCase))
                return 90f;
            return 0f;
        }

        private static bool IsBlueArcher(string modelName) =>
            modelName.IndexOf("cungky", StringComparison.OrdinalIgnoreCase) >= 0 &&
            modelName.EndsWith("_blue", StringComparison.OrdinalIgnoreCase);

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
                return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            var folderName = Path.GetFileName(path);
            if (string.IsNullOrEmpty(parent))
                throw new InvalidOperationException($"Không thể xác định thư mục cha của '{path}'.");
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, folderName);
        }

        private enum CharacterGroup
        {
            Infantry,
            Cavalry,
            Elephant,
            Siege
        }
    }
}
