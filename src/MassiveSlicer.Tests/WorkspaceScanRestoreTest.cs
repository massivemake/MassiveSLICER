using MassiveSlicer.App;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

public sealed class WorkspaceScanRestoreTest
{
    [Fact]
    public void ResolveRestoreMeshPath_prefers_embedded_stl_over_zdf_source()
    {
        var dir = NewTempDir();
        try
        {
            string stl = Path.Combine(dir, "workspace_meshes", "scan_abc.stl");
            Directory.CreateDirectory(Path.GetDirectoryName(stl)!);
            File.WriteAllText(stl, MinimalStl());
            string zdf = Path.Combine(dir, "scan.zdf");
            File.WriteAllBytes(zdf, [1, 2, 3]);
            string mass = Path.Combine(dir, "Rock V10.mass");

            var entry = new WorkspaceModelEntry
            {
                Name             = "Scan 17-33-54",
                IsScan           = true,
                SourcePath       = zdf,
                EmbeddedMeshPath = "workspace_meshes/scan_abc.stl",
                ScanZdfPath      = zdf,
            };

            string? mesh = WorkspaceService.ResolveRestoreMeshPath(entry, mass, out string? zdfPath);
            Assert.Equal(Path.GetFullPath(stl), mesh);
            Assert.Equal(Path.GetFullPath(zdf), zdfPath);
            Assert.False(WorkspaceService.IsZdfPath(mesh!));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void ResolveRestoreMeshPath_keeps_cad_source_ahead_of_embedded_stl()
    {
        var dir = NewTempDir();
        try
        {
            string stl = Path.Combine(dir, "workspace_meshes", "part.stl");
            Directory.CreateDirectory(Path.GetDirectoryName(stl)!);
            File.WriteAllText(stl, MinimalStl());
            string gltf = Path.Combine(dir, "rockV10.gltf");
            File.WriteAllText(gltf, "{}");
            string mass = Path.Combine(dir, "Rock V10.mass");

            var entry = new WorkspaceModelEntry
            {
                Name             = "rockV10",
                SourcePath       = gltf,
                EmbeddedMeshPath = "workspace_meshes/part.stl",
            };

            string? mesh = WorkspaceService.ResolveRestoreMeshPath(entry, mass, out string? zdfPath);
            Assert.Equal(gltf, mesh);
            Assert.Null(zdfPath);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void ResolveRestoreMeshPath_prefers_embedded_stl_for_a_shared_assembly_piece()
    {
        var dir = NewTempDir();
        try
        {
            string stl = Path.Combine(dir, "workspace_meshes", "lobe.stl");
            Directory.CreateDirectory(Path.GetDirectoryName(stl)!);
            File.WriteAllText(stl, MinimalStl());
            string glb = Path.Combine(dir, "iceberg_tables_rev8_grounded_tops.glb");
            File.WriteAllBytes(glb, [0x67, 0x6c, 0x54, 0x46]);
            string mass = Path.Combine(dir, "Lobes_All.mass");

            var piece = new WorkspaceModelEntry
            {
                Name             = "Lobes_P1",
                SourcePath       = glb,
                EmbeddedMeshPath = "workspace_meshes/lobe.stl",
                ChildPoses       = [new WorkspaceNodePose { Path = "0" }],
            };
            var assembly = new WorkspaceModelEntry
            {
                Name       = "iceberg_tables_rev8_grounded_tops",
                SourcePath = glb,
            };
            var counts = WorkspaceService.CountSourcePaths([piece, assembly]);

            Assert.True(WorkspaceService.PreferEmbeddedAssemblyPiece(piece, counts));
            Assert.False(WorkspaceService.PreferEmbeddedAssemblyPiece(assembly, counts));

            string? mesh = WorkspaceService.ResolveRestoreMeshPath(
                piece, mass, out string? zdfPath, preferEmbeddedPiece: true);
            Assert.Equal(Path.GetFullPath(stl), mesh);
            Assert.Null(zdfPath);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void PreferEmbeddedAssemblyPiece_leaves_a_real_hierarchy_on_the_cad_file()
    {
        string glb = Path.Combine(Path.GetTempPath(), "assembly.glb");
        var piece = new WorkspaceModelEntry
        {
            Name             = "Lobes_P1",
            SourcePath       = glb,
            EmbeddedMeshPath = "workspace_meshes/lobe.stl",
            ChildPoses =
            [
                new WorkspaceNodePose { Path = "0" },
                new WorkspaceNodePose { Path = "0/1" },
            ],
        };
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [glb] = 13 };
        Assert.False(WorkspaceService.PreferEmbeddedAssemblyPiece(piece, counts));

        var alone = new WorkspaceModelEntry
        {
            Name             = "rockV10",
            SourcePath       = glb,
            EmbeddedMeshPath = "workspace_meshes/part.stl",
        };
        var one = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [glb] = 1 };
        Assert.False(WorkspaceService.PreferEmbeddedAssemblyPiece(alone, one));
    }

    [Fact]
    public void RestoreModels_loads_scan_from_embedded_stl_when_source_is_zdf()
    {
        var dir = NewTempDir();
        try
        {
            string meshDir = Path.Combine(dir, "workspace_meshes");
            Directory.CreateDirectory(meshDir);
            string stl = Path.Combine(meshDir, "scan_abc.stl");
            File.WriteAllText(stl, MinimalStl());
            string zdf = Path.Combine(dir, "scan.zdf");
            File.WriteAllBytes(zdf, [1, 2, 3]);
            string mass = Path.Combine(dir, "Rock V10.mass");

            var doc = new WorkspaceDocument
            {
                Models =
                [
                    new WorkspaceModelEntry
                    {
                        Name             = "Scan 17-33-54",
                        IsScan           = true,
                        Visible          = true,
                        SourcePath       = zdf,
                        EmbeddedMeshPath = "workspace_meshes/scan_abc.stl",
                        ScanZdfPath      = zdf,
                    },
                ],
            };

            var vm = new ViewportViewModel();
            var pivot = new SceneNode { Name = "RotaryBed_Top", Selectable = false, PickTier = PickTier.Environment };
            vm.SetRotaryBedGroup(pivot, "Rotary Bed");

            int restored = WorkspaceService.RestoreModels(doc, vm, mass);
            Assert.Equal(1, restored);

            var scans = vm.GetBedLevelScanItems();
            Assert.Single(scans);
            Assert.Equal("Scan 17-33-54", scans[0].Name);
            Assert.True(OutlinerModelOps.IsScanItem(scans[0]));
            Assert.NotNull(scans[0].Node.PendingMesh);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"mslicer-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
    }

    static string MinimalStl() =>
        """
        solid test
          facet normal 0 0 1
            outer loop
              vertex 0 0 0
              vertex 1 0 0
              vertex 0 1 0
            endloop
          endfacet
        endsolid test
        """;
}
