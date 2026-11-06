using UnityEditor;

namespace RecoilPathfindingEditor
{
    internal static class RecoilSoldierPoolChecks
    {
        [MenuItem("Game Tools/Công Thành/Kiểm tra pool lính")]
        public static void Validate() => RecoilPathfinding.RecoilAutoBattleDemo.ValidateInactiveSoldierPool();

        [MenuItem("Game Tools/Công Thành/Kiểm tra hàng xuất quân")]
        public static void ValidateSpawnRows() => RecoilPathfinding.RecoilAutoBattleDemo.ValidateSpawnTypeDistribution();
    }
}
