using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using RecoilPathfinding;

namespace RecoilPathfindingEditor
{
    [InitializeOnLoad]
    internal static class RecoilCombatCapacityRunner
    {
        private const string Pending = "RecoilCapacityBenchmark.Pending";
        private const string Batch = "RecoilCapacityBenchmark.Batch";
        private static RecoilAutoBattleDemo running;
        static RecoilCombatCapacityRunner() => EditorApplication.update += Poll;

        [MenuItem("Game Tools/Công Thành/Đo sức chứa combat không UI")]
        public static void Run()
        {
            SessionState.SetBool(Pending, true);
            if (!EditorApplication.isPlaying) EditorApplication.isPlaying = true;
        }

        public static void RunFromCommandLine()
        {
            EditorSceneManager.OpenScene("Assets/Scenes/new.unity");
            SessionState.SetBool(Batch, true);
            Run();
        }

        private static void Poll()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var request = System.IO.Path.Combine(Application.dataPath, "CombatCapacityReports/run.request");
            if (System.IO.File.Exists(request))
            {
                System.IO.File.Delete(request);
                Run();
            }
            if (!EditorApplication.isPlaying) return;
            if (SessionState.GetBool(Pending, false))
            {
                running = UnityEngine.Object.FindFirstObjectByType<RecoilAutoBattleDemo>();
                if (running == null) return;
                SessionState.SetBool(Pending, false);
                running.StartCombatCapacityBenchmark();
            }
            if (running != null && !running.CapacityBenchmarkRunning)
            {
                running = null;
                if (SessionState.GetBool(Batch, false)) { SessionState.SetBool(Batch, false); EditorApplication.Exit(0); }
            }
        }
    }
}
