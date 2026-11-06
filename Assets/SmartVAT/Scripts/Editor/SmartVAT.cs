using NUnit.Framework;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Formats.Alembic.Importer;
using UnityEngine.Playables;
using UnityEngine.Timeline;
using static UnityEngine.Mesh;

namespace SmartVAT
{
    [System.Serializable]
    public class AnimatedObject
    {
        [SerializeField] public Renderer rendrer;
        [SerializeField] public int index;
    }
    public struct smartVATdata
    {
        public Vector2[] uv2;
        public Vector2[] uv3;
        public Vector3[] bounds;
    }
    public class SmartVAT : EditorWindow
    {
        public GameObject smartVatPreviousSettings;
        [MenuItem("Tools/Smart VAT Free/Smart VAT Free")]
        public static void ShowExample()
        {
            SmartVAT wnd = GetWindow<SmartVAT>();
            wnd.titleContent = new GUIContent("Smart VAT Free");
        }
        SmartVAT vatConverter;
        public SerializedObject so;
        bool showAdvancedSettings = false;
        bool showColorLegend = true;
        bool settingsFileExists;
        string foldoutStatus;
        bool windowInitialized = false;
        [SerializeField] private bool exportPathInitialized;
        [SerializeField] private bool assetNameInitialized;
        [SerializeField] private bool presetSeeded;
        int resolutionX;
        string settingsPath;
        string settingsFullPath;
        private SmartVATSettings defaultSettings;
        private AlembicStreamPlayer alembicPlayer;

        [Tooltip("Name of the final Prefab. Resulting textures and mesh will also inherit this as part of their names.")] public string assetName;
        [Tooltip("Vertical resolution of the VAT textures.")] public int frameCount;
        public PlayableDirector playableDirector;
        [SerializeField] public Renderer[] animatedMeshes;
        [SerializeField] public AnimatedObject[] animatedObjects;
        [SerializeField] public AnimatedObject[] sortedAnimatedObjects;
        public int[] rendererType;
        public Mesh[] baseMeshes;
        public List<List<Mesh>> bakedFrames;
        //private WaitForFixedUpdate wait = new WaitForFixedUpdate();
        public Vector2[] uv2 = new Vector2[0];
        List<Vector3> combinedBasePositions = new List<Vector3>();
        List<Vector3> combinedBaseNormals = new List<Vector3>();

        Vector2 scrollPosition;
        Rect animatedObjectsHeaderRect;

        public string exportPath = "Assets/";
        string texPrefix;
        string meshPrefix;
        string posSuffix;
        string normSuffix;
        string matPrefix;
        string[] presetNames;
        SmartVATPresets[] settingPresets;

        //string vatSuffix;
        public bool bakeVertexColor = true;

        private string posTexPath;
        private string normTexPath;
        private string meshPath;
        private string materialPath;

        bool settingsValid;
        bool cancelRequested;

        public enum MeshBakePose
        {
            FirstFrame = 1,
            OriginalPose = 0
        }

        public enum ExportType
        {
            MeshRenderer = 0,
            ParticleSystem = 1
        }

        public enum DirectionBake
        {
            [InspectorName("Copy Source")] orig = 0,
            [InspectorName("X+")] Xpos = 1,
            [InspectorName("X-")] Xneg = 2,
            [InspectorName("Y+")] Ypos = 3,
            [InspectorName("Y-")] Yneg = 4,
            [InspectorName("Z+")] Zpos = 5,
            [InspectorName("Z-")] Zneg = 6
        }

        public enum MaterialMergeMode
        {
            [InspectorName("Merge All (Index Ignored)")] mergeall = 0,
            [InspectorName("Multiple Materials")] multimats = 1,
            [InspectorName("Texture Array")] texarray = 2
        }

        public enum NormalTextureOptimize
        {
            ExcludeNonMoving = 0,
            IncludeAll = 1
        }


        [Tooltip("This can help to optimize the VAT texture size if some vertices never move from the first frame but are at different positions from the bind pose. The tool doesn't generate texture data for vertices that never move.")] public MeshBakePose meshBakePose = (MeshBakePose)1;
        [Tooltip("Set to Include All if you need to bake a single direction for particle purposes. Normals that are detected as non moving only occupy 1 pixel in the texture as opposed to 1 x FrameCount for animated ones.")] public NormalTextureOptimize normalTextureMode = (NormalTextureOptimize)0;
        [Tooltip("Override Vertex Normal with a single direction. Hack for Particle Vertex Offset Alignment. With Normal Texture Optimize Off, you can ensure that all Vertex Normals are store in the Texture.")] public DirectionBake meshNormalBake = (DirectionBake)0;
        [Tooltip("Override Vertex Tangent with a single direction. Hack for Particle Vertex Offset Alignment.")] public DirectionBake tangentBake = (DirectionBake)0;
        [Tooltip("How to handle merging of multiple materials.\n\nMerge All: All materials are merged. Index property is ignored.\n\nMultiple Materials: Mesh use multiple materials. Manually set Material IDs next to each Animated Object.\n\nTexture Array: Mesh uses a single material, but material uses Texture Arrays. Manually set Array Index next to each Animated Object.")] public MaterialMergeMode materialMergeMode = (MaterialMergeMode)0;

        public ExportType exportType = (ExportType)0;

        List<List<Vector3>> basePositions;

        private Mesh newMesh;
        public int vertexMaxID = 0;
        public int normalMaxID = 0;
        public List<int> uvID = new List<int>();
        public List<List<List<int>>> matchingVertices = new List<List<List<int>>>(0);
        public List<List<List<int>>> matchingNormals = new List<List<List<int>>>(0);

        public Texture2D positionTex;
        public Texture2D normalTex;
        public Texture2D optimizedPositionTex;
        public Texture2D optimizedNormalTex;

        [Min(0f)] public float movementFilter = 0f;
        [Min(0f)] public float normalFilter = 1f;
        [Min(0f)] public float movementMatchFilter = 0f;
        [Min(0f)] public float normalMatchFilter = 0f;

        public UnityEngine.Transform[] objectTransform;

        int presetNum = 0;

        SerializedProperty r_presetNum;
        SerializedProperty r_materialMergeMode;
        SerializedProperty r_animatedObjects;
        SerializedProperty r_timeline;
        SerializedProperty r_meshBakePose;
        SerializedProperty r_normalTextureMode;
        SerializedProperty r_meshNormalBake;
        SerializedProperty r_tangentBake;
        SerializedProperty r_noPosMoveTolerance;
        SerializedProperty r_noNormMoveTolerance;
        SerializedProperty r_samePosTolerance;
        SerializedProperty r_sameNormTolerance;
        SerializedProperty r_frameCount;
        SerializedProperty r_bakeVertexColor;
        SerializedProperty r_assetName;
        SerializedProperty r_exportPath;
        SerializedProperty r_exportType;

        public void CreateGUI()
        {
            InitializeWindow();
        }



        public void OnGUI()
        {
            scrollPosition = GUILayout.BeginScrollView(
                scrollPosition);
            //Styles
            GUIStyle pathButton = new GUIStyle(GUI.skin.button)
            {
                fixedWidth = 30
            };
            GUIStyle processButton = new GUIStyle(GUI.skin.button)
            {
                fixedWidth = 170
            };
            GUIStyle wideProcessButton = new GUIStyle(GUI.skin.button)
            {
                fixedWidth = 240
            };
            GUIStyle pathStyle = new GUIStyle(GUI.skin.label)
            {
                stretchWidth = true,
                alignment = TextAnchor.MiddleLeft
            };
            GUIStyle headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold
            };
            GUIStyle foldoutStyle = new GUIStyle(GUI.skin.box)
            {
                fontStyle = FontStyle.Bold
            };


            GUIStyle enumStyle = new GUIStyle()
            {
                fixedWidth = 170
            };
            GUILayoutOption[] enumOptions = new GUILayoutOption[]
            {
            GUILayout.Width(270)
            };
            if (windowInitialized)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(new GUIContent("Load Settings from Preset (Pro)",
                        "Preset switching available in the full version of Smart VAT."), headerStyle);
                    GUILayout.Button("Refresh Presets");
                    EditorGUILayout.EndHorizontal();
                }
                /*
                r_presetNum = so.FindProperty("presetNum");
                r_animatedObjects = so.FindProperty("animatedObjects");
                r_timeline = so.FindProperty("playableDirector");
                r_meshBakePose = so.FindProperty("meshBakePose");
                r_normalTextureMode = so.FindProperty("normalTextureMode");
                r_meshNormalBake = so.FindProperty("meshNormalBake");
                r_tangentBake = so.FindProperty("tangentBake");
                r_noPosMoveTolerance = so.FindProperty("movementFilter");
                r_noNormMoveTolerance = so.FindProperty("normalFilter");
                r_samePosTolerance = so.FindProperty("movementMatchFilter");
                r_frameCount = so.FindProperty("frameCount");
                r_bakeVertexColor = so.FindProperty("bakeVertexColor");
                r_assetName = so.FindProperty("assetName");
                r_exportPath = so.FindProperty("exportPath");
                r_exportType = so.FindProperty("exportType");*/

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.Popup(presetNum, presetNames);
                }

                EditorGUILayout.LabelField("Targeted Objects", headerStyle);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(r_timeline, true); // True means show children
                if (GUILayout.Button("Create new from Animated Objects", wideProcessButton))
                {
                    CreateTimeline();
                }   


                EditorGUILayout.EndHorizontal();
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(r_materialMergeMode, new GUIContent("Material Merge Mode (Pro)",
                        "How to handle merging of multiple materials.\n\nMerge All: All materials are merged. Index property is ignored.\n\nMultiple Materials and Texture Array modes available in the full version of Smart VAT."),
                        true);
                }

                HandleAnimatedObjectDrop(animatedObjectsHeaderRect);
                EditorGUILayout.PropertyField(r_animatedObjects, true);
                if (Event.current.type == EventType.Repaint)
                {
                    Rect full = GUILayoutUtility.GetLastRect();
                    animatedObjectsHeaderRect = new Rect(full.x, full.y, full.width, EditorGUIUtility.singleLineHeight);
                }


                /*
                for (int i = 0; i < animatedObjects.Length; i++)
                {
                    animatedObjects[i].index = i;
                    r_animatedObjects[i] = animatedObjects[i];
                }*/

                EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

                showAdvancedSettings = EditorGUILayout.BeginFoldoutHeaderGroup(showAdvancedSettings, foldoutStatus);
                if (showAdvancedSettings)
                {
                    foldoutStatus = "Hide Advanced Settings";
                    using (new EditorGUI.DisabledScope(true))
                    {
                        DrawFilterRow(new GUIContent("Movement Filter (Pro)",
                            "Vertices that never move by more than this value are considered static and won't generate texture data on export.\n\nAvailable in the full version of Smart VAT."),
                            r_noPosMoveTolerance);
                        DrawFilterRow(new GUIContent("Normal Filter (Pro)",
                            "Normals that never rotate by more than this angle are considered static and won't generate texture data on export.\n\nAvailable in the full version of Smart VAT."),
                            r_noNormMoveTolerance);
                        DrawFilterRow(new GUIContent("Movement Match Filter (Pro)",
                            "Vertices that are always within that distance from each other through every frame of the animation will snap and share the same VAT pixels.\n\nAvailable in the full version of Smart VAT."),
                            r_samePosTolerance);
                        DrawFilterRow(new GUIContent("Normal Match Filter (Pro)",
                            "Normals that are always under that angle difference from each other through every frame of the animation will snap and share the same VAT pixels.\n\nAvailable in the full version of Smart VAT."),
                            r_sameNormTolerance);
                    }
                    EditorGUILayout.PropertyField(r_bakeVertexColor, true);
                    EditorGUILayout.PropertyField(r_frameCount, true);
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.PropertyField(r_meshBakePose, new GUIContent("Mesh Bake Pose (Pro)",
                            "This can help to optimize the VAT texture size if some vertices never move from the first frame but are at different positions from the bind pose. The tool doesn't generate texture data for vertices that never move.\n\nAvailable in the full version of Smart VAT."),
                            true, enumOptions);
                        EditorGUILayout.PropertyField(r_normalTextureMode, new GUIContent("Normal Texture Mode (Pro)",
                            "Set to Include All if you need to bake a single direction for particle purposes. Normals that are detected as non moving only occupy 1 pixel in the texture as opposed to 1 x FrameCount for animated ones.\n\nAvailable in the full version of Smart VAT."),
                            true, enumOptions);
                        EditorGUILayout.PropertyField(r_meshNormalBake, new GUIContent("Mesh Normal Bake (Pro)",
                            "Override Vertex Normal with a single direction. Hack for Particle Vertex Offset Alignment. With Normal Texture Optimize Off, you can ensure that all Vertex Normals are stored in the Texture.\n\nAvailable in the full version of Smart VAT."),
                            true, enumOptions);
                        EditorGUILayout.PropertyField(r_tangentBake, new GUIContent("Tangent Bake (Pro)",
                            "Override Vertex Tangent with a single direction. Hack for Particle Vertex Offset Alignment.\n\nAvailable in the full version of Smart VAT."),
                            true, enumOptions);
                    }
                }
                else
                {
                    foldoutStatus = "Show Advanced Settings";
                }
                EditorGUILayout.EndFoldoutHeaderGroup();

                EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);
                EditorGUILayout.LabelField("File Export Settings", headerStyle);
                EditorGUILayout.LabelField("Asset Base Name", headerStyle);
                EditorGUILayout.PropertyField(r_assetName, GUIContent.none);
                EditorGUILayout.LabelField("Export Path", headerStyle);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(r_exportPath, GUIContent.none);

                if (GUILayout.Button("...", pathButton))
                {
                    r_exportPath.stringValue = SetExportPath();
                    if (r_exportPath.stringValue.StartsWith(Application.dataPath))
                    {
                        r_exportPath.stringValue = "Assets" + r_exportPath.stringValue.Substring(Application.dataPath.Length);
                    }
                }

                EditorGUILayout.EndHorizontal();
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(r_exportType, new GUIContent("Export Type (Pro)",
                        "ParticleSystem export available in the full version of Smart VAT."),
                        true, enumOptions);
                }
                so.ApplyModifiedProperties();

                EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Filter Mesh Preview", wideProcessButton))
                {
                    RefreshAnimatedMeshesFromObjects();
                    if (CheckSettings())
                    {
                        CheckTimeline(PreviewPositionFilter);
                    }
                    else
                    {
                        ErrorDialog();
                    }
                }
                EditorGUILayout.EndHorizontal();

                showColorLegend = EditorGUILayout.BeginFoldoutHeaderGroup(showColorLegend, "Filter Mesh Preview Color Legend");
                if (showColorLegend)
                {
                    EditorGUILayout.LabelField("White = Normal Rotation + Movement Detected");
                    EditorGUILayout.LabelField("Yellow = Normal Rotation Filtered Out");
                    EditorGUILayout.LabelField("Blue = Movement Filtered Out");
                }
                EditorGUILayout.EndFoldoutHeaderGroup();

                if (GUILayout.Button("Export VAT", processButton))
                {
                    RefreshAnimatedMeshesFromObjects();
                    settingsValid = CheckSettings();
                    if (settingsValid)
                    {
                        CheckTimeline(ProcessVAT);
                    }
                    else
                    {
                        ErrorDialog();
                    }
                }
            }
            GUILayout.EndScrollView();
        }




        public void ProcessVAT()
        {
            cancelRequested = false;
            materialMergeMode = (MaterialMergeMode)0;
            meshBakePose = (MeshBakePose)1;
            normalTextureMode = (NormalTextureOptimize)0;
            tangentBake = (DirectionBake)0;
            meshNormalBake = (DirectionBake)0;
            exportType = (ExportType)0;
            normalMatchFilter = 1f;
            try
            {
            if (ReportProgress("Preparing meshes", 0f)) return;
            AnimatedObject[] sortedAnimatedObjects = animatedObjects.OrderBy(x => x.index).ToArray();


            animatedMeshes = new Renderer[sortedAnimatedObjects.Length];
            for (int i = 0; i < sortedAnimatedObjects.Length; i++)
            {
                animatedMeshes[i] = sortedAnimatedObjects[i].rendrer;
            }

            //Issue with above code is that the asset that has an Animator Component is not the Mesh Renderer.

            //List of all transforms associated to the meshes.
            objectTransform = new UnityEngine.Transform[animatedMeshes.Length];
            for (int i = 0; i < animatedMeshes.Length; i++)
            {
                objectTransform[i] = animatedMeshes[i].transform;

            }
            //Create list of bool to check if renderer is skinned or not.
            rendererType = new int[animatedMeshes.Length];
            for (int i = 0; i < animatedMeshes.Length; i++)
            {
                bool isSkinned = animatedMeshes[i].TryGetComponent<SkinnedMeshRenderer>(out SkinnedMeshRenderer skip);
                bool isAlembic = animatedMeshes[i].TryGetComponent<AlembicCustomData>(out AlembicCustomData skip2);
                if (isSkinned)
                {
                    rendererType[i] = 0; //0 means it's a skinned renderer
                }
                else if (isAlembic)
                {
                    rendererType[i] = 2; //2 means it's an alembic
                }
                else
                {
                    rendererType[i] = 1; //1 means it's a mesh filter
                }

            }



            newMesh = new Mesh();
            float timelineDuration = (float)playableDirector.duration;
            float animationStepSize = timelineDuration / frameCount;

            //baseMeshes is the reference mesh to determine all vertex movement
            bakedFrames = new List<List<Mesh>>();
            resolutionX = 0;
            basePositions = new List<List<Vector3>>();

            baseMeshes = CreateBaseMeshes(animatedMeshes);

            //Debug.Log("Basemesh Color 0: " + baseMeshes[0].colors[0]);
            //Converting baseMeshes to Array


            //Create temporary mesh for every exported frame 
            for (int i = 0; i < baseMeshes.Length; i++)
            {
                resolutionX += baseMeshes[i].vertexCount;
                basePositions.Add(baseMeshes[i].vertices.ToList());

                bakedFrames.Add(new List<Mesh>());

                if (rendererType[i] == 0)
                {
                    SkinnedMeshRenderer skinnedMeshRenderer = animatedMeshes[i].GetComponent<SkinnedMeshRenderer>();
                    for (int j = 0; j < frameCount; j++)
                    {
                        if (ReportProgress($"Baking mesh {i + 1}/{baseMeshes.Length} frame {j + 1}/{frameCount}",
                            ((float)i + (float)j / frameCount) / baseMeshes.Length * 0.5f)) return;
                        //control Playable Director time before baking mesh at that position
                        playableDirector.time = (double)(j * animationStepSize);
                        playableDirector.Evaluate();

                        bakedFrames[i].Add(new Mesh());

                        skinnedMeshRenderer.BakeMesh(bakedFrames[i][j]);

                        bakedFrames[i][j] = BakeStaticMesh(bakedFrames[i][j], objectTransform[i]);

                        /*
                        GameObject go = new GameObject("BakedFrames_" + j, typeof(MeshFilter), typeof(MeshRenderer));
                        MeshFilter goMF = go.GetComponent<MeshFilter>();
                        goMF.mesh = bakedFrames[i][j];*/

                    }
                }
                else if (rendererType[i] == 1)
                {
                    MeshFilter meshFilter = animatedMeshes[i].GetComponent<MeshFilter>();
                    for (int j = 0; j < frameCount; j++)
                    {
                        if (ReportProgress($"Baking mesh {i + 1}/{baseMeshes.Length} frame {j + 1}/{frameCount}",
                            ((float)i + (float)j / frameCount) / baseMeshes.Length * 0.5f)) return;
                        //control Playable Director time before baking mesh at that position
                        playableDirector.time = (double)(j * animationStepSize);
                        playableDirector.Evaluate();

                        //Debug.Log("Frame " + j + " is: " + objectTransform[i].rotation);

                        bakedFrames[i].Add(new Mesh());
                        bakedFrames[i][j] = BakeStaticMesh(animatedMeshes[i].GetComponent<MeshFilter>().sharedMesh, objectTransform[i]);



                        /*
                        GameObject go = new GameObject("BakedFrames_" + j, typeof(MeshFilter), typeof(MeshRenderer));
                        MeshFilter goMF = go.GetComponent<MeshFilter>();
                        goMF.mesh = bakedFrames[i][j];*/
                    }
                }
                else
                {
                    MeshFilter meshFilter = animatedMeshes[i].GetComponent<MeshFilter>();
                    FindAlembicPlayer(animatedMeshes[i].gameObject);
                    if (alembicPlayer == null || !alembicPlayer.isActiveAndEnabled)
                    {
                        return;
                    }

                    playableDirector.time = (double)(0f);
                    playableDirector.Evaluate();
                    alembicPlayer.UpdateImmediately(alembicPlayer.CurrentTime);
                    int alembicVertexCount = meshFilter.sharedMesh.vertexCount;
                    int alembicTriangleCount = meshFilter.sharedMesh.triangles.Length;
                    for (int j = 0; j < frameCount; j++)
                    {
                        if (ReportProgress($"Baking mesh {i + 1}/{baseMeshes.Length} frame {j + 1}/{frameCount}",
                            ((float)i + (float)j / frameCount) / baseMeshes.Length * 0.5f)) return;
                        //control Playable Director time before baking mesh at that position
                        playableDirector.time = (double)(j * animationStepSize);
                        playableDirector.Evaluate();
                        alembicPlayer.UpdateImmediately(alembicPlayer.CurrentTime);

                        bakedFrames[i].Add(new Mesh());
                        bakedFrames[i][j] = BakeStaticMesh(animatedMeshes[i].GetComponent<MeshFilter>().sharedMesh, objectTransform[i]);

                        if (bakedFrames[i][j].vertexCount != alembicVertexCount || bakedFrames[i][j].triangles.Length != alembicTriangleCount)
                        {
                            AlembicVertexError();
                            return;
                        }

                        /*
                        GameObject go = new GameObject("BakedFrames_" + j, typeof(MeshFilter), typeof(MeshRenderer));
                        MeshFilter goMF = go.GetComponent<MeshFilter>();
                        goMF.mesh = bakedFrames[i][j];*/
                    }
                }
            }






            combinedBasePositions = new List<Vector3>();
            for (int i = 0; i < baseMeshes.Length; i++)
            {
                combinedBasePositions.AddRange(baseMeshes[i].vertices);
            }


            combinedBaseNormals = new List<Vector3>();
            for (int i = 0; i < baseMeshes.Length; i++)
            {
                combinedBaseNormals.AddRange(baseMeshes[i].normals);
            }

            List<Vector3> newVertices = new List<Vector3>();
            List<Vector3> newNormals = new List<Vector3>();
            List<Vector4> newTangents = new List<Vector4>();
            List<Vector2> newUV = new List<Vector2>();
            List<int> newTriangles = new List<int>();
            List<Color32> newColors = new List<Color32>();

            Vector4 bakedTangent = new Vector4(1, 0, 0, 1);

            int currentTriangleCount = 0;

            for (int i = 0; i < baseMeshes.Length; i++)
            {
                Mesh baseMesh = baseMeshes[i];
                Vector3[] vertices = baseMesh.vertices;
                Vector3[] normals = baseMesh.normals;
                Vector4[] tangents = baseMesh.tangents;
                Color32[] colors32 = baseMesh.colors32;
                int[] triangles = baseMesh.triangles;
                Vector2[] uv = baseMesh.uv;
                UnityEngine.Transform t = objectTransform[i];
                bool hasColors = colors32.Length > 0;

                if (tangents.Length < vertices.Length)
                {
                    MissingTangentsError(baseMesh != null ? baseMesh.name : "<unnamed>");
                    return;
                }

                for (int j = 0; j < vertices.Length; j++)
                {

                    newVertices.Add(vertices[j]);
                    newNormals.Add(normals[j]);

                    Vector4 srcT = tangents[j];
                    Vector3 dirT = t.TransformDirection(new Vector3(srcT.x, srcT.y, srcT.z));
                    newTangents.Add(new Vector4(dirT.x, dirT.y, dirT.z, srcT.w));

                    if (!hasColors | !bakeVertexColor)
                    {
                        newColors.Add(new Color32(255, 255, 255, 255));
                    }
                    else
                    {
                        newColors.Add(colors32[j]);
                    }
                }

                newUV.AddRange(uv);

                //need to create a temp list and add the previous mesh vertex count to every triangle of the next one.
                List<int> tempTriangles = new List<int>(triangles.Length);
                for (int j = 0; j < triangles.Length; j++)
                {
                    tempTriangles.Add(triangles[j] + currentTriangleCount);
                }

                newTriangles.AddRange(tempTriangles);
                currentTriangleCount += vertices.Length;

            }


            if (ReportProgress("Optimizing VAT data", 0.5f)) return;
            smartVATdata vatData = OptimizeVAT(newVertices, newNormals);
            if (cancelRequested) return;

            //add bound vertices
            newVertices.Add(vatData.bounds[0]);
            newVertices.Add(vatData.bounds[0]);
            newVertices.Add(vatData.bounds[0]);
            newVertices.Add(vatData.bounds[1]);
            newVertices.Add(vatData.bounds[1]);
            newVertices.Add(vatData.bounds[1]);

            //Add normals, uvs, colors and tangents to bound points
            for (int i = 0; i < 6; i++)
            {
                newNormals.Add(Vector3.up);
                newUV.Add(Vector2.zero);
                newColors.Add(Color.white);
                newTangents.Add(bakedTangent);
            }

            //Create bound triangles

            int[] boundVertices = new int[]
                {
                (newVertices.Count - 6),
                (newVertices.Count - 5),
                (newVertices.Count - 4),
                (newVertices.Count - 3),
                (newVertices.Count - 2),
                (newVertices.Count - 1),
                };
            newTriangles.AddRange(boundVertices);




            newMesh.vertices = newVertices.ToArray();
            newMesh.normals = newNormals.ToArray();
            newMesh.tangents = newTangents.ToArray();
            newMesh.uv = newUV.ToArray();
            newMesh.triangles = newTriangles.ToArray();
            newMesh.uv2 = vatData.uv2; //function already returned uv2 + bounds
            newMesh.uv3 = vatData.uv3;

            newMesh.colors32 = newColors.ToArray();





            meshPath = exportPath + meshPrefix + assetName + ".asset";
            System.IO.Directory.CreateDirectory(exportPath);
            newMesh.RecalculateNormals();
            newMesh.normals = newNormals.ToArray();

            if (ReportProgress("Saving mesh asset", 0.95f)) return;
            AssetDatabase.CreateAsset(newMesh, meshPath);
            AssetDatabase.SaveAssets();


            if (ReportProgress("Creating prefab and material", 0.98f)) return;
            CreateFinalAsset();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }


        public void PreviewPositionFilter()
        {
            RunFilterAnalysis();
        }

        private void RunFilterAnalysis()
        {
            cancelRequested = false;
            GameObject previewGo = null;

            try
            {
                if (ReportProgress("Preparing preview", 0f)) return;

                AnimatedObject[] sortedObjs = animatedObjects.OrderBy(x => x.index).ToArray();
                Renderer[] meshes = new Renderer[sortedObjs.Length];
                for (int i = 0; i < sortedObjs.Length; i++)
                {
                    meshes[i] = sortedObjs[i].rendrer;
                }
                animatedMeshes = meshes;

                objectTransform = new UnityEngine.Transform[meshes.Length];
                rendererType = new int[meshes.Length];
                AlembicStreamPlayer[] alembicPlayers = new AlembicStreamPlayer[meshes.Length];
                for (int i = 0; i < meshes.Length; i++)
                {
                    objectTransform[i] = meshes[i].transform;
                    bool isSkinned = meshes[i].TryGetComponent<SkinnedMeshRenderer>(out _);
                    bool isAlembic = meshes[i].TryGetComponent<AlembicCustomData>(out _);
                    rendererType[i] = isSkinned ? 0 : (isAlembic ? 2 : 1);
                    if (rendererType[i] == 2)
                    {
                        FindAlembicPlayer(meshes[i].gameObject);
                        if (alembicPlayer == null || !alembicPlayer.isActiveAndEnabled)
                        {
                            return;
                        }
                        alembicPlayers[i] = alembicPlayer;
                    }
                }

                baseMeshes = CreateBaseMeshes(meshes);

                int totalVerts = 0;
                int totalTris = 0;
                for (int i = 0; i < baseMeshes.Length; i++)
                {
                    totalVerts += baseMeshes[i].vertexCount;
                    totalTris += baseMeshes[i].triangles.Length;
                }

                Vector3[] basePositions = new Vector3[totalVerts];
                Vector3[] baseNormals = new Vector3[totalVerts];
                int wp = 0;
                for (int i = 0; i < baseMeshes.Length; i++)
                {
                    Vector3[] mv = baseMeshes[i].vertices;
                    Vector3[] mn = baseMeshes[i].normals;
                    for (int j = 0; j < mv.Length; j++)
                    {
                        basePositions[wp] = mv[j];
                        baseNormals[wp] = mn[j];
                        wp++;
                    }
                }

                bool[] isMoving = new bool[totalVerts];
                bool[] normalIsAnimated = new bool[totalVerts];

                float duration = (float)playableDirector.duration;
                float step = duration / frameCount;

                for (int frame = 0; frame < frameCount; frame++)
                {
                    if (ReportProgress($"Sampling frame {frame + 1}/{frameCount}",
                        0.05f + 0.85f * (float)frame / frameCount)) return;

                    playableDirector.time = frame * step;
                    playableDirector.Evaluate();

                    int offset = 0;
                    for (int i = 0; i < meshes.Length; i++)
                    {
                        Mesh staticMesh;
                        if (rendererType[i] == 0)
                        {
                            Mesh skinned = new Mesh();
                            meshes[i].GetComponent<SkinnedMeshRenderer>().BakeMesh(skinned);
                            staticMesh = BakeStaticMesh(skinned, objectTransform[i]);
                            UnityEngine.Object.DestroyImmediate(skinned);
                        }
                        else if (rendererType[i] == 1)
                        {
                            staticMesh = BakeStaticMesh(meshes[i].GetComponent<MeshFilter>().sharedMesh, objectTransform[i]);
                        }
                        else
                        {
                            alembicPlayers[i].UpdateImmediately(alembicPlayers[i].CurrentTime);
                            staticMesh = BakeStaticMesh(meshes[i].GetComponent<MeshFilter>().sharedMesh, objectTransform[i]);
                        }

                        Vector3[] fv = staticMesh.vertices;
                        Vector3[] fn = staticMesh.normals;
                        int vc = baseMeshes[i].vertexCount;
                        if (fv.Length == vc)
                        {
                            for (int k = 0; k < vc; k++)
                            {
                                int idx = offset + k;
                                if (!isMoving[idx])
                                {
                                    float dx = fv[k].x - basePositions[idx].x;
                                    float dy = fv[k].y - basePositions[idx].y;
                                    float dz = fv[k].z - basePositions[idx].z;
                                    if (Mathf.Abs(dx) > movementFilter ||
                                        Mathf.Abs(dy) > movementFilter ||
                                        Mathf.Abs(dz) > movementFilter)
                                    {
                                        isMoving[idx] = true;
                                    }
                                }
                                if (!normalIsAnimated[idx])
                                {
                                    float angle = Vector3.Angle(Vector3.Normalize(baseNormals[idx]), Vector3.Normalize(fn[k]));
                                    if (Mathf.Abs(angle) >= normalFilter)
                                    {
                                        normalIsAnimated[idx] = true;
                                    }
                                }
                            }
                        }

                        UnityEngine.Object.DestroyImmediate(staticMesh);
                        offset += vc;
                    }
                }

                if (ReportProgress("Building preview mesh", 0.95f)) return;

                Vector3[] outVerts = new Vector3[totalVerts];
                Vector3[] outNorms = new Vector3[totalVerts];
                Vector2[] outUv = new Vector2[totalVerts];
                Color[] outColors = new Color[totalVerts];
                int[] outTris = new int[totalTris];

                int vi = 0;
                int ti = 0;
                for (int i = 0; i < baseMeshes.Length; i++)
                {
                    Vector3[] mv = baseMeshes[i].vertices;
                    Vector3[] mn = baseMeshes[i].normals;
                    Vector2[] muv = baseMeshes[i].uv;
                    int[] mt = baseMeshes[i].triangles;
                    int meshStart = vi;
                    bool hasUv = muv.Length == mv.Length;
                    for (int k = 0; k < mv.Length; k++)
                    {
                        outVerts[vi] = mv[k];
                        outNorms[vi] = mn[k];
                        outUv[vi] = hasUv ? muv[k] : Vector2.zero;
                        float move = isMoving[vi] ? 1f : 0f;
                        float rot = normalIsAnimated[vi] ? 1f : 0f;
                        outColors[vi] = new Color(move, move, rot, 1f);
                        vi++;
                    }
                    for (int k = 0; k < mt.Length; k++)
                    {
                        outTris[ti++] = mt[k] + meshStart;
                    }
                }

                Mesh previewMesh = new Mesh();
                previewMesh.indexFormat = totalVerts > 65535
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16;
                previewMesh.name = (string.IsNullOrEmpty(assetName) ? "VatAsset" : assetName) + "_FilterPreview";
                previewMesh.vertices = outVerts;
                previewMesh.normals = outNorms;
                previewMesh.uv = outUv;
                previewMesh.triangles = outTris;
                previewMesh.colors = outColors;
                previewMesh.RecalculateBounds();

                previewGo = new GameObject(previewMesh.name);
                MeshFilter mf = previewGo.AddComponent<MeshFilter>();
                mf.sharedMesh = previewMesh;
                MeshRenderer mr = previewGo.AddComponent<MeshRenderer>();
                Material previewMat = defaultSettings != null ? defaultSettings.defaultPreviewMaterial : null;
                if (previewMat == null)
                {
                    previewMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/SmartVAT/Materials/M_SmartVATFilterPreview.mat");
                    if (previewMat != null && defaultSettings != null)
                    {
                        defaultSettings.defaultPreviewMaterial = previewMat;
                        EditorUtility.SetDirty(defaultSettings);
                    }
                }
                if (previewMat != null)
                {
                    mr.sharedMaterial = previewMat;
                }

                Selection.activeGameObject = previewGo;
                EditorGUIUtility.PingObject(previewGo);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (cancelRequested && previewGo != null)
                {
                    UnityEngine.Object.DestroyImmediate(previewGo);
                }
            }
        }


        /*
        public void TestPlayableDirector()
        {
            playableDirector.time = (double)0.5f;
            playableDirector.playableGraph.GetRootPlayable(0).SetSpeed(0);
            playableDirector.DeferredEvaluate();

        }*/

        public smartVATdata OptimizeVAT(List<Vector3> baseMeshPos, List<Vector3> baseMeshNorm) //This function creates the VAT textures and returns the UV2 for the shader
        {
            smartVATdata result; //data to return at the end of the function

            //Create ordered list of all positions and all normals through all frames.
            List<List<Vector3>> allAnimatedPositions = new List<List<Vector3>>();
            List<List<Vector3>> allAnimatedNormals = new List<List<Vector3>>();
            for (int i = 0; i < frameCount; i++) //for each frame
            {
                allAnimatedPositions.Add(new List<Vector3>()); //each frame is a separate list
                allAnimatedNormals.Add(new List<Vector3>());

                for (int j = 0; j < animatedMeshes.Length; j++) //cycle through each mesh but add vertex positions from different meshes to the same frame list
                {

                    Mesh frameMesh = bakedFrames[j][i];
                    allAnimatedPositions[i].AddRange(frameMesh.vertices);
                    allAnimatedNormals[i].AddRange(frameMesh.normals);
                }
            }

            //Compare each frame's position to baked position to see if Moving
            bool[] isMoving = new bool[allAnimatedPositions[0].Count];
            for (int i = 0; i < isMoving.Length; i++)
            {
                isMoving[i] = false;
                Vector3 pos0 = baseMeshPos[i];
                Vector3 pos1;
                for (int j = 0; j < allAnimatedPositions.Count; j++)
                {
                    pos1 = allAnimatedPositions[j][i];
                    Vector3 diff = new Vector3(Mathf.Abs(pos1.x - pos0.x), Mathf.Abs(pos1.y - pos0.y), Mathf.Abs(pos1.z - pos0.z));
                    if (diff.x > movementFilter || diff.y > movementFilter || diff.z > movementFilter)
                    {
                        isMoving[i] = true;
                        break;
                    }
                }
            }

            Debug.Log("AllAnimatedNormals Count = " + allAnimatedNormals[0].Count);
            //Compare each frame's normal to initial normal to see if Rotating
            int normalIsAnimatedCount = 0;
            bool[] normalIsAnimated = new bool[allAnimatedNormals[0].Count];
            for (int i = 0; i < normalIsAnimated.Length; i++)
            {
                normalIsAnimated[i] = false;
                Vector3 norm0 = Vector3.Normalize(baseMeshNorm[i]);
                Vector3 norm1;
                for (int j = 0; j < allAnimatedNormals.Count; j++)
                {
                    norm1 = Vector3.Normalize(allAnimatedNormals[j][i]);
                    float diffangle = Mathf.Abs(Vector3.Angle(norm0, norm1));

                    if (diffangle >= normalFilter)
                    {
                        normalIsAnimated[i] = true;
                        normalIsAnimatedCount += 1;
                        break;
                    }
                }
            }
            Debug.Log("IsRotatingCount = " + normalIsAnimatedCount);

            //For each vertex, find the set of other vertices it matches in EVERY frame.
            //Single-pass incremental intersection over frames, backed by HashSet<int> for O(1) membership.
            //Replaces the prior three-pass approach (per-frame matches -> union -> prune) which was O(F*V^2)
            //with O(N) Contains/Remove and large List<List<List<int>>> peak memory.
            int vertCount = allAnimatedPositions[0].Count;
            int matchFrameCount = allAnimatedPositions.Count;

            //Pre-normalize animated normals once, so the match loop can use a plain dot product
            //against a precomputed cosine threshold instead of Vector3.Angle (which does Acos + normalize per call).
            for (int i = 0; i < matchFrameCount; i++)
            {
                List<Vector3> fn = allAnimatedNormals[i];
                for (int j = 0; j < fn.Count; j++) fn[j] = Vector3.Normalize(fn[j]);
            }
            float normalMatchCos = Mathf.Cos(normalMatchFilter * Mathf.Deg2Rad);

            HashSet<int>[] posMatchSets = new HashSet<int>[vertCount];
            HashSet<int>[] normMatchSets = new HashSet<int>[vertCount];
            for (int j = 0; j < vertCount; j++) posMatchSets[j] = new HashSet<int> { j };
            for (int j = 0; j < vertCount; j++) normMatchSets[j] = new HashSet<int> { j };

            List<int> removeBuf = new List<int>(64);

            for (int i = 0; i < matchFrameCount; i++)
            {
                if (ReportProgress($"Matching frame {i + 1}/{matchFrameCount}",
                    0.55f + 0.28f * (float)i / matchFrameCount)) return new smartVATdata();
                List<Vector3> posF = allAnimatedPositions[i];
                List<Vector3> normF = allAnimatedNormals[i];

                for (int j = 0; j < vertCount; j++)
                {
                    if ((j & 1023) == 0 && ReportProgress(
                        $"Matching frame {i + 1}/{matchFrameCount} vert {j}/{vertCount}",
                        0.55f + 0.28f * ((float)i + (float)j / vertCount) / matchFrameCount)) return new smartVATdata();

                    {
                        Vector3 pos0 = posF[j];
                        HashSet<int> set = posMatchSets[j];
                        if (i == 0)
                        {
                            //First frame: build initial candidate set with a full scan
                            for (int k = 0; k < vertCount; k++)
                            {
                                if (k == j) continue;
                                Vector3 pos1 = posF[k];
                                if (Mathf.Abs(pos0.x - pos1.x) <= movementMatchFilter
                                    && Mathf.Abs(pos0.y - pos1.y) <= movementMatchFilter
                                    && Mathf.Abs(pos0.z - pos1.z) <= movementMatchFilter)
                                {
                                    set.Add(k);
                                }
                            }
                        }
                        else if (set.Count > 1)
                        {
                            //Subsequent frames: drop any candidate that fails this frame's test
                            removeBuf.Clear();
                            foreach (int k in set)
                            {
                                if (k == j) continue;
                                Vector3 pos1 = posF[k];
                                if (!(Mathf.Abs(pos0.x - pos1.x) <= movementMatchFilter
                                    && Mathf.Abs(pos0.y - pos1.y) <= movementMatchFilter
                                    && Mathf.Abs(pos0.z - pos1.z) <= movementMatchFilter))
                                {
                                    removeBuf.Add(k);
                                }
                            }
                            for (int r = 0; r < removeBuf.Count; r++) set.Remove(removeBuf[r]);
                        }
                    }

                    {
                        Vector3 norm0 = normF[j];
                        HashSet<int> set = normMatchSets[j];
                        if (i == 0)
                        {
                            for (int k = 0; k < vertCount; k++)
                            {
                                if (k == j) continue;
                                Vector3 norm1 = normF[k];
                                if (norm0.x * norm1.x + norm0.y * norm1.y + norm0.z * norm1.z >= normalMatchCos)
                                {
                                    set.Add(k);
                                }
                            }
                        }
                        else if (set.Count > 1)
                        {
                            removeBuf.Clear();
                            foreach (int k in set)
                            {
                                if (k == j) continue;
                                Vector3 norm1 = normF[k];
                                if (norm0.x * norm1.x + norm0.y * norm1.y + norm0.z * norm1.z < normalMatchCos)
                                {
                                    removeBuf.Add(k);
                                }
                            }
                            for (int r = 0; r < removeBuf.Count; r++) set.Remove(removeBuf[r]);
                        }
                    }
                }
            }

            //Materialize sorted lists. Downstream reads [0] expecting the smallest matching vertex index.
            List<List<int>> matchingVerticesAllFrames = new List<List<int>>(vertCount);
            List<List<int>> matchingNormalsAllFrames = new List<List<int>>(vertCount);
            for (int j = 0; j < vertCount; j++)
            {
                List<int> posList = new List<int>(posMatchSets[j]);
                posList.Sort();
                matchingVerticesAllFrames.Add(posList);
                List<int> normList = new List<int>(normMatchSets[j]);
                normList.Sort();
                matchingNormalsAllFrames.Add(normList);
            }

            //Per-frame fields are no longer populated; downstream code only reads the AllFrames lists.
            matchingVertices = new List<List<List<int>>>(0);
            matchingNormals = new List<List<List<int>>>(0);

            //return smallest int in all matches
            //Also assign texID and find highest number of different matches
            //exclude texID if nonMoving
            List<int> posCorrespondanceID = new List<int>();
            List<int> posTexID = new List<int>();
            vertexMaxID = 0;

            for (int i = 0; i < matchingVerticesAllFrames.Count; i++)
            {
                posCorrespondanceID.Add(matchingVerticesAllFrames[i][0]);
                if (isMoving[i])
                {
                    if (matchingVerticesAllFrames[i][0] == i) //if smallest correspondance is its own vertex number, it's a new position
                    {
                        posTexID.Add(vertexMaxID);
                        vertexMaxID += 1;
                    }
                    else
                    {
                        posTexID.Add(posTexID[matchingVerticesAllFrames[i][0]]); //if smallest correspondance is another number, find that vertex's texID and assign it to this vertex too
                    }
                }
                else
                {
                    posTexID.Add(-1);
                }
            }

            List<int> normCorrespondanceID = new List<int>();
            List<int> normTexID = new List<int>();
            normalMaxID = 0;

            for (int i = 0; i < matchingNormalsAllFrames.Count; i++)
            {
                normCorrespondanceID.Add(matchingNormalsAllFrames[i][0]);

                if (normalIsAnimated[i])
                {
                    if (matchingNormalsAllFrames[i][0] == i) //if smallest correspondance is its own vertex number, it's a new Normal
                    {
                        normTexID.Add(normalMaxID);
                        normalMaxID += 1;
                    }
                    else
                    {
                        normTexID.Add(normTexID[matchingNormalsAllFrames[i][0]]); //if smallest correspondance is another number, find that vertex's texID and assign it to this vertex too
                    }
                }
                else
                {
                    normTexID.Add(-1);
                }
            }



            //create uv2 for pos
            Vector2[] uvPos = new Vector2[posTexID.Count + 6];
            Vector2[] uvNorm = new Vector2[posTexID.Count + 6];
            for (int i = 0; i < uvPos.Length - 6; i++)
            {
                uvPos[i] = Vector2.zero;
                uvNorm[i] = Vector2.zero;
                if (posTexID[i] != -1)
                {
                    uvPos[i].x = (float)posTexID[i] / (float)vertexMaxID + 0.5f / (float)vertexMaxID;
                }
                else
                {
                    uvPos[i].x = -1f;
                }

                if (normTexID[i] != -1)
                {
                    uvPos[i].y = (float)normTexID[i] / (float)normalMaxID + 0.5f / (float)normalMaxID;
                }
                else
                {
                    uvPos[i].y = -2f;
                }

            }

            for (int i = uvPos.Length - 6; i < uvPos.Length; i++)
            {
                uvPos[i] = Vector2.left; //(-1,0)
            }

            for (int i = uvPos.Length - 6; i < uvPos.Length; i++)
            {
                uvNorm[i] = Vector2.left; //(-1,0)
            }


            //create list 

            List<Color> posColor = new List<Color>();
            for (int i = 0; i < allAnimatedPositions.Count; i++)
            {
                for (int j = 0; j < posCorrespondanceID.Count; j++)
                {
                    if (j == posCorrespondanceID[j] && isMoving[j])
                    {
                        Vector3 basePos = combinedBasePositions[j];
                        Vector3 posCompare = allAnimatedPositions[i][j];

                        Vector3 diff = posCompare - basePos;
                        posColor.Add(new Color(diff.x, diff.y, diff.z, 0f));
                    }
                }
            }

            List<Color> normColor = new List<Color>();
            //int staticBakeOffset = 0;
            int numColorAdded = 0;
            for (int i = 0; i < allAnimatedNormals.Count; i++) //for every frame
            {
                for (int j = 0; j < normCorrespondanceID.Count; j++) //for every vertex
                {

                    if (j == normCorrespondanceID[j] && normalIsAnimated[j])    //if normal is animated and correspondance = self, add color. If correspondance!= self, then that color value has already been added
                    {

                        Vector3 normCompare = allAnimatedNormals[i][j];

                        normColor.Add(new Color(normCompare.x * 0.5f + 0.5f, normCompare.y * 0.5f + 0.5f, normCompare.z * 0.5f + 0.5f, 0f));
                        numColorAdded += 1;
                        //Debug.Log("Added reg Color");
                    }
                }
            }

            Debug.Log("Position Color Count = " + posColor.Count);
            Debug.Log("Normal Color Count = " + normColor.Count);
            Debug.Log("xRes = " + normalMaxID);
            Debug.Log("vRes = " + frameCount);


            if (ReportProgress("Encoding VAT textures", 0.9f)) return new smartVATdata();
            optimizedPositionTex = new Texture2D(vertexMaxID, frameCount, TextureFormat.RGBAHalf, -1, true);
            optimizedNormalTex = new Texture2D(normalMaxID, frameCount, TextureFormat.RGB24, -1, true);



            optimizedPositionTex.SetPixels(posColor.ToArray(), 0);
            optimizedPositionTex.Apply();

            byte[] posBytes = optimizedPositionTex.EncodeToEXR(Texture2D.EXRFlags.CompressZIP);

            System.IO.Directory.CreateDirectory(exportPath);
            posTexPath = exportPath + texPrefix + assetName + posSuffix + ".exr";

            File.WriteAllBytes(posTexPath, posBytes);
            SetTextureSettings();

            optimizedNormalTex.SetPixels(normColor.ToArray(), 0);
            optimizedNormalTex.Apply();

            byte[] normBytes = optimizedNormalTex.EncodeToPNG();

            System.IO.Directory.CreateDirectory(exportPath);
            normTexPath = exportPath + texPrefix + assetName + normSuffix + ".png";

            File.WriteAllBytes(normTexPath, normBytes);
            SetTextureSettings();

            AssetDatabase.ImportAsset(posTexPath, ImportAssetOptions.ForceUpdate);


            //Set Texture Import Settings
            TextureImporter posimporter = TextureImporter.GetAtPath(posTexPath) as TextureImporter;
            TextureImporterPlatformSettings[] posTexSettings;
            int texFormatI;
            if (defaultSettings.textureFormatOverrides == null)
            {
                posTexSettings = new TextureImporterPlatformSettings[1];
                texFormatI = 1;
            }
            else
            {
                texFormatI = defaultSettings.textureFormatOverrides.Length + 1;
                posTexSettings = new TextureImporterPlatformSettings[texFormatI];
   
            }
            //TextureImporterPlatformSettings[] posTexSettings = new TextureImporterPlatformSettings[defaultSettings.textureFormatOverrides.Length + 1];
            for (int i = 0; i < texFormatI; i++)
            {
                if (i == 0)
                {
                    posTexSettings[i] = posimporter.GetDefaultPlatformTextureSettings();
                    posTexSettings[i].format = defaultSettings.defaultTextureFormat.positionTextureFormat;
                }
                else
                {
                    posTexSettings[i] = posimporter.GetPlatformTextureSettings(defaultSettings.textureFormatOverrides[i - 1].platform);
                    posTexSettings[i].overridden = true;
                    posTexSettings[i].format = defaultSettings.textureFormatOverrides[i - 1].positionTextureFormat;
                }
                posTexSettings[i].maxTextureSize = 16384;
                posimporter.SetPlatformTextureSettings(posTexSettings[i]);
            }




            //posimporter.SetPlatformTextureSettings(defaultPosTexSet);
            posimporter.maxTextureSize = 16384;
            posimporter.npotScale = TextureImporterNPOTScale.None;
            posimporter.mipmapEnabled = false;
            AssetDatabase.ImportAsset(posTexPath, ImportAssetOptions.ForceUpdate);


            AssetDatabase.ImportAsset(normTexPath, ImportAssetOptions.ForceUpdate);
            TextureImporter normimporter = TextureImporter.GetAtPath(normTexPath) as TextureImporter;
            TextureImporterPlatformSettings[] normTexSettings = new TextureImporterPlatformSettings[texFormatI];
            for (int i = 0; i < texFormatI; i++)
            {
                if (i == 0)
                {
                    normTexSettings[i] = normimporter.GetDefaultPlatformTextureSettings();
                    normTexSettings[i].format = defaultSettings.defaultTextureFormat.normalTextureFormat;

                }
                else
                {
                    normTexSettings[i] = normimporter.GetPlatformTextureSettings(defaultSettings.textureFormatOverrides[i - 1].platform);
                    normTexSettings[i].overridden = true;
                    normTexSettings[i].format = defaultSettings.textureFormatOverrides[i - 1].normalTextureFormat;
                }
                normTexSettings[i].maxTextureSize = 16384;
                normimporter.SetPlatformTextureSettings(normTexSettings[i]);
            }

            normimporter.maxTextureSize = 16384;
            normimporter.sRGBTexture = false;
            normimporter.npotScale = TextureImporterNPOTScale.None;
            normimporter.mipmapEnabled = false;


            AssetDatabase.WriteImportSettingsIfDirty(normTexPath);
            AssetDatabase.ImportAsset(normTexPath, ImportAssetOptions.ForceUpdate);



            result.uv2 = uvPos;
            result.uv3 = uvNorm;
            result.bounds = CreateBoundsTriangles(allAnimatedPositions);
            return result;


        }

        public Vector3[] TransformSpace(Vector3[] arrayToTransform, UnityEngine.Transform transform)
        {
            for (int i = 0; i < arrayToTransform.Length; i++)
            {
                arrayToTransform[i] = transform.TransformPoint(arrayToTransform[i]);
            }
            return arrayToTransform;
        }
        public Vector3[] TransformNormal(Vector3[] arrayToTransform, UnityEngine.Transform transform)
        {
            for (int i = 0; i < arrayToTransform.Length; i++)
            {
                arrayToTransform[i] = transform.TransformDirection(arrayToTransform[i]);
            }
            return arrayToTransform;
        }
        public void SetTextureSettings()
        {
            string filename = "Assets/SaveTest/textureTest.exr";
            System.Type assetType = AssetDatabase.GetMainAssetTypeAtPath(filename);
            if (assetType != null)
            {
                TextureImporter importer = TextureImporter.GetAtPath(filename) as TextureImporter;

                TextureImporterPlatformSettings texset = importer.GetDefaultPlatformTextureSettings();
                texset.format = TextureImporterFormat.RGBAHalf;

                importer.mipmapEnabled = false;
                importer.SetPlatformTextureSettings(texset);

                AssetDatabase.ImportAsset(filename, ImportAssetOptions.ForceUpdate);
            }
        }
        private string SetExportPath()
        {
            string navigatedFolder = EditorUtility.OpenFolderPanel("Work Folder Location", exportPath, "") + "/";
            if (navigatedFolder != "/")
            {
                return navigatedFolder;
            }
            else
            {
                return exportPath;
            }
        }

        private Mesh[] CreateBaseMeshes(Renderer[] meshArray)
        {
            List<Mesh> meshes = new List<Mesh>();
            for (int i = 0; i < animatedMeshes.Length; i++)
            {
                playableDirector.time = (double)(0);
                playableDirector.Evaluate();

                meshes.Add(new Mesh());
                UnityEngine.Transform t = objectTransform[i];
                if (rendererType[i] == 0)
                {
                    SkinnedMeshRenderer smr = meshArray[i].GetComponent<SkinnedMeshRenderer>();
                    Mesh sourceMesh = smr.sharedMesh;
                    smr.BakeMesh(meshes[i]);
                    meshes[i] = BakeStaticMesh(meshes[i], t);
                    meshes[i].colors = sourceMesh.colors;
                }
                else
                {
                    Mesh sourceMesh = meshArray[i].GetComponent<MeshFilter>().sharedMesh;
                    meshes[i] = BakeStaticMesh(sourceMesh, t);
                    meshes[i].colors = sourceMesh.colors;
                }
            }


            //remove later
            /*
            for (int i = 0; i < meshes.Count; i++)
            {
                GameObject go = new GameObject("Basemesh" + i, typeof(MeshFilter), typeof(MeshRenderer));
                MeshFilter goMF = go.GetComponent<MeshFilter>();
                goMF.mesh = meshes[i];
            }*/

            return meshes.ToArray();
        }

        private void InitializeWindow()
        {
            vatConverter = this;

            settingsPath = "Assets/SmartVAT/Settings/";
            settingsFullPath = "Assets/SmartVAT/Settings/SmartVATDefaultSettings.asset";
            if (!File.Exists(settingsFullPath))
            {
                CreateSettingsFiles();
            }
            //RefreshPresets();
            defaultSettings = (SmartVATSettings)AssetDatabase.LoadAssetAtPath(settingsFullPath, typeof(SmartVATSettings));

            //Only seed assetName and exportPath from defaultSettings on the first init of this
            //window instance. After that, the user's choices persist across domain reloads
            //(entering play mode, recompiling, asset imports) and only reset when the window
            //is actually closed.
            if (!assetNameInitialized)
            {
                assetName = defaultSettings.baseName;
                assetNameInitialized = true;
            }
            if (!exportPathInitialized)
            {
                exportPath = defaultSettings.saveLocation;
                exportPathInitialized = true;
            }

            texPrefix = defaultSettings.texturePrefix;
            meshPrefix = defaultSettings.meshPrefix;
            posSuffix = defaultSettings.positionSuffix;
            normSuffix = defaultSettings.normalSuffix;
            matPrefix = defaultSettings.materialPrefix;

            if (defaultSettings.smartVATPresets == null)
            {
                settingPresets = new SmartVATPresets[1];
            }
            else
            {
                settingPresets = new SmartVATPresets[defaultSettings.smartVATPresets.Length + 1];
            }

            settingPresets[0] = defaultSettings.defaultVATPreset;
            for (int i = 0; i < settingPresets.Length - 1; i++)
            {
                settingPresets[i + 1] = defaultSettings.smartVATPresets[i];
            }

            presetNames = new string[settingPresets.Length];
            for (int i = 0; i < presetNames.Length; i++)
            {
                presetNames[i] = settingPresets[i].presetName;
            }

            so = new SerializedObject(vatConverter);
            r_presetNum = so.FindProperty("presetNum");
            r_materialMergeMode = so.FindProperty("materialMergeMode");
            animatedObjects = AnimatedObjectSingleton.instance.animatedObjects;
            r_animatedObjects = so.FindProperty("animatedObjects");
            playableDirector = AnimatedObjectSingleton.instance.timeline;
            r_timeline = so.FindProperty("playableDirector");
            r_meshBakePose = so.FindProperty("meshBakePose");
            r_normalTextureMode = so.FindProperty("normalTextureMode");
            r_meshNormalBake = so.FindProperty("meshNormalBake");
            r_tangentBake = so.FindProperty("tangentBake");
            r_noPosMoveTolerance = so.FindProperty("movementFilter");
            r_noNormMoveTolerance = so.FindProperty("normalFilter");
            r_samePosTolerance = so.FindProperty("movementMatchFilter");
            r_sameNormTolerance = so.FindProperty("normalMatchFilter");

            r_frameCount = so.FindProperty("frameCount");
            r_bakeVertexColor = so.FindProperty("bakeVertexColor");
            r_assetName = so.FindProperty("assetName");
            r_exportPath = so.FindProperty("exportPath");
            r_exportType = so.FindProperty("exportType");
            // Seed filter / preset-driven values from preset 0 only on the first init of
            // this window instance — same pattern as assetName/exportPath above. After
            // that, user-edited values survive domain reloads; the preset dropdown still
            // overwrites them when the user explicitly switches presets.
            if (!presetSeeded)
            {
                LoadPreset(0);
                presetSeeded = true;
            }
            windowInitialized = true;
        }
        private void LoadPreset(int presetNum)
        {
            r_noPosMoveTolerance.floatValue = settingPresets[presetNum].movementFilter;
            r_noNormMoveTolerance.floatValue = settingPresets[presetNum].normalFilter;
            r_samePosTolerance.floatValue = settingPresets[presetNum].movementMatchFilter;
            r_sameNormTolerance.floatValue = settingPresets[presetNum].normalMatchFilter;
            r_bakeVertexColor.boolValue = settingPresets[presetNum].bakeVertexColor;
            r_frameCount.intValue = settingPresets[presetNum].frameCount;
            r_meshBakePose.intValue = 1;
            r_tangentBake.intValue = 0;
            r_meshNormalBake.intValue = 0;
            r_normalTextureMode.intValue = 0;
            r_exportType.intValue = 0;
            r_materialMergeMode.intValue = 0;
            so.ApplyModifiedProperties();
        }
        private Mesh BakeStaticMesh(Mesh mesh, UnityEngine.Transform transform)
        {
            Mesh result = new Mesh();
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector4[] tangents = mesh.tangents;
            int[] triangles = mesh.triangles;
            Vector2[] uv = mesh.uv;

            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = transform.localToWorldMatrix.MultiplyPoint3x4(vertices[i]);
            }

            for (int i = 0; i < mesh.vertexCount; i++)
            {
                normals[i] = transform.TransformDirection(normals[i]);
            }

            for (int i = 0; i < mesh.vertexCount; i++)
            {
                tangents[i] = transform.TransformDirection(tangents[i]);
            }

            result.vertices = vertices;
            result.normals = normals;
            result.tangents = tangents;
            result.triangles = triangles;
            result.uv = uv;

            return result;
        }
        private Vector3[] CreateBoundsTriangles(List<List<Vector3>> allPositions)
        {
            float minX = 1000000;
            float maxX = -1000000;
            float minY = 1000000;
            float maxY = -1000000;
            float minZ = 1000000;
            float maxZ = -1000000;

            for (int i = 0; i < allPositions.Count; i++)
            {
                for (int j = 0; j < allPositions[i].Count; j++)
                {
                    minX = Mathf.Min(minX, allPositions[i][j].x);
                    maxX = Mathf.Max(maxX, allPositions[i][j].x);
                    minY = Mathf.Min(minY, allPositions[i][j].y);
                    maxY = Mathf.Max(maxY, allPositions[i][j].y);
                    minZ = Mathf.Min(minZ, allPositions[i][j].z);
                    maxZ = Mathf.Max(maxZ, allPositions[i][j].z);
                }
            }
            Vector3[] result = new Vector3[2];
            result[0] = new Vector3(minX, minY, minZ);
            result[1] = new Vector3(maxX, maxY, maxZ);

            return result;
        }
        private bool CheckForNull(Renderer[] objects)
        {
            if (objects == null)
            {
                return false;
            }
            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] == null)
                {
                    return false;
                }
            }
            return true;
        }

        private void RefreshAnimatedMeshesFromObjects()
        {
            if (animatedObjects == null)
            {
                animatedMeshes = null;
                return;
            }
            AnimatedObject[] sorted = animatedObjects.OrderBy(x => x == null ? int.MaxValue : x.index).ToArray();
            animatedMeshes = new Renderer[sorted.Length];
            for (int i = 0; i < sorted.Length; i++)
            {
                animatedMeshes[i] = sorted[i] != null ? sorted[i].rendrer : null;
            }
        }

        private bool CheckSettings()
        {
            bool result;
            if (playableDirector != null & animatedMeshes != null & frameCount > 0)
            {
                if (CheckForNull(animatedMeshes))
                {
                    result = true;
                }
                else
                {
                    result = false;
                }

            }
            else
            {
                result = false;
            }

            return result;
        }
        private void CheckTimeline(System.Action onValid)
        {
            if ((float)playableDirector.duration <= 0)
            {
                TimelineDurationError();
                return;
            }

            List<Transform> timelineObjects = new List<Transform>();
            var outputs = playableDirector.playableAsset.outputs;
            foreach (var output in outputs)
            {
                UnityEngine.Object bindObject = playableDirector.GetGenericBinding(output.sourceObject);

                if (bindObject != null)
                {
                    Transform go;
                    if (bindObject is GameObject boundGo)
                        go = boundGo.transform;
                    else if (bindObject is UnityEngine.Component boundComp)
                        go = boundComp.transform;
                    else
                    {
                        NullInTimelineDialog();
                        return;
                    }

                    timelineObjects.Add(go);
                }
                else
                {
                    NullInTimelineDialog();
                    return;
                }

                //Debug.Log(go.name);
            }

            bool isActive = true;
            bool isInTimeline = true;

            for (int i = 0; i < animatedMeshes.Length; i++)
            {
                if (!animatedMeshes[i].gameObject.activeInHierarchy)
                {
                    isActive = false;
                    break;
                }
            }
            if (!isActive)
            {
                NotActiveDialog();
                return;
            }

            for (int i = 0; i < animatedMeshes.Length; i++)
            {
                for (int j = 0; j < timelineObjects.Count;)
                {
                    if (animatedMeshes[i].transform.IsChildOf(timelineObjects[j].transform))
                    {
                        isInTimeline = true;
                        break;
                    }
                    else
                    {
                        isInTimeline = false;
                        j++;
                    }
                }

            }
            if (!isInTimeline)
            {
                NotInTimelineDialog();
            }
            else
            {
                onValid?.Invoke();
            }
        }
        private void CreateFinalAsset()
        {
            Shader matShader = Shader.Find("SmartVATFree/ShaderGraph/Shader_SmartVAT_Lit_01");
            Material vatMaterial = new Material(matShader);
            vatMaterial.SetTexture("_VATPosition", (Texture2D)AssetDatabase.LoadAssetAtPath(posTexPath, typeof(Texture2D)));
            vatMaterial.SetTexture("_VATNormal", (Texture2D)AssetDatabase.LoadAssetAtPath(normTexPath, typeof(Texture2D)));

            vatMaterial.DisableKeyword("_ANIMATION_MODE_MANUAL");
            vatMaterial.DisableKeyword("_ANIMATION_MODE_PARTICLE");
            vatMaterial.EnableKeyword("_ANIMATION_MODE_AUTO");
            vatMaterial.SetFloat("_ANIMATION_MODE", 0f);
            vatMaterial.SetFloat("_ParticleVertexStream", 0);

            materialPath = exportPath + matPrefix + assetName + ".mat";

            AssetDatabase.CreateAsset(vatMaterial, materialPath);

            ExportToMeshRenderer();
        }


        private void ExportToMeshRenderer()
        {
            GameObject go = new GameObject();
            go.name = assetName;
            MeshRenderer goMR = go.AddComponent<MeshRenderer>();
            goMR.material = (Material)AssetDatabase.LoadAssetAtPath(materialPath, typeof(Material));

            MeshFilter goMF = go.AddComponent<MeshFilter>();
            goMF.sharedMesh = (Mesh)AssetDatabase.LoadAssetAtPath(meshPath, typeof(Mesh));
            PrefabUtility.SaveAsPrefabAssetAndConnect(go, exportPath + assetName + ".prefab", InteractionMode.AutomatedAction);

            GameObject[] newSelection = new GameObject[1];
            newSelection[0] = go;
            Selection.objects = newSelection;
        }
        private void FindAlembicPlayer(GameObject go)
        {
            AlembicStreamPlayer[] parentAlPlayer = go.GetComponentsInParent<AlembicStreamPlayer>();

            if (parentAlPlayer.Length > 0 & parentAlPlayer[0].isActiveAndEnabled)
            {
                alembicPlayer = parentAlPlayer[0];
            }
            else
            {
                AlembicError();
                alembicPlayer = null;
                return;
            }
        }
        private void CreateSettingsFiles()
        {
            SmartVATNoSettings();
            defaultSettings = new SmartVATSettings();
            defaultSettings.defaultVATPreset.presetName = "Default";
            defaultSettings.defaultVATPreset.movementFilter = 0f;
            defaultSettings.defaultVATPreset.normalFilter = 1f;
            defaultSettings.defaultVATPreset.movementMatchFilter = 0f;
            defaultSettings.defaultVATPreset.normalMatchFilter = 1f;
            defaultSettings.defaultVATPreset.bakeVertexColor = true;
            defaultSettings.defaultVATPreset.frameCount = 32;
            defaultSettings.defaultVATPreset.standardShader = Shader.Find("SmartVATFree/ShaderGraph/Shader_SmartVAT_Lit_01");
            defaultSettings.defaultVATPreset.bakePos = SmartVATPresets.MeshBakePose.FirstFrame;
            defaultSettings.defaultVATPreset.normalTextureMode = SmartVATPresets.NormalTextureOptimize.ExcludeNonMoving;
            defaultSettings.defaultVATPreset.normalBake = SmartVATPresets.DirectionBake.orig;
            defaultSettings.defaultVATPreset.tangentBake = SmartVATPresets.DirectionBake.orig;
            defaultSettings.defaultVATPreset.exportType = SmartVATPresets.ExportType.MeshRenderer;

            defaultSettings.smartVATPresets = new SmartVATPresets[0];

            defaultSettings.defaultPreviewMaterial = AssetDatabase.LoadAssetAtPath<Material>(
                "Assets/SmartVAT/Materials/M_SmartVATFilterPreview.mat");

            Directory.CreateDirectory(settingsPath);
            Debug.Log(settingsFullPath);
            AssetDatabase.CreateAsset(defaultSettings, settingsFullPath);
            AssetDatabase.ImportAsset(settingsFullPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            RefreshPresets();
            EditorGUIUtility.PingObject(defaultSettings);

        }
        private void RefreshPresets()
        {
            if (defaultSettings.smartVATPresets == null)
            {
                settingPresets = new SmartVATPresets[1];
            }
            else
            {
                settingPresets = new SmartVATPresets[defaultSettings.smartVATPresets.Length + 1];
            }

            settingPresets[0] = defaultSettings.defaultVATPreset;
            for (int i = 0; i < settingPresets.Length - 1; i++)
            {
                settingPresets[i + 1] = defaultSettings.smartVATPresets[i];
            }

            presetNames = new string[settingPresets.Length];
            for (int i = 0; i < presetNames.Length; i++)
            {
                presetNames[i] = settingPresets[i].presetName;
            }
            defaultSettings = (SmartVATSettings)AssetDatabase.LoadAssetAtPath(settingsFullPath, typeof(SmartVATSettings));
        }
        private bool ReportProgress(string info, float progress)
        {
            if (cancelRequested)
            {
                return true;
            }
            if (EditorUtility.DisplayCancelableProgressBar("Smart VAT Free Export", info, Mathf.Clamp01(progress)))
            {
                cancelRequested = true;
                return true;
            }
            return false;
        }

        private void HandleAnimatedObjectDrop(Rect rect)
        {
            Event evt = Event.current;
            if (!rect.Contains(evt.mousePosition)) return;
            if (evt.type != EventType.DragUpdated && evt.type != EventType.DragPerform) return;

            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

            if (evt.type != EventType.DragPerform) return;

            DragAndDrop.AcceptDrag();

            List<Renderer> picked = new List<Renderer>();
            foreach (Object dragged in DragAndDrop.objectReferences)
            {
                if (dragged is Renderer r)
                {
                    picked.Add(r);
                }
                else if (dragged is GameObject go)
                {
                    Renderer found = go.GetComponent<Renderer>();
                    if (found == null) found = go.GetComponentInChildren<Renderer>();
                    if (found != null) picked.Add(found);
                }
            }

            if (picked.Count > 0)
            {
                List<AnimatedObject> list = animatedObjects != null
                    ? new List<AnimatedObject>(animatedObjects)
                    : new List<AnimatedObject>();
                int startIndex = list.Count;
                for (int i = 0; i < picked.Count; i++)
                {
                    list.Add(new AnimatedObject { rendrer = picked[i], index = startIndex + i });
                }
                animatedObjects = list.ToArray();
                if (so != null) so.Update();
                GUI.changed = true;
            }

            evt.Use();
        }

        private void DrawFilterRow(GUIContent label, SerializedProperty valueProp)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth));
            EditorGUILayout.Toggle(true, GUILayout.Width(20));
            EditorGUILayout.PropertyField(valueProp, GUIContent.none);
            EditorGUILayout.EndHorizontal();
        }
        private void CreateTimeline()
        {
            GameObject go = new GameObject("Timeline", typeof(PlayableDirector));
            PlayableDirector newTimeline = go.GetComponent<PlayableDirector>();
            //TimelineAsset timeline = ScriptableObject.Instantiate(new TimelineAsset());
            TimelineAsset newTimelineAsset;
            string newTimelinePath = EditorUtility.SaveFilePanel("Save Timeline To...", "Assets/SmartVAT", "Timeline", "playable");
            if (newTimelinePath.Length != 0)
            {
                Debug.Log(newTimelinePath);
                //ScriptableObject.CreateInstance<TimelineAsset>();
                if (newTimelinePath.StartsWith(Application.dataPath))
                {
                    newTimelinePath = "Assets" + newTimelinePath.Substring(Application.dataPath.Length);
                    Debug.Log(newTimelinePath);
                }

                AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<TimelineAsset>(), newTimelinePath);

                newTimelineAsset = (TimelineAsset)AssetDatabase.LoadAssetAtPath(newTimelinePath, typeof(TimelineAsset));

                List<GameObject> mainObjects = new List<GameObject>();
                if (animatedObjects != null)
                {
                    foreach (AnimatedObject ao in animatedObjects)
                    {
                        if (ao == null || ao.rendrer == null) continue;
                        Animator parentAnimator = ao.rendrer.GetComponentInParent<Animator>();
                        GameObject mainGo = parentAnimator != null
                            ? parentAnimator.gameObject
                            : ao.rendrer.transform.root.gameObject;
                        if (!mainObjects.Contains(mainGo))
                        {
                            mainObjects.Add(mainGo);
                        }
                    }
                }

                foreach (GameObject mainGo in mainObjects)
                {
                    bool hasAnimator = mainGo.TryGetComponent<Animator>(out Animator animator);
                    if (!hasAnimator)
                    {
                        animator = mainGo.AddComponent<Animator>();
                    }
                    AnimationTrack animTrack = newTimelineAsset.CreateTrack<AnimationTrack>(mainGo.name);
                    newTimeline.SetGenericBinding(animTrack, animator);
                }

                newTimeline.playableAsset = newTimelineAsset;

                r_timeline.objectReferenceValue = newTimeline;
                so.ApplyModifiedProperties();

                GameObject[] newSelection = new GameObject[1];
                newSelection[0] = go;
                Selection.objects = newSelection;

            }
        }
        private void ErrorDialog()
        {
            EditorUtility.DisplayDialog("Parameter error",
            "Some parameters are not properly set.\nDouble check the following: \n\n\u2022 Playable Director is not empty. \n\u2022 At least one Animated Object has been added. \n\u2022 None of the Animated Objects are empty. \n\u2022 Frame Count is higher than 0.", "Ok");
        }
        private void VertexColorIssue()
        {
            EditorUtility.DisplayDialog("Vertex Color Not Found",
            "Vertex Color data could not be found. Vertex Color set to white.", "OK");
        }
        private void NotInTimelineDialog()
        {
            EditorUtility.DisplayDialog("Error",
            "One or more Animated Objects are not part of the Timeline.", "OK");
        }
        private void TimelineDurationError()
        {
            EditorUtility.DisplayDialog("Error",
            "Timeline Duration is 0. This can happen if there is no animation. Make sure that the Duration is set to something longer.", "OK");
        }
        private void NullInTimelineDialog()
        {
            EditorUtility.DisplayDialog("Error",
            "One or more tracks in the Timeline have no Game Objects associated.", "OK");
        }
        private void NotActiveDialog()
        {
            EditorUtility.DisplayDialog("Error",
            "One or more Animated Objects are not enabled. Enable them and try again.", "OK");
        }
        private void AlembicError()
        {
            EditorUtility.DisplayDialog("Alembic Error",
            "Alembic Stream Player Component could not be found in any parents of the Alembic Object or Alembic Stream Player Component is disabled. Fix it and try again.", "OK");
        }
        private void AlembicVertexError()
        {
            EditorUtility.DisplayDialog("Alembic Topology Error",
            "Smart VAT Free does not support varying Vertex Count. Make sure that your Alembic file has the same amount of vertices and triangles through the entire animation.", "OK");
        }
        private void MissingTangentsError(string meshName)
        {
            EditorUtility.DisplayDialog("Missing Source Tangents",
            $"Tangent Bake is set to \"Copy Source\" but the source mesh \"{meshName}\" has no tangent data.\n\nEither enable tangent import on the source mesh (Model Import Settings → Tangents → Calculate / Import) or pick an explicit Tangent Bake direction.", "OK");
        }
        private void SmartVATNoSettings()
        {
            EditorUtility.DisplayDialog("Smart VAT Free Settings not found",
            "Thanks for installing the free version of Smart VAT. Some features are only available in the full version. Settings file could not be found. Creating settings file at:\n\nAssets/SmartVAT/Settings/SmartVATDefaultSettings.asset\n\nThe full version allows you to customize the settings depending on your project's needs. You can create and change presets, change the naming convention and more.", "OK");
        }
        private void OnDestroy()
        {
            windowInitialized = false;
        }
        private void OnEnable()
        {
            InitializeWindow();
        }
        private void OnDisable()
        {
            AnimatedObjectSingleton.instance.animatedObjects = animatedObjects;
            AnimatedObjectSingleton.instance.timeline = playableDirector;
        }
    }
    
    public class AnimatedObjectSingleton : ScriptableSingleton<AnimatedObjectSingleton>
    {
        [SerializeField] public AnimatedObject[] animatedObjects;
        [SerializeField] public PlayableDirector timeline;
    }

}


