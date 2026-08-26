namespace MassiveSlicer.Core.Models;

/// <summary>Metric used to map each layer between minimum and maximum print speed.</summary>
public enum LayerSpeedBasis
{
    /// <summary>Interpolate by cut path length (extrude + mill) per layer.</summary>
    CutLength,

    /// <summary>Interpolate by estimated layer print time (all moves).</summary>
    LayerTime,

    /// <summary>
    /// Live print feedback. File length only slows short layers; long layers stay at
    /// print speed. Operator notes (layer:Δ%) then scale individual layers.
    /// </summary>
    PrintFeedback,
}