using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace RecoilPathfinding.EditorTools
{
    public sealed class StormFlipbookSkyboxSetupWindow : EditorWindow
    {
        private const string AssetFolder = "Assets/_Game/UI/Skybox";
        private const string TexturePath = AssetFolder + "/StormFlipbook_8x8.png";
        private const string ShaderPath = AssetFolder + "/StormFlipbookSkyDome.shader";
        private const string ComponentPath = AssetFolder + "/StormFlipbookSkyDome.cs";
        private const string MaterialPath = AssetFolder + "/StormFlipbookSkyDome.mat";
        private const string DomeName = "Storm Flipbook Sky Dome";

        private Camera targetCamera;
        private float domeDiameter = 500f;
        private int columns = 8;
        private int rows = 8;
        private int frameCount = 63;
        private float fps = 8f;
        private bool animate = true;
        private bool followCamera = true;

        [MenuItem("Game Tools/Công Thành/Thiết lập Skybox bão")]
        private static void Open()
        {
            var window = GetWindow<StormFlipbookSkyboxSetupWindow>("Thiết lập Skybox");
            window.minSize = new Vector2(360f, 330f);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Storm Flipbook Sky Dome", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Cấu hình atlas 8×8, tạo material đúng shader và thêm sky dome vào scene đang mở.",
                MessageType.Info);

            targetCamera = (Camera)EditorGUILayout.ObjectField("Camera", targetCamera, typeof(Camera), true);
            domeDiameter = EditorGUILayout.FloatField("Đường kính dome", domeDiameter);
            columns = Mathf.Max(1, EditorGUILayout.IntField("Số cột", columns));
            rows = Mathf.Max(1, EditorGUILayout.IntField("Số hàng", rows));
            frameCount = Mathf.Max(1, EditorGUILayout.IntField("Số frame", frameCount));
            fps = Mathf.Max(0.01f, EditorGUILayout.FloatField("FPS", fps));
            animate = EditorGUILayout.Toggle("Chạy animation", animate);
            followCamera = EditorGUILayout.Toggle("Đi theo camera", followCamera);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!CanConfigureAssets()))
            {
                if (GUILayout.Button("Cấu hình texture + tạo material"))
                    ConfigureAssets();
            }

            using (new EditorGUI.DisabledScope(AssetDatabase.LoadAssetAtPath<Material>(MaterialPath) == null))
            {
                if (GUILayout.Button("Thêm / cập nhật dome trong scene"))
                    CreateOrUpdateDome();
            }

            if (GUILayout.Button("Thiết lập nhanh: asset và dome"))
            {
                if (ConfigureAssets())
                    CreateOrUpdateDome();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Material", MaterialPath, EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Texture", TexturePath, EditorStyles.miniLabel);
        }

        private static bool CanConfigureAssets()
        {
            return AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath) != null &&
                   AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath) != null &&
                   AssetDatabase.LoadAssetAtPath<MonoScript>(ComponentPath) != null;
        }

        private static bool ConfigureAssets()
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (texture == null || shader == null || AssetDatabase.LoadAssetAtPath<MonoScript>(ComponentPath) == null)
            {
                EditorUtility.DisplayDialog("Không tìm thấy asset",
                    "Cần có PNG atlas, shader và StormFlipbookSkyDome.cs trong Assets/_Game/UI/Skybox.", "Đóng");
                return false;
            }

            if (AssetImporter.GetAtPath(TexturePath) is TextureImporter importer)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.maxTextureSize = 4096;
                importer.mipmapEnabled = false;
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }

            texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                material = new Material(shader) { name = "StormFlipbookSkyDome" };
                AssetDatabase.CreateAsset(material, MaterialPath);
            }
            else
            {
                Undo.RecordObject(material, "Update Storm Flipbook Sky Material");
                material.shader = shader;
            }

            material.SetTexture("_MainTex", texture);
            material.SetFloat("_Columns", 8f);
            material.SetFloat("_Rows", 8f);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return true;
        }

        private void CreateOrUpdateDome()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            var componentType = AssetDatabase.LoadAssetAtPath<MonoScript>(ComponentPath)?.GetClass();
            if (material == null || componentType == null)
            {
                EditorUtility.DisplayDialog("Chưa sẵn sàng",
                    "Hãy cấu hình texture và material trước; component SkyDome cần biên dịch thành công.", "Đóng");
                return;
            }

            if (targetCamera == null)
                targetCamera = Camera.main;
            if (targetCamera == null)
                targetCamera = Object.FindFirstObjectByType<Camera>();

            var dome = GameObject.Find(DomeName);
            if (dome == null)
            {
                dome = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Undo.RegisterCreatedObjectUndo(dome, "Create Storm Flipbook Sky Dome");
                dome.name = DomeName;
            }
            else
            {
                Undo.RecordObject(dome.transform, "Update Storm Flipbook Sky Dome");
            }

            var collider = dome.GetComponent<Collider>();
            if (collider != null)
                Undo.DestroyObjectImmediate(collider);

            dome.transform.localScale = Vector3.one * Mathf.Max(1f, domeDiameter);
            var renderer = dome.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.enabled = true;

            if (targetCamera != null)
            {
                Undo.RecordObject(targetCamera, "Extend Camera Far Clip For Sky Dome");
                targetCamera.farClipPlane = Mathf.Max(targetCamera.farClipPlane, domeDiameter * 0.6f);
                dome.layer = targetCamera.gameObject.layer;
            }

            var controller = dome.GetComponent(componentType);
            if (controller == null)
                controller = Undo.AddComponent(dome, componentType);

            var serialized = new SerializedObject(controller);
            serialized.FindProperty("targetCamera").objectReferenceValue = targetCamera;
            serialized.FindProperty("skyMaterial").objectReferenceValue = material;
            serialized.FindProperty("columns").intValue = columns;
            serialized.FindProperty("rows").intValue = rows;
            serialized.FindProperty("frameCount").intValue = Mathf.Clamp(frameCount, 1, Mathf.Max(1, columns * rows - 1));
            serialized.FindProperty("fps").floatValue = fps;
            serialized.FindProperty("animate").boolValue = animate;
            serialized.FindProperty("followCamera").boolValue = followCamera;
            serialized.ApplyModifiedProperties();

            if (targetCamera != null && followCamera)
                dome.transform.position = targetCamera.transform.position;
            Selection.activeGameObject = dome;
            EditorSceneManager.MarkSceneDirty(dome.scene);
        }
    }
}
