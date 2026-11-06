using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Serialization;

namespace SmartVAT
{
    [System.Serializable]
    public struct SmartVATTextureFormat
    {
        public string platform;
        public TextureImporterFormat positionTextureFormat;
        public TextureImporterFormat normalTextureFormat;
        public SmartVATTextureFormat(string plat, TextureImporterFormat postex, TextureImporterFormat normtex)
        {
            platform = plat;
            positionTextureFormat = postex;
            normalTextureFormat = normtex;
        }
    }

    [System.Serializable]
    public struct SmartVATPresets
    {
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

        public enum NormalTextureOptimize
        {
            ExcludeNonMoving = 0,
            IncludeAll = 1
        }

        [SerializeField] public string presetName;
        [Min(0f)] [FormerlySerializedAs("minPosMoveTolerance")] [SerializeField] public float movementFilter;
        [Min(0f)] [FormerlySerializedAs("minNormalMoveTolerance")] [SerializeField] public float normalFilter;
        [Min(0f)] [FormerlySerializedAs("samePositionTolerance")] [SerializeField] public float movementMatchFilter;
        [Min(0f)] [FormerlySerializedAs("sameNormalTolerance")] [SerializeField] public float normalMatchFilter;
        [SerializeField] public bool bakeVertexColor;
        [SerializeField] public int frameCount;
        [FormerlySerializedAs("shader")] [SerializeField] public Shader standardShader;
        [SerializeField] public MeshBakePose bakePos;
        [SerializeField] public NormalTextureOptimize normalTextureMode;
        [SerializeField] public DirectionBake normalBake;
        [SerializeField] public DirectionBake tangentBake;
        [SerializeField] public ExportType exportType;

    }

    [CreateAssetMenu(fileName = "SmartVATDefaultSettings", menuName = "ScriptableObjects/SmartVATFree Default Settings", order = 1)]
    public class SmartVATSettings : ScriptableObject
    {
        [SerializeField] public string baseName = "VatAsset";
        [SerializeField] public string texturePrefix = "T_";
        [SerializeField] public string materialPrefix = "M_";
        [SerializeField] public string meshPrefix = "Mesh_";
        [SerializeField] public string smartVATsuffix = "_SmartVATFree";
        [SerializeField] public string positionSuffix = "_POS";
        [SerializeField] public string normalSuffix = "_NORM";
        [SerializeField] public string saveLocation = "Assets/";
        [SerializeField] public SmartVATTextureFormat defaultTextureFormat = new SmartVATTextureFormat("Default", TextureImporterFormat.RGBAHalf, TextureImporterFormat.RGB16);
        [SerializeField] public SmartVATTextureFormat[] textureFormatOverrides;
        [SerializeField] public Material defaultPreviewMaterial;

        public SmartVATPresets defaultVATPreset;
        public SmartVATPresets[] smartVATPresets;
    }

}
