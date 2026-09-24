using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Core.Slicing;
using MassiveSlicer.Viewport.FK;
using NMatrix = System.Numerics.Matrix4x4;
using NVec3 = System.Numerics.Vector3;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace MassiveSlicer.Viewport.Validation;

/// <summary>
/// Cheap robot check of one Auto Orient candidate pose: IK over a sample of the part's moves,
/// never the whole toolpath. It ranks hundreds of spin/slide poses in the time one full
/// <see cref="ToolpathFeasibilityEvaluator"/> sweep takes; only the few best go on to the full
/// sweep (every move, singularity repair, collisions), which alone decides pass or fail.
/// </summary>
public static class PlacementPrescreen
{
    /// <summary>Everything that stays the same across candidates. Captured once.</summary>
    /// <param name="Samples">Sample moves in world space at the part's CURRENT pose, in toolpath order.</param>
    /// <param name="Joints">Cell joint limits, or null to judge reach by IK alone.</param>
    /// <param name="E1Motion">Rail planned per the operator's E1 setting; false = rail parked at home.</param>
    public sealed record Context(
        GltfNumericalIkSolver Solver,
        (NVec3 pos, NVec3 normal)[] Samples,
        float OffsetADeg, float OffsetBDeg, float OffsetCDeg,
        float[] SeedKrl,
        IReadOnlyList<JointConfig>? Joints,
        TkVector3 Robroot,
        bool E1Motion,
        RobotRailCellConfig? Rail,
        NVec3 HomeWorld,
        float HomeE1,
        float E1YPlusMm,
        float E1YMinusMm);

    /// <summary>Scores <paramref name="candidate"/> (a world-space transform of the current pose).</summary>
    public static PlacementSearch.Score Evaluate(Context ctx, NMatrix candidate, int stride = 1)
    {
        var solver = ctx.Solver;
        int n = (ctx.Samples.Length + stride - 1) / Math.Max(1, stride);
        var worlds = new NVec3[n];
        var normals = new NVec3[n];
        for (int i = 0, k = 0; i < ctx.Samples.Length && k < n; i += stride, k++)
        {
            worlds[k] = NVec3.Transform(ctx.Samples[i].pos, candidate);
            normals[k] = NVec3.TransformNormal(ctx.Samples[i].normal, candidate);
        }

        // Rail: same planner as export when the operator has E1 on; otherwise parked at home.
        float[]? e1 = null;
        if (ctx.E1Motion && ctx.Rail is { } rail)
        {
            Func<NVec3, bool> inWs = rel => solver.IsInWorkspace(new TkVector3(rel.X, rel.Y, rel.Z));
            e1 = RailE1Planner.PlanPath(worlds, ctx.HomeWorld, rail, ctx.HomeE1,
                ctx.E1YPlusMm, ctx.E1YMinusMm, solver.PreferredHorizontalReachMm, inWs,
                gridCount: 11, smoothBlend: 0.45f);
            for (int k = 0; k < n; k++)
                e1[k] = RailE1Planner.ClampToAllowance(
                    e1[k], ctx.HomeE1, ctx.E1YPlusMm, ctx.E1YMinusMm, rail.MinMm, rail.MaxMm);
        }

        int unreachable = 0;
        float margin = float.MaxValue;
        var seed = (float[])ctx.SeedKrl.Clone();
        for (int k = 0; k < n; k++)
        {
            var w = worlds[k];
            TkVector3 target;
            if (e1 is not null)
            {
                var baseW = RailE1Planner.BaseWorld(ctx.HomeWorld, ctx.Rail!, e1[k]);
                target = new TkVector3(w.X - baseW.X, w.Y - baseW.Y, w.Z - baseW.Z);
            }
            else
                target = new TkVector3(w.X, w.Y, w.Z) - ctx.Robroot;

            var nrm = normals[k].LengthSquared() > 1e-8f ? NVec3.Normalize(normals[k]) : NVec3.UnitZ;
            var rot = solver.TargetRotFromGlobalOrientation(
                new TkVector3(nrm.X, nrm.Y, nrm.Z), ctx.OffsetADeg, ctx.OffsetBDeg, ctx.OffsetCDeg);
            var sol = solver.Solve(target, seed, rot, maxIterations: 40);
            if (sol is null || (ctx.Joints is { } lim && !JointLimitEnvelope.JointsInside(sol, lim)))
            {
                unreachable++;
                continue;
            }
            seed = sol;
            margin = MathF.Min(margin, Margin(sol, ctx.Joints));
        }

        return new PlacementSearch.Score(unreachable, margin == float.MaxValue ? -999f : margin);
    }

    /// <summary>
    /// Room to spare for one solution, in degrees: the nearest usable joint limit, or how far A5
    /// is past the 5° wrist-singularity band, whichever is tighter.
    /// </summary>
    public static float Margin(float[] sol, IReadOnlyList<JointConfig>? joints)
    {
        float m = MathF.Abs(sol[4]) - 5f;
        if (joints is not null)
            for (int j = 0; j < Math.Min(6, joints.Count); j++)
                m = MathF.Min(m, MathF.Min(sol[j] - joints[j].UsableMinDeg, joints[j].UsableMaxDeg - sol[j]));
        return m;
    }
}
