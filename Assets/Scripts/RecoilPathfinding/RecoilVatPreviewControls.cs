using UnityEngine;
using UnityEngine.InputSystem;

namespace RecoilPathfinding
{
    // One input callback for all VAT preview instances, including existing preview scenes.
    public sealed class RecoilVatPreviewControls : MonoBehaviour
    {
        private static RecoilVatPreviewControls _instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _instance = null;

        internal static void EnsureExists()
        {
            if (!Application.isPlaying || _instance != null) return;
            _instance = FindFirstObjectByType<RecoilVatPreviewControls>();
            if (_instance == null)
                _instance = new GameObject("VAT Preview Controls (1 Idle, 2 Run, 3 Attack, 4 Death)")
                    .AddComponent<RecoilVatPreviewControls>();
        }

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (!Application.isFocused || keyboard == null) return;
            RecoilVatAnimation.AnimationState state;
            if (keyboard.digit1Key.wasPressedThisFrame || keyboard.numpad1Key.wasPressedThisFrame)
                state = RecoilVatAnimation.AnimationState.Idle;
            else if (keyboard.digit2Key.wasPressedThisFrame || keyboard.numpad2Key.wasPressedThisFrame)
                state = RecoilVatAnimation.AnimationState.Run;
            else if (keyboard.digit3Key.wasPressedThisFrame || keyboard.numpad3Key.wasPressedThisFrame)
                state = RecoilVatAnimation.AnimationState.Attack;
            else if (keyboard.digit4Key.wasPressedThisFrame || keyboard.numpad4Key.wasPressedThisFrame)
                state = RecoilVatAnimation.AnimationState.Death;
            else return;

            foreach (var player in FindObjectsByType<RecoilVatAnimation>(FindObjectsSortMode.None))
                if (player.isActiveAndEnabled && !player.DrivenByEcs) player.Play(state);
        }

        private void OnDestroy() { if (_instance == this) _instance = null; }
    }
}
