using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    [DisallowMultipleComponent]
    public sealed class RecoilBattleMinimap : MonoBehaviour
    {
        private const float PreferredSize = 220f;
        private const float Margin = 8f;
        private const float MarkerSize = 5f;
        private const float CastleMarkerSize = 18f;

        [SerializeField] private RecoilAutoBattleDemo battle;
        [SerializeField] private Texture2D castleIcon;

        private readonly List<Vector3> _bluePositions = new List<Vector3>(256);
        private readonly List<Vector3> _redPositions = new List<Vector3>(256);
        private Texture2D _unitDot;
        private float _nextPositionRefreshTime;

        private void OnEnable()
        {
            if (battle == null) battle = FindFirstObjectByType<RecoilAutoBattleDemo>();
            if (_unitDot == null) _unitDot = CreateUnitDot();
        }

        private void Update()
        {
            if (battle == null) battle = FindFirstObjectByType<RecoilAutoBattleDemo>();
            if (battle == null || Time.unscaledTime < _nextPositionRefreshTime) return;
            _nextPositionRefreshTime = Time.unscaledTime + 0.1f;
            battle.CopyMinimapUnitPositions(_bluePositions, _redPositions);
        }

        private void OnDestroy()
        {
            if (_unitDot == null) return;
            if (Application.isPlaying) Destroy(_unitDot);
            else DestroyImmediate(_unitDot);
            _unitDot = null;
        }

        private void OnGUI()
        {
            if (battle == null || !battle.ShouldShowGameplayHud || castleIcon == null) return;
            var map = battle.TerrainMap;
            if (map == null || !map.HasValidData || map.CastleBlockCount != 2) return;

            var size = Mathf.Min(PreferredSize, Screen.width - Margin * 2f, Screen.height - Margin * 2f);
            if (size <= 0f) return;
            var panel = new Rect(Screen.width - size - Margin, Margin, size, size);
            GUI.Box(panel, "Minimap");
            var available = new Rect(panel.x + 8f, panel.y + 23f, size - 16f, size - 31f);
            var aspect = map.WorldSize.x / map.WorldSize.y;
            var mapWidth = Mathf.Min(available.width, available.height * aspect);
            var mapHeight = mapWidth / aspect;
            var mapRect = new Rect(available.center.x - mapWidth * 0.5f,
                available.center.y - mapHeight * 0.5f, mapWidth, mapHeight);
            if (mapRect.width <= 0f || mapRect.height <= 0f) return;

            DrawTexture(mapRect, Texture2D.whiteTexture, new Color(0.08f, 0.1f, 0.12f, 0.92f));
            DrawGrid(mapRect);
            DrawCastle(mapRect, map, RecoilCastleTeam.Blue, new Color(0.2f, 0.62f, 1f));
            DrawCastle(mapRect, map, RecoilCastleTeam.Red, new Color(1f, 0.3f, 0.25f));

            DrawUnits(mapRect, map, _bluePositions, new Color(0.2f, 0.72f, 1f));
            DrawUnits(mapRect, map, _redPositions, new Color(1f, 0.32f, 0.28f));
        }

        private void DrawGrid(Rect rect)
        {
            var previousColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.12f);
            for (var i = 1; i < 4; i++)
            {
                var fraction = i * 0.25f;
                GUI.DrawTexture(new Rect(rect.x + rect.width * fraction, rect.y, 1f, rect.height),
                    Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(rect.x, rect.y + rect.height * fraction, rect.width, 1f),
                    Texture2D.whiteTexture);
            }
            GUI.color = previousColor;
        }

        private void DrawCastle(
            Rect rect, RecoilTerrainMapData map, RecoilCastleTeam team, Color color)
        {
            var bounds = GetCastleBounds(map, team);
            if (!TryMapPosition(rect, map, bounds.center, out var point)) return;
            var iconRect = new Rect(point.x - CastleMarkerSize * 0.5f,
                point.y - CastleMarkerSize * 0.5f, CastleMarkerSize, CastleMarkerSize);
            DrawTexture(iconRect, castleIcon, color);
        }

        private void DrawUnits(Rect rect, RecoilTerrainMapData map, List<Vector3> positions, Color color)
        {
            if (_unitDot == null) return;
            var previousColor = GUI.color;
            GUI.color = color;
            for (var i = 0; i < positions.Count; i++)
            {
                if (!TryMapPosition(rect, map, positions[i], out var point)) continue;
                GUI.DrawTexture(new Rect(point.x - MarkerSize * 0.5f,
                    point.y - MarkerSize * 0.5f, MarkerSize, MarkerSize), _unitDot);
            }
            GUI.color = previousColor;
        }

        private static bool TryMapPosition(
            Rect rect, RecoilTerrainMapData map, Vector3 worldPosition, out Vector2 point)
        {
            var origin = map.WorldOrigin;
            var size = map.WorldSize;
            var x = (worldPosition.x - origin.x) / size.x;
            var z = (worldPosition.z - origin.z) / size.y;
            point = new Vector2(rect.x + x * rect.width, rect.y + (1f - z) * rect.height);
            return x >= 0f && x <= 1f && z >= 0f && z <= 1f;
        }

        private static Bounds GetCastleBounds(RecoilTerrainMapData map, RecoilCastleTeam team)
        {
            for (var i = 0; i < map.CastleBlockCount; i++)
            {
                var castle = map.GetCastleBlock(i);
                if (castle.Team == team) return castle.WorldBounds;
            }
            return default;
        }

        private static void DrawTexture(Rect rect, Texture texture, Color color)
        {
            var previousColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, texture);
            GUI.color = previousColor;
        }

        private static Texture2D CreateUnitDot()
        {
            const int textureSize = 16;
            var texture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false)
            {
                name = "Battle Minimap Unit Dot",
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            var pixels = new Color[textureSize * textureSize];
            var center = (textureSize - 1) * 0.5f;
            var radius = textureSize * 0.5f - 0.5f;
            for (var y = 0; y < textureSize; y++)
            for (var x = 0; x < textureSize; x++)
            {
                var distance = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                pixels[y * textureSize + x] = new Color(1f, 1f, 1f,
                    Mathf.Clamp01(radius + 0.5f - distance));
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }
    }
}
