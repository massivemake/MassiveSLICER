namespace MassiveSlicer.Core.Models;

public enum SupportShapeKind { Rectangle, Circle }

/// <summary>
/// A "Structural Support" toolpath modifier: at a fixed anchor point on the wall
/// (the surface break), each affected layer detours off the wall — neck out to a
/// helper shape (2×4 pocket rectangle / cylinder circle), wrap it, neck back —
/// then continues the wall. Because the anchor and shape are IDENTICAL on every
/// layer, the neck and pocket stack into a clean vertical column (unlike the
/// hand-modeled version, where the slicer's per-layer break placement wanders).
/// All coordinates are in sliced/toolpath space (mm).
/// </summary>
public sealed record StructuralSupportSpec
{
    /// <summary>
    /// User-facing unique name ("Support 1", "Support 2", …). Assigned on create by
    /// <c>AdditiveSettingsViewModel.AddStructuralSupport</c> and renameable. Empty on
    /// specs restored from an older preferences/workspace file — callers fall back to
    /// a positional label rather than showing a blank.
    /// </summary>
    public string Name { get; init; } = "";

    public SupportShapeKind Shape { get; init; } = SupportShapeKind.Rectangle;

    /// <summary>Where the neck meets the wall (surface break), sliced XY.</summary>
    public float AnchorX { get; init; }
    public float AnchorY { get; init; }

    /// <summary>Layer the support was placed on (0-based).</summary>
    public int AnchorLayer { get; init; }

    /// <summary>How many layers above the anchor layer are affected (9999 = to top).</summary>
    public int LayersUp { get; init; } = 9999;

    /// <summary>How many layers below the anchor layer are affected.</summary>
    public int LayersDown { get; init; }

    /// <summary>Helper shape centre, sliced XY.</summary>
    public float CenterX { get; init; }
    public float CenterY { get; init; }

    /// <summary>Rectangle: X extent before rotation. Circle: diameter.</summary>
    public float WidthMm { get; init; } = 92f;   // 2×4 actual 89mm + clearance

    /// <summary>Rectangle: Y extent before rotation. Ignored for circles.</summary>
    public float DepthMm { get; init; } = 42f;   // 2×4 actual 38mm + clearance

    /// <summary>Rectangle rotation about its centre (degrees, CCW).</summary>
    public float RotationDeg { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Outline polygon (CCW, closed implicitly) in sliced XY.</summary>
    public System.Numerics.Vector2[] BuildOutline(int circleSegments = 32)
    {
        if (Shape == SupportShapeKind.Circle)
        {
            float r = WidthMm * 0.5f;
            var pts = new System.Numerics.Vector2[circleSegments];
            for (int i = 0; i < circleSegments; i++)
            {
                float a = 2f * MathF.PI * i / circleSegments;
                pts[i] = new System.Numerics.Vector2(
                    CenterX + r * MathF.Cos(a), CenterY + r * MathF.Sin(a));
            }
            return pts;
        }

        float hw = WidthMm * 0.5f, hd = DepthMm * 0.5f;
        float rad = RotationDeg * MathF.PI / 180f;
        float c = MathF.Cos(rad), s = MathF.Sin(rad);
        System.Numerics.Vector2 Rot(float x, float y) => new(
            CenterX + x * c - y * s, CenterY + x * s + y * c);
        return [Rot(-hw, -hd), Rot(hw, -hd), Rot(hw, hd), Rot(-hw, hd)];
    }

    /// <summary>
    /// True when <paramref name="p"/> (sliced XY) falls inside the helper footprint.
    /// Used by the viewport to decide which pocket a click grabs — deliberately the
    /// footprint only, never the anchor, since the anchor sits on the wall and would
    /// otherwise steal bead clicks from the selection workflow that places it.
    /// </summary>
    public bool ContainsPoint(System.Numerics.Vector2 p, int circleSegments = 32)
    {
        // Circles are exact rather than polygonal — no reason to inherit the outline's
        // faceting error for a pick test.
        if (Shape == SupportShapeKind.Circle)
        {
            float r = WidthMm * 0.5f;
            float dx = p.X - CenterX, dy = p.Y - CenterY;
            return dx * dx + dy * dy <= r * r;
        }

        var poly = BuildOutline(circleSegments);
        if (poly.Length < 3) return false;
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if (a.Y > p.Y != b.Y > p.Y
                && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
