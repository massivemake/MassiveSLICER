using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

/// <summary>
/// Two ways a workspace used to lose information that changes what the machine does.
///
/// 1. Settings whose value equals the type default (0 / false) were dropped by
///    <c>JsonIgnoreCondition.WhenWritingDefault</c>. On load the property initializer
///    supplied a *different* value, so "adaptive quality 0" came back as 0.5 and every
///    toggle the operator switched off came back on.
/// 2. <c>ToolpathMove.HeightScale</c> — the per-move flow factor that makes extrusion follow
///    real layer thickness — was never serialized at all, so reopening a workspace and
///    exporting without re-slicing commanded a full nominal layer's material on thin layers.
/// </summary>
public class WorkspacePersistenceTest
{
    private static T RoundTrip<T>(WorkspaceDocument doc, Func<WorkspaceDocument, T> pick)
    {
        var path = Path.Combine(Path.GetTempPath(), $"massive-persist-{Guid.NewGuid():N}.mass");
        try
        {
            WorkspaceLoader.Save(doc, path);
            var loaded = WorkspaceLoader.Load(path);
            Assert.NotNull(loaded);
            return pick(loaded!);
        }
        finally
        {
            foreach (var p in new[] { path, path + ".bak", path + ".tmp" })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AdaptiveQuality_zero_survives_a_save_and_load()
    {
        // Quality 0 = finest adaptive detail. It is a deliberate, print-validated setting,
        // and it is also `default(double)`, which is exactly why it used to vanish.
        var doc = new WorkspaceDocument();
        doc.Settings.AdaptiveLayerHeight = true;
        doc.Settings.AdaptiveQuality     = 0.0;

        Assert.Equal(0.0, RoundTrip(doc, d => d.Settings.AdaptiveQuality));
    }

    [Fact]
    public void Settings_turned_off_stay_off()
    {
        // Every one of these defaults to true/non-zero in AppPreferences, so switching it
        // off produced the type default and was silently dropped.
        var doc = new WorkspaceDocument();
        doc.Settings.ShowGrid              = false;
        doc.Settings.AntiAliasing          = false;
        doc.Settings.KeepOnBedWhenRotating = false;
        doc.Settings.TouchpadZoomSpeed     = 0f;
        doc.Settings.BrimLoops             = 0;

        var s = RoundTrip(doc, d => d.Settings);
        Assert.False(s.ShowGrid);
        Assert.False(s.AntiAliasing);
        Assert.False(s.KeepOnBedWhenRotating);
        Assert.Equal(0f, s.TouchpadZoomSpeed);
        Assert.Equal(0, s.BrimLoops);
    }

    [Fact]
    public void UiSession_toggles_turned_off_stay_off()
    {
        // Same class of loss, one struct over: ShowPaintMarkers initialises to true.
        var doc = new WorkspaceDocument
        {
            UiSession = new WorkspaceUiSession
            {
                ShowPaintMarkers       = false,
                ToolpathScrubLayerLow  = 0,
            },
        };

        var s = RoundTrip(doc, d => d.UiSession!);
        Assert.False(s.ShowPaintMarkers);
        Assert.Equal(0, s.ToolpathScrubLayerLow);
    }

    [Fact]
    public void HeightScale_round_trips_so_thin_layers_keep_their_reduced_flow()
    {
        // A 1 mm layer under a 3 mm nominal carries HeightScale 1/3. Lose it and the export
        // commands three times the material the layer can hold.
        var layer = new ToolpathLayer(0, 10f) { Height = 1f };
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(10, 0, 0), MoveKind.Extrude)
        {
            HeightScale = 1f / 3f,
        });
        layer.Moves.Add(new ToolpathMove(new Vector3(10, 0, 0), new Vector3(20, 0, 0), MoveKind.Extrude));

        var doc = new WorkspaceDocument
        {
            Models =
            {
                new WorkspaceModelEntry
                {
                    Name      = "Part",
                    Toolpaths =
                    {
                        new WorkspaceToolpathEntry
                        {
                            Name    = "Toolpath 1",
                            RawData = ToolpathSerializer.ToData(new Toolpath { Layers = { layer } }),
                        },
                    },
                },
            },
        };

        var raw = RoundTrip(doc, d => d.Models[0].Toolpaths[0].RawData!);
        var back = ToolpathSerializer.FromData(raw);

        Assert.Equal(1f / 3f, back.Layers[0].Moves[0].HeightScale, 5);
        // A move that never had a scale must still read as nominal, not as zero.
        Assert.Equal(1f, back.Layers[0].Moves[1].HeightScale, 5);
    }

    [Fact]
    public void HeightScale_reaches_the_exported_rpm_after_a_reload()
    {
        // The number that matters is what the exporter would write, not the field itself.
        var settings = new KrlExportSettings
        {
            ProgramName   = "T",
            BeadWidthMm   = 8f,
            LayerHeightMm = 3f,
            PrintSpeedMps = 0.06f,
            FlowRate      = 0.5863f,
        };

        var layer = new ToolpathLayer(1, 10f) { Height = 1f };
        layer.Moves.Add(new ToolpathMove(Vector3.Zero, new Vector3(10, 0, 0), MoveKind.Extrude)
        {
            HeightScale = 1f / 3f,
        });
        var toolpath = new Toolpath { Layers = { layer } };

        float before = ToolpathRpm.MovePercent(toolpath.Layers[0].Moves[0], settings);

        var reloaded = ToolpathSerializer.FromData(
            RoundTrip(
                new WorkspaceDocument
                {
                    Models =
                    {
                        new WorkspaceModelEntry
                        {
                            Name      = "Part",
                            Toolpaths = { new WorkspaceToolpathEntry
                            {
                                Name    = "Toolpath 1",
                                RawData = ToolpathSerializer.ToData(toolpath),
                            } },
                        },
                    },
                },
                d => d.Models[0].Toolpaths[0].RawData!));

        float after = ToolpathRpm.MovePercent(reloaded.Layers[0].Moves[0], settings);

        // Nominal for this bead/layer/speed is ~50.7 %; a 1 mm layer must ask for ~16.9 %.
        Assert.Equal(before, after, 3);
        Assert.InRange(after, 16.0f, 17.5f);
    }
}
