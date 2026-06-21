using System.Numerics;

namespace MassiveSlicer.Core.Models;

/// <summary>Source data and merge settings for a merged toolpath node.</summary>
public sealed class MergedToolpathRecord
{
    public required List<MergeSourceEntry> Sources { get; init; }
    public float RetractionHeightMm { get; set; }
    public float TravelSpeedMps { get; set; }
}

/// <summary>One toolpath baked into a merged result, with pose at merge time.</summary>
public sealed class MergeSourceEntry
{
    public required Toolpath LocalToolpath { get; init; }
    public required Vector3 Origin { get; init; }
    public required Matrix4x4 WorldTransform { get; init; }
    public float BeadWidth { get; init; }
    public float LayerHeight { get; init; }
    public System.Numerics.Vector3 MaterialColor { get; init; }
}