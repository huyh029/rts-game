#if SKGAMES_ANIMATED_IMPOSTOR
using System;
using SKGames.AnimatedImpostor;
using UnityEditor;
using UnityEngine;

namespace RecoilPathfindingEditor
{
    public static class RecoilImpostorPlaybackChecks
    {
        [MenuItem("Tools/Recoil Pathfinding/Validate Impostor Playback")]
        public static void Run()
        {
            var library = ScriptableObject.CreateInstance<AnimatedImpostorLibrary>();
            var replacement = ScriptableObject.CreateInstance<AnimatedImpostorLibrary>();
            var owner = new GameObject("Temporary impostor playback check");
            owner.SetActive(false);
            try
            {
                library.Clips = new[]
                {
                    new AnimatedImpostorLibrary.Clip { Name = "Idle", FirstSlice = 3, Frames = 8, Duration = 1f, Loop = true },
                    new AnimatedImpostorLibrary.Clip { Name = "Attack", FirstSlice = 11, Frames = 4, Duration = 0.5f, Loop = false }
                };
                replacement.Clips = new[]
                { new AnimatedImpostorLibrary.Clip { Name = "Walk", FirstSlice = 30, Frames = 6, Duration = 2f, Loop = true } };
                var player = owner.AddComponent<AnimatedImpostorPlayer>();
                var renderer = owner.GetComponent<MeshRenderer>();
                player.Library = library;
                player.Speed = 2f;
                owner.SetActive(true);
                player.Play(0, 0.4f);
                player.Speed = 3f;
                var block = new MaterialPropertyBlock();
                renderer.GetPropertyBlock(block);
                Require(block.GetVector("_Playback").x == 3f && block.GetVector("_AnimOptions").x == 3f &&
                    Mathf.Abs(block.GetVector("_AnimOptions").z - 0.4f) < 0.005f,
                    "Changing speed must update the shader immediately while preserving animation phase.");
                player.ClipIndex = 1;
                renderer.GetPropertyBlock(block);
                Require(block.GetVector("_Playback").x == 11f && block.GetVector("_AnimOptions").y == 0f,
                    "Changing ClipIndex must immediately select the non-looping clip without Update polling.");
                player.InitialClip = 0;
                player.Library = replacement;
                renderer.GetPropertyBlock(block);
                Require(block.GetVector("_Playback").x == 30f, "Changing Library must refresh the shader immediately.");
                owner.SetActive(false);
                player.Speed = 0.5f;
                owner.SetActive(true);
                // Explicitly invoke the lifecycle method so this check also works outside Play Mode.
                typeof(AnimatedImpostorPlayer).GetMethod("OnEnable",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(player, null);
                renderer.GetPropertyBlock(block);
                Require(block.GetVector("_AnimOptions").x == 0.5f && block.GetVector("_AnimOptions").z == 0f,
                    "A pooled player must restart with its current playback configuration.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(library);
                UnityEngine.Object.DestroyImmediate(replacement);
            }
            Debug.Log("Impostor playback checks passed: clip/library updates, speed phase continuity and pooled playback without per-frame Update.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
