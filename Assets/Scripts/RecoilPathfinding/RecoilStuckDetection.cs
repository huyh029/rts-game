// Adapted from RecoilEngine CGroundMoveType idling / repath handling; GPL-2.0-or-later.
// See Assets/_Game/RecoilEngine/rts/Sim/MoveTypes/GroundMoveType.cpp:
// OwnerMoved, FollowPath (numIdlingUpdates), SlowUpdate, ReRequestPath, Fail.
using Unity.Mathematics;

namespace RecoilPathfinding
{
    public enum RecoilStuckAction : byte { None, Repath, Fail }

    // One fixed movement tick of a unit, measured after its final position was applied.
    internal struct RecoilStuckSample
    {
        public float3 PreviousPosition;
        public float3 Position;
        public quaternion PreviousRotation;
        public quaternion Rotation;
        public float3 Waypoint;
        public byte HasPath;
        public byte MovementApplied; // 0 when a path/stop replaced the movement this tick.
        public byte Blocked;         // Step was clamped by a surface boundary (static collision).
        public float Speed;          // Units per second after the speed update.
        public float TurnSpeedDegrees;
        public float WaypointTolerance;
        public float DeltaTime;
        public int InstanceId;
    }

    public static class RecoilStuckDetection
    {
        // UNIT_SLOWUPDATE_RATE, MAX_IDLING_SLOWUPDATES and SPRING_MAX_HEADING in Recoil.
        public const int SlowUpdateRate = 15;
        public const int MaxIdlingSlowUpdates = 16;
        public const int MaxIdlingUpdates = 32768;
        // modInfo.pfRepathDelayInFrames / pfRepathMaxRateInFrames defaults (30 Hz ticks).
        public const int RepathDelayTicks = 60;
        public const int RepathMaxRateTicks = 150;

        // Recoil grows the move-command goal radius while a unit idles, so a crowd
        // pressing on one goal finishes instead of pushing forever.
        public static float GoalTolerance(in RecoilUnitNavigation navigation, float tolerance) =>
            navigation.CombatApproach == 0 ? tolerance * (navigation.IdlingSlowUpdates + 1) : tolerance;

        // Only the final waypoint stands for the goal; intermediate corners keep their tolerance.
        public static float WaypointTolerance(in RecoilUnitNavigation navigation, float tolerance, int index, int length) =>
            index == length - 1 ? GoalTolerance(navigation, tolerance) : tolerance;

        // Ticks needed to turn 180 degrees: the integer SPRING_MAX_HEADING / turnRate.
        public static int IdlingLimit(float turnSpeedDegrees, float deltaTime)
        {
            var step = turnSpeedDegrees * deltaTime;
            return step > 0f ? math.max(1, (int)(180f / step + 1e-3f)) : 1;
        }

        internal static RecoilStuckAction Step(ref RecoilUnitNavigation navigation, in RecoilStuckSample sample, int tick)
        {
            if (navigation.Active == 0 || navigation.HasDestination == 0)
            {
                // StopMoving: the next order starts with fresh counters. MoveFailed is kept for the AI.
                ResetCounters(ref navigation);
                navigation.StuckGoalSet = 0;
                return RecoilStuckAction.None;
            }
            if (navigation.StuckGoalSet == 0 || math.any(navigation.StuckGoal.xz != navigation.Destination.xz))
            {
                // StartMoving: a new goal resets every idling counter.
                ResetCounters(ref navigation);
                navigation.StuckGoal = navigation.Destination;
                navigation.StuckGoalSet = 1;
                navigation.MoveFailed = 0;
                navigation.StuckWaypointIndex = navigation.WaypointIndex;
                navigation.StuckMovementVersion = navigation.MovementVersion;
            }
            // SetNextWayPoint / a delivered path both prove progress: drop a deferred repath.
            if (navigation.StuckWaypointIndex != navigation.WaypointIndex ||
                navigation.StuckMovementVersion != navigation.MovementVersion)
            {
                navigation.WantRepath = 0;
                navigation.StuckWaypointIndex = navigation.WaypointIndex;
                navigation.StuckMovementVersion = navigation.MovementVersion;
            }

            var goalTolerance = GoalTolerance(navigation, sample.WaypointTolerance);
            var atGoal = math.distancesq(sample.Position.xz, navigation.Destination.xz) <= goalTolerance * goalTolerance;
            var hasPath = sample.HasPath != 0;
            if (sample.MovementApplied != 0 && hasPath)
            {
                navigation.Idling = (byte)(IsIdling(ref navigation, sample, atGoal) ? 1 : 0);
                if (!atGoal)
                    navigation.IdlingUpdates = navigation.Idling != 0
                        ? math.min(MaxIdlingUpdates, navigation.IdlingUpdates + 1)
                        : math.max(0, navigation.IdlingUpdates - 1);
                if (sample.Blocked != 0 && !atGoal) RequestSoftRepath(ref navigation, tick);
            }

            // CanSetNextWayPoint: reaching the last valid waypoint of an incomplete path sets
            // lastWaypoint and pathingFailed; the next Update calls Fail() instead of idling there.
            if (!hasPath && !atGoal && navigation.PathPending == 0 && navigation.MoveFailed == 0 &&
                ReachedPathEnd(navigation, sample) && PathIsIncomplete(navigation, goalTolerance))
            {
                navigation.WantRepath = 0;
                navigation.MoveFailed = 1;
                return RecoilStuckAction.Fail;
            }

            if ((tick + sample.InstanceId) % SlowUpdateRate != 0) return RecoilStuckAction.None;
            return SlowUpdate(ref navigation, sample, tick, hasPath, atGoal);
        }

        private static bool IsIdling(ref RecoilUnitNavigation navigation, in RecoilStuckSample sample, bool atGoal)
        {
            var moved = sample.Position.xz - sample.PreviousPosition.xz;
            navigation.PrevWaypointDistance = navigation.CurrWaypointDistance;
            navigation.CurrWaypointDistance = math.distance(sample.Waypoint.xz, sample.Position.xz);
            var movedSq = math.lengthsq(moved);
            if (movedSq < 1e-8f)
            {
                // Turning in place around an obstacle is not idling.
                var turnStep = math.max(0f, sample.TurnSpeedDegrees * sample.DeltaTime);
                return !atGoal && HeadingChangeDegrees(sample.PreviousRotation, sample.Rotation) < turnStep * 0.99f;
            }
            // Recoil compares the waypoint-distance delta against the displacement projected
            // on the waypoint direction: moving orthogonally to the waypoint counts as idling.
            // The Y test is dropped: our units follow the surface height on slopes.
            var forward = math.normalizesafe(math.rotate(sample.Rotation, new float3(0f, 0f, 1f)).xz);
            var waypointDirection = math.normalizesafe(sample.Waypoint.xz - sample.PreviousPosition.xz);
            var progress = navigation.CurrWaypointDistance - navigation.PrevWaypointDistance;
            var expected = sample.Speed * sample.DeltaTime * 0.5f;
            return progress * progress < math.dot(forward, waypointDirection) * movedSq * 0.5f &&
                movedSq < expected * expected;
        }

        private static RecoilStuckAction SlowUpdate(ref RecoilUnitNavigation navigation, in RecoilStuckSample sample,
            int tick, bool hasPath, bool atGoal)
        {
            if (navigation.MoveFailed != 0) return RecoilStuckAction.None;
            var action = RecoilStuckAction.None;
            if (hasPath || navigation.PathPending != 0)
            {
                navigation.IdlingSlowUpdates = navigation.Idling != 0
                    ? math.min(MaxIdlingSlowUpdates, navigation.IdlingSlowUpdates + 1)
                    : math.max(0, navigation.IdlingSlowUpdates - 1);
                // Case A: we have a path but are not moving.
                if (navigation.IdlingUpdates > IdlingLimit(sample.TurnSpeedDegrees, sample.DeltaTime))
                {
                    if (navigation.IdlingSlowUpdates < MaxIdlingSlowUpdates)
                    {
                        // Avoid spamming requests while the unit still makes progress.
                        if (navigation.Idling != 0) action = RecoilStuckAction.Repath;
                    }
                    // Stuck on a non-traversable spot or inside a non-moving crowd.
                    else action = RecoilStuckAction.Fail;
                }
            }
            else if (!atGoal)
                // Case B: we want to be moving but have no path. An incomplete path that was
                // finished has already failed; a finished path to an older goal is renewed.
                action = RecoilStuckAction.Repath;

            if (action == RecoilStuckAction.None && navigation.WantRepath != 0 && hasPath)
            {
                // Deferred repath: give the unit a chance to slide along the obstacle first.
                var distance = math.floor(math.distance(sample.Waypoint.xz, sample.Position.xz) * 10f) / 10f;
                if (distance < navigation.BestWaypointDistance)
                {
                    navigation.BestWaypointDistance = distance;
                    navigation.WantRepathTick = tick;
                }
                if (tick >= navigation.WantRepathTick + RepathDelayTicks &&
                    tick >= navigation.LastRepathTick + RepathMaxRateTicks)
                    action = RecoilStuckAction.Repath;
            }

            if (action == RecoilStuckAction.Repath)
            {
                // ReRequestPath(true): the counters are kept, so a unit that stays stuck
                // fails after MaxIdlingSlowUpdates slow updates instead of retrying forever.
                navigation.WantRepath = 0;
                navigation.LastRepathTick = tick;
            }
            else if (action == RecoilStuckAction.Fail)
            {
                navigation.WantRepath = 0;
                navigation.MoveFailed = 1;
            }
            return action;
        }

        private static bool ReachedPathEnd(in RecoilUnitNavigation navigation, in RecoilStuckSample sample)
        {
            if (navigation.HasPathEnd == 0) return false;
            var tolerance = GoalTolerance(navigation, sample.WaypointTolerance);
            return math.distancesq(sample.Position.xz, navigation.PathEnd.xz) <= tolerance * tolerance;
        }

        // QTPFS CurrentWaypointIsUnreachable: the delivered path stops short of the goal it was requested for.
        private static bool PathIsIncomplete(in RecoilUnitNavigation navigation, float tolerance) =>
            navigation.HasPathEnd != 0 &&
            math.distancesq(navigation.PathEnd.xz, navigation.RequestedDestination.xz) > tolerance * tolerance;

        private static void RequestSoftRepath(ref RecoilUnitNavigation navigation, int tick)
        {
            // ReRequestPath(false)
            if (navigation.WantRepath != 0) return;
            navigation.WantRepath = 1;
            navigation.WantRepathTick = tick;
            navigation.BestWaypointDistance = float.PositiveInfinity;
        }

        private static void ResetCounters(ref RecoilUnitNavigation navigation)
        {
            navigation.IdlingUpdates = 0;
            navigation.IdlingSlowUpdates = 0;
            navigation.Idling = 0;
            navigation.WantRepath = 0;
            navigation.PrevWaypointDistance = 0f;
            navigation.CurrWaypointDistance = 0f;
        }

        private static float HeadingChangeDegrees(quaternion previous, quaternion current)
        {
            var a = math.normalizesafe(math.rotate(previous, new float3(0f, 0f, 1f)).xz);
            var b = math.normalizesafe(math.rotate(current, new float3(0f, 0f, 1f)).xz);
            return math.degrees(math.acos(math.clamp(math.dot(a, b), -1f, 1f)));
        }

        public static void Validate()
        {
            const float dt = 1f / 30f;
            var east = quaternion.LookRotation(new float3(1f, 0f, 0f), new float3(0f, 1f, 0f));
            var blocked = new RecoilStuckSample
            {
                Waypoint = new float3(10f, 0f, 0f), HasPath = 1, MovementApplied = 1, Speed = 2f,
                PreviousRotation = east, Rotation = east, TurnSpeedDegrees = 180f, WaypointTolerance = .5f, DeltaTime = dt
            };
            RecoilUnitNavigation Fresh() => new RecoilUnitNavigation
            { Active = 1, HasDestination = 1, Destination = new float3(20f, 0f, 0f), WaypointIndex = 1 };

            // A unit pressing into an obstacle repaths once it idled longer than a half turn, then gives up.
            var navigation = Fresh();
            var firstRepath = -1; var failTick = -1; var repaths = 0;
            for (var tick = 1; tick <= 400; tick++)
            {
                var action = Step(ref navigation, blocked, tick);
                if (action == RecoilStuckAction.Repath) { repaths++; if (firstRepath < 0) firstRepath = tick; }
                if (action == RecoilStuckAction.Fail) { Require(failTick < 0, "A failed move must not fail again."); failTick = tick; }
                if (failTick >= 0) Require(action != RecoilStuckAction.Repath, "A failed move must not repath.");
            }
            Require(IdlingLimit(180f, dt) == 30,"The idling limit must be the ticks needed for a half turn.");
            Require(firstRepath == 45, $"A blocked unit must repath on the first slow update after 30 idle ticks (got {firstRepath}).");
            Require(failTick == SlowUpdateRate * MaxIdlingSlowUpdates && navigation.MoveFailed != 0 && repaths > 0,
                $"A unit idling for {MaxIdlingSlowUpdates} slow updates must fail (got {failTick}).");

            // A new goal is a new move order: counters and the failure flag reset.
            navigation.Destination = new float3(-20f, 0f, 0f);
            Step(ref navigation, blocked, 401);
            Require(navigation.MoveFailed == 0 && navigation.IdlingSlowUpdates == 0 && navigation.IdlingUpdates <= 1,
                "A new destination must reset the idling counters.");

            // Moving toward the waypoint never counts as idling.
            navigation = Fresh();
            var moving = blocked;
            moving.Waypoint = new float3(100f, 0f, 0f);
            for (var tick = 1; tick <= 600; tick++)
            {
                moving.PreviousPosition = moving.Position;
                moving.Position += new float3(2f * dt, 0f, 0f);
                Require(Step(ref navigation, moving, tick) == RecoilStuckAction.None && navigation.IdlingUpdates == 0,
                    "A unit making progress must not be treated as stuck.");
            }

            // Turning in place at full rate around an obstacle is not idling.
            navigation = Fresh();
            var turning = blocked;
            for (var tick = 1; tick <= 300; tick++)
            {
                turning.PreviousRotation = turning.Rotation;
                turning.Rotation = math.mul(quaternion.RotateY(math.radians(6f)), turning.Rotation);
                Require(Step(ref navigation, turning, tick) == RecoilStuckAction.None,
                    "A unit turning in place at its turn rate must not be treated as stuck.");
            }

            // Case B: an active order without a path asks for one.
            navigation = Fresh();
            var pathless = blocked;
            pathless.HasPath = 0;
            Require(Step(ref navigation, pathless, SlowUpdateRate) == RecoilStuckAction.Repath,
                "An active order without a path must request one on its slow update.");
            // lastWaypoint: the end of an incomplete path fails at once, on any tick, and only once.
            navigation = Fresh();
            navigation.HasPathEnd = 1;
            navigation.PathEnd = pathless.Position;
            navigation.RequestedDestination = navigation.Destination;
            Require(Step(ref navigation, pathless, 7) == RecoilStuckAction.Fail && navigation.MoveFailed != 0,
                "Reaching the end of a path that stops short of an unreachable goal must fail the move.");
            Require(Step(ref navigation, pathless, SlowUpdateRate) == RecoilStuckAction.None,
                "A failed move must neither fail again nor repath.");
            // A finished path delivered for an older goal is renewed, not failed.
            navigation = Fresh();
            navigation.HasPathEnd = 1;
            navigation.PathEnd = pathless.Position;
            navigation.RequestedDestination = pathless.Position;
            Require(Step(ref navigation, pathless, SlowUpdateRate) == RecoilStuckAction.Repath && navigation.MoveFailed == 0,
                "A finished path for an older goal must request a path to the current goal.");
            // A path that is still on its way must not be failed on the old path's end.
            navigation = Fresh();
            navigation.HasPathEnd = 1;
            navigation.PathEnd = pathless.Position;
            navigation.RequestedDestination = navigation.Destination;
            navigation.PathPending = 1;
            Require(Step(ref navigation, pathless, 7) == RecoilStuckAction.None,
                "A pending request must not be failed on the previous path's end.");
            navigation = Fresh();
            navigation.PathPending = 1;
            Require(Step(ref navigation, pathless, SlowUpdateRate) == RecoilStuckAction.None,
                "A pending request must not be duplicated.");

            // Deferred repath after a static collision: wait for no progress, respect the rate limit.
            navigation = Fresh();
            var sliding = turning;
            sliding.Blocked = 1;
            var deferred = -1;
            for (var tick = 1; tick <= 200 && deferred < 0; tick++)
            {
                sliding.PreviousRotation = sliding.Rotation;
                sliding.Rotation = math.mul(quaternion.RotateY(math.radians(6f)), sliding.Rotation);
                if (Step(ref navigation, sliding, tick) == RecoilStuckAction.Repath) deferred = tick;
            }
            Require(deferred == RepathMaxRateTicks, $"A blocked unit without progress must repath after the delay and rate limit (got {deferred}).");
            navigation = Fresh();
            Step(ref navigation, sliding, 1);
            Require(navigation.WantRepath != 0, "A static collision must request a deferred repath.");
            navigation.WaypointIndex++;
            sliding.Blocked = 0;
            Step(ref navigation, sliding, 2);
            Require(navigation.WantRepath == 0, "Reaching the next waypoint must cancel a deferred repath.");

            // The move goal radius grows while idling; attack approaches keep their range.
            navigation = Fresh();
            navigation.IdlingSlowUpdates = 3;
            Require(GoalTolerance(navigation, .5f) == 2f && WaypointTolerance(navigation, .5f, 0, 2) == .5f &&
                WaypointTolerance(navigation, .5f, 1, 2) == 2f, "Only the final waypoint uses the idling goal radius.");
            navigation.CombatApproach = 1;
            Require(GoalTolerance(navigation, .5f) == .5f, "Combat approaches must not widen their goal radius.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new System.InvalidOperationException(message);
        }
    }
}
