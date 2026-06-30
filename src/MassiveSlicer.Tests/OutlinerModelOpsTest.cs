using MassiveSlicer.App;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class OutlinerModelOpsTest
{
    [Fact]
    public void CanReload_requires_source_file_and_mesh_geometry()
    {
        var vm = new ViewportViewModel();
        var node = new SceneNode
        {
            Name           = "part.glb",
            SourceFilePath = @"C:\missing\part.glb",
            PendingMesh    = BoxMesh(),
        };
        vm.AddImportNode(node);
        var item = vm.OutlinerItems.Single();

        Assert.True(OutlinerModelOps.CanReplace(item));
        Assert.False(OutlinerModelOps.CanReload(item));
    }

    [Fact]
    public void CanReplace_is_false_for_toolpaths_and_cell_infrastructure()
    {
        var vm = new ViewportViewModel();
        var robot = new SceneNode { Name = "RobotRoot" };
        vm.SetRobotGroup(robot, null, null);
        var robotItem = vm.OutlinerItems.Single(i => i.Name == "Robot Root");
        Assert.False(OutlinerModelOps.CanReplace(robotItem));

        var import = new SceneNode { Name = "Part", PendingMesh = BoxMesh() };
        vm.AddImportNode(import);
        var importItem = vm.EnumerateUserModelItems().Single();
        var toolpath = new SceneNode { Name = "Toolpath (preview)", PendingMesh = BoxMesh() };
        vm.RegisterToolpathInOutliner(toolpath, importItem);
        var toolpathItem = importItem.Children.Single();

        Assert.True(OutlinerModelOps.CanReplace(importItem));
        Assert.False(OutlinerModelOps.CanReplace(toolpathItem));
    }

    [Fact]
    public void TryReloadInto_preserves_local_transform()
    {
        var path = WriteTempStl();
        try
        {
            var target = new SceneNode
            {
                Name           = "widget.stl",
                LocalTransform = OpenTK.Mathematics.Matrix4.CreateTranslation(10f, 20f, 30f),
            };
            Assert.True(ImportHelper.TryReloadInto(target, path));
            Assert.Equal(10f, target.LocalTransform.M41, 0.01f);
            Assert.Equal(20f, target.LocalTransform.M42, 0.01f);
            Assert.Equal(30f, target.LocalTransform.M43, 0.01f);
            Assert.Equal(path, target.SourceFilePath);
            Assert.NotNull(target.PendingMesh);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempStl()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mslicer-test-{Guid.NewGuid():N}.stl");
        File.WriteAllText(path,
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
            """);
        return path;
    }

    private static MassiveSlicer.Viewport.Scene.MeshData BoxMesh()
    {
        var positions = new[]
        {
            new OpenTK.Mathematics.Vector3(0, 0, 0),
            new OpenTK.Mathematics.Vector3(1, 0, 0),
            new OpenTK.Mathematics.Vector3(1, 1, 0),
            new OpenTK.Mathematics.Vector3(0, 1, 0),
            new OpenTK.Mathematics.Vector3(0, 0, 1),
            new OpenTK.Mathematics.Vector3(1, 0, 1),
            new OpenTK.Mathematics.Vector3(1, 1, 1),
            new OpenTK.Mathematics.Vector3(0, 1, 1),
        };
        uint[] indices =
        [
            0, 1, 2, 0, 2, 3,
            4, 6, 5, 4, 7, 6,
            0, 4, 5, 0, 5, 1,
            1, 5, 6, 1, 6, 2,
            2, 6, 7, 2, 7, 3,
            3, 7, 4, 3, 4, 0,
        ];
        return new MassiveSlicer.Viewport.Scene.MeshData(positions, positions, indices, "box");
    }
}