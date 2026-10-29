using RecoilPathfinding;
using UnityEditor;
using UnityEngine;

namespace RecoilPathfindingEditor
{
    [InitializeOnLoad]
    public static class RecoilSceneUnitPicker
    {
        static RecoilSceneUnitPicker()
        {
            SceneView.duringSceneGui += OnSceneGUI;
        }

        private static void OnSceneGUI(SceneView view)
        {
            if (!EditorApplication.isPlaying) return;
            var current = Event.current;
            if (!current.shift || current.alt || current.control || current.command) return;
            if (current.type == EventType.Layout)
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (current.type != EventType.MouseDown || current.button != 0) return;

            // Pick in screen space: units have disabled colliders and instanced renderers.
            // This scan runs only on a click, never on every Scene repaint.
            var agents = Object.FindObjectsByType<RecoilNavMeshPathAgent>(FindObjectsSortMode.None);
            var distance = 24f * 24f;
            RecoilNavMeshPathAgent nearest = null;
            foreach (var agent in agents)
            {
                var simulation = agent.Simulation;
                if (simulation == null || !simulation.IsActive) continue;
                var position = simulation.Position;
                if (view.camera.WorldToViewportPoint(position).z <= 0f) continue;
                var point = HandleUtility.WorldToGUIPoint(position);
                var candidateDistance = (point - current.mousePosition).sqrMagnitude;
                if (candidateDistance >= distance) continue;
                distance = candidateDistance;
                nearest = agent;
            }
            if (nearest != null)
            {
                Selection.activeGameObject = nearest.gameObject;
                EditorGUIUtility.PingObject(nearest.gameObject);
                view.Repaint();
            }
            else
                view.ShowNotification(new GUIContent("No active soldier within 24 pixels. Zoom closer and Shift-click again."));
            current.Use();
        }
    }
}
