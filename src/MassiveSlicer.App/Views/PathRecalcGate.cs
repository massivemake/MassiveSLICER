namespace MassiveSlicer.App.Views;

/// <summary>
/// The status circle is one hold for a rebake burst, not a count of rebakes.
/// A completion turns it off only when that completion is still the newest batch,
/// the UI queue is idle, and the toolpath upload queues are empty.
/// </summary>
internal static class PathRecalcGate
{
    public static bool CanRelease(
        int completionGeneration,
        int latestGeneration,
        bool workQueued,
        bool uploadPending)
        => completionGeneration == latestGeneration && !workQueued && !uploadPending;
}
