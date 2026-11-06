using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GameToolsEditor
{
    public static class CongThanhForestIndirectSetup
    {
        public static void Prepare(RecoilForestInstancedRenderer renderer)
        {
            if (renderer.data == null) throw new InvalidOperationException("Chưa có dữ liệu rừng đã bake.");
            const string folder = "Assets/Data/Environment/ForestIndirectShaders";
            var parent = "Assets";
            foreach (var part in folder.Substring(7).Split('/'))
            {
                if (!AssetDatabase.IsValidFolder(parent + "/" + part)) AssetDatabase.CreateFolder(parent, part);
                parent += "/" + part;
            }
            var sources = new HashSet<Shader>();
            foreach (var chunk in renderer.data.chunks)
                foreach (var batch in chunk.batches) if (batch.material != null) sources.Add(batch.material.shader);
            var pairs = new List<RecoilForestIndirectShader>();
            foreach (var source in sources)
            {
                var path = AssetDatabase.GetAssetPath(source);
                var guid = AssetDatabase.AssetPathToGUID(path);
                var text = path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase) ? GenerateGraph(path) : ReadShader(path);
                if (!text.Contains("HLSLPROGRAM")) throw new InvalidOperationException("Shader không hỗ trợ chuyển indirect: " + source.name);
                text = Regex.Replace(text, "Shader\\s+\"[^\"]+\"", "Shader \"Recoil/ForestIndirect/" + guid + "\"", RegexOptions.None, TimeSpan.FromSeconds(2));
                text = Regex.Replace(text, @"#pragma target\s+[\d.]+", "#pragma target 4.5");
                text = text.Replace("HLSLPROGRAM", "HLSLPROGRAM\n#pragma target 4.5\n#pragma multi_compile_instancing\n" +
                    "#pragma instancing_options procedural:RecoilForestSetup\n#include \"Assets/Resources/RecoilForestIndirectSetup.hlsl\"\n");
                var destination = folder + "/" + guid + ".shader";
                File.WriteAllText(destination, text);
                AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceSynchronousImport);
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(destination);
                if (shader == null || ShaderUtil.ShaderHasError(shader))
                    throw new InvalidOperationException("Shader indirect chưa biên dịch được: " + destination + ". Xem lỗi shader trong Console; chế độ cũ vẫn được giữ.");
                pairs.Add(new RecoilForestIndirectShader { source = source, indirect = shader });
            }
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/Resources/RecoilForestCulling.compute");
            if (compute == null) throw new InvalidOperationException("Thiếu RecoilForestCulling.compute.");
            Undo.RecordObject(renderer, "Prepare Forest Indirect Rendering");
            renderer.ConfigureIndirect(compute, pairs.ToArray());
            EditorUtility.SetDirty(renderer);
            EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"Prepared {pairs.Count} indirect shader variants. Source shaders, textures and baked forest data are preserved.", renderer);
        }

        private static string GenerateGraph(string path)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType("UnityEditor.ShaderGraph.ShaderGraphImporter");
                if (type == null) continue;
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                    if (method.Name == "GetShaderText" && method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(string))
                        return (string)method.Invoke(null, new object[] { path, null });
            }
            throw new InvalidOperationException("Không tìm thấy ShaderGraph generator của phiên bản Unity này.");
        }

        private static string ReadShader(string path)
        {
            if (path.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
                if (package != null) path = Path.Combine(package.resolvedPath, path.Substring(("Packages/" + package.name + "/").Length));
            }
            return File.ReadAllText(path);
        }
    }
}
