using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

public sealed class DriveJobStatusTest
{
    [Fact]
    public void Upload_pins_the_job_id_on_the_status_bar()
    {
        var bar = new StatusBarViewModel();
        bar.ShowUploadedDriveJob("  1842  ");
        Assert.Equal("Job 1842", bar.DriveJobText);
        Assert.True(bar.HasDriveJob);
    }

    [Fact]
    public void Blank_id_leaves_the_previous_job()
    {
        var bar = new StatusBarViewModel();
        bar.ShowUploadedDriveJob("9");
        bar.ShowUploadedDriveJob("  ");
        bar.ShowUploadedDriveJob(null);
        Assert.Equal("Job 9", bar.DriveJobText);
    }

    [Fact]
    public void Validation_text_does_not_clear_the_job()
    {
        var bar = new StatusBarViewModel();
        bar.ShowUploadedDriveJob("pkg-test");
        bar.OperationFeedback = "0 unreachable · 12 collisions";
        Assert.Equal("Job pkg-test", bar.DriveJobText);
        Assert.Equal("0 unreachable · 12 collisions", bar.OperationFeedback);
    }

    [Fact]
    public void A_later_upload_replaces_the_job()
    {
        var bar = new StatusBarViewModel();
        bar.ShowUploadedDriveJob("1");
        bar.ShowUploadedDriveJob("2");
        Assert.Equal("Job 2", bar.DriveJobText);
    }
}
