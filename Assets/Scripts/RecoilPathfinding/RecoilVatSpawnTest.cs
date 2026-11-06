using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RecoilPathfinding
{
    public sealed class RecoilVatSpawnTest : MonoBehaviour
    {
        [Min(0)] public int UnitsPerPrefab = 300;
        [Min(0)] public int SiegePerPrefab = 10;
        [Min(1)] public int SpawnsPerFrame = 40;
        [Min(1)] public int Columns = 100;
        [Min(.1f)] public float Spacing = 4.5f;
        public bool UseVatLod = false;
        public bool UseDistanceLod = false;
        [Min(0f)] public float Lod2Distance = 10f;
        [Min(0f)] public float Lod3Distance = 20f;
        public int SpawnedCount { get; private set; }
        public int TargetCount { get; private set; }
        private readonly List<GameObject> _units = new();
        private readonly List<(LODGroup Group, Transform Root, int Level)> _lods = new();
        private Camera _camera;
        private float _nextLodUpdate;
        private readonly List<(GameObject Prefab, int Count)> _plan = new();
        private int _entry, _entryCount, _ordinal;
        private float _nextCount;
        private string _label = "Preparing VAT prefabs...";
        private RecoilVatPrefabLibrary _library;

        private void Start()
        {
            _library = UseVatLod && RecoilVatPrefabLibrary.LodEnabled ? Resources.Load<RecoilVatPrefabLibrary>("RecoilVatLodPrefabs") : null;
            if (_library == null) _library = Resources.Load<RecoilVatPrefabLibrary>("RecoilVatPrefabs");
            if (_library == null) { _label = "Missing RecoilVatPrefabs library. Bake VAT prefabs first."; enabled = false; return; }
            QueueBatch();
        }
        private void QueueBatch()
        {
            if (_library == null) return;
            // Completed batches need not stay in the queue; keep any unfinished batch intact.
            if (_entry >= _plan.Count) { _plan.Clear(); _entry = 0; _entryCount = 0; }
            foreach (var entry in _library.Entries)
            {
                if (entry.Source == null || entry.Vat == null) continue;
                var count = entry.Source.name.StartsWith("maybanda", System.StringComparison.OrdinalIgnoreCase)
                    ? Mathf.Max(0, SiegePerPrefab) : Mathf.Max(0, UnitsPerPrefab);
                _plan.Add((entry.Vat, count)); TargetCount += count;
            }
            RefreshCount();
        }
        private void Update()
        {
            var keyboard = Keyboard.current;
            if (Application.isFocused && keyboard != null && keyboard.pKey.wasPressedThisFrame)
                QueueBatch();
            for (var i = 0; i < Mathf.Max(1, SpawnsPerFrame) && _entry < _plan.Count;)
            {
                var item = _plan[_entry];
                if (_entryCount >= item.Count) { _entry++; _entryCount = 0; continue; }
                var instance = Instantiate(item.Prefab, transform);
                var columns = Mathf.Max(1, Columns);
                var position = new Vector3((_ordinal % columns - (columns - 1) * .5f) * Spacing,
                    0, (_ordinal / columns) * Spacing);
                instance.transform.localPosition = position;
                _units.Add(instance);
                var lod = instance.GetComponent<LODGroup>();
                if (lod != null)
                {
                    var level = DesiredLod(instance.transform.position, lod.lodCount);
                    lod.ForceLOD(level);
                    _lods.Add((lod, instance.transform, level));
                }
                _ordinal++; _entryCount++; i++;
            }
            if (Time.unscaledTime >= _nextCount)
            {
                _nextCount = Time.unscaledTime + .25f;
                RefreshCount();
            }
            if (Time.unscaledTime >= _nextLodUpdate)
            {
                _nextLodUpdate = Time.unscaledTime + .1f;
                for (var i = _lods.Count - 1; i >= 0; i--)
                {
                    var item = _lods[i];
                    if (item.Group == null) { _lods.RemoveAt(i); continue; }
                    var level = DesiredLod(item.Root.position, item.Group.lodCount);
                    if (level == item.Level) continue;
                    item.Group.ForceLOD(level);
                    _lods[i] = (item.Group, item.Root, level);
                }
            }
        }
        private int DesiredLod(Vector3 position, int count)
        {
            if (!RecoilVatPrefabLibrary.LodEnabled) return 0;
            if (_camera == null) _camera = Camera.main;
            if (_camera == null || !UseVatLod || !UseDistanceLod) return -1;
            var distanceSquared = (_camera.transform.position - position).sqrMagnitude;
            var near = Mathf.Max(0, Lod2Distance);
            var far = Mathf.Max(near, Lod3Distance);
            var level = distanceSquared >= far * far ? 2 : distanceSquared >= near * near ? 1 : 0;
            return Mathf.Clamp(level, 0, Mathf.Max(0, count - 1));
        }
        private void OnDisable()
        {
            foreach (var item in _lods) if (item.Group != null) item.Group.ForceLOD(-1);
        }
        private void RefreshCount()
        {
            SpawnedCount = 0;
            foreach (var unit in _units) if (unit != null && unit.activeInHierarchy) SpawnedCount++;
            _label = $"VAT units: {SpawnedCount:N0} / {TargetCount:N0}   |   P: spawn more";
        }
        private void OnGUI() => GUI.Box(new Rect(12, 12, 360, 36), _label);
    }
}
