using System.Numerics;
using MassiveSlicer.Core.Kinematics;

namespace MassiveSlicer.Core.Collision;

/// <summary>
/// The robot's collision geometry: convex hulls per rigid body, posed by the same
/// FK convention as <c>GltfNumericalIkSolver.ComputePartialFkPos</c>
/// (<c>wt = rot * restPose[i] * wt</c>, row-vector).
///
/// Bodies: L0 = pedestal/carriage (chain-root frame), L1..L6 = arm links
/// (each in its joint_N node's local frame), L7 = tool (tool-mesh frame, rides L6).
/// Hull vertices stay in GLTF-local units — the ×1000 scene scale lives in the
/// chain-root / rest-pose matrices, so clearance margins must be applied as
/// world-space GJK thresholds, never baked into vertices.
/// </summary>
public sealed class RobotCollisionModel
{
    public const int LinkCount = 8; // L0 base, L1..L6 arm, L7 tool

    readonly IReadOnlyList<ConvexHull>[] _linkHulls;
    readonly Matrix4x4[] _restPoses;      // 6 entries, joint_1..joint_6
    readonly JointConfig[] _jointConfigs; // 6 entries
    readonly Matrix4x4 _toolLocal;        // tool-mesh space -> joint_6 frame
    readonly bool[,] _excludedPairs = new bool[LinkCount, LinkCount];

    /// <summary>Human-readable body names for hit reporting.</summary>
    public static readonly string[] LinkNames =
        ["base", "link_1", "link_2", "link_3", "link_4", "link_5", "link_6", "tool"];

    public IReadOnlyList<ConvexHull> HullsFor(int link) => _linkHulls[link];

    public RobotCollisionModel(
        IReadOnlyList<ConvexHull>[] linkHulls,
        IReadOnlyList<Matrix4x4> restPoses,
        IReadOnlyList<JointConfig> jointConfigs,
        Matrix4x4 toolLocal)
    {
        if (linkHulls.Length != LinkCount)
            throw new ArgumentException($"expected {LinkCount} link hull lists");
        _linkHulls = linkHulls;
        _restPoses = [.. restPoses];
        _jointConfigs = [.. jointConfigs];
        _toolLocal = toolLocal;

        // Kinematically adjacent bodies legitimately touch — exclude them, plus the
        // wrist cluster around the tool (A5/A6 geometry interleaves with the mount).
        for (int i = 0; i < LinkCount - 1; i++)
            Exclude(i, i + 1);
        Exclude(5, 7);
        Exclude(4, 6); // wrist housings overlap through A5's slender link
    }

    void Exclude(int a, int b)
    {
        _excludedPairs[a, b] = true;
        _excludedPairs[b, a] = true;
    }

    public bool IsExcluded(int a, int b) => _excludedPairs[a, b];

    /// <summary>
    /// World transform per body for a joint pose. <paramref name="outXf"/> must have
    /// <see cref="LinkCount"/> entries. Thread-safe (no shared mutable state).
    /// </summary>
    public void ComputeLinkTransforms(ReadOnlySpan<float> krlDeg, in Matrix4x4 chainRoot,
                                      Span<Matrix4x4> outXf)
    {
        outXf[0] = chainRoot;
        var wt = chainRoot;
        for (int i = 0; i < 6; i++)
        {
            var cfg = _jointConfigs[i];
            float boneAngle = (float)(cfg.KrlSign * krlDeg[i] * Math.PI / 180.0);
            var rot = cfg.Axis switch
            {
                RotationAxis.X => Matrix4x4.CreateRotationX(boneAngle),
                RotationAxis.Z => Matrix4x4.CreateRotationZ(boneAngle),
                _ => Matrix4x4.CreateRotationY(boneAngle),
            };
            wt = rot * _restPoses[i] * wt;
            outXf[i + 1] = wt;
        }
        outXf[7] = _toolLocal * outXf[6];
    }

    /// <summary>
    /// Evaluates every non-adjacent pair at the given pose (typically HOME) and
    /// auto-excludes pairs already within <paramref name="marginMm"/> — absorbs
    /// conservative-hull overlap at rest so it can't flood the results with
    /// false positives. Returns the pairs that were excluded (for logging).
    /// </summary>
    public List<(int a, int b, float distMm)> ApplySelfBaseline(
        ReadOnlySpan<float> homeKrlDeg, in Matrix4x4 chainRoot, float marginMm)
    {
        Span<Matrix4x4> xf = stackalloc Matrix4x4[LinkCount];
        ComputeLinkTransforms(homeKrlDeg, chainRoot, xf);

        var posed = new TransformedHull[LinkCount][];
        for (int l = 0; l < LinkCount; l++)
        {
            posed[l] = new TransformedHull[_linkHulls[l].Count];
            for (int h = 0; h < _linkHulls[l].Count; h++)
                posed[l][h] = new TransformedHull(_linkHulls[l][h], xf[l]);
        }

        var excluded = new List<(int, int, float)>();
        for (int a = 0; a < LinkCount; a++)
            for (int b = a + 1; b < LinkCount; b++)
            {
                if (_excludedPairs[a, b]) continue;
                if (posed[a].Length == 0 || posed[b].Length == 0) continue;

                float best = float.MaxValue;
                foreach (var ha in posed[a])
                    foreach (var hb in posed[b])
                    {
                        if (!ha.Bounds.Inflate(marginMm).Overlaps(hb.Bounds)) continue;
                        best = MathF.Min(best, Gjk.Distance(ha, hb));
                        if (best <= 0f) break;
                    }
                if (best < marginMm)
                {
                    Exclude(a, b);
                    excluded.Add((a, b, best));
                }
            }
        return excluded;
    }
}
