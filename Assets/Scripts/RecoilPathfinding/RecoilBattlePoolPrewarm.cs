using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

namespace RecoilPathfinding
{
    public sealed partial class RecoilAutoBattleDemo
    {
        [Header("Pool chuẩn bị trước trận")]
        [SerializeField] private bool prewarmSoldierPools = true;
        [SerializeField, Min(0)] private int prewarmPerTypePerTeam = 1000;
        [SerializeField, Min(1)] private int prewarmMaxCreatesPerFrame = 16;
        [SerializeField, Min(0.5f)] private float prewarmFrameBudgetMs = 4f;
        [SerializeField, Min(1f)] private float prewarmMaxSeconds = 60f;
        [SerializeField, Min(64)] private int prewarmMemoryBudgetMb = 1024;
        private Transform _inactiveUnitPoolRoot;
        private Coroutine _poolPrewarmCoroutine;
        private bool _poolPrewarmRunning, _poolPrewarmFinished;
        private string _poolPrewarmStatus;
        private float _poolPrewarmProgress;
        private string _poolPrewarmLabel = "Đang tải… 0%";
        private GUIStyle _poolPrewarmLabelStyle;
        public string PoolPrewarmSummary => _poolPrewarmStatus;

        private sealed class PrewarmPool
        {
            public GameObject Template;
            public bool Blue;
            public Queue<GameObject> Pool;
        }

        private GameObject CreateInactivePooledUnit(GameObject template, bool blue)
        {
            if (_inactiveUnitPoolRoot == null)
            {
                var root = new GameObject("Inactive Soldier Pool");
                root.SetActive(false);
                root.transform.SetParent(transform, false);
                _inactiveUnitPoolRoot = root.transform;
            }
            // Instantiating under an inactive parent prevents OnEnable from registering
            // animation/rendering work before this model is actually bound to a soldier.
            var unit = Instantiate(ResolvePresentationTemplate(template), _inactiveUnitPoolRoot);
            unit.SetActive(false);
            var type = Mathf.Clamp(GetCharacterType(template), 0, CharacterTypeCount - 1);
            if (blue) _bluePresentationModelsCreatedByType[type]++;
            else _redPresentationModelsCreatedByType[type]++;
            return unit;
        }

        private void BeginPoolPrewarm()
        {
            if (_poolPrewarmRunning) return;
            _poolPrewarmRunning = true;
            _poolPrewarmProgress = 0f;
            _poolPrewarmLabel = "Đang tải… 0%";
            _poolPrewarmCoroutine = StartCoroutine(PrewarmPoolsBeforeMatch());
        }

        private IEnumerator PrewarmPoolsBeforeMatch()
        {
            // First yield lets the lobby display its loading state before creating any models.
            _lobbyError = null;
            yield return null;
            var pools = new List<PrewarmPool>();
            var blueTemplates = new HashSet<GameObject>();
            var redTemplates = new HashSet<GameObject>();
            for (var type = 0; type < CharacterTypeCount; type++)
                for (var team = 0; team < 2; team++)
                {
                    var blue = team == 0;
                    var template = FindTemplateForType(blue, type);
                    var seen = blue ? blueTemplates : redTemplates;
                    if (template != null && seen.Add(template))
                        pools.Add(new PrewarmPool { Template = template, Blue = blue, Pool = GetUnitPool(blue, template) });
                }
            var target = Mathf.Max(0, prewarmPerTypePerTeam);
            var baselineMemory = Profiler.GetTotalAllocatedMemoryLong();
            var budgetBytes = (long)Mathf.Max(64, prewarmMemoryBudgetMb) * 1024 * 1024;
            if (SystemInfo.systemMemorySize > 0)
                budgetBytes = System.Math.Min(budgetBytes, (long)SystemInfo.systemMemorySize * 1024 * 1024 / 4);
            var totalMemoryLimit = SystemInfo.systemMemorySize > 0
                ? (long)SystemInfo.systemMemorySize * 1024 * 1024 * 3 / 4 : long.MaxValue;
            var start = Time.realtimeSinceStartupAsDouble;
            var lastStatus = -1d;
            var cursor = 0;
            var created = 0;
            var stopReason = "đủ số lượng";
            var finished = false;
            var watch = new System.Diagnostics.Stopwatch();
            while (!finished && pools.Count > 0)
            {
                var hasShortfall = false;
                foreach (var pool in pools) if (pool.Pool.Count < target) { hasShortfall = true; break; }
                if (!hasShortfall) break;
                watch.Restart();
                var creates = 0;
                var inspected = 0;
                while (creates < Mathf.Max(1, prewarmMaxCreatesPerFrame) &&
                    watch.Elapsed.TotalMilliseconds < Mathf.Max(0.5f, prewarmFrameBudgetMs))
                {
                    var memory = Profiler.GetTotalAllocatedMemoryLong();
                    if (memory - baselineMemory >= budgetBytes || memory >= totalMemoryLimit)
                    { stopReason = "giới hạn bộ nhớ"; finished = true; break; }
                    if (Time.realtimeSinceStartupAsDouble - start >= Mathf.Max(1f, prewarmMaxSeconds))
                    { stopReason = "giới hạn thời gian tải"; finished = true; break; }
                    var pool = pools[cursor];
                    cursor = (cursor + 1) % pools.Count;
                    if (pool.Pool.Count >= target)
                    {
                        if (++inspected >= pools.Count) { finished = true; break; }
                        continue;
                    }
                    inspected = 0;
                    GameObject model = null;
                    try { model = CreateInactivePooledUnit(pool.Template, pool.Blue); }
                    catch (System.Exception e) { Debug.LogException(e, this); stopReason = "không tạo được prefab"; }
                    if (model == null) { finished = true; break; }
                    pool.Pool.Enqueue(model);
                    creates++; created++;
                }
                var ready = 0;
                foreach (var pool in pools) ready += Mathf.Min(target, pool.Pool.Count);
                _poolPrewarmProgress = target > 0 && pools.Count > 0
                    ? Mathf.Clamp01(ready / ((float)target * pools.Count)) : 1f;
                if (Time.realtimeSinceStartupAsDouble - lastStatus >= 0.25d || finished)
                {
                    _poolPrewarmLabel = $"Đang tải… {_poolPrewarmProgress * 100f:F0}%";
                    lastStatus = Time.realtimeSinceStartupAsDouble;
                }
                if (!finished) yield return null;
            }
            var minimum = pools.Count > 0 ? int.MaxValue : 0;
            foreach (var pool in pools) minimum = Mathf.Min(minimum, pool.Pool.Count);
            var seconds = Time.realtimeSinceStartupAsDouble - start;
            var addedMb = (Profiler.GetTotalAllocatedMemoryLong() - baselineMemory) / (1024d * 1024d);
            _poolPrewarmStatus = $"Pool: ít nhất {minimum:N0}/{target:N0} lính mỗi mẫu/phe; {stopReason}.";
            Debug.Log($"[Soldier pool prewarm] Created={created:N0}, minimum per template/team={minimum}/{target}, " +
                $"elapsed={seconds:F1}s, added Unity allocated memory={addedMb:F1} MB, stop={stopReason}. " +
                "Hidden models have no simulation entities or GPU skin registrations; normal spawn fills any shortfall.", this);
            _poolPrewarmFinished = true;
            _poolPrewarmProgress = 1f;
            _poolPrewarmRunning = false;
            _poolPrewarmCoroutine = null;
            if (isActiveAndEnabled && !_lobbyStarted) StartMatchFromLobby();
        }

        private void CancelPoolPrewarm()
        {
            if (_poolPrewarmCoroutine != null) StopCoroutine(_poolPrewarmCoroutine);
            _poolPrewarmCoroutine = null;
            _poolPrewarmRunning = false;
        }

        private void DrawPoolLoadingBar(Rect rect)
        {
            if (_poolPrewarmLabelStyle == null)
            {
                _poolPrewarmLabelStyle = new GUIStyle(GUI.skin.label)
                { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                _poolPrewarmLabelStyle.normal.textColor = new Color(0.95f, 0.98f, 1f, 1f);
            }
            _poolPrewarmLabelStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(rect.height * 0.48f), 12, 24);
            var radius = rect.height * 0.5f;
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f,
                new Color(0.055f, 0.075f, 0.095f, 0.94f), 0f, radius);
            var fillWidth = rect.width * Mathf.Clamp01(_poolPrewarmProgress);
            if (fillWidth > 0f)
                GUI.DrawTexture(new Rect(rect.x, rect.y, fillWidth, rect.height), Texture2D.whiteTexture,
                    ScaleMode.StretchToFill, true, 0f, new Color(0.12f, 0.7f, 0.52f, 1f),
                    0f, Mathf.Min(radius, fillWidth * 0.5f));
            GUI.Label(rect, _poolPrewarmLabel, _poolPrewarmLabelStyle);
        }

#if UNITY_EDITOR
        public static void ValidateInactiveSoldierPool()
        {
            var owner = new GameObject("Temporary soldier pool check");
            owner.SetActive(false);
            var template = new GameObject("BO_BINH_blue");
            template.SetActive(false);
            template.AddComponent<RecoilNavMeshPathAgent>();
            try
            {
                var battle = owner.AddComponent<RecoilAutoBattleDemo>();
                var prepared = battle.CreateInactivePooledUnit(template, true);
                if (prepared.activeSelf || prepared.activeInHierarchy ||
                    prepared.GetComponent<RecoilNavMeshPathAgent>().Simulation != null || battle._gpuSkinnedCrowd != null)
                    throw new System.InvalidOperationException("Prewarmed models must stay inactive without simulation or GPU registrations.");
                battle.GetUnitPool(true, template).Enqueue(prepared);
                // Pool hits bypass the creation budget and preserve the existing instance.
                battle._newUnitsCreatedThisFrame = int.MaxValue;
                var reused = battle.GetOrCreatePooledUnit(template, true);
                if (reused != prepared || reused.activeSelf || reused.transform.parent != battle.transform)
                    throw new System.InvalidOperationException("Pool checkout must reuse and reparent the inactive model.");
                battle._newUnitsCreatedThisFrame = 0;
                var fresh = battle.GetOrCreatePooledUnit(template, false);
                if (fresh == null || fresh.activeSelf || fresh.transform.parent != battle.transform ||
                    fresh.transform.parent == battle._inactiveUnitPoolRoot)
                    throw new System.InvalidOperationException("A new model must leave the inactive pool root before spawning.");
                if (battle._newUnitsCreatedThisFrame != 1 || battle.GetUnitPool(true, template).Count != 0)
                    throw new System.InvalidOperationException("Creation budgeting and pool counts must remain correct.");
                Debug.Log("Soldier pool checks passed: inactive prewarm, no simulation/GPU registration, pool reuse, active-root parenting and creation budget. Test 500/type/team through Start to measure actual loading time and memory.");
            }
            finally
            {
                DestroyImmediate(owner);
                DestroyImmediate(template);
            }
        }
#endif
    }
}
