using System;
using UnityEngine;
using Unity.Entities;

namespace RecoilPathfinding
{
    // Standalone VAT preview. Shader time advances animation without Animator or CPU skinning.
    public sealed class RecoilVatAnimation : MonoBehaviour
    {
        public enum AnimationState { Idle, Run, Attack, Death }
        [Serializable] public struct Clip
        {
            public AnimationState State;
            public int StartFrame, FrameCount;
            public float Duration;
            public bool Loop;
        }
        public Renderer[] Parts;
        public Clip[] Clips;
        public AnimationState State;
        [Min(.01f)] public float Speed = 1f;
        private MaterialPropertyBlock _properties;
        private World _world;
        private Entity _entity;
        private int _version = -1;
        private RecoilVisualProjectilePool _projectiles;
        private RecoilCharacterPresentation _source;
        private Vector3 _projectileOffset, _attackTarget, _deathPosition;
        private bool _releasePending;
        private Action _retire;
        public bool DrivenByEcs => _world != null;
        public float AttackDuration => Duration(AnimationState.Attack);
        public float ProjectileSpeed => _source != null ? _source.ProjectileSpeed : 24f;
        public Vector3 ProjectileOrigin => transform.TransformPoint(_projectileOffset);

        private float Duration(AnimationState state)
        {
            if (Clips != null) foreach (var clip in Clips) if (clip.State == state) return Mathf.Max(.001f, clip.Duration);
            return 1f;
        }
        internal void BindToEcs(World world, Entity entity, RecoilCharacterPresentation source, RecoilVisualProjectilePool projectiles)
        {
            _world = world; _entity = entity; _source = source; _projectiles = projectiles;
            _version = -1; _releasePending = false; _retire = null;
            _projectileOffset = source != null && source.ProjectileSpawnPoint != null
                ? source.transform.InverseTransformPoint(source.ProjectileSpawnPoint.position) : Vector3.up;
            if (Parts != null && Parts.Length > 0 && Parts[0] != null) _projectileOffset += Parts[0].transform.localPosition;
            RecoilVatEcsBridge.Register(world, entity, this);
            PlayAt(AnimationState.Idle, 0);
        }
        internal void RequestAttack(Vector3 target)
        {
            _attackTarget = target;
            _releasePending = _source != null && _source.ProjectilePrefab != null;
            RecoilVatEcsBridge.Attack(_world, _entity, AttackDuration);
        }
        internal void BeginDeath(Action retire)
        {
            _releasePending = false; _retire = retire; _deathPosition = transform.position;
        }
        internal void ApplyEcsState(RecoilVatState state)
        {
            if (_version != state.Version || State != (AnimationState)state.State)
            {
                _version = state.Version;
                PlayAt((AnimationState)state.State, state.Elapsed);
            }
            if (_releasePending && state.State == 2 &&
                state.Elapsed >= AttackDuration * _source.ProjectileReleaseNormalizedTime)
            {
                _releasePending = false;
                _projectiles.Spawn(_source.ProjectilePrefab, ProjectileOrigin, transform.rotation,
                    _source.ProjectileScale, _attackTarget, _source.ProjectileSpeed);
            }
            if (state.State == 3 && _retire != null)
            {
                var duration = Duration(AnimationState.Death);
                transform.position = _deathPosition - Vector3.up * Mathf.Clamp01(state.Elapsed - duration) * 1.5f;
                if (state.Elapsed >= duration + 1f) { var retire = _retire; _retire = null; retire(); }
            }
        }
        private void OnDisable()
        {
            RecoilVatEcsBridge.Unregister(_world, _entity);
            _world = null; _entity = Entity.Null; _retire = null; _releasePending = false;
        }

        private void OnEnable()
        {
            if (!RecoilVatPrefabLibrary.LodEnabled)
            {
                var lod = GetComponent<LODGroup>();
                if (lod != null) lod.ForceLOD(0);
            }
            if (gameObject.scene.name == "VAT Preview") RecoilVatPreviewControls.EnsureExists();
            Play(State);
        }
        private void OnValidate() { if (Application.isPlaying) Play(State); }
        public void Play(AnimationState state)
        {
            PlayAt(state, 0);
        }
        private void PlayAt(AnimationState state, float elapsed)
        {
            if (Clips == null || Parts == null) return;
            foreach (var clip in Clips)
            {
                if (clip.State != state) continue;
                State = state;
                _properties ??= new MaterialPropertyBlock();
                _properties.SetVector("_VatClip", new Vector4(clip.StartFrame, clip.FrameCount,
                    Mathf.Max(.001f, clip.Duration), clip.Loop ? 1 : 0));
                var now = DrivenByEcs ? RecoilSimulationClock.SimulationSeconds : Time.time;
                _properties.SetVector("_VatPlayback", new Vector4(now - elapsed / Mathf.Max(.01f, Speed),
                    Mathf.Max(.01f, Speed), DrivenByEcs ? 1 : 0, 0));
                foreach (var part in Parts) if (part != null) part.SetPropertyBlock(_properties);
                return;
            }
        }
    }
}
