using UnityEngine;
using UnityEngine.InputSystem;

namespace RecoilPathfinding
{
    public sealed class RecoilCountdownTest : MonoBehaviour
    {
        private double _endsAt;
        private readonly RecoilCountdownVisual _visual = new();

        private void Start()
        {
            if (Camera.main == null)
            {
                var cameraObject = new GameObject("Countdown Test Camera", typeof(Camera));
                cameraObject.tag = "MainCamera";
                var camera = cameraObject.GetComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.045f, .055f, .08f);
                camera.cullingMask = 0;
            }
            _endsAt = Time.realtimeSinceStartupAsDouble + 3;
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.lKey.wasPressedThisFrame)
                _endsAt = Time.realtimeSinceStartupAsDouble + 3;
        }

        private void OnGUI()
        {
            GUI.Label(new Rect(20, 20, 500, 32), "COUNTDOWN TEST — L: phát lại 3–2–1");
            if (GUI.Button(new Rect(20, 58, 180, 42), "Phát lại 3–2–1"))
                _endsAt = Time.realtimeSinceStartupAsDouble + 3;
            var remaining = _endsAt - Time.realtimeSinceStartupAsDouble;
            if (remaining > 0) _visual.Draw(remaining);
        }
    }

    // Shared by the battle and the test scene, so both display the same effect.
    internal sealed class RecoilCountdownVisual
    {
        private GUIStyle _style;

        public void Draw(double remaining)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold,
                    padding = new RectOffset(), margin = new RectOffset()
                };
                // Drawing below always uses the normal state, independent of the pointer.
                foreach (var state in new[] { _style.normal, _style.hover, _style.active, _style.focused,
                    _style.onNormal, _style.onHover, _style.onActive, _style.onFocused })
                {
                    state.textColor = Color.white;
                    state.background = null;
                }
            }
            var digit = Mathf.Clamp((int)System.Math.Ceiling(remaining), 1, 3);
            var beat = Mathf.Clamp01((float)(digit - remaining));
            var solid = digit == 1 ? new Color(1f, .22f, .035f) : new Color(1f, .72f, .035f);
            var echo = digit == 1 ? new Color(1f, .40f, .04f) : new Color(1f, .32f, .025f);
            var number = digit.ToString();
            var rect = new Rect(0, 0, Screen.width, Screen.height);
            var baseSize = Mathf.Clamp(Screen.height * .44f, 110f, 462f);
            var oldColor = GUI.color;
            var oldContentColor = GUI.contentColor;
            GUI.color = GUI.contentColor = Color.white;
            // Settle the bold numeral first; release its single echo only afterward.
            const float settleDuration = .05f;
            _style.fontStyle = FontStyle.Bold;
            var content = new GUIContent(number);
            if (beat >= settleDuration)
            {
                var echoProgress = Mathf.Clamp01((beat - settleDuration) / (1f - settleDuration));
                var spread = 1f - Mathf.Pow(1f - echoProgress, 2f);
                _style.fontSize = Mathf.RoundToInt(baseSize * (1.04f + 1.26f * spread));
                echo.a = .18f * (1f - echoProgress) * (1f - echoProgress);
                GUI.contentColor = echo;
                DrawHeavyNumber(rect, content);
            }
            var settle = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(beat / settleDuration));
            _style.fontSize = Mathf.RoundToInt(baseSize * Mathf.Lerp(1.18f, 1f, settle));
            GUI.contentColor = new Color(.16f, .025f, 0f, .9f);
            DrawHeavyNumber(new Rect(6, 8, rect.width, rect.height), content);
            GUI.contentColor = solid;
            DrawHeavyNumber(rect, content);
            GUI.color = oldColor;
            GUI.contentColor = oldContentColor;
        }

        private void DrawHeavyNumber(Rect rect, GUIContent content)
        {
            // Thicken the bold glyph without changing its center or animation timing.
            var color = GUI.contentColor;
            var passColor = color;
            passColor.a = 1f - Mathf.Pow(1f - Mathf.Clamp01(color.a), .2f);
            GUI.contentColor = passColor;
            var offset = Mathf.Max(1f, _style.fontSize * .003f);
            _style.Draw(new Rect(rect.x - offset, rect.y, rect.width, rect.height), content, false, false, false, false);
            _style.Draw(new Rect(rect.x + offset, rect.y, rect.width, rect.height), content, false, false, false, false);
            _style.Draw(new Rect(rect.x, rect.y - offset, rect.width, rect.height), content, false, false, false, false);
            _style.Draw(new Rect(rect.x, rect.y + offset, rect.width, rect.height), content, false, false, false, false);
            _style.Draw(rect, content, false, false, false, false);
            GUI.contentColor = color;
        }
    }
}
