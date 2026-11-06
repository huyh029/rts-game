using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace SmartVAT
{
    [CustomPropertyDrawer(typeof(SmartVATTextureFormat))]
    public class SmartVATTextureFormatPropertyDrawer : PropertyDrawer
    {
        private struct PlatformMapping
        {
            public BuildTargetGroup group;
            public BuildTarget target;
            public string platformName;
        }

        private static readonly PlatformMapping[] PLATFORM_MAPPINGS = new[]
        {
            new PlatformMapping { group = BuildTargetGroup.Standalone, target = BuildTarget.StandaloneWindows64, platformName = "Standalone" },
            new PlatformMapping { group = BuildTargetGroup.Android,    target = BuildTarget.Android,             platformName = "Android"    },
            new PlatformMapping { group = BuildTargetGroup.iOS,        target = BuildTarget.iOS,                 platformName = "iPhone"     },
            new PlatformMapping { group = BuildTargetGroup.tvOS,       target = BuildTarget.tvOS,                platformName = "tvOS"       },
            new PlatformMapping { group = BuildTargetGroup.WebGL,      target = BuildTarget.WebGL,               platformName = "WebGL"      },
            new PlatformMapping { group = BuildTargetGroup.WSA,        target = BuildTarget.WSAPlayer,           platformName = "WindowsStoreApps" },
            new PlatformMapping { group = BuildTargetGroup.PS4,        target = BuildTarget.PS4,                 platformName = "PS4"        },
            new PlatformMapping { group = BuildTargetGroup.PS5,        target = BuildTarget.PS5,                 platformName = "PS5"        },
            new PlatformMapping { group = BuildTargetGroup.XboxOne,    target = BuildTarget.XboxOne,             platformName = "XboxOne"    },
            new PlatformMapping { group = BuildTargetGroup.Switch,     target = BuildTarget.Switch,              platformName = "Switch"     },
        };

        private static readonly string[] COMMON_FORMAT_NAMES =
        {
            "RGBA32", "ARGB32", "RGB24", "RGBA16", "RGB16",
            "R16", "R8", "RG16", "RG32", "RGB48", "RGBA64", "Alpha8",
            "RHalf", "RGHalf", "RGBAHalf",
            "RFloat", "RGFloat", "RGBAFloat",
        };

        private static readonly string[] DESKTOP_FORMAT_NAMES =
        {
            "DXT1", "DXT5", "BC4", "BC5", "BC6H", "BC7",
            "DXT1Crunched", "DXT5Crunched", "BC7Crunched",
        };

        private static readonly string[] ETC_FORMAT_NAMES =
        {
            "ETC_RGB4", "ETC2_RGB4", "ETC2_RGB4_PUNCHTHROUGH_ALPHA", "ETC2_RGBA8",
            "ETC_RGB4Crunched", "ETC2_RGBA8Crunched",
        };

        private static readonly string[] ASTC_FORMAT_NAMES =
        {
            "ASTC_4x4", "ASTC_5x5", "ASTC_6x6", "ASTC_8x8", "ASTC_10x10", "ASTC_12x12",
            "ASTC_HDR_4x4", "ASTC_HDR_5x5", "ASTC_HDR_6x6", "ASTC_HDR_8x8", "ASTC_HDR_10x10", "ASTC_HDR_12x12",
        };

        private static readonly string[] PVRTC_FORMAT_NAMES =
        {
            "PVRTC_RGB2", "PVRTC_RGB4", "PVRTC_RGBA2", "PVRTC_RGBA4",
        };

        private static readonly TextureImporterFormat[] COMMON_FORMATS = ResolveFormatNames(COMMON_FORMAT_NAMES);
        private static readonly TextureImporterFormat[] DESKTOP_FORMATS = ResolveFormatNames(DESKTOP_FORMAT_NAMES);
        private static readonly TextureImporterFormat[] ETC_FORMATS = ResolveFormatNames(ETC_FORMAT_NAMES);
        private static readonly TextureImporterFormat[] ASTC_FORMATS = ResolveFormatNames(ASTC_FORMAT_NAMES);
        private static readonly TextureImporterFormat[] PVRTC_FORMATS = ResolveFormatNames(PVRTC_FORMAT_NAMES);

        private static TextureImporterFormat[] ResolveFormatNames(string[] names)
        {
            List<TextureImporterFormat> result = new List<TextureImporterFormat>(names.Length);
            for (int i = 0; i < names.Length; i++)
            {
                if (System.Enum.TryParse(names[i], false, out TextureImporterFormat parsed)
                    && System.Enum.IsDefined(typeof(TextureImporterFormat), parsed))
                {
                    result.Add(parsed);
                }
            }
            return result.ToArray();
        }

        private static string[] cachedPlatformNames;
        private static readonly Dictionary<string, TextureImporterFormat[]> cachedFormatsByPlatform = new Dictionary<string, TextureImporterFormat[]>();
        private static readonly Dictionary<string, string[]> cachedFormatNamesByPlatform = new Dictionary<string, string[]>();

        private static string[] GetInstalledPlatformNames()
        {
            if (cachedPlatformNames != null) return cachedPlatformNames;

            List<string> names = new List<string> { "Default" };
            foreach (PlatformMapping mapping in PLATFORM_MAPPINGS)
            {
                if (BuildPipeline.IsBuildTargetSupported(mapping.group, mapping.target))
                {
                    if (!names.Contains(mapping.platformName)) names.Add(mapping.platformName);
                }
            }
            cachedPlatformNames = names.ToArray();
            return cachedPlatformNames;
        }

        private static TextureImporterFormat[] GetCompatibleFormats(string platform)
        {
            if (cachedFormatsByPlatform.TryGetValue(platform ?? "", out TextureImporterFormat[] cached)) return cached;

            IEnumerable<TextureImporterFormat> set = COMMON_FORMATS;
            switch (platform)
            {
                case "Default":
                case "Standalone":
                case "WindowsStoreApps":
                    set = set.Concat(DESKTOP_FORMATS);
                    break;
                case "Android":
                    set = set.Concat(ETC_FORMATS).Concat(ASTC_FORMATS);
                    break;
                case "iPhone":
                case "tvOS":
                    set = set.Concat(PVRTC_FORMATS).Concat(ASTC_FORMATS).Concat(ETC_FORMATS);
                    break;
                case "WebGL":
                    set = set.Concat(DESKTOP_FORMATS).Concat(ETC_FORMATS).Concat(ASTC_FORMATS);
                    break;
                case "PS4":
                case "PS5":
                case "XboxOne":
                    set = set.Concat(DESKTOP_FORMATS);
                    break;
                case "Switch":
                    set = set.Concat(DESKTOP_FORMATS).Concat(ASTC_FORMATS);
                    break;
            }

            TextureImporterFormat[] arr = set.Distinct().ToArray();
            cachedFormatsByPlatform[platform ?? ""] = arr;
            return arr;
        }

        private static string[] GetCompatibleFormatNames(string platform)
        {
            string key = platform ?? "";
            if (cachedFormatNamesByPlatform.TryGetValue(key, out string[] cached)) return cached;
            string[] names = GetCompatibleFormats(platform).Select(f => f.ToString()).ToArray();
            cachedFormatNamesByPlatform[key] = names;
            return names;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight * 4 + EditorGUIUtility.standardVerticalSpacing * 3;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            SerializedProperty platformProp = property.FindPropertyRelative("platform");
            SerializedProperty posFmtProp = property.FindPropertyRelative("positionTextureFormat");
            SerializedProperty normFmtProp = property.FindPropertyRelative("normalTextureFormat");

            float lh = EditorGUIUtility.singleLineHeight;
            float sp = EditorGUIUtility.standardVerticalSpacing;

            Rect headerRect = new Rect(position.x, position.y, position.width, lh);
            EditorGUI.LabelField(headerRect, label, EditorStyles.boldLabel);

            int oldIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel++;

            Rect row = new Rect(position.x, position.y + lh + sp, position.width, lh);

            DrawPlatformPopup(row, platformProp);
            row.y += lh + sp;

            string currentPlatform = platformProp.stringValue;
            DrawFormatPopup(row, posFmtProp, "Position Format", currentPlatform);
            row.y += lh + sp;

            DrawFormatPopup(row, normFmtProp, "Normal Format", currentPlatform);

            EditorGUI.indentLevel = oldIndent;
            EditorGUI.EndProperty();
        }

        private static void DrawPlatformPopup(Rect rect, SerializedProperty platformProp)
        {
            string[] installed = GetInstalledPlatformNames();
            string current = platformProp.stringValue ?? "";

            string[] displayed = installed;
            int idx = System.Array.IndexOf(installed, current);
            if (idx < 0)
            {
                string missingLabel = string.IsNullOrEmpty(current) ? "(Empty)" : current + " (Module not installed)";
                displayed = new string[installed.Length + 1];
                displayed[0] = missingLabel;
                System.Array.Copy(installed, 0, displayed, 1, installed.Length);
                idx = 0;
            }

            int newIdx = EditorGUI.Popup(rect, "Platform", idx, displayed);
            if (newIdx != idx)
            {
                string selected = displayed[newIdx];
                int suffix = selected.IndexOf(" (");
                if (suffix > 0) selected = selected.Substring(0, suffix);
                if (selected == "(Empty)") selected = "";
                platformProp.stringValue = selected;
            }
        }

        private static void DrawFormatPopup(Rect rect, SerializedProperty formatProp, string label, string platform)
        {
            TextureImporterFormat[] compatible = GetCompatibleFormats(platform);
            string[] names = GetCompatibleFormatNames(platform);

            TextureImporterFormat current = (TextureImporterFormat)formatProp.intValue;
            int idx = System.Array.IndexOf(compatible, current);

            string[] displayedNames = names;
            TextureImporterFormat[] displayedFormats = compatible;
            if (idx < 0)
            {
                displayedNames = new string[names.Length + 1];
                displayedFormats = new TextureImporterFormat[compatible.Length + 1];
                displayedNames[0] = current + " (Not compatible)";
                displayedFormats[0] = current;
                System.Array.Copy(names, 0, displayedNames, 1, names.Length);
                System.Array.Copy(compatible, 0, displayedFormats, 1, compatible.Length);
                idx = 0;
            }

            int newIdx = EditorGUI.Popup(rect, label, idx, displayedNames);
            if (newIdx != idx)
            {
                formatProp.intValue = (int)displayedFormats[newIdx];
            }
        }
    }
}
