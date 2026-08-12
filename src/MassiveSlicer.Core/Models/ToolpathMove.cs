using System.Numerics;

namespace MassiveSlicer.Core.Models;

public enum MoveKind { Extrude, Travel, Mill }

/// <summary>Shared predicates for how move kinds are rendered, picked, and scrubbed.</summary>
public static class ToolpathMoveKinds
{
    /// <summary>Visible cut/extrusion line segments (slicer extrude + imported KRL LIN).</summary>
    public static bool IsCutSegment(MoveKind kind) => kind is MoveKind.Extrude or MoveKind.Mill;

    /// <summary>Rapid positioning segments (slicer travel + imported KRL PTP).</summary>
    public static bool IsTravelSegment(MoveKind kind) => kind is MoveKind.Travel;
}

/// <summary>A single move segment in a toolpath -- from one point to another with a deposition intent.</summary>
public sealed record ToolpathMove(Vector3 From, Vector3 To, MoveKind Kind)
{
    public Vector3 Normal        { get; init; } = Vector3.Zero;  // Zero = use layer PlaneNormal (KRL exporter fallback)
    /// <summary>True when this travel move crosses a layer boundary (triggers ;layer change in KRL).</summary>
    public bool    IsLayerChange { get; init; } = false;
    /// <summary>True when this extrude move stitches the end of one layer to the start of the next
    /// (XY gap below the layer-change travel threshold). Post-processing effects should skip these.</summary>
    public bool    IsLayerStitch { get; init; } = false;

    /// <summary>Pre-travel filament wipe extrusion segment.</summary>
    public bool  IsWipe { get; init; }

    /// <summary>
    /// Bed-adhesion brim, not part of the object. Its length scales with the footprint
    /// perimeter and loop count, which says nothing about how long the object's own layer
    /// is — so layer-speed and thermal metrics must exclude it, and it must not take the
    /// adaptive speed scale (a brim wants the nominal first-layer speed, not the fastest
    /// speed in the part).
    /// </summary>
    public bool  IsBrim { get; init; }

    /// <summary>Local layer thickness relative to the layer's nominal height (1 = nominal).
    /// Multi-Planar layers are wedges: the plane tilt changes between layers, so thickness
    /// varies along the path. Scales extrusion RPM in export and bead height in preview.</summary>
    public float HeightScale { get; init; } = 1f;

    /// <summary>Part of a Lightning Bridge support finger (perimeter detour) —
    /// rendered as its own display layer so fingers can be isolated/hidden.</summary>
    public bool IsLightning { get; init; }

    /// <summary>RPM scale [0, 1] for wipe ramp-down (1 = full extrusion speed).</summary>
    public float WipeRpmScale { get; init; } = 1f;

    /// <summary>Post-travel resume ramp segment (stepped speed + RPM after travel).</summary>
    public bool IsResumeRamp { get; init; }

    /// <summary>Print speed scale [0, 1] for <see cref="IsResumeRamp"/> segments.</summary>
    public float ResumeSpeedScale { get; init; } = 1f;

    /// <summary>RPM scale [0, 1] for <see cref="IsResumeRamp"/> segments.</summary>
    public float ResumeRpmScale { get; init; } = 1f;

    /// <summary>Vertical or lifted component of a z-hop travel sequence.</summary>
    public bool IsZHop { get; init; }

    /// <summary>Travel inserted when merging separate toolpaths (retraction + connector).</summary>
    public bool IsMergeConnector { get; init; }

    /// <summary>Override travel speed (m/s) for this move during KRL export. Null uses global travel speed.</summary>
    public float? TravelSpeedMps { get; init; }

    /// <summary>
    /// Optional resume pause (seconds) after this travel (or wipe) before the next extrusion
    /// re-arms RPM. Used by multi-cell Start/Stop calibration; null = use export global
    /// <c>ExtrusionResumeWaitSec</c>.
    /// </summary>
    public float? ResumeWaitSec { get; init; }

    /// <summary>
    /// Layer-adaptive print speed scale [0, 1+] relative to global print speed.
    /// Also scales extrusion RPM for KRL <c>$ANOUT[4]</c> export.
    /// </summary>
    public float PrintSpeedScale { get; init; } = 1f;

    /// <summary>
    /// Extra TCP yaw (deg) applied on top of the toolhead orientation for this move.
    /// The nozzle is rotationally symmetric, so this spin is print-neutral — it is used
    /// to steer the robot wrist away from singularities. Mutable: assigned by the
    /// post-slice validation repair pass. 0 = no adjustment.
    /// </summary>
    public float TcpYawDeg { get; set; }

    /// <summary>
    /// Planned linear-rail E1 (mm) for this move. <see cref="float.NaN"/> = unset
    /// (exporter holds home E1 or falls back to geometric tracking).
    /// Filled by reachability-aware rail planning before KRL export.
    /// </summary>
    public float E1Mm { get; set; } = float.NaN;
}
