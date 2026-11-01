using UnityEngine;

namespace RecoilPathfinding
{
    [DefaultExecutionOrder(10000)]
    public sealed class RecoilCharacterPresentation : MonoBehaviour
    {
        private const float DeathFallbackDuration = 1.5f;
        private const float DeathFallbackDistance = 1.5f;
        // Internal so RecoilAutoBattleDemo can reproduce the same grace period for VAT-rendered
        // soldiers (RecoilVatCrowdRenderer applies the matching sink offset in its shader).
        internal const float DeathSinkDelay = 1f;
        internal const float DeathSinkDuration = 1.5f;
        private const float DeathSinkDistance = 1.5f;
        private const float DeathStateEntryTimeout = 0.25f;
        private const float DeathPlaybackTimeout = 8f;

        [SerializeField] private Animator animator;
        [SerializeField] private Transform visualRoot;
        [SerializeField] private AnimationClip attackClip;
        [SerializeField, Min(0.001f)] private float sourceModelHeight = 1f;
        [SerializeField] private Vector3 pivotOffset;
        [SerializeField] private Transform projectileSpawnPoint;
        [SerializeField] private GameObject projectilePrefab;
        [SerializeField] private bool useRangedAttackOverride;
        [SerializeField] private bool rangedAttack;
        [SerializeField, Min(0.001f)] private float projectileScale = 1f;
        [SerializeField] private string attackTrigger = "Attack";
        [SerializeField] private string attackStateName = "Attack";
        [SerializeField, Min(0.01f)] private float normalizedVisualHeight = 1.8f;
        [SerializeField] private float forwardYawOffset;
        [SerializeField] private float attackForwardYawOffset;
        [SerializeField, Range(0f, 1f)] private float projectileReleaseNormalizedTime = 0.5f;
        [SerializeField, Min(0.1f)] private float projectileSpeed = 24f;
        [Tooltip("Sprite atlas bake của lính này; khi ở xa lính được vẽ bằng impostor (RecoilImpostorCrowd).")]
        [SerializeField] private RecoilImpostorSpriteLibrary impostorLibrary;
        [Tooltip("Vertex-animation-texture bake của lính này; khi có, lính gần được vẽ bằng " +
            "RecoilVatCrowdRenderer (GPU instancing) thay vì Animator + SkinnedMeshRenderer riêng.")]
        [SerializeField] private RecoilVatLibrary vatLibrary;

        private Transform _pendingTarget;
        private Transform _queuedAttackTarget;
        private bool _releasePending;
        private float _appliedForwardYawOffset;
        private Animator[] _deathAnimators;
        private System.Action _deathPresentationCompleted;
        private bool _deathPresentationStarted;
        private float[] _deathAnimatorStartLocalY;
        private Transform _deathSinkTransform;
        private Vector3 _deathSinkStartPosition;
        private Transform _deathFallbackTransform;
        private Vector3 _deathFallbackStartPosition;
        private float _deathFallbackElapsed;
        private float _deathElapsed;
        private Animator[] _animators;
        private RecoilNavMeshPathAgent _agent;
        private int _attackStateHash;
        private int _attackShortHash;
        private float _movementSpeed;
        private int _movementFrame = -2;
        private bool _locomotionDirty = true;
        private bool _deathCompleted;
        private float _attackEntryDeadline;
        private bool _hasCachedGroundOffset;
        private float _cachedGroundOffset;

        public bool HasPendingAnimationWork => !_deathCompleted &&
            (_locomotionDirty || _queuedAttackTarget != null || _releasePending || _deathPresentationStarted);

        private void WakeAnimationWork()
        {
            if (Application.isPlaying && isActiveAndEnabled && HasPendingAnimationWork)
                RecoilCharacterPresentationScheduler.Register(this);
        }

        // Call after an external script changes a controller or plays a state directly.
        public void NotifyAnimationChanged()
        {
            _animators = null;
            _locomotionDirty = true;
            WakeAnimationWork();
        }

        // Reuse the render snapshot instead of querying EntityManager once per character.
        public void SetPresentationMovementSpeed(float speed)
        {
            var changed = _movementFrame < 0 || (speed > .05f) != (_movementSpeed > .05f);
            _movementSpeed = speed;
            _movementFrame = Time.frameCount;
            if (changed && !_deathPresentationStarted && !_deathCompleted)
            {
                _locomotionDirty = true;
                WakeAnimationWork();
            }
        }
        private static readonly int IdleHash = UnityEngine.Animator.StringToHash("Base Layer.Idle");
        private static readonly int RunHash = UnityEngine.Animator.StringToHash("Base Layer.Run");
        private static readonly int DeathHash = UnityEngine.Animator.StringToHash("Death");

        private void OnEnable()
        {
            _locomotionDirty = true;
            WakeAnimationWork();
        }

        private void OnDisable()
        {
            RecoilCharacterPresentationScheduler.Unregister(this);
        }

        private void OnTransformChildrenChanged()
        {
            _hasCachedGroundOffset = false;
            NotifyAnimationChanged();
        }

        private void CacheAnimationReferences()
        {
            if (_animators != null) return;
            _agent = GetComponent<RecoilNavMeshPathAgent>();
            _animators = (visualRoot != null ? visualRoot : transform).GetComponentsInChildren<Animator>(true);
            _attackStateHash = UnityEngine.Animator.StringToHash($"Base Layer.{attackStateName}");
            _attackShortHash = UnityEngine.Animator.StringToHash(attackStateName ?? string.Empty);
        }

        public Animator Animator => animator;
        public RecoilImpostorSpriteLibrary ImpostorLibrary => impostorLibrary;
        public RecoilVatLibrary VatLibrary => vatLibrary != null && vatLibrary.IsValid ? vatLibrary : null;
        // Diagnostic only: the raw assigned reference, even if not currently valid, so callers can
        // log why a template fell back to the old GameObject+Animator path instead of VAT.
        public RecoilVatLibrary RawVatLibrary => vatLibrary;
        public string AttackStateName => attackStateName;
        public Transform VisualRoot => visualRoot;
        public AnimationClip AttackClip => attackClip;
        public Transform ProjectileSpawnPoint => projectileSpawnPoint;
        public GameObject ProjectilePrefab => projectilePrefab;
        public bool HasRangedAttackOverride => useRangedAttackOverride;
        public bool IsRangedAttack => useRangedAttackOverride ? rangedAttack : projectilePrefab != null;
        public float ProjectileScale => projectileScale;
        public float NormalizedVisualHeight => normalizedVisualHeight;
        public float ForwardYawOffset => forwardYawOffset;
        public float AttackForwardYawOffset => attackForwardYawOffset;
        public float ProjectileReleaseNormalizedTime => projectileReleaseNormalizedTime;
        public float ProjectileSpeed => projectileSpeed;

        [ContextMenu("Preview Attack")]
        private void PreviewAttack()
        {
            PlayAttack(null);
        }

        private void OnValidate()
        {
            _animators = null;
            _hasCachedGroundOffset = false;
            ApplyVisualSettings();
        }

        public void ApplyVisualSettings()
        {
            if (visualRoot == null || !float.IsFinite(sourceModelHeight) || sourceModelHeight <= 0f)
                return;
            var scale = normalizedVisualHeight / sourceModelHeight;
            visualRoot.localScale = Vector3.one * scale;
            visualRoot.localPosition = (pivotOffset + Vector3.up * GetVisualGroundOffset()) * scale;
            visualRoot.localRotation = Quaternion.Euler(0f, forwardYawOffset, 0f);
            _appliedForwardYawOffset = forwardYawOffset;
        }

        private float GetVisualGroundOffset()
        {
            // The result only depends on the static local layout of renderers under visualRoot
            // (it cancels visualRoot's own world/local transform), so it never changes across
            // pooled reuse of the same instance. Recomputing it per spawn was the costliest part
            // of ResetForReuse for crowds, since it walks every LOD0 renderer's 8 AABB corners.
            if (_hasCachedGroundOffset) return _cachedGroundOffset;
            _cachedGroundOffset = ComputeVisualGroundOffset();
            _hasCachedGroundOffset = true;
            return _cachedGroundOffset;
        }

        private float ComputeVisualGroundOffset()
        {
            var lodGroup = visualRoot.GetComponentInChildren<LODGroup>(true);
            var lods = lodGroup != null ? lodGroup.GetLODs() : null;
            var renderers = lods != null && lods.Length > 0 && lods[0].renderers.Length > 0
                ? lods[0].renderers
                : visualRoot.GetComponentsInChildren<Renderer>(true);
            var rootInverse = visualRoot.worldToLocalMatrix;
            var minY = float.PositiveInfinity;
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;
                var bounds = renderer.localBounds;
                var localToRoot = rootInverse * renderer.transform.localToWorldMatrix;
                for (var corner = 0; corner < 8; corner++)
                {
                    var point = bounds.center + Vector3.Scale(bounds.extents, new Vector3(
                        (corner & 1) == 0 ? -1f : 1f,
                        (corner & 2) == 0 ? -1f : 1f,
                        (corner & 4) == 0 ? -1f : 1f));
                    var rootPoint = localToRoot.MultiplyPoint3x4(point);
                    if (float.IsFinite(rootPoint.y))
                        minY = Mathf.Min(minY, rootPoint.y);
                }
            }
            return float.IsFinite(minY) ? -minY : 0f;
        }

        public void ResetForReuse()
        {
            if (_deathSinkTransform != null)
                _deathSinkTransform.localPosition = _deathSinkStartPosition;
            if (_deathFallbackTransform != null)
                _deathFallbackTransform.localPosition = _deathFallbackStartPosition;
            _pendingTarget = null;
            _queuedAttackTarget = null;
            _releasePending = false;
            _deathPresentationCompleted = null;
            _deathPresentationStarted = false;
            _deathAnimatorStartLocalY = null;
            _deathSinkTransform = null;
            _deathFallbackTransform = null;
            _deathFallbackElapsed = 0f;
            _deathElapsed = 0f;
            _movementFrame = -2;
            _deathCompleted = false;
            _locomotionDirty = true;
            _attackEntryDeadline = 0f;
            WakeAnimationWork();
            ApplyVisualSettings();

            if (visualRoot == null)
                return;
            _animators = null;
            CacheAnimationReferences();
            foreach (var childAnimator in _animators)
            {
                if (childAnimator == null)
                    continue;
                // Rebind() rebuilds the whole avatar bind-pose cache and is one of Mecanim's
                // heaviest calls; it is unneeded here since the death presentation above already
                // restored every transform it touched, so the avatar mapping was never disturbed.
                // Force the clip state directly instead, and only fall back to Rebind() if this
                // controller has no "Idle" state to play.
                if (childAnimator.HasState(0, IdleHash))
                    childAnimator.Play(IdleHash, 0, 0f);
                else
                    childAnimator.Rebind();
                childAnimator.Update(0f);
            }
        }

        public bool PlayAttack(Transform target)
        {
            if (_deathPresentationStarted || _deathCompleted) return false;
            if (visualRoot == null)
            {
                Debug.LogError($"{nameof(RecoilCharacterPresentation)} requires a visual root.", this);
                return false;
            }
            if (string.IsNullOrWhiteSpace(attackStateName))
            {
                Debug.LogError("Attack state name must be assigned.", this);
                return false;
            }

            if (target != null && !IsFacingTarget(target))
            {
                _queuedAttackTarget = target;
                WakeAnimationWork();
                return true;
            }
            _queuedAttackTarget = null;
            _pendingTarget = target;
            _releasePending = false;
            CacheAnimationReferences();
            var attackStateHash = _attackStateHash;
            var played = false;
            foreach (var childAnimator in _animators)
            {
                if (childAnimator == null || !childAnimator.isActiveAndEnabled ||
                    childAnimator.runtimeAnimatorController == null ||
                    !childAnimator.HasState(0, attackStateHash))
                    continue;

                var currentState = childAnimator.GetCurrentAnimatorStateInfo(0);
                var alreadyAttacking = currentState.fullPathHash == attackStateHash ||
                    (childAnimator.IsInTransition(0) &&
                     childAnimator.GetNextAnimatorStateInfo(0).fullPathHash == attackStateHash);
                if (!alreadyAttacking)
                    childAnimator.CrossFadeInFixedTime(attackStateHash, 0.05f, 0, 0f);
                played = true;
            }

            if (!played && animator != null && animator.runtimeAnimatorController != null &&
                !string.IsNullOrWhiteSpace(attackTrigger) &&
                animator.parameters.Length > 0)
            {
                foreach (var parameter in animator.parameters)
                {
                    if (parameter.type != AnimatorControllerParameterType.Trigger ||
                        !string.Equals(parameter.name, attackTrigger, System.StringComparison.Ordinal))
                        continue;
                    animator.ResetTrigger(attackTrigger);
                    animator.SetTrigger(attackTrigger);
                    played = true;
                    break;
                }
            }

            if (!played)
            {
                Debug.LogWarning(
                    $"No child Animator has an '{attackStateName}' state for this character.",
                    this);
                return false;
            }

            _releasePending = attackClip != null && animator != null &&
                              projectilePrefab != null && projectileSpawnPoint != null && IsRangedAttack;
            _locomotionDirty = true;
            _attackEntryDeadline = Time.unscaledTime + .25f;
            WakeAnimationWork();
            return true;
        }

        public bool PlayDeath(System.Action onCompleted = null)
        {
            if (_deathPresentationStarted)
                return true;

            var deathStateHash = Animator.StringToHash("Base Layer.Death");
            CacheAnimationReferences();
            _deathSinkTransform = visualRoot != null
                ? visualRoot
                : animator != null
                    ? animator.transform
                    : transform;
            _deathSinkStartPosition = _deathSinkTransform.localPosition;
            var deathAnimators = _animators;
            var activeDeathAnimators = new System.Collections.Generic.List<Animator>();
            var deathAnimatorStartLocalY = new System.Collections.Generic.List<float>();
            foreach (var childAnimator in deathAnimators)
            {
                if (childAnimator == null || !childAnimator.isActiveAndEnabled ||
                    childAnimator.runtimeAnimatorController == null ||
                    !childAnimator.HasState(0, deathStateHash))
                    continue;

                childAnimator.Play(deathStateHash, 0, 0f);
                activeDeathAnimators.Add(childAnimator);
                deathAnimatorStartLocalY.Add(childAnimator.transform.localPosition.y);
            }

            _deathPresentationStarted = true;
            _deathCompleted = false;
            _queuedAttackTarget = null;
            _deathPresentationCompleted = onCompleted;
            _deathAnimators = activeDeathAnimators.ToArray();
            _deathAnimatorStartLocalY = deathAnimatorStartLocalY.ToArray();
            _releasePending = false;
            _deathElapsed = 0f;
            if (activeDeathAnimators.Count == 0)
                BeginDeathFallback();
            WakeAnimationWork();
            return true;
        }

        internal void TickPresentation()
        {
            UpdateDeathPresentation();
            if (_deathCompleted || !isActiveAndEnabled) return;
            if (!_deathPresentationStarted && _queuedAttackTarget != null && IsFacingTarget(_queuedAttackTarget))
                PlayAttack(_queuedAttackTarget);
            if (_locomotionDirty) UpdateLocomotionAnimation();
            if (!_releasePending) return;
            if (animator == null || projectilePrefab == null || projectileSpawnPoint == null)
            {
                _releasePending = false;
                _pendingTarget = null;
                return;
            }

            var state = animator.GetCurrentAnimatorStateInfo(0);
            if (!state.IsName(attackStateName) || state.normalizedTime < projectileReleaseNormalizedTime)
                return;

            _releasePending = false;
            var projectile = Instantiate(
                projectilePrefab,
                projectileSpawnPoint.position,
                projectileSpawnPoint.rotation * Quaternion.Euler(0f, _appliedForwardYawOffset, 0f));
            projectile.transform.localScale *= projectileScale;
            if (_pendingTarget != null)
                projectile.AddComponent<RecoilVisualProjectileFlight>().Initialize(
                    _pendingTarget.position, projectileSpeed);
            _pendingTarget = null;
        }

        internal void ClampDeathAnimatorRootHeight()
        {
            if (!_deathPresentationStarted)
                return;

            var sinkProgress = _deathFallbackTransform == null && _deathSinkTransform != null
                ? Mathf.Clamp01((_deathElapsed - DeathSinkDelay) / DeathSinkDuration)
                : 0f;
            if (_deathFallbackTransform == null && _deathSinkTransform != null)
            {
                _deathSinkTransform.localPosition = _deathSinkStartPosition +
                    Vector3.down * (DeathSinkDistance * sinkProgress);
            }

            if (_deathFallbackTransform != null)
                return;

            for (var i = 0; i < _deathAnimators.Length; i++)
            {
                var deathAnimator = _deathAnimators[i];
                if (deathAnimator == null || !deathAnimator.isActiveAndEnabled)
                    continue;

                var root = deathAnimator.transform;
                var localPosition = root.localPosition;
                var startY = root == _deathSinkTransform
                    ? _deathSinkStartPosition.y - DeathSinkDistance * sinkProgress
                    : _deathAnimatorStartLocalY[i];
                if (Mathf.Approximately(localPosition.y, startY))
                    continue;

                localPosition.y = startY;
                root.localPosition = localPosition;
            }
        }

        private bool IsFacingTarget(Transform target)
        {
            var direction = target.position - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.000001f) return true;
            var forward = transform.forward;
            forward.y = 0f;
            return Vector3.Dot(forward.normalized, direction.normalized) >= 0.94f;
        }

        private void UpdateLocomotionAnimation()
        {
            if (!_locomotionDirty) return;
            if (_deathPresentationStarted || visualRoot == null)
            {
                _locomotionDirty = false;
                return;
            }

            if (Time.unscaledTime < _attackEntryDeadline) return;
            _locomotionDirty = false;

            CacheAnimationReferences();
            if (_agent == null)
                return;

            var moving = (_movementFrame >= Time.frameCount - 1 ? _movementSpeed : _agent.CurrentSpeed) > 0.05f;
            // Only entered on movement changes or while an action is still running.
            for (var i = 0; i < _animators.Length; i++)
            {
                var childAnimator = _animators[i];
                if (childAnimator == null || !childAnimator.isActiveAndEnabled ||
                    childAnimator.runtimeAnimatorController == null)
                {
                    continue;
                }

                var stateHash = moving ? RunHash : IdleHash;
                var currentState = childAnimator.GetCurrentAnimatorStateInfo(0);
                var finishedAttack = currentState.shortNameHash == _attackShortHash &&
                    !currentState.loop && currentState.normalizedTime >= 1f && !_releasePending;
                if (IsAttackOrDeathState(currentState) && !finishedAttack)
                {
                    _locomotionDirty = true;
                    continue;
                }
                if (childAnimator.IsInTransition(0) &&
                    IsAttackOrDeathState(childAnimator.GetNextAnimatorStateInfo(0)))
                {
                    _locomotionDirty = true;
                    continue;
                }

                if (!childAnimator.HasState(0, stateHash))
                {
                    if (moving)
                        continue;
                    stateHash = IdleHash;
                    if (!childAnimator.HasState(0, stateHash))
                        continue;
                }

                if (currentState.fullPathHash != stateHash &&
                    (!childAnimator.IsInTransition(0) ||
                     childAnimator.GetNextAnimatorStateInfo(0).fullPathHash != stateHash))
                    childAnimator.CrossFadeInFixedTime(stateHash, 0.12f, 0, 0f);
            }
        }

        private bool IsAttackOrDeathState(AnimatorStateInfo state)
        {
            return state.fullPathHash == _attackStateHash ||
                   state.shortNameHash == _attackShortHash || state.shortNameHash == DeathHash;
        }

        private void UpdateDeathPresentation()
        {
            if (!_deathPresentationStarted)
                return;

            _deathElapsed += Time.deltaTime;
            if (_deathFallbackTransform != null)
            {
                _deathFallbackElapsed = Mathf.Min(
                    _deathFallbackElapsed + Time.deltaTime, DeathFallbackDuration);
                var progress = _deathFallbackElapsed / DeathFallbackDuration;
                _deathFallbackTransform.localPosition = _deathFallbackStartPosition +
                    Vector3.down * (DeathFallbackDistance * progress);
                if (progress < 1f)
                    return;
                CompleteDeathPresentation();
                return;
            }

            for (var i = 0; i < _deathAnimators.Length; i++)
            {
                var deathAnimator = _deathAnimators[i];
                if (deathAnimator == null || !deathAnimator.isActiveAndEnabled)
                    continue;

                var state = deathAnimator.GetCurrentAnimatorStateInfo(0);
                if (!state.IsName("Death") && _deathElapsed >= DeathStateEntryTimeout)
                {
                    BeginDeathFallback();
                    return;
                }
                if (!state.IsName("Death") || state.normalizedTime < 1f)
                {
                    var playbackTimeout = state.IsName("Death")
                        ? Mathf.Max(DeathPlaybackTimeout, state.length + 1f)
                        : DeathPlaybackTimeout;
                    if (_deathElapsed >= playbackTimeout)
                    {
                        BeginDeathFallback();
                        return;
                    }
                    return;
                }
            }

            if (_deathElapsed < DeathSinkDelay + DeathSinkDuration)
                return;

            CompleteDeathPresentation();
        }

        private void BeginDeathFallback()
        {
            _deathFallbackTransform = visualRoot != null
                ? visualRoot
                : animator != null
                    ? animator.transform
                    : transform;
            _deathFallbackStartPosition = _deathFallbackTransform.localPosition;
            _deathFallbackElapsed = 0f;
        }

        private void CompleteDeathPresentation()
        {
            var onCompleted = _deathPresentationCompleted;
            _deathPresentationCompleted = null;
            _deathPresentationStarted = false;
            _deathCompleted = true;
            _deathAnimatorStartLocalY = null;
            _locomotionDirty = false;
            _deathElapsed = 0f;
            onCompleted?.Invoke();
        }
    }

    public sealed class RecoilVisualProjectileFlight : MonoBehaviour
    {
        private Vector3 _start;
        private Vector3 _destination;
        private float _speed;
        private float _arcHeight;
        private float _duration;
        private float _elapsed;
        private bool _initialized;

        public void Initialize(Vector3 destination, float speed)
        {
            if (!float.IsFinite(speed) || speed <= 0f)
                throw new System.ArgumentOutOfRangeException(
                    nameof(speed), "Projectile speed must be finite and greater than zero.");

            _start = transform.position;
            _destination = destination;
            _speed = speed;
            var horizontalDistance = Vector2.Distance(
                new Vector2(_start.x, _start.z),
                new Vector2(_destination.x, _destination.z));
            _duration = Mathf.Max(0.05f, horizontalDistance / _speed);
            _arcHeight = Mathf.Max(1f, horizontalDistance * 0.18f);
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized)
                return;

            _elapsed = Mathf.Min(_elapsed + Time.deltaTime, _duration);
            var t = _elapsed / _duration;
            var basePosition = Vector3.Lerp(_start, _destination, t);
            var arcOffset = 4f * _arcHeight * t * (1f - t);
            transform.position = basePosition + Vector3.up * arcOffset;

            if (t >= 1f)
            {
                transform.position = _destination;
                Destroy(gameObject);
                return;
            }

            var tangent = (_destination - _start) / _duration +
                          Vector3.up * (4f * _arcHeight * (1f - 2f * t) / _duration);
            if (tangent.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(tangent);
        }
    }

}
