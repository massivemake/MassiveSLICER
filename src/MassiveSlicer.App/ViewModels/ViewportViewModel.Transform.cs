using System.Globalization;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Console-drivable transform tools: pivot placement and the position / rotation / scale of the
/// selected part.
/// </summary>
/// <remarks>
/// Every one of these is reachable from the local control bridge, so pivot and gizmo behaviour can
/// be measured headlessly instead of only judged by eye — including the things that are easy to get
/// wrong and hard to see, like whether a rotate ring turns about the axis it is drawn around, or
/// whether moving a pivot leaves the geometry alone.
/// </remarks>
public sealed partial class ViewportViewModel
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Invoked after a console-driven transform edit so it gets the same follow-up a typed field
    /// edit does: linked toolpaths carried along, readout refreshed, reach re-validated, one undo
    /// entry pushed. Receives the node and its matrix from before the edit.
    /// </summary>
    internal Action<SceneNode, Matrix4, string>? OnExternalNodeTransform { get; set; }

    private SceneNode? SelectedContentNode() => GetSelectedSceneNode?.Invoke();

    /// <summary>Applies <paramref name="edit"/> to the selection's placement and reports the result.</summary>
    private string WithPlacement(string tag, Func<NodeTransform, (NodeTransform Next, string Message)> edit)
    {
        if (SelectedContentNode() is not { } node)
            return $"[{tag}] nothing selected — run `select <name>` first.";

        // A node that has never been through the transform tools adopts a placement here, pivoted at
        // its box centre — the same one-time re-centre an import gets, and equally geometry-neutral.
        var existing = node.Placement;
        if (existing is null)
        {
            if (NodeBounds.LocalCenter(node) is not { } centre)
                return $"[{tag}] \"{node.Name}\" has no geometry and no placement.";
            existing = node.EnsurePlacement(centre);
        }

        var before  = node.LocalTransform;
        var current = existing.Value;
        var (next, message) = edit(current);
        node.SetPlacement(next);
        OnExternalNodeTransform?.Invoke(node, before, tag);
        return $"[{tag}] \"{node.Name}\" {message}";
    }

    private static string V(Vector3 v)
        => string.Format(Inv, "({0:F2}, {1:F2}, {2:F2})", v.X, v.Y, v.Z);

    // -- origin ----------------------------------------------------------------

    /// <summary>Backs the <c>origin</c> command.</summary>
    public string OriginCommand(string args)
    {
        var p = (args ?? string.Empty).Split((char[])[' ', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = p.Length > 0 ? p[0].ToLowerInvariant() : "show";

        switch (verb)
        {
            case "show":
                return DescribeOrigin();

            case "box":
                return DescribeBox();

            case "points":
                return "[origin] snap specs: any combination of ±x ±y ±z. Omitted axes sit at the "
                     + "box middle, so `+x` is a face centre, `+x+y` an edge midpoint, `+x+y+z` a "
                     + "corner. 26 in total; the box centre is `origin center`.";

            case "center":
            case "centre":
            case "recenter":
                return WithPlacement("origin", t =>
                {
                    if (SelectedContentNode() is { } n && NodeBounds.LocalCenter(n) is { } c)
                    {
                        t.SetOrigin(c);
                        return (t, $"pivot recentred to {V(c)} in mesh space; geometry unmoved.");
                    }
                    return (t, "no geometry to measure — pivot unchanged.");
                });

            case "set":
                if (p.Length < 4
                    || !float.TryParse(p[1], NumberStyles.Float, Inv, out float ox)
                    || !float.TryParse(p[2], NumberStyles.Float, Inv, out float oy)
                    || !float.TryParse(p[3], NumberStyles.Float, Inv, out float oz))
                    return "[origin] usage: origin set <x> <y> <z>   (mesh-space coordinates)";
                return WithPlacement("origin", t =>
                {
                    t.SetOrigin(new Vector3(ox, oy, oz));
                    return (t, $"pivot moved to {V(t.Origin)} in mesh space; geometry unmoved.");
                });

            case "snap":
                if (p.Length < 2) return "[origin] usage: origin snap <±x±y±z>   e.g. origin snap +x-y+z";
                return SnapOrigin(p[1]);

            default:
                return "[origin] usage: origin [show|box|points|center|set <x> <y> <z>|snap <±x±y±z>]";
        }
    }

    private string DescribeOrigin()
    {
        if (SelectedContentNode() is not { } node)
            return "[origin] nothing selected — run `select <name>` first.";
        if (node.Placement is not { } t)
            return $"[origin] \"{node.Name}\" has no placement (driven straight from a matrix).";

        var world  = Vector3.TransformPosition(t.Origin, node.WorldTransform);
        var center = NodeBounds.LocalCenter(node);
        bool centred = center is { } c && (c - t.Origin).Length < 0.01f;
        return $"[origin] \"{node.Name}\" pivot={V(t.Origin)} (mesh space) world={V(world)} "
             + $"boxCentre={(center is { } cc ? V(cc) : "n/a")} centred={centred}";
    }

    private string DescribeBox()
    {
        if (SelectedContentNode() is not { } node)
            return "[origin] nothing selected — run `select <name>` first.";
        if (NodeBounds.LocalAabb(node) is not { } b)
            return $"[origin] \"{node.Name}\" has no geometry to measure.";
        var size = b.Max - b.Min;
        return $"[origin] \"{node.Name}\" box min={V(b.Min)} max={V(b.Max)} size={V(size)} "
             + $"centre={V((b.Min + b.Max) * 0.5f)}";
    }

    /// <summary>
    /// Snaps the pivot to one of the bounding box's 26 common points, named by which extremes it
    /// sits at: <c>+x</c> is the centre of the +X face, <c>+x+y</c> an edge midpoint, <c>+x+y+z</c>
    /// a corner. Axes left out of the spec sit at the box's middle on that axis.
    /// </summary>
    private string SnapOrigin(string spec)
    {
        if (SelectedContentNode() is not { } node)
            return "[origin] nothing selected — run `select <name>` first.";
        if (NodeBounds.LocalAabb(node) is not { } b)
            return $"[origin] \"{node.Name}\" has no geometry to measure.";

        var mid = (b.Min + b.Max) * 0.5f;
        var target = mid;
        int named = 0;
        var s = spec.ToLowerInvariant();

        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '+' && s[i] != '-') continue;
            if (i + 1 >= s.Length) break;
            bool high = s[i] == '+';
            switch (s[i + 1])
            {
                case 'x': target.X = high ? b.Max.X : b.Min.X; named++; break;
                case 'y': target.Y = high ? b.Max.Y : b.Min.Y; named++; break;
                case 'z': target.Z = high ? b.Max.Z : b.Min.Z; named++; break;
                default: continue;
            }
        }

        if (named == 0)
            return $"[origin] '{spec}' names no axis. Use ±x ±y ±z, e.g. `origin snap +x-y+z`.";

        string kind = named switch { 3 => "corner", 2 => "edge midpoint", _ => "face centre" };
        return WithPlacement("origin", t =>
        {
            t.SetOrigin(target);
            return (t, $"pivot snapped to {kind} {V(target)} in mesh space; geometry unmoved.");
        });
    }

    // -- xform -----------------------------------------------------------------

    /// <summary>Backs the <c>xform</c> command.</summary>
    public string XformCommand(string args)
    {
        var p = (args ?? string.Empty).Split((char[])[' ', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = p.Length > 0 ? p[0].ToLowerInvariant() : "show";

        switch (verb)
        {
            case "show":
                return DescribePlacement();

            case "pos":
            case "move":
                if (!Triple(p, out var pos))
                    return "[xform] usage: xform pos <x> <y> <z>   (where the pivot goes, parent space)";
                return WithPlacement("xform", t =>
                {
                    t.Position = pos;
                    return (t, $"pivot moved to {V(t.Position)}.");
                });

            case "rot":
                if (!Triple(p, out var euler))
                    return "[xform] usage: xform rot <x> <y> <z>   (degrees about the part's own axes)";
                return WithPlacement("xform", t =>
                {
                    t.EulerDegrees = euler;
                    return (t, $"rotation set to {V(t.EulerDegrees)}° about its own axes.");
                });

            case "rotate":
                if (p.Length < 3 || AxisOf(p[1]) is not { } axis
                    || !float.TryParse(p[2], NumberStyles.Float, Inv, out float deg))
                    return "[xform] usage: xform rotate <x|y|z> <degrees>   (additive, about the part's own axis)";
                return WithPlacement("xform", t =>
                {
                    var wasAxis = t.LocalAxis(axis);
                    t.RotateLocal(axis, MathHelper.DegreesToRadians(deg));
                    // Reported so a bridge test can assert the dragged axis itself did not move.
                    float drift = (wasAxis - t.LocalAxis(axis)).Length;
                    return (t, $"rotated {deg:F2}° about its own {"XYZ"[axis]}; "
                             + $"now {V(t.EulerDegrees)}°, that axis moved {drift:F5}.");
                });

            case "scale":
                if (p.Length < 2 || !float.TryParse(p[1], NumberStyles.Float, Inv, out float sx))
                    return "[xform] usage: xform scale <s> | xform scale <x> <y> <z>";
                float sy = sx, sz = sx;
                if (p.Length >= 4)
                {
                    if (!float.TryParse(p[2], NumberStyles.Float, Inv, out sy)
                     || !float.TryParse(p[3], NumberStyles.Float, Inv, out sz))
                        return "[xform] usage: xform scale <s> | xform scale <x> <y> <z>";
                }
                return WithPlacement("xform", t =>
                {
                    t.Scale = new Vector3(sx, sy, sz);
                    t.ClampScale();
                    return (t, $"scale set to {V(t.Scale)} about pivot {V(t.Origin)}.");
                });

            default:
                return "[xform] usage: xform [show|pos <x y z>|rot <x y z>|rotate <axis> <deg>|scale <s|x y z>]";
        }
    }

    private string DescribePlacement()
    {
        if (SelectedContentNode() is not { } node)
            return "[xform] nothing selected — run `select <name>` first.";
        if (node.Placement is not { } t)
            return $"[xform] \"{node.Name}\" has no placement (driven straight from a matrix).";

        float shear = NodeTransform.ShearOf(node.LocalTransform);
        return $"[xform] \"{node.Name}\" pos={V(t.Position)} rot={V(t.EulerDegrees)}° "
             + $"scale={V(t.Scale)} pivot={V(t.Origin)} shear={shear:F6}";
    }

    /// <summary>
    /// Backs the <c>basis</c> command: the gizmo's pivot and the direction each coloured handle
    /// actually points, in world space.
    /// </summary>
    /// <remarks>
    /// The numeric check for "are the handles stuck to the object": rotate a part and the axis
    /// directions here should turn with it while <c>worldAligned</c> goes false.
    /// </remarks>
    public string BasisCommand()
    {
        if (SelectedContentNode() is not { } node)
            return "[basis] nothing selected — run `select <name>` first.";

        var w = node.WorldTransform;
        Vector3 Axis(Vector3 v) => v.LengthSquared > 1e-12f ? Vector3.Normalize(v) : Vector3.Zero;
        var ax = Axis(w.Row0.Xyz);
        var ay = Axis(w.Row1.Xyz);
        var az = Axis(w.Row2.Xyz);

        var pivot = node.Placement is { } t
            ? Vector3.TransformPosition(t.Origin, w)
            : w.Row3.Xyz;

        bool worldAligned =
            (ax - Vector3.UnitX).Length < 1e-3f &&
            (ay - Vector3.UnitY).Length < 1e-3f &&
            (az - Vector3.UnitZ).Length < 1e-3f;

        return $"[basis] \"{node.Name}\" pivot={V(pivot)} X(red)={V(ax)} Y(green)={V(ay)} "
             + $"Z(blue)={V(az)} worldAligned={worldAligned}";
    }

    /// <summary>
    /// Re-levels every cut plane under <paramref name="meshNode"/> so its own Horizontal/Vertical
    /// tag beats whatever rotation it inherited from the part.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A modifier's plane lives under a "Modifiers" group parented to the mesh
    /// (<c>GetOrCreateModifiersGroup</c>), and <c>SyncModifierGizmoNodeFromFields</c> divides the
    /// parent transform out only at the moment it writes the plane. Nothing re-ran when the mesh was
    /// later rotated, so the plane rode the part's tilt — and a plane tagged Horizontal could stop
    /// being horizontal, quietly contradicting a setting the user had chosen. Harmless while model
    /// rotation was world-axis about a distant pivot; visible as soon as it became local.
    /// </para>
    /// <para>
    /// The rule is the tag first, the parent in everything else. Translation is inherited whole, so
    /// cuts still travel with the part — which Jeff wanted. Orientation is rebuilt: a Horizontal
    /// plane is forced flat, and a Vertical plane is forced upright while keeping whatever yaw it
    /// has picked up, so spinning the part carries the cut round with it and it keeps slicing the
    /// same feature.
    /// </para>
    /// <para>
    /// Deliberately stateless — it reads the plane's current world pose and corrects it, rather than
    /// remembering some earlier "untilted" pose that could drift out of step with the fields.
    /// </para>
    /// </remarks>
    internal void ConstrainModifierPlanesUnder(SceneNode meshNode)
    {
        // Runs on every mouse-move of a drag, so it walks the (usually empty) modifier list and
        // checks parentage upward, rather than walking the mesh's descendants and asking
        // FindModifierForNode who owns each one — that scans every modifier's whole subtree per
        // node, so the cost was descendants x modifiers x modifier-subtree, with a fresh iterator
        // allocated each time, at mouse-poll rate. Suspected cause of the stutter Jeff hit when
        // dragging hard back and forth.
        if (_modifierGizmoNodes.Count == 0) return;

        foreach (var (cut, node) in _modifierGizmoNodes)
        {
            if (!IsDescendantOf(node, meshNode)) continue;

            var w = node.WorldTransform;

            Matrix4 levelled;
            if (cut.Orientation == CutOrientation.Horizontal)
            {
                // Flat, always: normal is world +Z regardless of how the part is tumbled.
                levelled = Matrix4.Identity;
            }
            else
            {
                // Upright, always — but keep the yaw the part has handed it, so the cut spins with
                // the model. Flattening the normal into world XY is what discards pitch and roll.
                var n = new Vector3(w.Row0.X, w.Row0.Y, 0f);
                n = n.LengthSquared > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitX;
                levelled = Matrix4.CreateRotationZ(MathF.Atan2(n.Y, n.X));
            }

            levelled.Row3 = w.Row3;   // position rides the part untouched

            var parent = node.Parent?.WorldTransform ?? Matrix4.Identity;
            node.LocalTransform = MathF.Abs(parent.Determinant) > 1e-12f
                ? levelled * parent.Inverted()
                : levelled;
        }
    }

    /// <summary>Walks up from <paramref name="node"/> — a short chain — rather than scanning down.</summary>
    private static bool IsDescendantOf(SceneNode node, SceneNode ancestor)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
            if (ReferenceEquals(p, ancestor)) return true;
        return false;
    }

    /// <summary>
    /// Snaps the part onto the next clean 90° stop about world <paramref name="axisIndex"/>. Backs
    /// both clicking an axis letter in the transform toolbar and the <c>step</c> console command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a straighten-up button, not a nudge. The point is to get a flat face back down onto
    /// the bed, so an off-angle part is corrected rather than carried along: at 37° a click lands on
    /// 90°, not 127°. Already on a stop, it advances a full 90° — so repeated clicks cycle
    /// 0 / 90 / 180 / 270 exactly. <paramref name="reverse"/> (Alt-click) goes the other way, taking
    /// 37° down to 0°.
    /// </para>
    /// <para>
    /// The other two axes are snapped to their nearest stop as well. Snapping one axis alone would
    /// not produce a flat face if the other two were off-grid, which is the whole reason for the
    /// button. A composition of quarter turns about the coordinate axes maps axes onto axes, so the
    /// result always has a face square to the bed.
    /// </para>
    /// <para>
    /// Deliberately not <see cref="NodeTransform.RotateLocal"/>: that turns about the part's own
    /// axes, which is right for dragging a rotate ring but would leave an off-angle part off-angle.
    /// </para>
    /// </remarks>
    public string StepRotation(int axisIndex, bool reverse)
        => WithPlacement("step", t =>
        {
            var e = t.EulerDegrees;
            var snapped = new Vector3(NearestStop(e.X), NearestStop(e.Y), NearestStop(e.Z));

            float current = axisIndex switch { 0 => e.X, 1 => e.Y, _ => e.Z };
            float stepped = Wrap360(NextStop(current, reverse));

            t.EulerDegrees = axisIndex switch
            {
                0 => new Vector3(stepped, Wrap360(snapped.Y), Wrap360(snapped.Z)),
                1 => new Vector3(Wrap360(snapped.X), stepped, Wrap360(snapped.Z)),
                _ => new Vector3(Wrap360(snapped.X), Wrap360(snapped.Y), stepped),
            };

            return (t, $"snapped to {V(t.EulerDegrees)}° about world {"XYZ"[axisIndex]}.");
        });

    private const float StopTolerance = 0.5f;

    /// <summary>Nearest multiple of 90°.</summary>
    private static float NearestStop(float deg) => MathF.Round(deg / 90f) * 90f;

    /// <summary>
    /// The next 90° stop in the chosen direction. Already sitting on one, it moves a full quarter
    /// turn; off-grid, it lands on the next stop that way rather than adding to the odd angle.
    /// </summary>
    private static float NextStop(float deg, bool reverse)
    {
        bool onStop = MathF.Abs(deg - NearestStop(deg)) < StopTolerance;
        if (onStop) return NearestStop(deg) + (reverse ? -90f : 90f);
        return reverse ? MathF.Floor(deg / 90f) * 90f : MathF.Ceiling(deg / 90f) * 90f;
    }

    /// <summary>Keeps reported angles in a tidy 0-359 rather than drifting to 450 or -270.</summary>
    private static float Wrap360(float deg)
    {
        deg %= 360f;
        if (deg < 0f) deg += 360f;
        // 360 and -0 both read as 0.
        return MathF.Abs(deg - 360f) < 1e-3f ? 0f : deg + 0f;
    }

    /// <summary>Backs the <c>step</c> command.</summary>
    public string StepCommand(string args)
    {
        var p = (args ?? string.Empty).Split((char[])[' ', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (p.Length < 1 || AxisOf(p[0]) is not { } axis)
            return "[step] usage: step <x|y|z> [-]   e.g. `step z` or `step z -` for the other way";
        bool reverse = p.Length > 1 && (p[1] == "-" || p[1].Equals("rev", StringComparison.OrdinalIgnoreCase));
        return StepRotation(axis, reverse);
    }

    /// <summary>
    /// Backs the <c>gizmo</c> command: reads or sets which transform tool is active, so a headless
    /// run can put the viewport into Move, Rotate or Scale before exercising a handle.
    /// </summary>
    public string GizmoCommand(string args)
    {
        string verb = (args ?? string.Empty).Trim().ToLowerInvariant();
        if (verb.Length == 0)
            return $"[gizmo] mode={_activeGizmoMode}";

        var mode = verb switch
        {
            "move" or "translate" or "t" => GizmoMode.Translate,
            "rotate" or "rot" or "r"     => GizmoMode.Rotate,
            "scale" or "s"               => GizmoMode.Scale,
            "none" or "off"              => GizmoMode.None,
            _                            => (GizmoMode?)null,
        };
        if (mode is null)
            return "[gizmo] usage: gizmo [move|rotate|scale|none]";

        ActiveGizmoModeInternal = mode.Value;
        return $"[gizmo] mode={_activeGizmoMode}";
    }

    private static bool Triple(string[] p, out Vector3 v)
    {
        v = Vector3.Zero;
        if (p.Length < 4) return false;
        if (!float.TryParse(p[1], NumberStyles.Float, Inv, out float x)) return false;
        if (!float.TryParse(p[2], NumberStyles.Float, Inv, out float y)) return false;
        if (!float.TryParse(p[3], NumberStyles.Float, Inv, out float z)) return false;
        v = new Vector3(x, y, z);
        return true;
    }

    private static int? AxisOf(string s) => s.ToLowerInvariant() switch
    {
        "x" => 0,
        "y" => 1,
        "z" => 2,
        _   => null,
    };
}
