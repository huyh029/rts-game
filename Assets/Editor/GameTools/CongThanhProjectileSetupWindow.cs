using System;
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
    public sealed class CongThanhProjectileSetupWindow : EditorWindow
    {
        [SerializeField] private GameObject _target;
        private bool _isRanged;
        private AnimationClip _firingClip;
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
            LoadTargetSettings();
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
                    "Kéo nhân vật từ Hierarchy hoặc Project vào ô bên dưới, tích Đánh xa rồi mở preview để căn điểm bắn bằng gizmo. Frame được tính từ 1. Cấu hình lưu vào đúng nhân vật đã chọn.",
                    MessageType.Info);
                EditorGUI.BeginChangeCheck();
                GameObject selected;
                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                    selected = (GameObject)EditorGUILayout.ObjectField("Nhân vật cần căn", _target, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck())
                {
                    ClosePreview();
                    _target = selected;
                    _status = string.Empty;
                    LoadTargetSettings();
                }
                if (_target == null)
                {
                    EditorGUILayout.HelpBox("Chọn nhân vật để thiết lập đánh xa và căn điểm bắn.", MessageType.Info);
                    _scrollPosition = scroll.scrollPosition;
                    return;
                }
                var presentation = _target.GetComponent<RecoilCharacterPresentation>();
                if (presentation == null)
                {
                    EditorGUILayout.HelpBox(
                        "Hãy chọn object gốc nhân vật có RecoilCharacterPresentation.",
                        MessageType.Error);
                    _scrollPosition = scroll.scrollPosition;
                    return;
                }

                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                    _isRanged = EditorGUILayout.ToggleLeft("Đánh xa", _isRanged);
                if (!_isRanged)
                {
                    if (_previewRoot != null) ClosePreview();
                    EditorGUILayout.HelpBox("Nhân vật sẽ đánh cận chiến. Cấu hình đạn được giữ lại để có thể bật lại sau.", MessageType.Info);
                    using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                        if (GUILayout.Button("Lưu cấu hình nhân vật")) SaveTarget();
                    if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, MessageType.Info);
                    _scrollPosition = scroll.scrollPosition;
                    return;
                }

                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    EditorGUI.BeginChangeCheck();
                    _firingClip = (AnimationClip)EditorGUILayout.ObjectField("Hoạt ảnh bắn", _firingClip, typeof(AnimationClip), false);
                    if (EditorGUI.EndChangeCheck())
                    {
                        ClosePreview();
                        _releaseFrame = 1;
                    }
                }
                var attackClip = _firingClip;

                if (!HasValidFrameRate(attackClip))
                {
                    EditorGUILayout.HelpBox(
                        "Chọn hoạt ảnh bắn có thời lượng và tốc độ khung hình hợp lệ. Máy bắn đá có thể dùng clip Scene.",
                        MessageType.Error);
                    _scrollPosition = scroll.scrollPosition;
                    return;
                }

                var frameCount = GetFrameCount(attackClip);
                EditorGUILayout.LabelField(
                    "Hoạt ảnh bắn",
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
                                OpenPreview();
                        }
                    }
                    else if (GUILayout.Button("Đóng preview"))
                    {
                        ClosePreview();
                    }

                    using (new EditorGUI.DisabledScope(editingDisabled))
                    {
                        if (GUILayout.Button("Lưu cấu hình nhân vật"))
                            SaveTarget();
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

        private void LoadTargetSettings()
        {
            _spawnLocalPosition = Vector3.zero;
            _releaseFrame = 1;
            _projectilePrefab = null;
            _projectileSpeed = 24f;
            _projectileScale = 1f;
            _isRanged = false;
            _firingClip = null;
            var presentation = _target != null ? _target.GetComponent<RecoilCharacterPresentation>() : null;
            if (presentation == null) return;
            _isRanged = presentation.IsRangedAttack;
            _firingClip = presentation.AttackClip;
            if (_firingClip == null && presentation.Animator != null && presentation.Animator.runtimeAnimatorController != null)
                _firingClip = presentation.Animator.runtimeAnimatorController.animationClips
                    .FirstOrDefault(clip => string.Equals(clip.name, "Scene", StringComparison.OrdinalIgnoreCase));
            if (presentation.ProjectileSpawnPoint != null)
                _spawnLocalPosition = presentation.ProjectileSpawnPoint.localPosition;
            _releaseFrame = GetFrameFromNormalizedTime(presentation.ProjectileReleaseNormalizedTime, _firingClip);
            _projectilePrefab = presentation.ProjectilePrefab;
            _projectileSpeed = presentation.ProjectileSpeed;
            _projectileScale = presentation.ProjectileScale;
        }
        private void OpenPreview()
        {
            ClosePreview();
            var prefab = _target;
            if (prefab == null)
                return;

            _previewScene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                if (EditorUtility.IsPersistent(prefab))
                    _previewRoot = PrefabUtility.InstantiatePrefab(prefab, _previewScene) as GameObject;
                else
                {
                    _previewRoot = Instantiate(prefab);
                    SceneManager.MoveGameObjectToScene(_previewRoot, _previewScene);
                }
                if (_previewRoot == null)
                    throw new InvalidOperationException(
                        $"Không thể tạo preview từ '{prefab.name}'.");

                _previewRoot.name = $"[Preview] {prefab.name}";
                _previewRoot.transform.position = new Vector3(1000f, 0f, 0f);
                _previewRoot.SetActive(true);
                foreach (var behaviour in _previewRoot.GetComponentsInChildren<MonoBehaviour>(true))
                    if (behaviour != null) behaviour.enabled = false;
                foreach (var group in _previewRoot.GetComponentsInChildren<LODGroup>(true)) group.ForceLOD(0);
                var presentation = _previewRoot.GetComponent<RecoilCharacterPresentation>();
                if (presentation == null)
                    throw new InvalidOperationException(
                        "Nhân vật không có cấu hình hiển thị.");

                _previewSpawnPoint = EnsureSpawnPoint(presentation, false);
                _previewSpawnPoint.localPosition = _spawnLocalPosition;
                _previewAttackClip = _firingClip;
                _previewModel = presentation.Animator != null ? presentation.Animator.gameObject : null;
                if (_previewModel == null)
                    throw new InvalidOperationException(
                        $"Không thể tìm Animator để preview Attack trong '{prefab.name}'.");

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
                        0f, presentation.ForwardYawOffset, 0f);
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
                ? presentation.ForwardYawOffset
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

        private static Transform EnsureSpawnPoint(RecoilCharacterPresentation presentation, bool undo)
        {
            if (presentation.ProjectileSpawnPoint != null) return presentation.ProjectileSpawnPoint;
            var point = new GameObject("Projectile Spawn Point");
            if (undo) Undo.RegisterCreatedObjectUndo(point, "Tạo điểm bắn");
            point.transform.SetParent(presentation.transform, false);
            var serialized = new SerializedObject(presentation);
            serialized.FindProperty("projectileSpawnPoint").objectReferenceValue = point.transform;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return point.transform;
        }

        private void SaveTarget()
        {
            if (_target == null || EditorApplication.isPlayingOrWillChangePlaymode) return;
            GameObject contents = null;
            try
            {
                var persistent = EditorUtility.IsPersistent(_target);
                var path = persistent ? AssetDatabase.GetAssetPath(_target) : null;
                if (persistent && !string.Equals(Path.GetExtension(path), ".prefab", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Chọn prefab .prefab hoặc nhân vật trong Hierarchy; không lưu trực tiếp vào model FBX.");
                if (persistent) contents = PrefabUtility.LoadPrefabContents(path);
                var root = contents != null ? contents : _target;
                var presentation = root.GetComponent<RecoilCharacterPresentation>();
                if (presentation == null) throw new InvalidOperationException("Nhân vật cần có RecoilCharacterPresentation.");
                ApplyTargetSettings(presentation, !persistent);
                if (persistent)
                {
                    PrefabUtility.SaveAsPrefabAsset(contents, path, out var success);
                    if (!success) throw new InvalidOperationException("Không thể lưu prefab.");
                }
                else
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(presentation);
                    EditorSceneManager.MarkSceneDirty(root.scene);
                }
                if (_isRanged && presentation.Animator != null)
                    AssetDatabase.SaveAssetIfDirty(presentation.Animator.runtimeAnimatorController);
                _status = "Đã lưu " + (_isRanged ? "đánh xa và điểm bắn" : "đánh cận chiến") +
                    " cho " + _target.name + (persistent ? "." : ". Hãy lưu scene để giữ cấu hình.");
                LoadTargetSettings();
            }
            catch (Exception exception)
            {
                _status = "Lưu thất bại: " + exception.Message;
                Debug.LogException(exception);
            }
            finally
            {
                if (contents != null) PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private void ApplyTargetSettings(RecoilCharacterPresentation presentation, bool undo)
        {
            if (_isRanged)
            {
                if (!HasValidFrameRate(_firingClip))
                    throw new InvalidOperationException("Chưa chọn hoạt ảnh bắn hợp lệ.");
                if (!IsFinite(_spawnLocalPosition.x) || !IsFinite(_spawnLocalPosition.y) || !IsFinite(_spawnLocalPosition.z))
                    throw new InvalidOperationException("Tọa độ điểm bắn phải hữu hạn.");
                if (!IsFinite(_projectileSpeed) || _projectileSpeed <= 0f || !IsFinite(_projectileScale) || _projectileScale <= 0f)
                    throw new InvalidOperationException("Tốc độ và tỉ lệ đạn phải hữu hạn và lớn hơn 0.");
            }
            if (undo) Undo.RecordObject(presentation, "Cấu hình đánh xa");
            var serialized = new SerializedObject(presentation);
            serialized.FindProperty("useRangedAttackOverride").boolValue = true;
            serialized.FindProperty("rangedAttack").boolValue = _isRanged;
            if (_isRanged)
            {
                ConfigureFiringAnimation(presentation, _firingClip);
                var point = EnsureSpawnPoint(presentation, undo);
                if (undo) Undo.RecordObject(point, "Căn điểm bắn");
                point.localPosition = _spawnLocalPosition;
                if (undo) PrefabUtility.RecordPrefabInstancePropertyModifications(point);
                serialized.Update();
                serialized.FindProperty("useRangedAttackOverride").boolValue = true;
                serialized.FindProperty("rangedAttack").boolValue = true;
                serialized.FindProperty("attackClip").objectReferenceValue = _firingClip;
                serialized.FindProperty("projectileReleaseNormalizedTime").floatValue =
                    Mathf.Clamp01(GetFrameTime(_releaseFrame, _firingClip) / _firingClip.length);
                serialized.FindProperty("projectilePrefab").objectReferenceValue = _projectilePrefab;
                serialized.FindProperty("projectileSpeed").floatValue = _projectileSpeed;
                serialized.FindProperty("projectileScale").floatValue = _projectileScale;
            }
            if (undo) serialized.ApplyModifiedProperties();
            else serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        internal static void ConfigureFiringAnimation(RecoilCharacterPresentation presentation, AnimationClip clip)
        {
            var animator = presentation.Animator;
            var controller = animator != null ? animator.runtimeAnimatorController as AnimatorController : null;
            if (controller == null || controller.layers.Length == 0)
                throw new InvalidOperationException("Nhân vật cần Animator Controller để phát hoạt ảnh bắn.");
            var serialized = new SerializedObject(presentation);
            var stateName = serialized.FindProperty("attackStateName").stringValue;
            if (string.IsNullOrWhiteSpace(stateName)) stateName = "Attack";
            var machine = controller.layers[0].stateMachine;
            var attack = machine.states.Select(child => child.state).FirstOrDefault(state => state.name == stateName);
            if (attack == null)
            {
                Undo.RegisterCompleteObjectUndo(machine, "Gán hoạt ảnh bắn");
                attack = machine.AddState(stateName);
                var idle = machine.defaultState;
                if (idle != null && idle != attack)
                {
                    if (idle.motion == clip)
                    {
                        Undo.RecordObject(idle, "Không tự bắn trong trạng thái nghỉ");
                        idle.motion = null;
                        EditorUtility.SetDirty(idle);
                    }
                    var back = attack.AddTransition(idle);
                    back.hasExitTime = true;
                    back.exitTime = 1f;
                    back.duration = 0.05f;
                    back.hasFixedDuration = true;
                }
            }
            Undo.RecordObject(attack, "Gán hoạt ảnh bắn");
            attack.motion = clip;
            serialized.FindProperty("attackClip").objectReferenceValue = clip;
            serialized.FindProperty("attackStateName").stringValue = stateName;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(attack);
            EditorUtility.SetDirty(controller);
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
