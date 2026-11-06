using System.IO;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RecoilPathfindingEditor
{
    [InitializeOnLoad]
    public static class RecoilVatSpawnTestSetup
    {
        private const string Request = "Assets/Editor/RecoilPathfinding/RecoilVatSpawnTest.request";
        private const string Path = "Assets/Data/Characters/Scenes/VAT Spawn Test.unity";
        private static double nextCheck;
        static RecoilVatSpawnTestSetup() { nextCheck = EditorApplication.timeSinceStartup + 5; EditorApplication.update += Check; }
        private static void Check()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess() || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.timeSinceStartup < nextCheck) return;
            nextCheck = EditorApplication.timeSinceStartup + 2;
            if (!File.Exists(Request) || string.IsNullOrWhiteSpace(File.ReadAllText(Request))) return;
            File.WriteAllText(Request, string.Empty);
            Create();
        }
        [MenuItem("Tools/Recoil Pathfinding/VAT/Create spawn-only test scene")]
        public static void Create()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || AssetDatabase.IsAssetImportWorkerProcess()) return;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(Path) != null) { Debug.Log("Spawn test scene already exists: " + Path); return; }
            Directory.CreateDirectory("Assets/Data/Characters/Scenes");
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var root = new GameObject("VAT Spawn Test - render only");
                SceneManager.MoveGameObjectToScene(root, scene);
                root.AddComponent<RecoilVatSpawnTest>();
                var camera = new GameObject("Main Camera").AddComponent<Camera>();
                SceneManager.MoveGameObjectToScene(camera.gameObject, scene);
                camera.tag = "MainCamera"; camera.backgroundColor = new Color(.12f, .15f, .19f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.orthographic = true; camera.orthographicSize = 170; camera.farClipPlane = 2000;
                camera.transform.position = new Vector3(0, 400, -100);
                camera.transform.LookAt(new Vector3(0, 0, 95));
                var controls = camera.gameObject.AddComponent<RecoilDemoCameraController>();
                var serialized = new SerializedObject(controls);
                serialized.FindProperty("pitchDegrees").floatValue = 64;
                serialized.FindProperty("zoomUnitsPerNotch").floatValue = 2;
                serialized.FindProperty("zoomSensitivityMultiplier").floatValue = 1;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var sun = new GameObject("Directional Light").AddComponent<Light>();
                SceneManager.MoveGameObjectToScene(sun.gameObject, scene);
                sun.type = LightType.Directional;
                sun.color = new Color(1, .956f, .84f);
                sun.intensity = 1;
                sun.shadows = LightShadows.None;
                sun.transform.rotation = Quaternion.Euler(50, -30, 0);
                if (!EditorSceneManager.SaveScene(scene, Path)) throw new IOException("Could not save " + Path);
                Debug.Log("Created spawn-only VAT scene: " + Path);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            }
        }
    }
}
