#if SKGAMES_ANIMATED_IMPOSTOR
using System;
using UnityEditor;
using UnityEngine;

namespace RecoilPathfindingEditor
{
    public static class RecoilGpuImpostorChecks
    {
        [MenuItem("Tools/Recoil Pathfinding/Validate GPU Impostors")]
        public static void Run()
        {
            var shader = Shader.Find("SKGames/Animated Impostor");
            if (shader == null) throw new InvalidOperationException("Animated impostor shader was not found.");
            var material = new Material(shader) { enableInstancing = true };
            try
            {
                material.EnableKeyword("PROCEDURAL_INSTANCING_ON");
                ShaderUtil.CompilePass(material, 0, true);
                if (ShaderUtil.ShaderHasError(shader)) throw new InvalidOperationException("Impostor shader has compilation errors; inspect the Console.");
                RecoilPathfinding.RecoilGpuImpostorRenderer.ValidateGpuCulling();
                Debug.Log("GPU impostor checks passed: procedural shader pass, 176-byte instance layout, frustum/partial visibility, inactive units, indirect argument counter and optional distance limit.");
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }
    }
}
#endif
