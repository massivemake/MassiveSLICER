namespace MassiveSlicer.ViewModels;

/// <summary>Destination for the top-bar Send action.</summary>
public enum SendTargetKind
{
    /// <summary>Classic KRL upload to the cell robot share (SMB).</summary>
    Robot,

    /// <summary>Job package to MassiveDRIVE path executor (RSI + ClearCore).</summary>
    MassiveDrive,
}

/// <summary>One entry in the Send destination dropdown.</summary>
public sealed record SendTargetOption(
    SendTargetKind Kind,
    string Label,
    string? Url,
    string? CellId)
{
    public override string ToString() => Label;
}
