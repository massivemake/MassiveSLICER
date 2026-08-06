using System.Text.Json;
using MassiveSlicer.Core.IO;

namespace MassiveSlicer.Tests;

public sealed class MassiveDrivePathGateTest
{
    [Fact]
    public void Parse_executor_wrapper_active()
    {
        using var doc = JsonDocument.Parse("""
            {"ok":true,"executor":{"active":true,"phase":"print","last_action":"print seg=0","job_id":"j1"}}
            """);
        var s = MassiveDrivePathGate.ParseExecutorJson(doc.RootElement);
        Assert.True(s.Reachable);
        Assert.True(s.PathActive);
        Assert.False(s.SafeForCalibration);
        Assert.Equal("print", s.Phase);
        Assert.Equal("j1", s.JobId);
        Assert.Contains("ACTIVE", s.Summary);
    }

    [Fact]
    public void Parse_executor_idle()
    {
        using var doc = JsonDocument.Parse("""
            {"ok":true,"executor":{"active":false,"phase":"done","last_action":"complete"}}
            """);
        var s = MassiveDrivePathGate.ParseExecutorJson(doc.RootElement);
        Assert.True(s.SafeForCalibration);
        Assert.False(s.PathActive);
        Assert.Contains("idle", s.Summary);
    }

    [Fact]
    public void Parse_flat_executor_object()
    {
        using var doc = JsonDocument.Parse("""{"active":true,"phase":"mill","package_id":"p9"}""");
        var s = MassiveDrivePathGate.ParseExecutorJson(doc.RootElement);
        Assert.True(s.PathActive);
        Assert.Equal("p9", s.JobId);
        Assert.Equal("mill", s.Phase);
    }

    [Fact]
    public void Unreachable_is_safe_for_offline_cal()
    {
        var s = MassiveDrivePathGate.Unreachable("connection refused");
        Assert.False(s.Reachable);
        Assert.False(s.PathActive);
        // Gate policy: unreachable DRIVE does not block CELL cal (handled by caller).
        Assert.False(s.SafeForCalibration); // SafeForCalibration requires Reachable
        Assert.Contains("unreachable", s.Summary, StringComparison.OrdinalIgnoreCase);
    }
}
