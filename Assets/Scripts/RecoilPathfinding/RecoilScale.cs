// Adapted from RecoilEngine ground movement concepts; GPL-2.0-or-later.
namespace RecoilPathfinding
{
    // Recoil measures distance in elmos (SQUARE_SIZE = 8 elmos) and speed per 30 Hz simulation
    // frame. Constants taken from GroundMoveType are converted here instead of being reused
    // as raw numbers in Unity units and units per second.
    public static class RecoilScale
    {
        // GAME_SPEED: simulation frames per second.
        public const float GameSpeed = 30f;
        // A 2x2-square infantry footprint has a max interior radius of 8 elmos, which is the
        // 0.5 unit body radius of our soldiers.
        public const float ElmosPerUnit = 16f;
        // SQUARE_SIZE and WAYPOINT_RADIUS (1.25 * SQUARE_SIZE) in Unity units.
        public const float SquareSize = 8f / ElmosPerUnit;
        public const float WaypointRadius = 1.25f * SquareSize;

        public static float FromElmos(float elmos) => elmos / ElmosPerUnit;
        public static float ToElmos(float units) => units * ElmosPerUnit;
        // Units per second <-> elmos per frame (CUnit::speed.w, currentSpeed).
        public static float ToElmosPerFrame(float unitsPerSecond) => unitsPerSecond * ElmosPerUnit / GameSpeed;
        public static float FromElmosPerFrame(float elmosPerFrame) => elmosPerFrame * GameSpeed / ElmosPerUnit;
    }
}
