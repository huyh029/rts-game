using UnityEditor;
using UnityEngine;

namespace SmartVAT
{
    [CustomEditor(typeof(SmartVATSettings))]
    public class SmartVATSettingsEditor : Editor
    {
        private const string PREF_PREFIX = "SmartVATSettings.";

        private SerializedProperty p_baseName;
        private SerializedProperty p_texturePrefix;
        private SerializedProperty p_materialPrefix;
        private SerializedProperty p_meshPrefix;
        private SerializedProperty p_smartVATsuffix;
        private SerializedProperty p_positionSuffix;
        private SerializedProperty p_normalSuffix;
        private SerializedProperty p_saveLocation;
        private SerializedProperty p_defaultTextureFormat;
        private SerializedProperty p_textureFormatOverrides;
        private SerializedProperty p_defaultPreviewMaterial;
        private SerializedProperty p_defaultVATPreset;
        private SerializedProperty p_smartVATPresets;

        private bool showNaming;
        private bool showTextureFormat;
        private bool showDebug;
        private bool showPresets;

        private void OnEnable()
        {
            this.p_baseName = this.serializedObject.FindProperty("baseName");
            this.p_texturePrefix = this.serializedObject.FindProperty("texturePrefix");
            this.p_materialPrefix = this.serializedObject.FindProperty("materialPrefix");
            this.p_meshPrefix = this.serializedObject.FindProperty("meshPrefix");
            this.p_smartVATsuffix = this.serializedObject.FindProperty("smartVATsuffix");
            this.p_positionSuffix = this.serializedObject.FindProperty("positionSuffix");
            this.p_normalSuffix = this.serializedObject.FindProperty("normalSuffix");
            this.p_saveLocation = this.serializedObject.FindProperty("saveLocation");
            this.p_defaultTextureFormat = this.serializedObject.FindProperty("defaultTextureFormat");
            this.p_textureFormatOverrides = this.serializedObject.FindProperty("textureFormatOverrides");
            this.p_defaultPreviewMaterial = this.serializedObject.FindProperty("defaultPreviewMaterial");
            this.p_defaultVATPreset = this.serializedObject.FindProperty("defaultVATPreset");
            this.p_smartVATPresets = this.serializedObject.FindProperty("smartVATPresets");

            this.showNaming = EditorPrefs.GetBool(PREF_PREFIX + "showNaming", true);
            this.showTextureFormat = EditorPrefs.GetBool(PREF_PREFIX + "showTextureFormat", true);
            this.showDebug = EditorPrefs.GetBool(PREF_PREFIX + "showDebug", true);
            this.showPresets = EditorPrefs.GetBool(PREF_PREFIX + "showPresets", true);
        }

        public override void OnInspectorGUI()
        {
            this.serializedObject.Update();

            EditorGUILayout.HelpBox(
                "These settings are locked in the free version of Smart VAT. Unlock them in the full version.",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(true))
            {
                DrawSection("Naming Convention and Default Paths", ref this.showNaming, PREF_PREFIX + "showNaming", () =>
                {
                    EditorGUILayout.PropertyField(this.p_baseName);
                    EditorGUILayout.PropertyField(this.p_texturePrefix);
                    EditorGUILayout.PropertyField(this.p_materialPrefix);
                    EditorGUILayout.PropertyField(this.p_meshPrefix);
                    EditorGUILayout.PropertyField(this.p_smartVATsuffix);
                    EditorGUILayout.PropertyField(this.p_positionSuffix);
                    EditorGUILayout.PropertyField(this.p_normalSuffix);
                    EditorGUILayout.PropertyField(this.p_saveLocation);
                });

                DrawSection("Texture Format", ref this.showTextureFormat, PREF_PREFIX + "showTextureFormat", () =>
                {
                    EditorGUILayout.PropertyField(this.p_defaultTextureFormat, true);
                    EditorGUILayout.PropertyField(this.p_textureFormatOverrides, true);
                });

                DrawSection("Debug Material", ref this.showDebug, PREF_PREFIX + "showDebug", () =>
                {
                    EditorGUILayout.PropertyField(this.p_defaultPreviewMaterial);
                });

                DrawSection("Export Presets", ref this.showPresets, PREF_PREFIX + "showPresets", () =>
                {
                    EditorGUILayout.PropertyField(this.p_defaultVATPreset, true);
                    EditorGUILayout.PropertyField(this.p_smartVATPresets, true);
                });
            }

            this.serializedObject.ApplyModifiedProperties();
        }

        private static void DrawSection(string title, ref bool expanded, string prefKey, System.Action drawContents)
        {
            bool previous = expanded;
            expanded = EditorGUILayout.Foldout(expanded, title, true, EditorStyles.foldoutHeader);
            if (expanded != previous)
            {
                EditorPrefs.SetBool(prefKey, expanded);
            }
            if (expanded)
            {
                EditorGUI.indentLevel++;
                drawContents();
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.Space();
        }
    }
}
