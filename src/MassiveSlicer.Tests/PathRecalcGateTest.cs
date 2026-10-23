using MassiveSlicer.App.Views;
using Xunit;

namespace MassiveSlicer.Tests;

public class PathRecalcGateTest
{
    [Fact]
    public void Newest_idle_batch_releases_the_circle()
    {
        Assert.True(PathRecalcGate.CanRelease(3, 3, workQueued: false, uploadPending: false));
    }

    [Fact]
    public void Older_batch_does_not_release_over_a_newer_one()
    {
        Assert.False(PathRecalcGate.CanRelease(2, 3, workQueued: false, uploadPending: false));
    }

    [Fact]
    public void Queued_work_or_a_pending_upload_keeps_the_circle()
    {
        Assert.False(PathRecalcGate.CanRelease(3, 3, workQueued: true, uploadPending: false));
        Assert.False(PathRecalcGate.CanRelease(3, 3, workQueued: false, uploadPending: true));
    }
}
