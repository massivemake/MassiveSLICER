using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public class MassiveDriveWaypointNotesTest
{
    [Fact]
    public void RequestsScan_Recognizes_Canonical_Tokens()
    {
        Assert.True(MassiveDriveWaypointNotes.RequestsScan("scan"));
        Assert.True(MassiveDriveWaypointNotes.RequestsScan("SCAN"));
        Assert.True(MassiveDriveWaypointNotes.RequestsScan("capture"));
        Assert.True(MassiveDriveWaypointNotes.RequestsScan("slicer:scan"));
        Assert.True(MassiveDriveWaypointNotes.RequestsScan("hand-eye"));
        Assert.True(MassiveDriveWaypointNotes.RequestsScan("do scan now"));
    }

    [Fact]
    public void RequestsScan_Ignores_Non_Capture_Notes()
    {
        Assert.False(MassiveDriveWaypointNotes.RequestsScan(null));
        Assert.False(MassiveDriveWaypointNotes.RequestsScan(""));
        Assert.False(MassiveDriveWaypointNotes.RequestsScan("approach"));
        Assert.False(MassiveDriveWaypointNotes.RequestsScan("home"));
        Assert.False(MassiveDriveWaypointNotes.RequestsScan("scanner docked"));
        Assert.False(MassiveDriveWaypointNotes.RequestsScan("bed")); // bed-cal is separate
    }

    [Fact]
    public void RequestsBed_Recognizes_Canonical_Tokens()
    {
        Assert.True(MassiveDriveWaypointNotes.RequestsBed("bed"));
        Assert.True(MassiveDriveWaypointNotes.RequestsBed("BED"));
        Assert.True(MassiveDriveWaypointNotes.RequestsBed("bedscan"));
        Assert.True(MassiveDriveWaypointNotes.RequestsBed("bed-cal"));
        Assert.True(MassiveDriveWaypointNotes.RequestsBed("slicer:bed"));
        Assert.False(MassiveDriveWaypointNotes.RequestsBed("scan"));
        Assert.False(MassiveDriveWaypointNotes.RequestsBed("approach"));
    }

    [Fact]
    public void SequenceNames_Are_Stable()
    {
        Assert.Equal("Scanner Calibration", MassiveDriveWaypointNotes.ScannerCalibrationSequenceName);
        Assert.Equal("Bed Calibration", MassiveDriveWaypointNotes.BedCalibrationSequenceName);
        Assert.Equal("scan", MassiveDriveWaypointNotes.ScanToken);
        Assert.Equal("bed", MassiveDriveWaypointNotes.BedToken);
    }
}
