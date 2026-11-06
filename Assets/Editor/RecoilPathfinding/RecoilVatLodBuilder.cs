using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecoilPathfindingEditor
{
    [InitializeOnLoad]
    public static class RecoilVatLodBuilder
    {
        private const string Output = "Assets/Data/Characters/Prefabs VAT LOD";
        private const string Request = "Assets/Editor/RecoilPathfinding/RecoilVatLod.request";
        private static double nextCheck;
        private static bool running;
        static RecoilVatLodBuilder() { nextCheck = EditorApplication.timeSinceStartup + 5; EditorApplication.update += Check; }
        private static void Check()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess() || running) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            { nextCheck = EditorApplication.timeSinceStartup + 5; return; }
            if (EditorApplication.timeSinceStartup < nextCheck) return;
            nextCheck = EditorApplication.timeSinceStartup + 2;
            if (!File.Exists(Request) || string.IsNullOrWhiteSpace(File.ReadAllText(Request))) return;
            File.WriteAllText(Request, string.Empty); Build();
        }

        private static T Save<T>(T asset, string path) where T : UnityEngine.Object
        {
            asset.name = Path.GetFileNameWithoutExtension(path);
            var previous = AssetDatabase.LoadAssetAtPath<T>(path);
            if (previous == null) { AssetDatabase.CreateAsset(asset, path); return asset; }
            EditorUtility.CopySerialized(asset, previous); EditorUtility.SetDirty(previous);
            UnityEngine.Object.DestroyImmediate(asset); return previous;
        }

        [MenuItem("Tools/Recoil Pathfinding/VAT/Build half-and-quarter LOD prefabs")]
        public static void Build()
        {
            if (running || EditorApplication.isPlayingOrWillChangePlaymode || AssetDatabase.IsAssetImportWorkerProcess()) return;
            var library = Resources.Load<RecoilVatPrefabLibrary>("RecoilVatPrefabs");
            if (library == null) throw new InvalidOperationException("Update the VAT game library first.");
            Directory.CreateDirectory(Output); AssetDatabase.Refresh();
            running = true;
            var entries = new List<RecoilVatPrefabLibrary.Entry>();
            var report = new List<string> { "LOD2 target: ~50%, LOD3 target: ~25% of original vertices using connected vertex clustering.",
                "Each retained vertex reads its original VAT pixel through UV channel 3. Original textures and prefabs are unchanged.",
                "Small parts retain original geometry. Normal units: original/half/quarter, screen-height .75/.50/0. Elephants and siege: original/half at .50/0; no quarter mesh." };
            try
            {
                ValidateRemapping();
                for (var i = 0; i < library.Entries.Length; i++)
                {
                    var entry = library.Entries[i];
                    if (entry.Source == null || entry.Vat == null) continue;
                    if (EditorUtility.DisplayCancelableProgressBar("Build VAT LOD", entry.Vat.name, (float)i / library.Entries.Length))
                        throw new OperationCanceledException("LOD build cancelled; completed assets retained.");
                    var scene = EditorSceneManager.NewPreviewScene();
                    try
                    {
                        var clone = (GameObject)PrefabUtility.InstantiatePrefab(entry.Vat, scene);
                        var animation = clone.GetComponent<RecoilVatAnimation>();
                        var original = animation.Parts.Where(p => p != null).ToArray();
                        var largeUnit = entry.Source.name.StartsWith("VOI_", StringComparison.OrdinalIgnoreCase) ||
                            entry.Source.name.StartsWith("maybanda", StringComparison.OrdinalIgnoreCase);
                        var lod2 = new List<Renderer>();
                        var lod3 = new List<Renderer>();
                        var directory = Output + "/" + entry.Source.name;
                        Directory.CreateDirectory(directory); AssetDatabase.Refresh();
                        var originalCount = 0;
                        for (var level = 0; level < (largeUnit ? 1 : 2); level++)
                        {
                        var lower = level == 0 ? lod2 : lod3;
                        var suffix = level == 0 ? "_LOD2" : "_LOD3";
                        var reducedCount = 0;
                        for (var part = 0; part < original.Length; part++)
                        {
                            var source = original[part];
                            var mesh = source.GetComponent<MeshFilter>()?.sharedMesh;
                            if (mesh == null) throw new InvalidOperationException("Missing VAT mesh: " + source.name);
                            if (level == 0) originalCount += mesh.vertexCount;
                            if (mesh.vertexCount < 128) { lower.Add(source); reducedCount += mesh.vertexCount; continue; }
                            var reduced = Reduce(mesh, level == 0 ? .5f : .25f);
                            if (reduced.vertexCount >= mesh.vertexCount)
                            { UnityEngine.Object.DestroyImmediate(reduced); lower.Add(source); reducedCount += mesh.vertexCount; continue; }
                            reducedCount += reduced.vertexCount;
                            report.Add($"{entry.Source.name}/{source.name}{suffix}: {mesh.vertexCount} -> {reduced.vertexCount} vertices, {mesh.triangles.Length / 3} -> {reduced.triangles.Length / 3} triangles");
                            reduced = Save(reduced, directory + "/part" + part + suffix + ".asset");
                            var child = new GameObject(source.name + " VAT" + suffix);
                            child.transform.SetParent(source.transform.parent, false);
                            child.transform.localPosition = source.transform.localPosition;
                            child.transform.localRotation = source.transform.localRotation;
                            child.transform.localScale = source.transform.localScale;
                            child.layer = source.gameObject.layer;
                            child.AddComponent<MeshFilter>().sharedMesh = reduced;
                            var renderer = child.AddComponent<MeshRenderer>();
                            renderer.shadowCastingMode = ShadowCastingMode.Off;
                            var materials = new Material[source.sharedMaterials.Length];
                            for (var m = 0; m < materials.Length; m++)
                            {
                                var material = new Material(source.sharedMaterials[m]) { enableInstancing = true };
                                material.SetFloat("_VatVertexMap", 1);
                                materials[m] = Save(material, directory + "/part" + part + suffix + "_material" + m + ".mat");
                            }
                            renderer.sharedMaterials = materials; lower.Add(renderer);
                        }
                        report.Add($"TOTAL {entry.Source.name}{suffix}: {originalCount} -> {reducedCount} vertices ({100f * reducedCount / Mathf.Max(1, originalCount):F1}%).");
                        }
                        animation.Parts = original.Concat(lod2).Concat(lod3).Distinct().ToArray();
                        var lod = clone.GetComponent<LODGroup>();
                        if (lod == null) lod = clone.AddComponent<LODGroup>();
                        if (lod == null) throw new InvalidOperationException("Could not create LODGroup on " + clone.name);
                        lod.fadeMode = LODFadeMode.None;
                        lod.SetLODs(largeUnit
                            ? new[] { new LOD(.50f, original), new LOD(0f, lod2.ToArray()) }
                            : new[] { new LOD(.75f, original), new LOD(.50f, lod2.ToArray()), new LOD(0f, lod3.ToArray()) });
                        lod.RecalculateBounds();
                        var prefab = PrefabUtility.SaveAsPrefabAsset(clone, Output + "/" + entry.Source.name + " VAT LOD.prefab");
                        entries.Add(new RecoilVatPrefabLibrary.Entry { Source = entry.Source, Vat = prefab });
                    }
                    finally { EditorSceneManager.ClosePreviewScene(scene); }
                }
                var result = ScriptableObject.CreateInstance<RecoilVatPrefabLibrary>(); result.Entries = entries.ToArray();
                Save(result, "Assets/Resources/RecoilVatLodPrefabs.asset");
                report.Add("Completed LOD prefabs: " + entries.Count);
            }
            catch (Exception error) { report.Add(error.ToString()); Debug.LogException(error); }
            finally
            {
                File.WriteAllLines(Output + "/LOD Report.txt", report);
                AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); EditorUtility.ClearProgressBar(); running = false;
                Debug.Log("VAT LOD report: " + Output + "/LOD Report.txt");
            }
        }

        [MenuItem("Tools/Recoil Pathfinding/VAT/Validate LOD vertex remapping")]
        public static void ValidateRemapping()
        {
            const int side = 30;
            var source = new Mesh(); Mesh reduced = null;
            try
            {
                var vertices = new Vector3[side * side];
                var uv = new Vector2[vertices.Length]; var normals = new Vector3[vertices.Length];
                for (var z = 0; z < side; z++) for (var x = 0; x < side; x++)
                {
                    var i = z * side + x; vertices[i] = new Vector3(x, 0, z);
                    uv[i] = new Vector2((float)x / side, (float)z / side); normals[i] = Vector3.up;
                }
                var triangles = new List<int>();
                for (var z = 0; z < side - 1; z++) for (var x = 0; x < side - 1; x++)
                {
                    var i = z * side + x;
                    triangles.AddRange(new[] { i, i + side, i + 1, i + 1, i + side, i + side + 1 });
                }
                source.vertices = vertices; source.uv = uv; source.normals = normals; source.triangles = triangles.ToArray();
                foreach (var ratio in new[] { .5f, .25f })
                {
                reduced = Reduce(source, ratio);
                if (reduced.vertexCount < vertices.Length * ratio * .7f || reduced.vertexCount > vertices.Length * ratio * 1.3f)
                    throw new InvalidOperationException("LOD validation: reduction did not approach requested ratio " + ratio);
                var ids = new List<Vector2>(); reduced.GetUVs(3, ids);
                var positions = reduced.vertices; var reducedUv = reduced.uv;
                for (var i = 0; i < reduced.vertexCount; i++)
                {
                    var id = Mathf.RoundToInt(ids[i].x);
                    if (id < 0 || id >= vertices.Length || positions[i] != vertices[id] || reducedUv[i] != uv[id])
                        throw new InvalidOperationException("LOD validation: source VAT vertex/UV mapping was corrupted.");
                }
                if (reduced.triangles.Length >= triangles.Count || reduced.triangles.Any(i => i < 0 || i >= reduced.vertexCount))
                    throw new InvalidOperationException("LOD validation: invalid reduced topology.");
                Debug.Log($"VAT LOD remapping validation passed: {vertices.Length} -> {reduced.vertexCount} vertices.");
                UnityEngine.Object.DestroyImmediate(reduced); reduced = null;
                }
            }
            finally { if (reduced != null) UnityEngine.Object.DestroyImmediate(reduced); UnityEngine.Object.DestroyImmediate(source); }
        }

        private static Mesh Reduce(Mesh source, float ratio = .5f)
        {
            var vertices = source.vertices;
            var normals = source.normals;
            var uv = source.uv;
            var submeshes = Enumerable.Range(0, source.subMeshCount).Select(source.GetTriangles).ToArray();
            if (uv.Length != vertices.Length) throw new InvalidOperationException("VAT mesh has no complete UV0: " + source.name);
            var bounds = new Bounds(vertices[0], Vector3.zero);
            foreach (var v in vertices) bounds.Encapsulate(v);
            var low = .000001f; var high = Mathf.Max(low * 2, bounds.size.magnitude * 2);
            int[] best = null; var bestError = int.MaxValue;
            var target = Mathf.Max(3, Mathf.RoundToInt(vertices.Length * ratio));
            for (var pass = 0; pass < 22; pass++)
            {
                var size = (low + high) * .5f;
                var parent = Enumerable.Range(0, vertices.Length).ToArray();
                var cells = vertices.Select(v => Vector3Int.FloorToInt((v - bounds.min) / size)).ToArray();
                int Find(int v) { while (parent[v] != v) { parent[v] = parent[parent[v]]; v = parent[v]; } return v; }
                void Join(int a, int b)
                {
                    if (cells[a] != cells[b] || (uv[a] - uv[b]).sqrMagnitude > .0625f) return;
                    if (normals.Length == vertices.Length && Vector3.Dot(normals[a], normals[b]) < .6f) return;
                    var x = Find(a); var y = Find(b); if (x != y) parent[Mathf.Max(x, y)] = Mathf.Min(x, y);
                }
                foreach (var triangles in submeshes)
                    for (var t = 0; t < triangles.Length; t += 3)
                    { Join(triangles[t], triangles[t + 1]); Join(triangles[t + 1], triangles[t + 2]); Join(triangles[t + 2], triangles[t]); }
                for (var v = 0; v < parent.Length; v++) parent[v] = Find(v);
                var used = new HashSet<int>();
                foreach (var triangles in submeshes)
                    for (var t = 0; t < triangles.Length; t += 3)
                    {
                        var a = parent[triangles[t]]; var b = parent[triangles[t + 1]]; var c = parent[triangles[t + 2]];
                        if (a == b || a == c || b == c) continue;
                        used.Add(a); used.Add(b); used.Add(c);
                    }
                var error = Mathf.Abs(used.Count - target);
                if (used.Count >= 3 && error < bestError) { best = parent; bestError = error; }
                if (used.Count > target) low = size; else high = size;
            }
            if (best == null) return UnityEngine.Object.Instantiate(source);
            var remap = new Dictionary<int, int>(); var retained = new List<int>();
            int Map(int v) { if (remap.TryGetValue(v, out var index)) return index; index = retained.Count; remap.Add(v, index); retained.Add(v); return index; }
            var outputTriangles = new List<int[]>();
            foreach (var triangles in submeshes)
            {
                var output = new List<int>();
                for (var t = 0; t < triangles.Length; t += 3)
                {
                    var a = best[triangles[t]]; var b = best[triangles[t + 1]]; var c = best[triangles[t + 2]];
                    if (a == b || a == c || b == c) continue;
                    output.Add(Map(a)); output.Add(Map(b)); output.Add(Map(c));
                }
                outputTriangles.Add(output.ToArray());
            }
            var mesh = new Mesh { indexFormat = retained.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = retained.Select(v => vertices[v]).ToArray();
            mesh.uv = retained.Select(v => uv[v]).ToArray();
            mesh.SetUVs(3, retained.Select(v => new Vector2(v, 0)).ToList());
            mesh.subMeshCount = source.subMeshCount;
            for (var sub = 0; sub < outputTriangles.Count; sub++) mesh.SetTriangles(outputTriangles[sub], sub, false);
            mesh.bounds = source.bounds; // Includes the complete animation, not just the idle mesh.
            return mesh;
        }
    }
}
