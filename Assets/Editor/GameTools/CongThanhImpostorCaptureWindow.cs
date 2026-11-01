using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace GameToolsEditor
{
    public sealed class CongThanhImpostorCaptureWindow : EditorWindow
    {
        private const string PrefabFolder = "Assets/Data/Characters/Prefabs";
        private const string OutputFolder = "Assets/Resources/CharacterImpostors";
        private const string CaptureSceneFolder = "Assets/Editor/GameTools/Scenes";
        private const string CaptureScenePath = CaptureSceneFolder + "/CongThanhImpostorCapture.unity";
        private const string CaptureRigName = "CongThanh Impostor Capture Rig";
        private const string CaptureCameraName = "Impostor Capture Camera";
        private const string CaptureLightName = "Impostor Capture Light";
        private const int CaptureLayer = 31;
        private static readonly string[] StateNames = { "Idle", "Run", "Attack", "Death" };
        private static readonly string[][] StateAliases =
        {
            new[] { "idle", "stand" },
            new[] { "run", "walk" },
            new[] { "attack", "scene" },
            new[] { "death", "die" }
        };

        private readonly List<string> _prefabPaths = new();
        private string _status;
        private bool _hasScanned;
        private bool _isConverting;
        private Vector2 _statusScroll;

        [MenuItem("Game Tools/Công Thành/Bake impostor sprite sheets")]
        private static void Open()
        {
            GetWindow<CongThanhImpostorCaptureWindow>("Impostor Capture").minSize = new Vector2(510f, 570f);
        }

        private void OnEnable() => ScanPrefabs();

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Convert prefab thành impostor", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Convert sẽ tự quét toàn bộ prefab, bake các trạng thái Idle/Run/Attack/Death " +
                "(Attack cũng nhận clip Scene), chụp 8 hướng ngang và 8 hướng từ trên xuống " +
                "cho mỗi frame. Tool chỉ chụp renderer LOD0 trên layer riêng và căn tâm theo xương hông/root bone, " +
                "rồi lưu atlas và library vào Resources để game sử dụng. " +
                "Nếu chưa có scene chụp, tool sẽ tự tạo và cấu hình scene trước khi bake.",
                MessageType.Info);

            EditorGUILayout.LabelField($"Prefab sẽ quét: {_prefabPaths.Count}");
            EditorGUI.BeginDisabledGroup(_isConverting);
            if (GUILayout.Button(_isConverting ? "Đang convert..." : "Convert", GUILayout.Height(40f)))
            {
                _isConverting = true;
                _status = "Đang mở scene chụp và chuẩn bị bake...";
                EditorApplication.delayCall += BakeAllAfterGui;
            }
            EditorGUI.EndDisabledGroup();

            if (!_hasScanned || _prefabPaths.Count == 0)
            {
                EditorGUILayout.HelpBox($"Không tìm thấy prefab trong {PrefabFolder}.", MessageType.Warning);
                return;
            }

            if (!string.IsNullOrEmpty(_status))
            {
                EditorGUILayout.LabelField("Kết quả", EditorStyles.boldLabel);
                _statusScroll = EditorGUILayout.BeginScrollView(_statusScroll, GUILayout.MinHeight(100f));
                EditorGUILayout.HelpBox(_status, MessageType.Info);
                EditorGUILayout.EndScrollView();
            }
        }

        private void BakeAllAfterGui()
        {
            try
            {
                BakeAll();
            }
            finally
            {
                _isConverting = false;
                Repaint();
            }
        }

        private void ScanPrefabs()
        {
            _prefabPaths.Clear();
            _prefabPaths.AddRange(AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            _hasScanned = true;
        }

        private static RecoilImpostorSpriteLibrary LoadOrCreateLibrary(GameObject prefab, bool create)
        {
            if (prefab == null) return null;
            var path = LibraryPath(prefab);
            var library = AssetDatabase.LoadAssetAtPath<RecoilImpostorSpriteLibrary>(path);
            if (library != null || !create) return library;
            Directory.CreateDirectory(OutputFolder);
            AssetDatabase.Refresh();
            library = CreateInstance<RecoilImpostorSpriteLibrary>();
            library.SourcePrefab = prefab;
            AssetDatabase.CreateAsset(library, path);
            return library;
        }

        private static string LibraryPath(GameObject prefab) =>
            $"{OutputFolder}/{Sanitize(prefab.name)}_Impostor.asset";

        private static string Sanitize(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
                value = value.Replace(invalid, '_');
            return value;
        }

        private sealed class CaptureTarget
        {
            public string State;
            public string Description;
            public bool Loop;
            public float Duration;
            public readonly List<(string animatorPath, AnimationClip clip)> Sources = new();
        }

        private sealed class BoundCaptureTarget
        {
            public CaptureTarget Target;
            public readonly List<(Animator animator, AnimationClip clip)> Sources = new();
        }

        private sealed class CaptureSceneContext : IDisposable
        {
            public readonly Scene Scene;
            public readonly Camera Camera;
            private RenderTexture _renderTexture;

            public CaptureSceneContext(Scene scene, Camera camera)
            {
                Scene = scene;
                Camera = camera;
            }

            public RenderTexture GetRenderTexture(int width, int height)
            {
                if (_renderTexture != null && _renderTexture.width == width && _renderTexture.height == height)
                    return _renderTexture;
                ReleaseRenderTexture();
                _renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                {
                    name = "Impostor Capture Render Texture",
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                if (!_renderTexture.Create())
                {
                    ReleaseRenderTexture();
                    throw new InvalidOperationException("Không thể tạo RenderTexture để chụp impostor.");
                }
                Camera.targetTexture = _renderTexture;
                return _renderTexture;
            }

            public void Dispose()
            {
                Camera.targetTexture = null;
                ReleaseRenderTexture();
            }

            private void ReleaseRenderTexture()
            {
                if (_renderTexture == null) return;
                _renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(_renderTexture);
                _renderTexture = null;
            }
        }

        private static List<CaptureTarget> FindCaptureTargets(GameObject prefab)
        {
            var result = new List<CaptureTarget>();
            if (prefab == null) return result;
            for (var stateIndex = 0; stateIndex < StateNames.Length; stateIndex++)
            {
                var target = new CaptureTarget { State = StateNames[stateIndex] };
                var descriptions = new List<string>();
                foreach (var animator in prefab.GetComponentsInChildren<Animator>(true))
                {
                    if (animator.runtimeAnimatorController == null) continue;
                    var clips = animator.runtimeAnimatorController.animationClips;
                    var matches = clips.Where(clip => clip != null && MatchesState(clip.name, stateIndex))
                        .OrderBy(clip => MatchRank(clip.name, stateIndex))
                        .ThenBy(clip => clip.name, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    if (matches.Length == 0) continue;
                    var clip = matches[0];
                    target.Sources.Add((AnimationUtility.CalculateTransformPath(animator.transform, prefab.transform), clip));
                    target.Duration = Mathf.Max(target.Duration, clip.length);
                    target.Loop |= clip.isLooping;
                    descriptions.Add(clip.name);
                }

                if (target.Sources.Count == 0)
                {
                    if (stateIndex != 0 || prefab.GetComponentInChildren<Renderer>(true) == null)
                        continue;
                    target.Duration = 1f;
                    target.Loop = true;
                    target.Description = "Bind pose";
                    result.Add(target);
                    continue;
                }
                target.Description = string.Join(", ", descriptions.Distinct());
                result.Add(target);
            }
            return result;
        }

        private static bool MatchesState(string clipName, int stateIndex)
        {
            var normalized = Normalize(clipName);
            return StateAliases[stateIndex].Any(alias => normalized.Contains(alias));
        }

        private static int MatchRank(string clipName, int stateIndex)
        {
            var normalized = Normalize(clipName);
            var preferred = StateAliases[stateIndex][0];
            return normalized.Equals(preferred, StringComparison.Ordinal) ? 0 :
                normalized.StartsWith(preferred, StringComparison.Ordinal) ? 1 :
                normalized.Contains(preferred) ? 2 : 3;
        }

        private static string Normalize(string value) =>
            new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        private void BakeAll()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                _status = "Hãy dừng Play Mode trước khi bake.";
                return;
            }

            ScanPrefabs();
            if (_prefabPaths.Count == 0)
            {
                _status = $"Không tìm thấy prefab trong {PrefabFolder}.";
                return;
            }
            Directory.CreateDirectory(OutputFolder);
            var report = new List<string>();
            var total = 0;
            var successful = 0;
            CaptureSceneContext captureScene = null;
            try
            {
                captureScene = OpenCaptureScene();
                for (var prefabIndex = 0; prefabIndex < _prefabPaths.Count; prefabIndex++)
                {
                    var path = _prefabPaths[prefabIndex];
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null)
                    {
                        report.Add($"{path}: prefab không tải được.");
                        continue;
                    }

                    try
                    {
                        var library = LoadOrCreateLibrary(prefab, true);
                        var calibration = ReadCalibration(library);
                        var targets = FindCaptureTargets(prefab);
                        if (targets.Count == 0)
                        {
                            report.Add($"{prefab.name}: bỏ qua — không tìm thấy clip Idle/Run/Attack/Death/Scene.");
                            continue;
                        }

                        var sheets = new List<RecoilImpostorSpriteLibrary.AnimationSheet>();
                        var maxWorldWidth = 0f;
                        var maxWorldHeight = 0f;
                        foreach (var target in targets)
                        {
                            total++;
                            var atlas = BakeTarget(prefab, target, calibration, captureScene,
                                prefabIndex, _prefabPaths.Count, out var worldSize);
                            if (atlas == null) continue;
                            maxWorldWidth = Mathf.Max(maxWorldWidth, worldSize.x);
                            maxWorldHeight = Mathf.Max(maxWorldHeight, worldSize.y);
                            sheets.Add(new RecoilImpostorSpriteLibrary.AnimationSheet
                            {
                                State = target.State,
                                SourceClip = target.Description,
                                Atlas = atlas,
                                Frames = calibration.Frames,
                                Duration = target.Duration,
                                Loop = target.Loop
                            });
                            successful++;
                        }

                        library.SourcePrefab = prefab;
                        library.Animations = sheets.ToArray();
                        if (sheets.Count > 0)
                        {
                            library.WorldWidth = maxWorldWidth;
                            library.WorldHeight = maxWorldHeight;
                        }
                        EditorUtility.SetDirty(library);
                        report.Add($"{prefab.name}: {sheets.Count} trạng thái.");
                    }
                    catch (OperationCanceledException exception)
                    {
                        report.Add(exception.Message);
                        break;
                    }
                    catch (Exception exception)
                    {
                        report.Add($"{prefab.name}: LỖI — {exception.Message}");
                        Debug.LogException(exception);
                    }
                }
                ShowCapturePreview(captureScene);
            }
            catch (Exception exception)
            {
                report.Add($"Không thể thiết lập scene chụp impostor: {exception.Message}");
                Debug.LogException(exception);
            }
            finally
            {
                captureScene?.Dispose();
                if (captureScene != null)
                {
                    var preview = captureScene.Scene.GetRootGameObjects()
                        .FirstOrDefault(root => root.name.StartsWith("Capture Preview - ", StringComparison.Ordinal));
                    if (preview != null)
                        Selection.activeGameObject = preview;
                    var sceneView = EditorWindow.GetWindow<SceneView>();
                    sceneView.FrameSelected();
                }
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            _status = $"Convert hoàn tất: {successful}/{total} atlas, {_prefabPaths.Count} prefab.\n" +
                      $"Đã lưu atlas tại {OutputFolder}.\n" +
                      $"Scene chụp: {CaptureScenePath}.\n" +
                      string.Join("\n", report);
            Debug.Log("Convert impostor hoàn tất:\n" + _status);
        }

        private readonly struct Calibration
        {
            public readonly int Width, Height, Frames;
            public readonly float Elevation, Scale;
            public readonly Vector3 Offset;
            public Calibration(int width, int height, int frames, float elevation, float scale, Vector3 offset)
            {
                Width = width; Height = height; Frames = frames;
                Elevation = elevation; Scale = scale; Offset = offset;
            }
        }

        private static Calibration ReadCalibration(RecoilImpostorSpriteLibrary library) =>
            new Calibration(library.FrameWidth, library.FrameHeight, library.FramesPerState,
                library.ElevatedViewAngle, library.OrthographicScale, library.CaptureOffset);

        private Texture2D BakeTarget(GameObject prefab, CaptureTarget target, Calibration calibration,
            CaptureSceneContext captureScene, int prefabIndex, int prefabCount, out Vector2 worldSize)
        {
            worldSize = Vector2.one;
            var atlas = new Texture2D(calibration.Width * 16, calibration.Height * calibration.Frames,
                TextureFormat.RGBA32, false, false) { name = $"{prefab.name}_{target.State}_Atlas" };
            GameObject instance = null;
            try
            {
                instance = PrefabUtility.InstantiatePrefab(prefab, captureScene.Scene) as GameObject;
                if (instance == null)
                    throw new InvalidOperationException($"Không thể khởi tạo prefab '{prefab.name}' trong scene chụp.");
                PrepareInstance(instance);
                foreach (var lodGroup in instance.GetComponentsInChildren<LODGroup>(true))
                    lodGroup.ForceLOD(0);
                var renderers = PrepareCaptureRenderers(instance);
                SetLayerRecursively(instance.transform, CaptureLayer);
                var boundTarget = BindTarget(target, instance);
                var camera = captureScene.Camera;
                var renderTexture = captureScene.GetRenderTexture(calibration.Width, calibration.Height);
                SampleTarget(boundTarget, 0f);
                var bounds = GetBounds(renderers, instance.name);
                worldSize = new Vector2(Mathf.Max(bounds.size.x, bounds.size.z), bounds.size.y);
                var capturePivot = GetCapturePivot(renderers, boundTarget, instance.transform);

                for (var frame = 0; frame < calibration.Frames; frame++)
                {
                    var normalizedTime = target.Loop
                        ? frame / (float)calibration.Frames
                        : frame / (float)Mathf.Max(1, calibration.Frames - 1);
                    SampleTarget(boundTarget, normalizedTime);
                    for (var view = 0; view < 16; view++)
                    {
                        if (EditorUtility.DisplayCancelableProgressBar(
                                $"Baking {prefab.name}", $"{target.State}: frame {frame + 1}/{calibration.Frames}, view {view + 1}/16",
                                (prefabIndex + (frame + (float)view / 16f) / calibration.Frames) / prefabCount))
                            throw new OperationCanceledException("Bake dibatalkan.");
                        PositionCamera(camera, bounds, calibration, capturePivot.position, instance.transform, view);
                        RenderCapture(camera, renderTexture);
                        var pixels = ReadPixels(renderTexture, calibration.Width, calibration.Height);
                        if (frame == 0 && view == 0 && !pixels.Any(pixel => pixel.a > 0.01f))
                            throw new InvalidOperationException(
                                $"Ảnh chụp {target.State} ({target.Description}), frame {frame + 1}, hướng {view + 1} " +
                                "trong suốt hoàn toàn. Kiểm tra camera, renderer, material và render pipeline.");
                        atlas.SetPixels(view * calibration.Width, frame * calibration.Height,
                            calibration.Width, calibration.Height, pixels);
                    }
                }
                atlas.Apply(false, false);
                var atlasPath = $"{OutputFolder}/{Sanitize(prefab.name)}_{Sanitize(target.State)}.png";
                File.WriteAllBytes(atlasPath, atlas.EncodeToPNG());
                DestroyImmediate(atlas);
                atlas = null;
                AssetDatabase.ImportAsset(atlasPath, ImportAssetOptions.ForceUpdate);
                var importer = (TextureImporter)AssetImporter.GetAtPath(atlasPath);
                importer.textureType = TextureImporterType.Default;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.maxTextureSize = Mathf.NextPowerOfTwo(Mathf.Max(calibration.Width * 16,
                    calibration.Height * calibration.Frames));
                importer.SaveAndReimport();
                return AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);
            }
            catch
            {
                if (atlas != null) DestroyImmediate(atlas);
                throw;
            }
            finally
            {
                if (instance != null) DestroyImmediate(instance);
            }
        }

        private static CaptureSceneContext OpenCaptureScene()
        {
            EnsureCaptureSceneAsset();
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                throw new OperationCanceledException("Đã hủy chuyển sang scene chụp.");
            var scene = EditorSceneManager.OpenScene(CaptureScenePath, OpenSceneMode.Single);
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException($"Không thể mở scene chụp '{CaptureScenePath}'.");

            EditorSceneManager.SetActiveScene(scene);
            var camera = ConfigureCaptureScene(scene);
            if (scene.isDirty && !EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Không thể lưu cấu hình scene chụp '{CaptureScenePath}'.");
            return new CaptureSceneContext(scene, camera);
        }

        private void ShowCapturePreview(CaptureSceneContext captureScene)
        {
            if (captureScene == null) return;
            foreach (var root in captureScene.Scene.GetRootGameObjects())
                if (root.name.StartsWith("Capture Preview - ", StringComparison.Ordinal))
                    DestroyImmediate(root);

            var prefab = _prefabPaths.Select(AssetDatabase.LoadAssetAtPath<GameObject>)
                .FirstOrDefault(candidate => candidate != null);
            if (prefab == null) return;

            var instance = PrefabUtility.InstantiatePrefab(prefab, captureScene.Scene) as GameObject;
            if (instance == null)
                throw new InvalidOperationException($"Không thể tạo preview cho '{prefab.name}' trong scene chụp.");
            instance.name = $"Capture Preview - {prefab.name}";
            PrepareInstance(instance);
            foreach (var lodGroup in instance.GetComponentsInChildren<LODGroup>(true))
                lodGroup.ForceLOD(0);

            var renderers = PrepareCaptureRenderers(instance);
            SetLayerRecursively(instance.transform, CaptureLayer);
            var bounds = GetBounds(renderers, instance.name);
            var library = LoadOrCreateLibrary(prefab, false);
            var calibration = ReadCalibration(library);
            var pivot = GetCapturePivot(renderers, null, instance.transform);
            PositionCamera(captureScene.Camera, bounds, calibration, pivot.position, instance.transform, 0);
            if (!EditorSceneManager.SaveScene(captureScene.Scene))
                throw new InvalidOperationException($"Không thể lưu prefab preview vào '{CaptureScenePath}'.");
        }

        private static void EnsureCaptureSceneAsset()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(CaptureScenePath) != null)
                return;

            Directory.CreateDirectory(CaptureSceneFolder);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            EditorSceneManager.SetActiveScene(scene);
            ConfigureCaptureScene(scene);
            if (!EditorSceneManager.SaveScene(scene, CaptureScenePath))
                throw new InvalidOperationException($"Không thể tạo scene chụp tại '{CaptureScenePath}'.");
            AssetDatabase.Refresh();
        }

        private static Camera ConfigureCaptureScene(Scene scene)
        {
            var rig = scene.GetRootGameObjects().FirstOrDefault(root => root.name == CaptureRigName);
            if (rig == null)
            {
                rig = new GameObject(CaptureRigName);
                SceneManager.MoveGameObjectToScene(rig, scene);
            }

            var cameraTransform = rig.transform.Find(CaptureCameraName);
            var cameraObject = cameraTransform != null ? cameraTransform.gameObject : new GameObject(CaptureCameraName);
            if (cameraTransform == null)
                cameraObject.transform.SetParent(rig.transform, false);
            var camera = cameraObject.GetComponent<Camera>();
            if (camera == null) camera = cameraObject.AddComponent<Camera>();
            camera.enabled = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.cullingMask = 1 << CaptureLayer;
            camera.orthographic = true;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 500f;
            camera.allowHDR = false;
            camera.allowMSAA = false;

            var lightTransform = rig.transform.Find(CaptureLightName);
            var lightObject = lightTransform != null ? lightTransform.gameObject : new GameObject(CaptureLightName);
            if (lightTransform == null)
                lightObject.transform.SetParent(rig.transform, false);
            var light = lightObject.GetComponent<Light>();
            if (light == null) light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.cullingMask = 1 << CaptureLayer;
            lightObject.transform.rotation = Quaternion.Euler(35f, -35f, 0f);
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
            EditorSceneManager.MarkSceneDirty(scene);
            return camera;
        }

        private static void PrepareInstance(GameObject instance)
        {
            foreach (var behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true))
                behaviour.enabled = false;
            foreach (var presentation in instance.GetComponentsInChildren<RecoilCharacterPresentation>(true))
                presentation.ApplyVisualSettings();
            foreach (var animator in instance.GetComponentsInChildren<Animator>(true))
                animator.enabled = false;
        }

        private static BoundCaptureTarget BindTarget(CaptureTarget target, GameObject instance)
        {
            var bound = new BoundCaptureTarget { Target = target };
            foreach (var source in target.Sources)
            {
                var animatorTransform = string.IsNullOrEmpty(source.animatorPath)
                    ? instance.transform
                    : instance.transform.Find(source.animatorPath);
                var animator = animatorTransform != null ? animatorTransform.GetComponent<Animator>() : null;
                if (animator == null)
                    throw new InvalidOperationException(
                        $"Không tìm thấy Animator clone theo đường dẫn '{source.animatorPath}' cho clip '{source.clip.name}'.");
                bound.Sources.Add((animator, source.clip));
            }
            return bound;
        }

        private static void SampleTarget(BoundCaptureTarget target, float normalizedTime)
        {
            foreach (var source in target.Sources)
                if (source.animator != null && source.clip != null)
                {
                    var rootPosition = source.animator.transform.localPosition;
                    source.clip.SampleAnimation(source.animator.gameObject,
                        Mathf.Clamp01(normalizedTime) * source.clip.length);
                    source.animator.transform.localPosition = rootPosition;
                }
        }

        private static Renderer[] PrepareCaptureRenderers(GameObject instance)
        {
            var allRenderers = instance.GetComponentsInChildren<Renderer>(true);
            var lodGroups = instance.GetComponentsInChildren<LODGroup>(true);
            var lodZeroRenderers = lodGroups.Length == 0
                ? allRenderers
                : lodGroups.SelectMany(group =>
                {
                    var lods = group.GetLODs();
                    return lods.Length > 0 ? lods[0].renderers : Array.Empty<Renderer>();
                });
            var selected = new HashSet<Renderer>(lodZeroRenderers.Where(renderer =>
                renderer != null && renderer.sharedMaterial != null));
            foreach (var renderer in allRenderers)
                if (renderer != null)
                    renderer.enabled = selected.Contains(renderer);
            var renderers = selected.ToArray();
            if (renderers.Length == 0)
                throw new InvalidOperationException(
                    $"'{instance.name}' không có Renderer LOD0 hợp lệ để chụp.");
            return renderers;
        }

        private static Bounds GetBounds(Renderer[] renderers, string instanceName)
        {
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            if (bounds.size.sqrMagnitude < 0.0001f)
                throw new InvalidOperationException($"Không đo được bounds LOD0 cho '{instanceName}'.");
            return bounds;
        }

        private static Transform GetCapturePivot(Renderer[] renderers, BoundCaptureTarget target,
            Transform instanceRoot)
        {
            if (target != null)
            {
                foreach (var source in target.Sources)
                    if (source.animator != null && source.animator.isHuman)
                    {
                        var hips = source.animator.GetBoneTransform(HumanBodyBones.Hips);
                        if (hips != null) return hips;
                    }
            }

            foreach (var animator in instanceRoot.GetComponentsInChildren<Animator>(true))
                if (animator != null && animator.isHuman)
                {
                    var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
                    if (hips != null) return hips;
                }

            foreach (var renderer in renderers)
                if (renderer is SkinnedMeshRenderer skinned && skinned.rootBone != null)
                    return skinned.rootBone;

            if (target != null)
                foreach (var source in target.Sources)
                    if (source.animator != null)
                        return source.animator.transform;

            return instanceRoot;
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            root.gameObject.layer = layer;
            for (var i = 0; i < root.childCount; i++)
                SetLayerRecursively(root.GetChild(i), layer);
        }

        private static void PositionCamera(Camera camera, Bounds bounds, Calibration calibration,
            Vector3 pivot, Transform root, int view)
        {
            var elevated = view >= 8;
            var azimuth = (view % 8) * 45f;
            var elevation = elevated ? calibration.Elevation : 0f;
            var radians = azimuth * Mathf.Deg2Rad;
            var pitch = elevation * Mathf.Deg2Rad;
            var direction = new Vector3(Mathf.Sin(radians) * Mathf.Cos(pitch), Mathf.Sin(pitch),
                Mathf.Cos(radians) * Mathf.Cos(pitch)).normalized;
            var center = pivot + root.TransformVector(calibration.Offset);
            var extents = bounds.extents;
            var radius = Mathf.Max(extents.magnitude, 0.25f) * 2.5f;
            camera.aspect = calibration.Width / (float)calibration.Height;
            camera.transform.position = center + direction * radius;
            camera.transform.rotation = Quaternion.LookRotation(-direction, Vector3.up);

            var right = camera.transform.right;
            var up = camera.transform.up;
            var horizontalExtent = 0f;
            var verticalExtent = 0f;
            for (var corner = 0; corner < 8; corner++)
            {
                var point = bounds.center + Vector3.Scale(extents, new Vector3(
                    (corner & 1) == 0 ? -1f : 1f,
                    (corner & 2) == 0 ? -1f : 1f,
                    (corner & 4) == 0 ? -1f : 1f));
                var relative = point - center;
                horizontalExtent = Mathf.Max(horizontalExtent, Mathf.Abs(Vector3.Dot(relative, right)));
                verticalExtent = Mathf.Max(verticalExtent, Mathf.Abs(Vector3.Dot(relative, up)));
            }

            var halfHeight = Mathf.Max(verticalExtent, horizontalExtent / camera.aspect);
            camera.orthographicSize = Mathf.Max(halfHeight, 0.1f) *
                                      1.15f / Mathf.Max(0.1f, calibration.Scale);
        }

        private static Color[] ReadPixels(RenderTexture source, int width, int height)
        {
            var previous = RenderTexture.active;
            Texture2D image = null;
            try
            {
                RenderTexture.active = source;
                image = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                image.Apply(false, false);
                return image.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                if (image != null) DestroyImmediate(image);
            }
        }

        private static void RenderCapture(Camera camera, RenderTexture destination)
        {
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                camera.targetTexture = destination;
                camera.Render();
                return;
            }

            var request = new RenderPipeline.StandardRequest
            {
                destination = destination,
                mipLevel = 0,
                slice = 0,
                face = CubemapFace.Unknown
            };
            if (!RenderPipeline.SupportsRenderRequest(camera, request))
                throw new InvalidOperationException(
                    $"Render pipeline '{GraphicsSettings.currentRenderPipeline.name}' không hỗ trợ chụp camera.");
            RenderPipeline.SubmitRenderRequest(camera, request);
        }

    }
}
