using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class WorkspaceSaveTest
{
    [Fact]
    public void UiSession_round_trips_with_workspace()
    {
        var doc = new WorkspaceDocument
        {
            UiSession = new WorkspaceUiSession
            {
                ViewMode               = "Preview",
                IsPaintEditOpen        = true,
                IsSlicePlaneViewerActive = true,
                ShowMultiPlanarPlanes  = false,
                PaintLineRemoveActive  = true,
                PaintSelectGranularity = "Point",
                PaintPickFilter        = "Formbound",
                ToolpathScrubIndex     = 4200,
                ToolpathScrubLowIndex  = 3800,
                ToolpathScrubLayerHigh = 120,
                ToolpathScrubLayerLow  = 110,
                IsScrubSessionActive   = true,
                SelectToolpath         = true,
                ScrubModelName         = "Curtain",
                ScrubToolpathName      = "Toolpath 1",
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"massive-ui-session-{Guid.NewGuid():N}.mass");
        try
        {
            WorkspaceLoader.Save(doc, path);
            var loaded = WorkspaceLoader.Load(path);
            Assert.NotNull(loaded?.UiSession);
            var s = loaded!.UiSession!;
            Assert.Equal("Preview", s.ViewMode);
            Assert.True(s.IsPaintEditOpen);
            Assert.True(s.IsSlicePlaneViewerActive);
            Assert.False(s.ShowMultiPlanarPlanes);
            Assert.True(s.PaintLineRemoveActive);
            Assert.Equal("Point", s.PaintSelectGranularity);
            Assert.Equal("Formbound", s.PaintPickFilter);
            Assert.Equal(4200, s.ToolpathScrubIndex);
            Assert.Equal(3800, s.ToolpathScrubLowIndex);
            Assert.Equal(120, s.ToolpathScrubLayerHigh);
            Assert.Equal(110, s.ToolpathScrubLayerLow);
            Assert.True(s.IsScrubSessionActive);
            Assert.True(s.SelectToolpath);
            Assert.Equal("Curtain", s.ScrubModelName);
            Assert.Equal("Toolpath 1", s.ScrubToolpathName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_large_toolpath_raw_only_round_trips()
    {
        var doc = new WorkspaceDocument();
        var layer = new WorkspaceToolpathLayerData { Index = 0, Z = 0f };
        for (int i = 0; i < 20_000; i++)
        {
            float x0 = i;
            float x1 = i + 1;
            layer.Moves.Add(new WorkspaceToolpathMoveData
            {
                From = [x0, 0, 0],
                To   = [x1, 0, 0],
                Kind = nameof(MoveKind.Extrude),
            });
        }

        doc.Models.Add(new WorkspaceModelEntry
        {
            Name       = "Part",
            SourcePath = "C:\\models\\part.glb",
            Toolpaths =
            [
                new WorkspaceToolpathEntry
                {
                    Name    = "Toolpath",
                    RawData = new WorkspaceToolpathData { Layers = [layer] },
                },
            ],
        });

        var path = Path.Combine(Path.GetTempPath(), $"massive-ws-{Guid.NewGuid():N}.mass");
        try
        {
            WorkspaceLoader.Save(doc, path);
            Assert.True(new FileInfo(path).Length > 0);

            var loaded = WorkspaceLoader.Load(path);
            Assert.NotNull(loaded);
            Assert.Single(loaded!.Models);
            Assert.Single(loaded.Models[0].Toolpaths);
            Assert.Equal(20_000, loaded.Models[0].Toolpaths[0].RawData!.Layers[0].Moves.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}