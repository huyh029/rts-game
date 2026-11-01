using System;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace GameToolsEditor
{
    public sealed class CongThanhSpacingSetupWindow : EditorWindow
    {
        private const string PrefabFolder = "Assets/Data/Characters/Prefabs";
        private string[] _paths = Array.Empty<string>();
        private string[] _names = Array.Empty<string>();
        private int _selected;
        private float _bodyRadius = 0.5f;
        private float _avoidanceRadius = 1.2f;
        private string _status;
        private PreviewRenderUtility _preview;
        private GameObject _previewModel;
        private Mesh _ringMesh;
        private Material _bodyMaterial;
        private Material _avoidanceMaterial;
        private Bounds _previewBounds;
        private Vector3 _previewCenter;
        private Vector2 _orbit = new Vector2(35f, 25f);
        private float _zoom = 1f;
        private Vector2 _scroll;

        [MenuItem("Game Tools/Công Thành/Thiết lập khoảng cách lính")]
        private static void Open()
        {
            var window = GetWindow<CongThanhSpacingSetupWindow>("Khoảng cách lính");
            window.minSize = new Vector2(480f, 650f);
            window.Show();
        }

        private void OnEnable() => RefreshTargets();
        private void OnDisable() => ClosePreview();

        private void ClosePreview()
        {
            _preview?.Cleanup();
            _preview = null;
            _previewModel = null;
            if (_ringMesh != null) DestroyImmediate(_ringMesh);
            if (_bodyMaterial != null) DestroyImmediate(_bodyMaterial);
            if (_avoidanceMaterial != null) DestroyImmediate(_avoidanceMaterial);
        }

        private void CreatePreview(GameObject prefab)
        {
            ClosePreview();
            if (prefab == null) return;
            _preview = new PreviewRenderUtility();
            _preview.camera.backgroundColor = new Color(0.12f, 0.14f, 0.17f);
            _preview.camera.clearFlags = CameraClearFlags.SolidColor;
            _preview.camera.fieldOfView = 35f;
            _preview.lights[0].intensity = 1.2f;
            _preview.lights[0].transform.rotation = Quaternion.Euler(40f, 30f, 0f);
            _preview.lights[1].intensity = 0.7f;
            _preview.ambientColor = new Color(0.4f, 0.4f, 0.4f);
            _previewModel = _preview.InstantiatePrefabInScene(prefab);
            foreach (var behaviour in _previewModel.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != null) behaviour.enabled = false;
            _previewModel.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _previewModel.SetActive(true);
            foreach (var presentation in _previewModel.GetComponentsInChildren<RecoilCharacterPresentation>(true))
                presentation.ApplyVisualSettings();
            foreach (var group in _previewModel.GetComponentsInChildren<LODGroup>(true)) group.ForceLOD(0);
            // FBX bind poses may be offset from the unit origin. Evaluate the prefab's
            // idle animation on its Animator root, as the character tools do when measuring poses.
            foreach (var animator in _previewModel.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController == null) continue;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
                animator.Update(0f);
                var controller = animator.runtimeAnimatorController as AnimatorController;
                if (controller != null && controller.layers.Length > 0)
                {
                    var machine = controller.layers[0].stateMachine;
                    var idle = machine.states.Select(state => state.state)
                        .FirstOrDefault(state => string.Equals(state.name, "Idle", StringComparison.OrdinalIgnoreCase));
                    var clip = (idle != null ? idle.motion : machine.defaultState?.motion) as AnimationClip;
                    if (clip != null) clip.SampleAnimation(animator.gameObject, 0f);
                }
                animator.enabled = false;
            }
            // Sampling must not overwrite the visual scale/pivot configured on the prefab.
            foreach (var presentation in _previewModel.GetComponentsInChildren<RecoilCharacterPresentation>(true))
                presentation.ApplyVisualSettings();
            MeasurePreviewGeometry();
            _zoom = 1f;

            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            _bodyMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _avoidanceMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            SetPreviewColor(_bodyMaterial, new Color(1f, 0.65f, 0.1f));
            SetPreviewColor(_avoidanceMaterial, new Color(0.1f, 0.8f, 1f));
            const int segments = 128;
            var vertices = new Vector3[segments * 2];
            var indices = new int[segments * 6];
            for (var i = 0; i < segments; i++)
            {
                var angle = i * Mathf.PI * 2f / segments;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                vertices[i * 2] = direction * 0.985f;
                vertices[i * 2 + 1] = direction * 1.015f;
                var next = (i + 1) % segments * 2;
                var offset = i * 6;
                indices[offset] = i * 2;
                indices[offset + 1] = next;
                indices[offset + 2] = i * 2 + 1;
                indices[offset + 3] = i * 2 + 1;
                indices[offset + 4] = next;
                indices[offset + 5] = next + 1;
            }
            _ringMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, vertices = vertices, triangles = indices };
            _ringMesh.RecalculateBounds();
        }

        private void MeasurePreviewGeometry()
        {
            var groups = _previewModel.GetComponentsInChildren<LODGroup>(true);
            var visible = groups.SelectMany(group => group.GetLODs().Take(1))
                .SelectMany(lod => lod.renderers).Where(renderer => renderer != null).ToHashSet();
            var initialized = false;
            SkinnedMeshRenderer body = null;
            var bodyVertices = 0;
            foreach (var renderer in _previewModel.GetComponentsInChildren<Renderer>())
            {
                if (visible.Count > 0 && !visible.Contains(renderer)) continue;
                Mesh mesh = null;
                var temporary = false;
                if (renderer is SkinnedMeshRenderer skin && skin.sharedMesh != null)
                {
                    mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                    skin.BakeMesh(mesh);
                    temporary = true;
                    if (skin.rootBone != null && skin.sharedMesh.vertexCount > bodyVertices)
                    {
                        body = skin;
                        bodyVertices = skin.sharedMesh.vertexCount;
                    }
                }
                else if (renderer is MeshRenderer)
                    mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                try
                {
                    var bounds = mesh.bounds;
                    for (var i = 0; i < 8; i++)
                    {
                        var point = renderer.transform.TransformPoint(bounds.center + Vector3.Scale(bounds.extents,
                            new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                        if (!initialized) { _previewBounds = new Bounds(point, Vector3.zero); initialized = true; }
                        else _previewBounds.Encapsulate(point);
                    }
                }
                finally { if (temporary) DestroyImmediate(mesh); }
            }
            if (!initialized) _previewBounds = new Bounds(_previewModel.transform.position, Vector3.one);
            var anchor = body != null ? body.rootBone.position : _previewBounds.center;
            _previewCenter = new Vector3(anchor.x, _previewBounds.min.y, anchor.z);
        }

        private static void SetPreviewColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
        }

        private void DrawPreview()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Xem trước kích thước thực tế", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Nguồn: " + _paths[_selected], EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Tư thế đứng (Idle) • Tâm vòng theo chân model trong preview", EditorStyles.miniLabel);
            if (new Vector2(_previewCenter.x, _previewCenter.z).magnitude > 0.05f)
                EditorGUILayout.HelpBox(
                    $"Model lệch khỏi gốc prefab: X={_previewCenter.x:F3}, Z={_previewCenter.z:F3}. " +
                    "Preview căn vòng theo model để chỉnh bán kính; tâm va chạm trong battle vẫn là gốc quân.", MessageType.Info);
            EditorGUILayout.LabelField("Cam: vùng chiếm chỗ  •  Xanh: vùng né tránh");
            EditorGUILayout.LabelField("Kéo chuột để xoay • Lăn chuột để thu phóng", EditorStyles.miniLabel);
            if (GUILayout.Button("Nhìn từ trên xuống")) _orbit = new Vector2(0f, 89f);
            var rect = GUILayoutUtility.GetRect(100f, 300f, GUILayout.ExpandWidth(true));
            if (_preview == null || _previewModel == null) return;
            var evt = Event.current;
            if (rect.Contains(evt.mousePosition))
            {
                if (evt.type == EventType.MouseDrag && evt.button == 0)
                {
                    _orbit += new Vector2(evt.delta.x, evt.delta.y) * 0.5f;
                    _orbit.y = Mathf.Clamp(_orbit.y, 5f, 89f);
                    evt.Use();
                    Repaint();
                }
                else if (evt.type == EventType.ScrollWheel)
                {
                    _zoom = Mathf.Clamp(_zoom * Mathf.Exp(evt.delta.y * 0.05f), 0.25f, 4f);
                    evt.Use();
                    Repaint();
                }
            }
            if (evt.type != EventType.Repaint) return;
            var body = IsValid(_bodyRadius, 0f) ? _bodyRadius : 0f;
            var avoidance = IsValid(_avoidanceRadius, 0f) ? _avoidanceRadius : 0f;
            var framed = _previewBounds;
            var ringRadius = Mathf.Max(body, avoidance) * 1.05f;
            framed.Encapsulate(new Bounds(_previewCenter, new Vector3(ringRadius * 2f, 0.05f, ringRadius * 2f)));
            var radius = Mathf.Max(0.5f, framed.extents.magnitude);
            var camera = _preview.camera;
            var rotation = Quaternion.Euler(_orbit.y, _orbit.x, 0f);
            var target = framed.center;
            var halfAngle = Mathf.Atan(Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) * Mathf.Min(1f, rect.width / rect.height));
            var distance = radius / Mathf.Sin(halfAngle) * _zoom;
            camera.transform.SetPositionAndRotation(target - rotation * Vector3.forward * distance, rotation);
            camera.nearClipPlane = Mathf.Max(0.001f, distance * 0.001f);
            camera.farClipPlane = distance + radius * 4f;
            _preview.BeginPreview(rect, GUIStyle.none);
            if (body > 0f)
                _preview.DrawMesh(_ringMesh, Matrix4x4.TRS(_previewCenter + Vector3.up * 0.02f, Quaternion.identity, Vector3.one * body), _bodyMaterial, 0);
            if (avoidance > 0f)
                _preview.DrawMesh(_ringMesh, Matrix4x4.TRS(_previewCenter + Vector3.up * 0.025f, Quaternion.identity, Vector3.one * avoidance), _avoidanceMaterial, 0);
            _preview.Render(true);
            GUI.DrawTexture(rect, _preview.EndPreview(), ScaleMode.StretchToFill, false);
        }

        private void RefreshTargets()
        {
            var previous = _paths.Length > _selected ? _paths[_selected] : null;
            _paths = AssetDatabase.IsValidFolder(PrefabFolder)
                ? AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder })
                    .Select(AssetDatabase.GUIDToAssetPath).OrderBy(path => path).ToArray()
                : Array.Empty<string>();
            _names = _paths.Select(path => Path.GetFileNameWithoutExtension(path)
                .Replace("LOD_", "").Replace("lod_", "")
                .Replace("CUNGKY", "Cung kỵ").Replace("DAO_BINH", "Đao binh")
                .Replace("GIAO_BINH", "Giáo binh").Replace("KYBINH", "Kỵ binh")
                .Replace("mayda", "Máy bắn đá").Replace("quansu", "Quân sư")
                .Replace("voi", "Voi chiến")
                .Replace("_BLUE", " — Phe xanh").Replace("_blue", " — Phe xanh")
                .Replace("_RED", " — Phe đỏ").Replace("_red", " — Phe đỏ")).ToArray();
            _selected = Mathf.Max(0, Array.IndexOf(_paths, previous));
            LoadSettings();
        }

        private void LoadSettings()
        {
            _status = string.Empty;
            if (_paths.Length == 0)
            {
                ClosePreview();
                return;
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(_paths[_selected]);
            var agent = prefab != null ? prefab.GetComponent<RecoilNavMeshPathAgent>() : null;
            _bodyRadius = agent != null ? agent.MovementSettings.BodyRadius : 0.5f;
            _avoidanceRadius = agent != null ? agent.MovementSettings.AvoidanceRadius : 1.2f;
            CreatePreview(prefab);
        }

        private void OnGUI()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                DrawContent();
                _scroll = scroll.scrollPosition;
            }
        }

        private void DrawContent()
        {
            EditorGUILayout.LabelField("Thiết lập vùng chiếm chỗ và né tránh", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Mỗi loại lính và mỗi phe có thể dùng bán kính riêng. Đơn vị là đơn vị thế giới Unity. " +
                "Cấu hình được lưu vào prefab và áp dụng cho quân sinh ra sau đó.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Quét lại danh sách lính")) RefreshTargets();
                if (_paths.Length == 0)
                {
                    EditorGUILayout.HelpBox("Chưa có prefab lính. Hãy thiết lập nhân vật trước.", MessageType.Warning);
                    return;
                }
                EditorGUI.BeginChangeCheck();
                var selected = EditorGUILayout.Popup("Loại lính / phe", _selected, _names);
                if (EditorGUI.EndChangeCheck())
                {
                    _selected = selected;
                    LoadSettings();
                }
                _bodyRadius = EditorGUILayout.FloatField(new GUIContent("Bán kính chiếm chỗ",
                    "Bán kính thân dùng để đẩy tách các quân bị chồng lấn."), _bodyRadius);
                _avoidanceRadius = EditorGUILayout.FloatField(new GUIContent("Bán kính né tránh",
                    "Phạm vi tìm quân lân cận để điều chỉnh hướng di chuyển."), _avoidanceRadius);
                EditorGUILayout.HelpBox(
                    "Khoảng cách chống chồng lấn giữa tâm hai quân = tổng bán kính chiếm chỗ + 0,05. " +
                    "Khoảng cách đội hình lúc sinh quân vẫn được chỉnh trên bộ điều khiển trận chiến.", MessageType.None);
                var valid = IsValid(_bodyRadius, 0.05f) && IsValid(_avoidanceRadius, 0f);
                if (!valid)
                    EditorGUILayout.HelpBox("Bán kính chiếm chỗ phải từ 0,05; bán kính né tránh phải từ 0. Giá trị phải hữu hạn.", MessageType.Error);
                using (new EditorGUI.DisabledScope(!valid))
                    if (GUILayout.Button("Lưu cấu hình cho lính đã chọn")) SaveSettings();
                if (GUILayout.Button("Đọc lại cấu hình đã lưu")) LoadSettings();
                if (GUILayout.Button("Chọn prefab trong cửa sổ tài nguyên"))
                {
                    Selection.activeObject = AssetDatabase.LoadAssetAtPath<GameObject>(_paths[_selected]);
                    EditorGUIUtility.PingObject(Selection.activeObject);
                }
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                EditorGUILayout.HelpBox("Hãy dừng chế độ chạy để chỉnh cấu hình.", MessageType.Info);
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, MessageType.Info);
            if (_paths.Length > 0) DrawPreview();
        }

        private static bool IsValid(float value, float minimum) =>
            !float.IsNaN(value) && !float.IsInfinity(value) && value >= minimum;

        private void SaveSettings()
        {
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(_paths[_selected]);
                var agent = root.GetComponent<RecoilNavMeshPathAgent>();
                if (agent == null) agent = root.AddComponent<RecoilNavMeshPathAgent>();
                var serialized = new SerializedObject(agent);
                serialized.FindProperty("bodyRadius").floatValue = _bodyRadius;
                serialized.FindProperty("avoidanceRadius").floatValue = _avoidanceRadius;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, _paths[_selected], out var success);
                if (!success) throw new InvalidOperationException("Không thể lưu prefab.");
                _status = "Đã lưu cấu hình: " + _names[_selected];
            }
            catch (Exception exception)
            {
                _status = "Lưu thất bại: " + exception.Message;
                Debug.LogException(exception);
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
