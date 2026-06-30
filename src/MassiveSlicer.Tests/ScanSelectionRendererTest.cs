using MassiveSlicer.Viewport;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public sealed class ScanSelectionRendererTest
{
    [Fact]
    public void SetScanSelection_Tracks_Multiple_Scans()
    {
        var renderer = new SceneRenderer();
        var a = new SceneNode { Name = "Scan 10-48" };
        var b = new SceneNode { Name = "Scan 10-49" };

        renderer.SetScanSelection([a, b], b);

        Assert.Equal(2, renderer.SelectedScanCount);
        Assert.Same(b, renderer.SelectedNode);
        Assert.Contains(a, renderer.SelectedScans);
        Assert.Contains(b, renderer.SelectedScans);
    }

    [Fact]
    public void Select_Clears_Scan_Multi_Select()
    {
        var renderer = new SceneRenderer();
        var scan = new SceneNode { Name = "Scan 10-48" };
        var import = new SceneNode { Name = "part.glb" };

        renderer.SetScanSelection([scan], scan);
        renderer.Select(import);

        Assert.Equal(0, renderer.SelectedScanCount);
        Assert.Same(import, renderer.SelectedNode);
    }
}