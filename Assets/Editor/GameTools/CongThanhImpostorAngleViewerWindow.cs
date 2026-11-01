using System;
using System.Linq;
using RecoilPathfinding;
using UnityEditor;
using UnityEngine;

namespace GameToolsEditor
{
    public sealed class CongThanhImpostorAngleViewerWindow : EditorWindow
    {
        private const string LibraryFolder = "Assets/Resources/CharacterImpostors";
        private static readonly string[] DirectionNames =
        {
            "Trước", "Trước-phải", "Phải", "Sau-phải",
            "Sau", "Sau-trái", "Trái", "Trước-trái"
        };

        private RecoilImpostorSpriteLibrary[] _libraries = Array.Empty<RecoilImpostorSpriteLibrary>();
        private string[] _libraryNames = Array.Empty<string>();
        private int _libraryIndex;
        private int _stateIndex;
        private int _frame;
        private int _view;
        private bool _playing;
        private bool _rotatingViews;
        private double _lastFrameTime;
        private double _lastViewTime;
        private Vector2 _angleScroll;

        private RecoilImpostorSpriteLibrary Library =>
            _libraries.Length > _libraryIndex ? _libraries[_libraryIndex] : null;

        private RecoilImpostorSpriteLibrary.AnimationSheet Sheet
        {
            get
            {
                var animations = Library != null ? Library.Animations : null;
                return animations != null && animations.Length > _stateIndex
                    ? animations[_stateIndex]
                    : null;
            }
        }

        private int FrameCount => Sheet != null && Sheet.Frames > 0
            ? Sheet.Frames
            : Mathf.Max(1, Library != null ? Library.FramesPerState : 1);

        private int ViewCount => Library != null
            ? Mathf.Max(1, Library.HorizontalDirections + Library.ElevatedDirections)
            : 1;

        [MenuItem("Game Tools/Công Thành/Xem góc Impostor")]
        private static void Open()
        {
            var window = GetWindow<CongThanhImpostorAngleViewerWindow>("Impostor Viewer");
            window.minSize = new Vector2(560f, 680f);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshLibraries();
            EditorApplication.update += UpdatePlayback;
        }

        private void OnDisable()
        {
            EditorApplication.update -= UpdatePlayback;
        }

        private void RefreshLibraries()
        {
            var previousPath = Library != null ? AssetDatabase.GetAssetPath(Library) : null;
            _libraries = AssetDatabase.FindAssets("t:RecoilImpostorSpriteLibrary", new[] { LibraryFolder })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<RecoilImpostorSpriteLibrary>)
                .Where(library => library != null)
                .OrderBy(library => library.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _libraryNames = _libraries.Select(library => library.name).ToArray();
            var previousIndex = Array.FindIndex(_libraries,
                library => AssetDatabase.GetAssetPath(library) == previousPath);
            _libraryIndex = Mathf.Clamp(previousIndex, 0, Mathf.Max(0, _libraries.Length - 1));
            _stateIndex = 0;
            _frame = 0;
            _view = Mathf.Clamp(_view, 0, ViewCount - 1);
        }

        private void OnGUI()
        {
            DrawToolbar();
            EditorGUILayout.Space(4f);

            if (Library == null)
            {
                EditorGUILayout.HelpBox(
                    $"Không tìm thấy Impostor Sprite Library trong {LibraryFolder}. Hãy bake sprite sheet trước.",
                    MessageType.Warning);
                return;
            }

            DrawLibraryInfo();
            DrawStateSelector();
            if (Sheet == null || Sheet.Atlas == null)
            {
                EditorGUILayout.HelpBox("Trạng thái này chưa có atlas.", MessageType.Warning);
                return;
            }

            DrawFrameControls();
            DrawMainPreview();
            DrawAngleGrid();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField("Prefab / library", GUILayout.Width(100f));
                if (_libraryNames.Length > 0)
                {
                    EditorGUI.BeginChangeCheck();
                    _libraryIndex = EditorGUILayout.Popup(_libraryIndex, _libraryNames, EditorStyles.toolbarPopup);
                    if (EditorGUI.EndChangeCheck())
                    {
                        _stateIndex = 0;
                        _frame = 0;
                        _view = 0;
                    }
                }
                else
                    EditorGUILayout.LabelField("Không có library", EditorStyles.toolbarButton);

                if (GUILayout.Button("Làm mới", EditorStyles.toolbarButton, GUILayout.Width(58f)))
                    RefreshLibraries();
            }
        }

        private void DrawLibraryInfo()
        {
            var atlas = Sheet != null ? Sheet.Atlas : null;
            var textureSize = atlas != null ? $"{atlas.width} x {atlas.height}" : "chưa có";
            EditorGUILayout.LabelField(
                $"Atlas: {textureSize}   •   Khung: {Library.FrameWidth} x {Library.FrameHeight}   •   " +
                $"Hướng: {Library.HorizontalDirections} ngang + {Library.ElevatedDirections} trên cao",
                EditorStyles.miniLabel);
        }

        private void DrawStateSelector()
        {
            var animations = Library.Animations ?? Array.Empty<RecoilImpostorSpriteLibrary.AnimationSheet>();
            if (animations.Length == 0)
            {
                EditorGUILayout.HelpBox("Library chưa có trạng thái nào.", MessageType.Warning);
                return;
            }

            var stateNames = animations.Select(animation =>
                animation != null ? animation.State : "(trống)").ToArray();
            EditorGUI.BeginChangeCheck();
            _stateIndex = EditorGUILayout.Popup("Trạng thái", Mathf.Clamp(_stateIndex, 0, animations.Length - 1),
                stateNames);
            if (EditorGUI.EndChangeCheck())
                _frame = 0;
        }

        private void DrawFrameControls()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _frame = EditorGUILayout.IntSlider("Frame", _frame, 0, FrameCount - 1);
                if (EditorGUI.EndChangeCheck())
                    _playing = false;

                if (GUILayout.Button(_playing ? "Dừng" : "Phát", GUILayout.Width(54f)))
                {
                    _playing = !_playing;
                    _lastFrameTime = EditorApplication.timeSinceStartup;
                }
                if (GUILayout.Button(_rotatingViews ? "Dừng xoay" : "Xoay góc", GUILayout.Width(78f)))
                {
                    _rotatingViews = !_rotatingViews;
                    _lastViewTime = EditorApplication.timeSinceStartup;
                }
            }

            EditorGUILayout.LabelField(
                $"Clip: {Sheet.SourceClip}   •   {FrameCount} frame   •   " +
                $"{Sheet.Duration:F2}s{(Sheet.Loop ? " (lặp)" : string.Empty)}",
                EditorStyles.miniLabel);
        }

        private void DrawMainPreview()
        {
            EditorGUILayout.Space(3f);
            var viewLabel = GetViewLabel(_view);
            EditorGUILayout.LabelField($"Góc đang chọn: {_view + 1}/{ViewCount} — {viewLabel}",
                EditorStyles.boldLabel);
            var rect = GUILayoutUtility.GetRect(100f, 300f, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, new Color(0.13f, 0.14f, 0.16f));
            DrawAtlasCell(rect, _view, _frame);
        }

        private void DrawAngleGrid()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Chọn góc nhìn", EditorStyles.boldLabel);
            _angleScroll = EditorGUILayout.BeginScrollView(_angleScroll, GUILayout.Height(180f));
            var columns = 4;
            var cellWidth = Mathf.Max(80f, (position.width - 40f) / columns);
            for (var row = 0; row < Mathf.CeilToInt(ViewCount / (float)columns); row++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (var column = 0; column < columns; column++)
                    {
                        var view = row * columns + column;
                        if (view >= ViewCount) break;
                        DrawAngleCell(view, cellWidth);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawAngleCell(int view, float width)
        {
            var rect = GUILayoutUtility.GetRect(width, 145f, GUILayout.Width(width));
            var selected = view == _view;
            EditorGUI.DrawRect(rect, selected
                ? new Color(0.22f, 0.36f, 0.52f)
                : new Color(0.18f, 0.19f, 0.21f));
            var imageRect = new Rect(rect.x + 3f, rect.y + 3f, rect.width - 6f, rect.height - 29f);
            DrawAtlasCell(imageRect, view, _frame);
            GUI.Label(new Rect(rect.x + 3f, rect.yMax - 24f, rect.width - 6f, 21f),
                GetViewLabel(view), EditorStyles.centeredGreyMiniLabel);
            if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
            {
                _view = view;
                Repaint();
            }
        }

        private void DrawAtlasCell(Rect rect, int view, int frame)
        {
            var atlas = Sheet.Atlas;
            var aspect = Library.FrameWidth / (float)Mathf.Max(1, Library.FrameHeight);
            if (rect.width / rect.height > aspect)
            {
                rect.width = rect.height * aspect;
                rect.x = rect.center.x - rect.width * 0.5f;
            }
            else
            {
                rect.height = rect.width / aspect;
                rect.y = rect.center.y - rect.height * 0.5f;
            }

            var uv = new Rect(
                view / (float)ViewCount,
                frame / (float)FrameCount,
                1f / ViewCount,
                1f / FrameCount);
            GUI.DrawTextureWithTexCoords(rect, atlas, uv, true);
        }

        private string GetViewLabel(int view)
        {
            var horizontalCount = Mathf.Max(1, Library.HorizontalDirections);
            if (view < horizontalCount)
            {
                var direction = view * 8 / horizontalCount;
                return $"Ngang {DirectionNames[direction]} ({view * 360f / horizontalCount:F0}°)";
            }

            var elevatedIndex = view - horizontalCount;
            var elevatedCount = Mathf.Max(1, Library.ElevatedDirections);
            var elevatedDirection = elevatedIndex * 8 / elevatedCount;
            return $"Trên cao {DirectionNames[elevatedDirection]} " +
                   $"({Library.ElevatedViewAngle:F0}°)";
        }

        private void UpdatePlayback()
        {
            if ((!_playing && !_rotatingViews) || Sheet == null || Sheet.Atlas == null)
                return;
            var now = EditorApplication.timeSinceStartup;
            if (_playing)
            {
                var frameDuration = Sheet.Duration > 0f ? Sheet.Duration / FrameCount : 0.1f;
                if (now - _lastFrameTime >= frameDuration)
                {
                    _lastFrameTime = now;
                    if (_frame + 1 < FrameCount)
                        _frame++;
                    else if (Sheet.Loop)
                        _frame = 0;
                    else
                        _playing = false;
                }
            }
            if (_rotatingViews && now - _lastViewTime >= 0.2d)
            {
                _lastViewTime = now;
                _view = (_view + 1) % ViewCount;
            }
            Repaint();
        }
    }
}
