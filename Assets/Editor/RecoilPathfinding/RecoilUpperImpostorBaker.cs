using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace RecoilPathfindingEditor
{
    internal static class RecoilUpperImpostorBaker
    {
        private const string Folder = "Assets/Data/Characters/UpperImpostors";
        private const string Prefabs = "Assets/Data/Characters/Prefabs";
        private const string ChildName = "LOD4_UpperImpostor";
        private const string ShaderName = "Recoil/Upper Hemisphere Impostor";
        private static readonly float[] Elevations = { 30f, 50f, 70f, 90f };

        // Automatic baking is suspended while impostors are temporarily disabled.

        private static void PrepareMissing()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            var paths = AssetDatabase.FindAssets("t:Prefab", new[] { Prefabs }).Select(AssetDatabase.GUIDToAssetPath).ToArray();
            if (paths.Length == 0 || paths.All(HasValidImpostor)) return;
            try { BakeAll(); } catch (Exception exception) { Debug.LogException(exception); }
        }

        private static bool HasValidImpostor(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var group = prefab != null ? prefab.GetComponentInChildren<LODGroup>(true) : null;
            if (group == null) return false;
            var lods = group.GetLODs();
            return lods.Length == 4 && lods[3].renderers.Any(r => r != null && r.name == ChildName &&
                    r.sharedMaterial != null && r.sharedMaterial.HasProperty("_BakeVersion") &&
                    r.sharedMaterial.GetFloat("_BakeVersion") >= 6f && r.sharedMaterial.HasProperty("_FootHeight") &&
                    r.sharedMaterial.GetFloat("_FootHeight") > 0f) &&
                lods[0].screenRelativeTransitionHeight > lods[1].screenRelativeTransitionHeight &&
                lods[1].screenRelativeTransitionHeight > lods[2].screenRelativeTransitionHeight &&
                lods[2].screenRelativeTransitionHeight > lods[3].screenRelativeTransitionHeight;
        }

        [MenuItem("Game Tools/Công Thành/Bake impostor góc trên và gắn LOD4")]
        public static void BakeAll()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Dừng Play Mode trước khi bake impostor.");
            var shader = Shader.Find(ShaderName);
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Upper impostor shader is unavailable.");
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/Data/Characters", "UpperImpostors");
            var paths = AssetDatabase.FindAssets("t:Prefab", new[] { Prefabs }).Select(AssetDatabase.GUIDToAssetPath).ToArray();
            try
            {
                for (var i = 0; i < paths.Length; i++)
                {
                    EditorUtility.DisplayProgressBar("Bake LOD4 impostor", Path.GetFileNameWithoutExtension(paths[i]), (float)i / paths.Length);
                    Bake(paths[i], shader);
                }
                AssetDatabase.SaveAssets();
                Debug.Log($"Upper impostors: prepared {paths.Length} prefabs, 8 directions × 4 upper elevations × 3 animations × 4 frames, assigned LOD4.");
            }
            finally { EditorUtility.ClearProgressBar(); }
        }

        private static void Bake(string path, Shader shader)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            var scene = EditorSceneManager.NewPreviewScene();
            RenderTexture target = null;
            Texture2D atlas = null, tile = null;
            var previousTarget = RenderTexture.active;
            try
            {
                var group = root.GetComponentInChildren<LODGroup>(true);
                if (group == null || group.GetLODs().Length < 3) throw new InvalidOperationException($"{path}: cần LOD1/2/3 trước khi bake.");
                var original = group.GetLODs();
                var clone = UnityEngine.Object.Instantiate(root);
                SceneManager.MoveGameObjectToScene(clone, scene);
                // Isolate both native renderers and custom scene-wide instanced draws.
                foreach (var child in clone.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 31;
                clone.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                clone.transform.localScale = Vector3.one;
                // Instantiate adds "(Clone)" to the root name. Match the component's
                // hierarchy order instead; duplicate child names are also valid.
                var sourceGroups = root.GetComponentsInChildren<LODGroup>(true);
                var cloneGroups = clone.GetComponentsInChildren<LODGroup>(true);
                var groupIndex = Array.IndexOf(sourceGroups, group);
                if (groupIndex < 0 || groupIndex >= cloneGroups.Length)
                    throw new InvalidOperationException($"{path}: cloned LOD hierarchy does not match the source prefab.");
                var cloneGroup = cloneGroups[groupIndex];
                foreach (var lodGroup in clone.GetComponentsInChildren<LODGroup>(true)) lodGroup.ForceLOD(0);
                var animations = new Dictionary<Animator, AnimationClip[]>();
                var animationRoots = new Dictionary<Transform, (Vector3 position, Quaternion rotation, Vector3 scale)>();
                var restPose = clone.GetComponentsInChildren<Transform>(true).Select(t =>
                    (transform: t, position: t.localPosition, rotation: t.localRotation, scale: t.localScale)).ToArray();
                var presentation = clone.GetComponentInChildren<RecoilPathfinding.RecoilCharacterPresentation>(true);
                foreach (var animator in clone.GetComponentsInChildren<Animator>(true))
                {
                    var clips = animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.animationClips : Array.Empty<AnimationClip>();
                    var idle = FindStateClip(animator, "Idle") ?? clips.FirstOrDefault(clip => clip.name.IndexOf("idle", StringComparison.OrdinalIgnoreCase) >= 0) ?? clips.FirstOrDefault();
                    var walk = FindStateClip(animator, "Run") ?? FindStateClip(animator, "Walk") ?? clips.FirstOrDefault(clip => clip.name.IndexOf("walk", StringComparison.OrdinalIgnoreCase) >= 0) ??
                        clips.FirstOrDefault(clip => clip.name.IndexOf("run", StringComparison.OrdinalIgnoreCase) >= 0) ?? idle;
                    var attack = FindStateClip(animator, "Attack") ??
                        (presentation != null && presentation.AttackClip != null && clips.Contains(presentation.AttackClip) ? presentation.AttackClip : null) ??
                        clips.FirstOrDefault(clip => clip.name.IndexOf("attack", StringComparison.OrdinalIgnoreCase) >= 0) ?? idle;
                    animations[animator] = new[] { idle, walk, attack };
                    var animationRoot = animator.transform;
                    animationRoots[animationRoot] = (animationRoot.localPosition, animationRoot.localRotation, animationRoot.localScale);
                    animator.applyRootMotion = false;
                    animator.enabled = false;
                    if (idle == null || walk == idle || attack == idle)
                        Debug.LogWarning($"{path}: animation clip missing; affected impostor animation uses the idle clip.");
                }
                void SampleFrame(int bank, int frame)
                {
                    // Clips need not key every bone. Reset between samples so rider/horse
                    // poses and scale cannot leak from the previous animation bank.
                    foreach (var pose in restPose)
                    {
                        pose.transform.SetLocalPositionAndRotation(pose.position, pose.rotation);
                        pose.transform.localScale = pose.scale;
                    }
                    foreach (var pair in animations)
                    {
                        var clip = pair.Value[bank];
                        if (clip != null) clip.SampleAnimation(pair.Key.gameObject, clip.length * frame / 4f);
                    }
                    // SampleAnimation can write imported root-motion/scale keys. Runtime
                    // presentation keeps these roots normalized; bake must do the same.
                    foreach (var pair in animationRoots)
                    {
                        pair.Key.SetLocalPositionAndRotation(pair.Value.position, pair.Value.rotation);
                        pair.Key.localScale = pair.Value.scale;
                    }
                    clone.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                }
                SampleFrame(0, 0);
                var visible = cloneGroup.GetLODs()[0].renderers.Where(r => r != null && r.enabled && r.gameObject.activeInHierarchy).ToArray();
                if (visible.Length == 0) throw new InvalidOperationException($"{path}: LOD1 has no visible renderers.");
                var allowed = visible.ToHashSet();
                foreach (var renderer in clone.GetComponentsInChildren<Renderer>(true)) renderer.enabled = allowed.Contains(renderer);
                var bounds = CaptureBounds(visible[0]);
                foreach (var renderer in visible.Skip(1)) bounds.Encapsulate(CaptureBounds(renderer));
                for (var bank = 0; bank < 3; bank++)
                for (var frame = 0; frame < 4; frame++)
                {
                    SampleFrame(bank, frame);
                    foreach (var renderer in visible) bounds.Encapsulate(CaptureBounds(renderer));
                }
                var diameter = Mathf.Max(0.1f, bounds.extents.magnitude * 2.15f);
                var cameraObject = new GameObject("Impostor bake camera", typeof(Camera));
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.GetComponent<Camera>();
                camera.scene = scene;
                camera.overrideSceneCullingMask = EditorSceneManager.GetSceneCullingMask(scene);
                camera.cullingMask = 1 << 31;
                camera.enabled = false;
                camera.orthographic = true;
                camera.orthographicSize = diameter * 0.5f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = diameter * 6f;
                camera.allowHDR = false;
                camera.allowMSAA = false;
                camera.useOcclusionCulling = false;
                var lightObject = new GameObject("Impostor bake light", typeof(Light));
                SceneManager.MoveGameObjectToScene(lightObject, scene);
                var light = lightObject.GetComponent<Light>();
                light.type = LightType.Directional;
                light.cullingMask = 1 << 31;
                light.intensity = 1f;
                light.shadows = LightShadows.None;
                light.transform.rotation = Quaternion.Euler(55f, -35f, 0f);
                // Capture at twice the tile resolution, then resolve coverage in linear space.
                // This removes jagged/speckled edges without blurring neighboring atlas tiles.
                target = new RenderTexture(248, 248, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                camera.targetTexture = target;
                atlas = new Texture2D(1024, 6144, TextureFormat.RGBA32, false);
                atlas.SetPixels32(new Color32[1024 * 6144]);
                tile = new Texture2D(248, 248, TextureFormat.RGBA32, false, true);
                for (var animationFrame = 0; animationFrame < 12; animationFrame++)
                {
                SampleFrame(animationFrame / 4, animationFrame % 4);
                for (var row = 0; row < 4; row++)
                for (var column = 0; column < 8; column++)
                {
                    var elevation = Elevations[row] * Mathf.Deg2Rad;
                    var azimuth = column * Mathf.PI * 0.25f;
                    var direction = new Vector3(Mathf.Sin(azimuth) * Mathf.Cos(elevation), Mathf.Sin(elevation), Mathf.Cos(azimuth) * Mathf.Cos(elevation));
                    // Keep a defined orientation at the pole for all eight top views.
                    var up = new Vector3(-Mathf.Sin(azimuth) * Mathf.Sin(elevation), Mathf.Cos(elevation), -Mathf.Cos(azimuth) * Mathf.Sin(elevation));
                    camera.transform.SetPositionAndRotation(bounds.center + direction * diameter * 2f, Quaternion.LookRotation(-direction, up));
                    camera.Render();
                    RenderTexture.active = target;
                    tile.ReadPixels(new Rect(0, 0, 248, 248), 0, 0);
                    var pixels = ResolveCapture(tile.GetPixels());
                    if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                        for (var pixel = 0; pixel < pixels.Length; pixel++)
                        {
                            var alpha = pixels[pixel].a;
                            pixels[pixel] = pixels[pixel].gamma;
                            pixels[pixel].a = alpha;
                        }
                    atlas.SetPixels(column * 128 + 2, (animationFrame * 4 + row) * 128 + 2, 124, 124, pixels);
                }
                }
                atlas.Apply();
                var stem = Folder + "/" + Path.GetFileNameWithoutExtension(path);
                File.WriteAllBytes(stem + ".png", atlas.EncodeToPNG());
                AssetDatabase.ImportAsset(stem + ".png", ImportAssetOptions.ForceSynchronousImport);
                var importer = (TextureImporter)AssetImporter.GetAtPath(stem + ".png");
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.sRGBTexture = true;
                importer.maxTextureSize = 8192;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.anisoLevel = 0;
                importer.SaveAndReimport();
                var material = AssetDatabase.LoadAssetAtPath<Material>(stem + ".mat");
                if (material == null) { material = new Material(shader); AssetDatabase.CreateAsset(material, stem + ".mat"); }
                material.shader = shader;
                material.enableInstancing = true;
                material.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(stem + ".png"));
                material.SetFloat("_Diameter", diameter);
                material.SetFloat("_FootHeight", bounds.extents.y);
                material.SetFloat("_ColorGain", 1f);
                material.SetFloat("_Saturation", 1f);
                material.SetFloat("_BakeVersion", 6f);
                material.SetFloat("_AnimationFrames", 4f);
                material.SetFloat("_ViewOffsetSteps", path.EndsWith("_red.prefab", StringComparison.OrdinalIgnoreCase) ? 6f : 2f);
                EditorUtility.SetDirty(material);
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(stem + ".asset");
                if (mesh == null)
                {
                    mesh = new Mesh { name = "Upper impostor card" };
                    mesh.vertices = new[] { new Vector3(-.5f,-.5f,0), new Vector3(.5f,-.5f,0), new Vector3(.5f,.5f,0), new Vector3(-.5f,.5f,0) };
                    mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
                    mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                    AssetDatabase.CreateAsset(mesh, stem + ".asset");
                }
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * diameter * 1.1f);
                EditorUtility.SetDirty(mesh);
                var old = group.transform.Find(ChildName);
                if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
                var card = new GameObject(ChildName, typeof(MeshFilter), typeof(MeshRenderer));
                card.layer = root.layer;
                card.transform.SetParent(group.transform, false);
                // Foot pivot is the unit origin, not the center/minimum of an animated AABB.
                card.transform.position = root.transform.TransformPoint(Vector3.up * bounds.extents.y);
                card.transform.localScale = Vector3.one;
                card.GetComponent<MeshFilter>().sharedMesh = mesh;
                var cardRenderer = card.GetComponent<MeshRenderer>();
                cardRenderer.sharedMaterial = material;
                cardRenderer.shadowCastingMode = ShadowCastingMode.Off;
                cardRenderer.receiveShadows = false;
                var lods = original.Take(3).ToArray();
                lods[0].screenRelativeTransitionHeight = 0.93f;
                lods[1].screenRelativeTransitionHeight = 0.56f;
                lods[2].screenRelativeTransitionHeight = 0.10f;
                var referencePoint = group.localReferencePoint;
                var groupSize = group.size;
                group.SetLODs(lods.Concat(new[] { new LOD(0f, new Renderer[] { cardRenderer }) }).ToArray());
                group.localReferencePoint = referencePoint;
                group.size = groupSize;
                var configured = group.GetLODs();
                if (configured.Length != 4 || configured[3].renderers.Length != 1 || configured[3].renderers[0] != cardRenderer)
                    throw new InvalidOperationException($"{path}: LOD4 assignment failed; prefab was not saved.");
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                RenderTexture.active = previousTarget;
                if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                if (atlas != null) UnityEngine.Object.DestroyImmediate(atlas);
                if (tile != null) UnityEngine.Object.DestroyImmediate(tile);
                EditorSceneManager.ClosePreviewScene(scene);
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static Color[] ResolveCapture(Color[] samples)
        {
            var pixels = new Color[124 * 124];
            for (var y = 0; y < 124; y++)
            for (var x = 0; x < 124; x++)
            {
                var rgb = Vector3.zero;
                var alpha = 0f;
                for (var dy = 0; dy < 2; dy++)
                for (var dx = 0; dx < 2; dx++)
                {
                    var sample = samples[(y * 2 + dy) * 248 + x * 2 + dx];
                    rgb += new Vector3(sample.r, sample.g, sample.b) * sample.a;
                    alpha += sample.a;
                }
                if (alpha <= 0.001f) { pixels[y * 124 + x] = Color.clear; continue; }
                rgb /= alpha;
                pixels[y * 124 + x] = new Color(rgb.x, rgb.y, rgb.z, alpha * 0.25f);
            }
            return pixels;
        }

        private static AnimationClip FindStateClip(Animator animator, string stateName)
        {
            if (!(animator.runtimeAnimatorController is UnityEditor.Animations.AnimatorController controller)) return null;
            foreach (var layer in controller.layers)
            foreach (var child in layer.stateMachine.states)
                if (string.Equals(child.state.name, stateName, StringComparison.OrdinalIgnoreCase) && child.state.motion is AnimationClip clip)
                    return clip;
            return null;
        }

        private static Bounds CaptureBounds(Renderer renderer)
        {
            if (!(renderer is SkinnedMeshRenderer skinned)) return renderer.bounds;
            // Renderer.bounds may still describe a different animation pose in Edit Mode.
            var mesh = new Mesh();
            try
            {
                skinned.BakeMesh(mesh);
                mesh.RecalculateBounds();
                var local = mesh.bounds;
                var result = new Bounds(skinned.transform.TransformPoint(local.min), Vector3.zero);
                for (var i = 0; i < 8; i++)
                    result.Encapsulate(skinned.transform.TransformPoint(new Vector3(
                        (i & 1) == 0 ? local.min.x : local.max.x,
                        (i & 2) == 0 ? local.min.y : local.max.y,
                        (i & 4) == 0 ? local.min.z : local.max.z)));
                return result;
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
    }
}
