using RecoilPathfinding;
using UnityEditor;
using UnityEngine;

namespace GameToolsEditor
{
    public abstract class RecoilForestProfilingInspectorBase : UnityEditor.Editor
    {
        private double nextRefresh;
        private bool showGame = true, showScene;

        protected virtual void OnEnable() => EditorApplication.update += Refresh;
        protected virtual void OnDisable() => EditorApplication.update -= Refresh;

        private void Refresh()
        {
            if (EditorApplication.timeSinceStartup < nextRefresh) return;
            nextRefresh = EditorApplication.timeSinceStartup + 0.25;
            Repaint();
        }

        protected void DrawProfile(RecoilForestInstancedRenderer renderer)
        {
            if (renderer == null)
            {
                EditorGUILayout.HelpBox("Chưa có Instanced Forest để đo. ForestArea chỉ giữ dữ liệu gốc cây; renderer con thực hiện vẽ rừng.", MessageType.Info);
                return;
            }
            EditorGUILayout.Space();
            var settings = new SerializedObject(renderer);
            settings.Update();
            EditorGUILayout.PropertyField(settings.FindProperty("useIndirect"), new GUIContent("Thử GPU indirect"));
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                if (GUILayout.Button("Chuẩn bị shader + bật indirect thử"))
                    EditorApplication.delayCall += () =>
                    {
                        if (renderer == null) return;
                        try { CongThanhForestIndirectSetup.Prepare(renderer); }
                        catch (System.Exception e) { Debug.LogException(e, renderer); }
                    };
            EditorGUILayout.PropertyField(settings.FindProperty("logToConsole"), new GUIContent("In Debug Log khi Play"));
            EditorGUILayout.PropertyField(settings.FindProperty("logIntervalSeconds"), new GUIContent("Chu kỳ log (giây)"));
            EditorGUILayout.PropertyField(settings.FindProperty("logSceneCamera"), new GUIContent("In thêm Scene Camera"));
            settings.ApplyModifiedProperties();
            EditorGUILayout.LabelField("Forest CPU profiling · cập nhật 4 Hz", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Đo CPU cho từng camera, mỗi lượt render. Submit là thời gian gửi lệnh; thời gian GPU vẽ cây/lá cần xem GPU Profiler.", MessageType.Info);
            EditorGUILayout.LabelField("Cache: số lần / lần gần nhất", $"{renderer.RebuildCount} / {renderer.LastRebuildMilliseconds:F3} ms");
            showGame = EditorGUILayout.Foldout(showGame, "Game Camera", true);
            if (showGame) DrawSample(renderer.LastGameCamera, renderer.LastGameSample);
            showScene = EditorGUILayout.Foldout(showScene, "Scene Camera (Editor)", true);
            if (showScene) DrawSample(renderer.LastSceneCamera, renderer.LastSceneSample);
        }

        private static void DrawSample(Camera camera, RecoilForestInstancedRenderer.ProfileSample s)
        {
            if (camera == null) { EditorGUILayout.LabelField("Chưa có mẫu từ camera này."); return; }
            EditorGUILayout.LabelField(camera.name, $"{(s.Playing ? "Play Mode" : "Edit Mode")} · frame {s.Frame}");
            var age = Time.realtimeSinceStartupAsDouble - s.Timestamp;
            if (age > 1.0) EditorGUILayout.LabelField("Tuổi mẫu", $"{age:F1} giây (camera chưa vẽ lại)");
            EditorGUILayout.LabelField("Tổng CPU rừng", $"{s.TotalMs:F3} ms", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Chế độ vẽ", s.Indirect ? "Indirect · GPU culling / LOD" : "Instanced · CPU culling / LOD");
            if (s.Indirect)
                EditorGUILayout.HelpBox(s.GpuCountsAvailable ? $"Số ô/instance/tam giác đọc bất đồng bộ, trễ {s.GpuCountAge:F1} giây. Frustum/LOD CPU bằng 0 vì chạy trên GPU."
                    : "Đang chờ số liệu GPU bất đồng bộ. Các bộ đếm đang bằng 0 chưa có nghĩa là không vẽ.", MessageType.Info);
            var max = Mathf.Max(s.RebuildMs, s.PrepareMs, s.FrustumMs, s.LodMs, s.GatherMs, s.CopyMs, s.SubmitMs);
            Row("RebuildCache", s.RebuildMs, max);
            Row("PrepareCamera", s.PrepareMs, max);
            Row("FrustumCulling", s.FrustumMs, max);
            Row("SelectLOD", s.LodMs, max);
            Row("GatherBatches", s.GatherMs, max);
            Row("CopyMatrices", s.CopyMs, max);
            Row("SubmitInstanced", s.SubmitMs, max);
            EditorGUILayout.Space(3);
            EditorGUILayout.LabelField("Ô tổng / thấy / loại frustum", $"{s.TotalChunks:N0} / {s.VisibleChunks:N0} / {s.FrustumRejected:N0}");
            EditorGUILayout.LabelField("Ô loại bởi LOD", $"{s.LodRejected:N0}");
            EditorGUILayout.LabelField("Ô LOD0 / LOD thấp hơn", $"{s.Lod0Chunks:N0} / {s.OtherLodChunks:N0}");
            EditorGUILayout.LabelField("Batch nguồn / draw calls", $"{s.SourceBatches:N0} / {s.DrawCalls:N0}");
            EditorGUILayout.LabelField("Instances theo submesh", $"{s.Instances:N0}");
            EditorGUILayout.LabelField("Tam giác gửi (ước tính)", $"{s.Triangles:N0}");
            EditorGUILayout.LabelField("Lần copy / dữ liệu ma trận", $"{s.MatrixCopies:N0} / {s.CopiedBytes / (1024f * 1024f):F2} MB");
        }

        private static void Row(string name, float ms, float max)
        {
            var hottest = ms > 0f && ms == max;
            EditorGUILayout.LabelField((hottest ? "→ " : "   ") + name, $"{ms:F3} ms",
                hottest ? EditorStyles.boldLabel : EditorStyles.label);
        }
    }

    [CustomEditor(typeof(RecoilForestInstancedRenderer))]
    public sealed class RecoilForestRendererInspector : RecoilForestProfilingInspectorBase
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("data"));
            serializedObject.ApplyModifiedProperties();
            DrawProfile((RecoilForestInstancedRenderer)target);
        }
    }

    [CustomEditor(typeof(RecoilForestArea))]
    public sealed class RecoilForestAreaInspector : RecoilForestProfilingInspectorBase
    {
        public override void OnInspectorGUI()
        {
            var area = (RecoilForestArea)target;
            EditorGUILayout.LabelField("Số cây / bụi cỏ", $"{area.TreeCount:N0} / {area.grassCount:N0}");
            EditorGUILayout.LabelField("Đã bake", area.meshBaked ? "Có" : "Chưa");
            if (!string.IsNullOrEmpty(area.meshAssetFolder)) EditorGUILayout.SelectableLabel(area.meshAssetFolder, EditorStyles.wordWrappedMiniLabel, GUILayout.Height(34));
            DrawProfile(area.GetComponentInChildren<RecoilForestInstancedRenderer>(true));
        }
    }
}
