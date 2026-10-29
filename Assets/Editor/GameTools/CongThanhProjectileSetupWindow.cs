using System;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameToolsEditor
{
    public sealed class CongThanhProjectileSetupWindow : EditorWindow
    {
        private const string PrefabFolder = "Assets/Data/Characters/Prefabs";
        private string[] _prefabPaths = Array.Empty<string>();
        private string[] _prefabNames = Array.Empty<string>();
        private int _selectedPrefabIndex = -1;
        private Vector3 _spawnLocalPosition;
        private int _releaseFrame = 1;
        private GameObject _projectilePrefab;
        private float _projectileSpeed = 24f;
        private float _projectileScale = 1f;
        private string _status = string.Empty;
        private Vector2 _scrollPosition;

        private Scene _previewScene;
        private GameObject _previewRoot;
        private GameObject _previewModel;
        private GameObject _previewProjectile;
        private Transform _previewSpawnPoint;
        private AnimationClip _previewAttackClip;
        private bool _ownsAnimationMode;
        private UnityEngine.Object _previousSelection;
        private float _lastSampleTime = -1f;

        [MenuItem("Game Tools/Công Thành/Căn điểm bắn")]
        private static void Open()
        {
            var window = GetWindow<CongThanhProjectileSetupWindow>();
            window.titleContent = new GUIContent("Công Thành - Căn điểm bắn");
            window.minSize = new Vector2(420f, 260f);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshTargets();
        }

        private void OnDisable()
        {
            ClosePreview();
        }

        private void OnGUI()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scrollPosition))
            {
                EditorGUILayout.LabelField("Căn điểm bắn và frame nhả đạn", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox(
                    "Chọn prefab, mở preview hoạt ảnh Attack rồi kéo gizmo tại Scene View để đặt điểm xuất phát. Frame được tính từ 1; cấu hình chỉ lưu vào prefab đang chọn.",
                    MessageType.Info);
                if (GUILayout.Button("Quét lại danh sách prefab"))
                    RefreshTargets();

                if (_prefabPaths.Length == 0)
                {
                    EditorGUILayout.HelpBox(
                        $"Không tìm thấy prefab cung thủ hoặc máy bắn đá trong '{PrefabFolder}'. Hãy tạo prefab trước.",
                        MessageType.Warning);
                    _scrollPosition = scroll.scrollPosition;
                    return;
                }

                _selectedPrefabIndex = Mathf.Clamp(
                    _selectedPrefabIndex, 0, _prefabPaths.Length - 1);
                EditorGUI.BeginChangeCheck();
                var selectedIndex = EditorGUILayout.Popup(
                    "Prefab cần căn", _selectedPrefabIndex, _prefabNames);
                if (EditorGUI.EndChangeCheck())
                {
                    ClosePreview();
                    _selectedPrefabIndex = selectedIndex;
                    _status = string.Empty;
                    LoadTargetSettings();
                }

                var prefabPath = _prefabPaths[_selectedPrefabIndex];
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var presentation = prefab != null
                    ? prefab.GetComponent<RecoilCharacterPresentation>()
                    : null;
                var attackClip = presentation != null ? presentation.AttackClip : null;
                if (presentation == null || attackClip == null)
                {
                    EditorGUILayout.HelpBox(
                        "Prefab đang chọn không có RecoilCharacterPresentation hoặc chưa gán clip Attack.",
                        MessageType.Error);
                    _scrollPosition = scroll.scrollPosition;
                    return;
                }

                if (!HasValidFrameRate(attackClip))
                {
                    EditorGUILayout.HelpBox(
                        "Clip Attack phải có thời lượng và tốc độ khung hình hợp lệ để căn frame nhả đạn.",
                        MessageType.Error);
                    _scrollPosition = scroll.scrollPosition;
                    return;
                }

                var frameCount = GetFrameCount(attackClip);
                EditorGUILayout.LabelField(
                    "Clip Attack",
                    $"{attackClip.name} | {attackClip.frameRate:F2} FPS | {frameCount} frame");
                using (new EditorGUI.DisabledScope(
                           EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    EditorGUI.BeginChangeCheck();
                    _projectilePrefab = (GameObject)EditorGUILayout.ObjectField(
                        "Prefab đạn", _projectilePrefab, typeof(GameObject), false);
                    _projectileSpeed = EditorGUILayout.FloatField(
                        "Tốc độ đạn", _projectileSpeed);
                    _projectileScale = EditorGUILayout.FloatField(
                        "Tỉ lệ kích thước đạn", _projectileScale);
                    if (EditorGUI.EndChangeCheck())
                        UpdateProjectilePreview();
                }
                if (_projectilePrefab == null)
                    EditorGUILayout.HelpBox(
                        "Chưa gán prefab đạn; lưu cấu hình sẽ tắt việc tạo đạn cho nhân vật này.",
                        MessageType.Warning);
                else if (!IsFinite(_projectileScale) || _projectileScale <= 0f)
                    EditorGUILayout.HelpBox(
                        "Tỉ lệ kích thước đạn phải là số hữu hạn lớn hơn 0.",
                        MessageType.Warning);
                else
                    EditorGUILayout.HelpBox(
                        $"Đạn preview: {_projectilePrefab.name} | kích thước: {_projectileScale:F2}x",
                        MessageType.None);

                var editingDisabled = EditorApplication.isPlayingOrWillChangePlaymode;
                using (new EditorGUI.DisabledScope(editingDisabled))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PrefixLabel("Frame nhả đạn");
                        if (GUILayout.Button("◀ Prev", GUILayout.Width(70f)))
                            SetReleaseFrame(_releaseFrame - 1, frameCount);

                        EditorGUI.BeginChangeCheck();
                        var frame = EditorGUILayout.IntField(_releaseFrame);
                        if (EditorGUI.EndChangeCheck())
                            SetReleaseFrame(frame, frameCount);

                        if (GUILayout.Button("Next ▶", GUILayout.Width(70f)))
                            SetReleaseFrame(_releaseFrame + 1, frameCount);
                    }

                    EditorGUI.BeginChangeCheck();
                    var spawnPosition = EditorGUILayout.Vector3Field(
                        "Vị trí điểm bắn (tọa độ local)", _spawnLocalPosition);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _spawnLocalPosition = spawnPosition;
                        if (_previewSpawnPoint != null)
                            _previewSpawnPoint.localPosition = spawnPosition;
                        UpdateProjectilePreview();
                        SceneView.RepaintAll();
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (_previewRoot == null)
                    {
                        using (new EditorGUI.DisabledScope(editingDisabled))
                        {
                            if (GUILayout.Button("Mở preview và căn bằng gizmo"))
                                OpenPreview(prefabPath);
                        }
                    }
                    else if (GUILayout.Button("Đóng preview"))
                    {
                        ClosePreview();
                    }

                    using (new EditorGUI.DisabledScope(editingDisabled))
                    {
                        if (GUILayout.Button("Lưu vào prefab"))
                            SaveTarget(prefabPath);
                    }
                }

                if (_previewRoot != null)
                    EditorGUILayout.HelpBox(
                        "Preview đang mở ở Scene View. Kéo gizmo tại điểm nhả đạn; có thể nhập tọa độ hoặc frame ở trên để tinh chỉnh.",
                        MessageType.Info);
                if (!string.IsNullOrEmpty(_status))
                    EditorGUILayout.HelpBox(_status, MessageType.Info);

                _scrollPosition = scroll.scrollPosition;
            }
        }

        private void SetReleaseFrame(int frame, int frameCount)
        {
            var clampedFrame = Mathf.Clamp(frame, 1, frameCount);
            if (clampedFrame == _releaseFrame)
                return;

            _releaseFrame = clampedFrame;
            SamplePreviewFrame();
            UpdateProjectilePreview();
            Repaint();
        }

        private void RefreshTargets()
        {
            var previousPath = _selectedPrefabIndex >= 0 &&
                               _selectedPrefabIndex < _prefabPaths.Length
                ? _prefabPaths[_selectedPrefabIndex]
                : null;
            _prefabPaths = AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path =>
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    return name.IndexOf("cungky", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           name.IndexOf("mayda", StringComparison.OrdinalIgnoreCase) >= 0;
                })
                .Where(path =>
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    var presentation = prefab != null
                        ? prefab.GetComponent<RecoilCharacterPresentation>()
                        : null;
                    return presentation != null &&
                           presentation.ProjectileSpawnPoint != null &&
                           presentation.AttackClip != null;
                })
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _prefabNames = _prefabPaths.Select(Path.GetFileNameWithoutExtension).ToArray();

            _selectedPrefabIndex = Array.IndexOf(_prefabPaths, previousPath);
            if (_selectedPrefabIndex < 0 && _prefabPaths.Length > 0)
                _selectedPrefabIndex = 0;
            if (_selectedPrefabIndex >= 0)
                LoadTargetSettings();
            Repaint();
        }

        private void LoadTargetSettings()
        {
            if (_selectedPrefabIndex < 0 || _selectedPrefabIndex >= _prefabPaths.Length)
                return;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                _prefabPaths[_selectedPrefabIndex]);
            var presentation = prefab != null
                ? prefab.GetComponent<RecoilCharacterPresentation>()
                : null;
            if (presentation == null || presentation.ProjectileSpawnPoint == null)
                return;

            _spawnLocalPosition = presentation.ProjectileSpawnPoint.localPosition;
            _releaseFrame = GetFrameFromNormalizedTime(
                presentation.ProjectileReleaseNormalizedTime, presentation.AttackClip);
            _projectilePrefab = presentation.ProjectilePrefab;
            _projectileSpeed = presentation.ProjectileSpeed;
            _projectileScale = presentation.ProjectileScale;
        }

        private void OpenPreview(string prefabPath)
        {
            ClosePreview();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                throw new InvalidOperationException($"Không thể tải prefab preview '{prefabPath}'.");

            _previewScene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                _previewRoot = PrefabUtility.InstantiatePrefab(prefab, _previewScene) as GameObject;
                if (_previewRoot == null)
                    throw new InvalidOperationException(
                        $"Không thể tạo bản preview từ prefab '{prefabPath}'.");

                _previewRoot.name = $"[Preview] {prefab.name}";
                _previewRoot.transform.position = new Vector3(1000f, 0f, 0f);
                var presentation = _previewRoot.GetComponent<RecoilCharacterPresentation>();
                if (presentation == null || presentation.ProjectileSpawnPoint == null)
                    throw new InvalidOperationException(
                        $"Prefab '{prefabPath}' không có điểm bắn hợp lệ.");

                _previewSpawnPoint = presentation.ProjectileSpawnPoint;
                _previewSpawnPoint.localPosition = _spawnLocalPosition;
                _previewAttackClip = presentation.AttackClip;
                _previewModel = presentation.VisualRoot != null
                    ? presentation.VisualRoot.Find("Visual")?.gameObject
                    : null;
                if (_previewModel == null && presentation.Animator != null)
                    _previewModel = presentation.Animator.transform.root.gameObject;
                if (_previewModel == null)
                    throw new InvalidOperationException(
                        $"Không thể tìm model để preview hoạt ảnh Attack trong '{prefabPath}'.");

                _previousSelection = Selection.activeObject;
                if (!AnimationMode.InAnimationMode())
                {
                    AnimationMode.StartAnimationMode();
                    _ownsAnimationMode = true;
                }

                _lastSampleTime = -1f;
                SamplePreviewFrame();
                UpdateProjectilePreview();
                SceneView.duringSceneGui += DrawSceneHandles;

                var renderers = _previewRoot.GetComponentsInChildren<Renderer>(true);
                var bounds = renderers.Length > 0
                    ? renderers[0].bounds
                    : new Bounds(_previewRoot.transform.position, Vector3.one);
                for (var i = 1; i < renderers.Length; i++)
                    bounds.Encapsulate(renderers[i].bounds);
                bounds.Encapsulate(_previewSpawnPoint.position);
                if (_previewProjectile != null)
                {
                    foreach (var renderer in _previewProjectile.GetComponentsInChildren<Renderer>(true))
                        bounds.Encapsulate(renderer.bounds);
                }

                Selection.activeGameObject = _previewRoot;
                var sceneView = SceneView.lastActiveSceneView ?? EditorWindow.GetWindow<SceneView>();
                sceneView.Frame(bounds, false);
                sceneView.Repaint();
            }
            catch
            {
                ClosePreview();
                throw;
            }
        }

        private void SamplePreviewFrame()
        {
            if (_previewModel == null || _previewAttackClip == null ||
                !AnimationMode.InAnimationMode())
                return;

            var sampleTime = GetFrameTime(_releaseFrame, _previewAttackClip);
            if (Mathf.Approximately(sampleTime, _lastSampleTime))
                return;

            AnimationMode.BeginSampling();
            try
            {
                AnimationMode.SampleAnimationClip(
                    _previewModel, _previewAttackClip, sampleTime);
                var presentation = _previewRoot.GetComponent<RecoilCharacterPresentation>();
                if (presentation != null && presentation.VisualRoot != null)
                    presentation.VisualRoot.localRotation = Quaternion.Euler(
                        0f, presentation.AttackForwardYawOffset, 0f);
                _lastSampleTime = sampleTime;
            }
            finally
            {
                AnimationMode.EndSampling();
            }
            SceneView.RepaintAll();
        }

        private void UpdateProjectilePreview()
        {
            if (_previewRoot == null || _previewSpawnPoint == null)
                return;

            if (_previewProjectile != null &&
                PrefabUtility.GetCorrespondingObjectFromSource(_previewProjectile) != _projectilePrefab)
            {
                DestroyImmediate(_previewProjectile);
                _previewProjectile = null;
            }
            if (_projectilePrefab == null)
            {
                if (_previewProjectile != null)
                    DestroyImmediate(_previewProjectile);
                _previewProjectile = null;
                SceneView.RepaintAll();
                return;
            }
            if (!IsFinite(_projectileScale) || _projectileScale <= 0f)
            {
                if (_previewProjectile != null)
                    DestroyImmediate(_previewProjectile);
                _previewProjectile = null;
                return;
            }

            if (_previewProjectile == null)
            {
                _previewProjectile = PrefabUtility.InstantiatePrefab(
                    _projectilePrefab, _previewScene) as GameObject;
                if (_previewProjectile == null)
                    throw new InvalidOperationException(
                        $"Không thể tạo preview từ prefab đạn '{_projectilePrefab.name}'.");
            }

            _previewProjectile.name = $"[Preview] {_projectilePrefab.name}";
            _previewProjectile.transform.SetPositionAndRotation(
                _previewSpawnPoint.position, GetPreviewProjectileRotation());
            _previewProjectile.transform.localScale =
                _projectilePrefab.transform.localScale * _projectileScale;
            SceneView.RepaintAll();
        }

        private Quaternion GetPreviewProjectileRotation()
        {
            var presentation = _previewRoot != null
                ? _previewRoot.GetComponent<RecoilCharacterPresentation>()
                : null;
            var attackYaw = presentation != null
                ? presentation.AttackForwardYawOffset
                : 0f;
            return _previewSpawnPoint.rotation * Quaternion.Euler(0f, attackYaw, 0f);
        }

        private void DrawSceneHandles(SceneView sceneView)
        {
            if (_previewRoot == null || _previewSpawnPoint == null)
                return;

            var colorBefore = Handles.color;
            Handles.color = new Color(0.2f, 0.9f, 1f, 1f);
            var handleSize = HandleUtility.GetHandleSize(_previewSpawnPoint.position);
            Handles.SphereHandleCap(
                0, _previewSpawnPoint.position, Quaternion.identity,
                handleSize * 0.12f, EventType.Repaint);
            Handles.ArrowHandleCap(
                0, _previewSpawnPoint.position, _previewSpawnPoint.rotation,
                handleSize * 0.6f, EventType.Repaint);
            Handles.Label(_previewSpawnPoint.position, "Điểm nhả đạn");
            if (_previewProjectile != null)
            {
                var name = _projectilePrefab != null ? _projectilePrefab.name : "Đạn";
                Handles.Label(
                    GetPreviewProjectileBounds().center,
                    $"{name} x{_projectileScale:F2}");
            }

            var handleRotation = Tools.pivotRotation == PivotRotation.Local
                ? _previewSpawnPoint.parent.rotation
                : Quaternion.identity;
            EditorGUI.BeginChangeCheck();
            var worldPosition = Handles.PositionHandle(
                _previewSpawnPoint.position, handleRotation);
            if (EditorGUI.EndChangeCheck())
            {
                _previewSpawnPoint.position = worldPosition;
                _spawnLocalPosition =
                    _previewSpawnPoint.parent.InverseTransformPoint(worldPosition);
                UpdateProjectilePreview();
                Repaint();
            }
            Handles.color = colorBefore;
        }

        private void SaveTarget(string prefabPath)
        {
            var prefabContents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var presentation = prefabContents.GetComponent<RecoilCharacterPresentation>();
                if (presentation == null || presentation.ProjectileSpawnPoint == null)
                    throw new InvalidOperationException(
                        $"Prefab '{prefabPath}' không còn điểm bắn để lưu.");
                if (!HasValidFrameRate(presentation.AttackClip))
                    throw new InvalidOperationException(
                        $"Prefab '{prefabPath}' không có clip Attack hợp lệ để lưu frame nhả đạn.");
                if (!IsFinite(_spawnLocalPosition.x) ||
                    !IsFinite(_spawnLocalPosition.y) ||
                    !IsFinite(_spawnLocalPosition.z))
                    throw new InvalidOperationException(
                        "Tọa độ điểm bắn phải là các giá trị hữu hạn.");

                presentation.ProjectileSpawnPoint.localPosition = _spawnLocalPosition;
                var serializedPresentation = new SerializedObject(presentation);
                var releaseTime = GetFrameTime(
                    _releaseFrame, presentation.AttackClip) /
                                  presentation.AttackClip.length;
                var releaseTimeProperty =
                    serializedPresentation.FindProperty("projectileReleaseNormalizedTime");
                if (releaseTimeProperty == null)
                    throw new InvalidOperationException(
                        $"Prefab '{prefabPath}' không có trường thời điểm nhả đạn.");
                releaseTimeProperty.floatValue = Mathf.Clamp01(releaseTime);

                if (!float.IsFinite(_projectileSpeed) || _projectileSpeed <= 0f)
                    throw new InvalidOperationException("Tốc độ đạn phải là số hữu hạn lớn hơn 0.");
                if (!IsFinite(_projectileScale) || _projectileScale <= 0f)
                    throw new InvalidOperationException(
                        "Tỉ lệ kích thước đạn phải là số hữu hạn lớn hơn 0.");
                var projectileProperty =
                    serializedPresentation.FindProperty("projectilePrefab");
                if (projectileProperty == null)
                    throw new InvalidOperationException(
                        $"Prefab '{prefabPath}' không có trường prefab đạn.");
                projectileProperty.objectReferenceValue = _projectilePrefab;
                var projectileScaleProperty =
                    serializedPresentation.FindProperty("projectileScale");
                if (projectileScaleProperty == null)
                    throw new InvalidOperationException(
                        $"Prefab '{prefabPath}' không có trường tỉ lệ kích thước đạn.");
                projectileScaleProperty.floatValue = _projectileScale;
                var projectileSpeedProperty =
                    serializedPresentation.FindProperty("projectileSpeed");
                if (projectileSpeedProperty == null)
                    throw new InvalidOperationException(
                        $"Prefab '{prefabPath}' không có trường tốc độ đạn.");
                projectileSpeedProperty.floatValue = _projectileSpeed;

                serializedPresentation.ApplyModifiedPropertiesWithoutUndo();
                if (PrefabUtility.SaveAsPrefabAsset(prefabContents, prefabPath) == null)
                    throw new InvalidOperationException(
                        $"Unity không thể lưu prefab '{prefabPath}'.");

                _status =
                    $"Đã lưu điểm bắn và frame {_releaseFrame} vào prefab {Path.GetFileNameWithoutExtension(prefabPath)}.";
                Debug.Log(_status);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabContents);
            }

            RefreshTargets();
            if (_previewSpawnPoint != null)
                _previewSpawnPoint.localPosition = _spawnLocalPosition;
        }

        private void ClosePreview()
        {
            SceneView.duringSceneGui -= DrawSceneHandles;
            if (_ownsAnimationMode && AnimationMode.InAnimationMode())
                AnimationMode.StopAnimationMode();
            _ownsAnimationMode = false;
            _previewRoot = null;
            _previewModel = null;
            _previewProjectile = null;
            _previewSpawnPoint = null;
            _previewAttackClip = null;
            _lastSampleTime = -1f;

            if (_previewScene.IsValid() && _previewScene.isLoaded)
                EditorSceneManager.CloseScene(_previewScene, true);
            _previewScene = default;

            if (_previousSelection != null)
                Selection.activeObject = _previousSelection;
            _previousSelection = null;
            SceneView.RepaintAll();
        }

        private Bounds GetPreviewProjectileBounds()
        {
            if (_previewProjectile == null)
                return new Bounds(_previewSpawnPoint.position, Vector3.zero);

            var renderers = _previewProjectile.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return new Bounds(_previewProjectile.transform.position, Vector3.zero);

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private static bool HasValidFrameRate(AnimationClip clip) =>
            clip != null &&
            float.IsFinite(clip.length) &&
            clip.length > 0f &&
            float.IsFinite(clip.frameRate) &&
            clip.frameRate > 0f;

        private static int GetFrameCount(AnimationClip clip)
        {
            if (!HasValidFrameRate(clip))
                return 1;
            return Mathf.Max(1, Mathf.CeilToInt(clip.length * clip.frameRate));
        }

        private static int GetFrameFromNormalizedTime(float normalizedTime, AnimationClip clip)
        {
            if (!HasValidFrameRate(clip))
                return 1;
            var frame = Mathf.RoundToInt(normalizedTime * clip.length * clip.frameRate) + 1;
            return Mathf.Clamp(frame, 1, GetFrameCount(clip));
        }

        private static float GetFrameTime(int frame, AnimationClip clip)
        {
            if (!HasValidFrameRate(clip))
                return 0f;
            return Mathf.Clamp(
                (Mathf.Clamp(frame, 1, GetFrameCount(clip)) - 1) / clip.frameRate,
                0f,
                Mathf.Max(0f, clip.length - 0.0001f));
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
