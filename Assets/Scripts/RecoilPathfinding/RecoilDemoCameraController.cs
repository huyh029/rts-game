using UnityEngine;
using UnityEngine.InputSystem;

namespace RecoilPathfinding
{
    [DisallowMultipleComponent]
    public sealed class RecoilDemoCameraController : MonoBehaviour
    {
        [SerializeField, Min(0.001f)] private float panUnitsPerPixel = 0.08f;
        [SerializeField, Min(0.001f)] private float rotationDegreesPerPixel = 0.2f;
        [SerializeField, Min(0.1f)] private float zoomUnitsPerNotch = 5f;
        [SerializeField, Min(0.1f)] private float zoomSensitivityMultiplier = 3f;
        [SerializeField, Range(10f, 85f)] private float pitchDegrees = 45f;
        [SerializeField] private float yawDegrees;
        [SerializeField] private RecoilTerrainMapData terrainMap;
        [SerializeField, Min(0f)] private float minHeightAboveMap = 10f;
        [SerializeField, Min(0.1f)] private float maxHeightAboveMap = 200f;
        private RecoilAutoBattleDemo _battle;
        private RecoilBattleMinimap _minimap;
        private bool _draggingMinimap;
        private float _nextReferenceSearchTime;
        private Camera _camera;

        private void OnEnable()
        {
            _camera = GetComponent<Camera>();
            _nextReferenceSearchTime = 0f;
            ApplyCameraRotation();
        }

        private void ApplyCameraRotation()
        {
            pitchDegrees = Mathf.Clamp(pitchDegrees, 10f, 85f);
            yawDegrees = Mathf.Repeat(yawDegrees, 360f);
            transform.rotation = Quaternion.Euler(pitchDegrees, yawDegrees, 0f);
        }

        private void FocusMinimap(Vector3 target)
        {
            // Move the point under the screen centre to the clicked terrain position.
            var cameraComponent = GetComponent<Camera>();
            var ray = cameraComponent != null
                ? cameraComponent.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f))
                : new Ray(transform.position, transform.forward);
            var plane = new Plane(Vector3.up, target);
            var focus = plane.Raycast(ray, out var distance) ? ray.GetPoint(distance) : transform.position;
            var offset = target - focus;
            offset.y = 0f;
            transform.position = ClampPositionToMap(transform.position + offset);
        }

        public Vector3 ClampPositionToMap(Vector3 position)
        {
            if (terrainMap == null || !terrainMap.HasValidData)
                return position;

            var origin = terrainMap.WorldOrigin;
            var size = terrainMap.WorldSize;
            position.x = Mathf.Clamp(position.x, origin.x, origin.x + size.x);
            position.z = Mathf.Clamp(position.z, origin.z, origin.z + size.y);
            if (terrainMap.TrySample(position, out var mapHeight, out _))
            {
                var minHeight = Mathf.Min(minHeightAboveMap, maxHeightAboveMap);
                var maxHeight = Mathf.Max(minHeightAboveMap, maxHeightAboveMap);
                position.y = Mathf.Clamp(position.y, mapHeight + minHeight, mapHeight + maxHeight);
            }
            return position;
        }

        private void Update()
        {
            if ((_battle == null || _minimap == null) && Time.unscaledTime >= _nextReferenceSearchTime)
            {
                _nextReferenceSearchTime = Time.unscaledTime + 1f;
                if (_battle == null) _battle = FindFirstObjectByType<RecoilAutoBattleDemo>();
                if (_minimap == null) _minimap = FindFirstObjectByType<RecoilBattleMinimap>();
            }
            if (_battle != null && !_battle.CanControlCamera)
            {
                _draggingMinimap = false;
                return;
            }

            var mouse = Mouse.current;
            if (mouse == null || !Application.isFocused)
            {
                _draggingMinimap = false;
                return;
            }

            if (!mouse.leftButton.isPressed) _draggingMinimap = false;
            if (_minimap != null && mouse.leftButton.wasPressedThisFrame &&
                _minimap.TryGetWorldPosition(mouse.position.ReadValue(), false, out _))
                _draggingMinimap = true;
            if (_draggingMinimap)
            {
                if (_minimap != null &&
                    _minimap.TryGetWorldPosition(mouse.position.ReadValue(), true, out var target))
                    FocusMinimap(target);
                return;
            }

            // Mouse delta is already displacement for this frame; do not multiply by deltaTime.
            var delta = mouse.delta.ReadValue();
            if (mouse.rightButton.isPressed && !mouse.rightButton.wasPressedThisFrame)
            {
                yawDegrees += delta.x * rotationDegreesPerPixel;
                pitchDegrees -= delta.y * rotationDegreesPerPixel;
                if (delta.sqrMagnitude > 0f) ApplyCameraRotation();
            }

            var panWithLeft = mouse.leftButton.isPressed && !mouse.leftButton.wasPressedThisFrame;
            var panWithMiddle = mouse.middleButton.isPressed && !mouse.middleButton.wasPressedThisFrame;
            if ((panWithLeft || panWithMiddle) && !mouse.rightButton.isPressed)
            {
                var yawRotation = Quaternion.Euler(0f, yawDegrees, 0f);
                var movement = new Vector3(-delta.x, 0f, -delta.y) * panUnitsPerPixel;
                transform.position = ClampPositionToMap(transform.position + yawRotation * movement);
            }

            var scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.001f)
            {
                // Input backends can report a wheel step as either 1 or 120.
                var notches = Mathf.Abs(scroll) >= 120f ? scroll / 120f : scroll;
                notches = Mathf.Clamp(notches, -4f, 4f);
                if (_camera != null && _camera.orthographic)
                {
                    _camera.orthographicSize = Mathf.Max(.5f, _camera.orthographicSize -
                        notches * zoomUnitsPerNotch * zoomSensitivityMultiplier);
                    return;
                }
                transform.position = ClampPositionToMap(transform.position + transform.forward *
                    (notches * zoomUnitsPerNotch * zoomSensitivityMultiplier));
            }
        }
    }
}
