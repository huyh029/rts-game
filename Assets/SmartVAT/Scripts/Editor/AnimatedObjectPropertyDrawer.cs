using UnityEditor;
using UnityEngine;



namespace SmartVAT
{
    [CustomPropertyDrawer(typeof(AnimatedObject))]
    public class AnimatedObjectPropertyDrawer : PropertyDrawer
    {
        
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            SerializedProperty renderer = property.FindPropertyRelative("rendrer");
            SerializedProperty index = property.FindPropertyRelative("index");
            SerializedProperty mergeMode = property.serializedObject.FindProperty("materialMergeMode");
            bool hideIndex = mergeMode != null && mergeMode.intValue == 0;

            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            EditorGUIUtility.labelWidth = 65;
            if (hideIndex)
            {
                EditorGUI.PropertyField(position, renderer, new GUIContent("Renderer", "Mesh Filter or Skinned Mesh Renderer that you want to convert."));
            }
            else
            {
                float widthSize = position.width / 3;
                float offsetSize = 10;
                Rect pos1 = new Rect(position.x, position.y, widthSize * 2, position.height);
                Rect pos2 = new Rect(position.x + widthSize * 2 + offsetSize, position.y, widthSize - offsetSize, position.height);
                EditorGUI.PropertyField(pos1, renderer, new GUIContent("Renderer", "Mesh Filter or Skinned Mesh Renderer that you want to convert."));
                EditorGUIUtility.labelWidth = 50;
                EditorGUI.PropertyField(pos2, index, new GUIContent("Index", "Material ID or Texture Array Index depending on Material Merge Mode.\n\nIgnored if Material Merge Mode is set to Merge All.\n\nIf numbers are skipped, Smart VAT automatically reduces them to the smallest increment.\n\nFor example, 0 - 2 - 5 becomes 0 - 1 - 2."));
            }

            EditorGUI.indentLevel = indent;
            EditorGUI.EndProperty();
        }
    }
}
