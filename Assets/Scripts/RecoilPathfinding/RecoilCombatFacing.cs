using Unity.Mathematics;

namespace RecoilPathfinding
{
    public static class RecoilCombatFacing
    {
        public static void FaceTarget(ref RecoilUnitPose pose, float3 target)
        {
            var direction = target - pose.Position;
            direction.y = 0f;
            if (math.lengthsq(direction) > 0.000001f)
                pose.Rotation = quaternion.LookRotationSafe(math.normalize(direction), math.up());
        }
    }
}
