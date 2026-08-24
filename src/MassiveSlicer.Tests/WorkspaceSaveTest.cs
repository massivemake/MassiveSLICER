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
                KrlToolIndex           = 12,
                KrlBaseIndex           = 1,
                Lfam3WorkflowPhase     = "Mill",
                HasPrePrintScanStep    = false,
                MountedToolName        = "Tool 12",
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
            Assert.Equal(12, s.KrlToolIndex);
            Assert.Equal(1, s.KrlBaseIndex);
            Assert.Equal("Mill", s.Lfam3WorkflowPhase);
            Assert.Equal(false, s.HasPrePrintScanStep);
            Assert.Equal("Tool 12", s.MountedToolName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// The pivot and the import scale are the two things the composed matrix cannot carry, so they
    /// are the two that get silently lost if the schema or the writer regresses — and losing them is
    /// exactly the bug that sent reopened parts back to the exporter's origin with a detached gizmo.
    /// A null here is not a crash, it is a part that quietly comes back in the wrong place, so pin
    /// the round trip.
    /// </summary>
    [Fact]
    public void Placement_pivot_and_import_scale_round_trip()
    {
        var doc = new WorkspaceDocument
        {
            Models =
            [
                new WorkspaceModelEntry
                {
                    Name         = "Bendy Wall",
                    PivotOrigin  = [12.5f, -30.25f, 7f],
                    // Not 1: a metres-as-millimetres import is corrected x1000 before the placement
                    // is taken, which is the case that redefines 100% if it fails to persist.
                    ImportScale  = [1000f, 1000f, 1000f],
                },
            ],
        };

        var path = Path.Combine(Path.GetTempPath(), $"massive-placement-{Guid.NewGuid():N}.mass");
        try
        {
            WorkspaceLoader.Save(doc, path);
            var loaded = WorkspaceLoader.Load(path);

            var entry = Assert.Single(loaded!.Models);
            Assert.NotNull(entry.PivotOrigin);
            Assert.Equal(3, entry.PivotOrigin!.Length);
            Assert.Equal(12.5f,   entry.PivotOrigin[0], 4);
            Assert.Equal(-30.25f, entry.PivotOrigin[1], 4);
            Assert.Equal(7f,      entry.PivotOrigin[2], 4);

            Assert.NotNull(entry.ImportScale);
            Assert.Equal(3, entry.ImportScale!.Length);
            Assert.Equal(1000f, entry.ImportScale[0], 4);
            Assert.Equal(1000f, entry.ImportScale[1], 4);
            Assert.Equal(1000f, entry.ImportScale[2], 4);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// A file written before pivots existed carries neither field. It must load as null rather than
    /// as a zero vector: null means "no placement saved, adopt a box-centre one", while [0,0,0]
    /// would read as a real pivot at the mesh origin — the detached-gizmo behaviour again, this time
    /// asserted as intentional.
    /// </summary>
    [Fact]
    public void Pre_pivot_files_load_with_no_placement_rather_than_a_zero_one()
    {
        var doc = new WorkspaceDocument
        {
            Models = [new WorkspaceModelEntry { Name = "Legacy" }],
        };

        var path = Path.Combine(Path.GetTempPath(), $"massive-legacy-{Guid.NewGuid():N}.mass");
        try
        {
            WorkspaceLoader.Save(doc, path);
            var loaded = WorkspaceLoader.Load(path);

            var entry = Assert.Single(loaded!.Models);
            Assert.Null(entry.PivotOrigin);
            Assert.Null(entry.ImportScale);
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

    [Fact]
    public void Mill_sidebar_round_trips_with_workspace()
    {
        var doc = new WorkspaceDocument
        {
            Settings = new AppPreferences
            {
                Mill = new MillSidebarSettings
                {
                    SelectedOperation = nameof(MillOperationKind.PlanarFacing),
                    AreaSelectTool    = nameof(MillAreaSelectTool.Box),
                    SelectedBitId     = "bit-ap90",
                    PlanarToolAxis    = nameof(MillPlanarAxisKind.WorldNegZ),
                    PlanarTiltDeg     = 15,
                    OffsetDistanceMm  = 2.5,
                    StepoverMm        = 6.5,
                    SpindleRpm        = 1800,
                },
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"massive-mill-sidebar-{Guid.NewGuid():N}.mass");
        try
        {
            WorkspaceLoader.Save(doc, path);
            var loaded = WorkspaceLoader.Load(path);
            var mill = loaded!.Settings.Mill;
            Assert.NotNull(mill);
            Assert.Equal(nameof(MillOperationKind.PlanarFacing), mill!.SelectedOperation);
            Assert.Equal(nameof(MillAreaSelectTool.Box), mill.AreaSelectTool);
            Assert.Equal("bit-ap90", mill.SelectedBitId);
            Assert.Equal(15, mill.PlanarTiltDeg, 4);
            Assert.Equal(2.5, mill.OffsetDistanceMm, 4);
            Assert.Equal(6.5, mill.StepoverMm, 4);
            Assert.Equal(1800, mill.SpindleRpm, 4);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}