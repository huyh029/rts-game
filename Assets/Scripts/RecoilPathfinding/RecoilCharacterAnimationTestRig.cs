using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    public sealed class RecoilCharacterAnimationTestRig : MonoBehaviour
    {
        [SerializeField] private Transform charactersRoot;

        private static readonly string[] AnimationStates =
        {
            "Idle",
            "Run",
            "Attack",
            "Death"
        };

        private string _status = "Choose an animation with keys 1-4 or the buttons.";

        public Transform CharactersRoot
        {
            get => charactersRoot;
            set => charactersRoot = value;
        }

        private void OnGUI()
        {
            var currentEvent = Event.current;
            var requestedAnimation = -1;
            var consumeKeyEvent = currentEvent.type == EventType.KeyDown &&
                                  TryGetAnimationIndex(currentEvent.keyCode, out requestedAnimation);
            const float panelX = 12f;
            const float panelY = 12f;
            const float panelWidth = 320f;
            const float panelHeight = 220f;
            GUI.Box(new Rect(panelX, panelY, panelWidth, panelHeight), GUIContent.none);
            GUI.Label(new Rect(panelX + 12f, panelY + 10f, panelWidth - 24f, 24f),
                "Character Animation Test");
            GUI.Label(new Rect(panelX + 12f, panelY + 34f, panelWidth - 24f, 20f),
                "1 Idle   2 Run   3 Attack   4 Death");

            for (var i = 0; i < AnimationStates.Length; i++)
            {
                var buttonRect = new Rect(panelX + 12f, panelY + 60f + i * 30f, panelWidth - 24f, 26f);
                if (GUI.Button(buttonRect, $"{i + 1} - {AnimationStates[i]}"))
                    requestedAnimation = i;
            }

            GUI.Label(new Rect(panelX + 12f, panelY + 184f, panelWidth - 24f, 30f), _status);
            if (consumeKeyEvent)
                currentEvent.Use();
            if (requestedAnimation >= 0)
                PlayAnimation(requestedAnimation);
        }

        private void PlayAnimation(int animationIndex)
        {
            var stateName = AnimationStates[animationIndex];
            var stateHash = Animator.StringToHash($"Base Layer.{stateName}");
            var animators = charactersRoot != null
                ? charactersRoot.GetComponentsInChildren<Animator>(true)
                : GetComponentsInChildren<Animator>(true);
            var handledDeathPresentations = new HashSet<RecoilCharacterPresentation>();
            var playedCount = 0;
            var skippedCount = 0;

            foreach (var animator in animators)
            {
                if (animator == null || animator.runtimeAnimatorController == null)
                {
                    skippedCount++;
                    continue;
                }

                if (stateName == "Death")
                {
                    var presentation = animator.GetComponentInParent<RecoilCharacterPresentation>();
                    if (presentation != null)
                    {
                        if (!handledDeathPresentations.Add(presentation))
                            continue;
                        if (presentation.PlayDeath())
                            playedCount++;
                        else
                            skippedCount++;
                        continue;
                    }
                }

                if (!animator.HasState(0, stateHash))
                {
                    skippedCount++;
                    continue;
                }

                animator.Play(stateHash, 0, 0f);
                playedCount++;
            }

            _status = playedCount > 0
                ? $"{stateName}: played on {playedCount}; unavailable on {skippedCount}."
                : $"No character has an imported {stateName} animation.";
        }

        private static bool TryGetAnimationIndex(KeyCode keyCode, out int animationIndex)
        {
            switch (keyCode)
            {
                case KeyCode.Alpha1:
                case KeyCode.Keypad1:
                    animationIndex = 0;
                    return true;
                case KeyCode.Alpha2:
                case KeyCode.Keypad2:
                    animationIndex = 1;
                    return true;
                case KeyCode.Alpha3:
                case KeyCode.Keypad3:
                    animationIndex = 2;
                    return true;
                case KeyCode.Alpha4:
                case KeyCode.Keypad4:
                    animationIndex = 3;
                    return true;
                default:
                    animationIndex = -1;
                    return false;
            }
        }
    }
}
