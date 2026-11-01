using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;

namespace RecoilPathfindingEditor
{
    // Bakes a character prefab's Idle/Run/Attack/Death states into a RecoilVatLibrary: a combined
    // static mesh plus a position texture (vertices tiled across TextureWidth columns and
    // RowsPerFrame rows per sampled frame, since a high-poly character's vertex count alone can
    // exceed Unity's 16384 texture-dimension limit), so RecoilVatCrowdRenderer can draw and animate
    // soldiers with GPU instancing instead of a live Animator per instance.
    public sealed class RecoilVatBakeWindow : EditorWindow
    {
        private const string OutputFolder = "Assets/Data/Characters/Vat";
        // Unity's hard cap on a single texture dimension (confirmed by the "max supported 16384"
        // error a high-poly character's raw vertex count can hit if used directly as texture width).
        private const int MaxTextureDimension = 16384;
        private static readonly string[] StateNames = { "Idle", "Run", "Attack", "Death" };

        private GameObject _sourcePrefab;
        private float _sampleFrameRate = 20f;
        private int _textureWidth = 4096;
        private readonly List<GameObject> _batch = new();
        private Vector2 _batchScroll;

        [MenuItem("Tools/Recoil Pathfinding/Bake VAT Crowd")]
        private static void Open()
        {
            var window = GetWindow<RecoilVatBakeWindow>();
            window.titleContent = new GUIContent("VAT Crowd Bake");
            window.minSize = new Vector2(380f, 260f);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Bakes Idle/Run/Attack/Death into a vertex-animation-texture for " +
                "RecoilVatCrowdRenderer (Assets/Data/Characters/Vat). The prefab needs an Animator " +
                "whose controller has states named exactly Idle/Run/Attack/Death. Skinned meshes " +
                "and animated MeshRenderer/MeshFilter parts are supported; each baked part needs " +
                "a single submesh.",
                MessageType.Info);
            _sampleFrameRate = EditorGUILayout.FloatField("Sample Frame Rate", _sampleFrameRate);
            _textureWidth = EditorGUILayout.IntField("Texture Width", _textureWidth);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Một prefab", EditorStyles.boldLabel);
            _sourcePrefab = (GameObject)EditorGUILayout.ObjectField(
                "Character Prefab", _sourcePrefab, typeof(GameObject), false);
            using (new EditorGUI.DisabledScope(_sourcePrefab == null))
                if (GUILayout.Button("Bake"))
                    Bake(_sourcePrefab, Mathf.Max(2f, _sampleFrameRate),
                        Mathf.Clamp(_textureWidth, 64, MaxTextureDimension));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Nhiều prefab (chọn hết trong Project rồi bake)", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Thêm từ lựa chọn đang chọn trong Project"))
                    AddSelectionToBatch();
                using (new EditorGUI.DisabledScope(_batch.Count == 0))
                    if (GUILayout.Button("Xoá danh sách", GUILayout.Width(100f)))
                        _batch.Clear();
            }
            if (_batch.Count > 0)
            {
                EditorGUILayout.LabelField($"Đã chọn {_batch.Count} prefab:");
                using (var scroll = new EditorGUILayout.ScrollViewScope(_batchScroll, GUILayout.MaxHeight(140f)))
                {
                    _batchScroll = scroll.scrollPosition;
                    for (var i = _batch.Count - 1; i >= 0; i--)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            EditorGUILayout.ObjectField(_batch[i], typeof(GameObject), false);
                            if (GUILayout.Button("x", GUILayout.Width(22f)))
                                _batch.RemoveAt(i);
                        }
                    }
                }
            }
            using (new EditorGUI.DisabledScope(_batch.Count == 0))
                if (GUILayout.Button($"Bake Tất Cả ({_batch.Count})"))
                    BakeBatch(_batch, Mathf.Max(2f, _sampleFrameRate),
                        Mathf.Clamp(_textureWidth, 64, MaxTextureDimension));
        }

        private void AddSelectionToBatch()
        {
            var added = 0;
            foreach (var selected in Selection.gameObjects)
            {
                if (selected == null || !PrefabUtility.IsPartOfPrefabAsset(selected)) continue;
                if (selected.GetComponentInChildren<Animator>(true) == null) continue;
                if (_batch.Contains(selected)) continue;
                _batch.Add(selected);
                added++;
            }
            if (added == 0)
                Debug.LogWarning("Không có prefab hợp lệ nào trong lựa chọn hiện tại của Project " +
                    "(cần là prefab asset có Animator).");
        }

        private static void BakeBatch(List<GameObject> prefabs, float sampleFrameRate, int textureWidth)
        {
            var succeeded = 0;
            var failed = new List<string>();
            try
            {
                for (var i = 0; i < prefabs.Count; i++)
                {
                    var prefab = prefabs[i];
                    if (prefab == null) continue;
                    if (EditorUtility.DisplayCancelableProgressBar("Bake VAT Crowd",
                            $"{prefab.name} ({i + 1}/{prefabs.Count})", (float)i / prefabs.Count))
                        break;
                    if (Bake(prefab, sampleFrameRate, textureWidth))
                        succeeded++;
                    else
                        failed.Add(prefab.name);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            Debug.Log($"Bake VAT Crowd hàng loạt xong: {succeeded}/{prefabs.Count} thành công." +
                (failed.Count > 0 ? $" Thất bại: {string.Join(", ", failed)} (xem lỗi ở trên)." : string.Empty));
        }

        private static bool Bake(GameObject sourcePrefab, float sampleFrameRate, int textureWidth)
        {
            var sourceAnimator = sourcePrefab.GetComponentInChildren<Animator>(true);
            var controller = sourceAnimator != null ? sourceAnimator.runtimeAnimatorController as AnimatorController : null;
            if (controller == null)
            {
                Debug.LogError("Prefab has no Animator with an AnimatorController.", sourcePrefab);
                return false;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab);
            var bakedMeshes = new List<Mesh>();
            try
            {
                // Apply the same normalizedVisualHeight/sourceModelHeight scale and ground-offset
                // RecoilCharacterPresentation.ApplyVisualSettings applies to visualRoot at runtime,
                // so baked VAT soldiers come out the same size/pose as their Animator-driven siblings
                // instead of whatever raw scale the prefab asset happens to be authored at.
                instance.GetComponentInChildren<RecoilCharacterPresentation>(true)?.ApplyVisualSettings();

                var renderers = GetBakeRenderers(instance);
                if (renderers.Length == 0)
                {
                    Debug.LogError(
                        "Prefab has no bakeable SkinnedMeshRenderer or MeshRenderer with a MeshFilter.",
                        sourcePrefab);
                    return false;
                }

                // BakeMesh(useScale:false) gives skinned vertices in renderer-local space. MeshRenderer
                // parts use their MeshFilter mesh directly; each sampled frame applies that renderer's
                // current transform so clips that animate rigid parts are baked as well.
                var rootRelative = new Matrix4x4[renderers.Length];
                var rootToWorld = instance.transform.worldToLocalMatrix;
                for (var r = 0; r < renderers.Length; r++)
                    rootRelative[r] = rootToWorld * renderers[r].transform.localToWorldMatrix;

                var clipInfos = new List<(string State, AnimationClip Clip, int FrameCount)>();
                foreach (var stateName in StateNames)
                {
                    var clip = FindClip(controller, stateName);
                    if (clip == null)
                    {
                        Debug.LogWarning($"No '{stateName}' state/clip found on {sourcePrefab.name}; skipped.", sourcePrefab);
                        continue;
                    }
                    var frames = Mathf.Max(2, Mathf.CeilToInt(clip.length * sampleFrameRate));
                    clipInfos.Add((stateName, clip, frames));
                }
                if (clipInfos.Count == 0)
                {
                    Debug.LogError("No bakeable Idle/Run/Attack/Death clips found.", sourcePrefab);
                    return false;
                }

                // Reference topology (triangles/uv/normals) comes from each renderer's bind pose —
                // topology stays fixed while skinned vertices and renderer transforms vary per frame.
                var referenceMeshes = new Mesh[renderers.Length];
                var totalVertexCount = 0;
                for (var r = 0; r < renderers.Length; r++)
                {
                    var sourceMesh = GetSharedMesh(renderers[r]);
                    if (sourceMesh == null || sourceMesh.subMeshCount != 1)
                    {
                        Debug.LogError($"'{renderers[r].name}' must have exactly one submesh for VAT " +
                            "baking (multi-submesh renderers are not supported by this tool).", renderers[r]);
                        return false;
                    }

                    if (renderers[r] is SkinnedMeshRenderer skinnedRenderer)
                    {
                        var baked = new Mesh();
                        skinnedRenderer.BakeMesh(baked, false);
                        bakedMeshes.Add(baked);
                        referenceMeshes[r] = baked;
                    }
                    else
                    {
                        referenceMeshes[r] = sourceMesh;
                    }

                    totalVertexCount += referenceMeshes[r].vertexCount;
                }

                if (totalVertexCount == 0)
                {
                    Debug.LogError("Prefab has no vertices to bake.", sourcePrefab);
                    return false;
                }

                var totalFrames = clipInfos.Sum(c => c.FrameCount);

                // Unity caps a texture dimension at MaxTextureDimension, which totalVertexCount
                // alone can exceed for a high-poly character, so vertices tile across
                // `width` columns and `rowsPerFrame` rows per baked frame instead of one texel
                // row per frame.
                var width = Mathf.Min(textureWidth, totalVertexCount);
                var rowsPerFrame = Mathf.CeilToInt((float)totalVertexCount / width);
                var height = rowsPerFrame * totalFrames;
                if (height > MaxTextureDimension)
                {
                    Debug.LogError($"Baked VAT texture would be {width}x{height}, exceeding Unity's " +
                        $"{MaxTextureDimension} texture dimension limit. Increase Texture Width or " +
                        "lower Sample Frame Rate.", sourcePrefab);
                    return false;
                }

                var pixels = new Color[width * height];
                var normalPixels = new Color[width * height];
                var clips = new RecoilVatLibrary.ClipEntry[clipInfos.Count];
                var frameIndex = 0;

                AnimationMode.StartAnimationMode();
                try
                {
                    for (var c = 0; c < clipInfos.Count; c++)
                    {
                        var (stateName, clip, frameCount) = clipInfos[c];
                        clips[c] = new RecoilVatLibrary.ClipEntry
                        {
                            State = stateName, RowOffset = frameIndex, FrameCount = frameCount,
                            Duration = clip.length, Loop = clip.isLooping
                        };
                        for (var f = 0; f < frameCount; f++)
                        {
                            // Looping clips sample a full cycle (f/frameCount); one-shot clips end
                            // exactly on the last frame (f/(frameCount-1)) so Death holds its pose.
                            var time = frameCount == 1 ? 0f : clip.isLooping
                                ? clip.length * f / frameCount
                                : clip.length * f / (frameCount - 1);
                            AnimationMode.BeginSampling();
                            AnimationMode.SampleAnimationClip(instance, clip, time);
                            AnimationMode.EndSampling();

                            var vertexOffset = 0;
                            for (var r = 0; r < renderers.Length; r++)
                            {
                                Mesh frame = null;
                                Mesh sampledMesh;
                                if (renderers[r] is SkinnedMeshRenderer skinnedRenderer)
                                {
                                    frame = new Mesh();
                                    skinnedRenderer.BakeMesh(frame, false);
                                    sampledMesh = frame;
                                }
                                else
                                {
                                    sampledMesh = referenceMeshes[r];
                                }

                                var verts = sampledMesh.vertices;
                                var normals = sampledMesh.normals;
                                var transform = instance.transform.worldToLocalMatrix *
                                    renderers[r].transform.localToWorldMatrix;
                                for (var v = 0; v < verts.Length; v++)
                                {
                                    var globalIndex = vertexOffset + v;
                                    var localRow = globalIndex / width;
                                    var localCol = globalIndex % width;
                                    var pixelIndex = (frameIndex * rowsPerFrame + localRow) * width + localCol;
                                    var point = transform.MultiplyPoint3x4(verts[v]);
                                    pixels[pixelIndex] = new Color(point.x, point.y, point.z, 1f);
                                    // MultiplyVector ignores translation; renderers only carry a
                                    // uniform scale (ApplyVisualSettings), so this stays a valid
                                    // direction transform without needing an inverse-transpose.
                                    var normal = v < normals.Length
                                        ? transform.MultiplyVector(normals[v]).normalized : Vector3.up;
                                    normalPixels[pixelIndex] = new Color(normal.x, normal.y, normal.z, 1f);
                                }
                                vertexOffset += verts.Length;
                                if (frame != null)
                                    Object.DestroyImmediate(frame);
                            }
                            frameIndex++;
                        }
                    }
                }
                finally
                {
                    AnimationMode.StopAnimationMode();
                }

                Directory.CreateDirectory(OutputFolder);
                var baseName = sourcePrefab.name;

                var texture = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true)
                {
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = baseName + "_VatPositions"
                };
                texture.SetPixels(pixels);
                texture.Apply(false, false);
                var texturePath = $"{OutputFolder}/{baseName}_VatPositions.asset";
                AssetDatabase.DeleteAsset(texturePath);
                AssetDatabase.CreateAsset(texture, texturePath);

                var normalTexture = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true)
                {
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = baseName + "_VatNormals"
                };
                normalTexture.SetPixels(normalPixels);
                normalTexture.Apply(false, false);
                var normalTexturePath = $"{OutputFolder}/{baseName}_VatNormals.asset";
                AssetDatabase.DeleteAsset(normalTexturePath);
                AssetDatabase.CreateAsset(normalTexture, normalTexturePath);

                var combinedMesh = BuildCombinedMesh(referenceMeshes, rootRelative, baseName);
                var meshPath = $"{OutputFolder}/{baseName}_VatMesh.asset";
                AssetDatabase.DeleteAsset(meshPath);
                AssetDatabase.CreateAsset(combinedMesh, meshPath);

                var library = ScriptableObject.CreateInstance<RecoilVatLibrary>();
                library.SourcePrefab = sourcePrefab;
                library.BakedMesh = combinedMesh;
                library.PositionTexture = texture;
                library.NormalTexture = normalTexture;
                library.VertexCount = totalVertexCount;
                library.TextureWidth = width;
                library.RowsPerFrame = rowsPerFrame;
                library.Clips = clips;
                library.SourceMaterials = renderers.Select(r => r.sharedMaterial).ToArray();
                var libraryPath = $"{OutputFolder}/{baseName}_VatLibrary.asset";
                AssetDatabase.DeleteAsset(libraryPath);
                AssetDatabase.CreateAsset(library, libraryPath);
                AssetDatabase.SaveAssets();

                // Destroy the sampling instance before editing+saving the same prefab asset below:
                // PrefabUtility.SaveAsPrefabAsset can silently fail to persist changes while a scene
                // instance connected to that exact prefab is still alive (this was the actual cause
                // of 13/14 baked libraries never reaching RecoilCharacterPresentation.vatLibrary).
                foreach (var mesh in bakedMeshes) Object.DestroyImmediate(mesh);
                bakedMeshes.Clear();
                Object.DestroyImmediate(instance);
                instance = null;

                AssignLibraryToPrefab(sourcePrefab, library);

                Debug.Log($"Baked VAT crowd library for '{baseName}': {totalVertexCount} vertices, " +
                    $"{totalFrames} frames across {clips.Length} clips, texture {width}x{height} " +
                    $"({rowsPerFrame} row(s)/frame).", library);
                return true;
            }
            finally
            {
                foreach (var mesh in bakedMeshes) Object.DestroyImmediate(mesh);
                if (instance != null) Object.DestroyImmediate(instance);
            }
        }

        // Writes the baked library back onto the prefab asset's RecoilCharacterPresentation.vatLibrary
        // so TryAttachFullDetailView picks it up next battle, without a manual Inspector step per
        // prefab. Edits the asset on disk (PrefabUtility.LoadPrefabContents), not the throwaway
        // instance Bake() used for sampling.
        private static void AssignLibraryToPrefab(GameObject sourcePrefab, RecoilVatLibrary library)
        {
            var assetPath = AssetDatabase.GetAssetPath(sourcePrefab);
            if (string.IsNullOrEmpty(assetPath)) return;

            var contents = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                var presentation = contents.GetComponentInChildren<RecoilCharacterPresentation>(true);
                if (presentation == null)
                {
                    Debug.LogWarning($"'{sourcePrefab.name}' has no RecoilCharacterPresentation; the " +
                        "baked library was saved but not wired up. Assign it to Vat Library manually.", sourcePrefab);
                    return;
                }
                var serialized = new SerializedObject(presentation);
                var property = serialized.FindProperty("vatLibrary");
                if (property == null)
                {
                    Debug.LogError($"'{sourcePrefab.name}': RecoilCharacterPresentation has no serialized " +
                        "'vatLibrary' field (field renamed/removed?). Could not wire up the baked library.", sourcePrefab);
                    return;
                }
                property.objectReferenceValue = library;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(presentation);
                PrefabUtility.SaveAsPrefabAsset(contents, assetPath, out var saved);
                if (!saved)
                {
                    Debug.LogError($"'{sourcePrefab.name}': PrefabUtility.SaveAsPrefabAsset reported failure " +
                        "while wiring up the baked VAT library.", sourcePrefab);
                    return;
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            // Verify on disk, independent of in-memory state above: load the asset fresh and read
            // back the field, so a silent persistence failure is caught here instead of only
            // discovered later at runtime via LogVatIneligibleOnce.
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            var reloadedPresentation = reloaded != null
                ? reloaded.GetComponentInChildren<RecoilCharacterPresentation>(true) : null;
            if (reloadedPresentation == null || reloadedPresentation.RawVatLibrary != library)
                Debug.LogError($"'{sourcePrefab.name}': Vat Library did not persist to the prefab asset " +
                    "after saving (verified by reloading it from disk). The baked library asset itself is " +
                    "fine; only the prefab's reference to it failed to save.", sourcePrefab);
        }

        private static AnimationClip FindClip(AnimatorController controller, string stateName)
        {
            foreach (var layer in controller.layers)
                foreach (var child in layer.stateMachine.states)
                    if (child.state.name == stateName)
                        return child.state.motion as AnimationClip;
            return null;
        }

        private static Renderer[] GetBakeRenderers(GameObject instance)
        {
            var allRenderers = instance.GetComponentsInChildren<Renderer>(true);
            var groupedRenderers = new HashSet<Renderer>();
            var lodZeroRenderers = new HashSet<Renderer>();
            foreach (var group in instance.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = group.GetLODs();
                foreach (var lod in lods)
                    foreach (var renderer in lod.renderers)
                        if (renderer != null)
                            groupedRenderers.Add(renderer);
                if (lods.Length == 0)
                    continue;
                foreach (var renderer in lods[0].renderers)
                    if (renderer != null)
                        lodZeroRenderers.Add(renderer);
            }

            var result = new List<Renderer>(allRenderers.Length);
            foreach (var renderer in allRenderers)
            {
                var supported = renderer is SkinnedMeshRenderer
                    ? ((SkinnedMeshRenderer)renderer).sharedMesh != null
                    : renderer is MeshRenderer && GetSharedMesh(renderer) != null;
                if (!supported)
                    continue;
                if (groupedRenderers.Contains(renderer) && !lodZeroRenderers.Contains(renderer))
                    continue;
                result.Add(renderer);
            }
            return result.ToArray();
        }

        private static Mesh GetSharedMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinnedRenderer)
                return skinnedRenderer.sharedMesh;
            return renderer is MeshRenderer
                ? renderer.GetComponent<MeshFilter>()?.sharedMesh
                : null;
        }

        private static Mesh BuildCombinedMesh(Mesh[] sourceMeshes, Matrix4x4[] rootRelative, string baseName)
        {
            var combine = new CombineInstance[sourceMeshes.Length];
            for (var r = 0; r < sourceMeshes.Length; r++)
                combine[r] = new CombineInstance { mesh = sourceMeshes[r], subMeshIndex = 0, transform = rootRelative[r] };

            var mesh = new Mesh { name = baseName + "_VatMesh", indexFormat = IndexFormat.UInt32 };
            // mergeSubMeshes:false keeps one submesh per renderer (so RecoilVatCrowdRenderer can draw
            // each with its own material); useMatrices:true applies each CombineInstance.transform
            // (the same root-relative matrix used when writing the position texture), so the base
            // topology lines up with the baked frames instead of sitting at each renderer's raw
            // local-space origin.
            mesh.CombineMeshes(combine, false, true);

            // Vertex positions came from the bind pose; the baked animation range can exceed that
            // box, so expand bounds generously instead of a mid-swing soldier getting culled early.
            var bounds = mesh.bounds;
            bounds.Expand(4f);
            mesh.bounds = bounds;
            return mesh;
        }
    }
}
