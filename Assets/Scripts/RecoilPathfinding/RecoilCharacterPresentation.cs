using UnityEngine;

namespace RecoilPathfinding
{
    [DefaultExecutionOrder(10000)]
    public sealed class RecoilCharacterPresentation : MonoBehaviour
    {
        private const float DeathFallbackDuration = 1.5f;
        private const float DeathFallbackDistance = 1.5f;
        private const float DeathStateEntryTimeout = 0.25f;
        private const float DeathPlaybackTimeout = 8f;

        [SerializeField] private Animator animator;
        [SerializeField] private Transform visualRoot;
        [SerializeField] private AnimationClip attackClip;
        [SerializeField, Min(0.001f)] private float sourceModelHeight = 1f;
        [SerializeField] private Vector3 pivotOffset;
        [SerializeField] private Transform projectileSpawnPoint;
        [SerializeField] private GameObject projectilePrefab;
        [SerializeField, Min(0.001f)] private float projectileScale = 1f;
        [SerializeField] private string attackTrigger = "Attack";
        [SerializeField] private string attackStateName = "Attack";
        [SerializeField, Min(0.01f)] private float normalizedVisualHeight = 1.8f;
        [SerializeField] private float forwardYawOffset;
        [SerializeField] private float attackForwardYawOffset;
        [SerializeField, Range(0f, 1f)] private float projectileReleaseNormalizedTime = 0.5f;
        [SerializeField, Min(0.1f)] private float projectileSpeed = 24f;

        private Transform _pendingTarget;
        private bool _releasePending;
        private float _appliedForwardYawOffset;
        private Animator[] _deathAnimators;
        private System.Action _deathPresentationCompleted;
        private bool _deathPresentationStarted;
        private Transform _deathFallbackTransform;
        private Vector3 _deathFallbackStartPosition;
        private float _deathFallbackElapsed;
        private float _deathElapsed;

        public Animator Animator => animator;
        public Transform VisualRoot => visualRoot;
        public AnimationClip AttackClip => attackClip;
        public Transform ProjectileSpawnPoint => projectileSpawnPoint;
        public GameObject ProjectilePrefab => projectilePrefab;
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
            ApplyVisualSettings();
        }

        public void ApplyVisualSettings()
        {
            if (visualRoot == null || !float.IsFinite(sourceModelHeight) || sourceModelHeight <= 0f)
                return;
            var scale = normalizedVisualHeight / sourceModelHeight;
            visualRoot.localScale = Vector3.one * scale;
            visualRoot.localPosition = pivotOffset * scale;
            visualRoot.localRotation = Quaternion.Euler(0f, forwardYawOffset, 0f);
            _appliedForwardYawOffset = forwardYawOffset;
        }

        public void ResetForReuse()
        {
            if (_deathFallbackTransform != null)
                _deathFallbackTransform.localPosition = _deathFallbackStartPosition;
            _pendingTarget = null;
            _releasePending = false;
            _deathPresentationCompleted = null;
            _deathPresentationStarted = false;
            _deathFallbackTransform = null;
            _deathFallbackElapsed = 0f;
            _deathElapsed = 0f;
            ApplyVisualSettings();

            if (visualRoot == null)
                return;
            foreach (var childAnimator in visualRoot.GetComponentsInChildren<Animator>(true))
            {
                if (childAnimator == null)
                    continue;
                childAnimator.Rebind();
                childAnimator.Update(0f);
            }
        }

        public bool PlayAttack(Transform target)
        {
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

            _pendingTarget = target;
            _releasePending = false;
            var attackStateHash = Animator.StringToHash($"Base Layer.{attackStateName}");
            var played = false;
            foreach (var childAnimator in visualRoot.GetComponentsInChildren<Animator>(true))
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
                              projectilePrefab != null && projectileSpawnPoint != null;
            return true;
        }

        public bool PlayDeath(System.Action onCompleted = null)
        {
            if (_deathPresentationStarted)
                return true;

            var deathStateHash = Animator.StringToHash("Base Layer.Death");
            var deathAnimators = visualRoot != null
                ? visualRoot.GetComponentsInChildren<Animator>(true)
                : GetComponentsInChildren<Animator>(true);
            var activeDeathAnimators = new System.Collections.Generic.List<Animator>();
            foreach (var childAnimator in deathAnimators)
            {
                if (childAnimator == null || !childAnimator.isActiveAndEnabled ||
                    childAnimator.runtimeAnimatorController == null ||
                    !childAnimator.HasState(0, deathStateHash))
                    continue;

                childAnimator.Play(deathStateHash, 0, 0f);
                activeDeathAnimators.Add(childAnimator);
            }

            _deathPresentationStarted = true;
            _deathPresentationCompleted = onCompleted;
            _deathAnimators = activeDeathAnimators.ToArray();
            _releasePending = false;
            _deathElapsed = 0f;
            if (activeDeathAnimators.Count == 0)
                BeginDeathFallback();
            return true;
        }

        private void Update()
        {
            UpdateDeathPresentation();
            UpdateAttackFacing();
            UpdateLocomotionAnimation();
            if (!_releasePending || animator == null || projectilePrefab == null || projectileSpawnPoint == null)
                return;

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

        private void UpdateLocomotionAnimation()
        {
            if (_deathPresentationStarted || visualRoot == null)
                return;

            var agent = GetComponent<RecoilNavMeshPathAgent>();
            if (agent == null)
                return;

            var moving = agent.CurrentSpeed > 0.05f;
            var animators = visualRoot.GetComponentsInChildren<Animator>(true);
            foreach (var childAnimator in animators)
            {
                if (childAnimator == null || !childAnimator.isActiveAndEnabled ||
                    childAnimator.runtimeAnimatorController == null)
                    continue;

                var currentState = childAnimator.GetCurrentAnimatorStateInfo(0);
                if (IsAttackOrDeathState(currentState))
                    continue;
                if (childAnimator.IsInTransition(0) &&
                    IsAttackOrDeathState(childAnimator.GetNextAnimatorStateInfo(0)))
                    continue;

                var stateName = moving ? "Run" : "Idle";
                var stateHash = Animator.StringToHash($"Base Layer.{stateName}");
                if (!childAnimator.HasState(0, stateHash))
                {
                    if (moving)
                        continue;
                    stateName = "Idle";
                    stateHash = Animator.StringToHash("Base Layer.Idle");
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
            return state.IsName(attackStateName) ||
                   state.IsName($"Base Layer.{attackStateName}") ||
                   state.IsName("Death") ||
                   state.IsName("Base Layer.Death");
        }

        private void UpdateAttackFacing()
        {
            if (_deathPresentationStarted || visualRoot == null || animator == null)
                return;

            var state = animator.GetCurrentAnimatorStateInfo(0);
            var targetYaw = state.IsName(attackStateName)
                ? attackForwardYawOffset
                : forwardYawOffset;
            if (Mathf.Approximately(targetYaw, _appliedForwardYawOffset))
                return;

            visualRoot.localRotation = Quaternion.Euler(0f, targetYaw, 0f);
            _appliedForwardYawOffset = targetYaw;
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

            foreach (var deathAnimator in _deathAnimators)
            {
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
                    if (_deathElapsed >= DeathPlaybackTimeout)
                    {
                        BeginDeathFallback();
                        return;
                    }
                    return;
                }
            }

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
