using RecoilPathfinding;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace RecoilPathfindingEditor
{
    public sealed class RecoilPathfindingSetupWindow : EditorWindow
    {
        private const string DemoRootName = "Pathfinding Capsule Soldiers Demo";
        private const float InitialTeamDistance = 18f;
        private const float SoldierSpacing = 1.8f;

        [MenuItem("Tools/Recoil Pathfinding/Setup All")]
        private static void Open()
        {
            var window = GetWindow<RecoilPathfindingSetupWindow>();
            window.titleContent = new GUIContent("Pathfinding Setup");
            window.minSize = new Vector2(340f, 150f);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Recoil-inspired NavMesh Demo", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Uses the Terrain in the open scene and bakes its NavMesh. The service extracts a native triangle graph, batches triangle projection through asynchronous Burst jobs, and builds cached flow fields with Burst jobs; NavMesh sampling and path smoothing remain on the main thread. Combat state (team, health, cooldown, and target) is stored in ECS entities, with combat calculations dispatched to Burst jobs. GameObjects remain the movement/render bridge. Rendering uses GPU instancing with distance/frustum culling and a simplified far model. Crowd movement uses asynchronous Burst jobs at 30 Hz; dead units are pooled (up to 2,000 per team).",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Setup All", GUILayout.Height(36f)))
                    SetupAll();
            }

            EditorGUILayout.HelpBox(
                "Play: two teams charge automatically. 1: add 500 blue, 2: add 500 red, 3: add 500 to each. " +
                "Camera: WASD move, Q/E down/up, R rotate, Up/Down arrows tilt.",
                MessageType.None);
        }

        private static void SetupAll()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                Debug.LogError("Pathfinding setup failed: no active scene is loaded.");
                return;
            }

            var terrain = FindSceneTerrain(scene);
            if (terrain == null || terrain.terrainData == null)
            {
                Debug.LogError($"Pathfinding setup failed: no Terrain with TerrainData was found in scene '{scene.name}'.");
                return;
            }

            var oldDemo = FindDemoRoot(scene);
            if (oldDemo != null)
                oldDemo.SetActive(false);

            var navMeshSurface = terrain.GetComponent<NavMeshSurface>();
            if (navMeshSurface == null)
            {
                navMeshSurface = Undo.AddComponent<NavMeshSurface>(terrain.gameObject);
            }

            navMeshSurface.collectObjects = CollectObjects.All;
            navMeshSurface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            navMeshSurface.ignoreNavMeshAgent = true;
            navMeshSurface.ignoreNavMeshObstacle = true;
            navMeshSurface.BuildNavMesh();
            EditorUtility.SetDirty(navMeshSurface);

            var triangulation = NavMesh.CalculateTriangulation();
            if (triangulation.vertices == null || triangulation.indices == null ||
                triangulation.indices.Length < 3)
            {
                if (oldDemo != null)
                    oldDemo.SetActive(true);
                Debug.LogError("Pathfinding setup failed: baking the Terrain did not produce a usable NavMesh.", terrain);
                return;
            }

            if (oldDemo != null)
                Undo.DestroyObjectImmediate(oldDemo);

            var pathService = FindSceneService(scene);
            if (pathService == null)
            {
                var serviceObject = new GameObject("Recoil Pathfinding Service");
                Undo.RegisterCreatedObjectUndo(serviceObject, "Setup Recoil Pathfinding");
                SceneManager.MoveGameObjectToScene(serviceObject, scene);
                pathService = Undo.AddComponent<RecoilNavMeshPathService>(serviceObject);
            }

            if (!TryCreateMaterials(out var blueMaterial, out var redMaterial, out var arrowMaterial))
                return;

            var demoRoot = new GameObject(DemoRootName);
            Undo.RegisterCreatedObjectUndo(demoRoot, "Create Pathfinding Demo");
            SceneManager.MoveGameObjectToScene(demoRoot, scene);

            var center = GetTerrainCenter(terrain);
            var blueTemplate = CreateUnitTemplate(demoRoot.transform, "Blue Soldier Template",
                center - Vector3.right * InitialTeamDistance + Vector3.up, blueMaterial, arrowMaterial, 90f, pathService);
            var redTemplate = CreateUnitTemplate(demoRoot.transform, "Red Soldier Template",
                center + Vector3.right * InitialTeamDistance + Vector3.up, redMaterial, arrowMaterial, -90f, pathService);
            if (blueTemplate == null || redTemplate == null)
            {
                Undo.DestroyObjectImmediate(demoRoot);
                Debug.LogError("Pathfinding setup failed: could not create both team templates on the Terrain.", terrain);
                return;
            }

            blueTemplate.SetActive(false);
            redTemplate.SetActive(false);

            var instancedRenderer = Undo.AddComponent<RecoilInstancedSoldierRenderer>(demoRoot);
            var serializedRenderer = new SerializedObject(instancedRenderer);
            serializedRenderer.FindProperty("blueUnitTemplate").objectReferenceValue = blueTemplate;
            serializedRenderer.FindProperty("redUnitTemplate").objectReferenceValue = redTemplate;
            serializedRenderer.ApplyModifiedProperties();

            var battle = Undo.AddComponent<RecoilAutoBattleDemo>(demoRoot);
            var serializedBattle = new SerializedObject(battle);
            serializedBattle.FindProperty("terrain").objectReferenceValue = terrain;
            serializedBattle.FindProperty("pathService").objectReferenceValue = pathService;
            serializedBattle.FindProperty("instancedRenderer").objectReferenceValue = instancedRenderer;
            serializedBattle.FindProperty("blueUnitTemplate").objectReferenceValue = blueTemplate;
            serializedBattle.FindProperty("redUnitTemplate").objectReferenceValue = redTemplate;
            serializedBattle.FindProperty("battleCenter").vector3Value = center;
            serializedBattle.FindProperty("teamStartDistance").floatValue =
                Mathf.Min(InitialTeamDistance, terrain.terrainData.size.x * 0.2f);
            serializedBattle.FindProperty("spawnSpacing").floatValue = SoldierSpacing;
            serializedBattle.ApplyModifiedProperties();
            Undo.AddComponent<RecoilBattlePerformanceMonitor>(demoRoot);

            SetupDemoCamera(scene, center);
            EditorSceneManager.MarkSceneDirty(scene);
            Selection.activeGameObject = demoRoot;
            EditorGUIUtility.PingObject(demoRoot);
            Debug.Log(
                "Pathfinding setup complete. Press Play: two teams charge automatically; press 1 to add 500 blue, 2 to add 500 red, or 3 to add 500 to each.",
                demoRoot);
        }

        private static GameObject CreateUnitTemplate(
            Transform parent,
            string unitName,
            Vector3 desiredPosition,
            Material unitMaterial,
            Material arrowMaterial,
            float arrowYaw,
            RecoilNavMeshPathService pathService)
        {
            if (!NavMesh.SamplePosition(desiredPosition, out var spawn, 30f, NavMesh.AllAreas))
                return null;

            var soldier = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Undo.RegisterCreatedObjectUndo(soldier, "Create Battle Unit Template");
            soldier.name = unitName;
            soldier.transform.SetParent(parent);
            soldier.transform.position = spawn.position + Vector3.up;
            soldier.GetComponent<Renderer>().sharedMaterial = unitMaterial;

            var arrow = new GameObject("Cube Arrow");
            Undo.RegisterCreatedObjectUndo(arrow, "Create Cube Direction Arrow");
            arrow.transform.SetParent(soldier.transform, false);
            arrow.transform.localRotation = Quaternion.Euler(0f, arrowYaw, 0f);
            CreateArrowCube(arrow.transform, "Arrow Shaft", new Vector3(0f, 0.55f, 0.72f),
                new Vector3(0.12f, 0.12f, 0.75f), 0f, arrowMaterial);
            CreateArrowCube(arrow.transform, "Arrow Head Left", new Vector3(-0.16f, 0.55f, 1.02f),
                new Vector3(0.12f, 0.12f, 0.38f), -45f, arrowMaterial);
            CreateArrowCube(arrow.transform, "Arrow Head Right", new Vector3(0.16f, 0.55f, 1.02f),
                new Vector3(0.12f, 0.12f, 0.38f), 45f, arrowMaterial);

            var agent = Undo.AddComponent<RecoilNavMeshPathAgent>(soldier);
            var serializedAgent = new SerializedObject(agent);
            serializedAgent.FindProperty("pathService").objectReferenceValue = pathService;
            serializedAgent.ApplyModifiedProperties();
            EditorUtility.SetDirty(soldier);
            return soldier;
        }

        private static Terrain FindSceneTerrain(Scene scene)
        {
            foreach (var terrain in Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (terrain.gameObject.scene == scene)
                    return terrain;
            }

            return null;
        }

        private static RecoilNavMeshPathService FindSceneService(Scene scene)
        {
            foreach (var service in Object.FindObjectsByType<RecoilNavMeshPathService>(
                         FindObjectsInactive.Include,
                         FindObjectsSortMode.None))
            {
                if (service.gameObject.scene == scene)
                    return service;
            }

            return null;
        }

        private static GameObject FindDemoRoot(Scene scene)
        {
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == DemoRootName)
                    return root;
            }

            return null;
        }

        private static Camera FindSceneCamera(Scene scene)
        {
            foreach (var camera in Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (camera.gameObject.scene == scene && camera.CompareTag("MainCamera"))
                    return camera;
            }

            foreach (var camera in Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (camera.gameObject.scene == scene)
                    return camera;
            }

            return null;
        }

        private static Vector3 GetTerrainCenter(Terrain terrain)
        {
            var halfSize = terrain.terrainData.size * 0.5f;
            var center = terrain.transform.TransformPoint(new Vector3(halfSize.x, 0f, halfSize.z));
            center.y = terrain.SampleHeight(center) + terrain.transform.position.y;
            return center;
        }

        private static void SetupDemoCamera(Scene scene, Vector3 terrainCenter)
        {
            var camera = FindSceneCamera(scene);
            if (camera == null)
            {
                Debug.LogWarning("No Camera was found in the active scene; the demo was created but may not be visible.");
                return;
            }

            Undo.RecordObject(camera.transform, "Frame Pathfinding Demo");
            camera.transform.position = terrainCenter + new Vector3(0f, 48f, -48f);
            var direction = terrainCenter - camera.transform.position;
            var yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            var pitch = Mathf.Atan2(-direction.y, new Vector2(direction.x, direction.z).magnitude) * Mathf.Rad2Deg;
            camera.transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

            var controller = camera.GetComponent<RecoilDemoCameraController>();
            if (controller == null)
                controller = Undo.AddComponent<RecoilDemoCameraController>(camera.gameObject);

            var serializedController = new SerializedObject(controller);
            serializedController.FindProperty("pitchDegrees").floatValue = pitch;
            serializedController.FindProperty("yawDegrees").floatValue = yaw;
            serializedController.ApplyModifiedProperties();
            EditorUtility.SetDirty(controller);
            EditorUtility.SetDirty(camera);
        }

        private static bool TryCreateMaterials(
            out Material blueMaterial,
            out Material redMaterial,
            out Material arrowMaterial)
        {
            blueMaterial = CreateDemoMaterial(
                "Assets/Scripts/RecoilPathfinding/DemoBlueSoldier.mat",
                new Color(0.2f, 0.55f, 0.95f));
            redMaterial = CreateDemoMaterial(
                "Assets/Scripts/RecoilPathfinding/DemoRedSoldier.mat",
                new Color(0.9f, 0.2f, 0.18f));
            arrowMaterial = CreateDemoMaterial(
                "Assets/Scripts/RecoilPathfinding/DemoArrow.mat",
                new Color(1f, 0.78f, 0.12f));
            return blueMaterial != null && redMaterial != null && arrowMaterial != null;
        }

        private static Material CreateDemoMaterial(string assetPath, Color color)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            if (material != null)
                return material;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Standard");
            if (shader == null)
            {
                Debug.LogError("Could not find a supported shader for the pathfinding demo materials.");
                return null;
            }

            material = new Material(shader) { color = color };
            AssetDatabase.CreateAsset(material, assetPath);
            AssetDatabase.SaveAssets();
            return material;
        }

        private static void CreateArrowCube(
            Transform parent,
            string objectName,
            Vector3 localPosition,
            Vector3 localScale,
            float yRotation,
            Material material)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Undo.RegisterCreatedObjectUndo(cube, "Create Cube Direction Arrow");
            cube.name = objectName;
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = localPosition;
            cube.transform.localScale = localScale;
            cube.transform.localRotation = Quaternion.Euler(0f, yRotation, 0f);
            cube.GetComponent<Renderer>().sharedMaterial = material;

            var collider = cube.GetComponent<Collider>();
            if (collider != null)
                Undo.DestroyObjectImmediate(collider);
        }
    }
}
