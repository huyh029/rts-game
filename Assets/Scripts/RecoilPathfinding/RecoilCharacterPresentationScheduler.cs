using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace RecoilPathfinding
{
    // One Unity callback for all character wrappers. Animator evaluation remains Unity's job.
    [DefaultExecutionOrder(10000)]
    public sealed class RecoilCharacterPresentationScheduler : MonoBehaviour
    {
        internal sealed class Registry
        {
            private readonly List<RecoilCharacterPresentation> _active = new();
            private readonly Dictionary<RecoilCharacterPresentation, int> _indices = new();
            private bool _ticking;
            private bool _needsCompaction;
            public int Count => _indices.Count;

            public void Add(RecoilCharacterPresentation view)
            {
                if (view == null || _indices.ContainsKey(view)) return;
                _indices.Add(view, _active.Count);
                _active.Add(view);
            }

            public void Remove(RecoilCharacterPresentation view)
            {
                if (ReferenceEquals(view, null) || !_indices.TryGetValue(view, out var index)) return;
                _indices.Remove(view);
                if (_ticking)
                {
                    // Death callbacks may disable several units, including earlier slots.
                    // Keep indices stable until the current traversal finishes.
                    _active[index] = null;
                    _needsCompaction = true;
                    return;
                }
                RemoveSlot(index);
            }

            private void RemoveSlot(int index)
            {
                var last = _active.Count - 1;
                if (index != last)
                {
                    var moved = _active[last];
                    _active[index] = moved;
                    if (!ReferenceEquals(moved, null)) _indices[moved] = index;
                }
                _active.RemoveAt(last);
            }

            public void Tick(Action<RecoilCharacterPresentation> visitor = null)
            {
                if (_ticking) throw new InvalidOperationException("Presentation tick cannot be nested.");
                _ticking = true;
                var count = _active.Count; // New registrations begin next frame.
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var view = _active[i];
                        if (view == null || !view.isActiveAndEnabled) continue;
                        if (visitor != null) visitor(view);
                        else
                        {
                            // Preserve Unity's per-component exception isolation.
                            try
                            {
                                view.TickPresentation();
                                if (!view.HasPendingAnimationWork) Remove(view);
                            }
                            catch (Exception exception) { Debug.LogException(exception, view); }
                        }
                    }
                }
                finally
                {
                    _ticking = false;
                    if (_needsCompaction)
                    {
                        for (var i = _active.Count - 1; i >= 0; i--)
                            if (ReferenceEquals(_active[i], null)) RemoveSlot(i);
                        _needsCompaction = false;
                    }
                }
            }
        }

        private static RecoilCharacterPresentationScheduler _instance;
        private static readonly ProfilerMarker TickMarker = new("Recoil.CharacterPresentation.Batch");
        private readonly Registry _registry = new();
        public int ActiveCharacterCount => _registry.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _instance = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // Also covers Enter Play Mode with domain/scene reload disabled.
            foreach (var view in FindObjectsByType<RecoilCharacterPresentation>(FindObjectsSortMode.None))
                if (view.isActiveAndEnabled) Register(view);
        }

        internal static void Register(RecoilCharacterPresentation view)
        {
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<RecoilCharacterPresentationScheduler>();
                if (_instance == null)
                {
                    var host = new GameObject("Character Presentation Scheduler");
                    _instance = host.AddComponent<RecoilCharacterPresentationScheduler>();
                    DontDestroyOnLoad(host);
                }
            }
            _instance._registry.Add(view);
        }

        internal static void Unregister(RecoilCharacterPresentation view)
        {
            if (_instance != null) _instance._registry.Remove(view);
        }

        private void Update()
        {
            using (TickMarker.Auto()) _registry.Tick();
        }

        private void LateUpdate()
        {
            _registry.Tick(view => view.ClampDeathAnimatorRootHeight());
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

#if UNITY_EDITOR
        public static void ValidateRegistry()
        {
            if (Application.isPlaying) throw new InvalidOperationException("Run registry checks outside Play Mode.");
            var objects = new GameObject[5];
            try
            {
                var views = new RecoilCharacterPresentation[5];
                for (var i = 0; i < views.Length; i++)
                {
                    objects[i] = new GameObject("Presentation registry check") { hideFlags = HideFlags.HideAndDontSave };
                    views[i] = objects[i].AddComponent<RecoilCharacterPresentation>();
                }
                var registry = new Registry();
                for (var i = 0; i < 4; i++) registry.Add(views[i]);
                registry.Add(views[0]);
                if (registry.Count != 4) throw new InvalidOperationException("Duplicate registration.");
                var visited = new List<RecoilCharacterPresentation>();
                registry.Tick(view =>
                {
                    visited.Add(view);
                    if (view == views[0])
                    {
                        registry.Remove(views[0]);
                        registry.Remove(views[1]);
                        registry.Add(views[4]);
                    }
                    if (view == views[2]) registry.Remove(views[3]);
                });
                if (visited.Count != 2 || visited[0] != views[0] || visited[1] != views[2] || registry.Count != 2)
                    throw new InvalidOperationException("Callback mutations skipped or duplicated a presentation.");
                var count = 0;
                registry.Tick(view => { count++; registry.Remove(view); });
                if (count != 2 || registry.Count != 0) throw new InvalidOperationException("Compaction lost an entry.");
                registry.Add(views[1]);
                objects[1].SetActive(false);
                registry.Tick(_ => throw new InvalidOperationException("Inactive presentation was updated."));
                objects[1].SetActive(true);
                count = 0;
                registry.Tick(_ => count++);
                if (count != 1) throw new InvalidOperationException("Reused presentation was not updated once.");
                registry.Tick();
                if (registry.Count != 0) throw new InvalidOperationException("Completed presentation work must leave the active list.");
                registry.Tick(); // Empty list must remain empty without polling sleeping views.
            }
            finally
            {
                foreach (var item in objects) if (item != null) DestroyImmediate(item);
            }
        }
#endif
    }
}
