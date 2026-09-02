namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// Mill vs print DLS policy. Internals stay <c>GltfNumericalIkSolver.Solve</c>.
/// Mill is position-first from named home (Mill Start); print is 6D from current pose.
/// </summary>
public sealed record SolveRecipe(
    bool PositionFirst,
    bool ThenOrient,
    bool RequireWorkspace,
    int PositionMaxIter,
    int OrientMaxIter,
    bool PreferNamedHomeSeed)
{
    public static readonly SolveRecipe Mill = new(
        PositionFirst: true, ThenOrient: true, RequireWorkspace: false,
        PositionMaxIter: 400, OrientMaxIter: 120, PreferNamedHomeSeed: true);

    public static readonly SolveRecipe Print = new(
        PositionFirst: false, ThenOrient: true, RequireWorkspace: true,
        PositionMaxIter: 0, OrientMaxIter: 300, PreferNamedHomeSeed: false);
}
