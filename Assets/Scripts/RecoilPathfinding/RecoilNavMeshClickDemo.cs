// Adapted from RecoilEngine ground movement concepts.
// RecoilEngine is GPL-2.0-or-later; this adapted file is distributed under the same terms.
// See Assets/_Game/RecoilEngine/LICENSE and its GPL license texts.

using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

namespace RecoilPathfinding
{
    public sealed class RecoilNavMeshClickDemo : MonoBehaviour
    {
        [SerializeField] private RecoilNavMeshPathAgent[] agents = System.Array.Empty<RecoilNavMeshPathAgent>();
        [SerializeField] private Camera inputCamera;
        [SerializeField, Min(0.1f)] private float rayDistance = 1000f;
        [SerializeField, Min(0.1f)] private float destinationSpacing = 1.8f;
        [SerializeField, Min(0.1f)] private float navMeshSampleDistance = 4f;

        private void Update()
        {
            if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame)
                return;

            if (inputCamera == null)
                inputCamera = Camera.main;

            if (inputCamera == null)
            {
                Debug.LogError($"{nameof(RecoilNavMeshClickDemo)} requires a camera tagged MainCamera.", this);
                return;
            }

            var ray = inputCamera.ScreenPointToRay(Mouse.current.position.ReadValue());
            if (!Physics.Raycast(ray, out var hit, rayDistance))
            {
                Debug.LogWarning("Click a collider on the ground to move the demo soldiers.", this);
                return;
            }

            if (!NavMesh.SamplePosition(hit.point, out var target, navMeshSampleDistance, NavMesh.AllAreas))
            {
                Debug.LogWarning("The clicked position is outside the NavMesh.", this);
                return;
            }

            var validAgentCount = 0;
            for (var i = 0; i < agents.Length; i++)
            {
                if (agents[i] != null && agents[i].isActiveAndEnabled)
                    validAgentCount++;
            }

            if (validAgentCount == 0)
            {
                Debug.LogWarning("The demo has no active path agents.", this);
                return;
            }

            var columns = Mathf.CeilToInt(Mathf.Sqrt(validAgentCount));
            var rows = Mathf.CeilToInt(validAgentCount / (float)columns);
            var assigned = 0;

            for (var i = 0; i < agents.Length; i++)
            {
                var agent = agents[i];
                if (agent == null || !agent.isActiveAndEnabled)
                    continue;

                var column = assigned % columns;
                var row = assigned / columns;
                var desired = target.position + new Vector3(
                    (column - (columns - 1) * 0.5f) * destinationSpacing,
                    0f,
                    (row - (rows - 1) * 0.5f) * destinationSpacing);
                if (NavMesh.SamplePosition(desired, out var destination, navMeshSampleDistance, NavMesh.AllAreas))
                    agent.SetDestination(destination.position);
                else
                    Debug.LogWarning($"Could not find a NavMesh destination for '{agent.name}'.", agent);

                assigned++;
            }
        }
    }
}
