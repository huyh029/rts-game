using UnityEditor;
using UnityEngine;

namespace RecoilPathfindingEditor
{
    public static class RecoilSharedPathChecks
    {
        [MenuItem("Tools/Recoil Pathfinding/Validate Shared Paths")]
        public static void Run()
        {
            // Public service diagnostic also checks rejected disconnected/area-restricted segments.
            RecoilPathfinding.RecoilNavMeshPathService.ValidateDirectPathChecks();
            // Cache internals stay in the runtime assembly; exercise them via its diagnostic.
            RecoilPathfinding.RecoilNavMeshPathService.ValidateSharedCorridors();
            Debug.Log("Shared path checks passed: direct connected segments, disconnected/area rejection, corridor copy/reuse, bounded capacity, invalidation and distinct unit endpoints.");
        }
    }
}
