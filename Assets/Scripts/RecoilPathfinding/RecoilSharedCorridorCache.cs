using System;
using System.Collections;
using System.Collections.Generic;

namespace RecoilPathfinding
{
    // Main-thread cache owned by one goal/area flow field. Views are transient and must
    // be consumed before another Store/Clear; only the final waypoint array escapes.
    internal sealed class RecoilSharedCorridorCache
    {
        private readonly RecoilPathPortal[] _storage;
        private readonly PortalView[] _views;
        private readonly Dictionary<int, int> _slots;
        private int _used, _count;
        public int Count => _count;
        public int StoredPortalCount => _used;

        public RecoilSharedCorridorCache(int maxEntries = 64, int maxPortals = 4096)
        {
            if (maxEntries < 1 || maxPortals < 1) throw new ArgumentOutOfRangeException();
            _storage = new RecoilPathPortal[maxPortals];
            _views = new PortalView[maxEntries];
            for (var i = 0; i < _views.Length; i++) _views[i] = new PortalView(_storage);
            _slots = new Dictionary<int, int>(maxEntries);
        }

        public bool TryGet(int startTriangle, out IReadOnlyList<RecoilPathPortal> portals)
        {
            if (_slots.TryGetValue(startTriangle, out var slot)) { portals = _views[slot]; return true; }
            portals = null;
            return false;
        }

        public bool Store(int startTriangle, IReadOnlyList<RecoilPathPortal> portals)
        {
            if (_slots.ContainsKey(startTriangle)) return true;
            var length = portals.Count;
            if (length > _storage.Length) return false;
            if (_count == _views.Length || _used + length > _storage.Length) Clear();
            for (var i = 0; i < length; i++) _storage[_used + i] = portals[i];
            _views[_count].Set(_used, length);
            _slots.Add(startTriangle, _count++);
            _used += length;
            return true;
        }

        public void Clear() { _slots.Clear(); _used = _count = 0; }

        private sealed class PortalView : IReadOnlyList<RecoilPathPortal>
        {
            private readonly RecoilPathPortal[] _storage;
            private int _start;
            public int Count { get; private set; }
            public PortalView(RecoilPathPortal[] storage) => _storage = storage;
            public void Set(int start, int count) { _start = start; Count = count; }
            public RecoilPathPortal this[int index] => index >= 0 && index < Count
                ? _storage[_start + index] : throw new ArgumentOutOfRangeException(nameof(index));
            public IEnumerator<RecoilPathPortal> GetEnumerator()
            { for (var i = 0; i < Count; i++) yield return this[i]; }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
