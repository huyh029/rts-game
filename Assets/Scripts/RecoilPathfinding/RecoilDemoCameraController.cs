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
            var mouse = Mouse.current;
            if (mouse == null || !Application.isFocused)
                return;

            // Mouse delta is already displacement for this frame; do not multiply by deltaTime.
            var delta = mouse.delta.ReadValue();
            if (mouse.rightButton.isPressed && !mouse.rightButton.wasPressedThisFrame)
            {
                yawDegrees += delta.x * rotationDegreesPerPixel;
                pitchDegrees -= delta.y * rotationDegreesPerPixel;
            }

            pitchDegrees = Mathf.Clamp(pitchDegrees, 10f, 85f);
            yawDegrees = Mathf.Repeat(yawDegrees, 360f);
            transform.rotation = Quaternion.Euler(pitchDegrees, yawDegrees, 0f);

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
                transform.position = ClampPositionToMap(transform.position + transform.forward *
                    (notches * zoomUnitsPerNotch * zoomSensitivityMultiplier));
            }
        }
    }
}
