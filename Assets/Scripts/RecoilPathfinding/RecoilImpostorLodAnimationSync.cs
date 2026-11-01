using System;
using UnityEngine;

namespace RecoilPathfinding
{
    // Keep the distant billboard on the same animation as the near character.
    [DefaultExecutionOrder(10001)]
    public sealed class RecoilImpostorLodAnimationSync : MonoBehaviour
    {
        private Animator _animator;
        private int _state;
        private float _time;

        private void OnEnable()
        {
            var presentation = GetComponentInParent<RecoilCharacterPresentation>();
            _animator = presentation != null ? presentation.Animator : null;
           
            _state = 0;
            _time = -1f;
        }

        private void LateUpdate()
        {
            if (_animator == null || !_animator.isActiveAndEnabled || _animator.runtimeAnimatorController == null) return;
            var state = _animator.IsInTransition(0)
                ? _animator.GetNextAnimatorStateInfo(0) : _animator.GetCurrentAnimatorStateInfo(0);
            var changed = state.fullPathHash != _state || state.normalizedTime < _time;
            _state = state.fullPathHash;
            _time = state.normalizedTime;
            var kind = state.IsName("Attack") ? "attack" : state.IsName("Death") ? "death" :
                state.IsName("Run") ? "run" : "idle";
            //foreach (var player in _players)
            //{
            //    if (player == null || player.Library == null || player.Library.Clips == null) continue;
            //    var clips = player.Library.Clips;
            //    var index = Array.FindIndex(clips, clip => clip.Name != null &&
            //        clip.Name.IndexOf(kind, StringComparison.OrdinalIgnoreCase) >= 0);
            //    if (index < 0) index = kind == "run" ? 1 : kind == "attack" ? 2 : kind == "death" ? 3 : 0;
            //    if (index >= clips.Length) index = 0;
            //    if (clips.Length == 0) continue;
            //    player.Speed = state.length > 0f ? clips[index].Duration / state.length * _animator.speed : 1f;
            //    if (changed || player.ClipIndex != index) player.Play(index, state.normalizedTime);
            //}
        }
    }
}
