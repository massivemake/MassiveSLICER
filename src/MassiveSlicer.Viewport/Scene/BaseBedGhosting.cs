namespace MassiveSlicer.Viewport.Scene;

/// <summary>
/// Solid vs ghost presentation for LFAM 3's two beds. Viewport-only — does not
/// change robot motion, IK, or KRL export.
/// </summary>
public static class BaseBedGhosting
{
    /// <summary>Fill alpha for the inactive bed (rotary when BASE #6, heated otherwise).</summary>
    public const float GhostOpacity = 0.25f;

    /// <summary>
    /// Heated base → rotary ghosted, heated solid. Rotary base → heated ghosted, rotary solid.
    /// </summary>
    public static void Apply(SceneNode? heatedBed, SceneNode? rotaryBed, bool heatedActive)
    {
        SetGhost(heatedBed, ghost: !heatedActive);
        SetGhost(rotaryBed, ghost: heatedActive);
    }

    public static void SetGhost(SceneNode? root, bool ghost)
    {
        if (root is null) return;
        foreach (var n in root.SelfAndDescendants())
        {
            n.EnvironmentGhost = ghost;
            n.TranslucentPass  = ghost;
            if (n.Mesh is { } mesh)
                mesh.GhostOpacity = ghost ? GhostOpacity : 1f;
        }
    }

    public static bool IsGhosted(SceneNode node)
    {
        for (var cur = node; cur is not null; cur = cur.Parent)
            if (cur.EnvironmentGhost) return true;
        return false;
    }

    /// <summary>True when <paramref name="node"/> is the dual-bed heated plate (or under it).</summary>
    public static bool IsHeatedBedSubtree(SceneNode node)
    {
        for (var cur = node; cur is not null; cur = cur.Parent)
            if (cur.Name == "HeatedBed") return true;
        return false;
    }

    /// <summary>Solid lower bed in Arctic — not the ghosted inactive plate.</summary>
    public static bool IsSolidHeatedBed(SceneNode node)
        => IsHeatedBedSubtree(node) && !IsGhosted(node);
}
