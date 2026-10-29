using System.Collections.Generic;
using UnityEngine;

namespace RecoilPathfinding
{
    public readonly struct RecoilPathPortal
    {
        public readonly Vector3 Left;
        public readonly Vector3 Right;
        public RecoilPathPortal(Vector3 left, Vector3 right) { Left = left; Right = right; }
    }

    public static class RecoilPathFunnel
    {
        // Portals are ordered along the corridor; left/right are viewed in the travel direction.
        public static Vector3[] Build(Vector3 start, Vector3 goal, IReadOnlyList<RecoilPathPortal> portals)
        {
            var result = new List<Vector3>();
            BuildInto(start, goal, portals, result);
            return result.ToArray();
        }

        public static void BuildInto(Vector3 start, Vector3 goal, IReadOnlyList<RecoilPathPortal> portals, List<Vector3> result)
        {
            result.Clear();
            result.Add(start);
            var apex = start;
            var left = start;
            var right = start;
            var apexIndex = -1;
            var leftIndex = -1;
            var rightIndex = -1;
            for (var i = 0; i <= portals.Count; i++)
            {
                var nextLeft = i == portals.Count ? goal : portals[i].Left;
                var nextRight = i == portals.Count ? goal : portals[i].Right;
                if (Area(apex, right, nextRight) <= 0f)
                {
                    if (Same(apex, right) || Area(apex, left, nextRight) > 0f)
                    { right = nextRight; rightIndex = i; }
                    else
                    {
                        Append(result, left);
                        apex = left; apexIndex = leftIndex;
                        left = right = apex;
                        leftIndex = rightIndex = apexIndex;
                        i = apexIndex;
                        continue;
                    }
                }
                if (Area(apex, left, nextLeft) >= 0f)
                {
                    if (Same(apex, left) || Area(apex, right, nextLeft) < 0f)
                    { left = nextLeft; leftIndex = i; }
                    else
                    {
                        Append(result, right);
                        apex = right; apexIndex = rightIndex;
                        left = right = apex;
                        leftIndex = rightIndex = apexIndex;
                        i = apexIndex;
                    }
                }
            }
            Append(result, goal);
        }

        private static float Area(Vector3 a, Vector3 b, Vector3 c) =>
            (b.z - a.z) * (c.x - a.x) - (b.x - a.x) * (c.z - a.z);
        private static bool Same(Vector3 a, Vector3 b) =>
            (a.x - b.x) * (a.x - b.x) + (a.z - b.z) * (a.z - b.z) < 0.00000001f;
        private static void Append(List<Vector3> result, Vector3 point)
        {
            if ((result[result.Count - 1] - point).sqrMagnitude > 0.00000001f) result.Add(point);
        }
    }
}
