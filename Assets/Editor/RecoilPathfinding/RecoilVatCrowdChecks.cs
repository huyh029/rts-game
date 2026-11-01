using UnityEditor;
using UnityEngine;

namespace RecoilPathfindingEditor
{
    public static class RecoilVatCrowdChecks
    {
        [MenuItem("Tools/Recoil Pathfinding/Validate VAT Crowd")]
        public static void Run()
        {
            RecoilPathfinding.RecoilVatCrowdRenderer.ValidateVatCrowd();
            Debug.Log("VAT crowd checks passed: loop frame wrapping, one-shot frame clamping (Death " +
                "holds its last baked pose), and case-insensitive clip lookup.");
        }
    }
}
