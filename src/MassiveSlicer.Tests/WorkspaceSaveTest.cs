using System.Numerics;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class WorkspaceSaveTest
{
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