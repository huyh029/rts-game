using System;
using System.Collections.Generic;
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
    public sealed class CongThanhAnimationTranslationWindow : EditorWindow
    {
        private const string Output = "Assets/Data/Characters/Animations/TranslatedRoots";
        [SerializeField] private GameObject _target;
        [SerializeField] private string _rootPath = "root";
        [SerializeField] private Vector3 _offset;
        private string _status;

        [Serializable]
        private sealed class Origin
        {
            public string sourceGuid;
            public long sourceId;
        }

        [MenuItem("Game Tools/Công Thành/Tịnh tiến root animation")]
        private static void Open()
        {
            GetWindow<CongThanhAnimationTranslationWindow>("Tịnh tiến animation").minSize = new Vector2(500f, 340f);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Tịnh tiến vị trí root trong các clip", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Cộng cùng một độ bù vào các key vị trí root của Idle, Run, Attack và các clip khác. " +
                "Giữ chuyển động tương đối, góc xoay, scale và FBX gốc. Tạo bản .anim và controller riêng cho nhân vật. " +
                "Đường dẫn root tính từ object có Animator.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                EditorGUI.BeginChangeCheck();
                _target = (GameObject)EditorGUILayout.ObjectField("Nhân vật", _target, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck())
                {
                    _offset = Vector3.zero;
                    _status = string.Empty;
                    var animator = GetAnimator(_target);
                    var root = animator != null ? animator.GetComponentsInChildren<Transform>(true)
                        .FirstOrDefault(item => string.Equals(item.name, "root", StringComparison.OrdinalIgnoreCase)) : null;
                    _rootPath = root != null ? AnimationUtility.CalculateTransformPath(root, animator.transform) : "root";
                }
                _rootPath = EditorGUILayout.TextField("Đường dẫn root", _rootPath);
                _offset = EditorGUILayout.Vector3Field("Độ tịnh tiến (local)", _offset);
                using (new EditorGUI.DisabledScope(_target == null))
                {
                    if (GUILayout.Button("Đo độ bù từ Idle về vị trí đã căn trong prefab"))
                        Execute(() => { _offset = MeasureOffset(_target, _rootPath); return "Độ bù đã đo: " + _offset.ToString("F6"); });
                    if (GUILayout.Button("Tạo animation đã tịnh tiến và gán cho nhân vật", GUILayout.Height(34f)))
                        Execute(() => TranslateCharacter(_target, _rootPath, _offset));
                }
            }
            EditorGUILayout.HelpBox(
                "Đo từ Idle rồi dùng nguyên độ bù cho mọi clip, không căn riêng từng clip về 0. " +
                "Chạy lại luôn lấy clip nguồn nên không cộng dồn độ lệch. Nhân vật trong Hierarchy cần lưu scene sau khi áp dụng.", MessageType.None);
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, MessageType.Info);
        }

        private void Execute(Func<string> action)
        {
            try { _status = action(); }
            catch (Exception exception) { _status = exception.Message; Debug.LogException(exception); }
        }

        private static Animator GetAnimator(GameObject target)
        {
            if (target == null) return null;
            var presentation = target.GetComponent<RecoilCharacterPresentation>();
            return presentation != null && presentation.Animator != null ? presentation.Animator : target.GetComponentInChildren<Animator>(true);
        }

        private static AnimatorController GetController(GameObject target, string rootPath)
        {
            var animator = GetAnimator(target);
            if (animator == null || string.IsNullOrWhiteSpace(rootPath) || animator.transform.Find(rootPath) == null)
                throw new InvalidOperationException("Không tìm thấy Animator hoặc root theo đường dẫn đã chọn.");
            var controller = animator.runtimeAnimatorController as AnimatorController;
            if (controller == null) throw new InvalidOperationException("Nhân vật cần Animator Controller thông thường.");
            return controller;
        }

        internal static Vector3 MeasureOffset(GameObject target, string rootPath)
        {
            var controller = GetController(target, rootPath);
            var machine = controller.layers[0].stateMachine;
            var idle = machine.states.FirstOrDefault(item => item.state.name == "Idle").state ?? machine.defaultState;
            var clip = idle != null ? idle.motion as AnimationClip : null;
            if (clip == null) throw new InvalidOperationException("Cần clip Idle làm mốc chung; Idle hiện không có clip trực tiếp.");
            clip = OriginalClip(clip);
            var desired = GetAnimator(target).transform.Find(rootPath).localPosition;
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var clone = Instantiate(target);
                SceneManager.MoveGameObjectToScene(clone, scene);
                clone.SetActive(true);
                var animator = GetAnimator(clone);
                animator.Rebind();
                animator.Update(0f);
                clip.SampleAnimation(animator.gameObject, 0f);
                return desired - animator.transform.Find(rootPath).localPosition;
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        private static AnimationClip OriginalClip(AnimationClip clip)
        {
            var path = AssetDatabase.GetAssetPath(clip);
            if (!path.StartsWith(Output + "/", StringComparison.Ordinal)) return clip;
            var importer = AssetImporter.GetAtPath(path);
            var origin = JsonUtility.FromJson<Origin>(importer.userData);
            if (origin == null || string.IsNullOrEmpty(origin.sourceGuid))
                throw new InvalidOperationException("Clip đã tạo thiếu thông tin nguồn: " + path);
            var sourcePath = AssetDatabase.GUIDToAssetPath(origin.sourceGuid);
            foreach (var source in AssetDatabase.LoadAllAssetsAtPath(sourcePath).OfType<AnimationClip>())
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long id) && id == origin.sourceId)
                    return source;
            throw new InvalidOperationException("Không tìm thấy clip nguồn của " + path);
        }

        internal static AnimationClip TranslateClip(AnimationClip current, string rootPath, Vector3 offset,
            Vector3 baseline, string folder)
        {
            var source = OriginalClip(current);
            if (source.humanMotion) throw new InvalidOperationException("Tool này xử lý Transform curve của clip Generic; chưa hỗ trợ Humanoid.");
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long id);
            var path = folder + "/" + guid + "_" + id + ".anim";
            var copy = Instantiate(source);
            copy.name = source.name;
            var bindings = AnimationUtility.GetCurveBindings(copy);
            for (var axis = 0; axis < 3; axis++)
            {
                var property = "m_LocalPosition." + "xyz"[axis];
                var binding = EditorCurveBinding.FloatCurve(rootPath, typeof(Transform), property);
                var curve = bindings.Any(item => item.path == rootPath && item.type == typeof(Transform) && item.propertyName == property)
                    ? AnimationUtility.GetEditorCurve(source, binding) : null;
                if (curve == null || curve.length == 0)
                    curve = AnimationCurve.Constant(0f, Mathf.Max(source.length, 0.001f), baseline[axis]);
                var keys = curve.keys;
                for (var key = 0; key < keys.Length; key++) keys[key].value += offset[axis];
                curve.keys = keys;
                AnimationUtility.SetEditorCurve(copy, binding, curve);
            }
            var saved = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (saved == null) { AssetDatabase.CreateAsset(copy, path); saved = copy; }
            else { EditorUtility.CopySerialized(copy, saved); DestroyImmediate(copy); }
            var importer = AssetImporter.GetAtPath(path);
            importer.userData = JsonUtility.ToJson(new Origin { sourceGuid = guid, sourceId = id });
            AssetDatabase.WriteImportSettingsIfDirty(path);
            EditorUtility.SetDirty(saved);
            AssetDatabase.SaveAssetIfDirty(saved);
            return saved;
        }

        internal static string TranslateCharacter(GameObject target, string rootPath, Vector3 offset)
        {
            if (!float.IsFinite(offset.x) || !float.IsFinite(offset.y) || !float.IsFinite(offset.z))
                throw new InvalidOperationException("Độ bù phải hữu hạn.");
            GetController(target, rootPath);
            var path = AssetDatabase.GetAssetPath(target);
            if (EditorUtility.IsPersistent(target) && !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Chọn prefab .prefab hoặc nhân vật trong Hierarchy, không chọn FBX gốc.");
            GameObject contents = null;
            try
            {
                if (EditorUtility.IsPersistent(target)) contents = PrefabUtility.LoadPrefabContents(path);
                var root = contents != null ? contents : target;
                var animator = GetAnimator(root);
                var sourceController = GetController(root, rootPath);
                var baseline = animator.transform.Find(rootPath).localPosition - MeasureOffset(root, rootPath);
                var key = GlobalObjectId.GetGlobalObjectIdSlow(target).ToString();
                if (!EditorUtility.IsPersistent(target) && string.IsNullOrEmpty(target.scene.path)) key += "_" + target.GetInstanceID();
                var folder = Output + "/" + Hash128.Compute(key + rootPath);
                Directory.CreateDirectory(folder);
                AssetDatabase.Refresh();
                var presentation = root.GetComponent<RecoilCharacterPresentation>();
                var clips = sourceController.animationClips.ToList();
                if (presentation != null && presentation.AttackClip != null && !clips.Contains(presentation.AttackClip)) clips.Add(presentation.AttackClip);
                var replacements = clips.Distinct().ToDictionary(clip => clip,
                    clip => TranslateClip(clip, rootPath, offset, baseline, folder));
                var controller = sourceController;
                if (!AssetDatabase.GetAssetPath(controller).StartsWith(folder + "/", StringComparison.Ordinal))
                {
                    var controllerPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/Controller.controller");
                    if (!AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(controller), controllerPath))
                        throw new InvalidOperationException("Không tạo được bản sao controller.");
                    controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
                }
                foreach (var layer in controller.layers) ReplaceMotions(layer.stateMachine, replacements, controller);
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssetIfDirty(controller);
                Undo.RecordObject(animator, "Gán animation đã tịnh tiến");
                animator.runtimeAnimatorController = controller;
                if (presentation != null && presentation.AttackClip != null && replacements.TryGetValue(presentation.AttackClip, out var attack))
                {
                    var serialized = new SerializedObject(presentation);
                    serialized.FindProperty("attackClip").objectReferenceValue = attack;
                    serialized.ApplyModifiedProperties();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(presentation);
                }
                if (contents != null)
                {
                    PrefabUtility.SaveAsPrefabAsset(contents, path, out var success);
                    if (!success) throw new InvalidOperationException("Không lưu được prefab.");
                }
                else
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(animator);
                    EditorSceneManager.MarkSceneDirty(root.scene);
                }
                return $"Đã tạo và gán {replacements.Count} clip cho {target.name}. Độ bù: {offset.ToString("F6")}.\nLưu tại: {folder}";
            }
            finally { if (contents != null) PrefabUtility.UnloadPrefabContents(contents); }
        }

        private static void ReplaceMotions(AnimatorStateMachine machine, Dictionary<AnimationClip, AnimationClip> replacements, AnimatorController owner)
        {
            foreach (var item in machine.states)
            {
                item.state.motion = ReplaceMotion(item.state.motion, replacements, owner);
                EditorUtility.SetDirty(item.state);
            }
            foreach (var child in machine.stateMachines) ReplaceMotions(child.stateMachine, replacements, owner);
        }

        private static Motion ReplaceMotion(Motion motion, Dictionary<AnimationClip, AnimationClip> replacements, AnimatorController owner)
        {
            if (motion is AnimationClip clip && replacements.TryGetValue(clip, out var translated)) return translated;
            if (motion is BlendTree tree)
            {
                if (AssetDatabase.GetAssetPath(tree) != AssetDatabase.GetAssetPath(owner))
                {
                    tree = Instantiate(tree);
                    AssetDatabase.AddObjectToAsset(tree, owner);
                }
                var children = tree.children;
                for (var i = 0; i < children.Length; i++) children[i].motion = ReplaceMotion(children[i].motion, replacements, owner);
                tree.children = children;
                EditorUtility.SetDirty(tree);
            }
            return motion;
        }
    }
}
