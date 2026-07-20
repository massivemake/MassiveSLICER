using System.Numerics;

namespace MassiveSlicer.Core.Collision;

/// <summary>
/// Aggregates the robot model, environment snapshot, deposited-material grid, and
/// thresholds into a single per-pose query. Thread-safe: all state is immutable;
/// per-call scratch comes from the caller (one <see cref="Scratch"/> per thread).
/// </summary>
public sealed class CollisionWorld
{
    public RobotCollisionModel Robot { get; }
    public EnvironmentCollider? Environment { get; }
    public BeadObstacleGrid? Beads { get; set; }
    public CollisionSettings Settings { get; }

    public CollisionWorld(RobotCollisionModel robot, EnvironmentCollider? environment,
                          CollisionSettings settings)
    {
        Robot = robot;
        Environment = environment;
        Settings = settings;
    }

    /// <summary>Per-thread reusable buffers for <see cref="CheckPose"/>.</summary>
    public sealed class Scratch
    {
        internal readonly Matrix4x4[] LinkXf = new Matrix4x4[RobotCollisionModel.LinkCount];
        internal readonly List<TransformedHull>[] Posed;
        internal readonly List<int> Hits = [];
        internal readonly HashSet<int> Seen = [];

        public Scratch()
        {
            Posed = new List<TransformedHull>[RobotCollisionModel.LinkCount];
            for (int i = 0; i < Posed.Length; i++) Posed[i] = [];
        }
    }

    /// <summary>
    /// Checks one joint pose against environment, self, and deposited material.
    /// Returns true and the first offending pair on a hit.
    /// <paramref name="maxBeadFlatIdx"/>: only beads at or below this flat move
    /// index exist (typically <c>moveIdx - RecentMoveSkip</c>).
    /// </summary>
    public bool CheckPose(ReadOnlySpan<float> krlDeg, in Matrix4x4 chainRoot,
                          int maxBeadFlatIdx, Vector3 tcpWorld,
                          Scratch scratch, out CollisionHit hit)
    {
        var s = Settings;
        Robot.ComputeLinkTransforms(krlDeg, chainRoot, scratch.LinkXf);

        // Pose every link's hulls once.
        for (int l = 0; l < RobotCollisionModel.LinkCount; l++)
        {
            scratch.Posed[l].Clear();
            var hulls = Robot.HullsFor(l);
            for (int h = 0; h < hulls.Count; h++)
                scratch.Posed[l].Add(new TransformedHull(hulls[h], scratch.LinkXf[l]));
        }

        // (a) Environment — L0 (pedestal/carriage) excluded: it permanently sits on
        // its own mount/rail.
        if (s.CheckEnvironment && Environment is { } env)
        {
            // Wrist + tool (L6/L7) legitimately work just above the bed/part — for
            // them, environment at or below the nozzle plane is process territory,
            // not a hazard (same principle as the material z-gate). Arm links keep
            // full checking so a genuine dive into the bed/floor still flags.
            float nozzlePlane = tcpWorld.Z + s.MaterialZToleranceMm;
            for (int l = 1; l < RobotCollisionModel.LinkCount; l++)
            {
                bool processLink = l >= 6;
                foreach (var hull in scratch.Posed[l])
                {
                    scratch.Hits.Clear();
                    env.Query(hull.Bounds.Inflate(s.ClearanceMm), scratch.Hits);
                    foreach (var ti in scratch.Hits)
                    {
                        if (processLink && env.TriangleTopZ(ti) <= nozzlePlane) continue;
                        if (Gjk.Distance(hull, env.Triangle(ti)) < s.ClearanceMm)
                        {
                            hit = new CollisionHit(
                                CollisionClass.Environment, l, env.SourceName(ti));
                            return true;
                        }
                    }
                }
            }
        }

        // (b) Self — non-adjacent link pairs.
        if (s.CheckSelf)
        {
            for (int a = 0; a < RobotCollisionModel.LinkCount; a++)
                for (int b = a + 1; b < RobotCollisionModel.LinkCount; b++)
                {
                    if (Robot.IsExcluded(a, b)) continue;
                    foreach (var ha in scratch.Posed[a])
                    {
                        var inflated = ha.Bounds.Inflate(s.SelfClearanceMm + 1e-3f);
                        foreach (var hb in scratch.Posed[b])
                        {
                            if (!inflated.Overlaps(hb.Bounds)) continue;
                            if (Gjk.Distance(ha, hb) < s.SelfClearanceMm + 1e-6f)
                            {
                                hit = new CollisionHit(
                                    CollisionClass.Self, a, RobotCollisionModel.LinkNames[b]);
                                return true;
                            }
                        }
                    }
                }
        }

        // (c) Deposited material — beads printed before this move, outside the
        // nozzle-exclusion radius.
        if (s.CheckMaterial && Beads is { } beads && maxBeadFlatIdx >= 0)
        {
            for (int l = 1; l < RobotCollisionModel.LinkCount; l++)
            {
                foreach (var hull in scratch.Posed[l])
                {
                    scratch.Hits.Clear();
                    beads.Query(hull.Bounds.Inflate(s.MaterialClearanceMm),
                        maxBeadFlatIdx, tcpWorld, s.NozzleExclusionRadiusMm,
                        tcpWorld.Z + s.MaterialZToleranceMm,
                        scratch.Hits, scratch.Seen);
                    foreach (var bi in scratch.Hits)
                    {
                        if (Gjk.Distance(hull, beads.Obb(bi)) < s.MaterialClearanceMm)
                        {
                            hit = new CollisionHit(
                                CollisionClass.Material, l, $"bead@move {beads.FlatIndex(bi)}");
                            return true;
                        }
                    }
                }
            }
        }

        hit = default;
        return false;
    }
}
