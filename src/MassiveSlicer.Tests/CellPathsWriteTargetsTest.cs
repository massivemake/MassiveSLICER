using System.IO;
using System.Linq;
using MassiveSlicer.Core.IO;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Guards the fix for the lfam3.json corruption: by default a cell write must touch ONLY the
/// given file, never fan out to the repo / source copies (which a temp or build-dir path used to do).
/// </summary>
public class CellPathsWriteTargetsTest
{
    [Fact]
    public void WriteTargets_DefaultsToPrimaryOnly_EvenForCellsPath()
    {
        bool prev = CellPaths.MirrorToSourceTrees;
        CellPaths.MirrorToSourceTrees = false;
        try
        {
            // A path that contains /assets/cells/ but lives in a temp location.
            var temp = Path.Combine(Path.GetTempPath(), "mslicer-wt", "assets", "cells", "LFAM3", "lfam3.json");
            var targets = CellPaths.WriteTargetsFor(temp);

            Assert.Single(targets);
            Assert.Equal(Path.GetFullPath(temp), Path.GetFullPath(targets[0]));
            // Must not reach into any repo/source copy.
            Assert.DoesNotContain(targets, t => t.Replace('\\', '/').Contains("MassiveSlicer.App/Assets"));
            Assert.DoesNotContain(targets, t => t.Replace('\\', '/').Contains("MassiveFILES"));
        }
        finally
        {
            CellPaths.MirrorToSourceTrees = prev;
        }
    }

    [Fact]
    public void WriteTargets_MirrorsWhenOptedIn()
    {
        bool prev = CellPaths.MirrorToSourceTrees;
        CellPaths.MirrorToSourceTrees = true;
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), "mslicer-wt2", "assets", "cells", "LFAM3", "lfam3.json");
            var targets = CellPaths.WriteTargetsFor(temp);
            // Opt-in dev mirroring may add source/repo copies; the primary is always included.
            Assert.Contains(targets, t => Path.GetFullPath(t) == Path.GetFullPath(temp));
        }
        finally
        {
            CellPaths.MirrorToSourceTrees = prev;
        }
    }
}
