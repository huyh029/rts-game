using UnityEngine;
using Unity.Mathematics;

namespace RecoilPathfinding
{
    [DefaultExecutionOrder(10000)]
    public sealed class RecoilCharacterPresentation : MonoBehaviour
    {
        private const float DeathFallbackDuration = 1.5f;
        private const float DeathFallbackDistance = 1.5f;
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

        private Transform _pendingTarget;
        private Vector3? _pendingTargetPosition;
        private Vector3? _queuedTargetPosition;
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
        private RecoilVisualProjectilePool _visualProjectilePool;

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
        internal int GetImpostorFrame(int framesPerAnimation)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return 0;
            var state = animator.GetCurrentAnimatorStateInfo(0);
            var attack = state.fullPathHash == _attackStateHash || state.shortNameHash == _attackShortHash;
            var bank = attack ? 2 : _movementSpeed > 0.05f ? 1 : 0;
            var phase = attack ? Mathf.Clamp01(state.normalizedTime) : Mathf.Repeat(state.normalizedTime, 1f);
            return bank * framesPerAnimation + Mathf.Min(framesPerAnimation - 1, Mathf.FloorToInt(phase * framesPerAnimation));
        }
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
            var visualScale = Vector3.one * scale;
            var visualPosition = Application.isPlaying ? Vector3.zero
                : (pivotOffset + Vector3.up * GetVisualGroundOffset()) * scale;
            if (visualRoot.localScale != visualScale) visualRoot.localScale = visualScale;
            if (visualRoot.localPosition != visualPosition) visualRoot.localPosition = visualPosition;
            if (!Application.isPlaying && _presentationGroundCorrection != 0f)
                visualRoot.position -= transform.TransformVector(Vector3.up * _presentationGroundCorrection);
            var visualRotation = Quaternion.Euler(0f, forwardYawOffset, 0f);
            if (visualRoot.localRotation != visualRotation) visualRoot.localRotation = visualRotation;
            _appliedForwardYawOffset = forwardYawOffset;
        }

        private float _presentationGroundCorrection;
        internal float PresentationGroundCorrection => Application.isPlaying ? 0f : _presentationGroundCorrection;
        internal Vector3 PresentationSinkOffset => _deathPresentationStarted && _deathSinkTransform != null && _deathSinkTransform.parent != null
            ? transform.InverseTransformVector(_deathSinkTransform.position-_deathSinkTransform.parent.TransformPoint(_deathSinkStartPosition)) : Vector3.zero;
        internal void SetGroundCorrection(float correction)
        {
            if (Mathf.Approximately(_presentationGroundCorrection,correction)) return;
            _presentationGroundCorrection = correction;
            ApplyVisualSettings();
        }

        internal void EnforceVisualRootTransform()
        {
            if (visualRoot == null || sourceModelHeight <= 0f) return;
            if (!_deathPresentationStarted) ApplyVisualSettings();
            else
            {
                // Death presentation owns the sinking position, while clips must not
                // overwrite the configured scale or heading of the whole model.
                visualRoot.localScale = Vector3.one*(normalizedVisualHeight/sourceModelHeight);
                visualRoot.localRotation = Quaternion.Euler(0f,forwardYawOffset,0f);
            }
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
            _pendingTargetPosition = null;
            _queuedTargetPosition = null;
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

        internal void ConfigureVisualProjectilePool(RecoilVisualProjectilePool pool) =>
            _visualProjectilePool = pool;

        public bool PlayAttack(Transform target, Vector3? targetPosition = null)
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
                _queuedTargetPosition = targetPosition;
                WakeAnimationWork();
                return true;
            }
            _queuedAttackTarget = null;
            _pendingTarget = target;
            // Keep an explicitly resolved ballistic intercept point. Tracking the live
            // Transform here makes the rendered projectile disagree with the combat
            // projectile, which already travels toward the intercept snapshot.
            _pendingTargetPosition = targetPosition;
            if (!_pendingTargetPosition.HasValue && target != null)
                _pendingTargetPosition = target.position;
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
                PlayAttack(_queuedAttackTarget, _queuedTargetPosition);
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
            var spawnRotation = projectileSpawnPoint.rotation *
                Quaternion.Euler(0f, _appliedForwardYawOffset, 0f);
            var destination = _pendingTargetPosition ??
                (_pendingTarget != null ? _pendingTarget.position : (Vector3?)null);
            if (_visualProjectilePool != null && destination.HasValue)
            {
                _visualProjectilePool.Spawn(projectilePrefab, projectileSpawnPoint.position,
                    spawnRotation, projectileScale, destination.Value, projectileSpeed);
            }
            else
            {
                var projectile = Instantiate(projectilePrefab, projectileSpawnPoint.position, spawnRotation);
                projectile.transform.localScale *= projectileScale;
                if (destination.HasValue)
                    projectile.AddComponent<RecoilVisualProjectileFlight>().Initialize(
                        destination.Value, projectileSpeed);
            }
            _pendingTarget = null;
            _pendingTargetPosition = null;
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
            _duration = RecoilCombatProjectileTrajectory.TravelDuration(horizontalDistance, _speed);
            _arcHeight = RecoilCombatProjectileTrajectory.ArcHeight(
                new float2(_start.x, _start.z), new float2(_destination.x, _destination.z));
            _initialized = true;
            _elapsed = 0f;
        }

        private void Update()
        {
            if (!_initialized)
                return;

            _elapsed = Mathf.Min(_elapsed + Time.deltaTime, _duration);
            var t = _elapsed / _duration;
            transform.position = RecoilCombatProjectileTrajectory.Position(
                (float3)_start, (float3)_destination, t, _arcHeight);

            if (t >= 1f)
            {
                transform.position = _destination;
                _initialized = false;
                Destroy(gameObject);
                return;
            }

            var tangent = (_destination - _start) / _duration +
                          Vector3.up * (4f * _arcHeight * (1f - 2f * t) / _duration);
            if (tangent.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(tangent);
        }
    }

    internal sealed class RecoilVisualProjectilePool : MonoBehaviour
    {
        private struct ActiveFlight
        {
            public GameObject Projectile;
            public GameObject Prefab;
            public Vector3 Start;
            public Vector3 Destination;
            public float ArcHeight;
            public float Duration;
            public float Elapsed;
            public float StartedAt;
            public bool SimulationDriven;
        }

        private const int MaxRetainedPerPrefab = 256;
        private readonly System.Collections.Generic.Dictionary<GameObject,
            System.Collections.Generic.Queue<GameObject>> _available = new();
        private readonly System.Collections.Generic.List<ActiveFlight> _activeFlights = new(256);

        internal void Spawn(GameObject prefab, Vector3 position, Quaternion rotation,
            float scale, Vector3 destination, float speed)
        {
            if (prefab == null || !float.IsFinite(speed) || speed <= 0f)
                return;

            if (!_available.TryGetValue(prefab, out var pool))
            {
                pool = new System.Collections.Generic.Queue<GameObject>();
                _available.Add(prefab, pool);
            }

            GameObject projectile = null;
            while (pool.Count > 0 && projectile == null)
                projectile = pool.Dequeue();
            if (projectile == null)
            {
                projectile = Instantiate(prefab, transform);
                projectile.SetActive(false);
            }

            projectile.transform.SetParent(transform, false);
            projectile.transform.SetPositionAndRotation(position, rotation);
            projectile.transform.localScale = prefab.transform.localScale * scale;
            projectile.SetActive(true);

            var horizontalDistance = Vector2.Distance(
                new Vector2(position.x, position.z), new Vector2(destination.x, destination.z));
            var duration = RecoilCombatProjectileTrajectory.TravelDuration(horizontalDistance, speed);
            _activeFlights.Add(new ActiveFlight
            {
                Projectile = projectile,
                Prefab = prefab,
                Start = position,
                Destination = destination,
                ArcHeight = RecoilCombatProjectileTrajectory.ArcHeight(
                    new float2(position.x, position.z), new float2(destination.x, destination.z)),
                Duration = duration,
                Elapsed = 0f,
                SimulationDriven = RecoilSimulationClock.IsRunning,
                StartedAt = RecoilSimulationClock.IsRunning ? RecoilSimulationClock.SimulationSeconds : Time.time
            });
        }

        internal void Release(GameObject prefab, GameObject projectile)
        {
            if (projectile == null)
                return;
            projectile.SetActive(false);
            projectile.transform.SetParent(transform, false);
            if (prefab == null || !_available.TryGetValue(prefab, out var pool))
            {
                Destroy(projectile);
                return;
            }
            if (pool.Count >= MaxRetainedPerPrefab)
            {
                Destroy(projectile);
                return;
            }
            pool.Enqueue(projectile);
        }

        private void LateUpdate()
        {
            for (var i = _activeFlights.Count - 1; i >= 0; i--)
            {
                var flight = _activeFlights[i];
                if (flight.Projectile == null)
                {
                    RemoveFlightAt(i);
                    continue;
                }

                var now = flight.SimulationDriven ? RecoilSimulationClock.SimulationSeconds : Time.time;
                flight.Elapsed = Mathf.Clamp(now - flight.StartedAt, 0f, flight.Duration);
                var t = flight.Duration > 0f ? flight.Elapsed / flight.Duration : 1f;
                var projectileTransform = flight.Projectile.transform;
                projectileTransform.position = RecoilCombatProjectileTrajectory.Position(
                    (float3)flight.Start, (float3)flight.Destination, t, flight.ArcHeight);
                if (t >= 1f)
                {
                    projectileTransform.position = flight.Destination;
                    Release(flight.Prefab, flight.Projectile);
                    RemoveFlightAt(i);
                    continue;
                }

                var tangent = (flight.Destination - flight.Start) / flight.Duration +
                    Vector3.up * (4f * flight.ArcHeight * (1f - 2f * t) / flight.Duration);
                if (tangent.sqrMagnitude > 0.0001f)
                    projectileTransform.rotation = Quaternion.LookRotation(tangent);
                _activeFlights[i] = flight;
            }
        }

        private void RemoveFlightAt(int index)
        {
            var last = _activeFlights.Count - 1;
            if (index != last)
                _activeFlights[index] = _activeFlights[last];
            _activeFlights.RemoveAt(last);
        }
    }

}
