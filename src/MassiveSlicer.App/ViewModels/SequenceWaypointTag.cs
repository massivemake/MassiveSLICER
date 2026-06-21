namespace MassiveSlicer.ViewModels;

/// <summary>Screen-space label for a tool-change sequence waypoint in the 3D viewport.</summary>
public sealed record SequenceWaypointTag(float ScreenX, float ScreenY, string Label);