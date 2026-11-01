using System;
using UnityEditor;
using UnityEngine;

namespace RecoilPathfindingEditor
{
    public sealed class RecoilTerrainMapBaker : EditorWindow
    {
        private Terrain _terrain;
        private float _cellSize = 1f;
        private float _maxSlope = 45f;
        private string _result = "";
        private const long MaxCells = 4_000_000;

        [MenuItem("Tools/Recoil Pathfinding/Bake Terrain Map")]
        public static void Open() => GetWindow<RecoilTerrainMapBaker>("Recoil Terrain Map");

        private void OnEnable() => _terrain = Terrain.activeTerrain;

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Bake terrain data without NavMesh", EditorStyles.boldLabel);
            _terrain = (Terrain)EditorGUILayout.ObjectField("Source Terrain", _terrain, typeof(Terrain), true);
            _cellSize = EditorGUILayout.FloatField("Cell size (world units)", _cellSize);
            _maxSlope = EditorGUILayout.Slider("Maximum slope", _maxSlope, 0f, 89f);
            EditorGUILayout.HelpBox("Saves world origin, grid heights and walkable cells. Holes and steep slopes are blocked. " +
                "Buildings, trees, mesh surfaces and moving obstacles are not included. " +
                "Assign the saved asset in Resources/RecoilTerrainBackend to use it in the battle.", MessageType.Info);
            var valid = _terrain != null && _terrain.terrainData != null && float.IsFinite(_cellSize) && _cellSize >= 0.1f;
            if (valid)
            {
                var size = _terrain.terrainData.size;
                var count = Math.Ceiling(size.x / _cellSize) * Math.Ceiling(size.z / _cellSize);
                EditorGUILayout.LabelField($"Cells: {count:N0} | Height/cell payload approximately {count * 5 / (1024 * 1024):F1} MiB");
                valid = count <= MaxCells && !EditorApplication.isPlaying;
            }
            using (new EditorGUI.DisabledScope(!valid))
                if (GUILayout.Button("Bake and save asset")) BakeAndSave();
            if (_result.Length > 0) EditorGUILayout.HelpBox(_result, MessageType.Info);
        }

        private void BakeAndSave()
        {
            var path = EditorUtility.SaveFilePanelInProject("Save baked terrain map", "RecoilTerrainMap", "asset", "Choose an asset location.");
            if (string.IsNullOrEmpty(path)) return;
            RecoilPathfinding.RecoilTerrainMapData baked = null;
            try
            {
                baked = Bake(_terrain, _cellSize, _maxSlope, (progress) =>
                    EditorUtility.DisplayCancelableProgressBar("Recoil terrain bake", "Sampling heights, slopes and holes", progress));
                // Update existing assets in place to keep their GUID and references.
                var existing = AssetDatabase.LoadMainAssetAtPath(path);
                if (existing != null && !(existing is RecoilPathfinding.RecoilTerrainMapData))
                    throw new InvalidOperationException("Destination is not a Recoil terrain map asset.");
                if (existing != null)
                {
                    Undo.RecordObject(existing, "Rebake terrain map");
                    EditorUtility.CopySerialized(baked, existing);
                    EditorUtility.SetDirty(existing);
                }
                else { AssetDatabase.CreateAsset(baked, path); baked = null; }
                AssetDatabase.SaveAssets();
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<RecoilPathfinding.RecoilTerrainMapData>(path);
                _result = $"Saved: {path}. Rebake after terrain edits.";
                Debug.Log(_result, Selection.activeObject);
            }
            catch (OperationCanceledException) { _result = "Bake cancelled; no asset was changed."; }
            catch (Exception ex) { _result = ex.Message; Debug.LogException(ex); }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (baked != null) DestroyImmediate(baked);
            }
        }

        public static RecoilPathfinding.RecoilTerrainMapData Bake(Terrain terrain, float cellSize,
            float maxSlope, Func<float, bool> cancel = null)
        {
            if (terrain == null || terrain.terrainData == null || !float.IsFinite(cellSize) || cellSize < 0.1f ||
                !float.IsFinite(maxSlope) || maxSlope < 0f || maxSlope >= 90f)
                throw new ArgumentException("Select a terrain and valid cell size/slope limit.");
            if (Quaternion.Angle(terrain.transform.rotation, Quaternion.identity) > 0.01f ||
                Vector3.Distance(terrain.transform.lossyScale, Vector3.one) > 0.001f)
                throw new InvalidOperationException("Terrain must have identity rotation and unit world scale.");
            var source = terrain.terrainData;
            var size = source.size;
            var cellColumns = Math.Ceiling(size.x / cellSize);
            var cellRows = Math.Ceiling(size.z / cellSize);
            if (cellColumns < 1 || cellRows < 1 || cellColumns * cellRows > MaxCells)
                throw new InvalidOperationException("Grid exceeds 4 million cells or has invalid dimensions. Increase cell size.");
            var columns = (int)cellColumns + 1;
            var rows = (int)cellRows + 1;
            var heights = new float[columns * rows];
            var slopes = new float[columns * rows];
            var cells = new byte[(columns - 1) * (rows - 1)];
            var holes = source.GetHoles(0, 0, source.holesResolution, source.holesResolution);
            for (var z = 0; z < rows; z++)
            {
                if (cancel != null && cancel(0.5f * z / rows)) throw new OperationCanceledException();
                var v = (float)z / (rows - 1);
                for (var x = 0; x < columns; x++)
                {
                    var u = (float)x / (columns - 1);
                    heights[z * columns + x] = source.GetInterpolatedHeight(u, v);
                    slopes[z * columns + x] = source.GetSteepness(u, v);
                }
            }
            for (var z = 0; z < rows - 1; z++)
            {
                if (cancel != null && cancel(0.5f + 0.5f * z / (rows - 1))) throw new OperationCanceledException();
                for (var x = 0; x < columns - 1; x++)
                {
                    var i = z * columns + x;
                    var slope = Mathf.Max(Mathf.Max(slopes[i], slopes[i + 1]), Mathf.Max(slopes[i + columns], slopes[i + columns + 1]));
                    slope = Mathf.Max(slope, source.GetSteepness((x + 0.5f) / (columns - 1), (z + 0.5f) / (rows - 1)));
                    var solid = true;
                    var minX = x * source.holesResolution / (columns - 1);
                    var maxX = Mathf.Min(source.holesResolution - 1, Mathf.CeilToInt((float)(x + 1) * source.holesResolution / (columns - 1)) - 1);
                    var minZ = z * source.holesResolution / (rows - 1);
                    var maxZ = Mathf.Min(source.holesResolution - 1, Mathf.CeilToInt((float)(z + 1) * source.holesResolution / (rows - 1)) - 1);
                    for (var hz = minZ; hz <= maxZ && solid; hz++)
                        for (var hx = minX; hx <= maxX; hx++)
                            if (!holes[hz, hx]) { solid = false; break; }
                    cells[z * (columns - 1) + x] = (byte)(solid && slope <= maxSlope ? 1 : 0);
                }
            }
            var asset = CreateInstance<RecoilPathfinding.RecoilTerrainMapData>();
            try { asset.SetBakedData(terrain.GetPosition(), new Vector2(size.x, size.z), columns, rows, maxSlope, heights, cells); }
            catch { DestroyImmediate(asset); throw; }
            return asset;
        }

        [MenuItem("Tools/Recoil Pathfinding/Validate Terrain Backend")]
        public static void ValidateBackend()
        {
            var map = Selection.activeObject as RecoilPathfinding.RecoilTerrainMapData;
            if (map == null)
            {
                var settings = Resources.Load<RecoilPathfinding.RecoilTerrainBackendSettings>("RecoilTerrainBackend");
                if (settings != null) map = settings.terrainMap;
            }
            RecoilPathfinding.RecoilNavMeshPathService.ValidateTerrainBackend(map);
            Debug.Log("Terrain backend checks passed: quadtree graph, cell-to-leaf projection, shared route, waypoint heights, scheduled Burst movement, out-of-map hold without managed retry and disposal.");
        }

        [MenuItem("Tools/Recoil Pathfinding/Validate Terrain Map Bake")]
        public static void Validate()
        {
            var source = new TerrainData { heightmapResolution = 33, size = new Vector3(8f, 8f, 8f) };
            GameObject terrainObject = null;
            RecoilPathfinding.RecoilTerrainMapData map = null;
            RecoilPathfinding.RecoilTerrainMapData steepMap = null;
            try
            {
                var heights = new float[33, 33];
                for (var z = 0; z < 33; z++)
                    for (var x = 0; x < 33; x++) heights[z, x] = x / 64f;
                source.SetHeights(0, 0, heights);
                var holes = new bool[source.holesResolution, source.holesResolution];
                for (var z = 0; z < source.holesResolution; z++)
                    for (var x = 0; x < source.holesResolution; x++) holes[z, x] = true;
                holes[1, 1] = false;
                source.SetHoles(0, 0, holes);
                terrainObject = Terrain.CreateTerrainGameObject(source);
                terrainObject.hideFlags = HideFlags.HideAndDontSave;
                terrainObject.transform.position = new Vector3(10f, 20f, 30f);
                var terrain = terrainObject.GetComponent<Terrain>();
                map = Bake(terrain, 2f, 45f);
                Require(map.HasValidData && map.Columns == 5 && map.Rows == 5, "Grid dimensions");
                Require(map.TrySample(new Vector3(14f, 999f, 34f), out var y, out var walkable) &&
                    Mathf.Abs(y - 22f) < 0.002f && walkable, "World-space interpolation and origin");
                Require(map.TrySample(new Vector3(18f, 0f, 38f), out y, out _) && Mathf.Abs(y - 24f) < 0.002f, "Maximum edge");
                Require(!map.TrySample(new Vector3(9f, 0f, 34f), out _, out _), "Outside bounds");
                Require(!map.TrySample(new Vector3(float.NaN, 0f, 34f), out _, out _), "Invalid coordinate");
                Require(!map.IsWalkable(0, 0), "Terrain holes block overlapping cells");
                steepMap = Bake(terrain, 2f, 10f);
                Require(!steepMap.IsWalkable(2, 2), "Slope limit");
                var cancelled = false;
                try { Bake(terrain, 2f, 45f, _ => true); }
                catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled, "Cancellation");
                Debug.Log("Terrain map bake checks passed: dimensions, translated origin, height interpolation, edges, invalid/outside coordinates, holes, slopes and cancellation.");
            }
            finally
            {
                if (map != null) DestroyImmediate(map);
                if (steepMap != null) DestroyImmediate(steepMap);
                if (terrainObject != null) DestroyImmediate(terrainObject);
                DestroyImmediate(source);
            }
        }

        private static void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Terrain map check failed: " + name);
        }
    }
}
