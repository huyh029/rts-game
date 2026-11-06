using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GameToolsEditor
{
    public sealed class CongThanhLobbyUiSetupWindow : EditorWindow
    {
        [InitializeOnLoadMethod]
        private static void RegisterInfantryMigration()
        {
            EditorApplication.delayCall += AddMissingInfantry;
            EditorApplication.playModeStateChanged -= OnInfantryPlayModeChanged;
            EditorApplication.playModeStateChanged += OnInfantryPlayModeChanged;
        }

        private static void OnInfantryPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) AddMissingInfantry();
        }

        private static void AddMissingInfantry()
        {
            if (EditorApplication.isPlaying) return;
            foreach (var battle in Resources.FindObjectsOfTypeAll<RecoilAutoBattleDemo>())
            {
                if (!battle.gameObject.scene.IsValid() || !battle.gameObject.scene.isLoaded ||
                    EditorUtility.IsPersistent(battle)) continue;
                var serialized = new SerializedObject(battle);
                foreach (var team in new[] { "blue", "red" })
                {
                    var units = serialized.FindProperty(team + "UnitsByType");
                    if (units.arraySize < 8)
                    {
                        units.arraySize = 8;
                        units.GetArrayElementAtIndex(7).objectReferenceValue = null;
                    }
                    var slot = units.GetArrayElementAtIndex(7);
                    if (slot.objectReferenceValue == null)
                        slot.objectReferenceValue = AssetDatabase.LoadAssetAtPath<GameObject>(
                            "Assets/Data/Characters/Prefabs/BO_BINH_" + team + ".prefab");
                }
                var images = serialized.FindProperty("lobbyUnitImages");
                if (images.arraySize < 8)
                {
                    images.arraySize = 8;
                    images.GetArrayElementAtIndex(7).objectReferenceValue = null;
                }
                if (images.GetArrayElementAtIndex(7).objectReferenceValue == null)
                    images.GetArrayElementAtIndex(7).objectReferenceValue = AssetDatabase.LoadAssetAtPath<Texture2D>(
                        "Assets/_Game/UI/Đội Hình Khiên Đỏ Xung Trận.png");
                if (serialized.ApplyModifiedProperties()) EditorSceneManager.MarkSceneDirty(battle.gameObject.scene);
            }
        }

        private static readonly string[] DurationLabels =
            { "Ảnh nút 5 phút", "Ảnh nút 10 phút", "Ảnh nút 15 phút", "Ảnh nút 30 phút" };
        private static readonly string[] UnitLabels =
            { "Cung kỵ", "Đao binh", "Giáo binh", "Kỵ binh", "Máy bắn đá", "Quân sư", "Voi chiến", "Bộ binh" };
        private RecoilAutoBattleDemo _battle;
        private Vector2 _scroll;

        [MenuItem("Game Tools/Công Thành/Thiết lập hình ảnh UI sảnh")]
        private static void Open()
        {
            var window = GetWindow<CongThanhLobbyUiSetupWindow>("Hình ảnh UI sảnh");
            window.minSize = new Vector2(430f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            _battle = FindSceneBattle(SceneManager.GetActiveScene());
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Hình ảnh màn sảnh", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Chọn ảnh Texture2D cho từng thành phần. Ảnh để trống sẽ giữ giao diện chữ hiện tại. " +
                "Thay đổi được ghi trực tiếp lên Battle trong scene đang mở; hãy lưu scene để giữ lại.",
                MessageType.Info);

            var selectedBattle = (RecoilAutoBattleDemo)EditorGUILayout.ObjectField(
                "Battle trong scene", _battle, typeof(RecoilAutoBattleDemo), true);
            if (selectedBattle != _battle)
                _battle = selectedBattle;
            if (_battle == null)
            {
                EditorGUILayout.HelpBox(
                    "Scene hiện tại chưa có RecoilAutoBattleDemo. Mở scene trận đấu hoặc kéo Battle vào ô phía trên.",
                    MessageType.Warning);
                return;
            }

            var serialized = new SerializedObject(_battle);
            serialized.Update();
            var durations = serialized.FindProperty("lobbyDurationImages");
            var units = serialized.FindProperty("lobbyUnitImages");
            if (durations == null || units == null)
            {
                EditorGUILayout.HelpBox(
                    "Battle không có các trường ảnh UI sảnh cần thiết. Hãy đợi Unity biên dịch xong rồi mở lại tool.",
                    MessageType.Error);
                return;
            }
            EditorGUI.BeginChangeCheck();
            durations.arraySize = DurationLabels.Length;
            units.arraySize = UnitLabels.Length;
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                DrawImageField(serialized.FindProperty("lobbyBackgroundImage"), "Ảnh nền sảnh", 72f);
                DrawImageField(serialized.FindProperty("lobbyTitleImage"), "Ảnh tiêu đề", 52f);

                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField("Nút chọn thời lượng", EditorStyles.boldLabel);
                for (var i = 0; i < DurationLabels.Length; i++)
                    DrawImageField(durations.GetArrayElementAtIndex(i), DurationLabels[i], 44f);

                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField("Nút sảnh", EditorStyles.boldLabel);
                DrawImageField(serialized.FindProperty("lobbyStartButtonImage"), "Bắt đầu", 46f);
                DrawImageField(serialized.FindProperty("lobbySettingsButtonImage"), "Cài đặt chỉ số", 46f);
                EditorGUILayout.Space(8f);
                EditorGUILayout.LabelField("Ảnh từng loại quân", EditorStyles.boldLabel);
                for (var i = 0; i < UnitLabels.Length; i++)
                    DrawImageField(units.GetArrayElementAtIndex(i), UnitLabels[i], 52f);

                _scroll = scroll.scrollPosition;
            }

            if (EditorGUI.EndChangeCheck())
            {
                serialized.ApplyModifiedProperties();
                if (_battle.gameObject.scene.IsValid())
                    EditorSceneManager.MarkSceneDirty(_battle.gameObject.scene);
            }
        }

        private static void DrawImageField(SerializedProperty property, string label, float previewHeight)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(property, new GUIContent(label));
            var image = property.objectReferenceValue as Texture2D;
            var preview = image != null ? AssetPreview.GetAssetPreview(image) : null;
            var rect = GUILayoutUtility.GetRect(72f, previewHeight, GUILayout.Width(72f));
            if (preview != null)
                GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit, true);
            else if (image != null)
                GUI.DrawTexture(rect, image, ScaleMode.ScaleToFit, true);
            EditorGUILayout.EndHorizontal();
        }

        private static RecoilAutoBattleDemo FindSceneBattle(Scene scene)
        {
            foreach (var battle in Object.FindObjectsByType<RecoilAutoBattleDemo>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (battle.gameObject.scene == scene) return battle;
            return null;
        }
    }
}
