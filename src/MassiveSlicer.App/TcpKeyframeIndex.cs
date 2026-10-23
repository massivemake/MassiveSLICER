using System.Collections.Generic;

namespace MassiveSlicer.App;

/// <summary>Picks a free move index while a keyframe is dragged along the timeline.</summary>
internal static class TcpKeyframeIndex
{
    /// <summary>
    /// <paramref name="desired"/> when it is free. An occupied index steps to the
    /// nearest free move, looking first in the direction of the drag.
    /// </summary>
    public static int Place(int desired, int current, int max, IReadOnlyCollection<int> occupied)
    {
        if (max < 0) return 0;
        desired = System.Math.Clamp(desired, 0, max);
        if (!occupied.Contains(desired)) return desired;

        int dir = desired >= current ? 1 : -1;
        for (int step = 1; step <= max + 1; step++)
        {
            int ahead = desired + dir * step;
            int behind = desired - dir * step;
            if (ahead >= 0 && ahead <= max && !occupied.Contains(ahead)) return ahead;
            if (behind >= 0 && behind <= max && !occupied.Contains(behind)) return behind;
        }
        return System.Math.Clamp(current, 0, max);
    }
}
