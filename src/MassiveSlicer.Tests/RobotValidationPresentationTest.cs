using MassiveSlicer.App.Views;
using Xunit;

namespace MassiveSlicer.Tests;

public class RobotValidationPresentationTest
{
    [Fact]
    public void Clean_pass_clears_error_ui_and_does_not_block_export()
    {
        var clean = new RobotValidationPresentation.Counts(0, 0, 0, 1640090, 1, float.MaxValue, float.MinValue);

        Assert.True(RobotValidationPresentation.ShouldClearErrorUi(clean));
        Assert.False(RobotValidationPresentation.HasIssues(clean));
        Assert.False(RobotValidationPresentation.BlocksExport(clean));
        Assert.Equal("All 1640090 reachable", RobotValidationPresentation.ReachabilityLabel(clean));
        Assert.Equal("Robot validation: All 1640090 reachable.", RobotValidationPresentation.CleanSliceStatus(clean));
        Assert.Equal(-1, RobotValidationPresentation.FirstIssueIndex(
            [true, true, true], [false, false, false], null));
    }

    [Fact]
    public void Dirty_pass_keeps_blocking_banner_and_export_confirm()
    {
        var dirty = new RobotValidationPresentation.Counts(12, 1007, 0, 1640090, 1, 420f, 890f);

        Assert.False(RobotValidationPresentation.ShouldClearErrorUi(dirty));
        Assert.True(RobotValidationPresentation.HasIssues(dirty));
        Assert.True(RobotValidationPresentation.BlocksExport(dirty));
        Assert.Equal("12 / 1640090 unreachable", RobotValidationPresentation.ReachabilityLabel(dirty));

        string banner = RobotValidationPresentation.ErrorSliceStatus(dirty);
        Assert.Contains($"{1007:N0} singularity-risk", banner);
        Assert.Contains("12 unreachable", banner);
        Assert.Contains("between Z 420 and 890 mm", banner);
        Assert.True(RobotValidationPresentation.IsRobotValidationError(banner));
    }

    [Fact]
    public void Collision_only_pass_still_warns_but_matches_current_export_gate()
    {
        var collisions = new RobotValidationPresentation.Counts(0, 0, 4, 100, 8, 10f, 20f);

        Assert.True(RobotValidationPresentation.HasIssues(collisions));
        Assert.False(RobotValidationPresentation.ShouldClearErrorUi(collisions));
        Assert.False(RobotValidationPresentation.BlocksExport(collisions));
        Assert.Contains("4 collision (sampled 1/8)", RobotValidationPresentation.ReachabilityLabel(collisions));
        Assert.Contains("4 predicted collision moves", RobotValidationPresentation.ErrorSliceStatus(collisions, " (first: A2 ↔ bed)"));
    }

    [Fact]
    public void First_issue_index_is_the_first_flagged_move()
    {
        Assert.Equal(1, RobotValidationPresentation.FirstIssueIndex(
            [true, false, true], [false, false, true], [false, false, true]));
        Assert.Equal(0, RobotValidationPresentation.FirstIssueIndex(
            [true, true, true], [false, false, true], [true, false, false]));
        Assert.Equal(2, RobotValidationPresentation.FirstIssueIndex(
            [true, true, true], [false, false, true], null));
    }

    [Fact]
    public void Superseded_or_cancelled_pass_must_not_publish()
    {
        Assert.False(RobotValidationPresentation.ShouldPublishCompletedPass(cancelled: true, isCurrentRun: true));
        Assert.False(RobotValidationPresentation.ShouldPublishCompletedPass(cancelled: false, isCurrentRun: false));
        Assert.True(RobotValidationPresentation.ShouldPublishCompletedPass(cancelled: false, isCurrentRun: true));
    }

    [Fact]
    public void Prior_robot_validation_banner_is_recognized_for_clearing()
    {
        Assert.True(RobotValidationPresentation.IsRobotValidationError(
            "⚠ Robot validation: 1,007 singularity-risk, 0 unreachable — the robot may fault or crash mid-print."));
        Assert.False(RobotValidationPresentation.IsRobotValidationError(
            "Robot validation: All 1640090 reachable."));
        Assert.False(RobotValidationPresentation.IsRobotValidationError("Slice complete — 12 layers"));
        Assert.False(RobotValidationPresentation.IsRobotValidationError(null));
    }
}
