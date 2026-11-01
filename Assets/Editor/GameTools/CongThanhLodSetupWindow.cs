using UnityEditor;
using UnityEngine;

namespace GameToolsEditor
{
    public sealed class CongThanhLodSetupWindow : EditorWindow
    {
        private string _report = string.Empty;
        private Vector2 _scroll;

        [MenuItem("Game Tools/Công Thành/Thiết lập LOD")]
        private static void Open()
        {
            var window = GetWindow<CongThanhLodSetupWindow>("Công Thành - LOD");
            window.minSize = new Vector2(480f, 280f);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Thiết lập LOD cho toàn bộ lính", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Tự dò mesh LOD1–3 và lưu toàn bộ prefab nhân vật. Mesh gốc không ghi LOD1 cũng được nhận diện. " +
                "Chỉ dùng mesh 3D; LOD3 giữ hiển thị đến tận đường chân trời, không chuyển sang chấm hoặc impostor.",
                MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Setup LOD", GUILayout.Height(38f)))
                    _report = CongThanhImpostorLodSetup.SetupMeshLods();
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                EditorGUILayout.HelpBox("Hãy dừng chế độ chạy trước khi thiết lập LOD.", MessageType.Info);
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                if (!string.IsNullOrEmpty(_report)) EditorGUILayout.HelpBox(_report, MessageType.Info);
                _scroll = scroll.scrollPosition;
            }
        }
    }
}
