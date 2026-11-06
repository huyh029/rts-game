using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace RecoilPathfindingEditor
{
    [InitializeOnLoad]
    public static class RecoilVatBaker
    {
        private const string Sources = "Assets/Data/Characters/Prefabs";
        private const string Output = "Assets/Data/Characters/Prefabs VAT";
        private const string Request = "Assets/Editor/RecoilPathfinding/RecoilVatBake.request";
        private const int Fps = 30;
        private static bool running;
        private static double nextCheck;

        private sealed class SkinSource
        {
            private readonly Vector3[] vertices;
            private readonly Matrix4x4[] bindPoses, matrices;
            private readonly Transform[] bones;
            private readonly byte[] counts;
            private readonly BoneWeight1[] weights;
            private readonly Transform rendererTransform;
            private readonly bool rigid;
            private readonly bool[] usedBones;
            public bool IsRigid => rigid;
            public SkinSource(SkinnedMeshRenderer renderer)
            {
                var mesh = renderer.sharedMesh;
                rendererTransform = renderer.transform;
                vertices = mesh.vertices;
                bindPoses = mesh.bindposes;
                bones = renderer.bones;
                matrices = new Matrix4x4[bindPoses.Length];
                usedBones = new bool[bindPoses.Length];
                using var boneCounts = mesh.GetBonesPerVertex();
                using var allWeights = mesh.GetAllBoneWeights();
                counts = boneCounts.ToArray(); weights = allWeights.ToArray();
                rigid = weights.Length == 0;
                if (!rigid && (counts.Length != vertices.Length || counts.Sum(c => (int)c) != weights.Length))
                    throw new InvalidOperationException($"Invalid skin data: {renderer.name}, vertices={vertices.Length}, counts={counts.Length}, weights={weights.Length}");
                foreach (var weight in weights)
                {
                    if (weight.boneIndex < 0 || weight.boneIndex >= bindPoses.Length ||
                        weight.boneIndex >= bones.Length || bones[weight.boneIndex] == null)
                        throw new InvalidOperationException($"Missing used skin bone {weight.boneIndex}: {renderer.name}");
                    usedBones[weight.boneIndex] = true;
                }
                for (var i = 0; i < mesh.blendShapeCount; i++)
                    if (Mathf.Abs(renderer.GetBlendShapeWeight(i)) > .001f)
                        throw new NotSupportedException("Nonzero blend shapes require a separate VAT blend-shape bake: " + renderer.name);
            }
            public Vector3[] Evaluate(Transform root)
            {
                var inverse = root.worldToLocalMatrix;
                var rigidMatrix = inverse * rendererTransform.localToWorldMatrix;
                if (rigid) return vertices.Select(v => rigidMatrix.MultiplyPoint3x4(v)).ToArray();
                for (var i = 0; i < matrices.Length; i++)
                {
                    if (!usedBones[i]) continue;
                    matrices[i] = inverse * bones[i].localToWorldMatrix * bindPoses[i];
                }
                var output = new Vector3[vertices.Length];
                var offset = 0;
                for (var v = 0; v < vertices.Length; v++)
                {
                    if (counts[v] == 0) output[v] = rigidMatrix.MultiplyPoint3x4(vertices[v]);
                    for (var w = 0; w < counts[v]; w++)
                    {
                        var weight = weights[offset++];
                        output[v] += matrices[weight.boneIndex].MultiplyPoint3x4(vertices[v]) * weight.weight;
                    }
                }
                return output;
            }
        }

        static RecoilVatBaker()
        {
            nextCheck = EditorApplication.timeSinceStartup + 5;
            EditorApplication.update += CheckRequest;
        }
        private static void CheckRequest()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                nextCheck = EditorApplication.timeSinceStartup + 5;
                return;
            }
            if (running || EditorApplication.timeSinceStartup < nextCheck) return;
            nextCheck = EditorApplication.timeSinceStartup + 2;
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
                EditorApplication.isUpdating || !File.Exists(Request)) return;
            // Only an explicit request file triggers this; ordinary imports do not rebake.
            var request = File.ReadAllText(Request);
            if (string.IsNullOrWhiteSpace(request)) return;
            var siegeOnly = request.Contains("siege-only");
            var previewOnly = request.Contains("preview-only");
            var gameOnly = request.Contains("game-only");
            // Keep the asset and its GUID; an empty payload means there is no pending work.
            File.WriteAllText(Request, string.Empty);
            if (gameOnly) { RecoilVatEcsBridge.ValidateStateTransitions(); BuildGameLibrary(); }
            else if (previewOnly) UpdatePreview(); else if (siegeOnly) BakeSiege(); else BakeAll();
        }

        [MenuItem("Tools/Recoil Pathfinding/VAT/Rebake siege and update preview")]
        public static void BakeSiege()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess() || running || EditorApplication.isPlayingOrWillChangePlaymode ||
                EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            running = true;
            var report = new List<string>();
            try
            {
                foreach (var path in AssetDatabase.FindAssets("t:Prefab", new[] { Sources }).Select(AssetDatabase.GUIDToAssetPath)
                    .Where(p => Path.GetFileName(p).StartsWith("maybanda", StringComparison.OrdinalIgnoreCase)))
                    Bake(path, report);
                var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { Output }).Select(AssetDatabase.GUIDToAssetPath)
                    .Select(AssetDatabase.LoadAssetAtPath<GameObject>).Where(p => p != null && p.GetComponent<RecoilVatAnimation>() != null)
                    .OrderBy(p => p.name).ToList();
                CreatePreview(prefabs);
                BuildGameLibrary();
                report.Add("Preview updated, including both siege VAT prefabs. Keys 1/2/3/4: Idle/Run/Attack/Death.");
            }
            catch (Exception error) { report.Add(error.ToString()); Debug.LogException(error); }
            finally
            {
                Directory.CreateDirectory(Output);
                File.WriteAllLines(Output + "/SiegeBakeReport.txt", report);
                AssetDatabase.SaveAssets(); AssetDatabase.Refresh(); running = false;
            }
        }

        private static AnimationClip SiegeAttackClip(GameObject source)
        {
            // Controllers can rename an imported take; inspect FBX subassets as well.
            var presentation = source.GetComponent<RecoilCharacterPresentation>();
            var attack = presentation != null ? presentation.AttackClip : null;
            var paths = AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(source))
                .Where(p => p.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)).ToList();
            if (attack != null) paths.Insert(0, AssetDatabase.GetAssetPath(attack));
            foreach (var path in paths.Distinct())
            {
                var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().ToArray();
                var clip = clips.FirstOrDefault(c => c.name.Equals("scene", StringComparison.OrdinalIgnoreCase));
                if (clip != null) return clip;
                if (AssetImporter.GetAtPath(path) is ModelImporter importer)
                {
                    var takes = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
                    var take = takes.FirstOrDefault(t => t.takeName.Equals("scene", StringComparison.OrdinalIgnoreCase));
                    if (take != null)
                    {
                        clip = clips.FirstOrDefault(c => c.name == take.name);
                        if (clip != null) return clip;
                    }
                }
            }
            throw new InvalidOperationException("Cannot locate imported scene take for " + source.name);
        }

        [MenuItem("Tools/Recoil Pathfinding/VAT/Bake all character prefabs")]
        public static void BakeAll()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("Wait until script compilation and asset import finish before baking VAT.");
            if (EditorApplication.isPlayingOrWillChangePlaymode || running)
                throw new InvalidOperationException("Stop Play Mode before baking VAT.");
            running = true;
            Directory.CreateDirectory(Output);
            AssetDatabase.Refresh();
            var report = new List<string> { "VAT preview: LOD0, 30 FPS, Idle/Run/Attack/Death.",
                "Positions stored in prefab-root local space. Output retains original prefab root scale.",
                "Preview materials are unlit; these are separate render-only prefabs, not battle replacements." };
            var prefabs = new List<GameObject>();
            try
            {
                var paths = AssetDatabase.FindAssets("t:Prefab", new[] { Sources })
                    .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p).ToArray();
                for (var i = 0; i < paths.Length; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Bake character VAT", paths[i], (float)i / paths.Length))
                        throw new OperationCanceledException("VAT bake cancelled; completed assets are retained.");
                    try { prefabs.Add(Bake(paths[i], report)); }
                    catch (Exception error) { report.Add("FAILED " + paths[i] + ": " + error); Debug.LogException(error); }
                }
                if (prefabs.Count > 0) { CreatePreview(prefabs); BuildGameLibrary(); }
                report.Add($"Completed {prefabs.Count}/{paths.Length} prefabs.");
            }
            catch (Exception error) { report.Add(error.ToString()); Debug.LogException(error); }
            finally
            {
                File.WriteAllLines(Output + "/BakeReport.txt", report);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.ClearProgressBar();
                running = false;
                Debug.Log("VAT bake report: " + Output + "/BakeReport.txt");
            }
        }

        private static AnimationClip StateClip(Animator animator, string state)
        {
            var runtime = animator.runtimeAnimatorController;
            if (runtime == null) return null;
            var overrides = runtime as AnimatorOverrideController;
            var controller = (overrides != null ? overrides.runtimeAnimatorController : runtime) as AnimatorController;
            AnimationClip Find(AnimatorStateMachine machine)
            {
                foreach (var child in machine.states)
                    if (child.state.name.Equals(state, StringComparison.OrdinalIgnoreCase))
                    {
                        if (child.state.motion is AnimationClip clip) return clip;
                        if (child.state.motion == null) return null; // Intentionally static state.
                        throw new NotSupportedException("VAT preview requires an evaluated BlendTree: " + state);
                    }
                foreach (var child in machine.stateMachines) { var clip = Find(child.stateMachine); if (clip != null) return clip; }
                return null;
            }
            var found = controller != null ? controller.layers.Select(l => Find(l.stateMachine)).FirstOrDefault(c => c != null) : null;
            if (found != null && overrides != null) found = overrides[found] ?? found;
            return found ?? runtime.animationClips.FirstOrDefault(c => c.name.Equals(state, StringComparison.OrdinalIgnoreCase) ||
                c.name.EndsWith("__" + state, StringComparison.OrdinalIgnoreCase));
        }

        private static T Save<T>(T asset, string path) where T : UnityEngine.Object
        {
            asset.name = Path.GetFileNameWithoutExtension(path);
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null) { AssetDatabase.CreateAsset(asset, path); return asset; }
            EditorUtility.CopySerialized(asset, existing);
            EditorUtility.SetDirty(existing);
            UnityEngine.Object.DestroyImmediate(asset);
            return existing;
        }

        private static GameObject Bake(string path, List<string> report)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var directory = Output + "/" + source.name;
            Directory.CreateDirectory(directory);
            AssetDatabase.Refresh();
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject clone = null, result = null;
            try
            {
                clone = (GameObject)PrefabUtility.InstantiatePrefab(source, scene);
                clone.transform.position = Vector3.zero;
                clone.transform.rotation = Quaternion.identity;
                var siege = source.name.StartsWith("maybanda", StringComparison.OrdinalIgnoreCase);
                if (siege)
                {
                    // Generated character wrappers zeroed some FBX child offsets. Recover authored
                    // arm pivots, but retain the outer wrapper's normalization and model-root scale.
                    foreach (var node in clone.GetComponentsInChildren<Transform>(true))
                    {
                        var original = PrefabUtility.GetCorrespondingObjectFromSource(node);
                        if (original == null || original.parent == null ||
                            !AssetDatabase.GetAssetPath(original).EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) continue;
                        node.localPosition = original.localPosition;
                        node.localRotation = original.localRotation;
                        node.localScale = original.localScale;
                    }
                    report.Add(source.name + ": restored authored FBX child transforms for arm pivots.");
                }
                foreach (var presentation in clone.GetComponentsInChildren<RecoilCharacterPresentation>(true))
                    presentation.ApplyVisualSettings();
                var rootScale = clone.transform.localScale;
                var transforms = clone.GetComponentsInChildren<Transform>(true);
                var positions = transforms.Select(t => t.localPosition).ToArray();
                var rotations = transforms.Select(t => t.localRotation).ToArray();
                var scales = transforms.Select(t => t.localScale).ToArray();
                var animators = clone.GetComponentsInChildren<Animator>(true);
                if (animators.Length == 0) throw new InvalidOperationException("No source Animator.");
                var states = (RecoilVatAnimation.AnimationState[])Enum.GetValues(typeof(RecoilVatAnimation.AnimationState));
                var siegeAttack = siege ? SiegeAttackClip(source) : null;
                var clips = new AnimationClip[states.Length, animators.Length];
                var metadata = new RecoilVatAnimation.Clip[states.Length];
                var totalFrames = 0;
                for (var state = 0; state < states.Length; state++)
                {
                    var duration = 0f;
                    for (var a = 0; a < animators.Length; a++)
                    {
                        if (siege)
                        {
                            // Siege models only animate Attack. Other states retain the authored rest pose.
                            clips[state, a] = states[state] == RecoilVatAnimation.AnimationState.Attack
                                ? siegeAttack
                                : null;
                            if (states[state] == RecoilVatAnimation.AnimationState.Attack && clips[state, a] == null)
                                throw new InvalidOperationException($"Missing exact attack clip 'scene' on {source.name}/{animators[a].name}.");
                        }
                        else clips[state, a] = StateClip(animators[a], states[state].ToString());
                        if (!siege && clips[state, a] == null && states[state] == RecoilVatAnimation.AnimationState.Run)
                            clips[state, a] = StateClip(animators[a], "Idle");
                        if (clips[state, a] == null)
                        {
                            if (!siege && state >= 2) throw new InvalidOperationException($"Missing {states[state]} clip on {animators[a].name}.");
                            report.Add($"{source.name}/{animators[a].name}: {states[state]} uses the prefab rest pose (no motion).");
                        }
                        else duration = Mathf.Max(duration, clips[state, a].length);
                    }
                    if (duration <= 0f) duration = 1f;
                    var staticSiege = siege && states[state] != RecoilVatAnimation.AnimationState.Attack;
                    var loop = !staticSiege && state < 2;
                    var frames = staticSiege ? 1 : Mathf.Max(2, Mathf.CeilToInt(duration * Fps) + (loop ? 0 : 1));
                    metadata[state] = new RecoilVatAnimation.Clip { State = states[state], Duration = duration,
                        StartFrame = totalFrames, FrameCount = frames, Loop = loop };
                    totalFrames += frames;
                }
                var translationOffsets = new List<(Transform Node, Vector3 Offset)>[states.Length];
                for (var state = 0; state < states.Length; state++)
                {
                    translationOffsets[state] = new List<(Transform, Vector3)>();
                    if (!siege && !source.name.StartsWith("VOI_", StringComparison.OrdinalIgnoreCase)) continue;
                    for (var a = 0; a < animators.Length; a++)
                    {
                        var clip = clips[state, a];
                        if (clip == null) continue;
                        var paths = AnimationUtility.GetCurveBindings(clip)
                            .Where(b => b.type == typeof(Transform) && b.propertyName.StartsWith("m_LocalPosition.", StringComparison.Ordinal) && b.path.Length > 0)
                            .Select(b => b.path).Distinct().ToArray();
                        // Correct only the highest translated nodes. Correcting every bone would
                        // distort the pose; correcting mesh bounds would move rotating arms.
                        var roots = paths.Where(p => !paths.Any(parent => p.StartsWith(parent + "/", StringComparison.Ordinal))).ToArray();
                        for (var t = 0; t < transforms.Length; t++)
                        { transforms[t].localPosition = positions[t]; transforms[t].localRotation = rotations[t]; transforms[t].localScale = scales[t]; }
                        clip.SampleAnimation(animators[a].gameObject, 0);
                        foreach (var bindingPath in roots)
                        {
                            var node = animators[a].transform.Find(bindingPath);
                            if (node == null) continue;
                            var index = Array.IndexOf(transforms, node);
                            var offset = positions[index] - node.localPosition;
                            if (offset.sqrMagnitude < .00000001f) continue;
                            translationOffsets[state].Add((node, offset));
                            report.Add($"{source.name}/{states[state]}: translation anchor {bindingPath}, constant local correction={offset}");
                        }
                    }
                }
                for (var t = 0; t < transforms.Length; t++)
                { transforms[t].localPosition = positions[t]; transforms[t].localRotation = rotations[t]; transforms[t].localScale = scales[t]; }
                var lodRenderers = new HashSet<Renderer>();
                var firstLodRenderers = new HashSet<Renderer>();
                foreach (var lod in clone.GetComponentsInChildren<LODGroup>(true))
                {
                    var levels = lod.GetLODs();
                    if (levels.Length == 0) continue;
                    foreach (var renderer in levels[0].renderers) if (renderer != null) firstLodRenderers.Add(renderer);
                    foreach (var level in levels)
                        foreach (var renderer in level.renderers) if (renderer != null) lodRenderers.Add(renderer);
                }
                // Weapons can be shared between LOD0 and lower levels. Membership in
                // LOD0 wins; only parts exclusive to lower levels should be excluded.
                var renderers = clone.GetComponentsInChildren<Renderer>(true).Where(r =>
                    (!lodRenderers.Contains(r) || firstLodRenderers.Contains(r)) &&
                    (r is SkinnedMeshRenderer || r is MeshRenderer) && r.enabled && r.gameObject.activeInHierarchy).ToArray();
                // Some imported parts expose both renderer types. Never bake the same object twice.
                renderers = renderers.GroupBy(r => r.gameObject).Select(g =>
                    g.FirstOrDefault(r => r is SkinnedMeshRenderer) ?? g.First()).ToArray();
                var uniqueParts = new HashSet<(Mesh, Matrix4x4)>();
                renderers = renderers.Where(r =>
                {
                    // Only rigid duplicates at the same placement are safe to collapse.
                    // Distinct skinned parts may share a mesh but bind to different bones.
                    if (r is SkinnedMeshRenderer) return true;
                    var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
                    var unique = uniqueParts.Add((mesh, r.transform.localToWorldMatrix));
                    if (!unique) report.Add(source.name + ": skipped duplicate rigid part " + r.name);
                    return unique;
                }).ToArray();
                result = new GameObject(source.name + " VAT");
                SceneManager.MoveGameObjectToScene(result, scene);
                result.transform.localScale = rootScale;
                var player = result.AddComponent<RecoilVatAnimation>();
                player.Clips = metadata;
                var parts = new List<Renderer>();
                var idleBounds = new Bounds();
                var hasIdleBounds = false;
                var shader = Shader.Find("Recoil/VAT Preview");
                if (shader == null || !shader.isSupported) throw new InvalidOperationException("VAT shader unavailable or unsupported.");
                for (var part = 0; part < renderers.Length; part++)
                {
                    var renderer = renderers[part];
                    var skin = renderer as SkinnedMeshRenderer;
                    var mesh = skin != null ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    if (mesh == null) continue;
                    var width = Mathf.Min(4096, Mathf.NextPowerOfTwo(mesh.vertexCount));
                    var rows = Mathf.CeilToInt((float)mesh.vertexCount / width);
                    var height = rows * totalFrames;
                    if (width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize)
                        throw new InvalidOperationException($"VAT texture too large: {width}x{height} for {renderer.name}.");
                    var pixels = new Color[width * height];
                    var skinSource = skin != null ? new SkinSource(skin) : null;
                    if (skinSource != null && skinSource.IsRigid)
                        report.Add(source.name + "/" + renderer.name + ": unweighted mesh, baked as rigid attachment with its animated transform.");
                    var outputMesh = UnityEngine.Object.Instantiate(mesh);
                    var bounds = new Bounds();
                    var initialized = false;
                    try
                    {
                        for (var state = 0; state < states.Length; state++)
                            for (var frame = 0; frame < metadata[state].FrameCount; frame++)
                            {
                                for (var t = 0; t < transforms.Length; t++)
                                { transforms[t].localPosition = positions[t]; transforms[t].localRotation = rotations[t]; transforms[t].localScale = scales[t]; }
                                var time = metadata[state].Duration * frame / Mathf.Max(1,
                                    metadata[state].Loop ? metadata[state].FrameCount : metadata[state].FrameCount - 1);
                                for (var a = 0; a < animators.Length; a++)
                                {
                                    var clip = clips[state, a];
                                    if (clip != null)
                                        clip.SampleAnimation(animators[a].gameObject, metadata[state].Loop ? time % Mathf.Max(.001f, clip.length) : Mathf.Min(time, clip.length));
                                    // Bake in place: animation must not move/scale the FBX owner.
                                    // Preserve the prefab's placement and scale relative to its wrapper.
                                    var owner = Array.IndexOf(transforms, animators[a].transform);
                                    animators[a].transform.localPosition = positions[owner];
                                    animators[a].transform.localRotation = rotations[owner];
                                    animators[a].transform.localScale = scales[owner];
                                }
                                foreach (var correction in translationOffsets[state])
                                    correction.Node.localPosition += correction.Offset;
                                var vertices = skinSource != null ? skinSource.Evaluate(clone.transform) : mesh.vertices;
                                // Skin matrices already produce prefab-root coordinates. Do not apply
                                // the renderer/FBX transform again; it is included in bone world matrices.
                                var toRoot = clone.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                                for (var vertex = 0; vertex < vertices.Length; vertex++)
                                {
                                    var local = skinSource != null ? vertices[vertex] : toRoot.MultiplyPoint3x4(vertices[vertex]);
                                    vertices[vertex] = local;
                                    if (state == 0 && frame == 0)
                                    {
                                        if (!hasIdleBounds) { idleBounds = new Bounds(local, Vector3.zero); hasIdleBounds = true; }
                                        else idleBounds.Encapsulate(local);
                                    }
                                    pixels[(metadata[state].StartFrame + frame) * rows * width + vertex] = new Color(local.x, local.y, local.z, 1);
                                    if (!initialized) { bounds = new Bounds(local, Vector3.zero); initialized = true; } else bounds.Encapsulate(local);
                                }
                                if (state == 0 && frame == 0) outputMesh.vertices = vertices;
                            }
                        bounds.Expand(.1f);
                        outputMesh.bounds = bounds;
                        var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
                            { name = renderer.name + " VAT positions", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                        texture.SetPixels(pixels); texture.Apply(false, true);
                        texture = Save(texture, directory + "/part" + part + "_positions.asset");
                        outputMesh = Save(outputMesh, directory + "/part" + part + "_mesh.asset");
                        var child = new GameObject(renderer.name);
                        child.transform.SetParent(result.transform, false);
                        child.AddComponent<MeshFilter>().sharedMesh = outputMesh;
                        var target = child.AddComponent<MeshRenderer>();
                        target.shadowCastingMode = ShadowCastingMode.Off;
                        var materials = new List<Material>();
                        for (var sub = 0; sub < mesh.subMeshCount; sub++)
                        {
                            var original = renderer.sharedMaterials.ElementAtOrDefault(sub);
                            var material = new Material(shader) { enableInstancing = true };
                            if (original != null)
                            {
                                var map = original.HasProperty("_BaseMap") ? "_BaseMap" : "_MainTex";
                                if (original.HasProperty(map)) { material.SetTexture("_BaseMap", original.GetTexture(map)); material.SetTextureScale("_BaseMap", original.GetTextureScale(map)); material.SetTextureOffset("_BaseMap", original.GetTextureOffset(map)); }
                                if (original.HasProperty("_BaseColor")) material.SetColor("_BaseColor", original.GetColor("_BaseColor"));
                                if (original.HasProperty("_Cutoff") && original.IsKeywordEnabled("_ALPHATEST_ON")) material.SetFloat("_Cutoff", original.GetFloat("_Cutoff"));
                            }
                            material.SetTexture("_VatPositions", texture);
                            material.SetVector("_VatSize", new Vector4(width, rows, 0, 0));
                            var idle = metadata[0];
                            material.SetVector("_VatClip", new Vector4(idle.StartFrame, idle.FrameCount, idle.Duration, 1));
                            materials.Add(Save(material, directory + "/part" + part + "_material" + sub + ".mat"));
                        }
                        target.sharedMaterials = materials.ToArray(); parts.Add(target);
                        report.Add($"{source.name}/{renderer.name}: vertices={mesh.vertexCount}, texture={width}x{height}, rootScale={rootScale}, skin=bindpose-to-prefab-root, bounds={bounds}");
                    }
                    finally { if (!AssetDatabase.Contains(outputMesh)) UnityEngine.Object.DestroyImmediate(outputMesh); }
                }
                if (parts.Count == 0) throw new InvalidOperationException("No LOD0 render parts found.");
                // One fixed foot pivot for the whole character, never one per mesh/frame.
                // Death poses and attack weapons cannot move the reference point.
                var pivot = new Vector3(idleBounds.center.x, idleBounds.min.y, idleBounds.center.z);
                foreach (var part in parts) part.transform.localPosition = -pivot;
                report.Add($"{source.name}: shared Idle foot pivot={pivot}, rootScale={rootScale}, Idle local size={idleBounds.size}, Idle world height={idleBounds.size.y * Mathf.Abs(rootScale.y):G5}");
                player.Parts = parts.ToArray();
                return PrefabUtility.SaveAsPrefabAsset(result, Output + "/" + source.name + " VAT.prefab");
            }
            finally { if (result != null) UnityEngine.Object.DestroyImmediate(result); if (clone != null) UnityEngine.Object.DestroyImmediate(clone); EditorSceneManager.ClosePreviewScene(scene); }
        }

        [MenuItem("Tools/Recoil Pathfinding/VAT/Update game VAT library")]
        public static void BuildGameLibrary()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || AssetDatabase.IsAssetImportWorkerProcess()) return;
            var entries = new List<RecoilVatPrefabLibrary.Entry>();
            foreach (var path in AssetDatabase.FindAssets("t:Prefab", new[] { Sources }).Select(AssetDatabase.GUIDToAssetPath))
            {
                var source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var vat = AssetDatabase.LoadAssetAtPath<GameObject>(Output + "/" + source.name + " VAT.prefab");
                if (vat != null && vat.GetComponent<RecoilVatAnimation>() != null)
                    entries.Add(new RecoilVatPrefabLibrary.Entry { Source = source, Vat = vat });
            }
            var library = ScriptableObject.CreateInstance<RecoilVatPrefabLibrary>();
            library.Entries = entries.ToArray();
            Save(library, "Assets/Resources/RecoilVatPrefabs.asset");
            AssetDatabase.SaveAssets();
            Debug.Log("VAT game library updated: " + entries.Count + " source-to-VAT mappings.");
        }

        [MenuItem("Tools/Recoil Pathfinding/VAT/Validate ECS animation states")]
        public static void ValidateGameStates() => RecoilVatEcsBridge.ValidateStateTransitions();

        [MenuItem("Tools/Recoil Pathfinding/VAT/Update preview scene only")]
        public static void UpdatePreview()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess() || EditorApplication.isPlayingOrWillChangePlaymode) return;
            var prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { Output }).Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                .Where(p => p != null && p.GetComponent<RecoilVatAnimation>() != null).OrderBy(p => p.name).ToList();
            if (prefabs.Count == 0) throw new InvalidOperationException("Bake VAT prefabs before creating the preview.");
            CreatePreview(prefabs);
        }

        private static void CreatePreview(List<GameObject> prefabs)
        {
            var path = Output + "/VAT Preview.unity";
            var scene = SceneManager.GetSceneByPath(path);
            var reuse = scene.IsValid() && scene.isLoaded;
            var hadUnsavedChanges = reuse && scene.isDirty;
            var previousActiveScene = SceneManager.GetActiveScene();
            if (!reuse) scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Update VAT preview");
            var completed = false;
            try
            {
                if (reuse)
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        var prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root) ?? string.Empty;
                        var generatedVat = root.GetComponent<RecoilVatAnimation>() != null &&
                            prefabPath.StartsWith(Output + "/", StringComparison.Ordinal);
                        var generatedReference = root.name.EndsWith(" ORIGINAL (left)", StringComparison.Ordinal) &&
                            prefabPath.StartsWith(Sources + "/", StringComparison.Ordinal);
                        var generatedCamera = root.name == "VAT Preview Camera" && root.GetComponent<Camera>() != null;
                        if (generatedVat || generatedReference || generatedCamera) Undo.DestroyObjectImmediate(root);
                    }
                var bounds = new Bounds(); var initialized = false;
                for (var i = 0; i < prefabs.Count; i++)
                {
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabs[i], scene);
                    Undo.RegisterCreatedObjectUndo(instance, "Add VAT preview");
                    var cell = new Vector3(i % 4 * 16f, 0, i / 4 * 16f);
                    instance.transform.position = cell + Vector3.right * 3f;
                    var sourceName = prefabs[i].name.Substring(0, prefabs[i].name.Length - " VAT".Length);
                    var source = AssetDatabase.LoadAssetAtPath<GameObject>(Sources + "/" + sourceName + ".prefab");
                    if (source != null)
                    {
                        var reference = (GameObject)PrefabUtility.InstantiatePrefab(source, scene);
                        Undo.RegisterCreatedObjectUndo(reference, "Add original preview");
                        reference.name = sourceName + " ORIGINAL (left)";
                        reference.transform.position = cell - Vector3.right * 3f;
                        // Keep Animator and renderers; omit battle/navigation callbacks in the comparison scene.
                        foreach (var behaviour in reference.GetComponentsInChildren<MonoBehaviour>(true)) behaviour.enabled = false;
                        foreach (var part in reference.GetComponentsInChildren<Renderer>())
                            if (!initialized) { bounds = part.bounds; initialized = true; } else bounds.Encapsulate(part.bounds);
                    }
                    foreach (var part in instance.GetComponentsInChildren<Renderer>())
                        if (!initialized) { bounds = part.bounds; initialized = true; } else bounds.Encapsulate(part.bounds);
                }
                var camera = new GameObject("VAT Preview Camera").AddComponent<Camera>();
                Undo.RegisterCreatedObjectUndo(camera.gameObject, "Add VAT camera");
                SceneManager.MoveGameObjectToScene(camera.gameObject, scene); camera.tag = "MainCamera";
                camera.transform.position = bounds.center + new Vector3(0, bounds.size.magnitude, -bounds.size.magnitude);
                camera.transform.LookAt(bounds.center); camera.farClipPlane = 2000;
                EditorSceneManager.MarkSceneDirty(scene);
                if (!hadUnsavedChanges && !EditorSceneManager.SaveScene(scene, path))
                    throw new IOException("Could not save VAT preview: " + path);
                if (hadUnsavedChanges)
                    Debug.Log("VAT Preview updated in the open scene. Existing unsaved edits were preserved; use Ctrl+S to save.");
                completed = true;
                Undo.CollapseUndoOperations(undoGroup);
            }
            finally
            {
                if (!completed) Undo.RevertAllDownToGroup(undoGroup);
                if (!reuse) EditorSceneManager.CloseScene(scene, true);
                if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                    SceneManager.SetActiveScene(previousActiveScene);
            }
        }
    }
}
