using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using System;
using System.IO;
using System.Linq;

namespace TiktokLibEditor
{
    public sealed class TikTokSetupWindow : EditorWindow
    {
        private string LibraryRoot => Path.GetDirectoryName(Path.GetDirectoryName(
            AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this)))).Replace('\\', '/');
        private string ConfigPath => LibraryRoot + "/Configs/TiktokConfig.asset";
        [SerializeField] private TiktokConfigSO configuration;
        [SerializeField] private int port = 7777;
        [SerializeField] private bool autoStart = true;
        [SerializeField] private bool addTestPopup = true;

        [MenuItem("Tools/TikTok/Setup All")]
        public static void Open() => GetWindow<TikTokSetupWindow>("TikTok Setup All");

        private void OnEnable()
        {
            if (configuration == null) configuration = AssetDatabase.LoadAssetAtPath<TiktokConfigSO>(ConfigPath);
        }

        private void OnGUI()
        {
            var scene = SceneManager.GetActiveScene();
            EditorGUILayout.LabelField("Scene", scene.IsValid() ? scene.name : "No scene");
            configuration = (TiktokConfigSO)EditorGUILayout.ObjectField("Configuration", configuration, typeof(TiktokConfigSO), false);
            port = EditorGUILayout.IntField("UDP port", port);
            autoStart = EditorGUILayout.Toggle("Auto start on Play", autoStart);
            addTestPopup = EditorGUILayout.Toggle("Add test popup (F2)", addTestPopup);
            EditorGUILayout.HelpBox("Setup All attaches UDP, config and incoming-event logging. Optional test popup opens with F2. No gameplay actions are connected.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode || !scene.IsValid() || !scene.isLoaded ||
                EditorSceneManager.IsPreviewScene(scene) || port < 1 || port > 65535))
                if (GUILayout.Button("Setup All")) Setup(scene);
        }

        private void Setup(Scene scene)
        {
            var receivers = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<TikTokUdpReceiver>(true)).ToArray();
            var dispatchers = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<MainThreadDispatcher>(true)).ToArray();
            if (receivers.Length > 1 || dispatchers.Length > 1)
            { ShowNotification(new GUIContent("Keep one UDP receiver/dispatcher before applying.")); return; }
            var popupPrefab = addTestPopup ? AssetDatabase.LoadAssetAtPath<GameObject>(LibraryRoot + "/TiktokRecevier/TiktokEventTest.prefab") : null;
            if (addTestPopup && popupPrefab == null) { ShowNotification(new GUIContent("Test popup prefab not found.")); return; }
            if (configuration == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)); AssetDatabase.Refresh();
                configuration = AssetDatabase.LoadAssetAtPath<TiktokConfigSO>(ConfigPath);
                if (configuration == null)
                {
                    configuration = CreateInstance<TiktokConfigSO>();
                    configuration.tiktokDatas = Array.Empty<TiktokConfigSO.TiktokData>();
                    AssetDatabase.CreateAsset(configuration, ConfigPath); AssetDatabase.SaveAssets();
                }
            }
            Undo.IncrementCurrentGroup(); var group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("TikTok Setup All");
            var root = receivers.Length == 1 ? receivers[0].gameObject : dispatchers.Length == 1 ? dispatchers[0].gameObject : new GameObject("TikTok Connection");
            if (receivers.Length == 0 && dispatchers.Length == 0)
            { SceneManager.MoveGameObjectToScene(root, scene); Undo.RegisterCreatedObjectUndo(root, "Create TikTok connection"); }
            if (root.GetComponent<MainThreadDispatcher>() == null) Undo.AddComponent<MainThreadDispatcher>(root);
            var receiver = root.GetComponent<TikTokUdpReceiver>();
            if (receiver == null) receiver = Undo.AddComponent<TikTokUdpReceiver>(root);
            var connection = root.GetComponent<TikTokConnection>();
            if (connection == null) connection = Undo.AddComponent<TikTokConnection>(root);
            var logger = root.GetComponent<TikTokEventLogger>();
            if (logger == null) logger = Undo.AddComponent<TikTokEventLogger>(root);
            Undo.RecordObjects(new UnityEngine.Object[] { receiver, connection, logger }, "Configure TikTok");
            receiver.port = port; receiver.autoStart = autoStart; receiver.logRaw = false;
            connection.Configuration = configuration; connection.Receiver = receiver; logger.LogEvents = true;
            foreach (var component in new Component[] { receiver, connection, logger })
            { EditorUtility.SetDirty(component); PrefabUtility.RecordPrefabInstancePropertyModifications(component); }
            if (addTestPopup && !scene.GetRootGameObjects().Any(r => r.GetComponentInChildren<TiktokEventTestPopup>(true) != null))
            {
                var popup = (GameObject)PrefabUtility.InstantiatePrefab(popupPrefab, scene);
                Undo.RegisterCreatedObjectUndo(popup, "Create TikTok test UI");
            }
            if (addTestPopup && FindObjectsByType<EventSystem>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length == 0)
            {
                var events = new GameObject("EventSystem"); SceneManager.MoveGameObjectToScene(events, scene);
                Undo.RegisterCreatedObjectUndo(events, "Create UI EventSystem");
                Undo.AddComponent<EventSystem>(events);
                var input = Undo.AddComponent<InputSystemUIInputModule>(events); input.AssignDefaultActions();
            }
            EditorSceneManager.MarkSceneDirty(scene); Undo.CollapseUndoOperations(group);
            Selection.activeGameObject = root;
            Debug.Log("TikTok setup complete. Play: UDP port " + port + ", F2: test UI. Received events are logged only; no gameplay actions.", root);
        }
    }
}
