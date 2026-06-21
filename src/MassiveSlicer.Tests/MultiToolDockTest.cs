using MassiveSlicer.App;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class MultiToolDockTest
{
    private static string Lfam3Json =>
        AssetPaths.Resolve("assets/cells/LFAM3/lfam3.json");

    [Fact]
    public void Lfam3_builds_parked_dock_visuals_for_non_default_tools()
    {
        if (!File.Exists(Lfam3Json))
        {
            System.Console.WriteLine("LFAM3 cell JSON not found — skipping");
            return;
        }

        var cell = CellLoader.Load(Lfam3Json);
        var env  = CellEnvironmentBuilder.Build(cell);

        Assert.NotNull(env.MultiTools);
        var mt = env.MultiTools!;

        Assert.Equal("HV Extruder", mt.DefaultToolName);

        var dockable = cell.EffectiveTools.Where(t => t.Dock is not null).Select(t => t.Name).ToHashSet();
        Assert.Contains("Scanner", dockable);
        Assert.Contains("Spindle", dockable);

        Assert.Null(mt.MountedToolName);

        foreach (var name in dockable)
        {
            Assert.True(mt.Tools.ContainsKey(name), $"missing tool visual: {name}");
            var pair = mt.Tools[name];
            Assert.NotNull(pair.DockHolder);
            Assert.Equal($"Dock_{name}", pair.DockHolder!.Name);
            Assert.False(pair.FlangeHolder.Visible);
            Assert.True(pair.DockHolder.Visible);
        }

        int parked = mt.Tools.Values.Count(p => p.DockHolder is { Visible: true });
        Assert.Equal(3, parked);
        System.Console.WriteLine($"[test] LFAM3 multi-tool: {parked} parked on docks (flange empty)");
    }
}