using System.Globalization;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// The scale tool: the mm ⇄ % fields, the chain that keeps a part in proportion, Fit to Cell and
/// Reset Scale.
/// </summary>
/// <remarks>
/// Scale is kept separate from the move/rotate partial because it is the one transform that changes
/// what the part <em>is</em> rather than where it sits, and therefore the one that invalidates a
/// toolpath outright — see <see cref="ScaleInvalidatesToolpath"/>.
/// </remarks>
public sealed partial class ViewportViewModel
{
    private bool _isScalePercent;
    private bool _isScaleChained = true;

    /// <summary>
    /// False shows the part's real size in millimetres, true shows percent of its imported size.
    /// </summary>
    /// <remarks>
    /// Defaults to millimetres: the usual question on the shop floor is "how tall is this", and mm
    /// is the answer in the same units as everything else on screen. Percent earns its place by
    /// making 100 a free reset — you can always type 100 to get the part back to the size the file
    /// arrived at, without having to remember what that was.
    /// </remarks>
    public bool IsScalePercent
    {
        get => _isScalePercent;
        set
        {
            if (!SetField(ref _isScalePercent, value)) return;
            OnPropertyChanged(nameof(ScaleUnitLabel));
            RefreshScaleFields();
        }
    }

    /// <summary>Whether editing one axis carries the other two with it.</summary>
    /// <remarks>
    /// On by default. Scaling a printed part on one axis alone is nearly always a mistake rather
    /// than an intent, so the safe behaviour is the default and breaking proportion is the
    /// deliberate act.
    /// </remarks>
    public bool IsScaleChained
    {
        get => _isScaleChained;
        set => SetField(ref _isScaleChained, value);
    }

    /// <summary>What the unit-toggle button reads — the unit currently in force.</summary>
    public string ScaleUnitLabel => _isScalePercent ? "%" : "mm";

    /// <summary>Toggles millimetres ⇄ percent.</summary>
    public void ToggleScaleUnit() => IsScalePercent = !IsScalePercent;

    /// <summary>Toggles the proportion chain.</summary>
    public void ToggleScaleChain() => IsScaleChained = !IsScaleChained;

    // -- The three fields ------------------------------------------------------

    private double _scaleFieldX = 100d;
    private double _scaleFieldY = 100d;
    private double _scaleFieldZ = 100d;

    public double ScaleFieldX
    {
        get => _scaleFieldX;
        set { if (SetField(ref _scaleFieldX, value)) CommitScaleField(0, value); }
    }

    public double ScaleFieldY
    {
        get => _scaleFieldY;
        set { if (SetField(ref _scaleFieldY, value)) CommitScaleField(1, value); }
    }

    public double ScaleFieldZ
    {
        get => _scaleFieldZ;
        set { if (SetField(ref _scaleFieldZ, value)) CommitScaleField(2, value); }
    }

    private bool _suppressScaleCallback;

    /// <summary>
    /// Re-reads the three fields from the selection, in whichever unit is showing. Called after a
    /// unit toggle, a drag, or anything else that changes the part's size from outside the fields.
    /// </summary>
    internal void RefreshScaleFields()
    {
        if (SelectedContentNode() is not { } node || node.Placement is not { } t) return;
        if (ScaleBaseSize(node) is not { } baseSize) return;

        var shown = _isScalePercent
            ? PercentOf(node, t.Scale)
            : new Vector3(baseSize.X * t.Scale.X, baseSize.Y * t.Scale.Y, baseSize.Z * t.Scale.Z);

        _suppressScaleCallback = true;
        ScaleFieldX = shown.X;
        ScaleFieldY = shown.Y;
        ScaleFieldZ = shown.Z;
        _suppressScaleCallback = false;
    }

    /// <summary>Applies a typed field value to the selection.</summary>
    private void CommitScaleField(int axis, double value)
    {
        if (_suppressScaleCallback) return;
        SetScaleAxis(axis, value);
    }

    // -- The maths -------------------------------------------------------------

    /// <summary>
    /// The part's size at scale 1 — the extent of its own geometry before any scaling.
    /// </summary>
    /// <remarks>
    /// <see cref="NodeBounds.LocalAabb"/> measures in the node's own space with the node's own
    /// transform divided back out, so what comes back is the raw mesh extent and multiplying it by
    /// <see cref="NodeTransform.Scale"/> gives the real millimetres on screen.
    /// </remarks>
    private static Vector3? ScaleBaseSize(SceneNode node)
    {
        if (NodeBounds.LocalAabb(node) is not { } box) return null;
        var size = box.Max - box.Min;
        // A flat part (a plane, a single-layer slice) has a zero extent on one axis and no
        // meaningful millimetre size there; percent still works, so this is not fatal.
        return size;
    }

    /// <summary>Percent of imported size, per axis, for a given raw scale.</summary>
    private static Vector3 PercentOf(SceneNode node, Vector3 scale)
    {
        var import = node.ImportScale ?? Vector3.One;
        return new Vector3(
            SafeRatio(scale.X, import.X) * 100f,
            SafeRatio(scale.Y, import.Y) * 100f,
            SafeRatio(scale.Z, import.Z) * 100f);
    }

    private static float SafeRatio(float numerator, float denominator)
        => MathF.Abs(denominator) < 1e-9f ? 1f : numerator / denominator;

    /// <summary>
    /// Sets one axis from a typed field, honouring the unit in force and the proportion chain.
    /// </summary>
    /// <remarks>
    /// Chained, this scales every axis <em>by the same ratio</em>, not to the same amount. Typing 50
    /// over a 100 halves the part, so a 1300 on another axis becomes 650 — the shape is preserved,
    /// which is the entire point of the chain. Setting all three axes to the typed number instead
    /// would turn any part into a cube.
    /// </remarks>
    public string SetScaleAxis(int axis, double value)
        => WithPlacement("scale", t =>
        {
            var node = SelectedContentNode()!;
            if (ScaleBaseSize(node) is not { } baseSize)
                return (t, "no geometry to size.");

            float target = (float)value;
            if (!float.IsFinite(target) || target <= 0f)
                return (t, $"{value} is not a usable size — must be greater than zero.");

            // Whatever the unit on screen, the edit lands as a raw scale factor for that axis.
            float wanted = _isScalePercent
                ? (node.ImportScale ?? Vector3.One)[axis] * target / 100f
                : SafeRatio(target, baseSize[axis]);

            float current = t.Scale[axis];
            if (MathF.Abs(current) < 1e-9f) return (t, "current scale is degenerate.");

            if (_isScaleChained)
            {
                float ratio = wanted / current;
                t.Scale *= ratio;
            }
            else
            {
                var s = t.Scale;
                s[axis] = wanted;
                t.Scale = s;
            }

            t.ClampScale();
            return (t, $"scale now {V(t.Scale)} ({Describe(node, t.Scale, baseSize)}).");
        });

    /// <summary>Returns the part to the size it was imported at.</summary>
    public string ResetScale()
        => WithPlacement("scale", t =>
        {
            var node = SelectedContentNode()!;
            t.Scale = node.ImportScale ?? Vector3.One;
            t.ClampScale();
            return (t, "reset to imported size (100%).");
        });

    /// <summary>
    /// Scales the part uniformly until it just fits the cell's footprint, with padding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Always uniform — a per-axis fit would distort the part, and nobody asking to "fit this in the
    /// cell" wants it squashed. The limiting axis is whichever runs out of room first.
    /// </para>
    /// <para>
    /// ⚠️ Constrained in X and Y only. The cell config carries a bed <c>Width</c> and <c>Depth</c>
    /// (and a <c>Diameter</c> for a rotary platter) but declares no maximum build <em>height</em>,
    /// so there is no honest number to clamp Z against. If a real per-cell height limit exists it
    /// belongs in the cell JSON, and this should then take the smallest of all three.
    /// </para>
    /// </remarks>
    public string FitToCell()
        => WithPlacement("scale", t =>
        {
            var node = SelectedContentNode()!;
            if (ScaleBaseSize(node) is not { } baseSize)
                return (t, "no geometry to fit.");

            var scaled = new Vector3(
                baseSize.X * t.Scale.X,
                baseSize.Y * t.Scale.Y,
                baseSize.Z * t.Scale.Z);

            if (scaled.X <= 1e-6f || scaled.Y <= 1e-6f)
                return (t, "part has no footprint to fit.");

            var bed = ActiveCell?.Bed;
            float allowedX, allowedY;
            if (bed?.IsRotaryPrintBed == true && bed.Diameter is > 0)
            {
                // A round platter constrains the part's diagonal, not its X and Y separately.
                float usableDiameter = bed.Diameter.Value * FitToCellMargin;
                float diagonal = MathF.Sqrt(scaled.X * scaled.X + scaled.Y * scaled.Y);
                float rotaryRatio = usableDiameter / diagonal;
                t.Scale *= rotaryRatio;
                t.ClampScale();
                return (t, $"fitted to the {bed.Diameter.Value:F0}mm platter "
                         + $"(x{rotaryRatio:F3}, {Describe(node, t.Scale, baseSize)}).");
            }

            var (bedWidth, bedDepth) = ResolveBedSizeXY();
            allowedX = bedWidth * FitToCellMargin;
            allowedY = bedDepth * FitToCellMargin;

            float ratioX = allowedX / scaled.X;
            float ratioY = allowedY / scaled.Y;
            float ratio  = MathF.Min(ratioX, ratioY);
            string limiting = ratioX <= ratioY ? "X" : "Y";

            t.Scale *= ratio;
            t.ClampScale();
            return (t, $"fitted to the bed on {limiting} "
                     + $"(x{ratio:F3}, {Describe(node, t.Scale, baseSize)}).");
        });

    /// <summary>
    /// How much of the bed a Fit to Cell is allowed to use. Deliberately generous: a part touching
    /// the exact edge of the declared footprint leaves no room for a brim, a purge line, or the
    /// nozzle body itself.
    /// </summary>
    private const float FitToCellMargin = 0.90f;

    private static string Describe(SceneNode node, Vector3 scale, Vector3 baseSize)
    {
        var pct = PercentOf(node, scale);
        return string.Format(CultureInfo.InvariantCulture,
            "{0:F1} x {1:F1} x {2:F1} mm, {3:F1}% x {4:F1}% x {5:F1}%",
            baseSize.X * scale.X, baseSize.Y * scale.Y, baseSize.Z * scale.Z,
            pct.X, pct.Y, pct.Z);
    }

    /// <summary>
    /// Whether a scale edit should throw the part's toolpath away and re-slice.
    /// </summary>
    /// <remarks>
    /// It should, always — and this is a deliberate departure from the original sketch of the
    /// feature, which said scale should "move the wire path only". A toolpath cannot simply be
    /// stretched: its bead width is baked into the drawn geometry at slice time, so scaling the
    /// node inflates the beads, and its layer spacing is a machine setting rather than a property
    /// of the shape, so scaling Z silently prints layers at the wrong height. Both produce a
    /// plausible-looking path that would print wrong. Re-slicing honours the intent behind that
    /// note — bead width must not inflate — by regenerating the path at the settings' real widths
    /// for the part's new size.
    /// </remarks>
    internal const bool ScaleInvalidatesToolpath = true;

    // -- Console -----------------------------------------------------------------

    /// <summary>Backs the <c>scale</c> command.</summary>
    public string ScaleCommand(string args)
    {
        var p = (args ?? string.Empty).Split((char[])[' ', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = p.Length > 0 ? p[0].ToLowerInvariant() : "show";

        switch (verb)
        {
            case "show":
                return DescribeScale();

            case "mm":
                IsScalePercent = false;
                return "[scale] fields now in millimetres.";

            case "pct":
            case "percent":
                IsScalePercent = true;
                return "[scale] fields now in percent of imported size.";

            case "chain":
                if (p.Length > 1) IsScaleChained = p[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                else              ToggleScaleChain();
                return $"[scale] chain {(IsScaleChained ? "on — axes keep proportion" : "off — axes move independently")}.";

            case "reset":
                return ResetScale();

            case "fit":
                return FitToCell();

            case "x":
            case "y":
            case "z":
            {
                if (p.Length < 2 || !double.TryParse(p[1], NumberStyles.Float, Inv, out double v))
                    return "[scale] usage: scale <x|y|z> <value>   (value is mm or %, whichever is showing)";
                int axis = verb == "x" ? 0 : verb == "y" ? 1 : 2;
                return SetScaleAxis(axis, v);
            }

            default:
                return "[scale] usage: scale [show|mm|pct|chain [on|off]|reset|fit|x <v>|y <v>|z <v>]";
        }
    }

    private string DescribeScale()
    {
        if (SelectedContentNode() is not { } node)
            return "[scale] nothing selected — run `select <name>` first.";
        if (node.Placement is not { } t)
            return $"[scale] \"{node.Name}\" has no placement.";
        if (ScaleBaseSize(node) is not { } baseSize)
            return $"[scale] \"{node.Name}\" has no geometry to measure.";

        return $"[scale] \"{node.Name}\" {Describe(node, t.Scale, baseSize)} "
             + $"raw={V(t.Scale)} import={V(node.ImportScale ?? Vector3.One)} "
             + $"unit={ScaleUnitLabel} chain={(IsScaleChained ? "on" : "off")}";
    }
}
