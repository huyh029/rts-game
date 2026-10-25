using UnityEngine;
using UnityEngine.InputSystem;

namespace RecoilPathfinding
{
    [DisallowMultipleComponent]
    public sealed class RecoilDemoCameraController : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float moveSpeed = 30f;
        [SerializeField, Min(0.1f)] private float verticalSpeed = 20f;
        [SerializeField, Min(0.1f)] private float yawSpeed = 90f;
        [SerializeField, Min(0.1f)] private float pitchSpeed = 60f;
        [SerializeField, Range(10f, 85f)] private float pitchDegrees = 45f;
        [SerializeField] private float yawDegrees;

        private void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            var deltaTime = Time.deltaTime;
            var movement = Vector3.zero;
            if (keyboard.wKey.isPressed)
                movement += Vector3.forward;
            if (keyboard.sKey.isPressed)
                movement += Vector3.back;
            if (keyboard.dKey.isPressed)
                movement += Vector3.right;
            if (keyboard.aKey.isPressed)
                movement += Vector3.left;

            if (movement.sqrMagnitude > 1f)
                movement.Normalize();

            var yawRotation = Quaternion.Euler(0f, yawDegrees, 0f);
            transform.position += yawRotation * movement * (moveSpeed * deltaTime);

            if (keyboard.eKey.isPressed)
                transform.position += Vector3.up * (verticalSpeed * deltaTime);
            if (keyboard.qKey.isPressed)
                transform.position += Vector3.down * (verticalSpeed * deltaTime);

            if (keyboard.rKey.isPressed)
                yawDegrees += yawSpeed * deltaTime;

            if (keyboard.upArrowKey.isPressed)
                pitchDegrees -= pitchSpeed * deltaTime;
            if (keyboard.downArrowKey.isPressed)
                pitchDegrees += pitchSpeed * deltaTime;

            pitchDegrees = Mathf.Clamp(pitchDegrees, 10f, 85f);
            yawDegrees = Mathf.Repeat(yawDegrees, 360f);
            transform.rotation = Quaternion.Euler(pitchDegrees, yawDegrees, 0f);
        }
    }
}
