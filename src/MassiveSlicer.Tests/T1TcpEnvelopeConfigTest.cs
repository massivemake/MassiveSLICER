using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public class T1TcpEnvelopeConfigTest
{
    [Fact]
    public void Lfam3_json_has_t1_tcp_envelope_glb()
    {
        var cell = CellLoader.Load(ResolveLfam3());
        Assert.NotNull(cell.T1TcpEnvelope);
        Assert.Equal("T1 HV TCP outer", cell.T1TcpEnvelope!.Name);
        Assert.Equal("assets/cells/LFAM3/t1_hv_tcp_envelope.glb", cell.T1TcpEnvelope.ModelPath);
    }

    [Fact]
    public void Every_checked_in_lfam3_json_has_t1_tcp_envelope()
    {
        foreach (var path in HeatedBedConfigTestEnumerate())
        {
            var cell = CellLoader.Load(path);
            Assert.True(cell.T1TcpEnvelope is not null, $"{path} missing t1TcpEnvelope");
            Assert.Contains("t1_hv_tcp_envelope.glb", cell.T1TcpEnvelope!.ModelPath);
        }
    }

    [Fact]
    public void Envelope_ghost_opacity_is_five_percent()
    {
        Assert.Equal(0.05f, BaseBedGhosting.T1EnvelopeGhostOpacity);
        var root = new SceneNode { Name = BaseBedGhosting.T1TcpEnvelopeNodeName };
        var child = new SceneNode { Name = "mesh" };
        root.AddChild(child);
        Assert.True(BaseBedGhosting.IsT1TcpEnvelope(child));
        Assert.False(BaseBedGhosting.IsT1TcpEnvelope(new SceneNode { Name = "HeatedBed" }));
    }

    private static IEnumerable<string> HeatedBedConfigTestEnumerate()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = root;
            for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
            {
                string[] rels =
                [
                    Path.Combine(dir, "assets", "cells", "LFAM3", "lfam3.json"),
                    Path.Combine(dir, "src", "assets", "cells", "LFAM3", "lfam3.json"),
                    Path.Combine(dir, "src", "MassiveSlicer.App", "Assets", "cells", "LFAM3", "lfam3.json"),
                    Path.Combine(dir, "src", "MassiveSlicer.Tests", "assets", "cells", "LFAM3", "lfam3.json"),
                ];
                foreach (var p in rels)
                {
                    if (!File.Exists(p)) continue;
                    var full = Path.GetFullPath(p);
                    if (full.IndexOf("MassiveSLICER", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (seen.Add(full))
                        yield return full;
                }
                dir = Directory.GetParent(dir)?.FullName ?? "";
            }
        }
    }

    private static string ResolveLfam3()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var p = Path.Combine(dir, "assets", "cells", "LFAM3", "lfam3.json");
            if (File.Exists(p)) return p;
            dir = Directory.GetParent(dir)?.FullName ?? "";
            if (string.IsNullOrEmpty(dir)) break;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "assets", "cells", "LFAM3", "lfam3.json");
    }
}
