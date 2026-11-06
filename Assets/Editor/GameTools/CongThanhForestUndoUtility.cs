using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GameToolsEditor
{
    public static class CongThanhForestUndoUtility
    {
        // Destroy prefab instances individually rather than serializing an entire forest
        // as one Destroy Object record. Keep each prefab intact so Undo preserves its links.
        public static void DestroyHierarchy(GameObject root)
        {
            if (root == null) return;
            if (!PrefabUtility.IsAnyPrefabInstanceRoot(root))
            {
                var children = new List<GameObject>(root.transform.childCount);
                foreach (Transform child in root.transform) children.Add(child.gameObject);
                foreach (var child in children) DestroyHierarchy(child);
            }
            Undo.DestroyObjectImmediate(root);
        }
    }
}
