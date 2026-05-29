using System.Numerics;

namespace MassiveSlicer.Core.Models;

public enum MoveKind { Extrude, Travel }

/// <summary>A single move segment in a toolpath -- from one point to another with a deposition intent.</summary>
public sealed record ToolpathMove(Vector3 From, Vector3 To, MoveKind Kind)
{
    public Vector3 Normal { get; init; } = Vector3.UnitZ;
}
