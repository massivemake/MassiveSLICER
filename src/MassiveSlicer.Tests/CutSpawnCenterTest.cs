using MassiveSlicer.App.Enums;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
using NumVec3 = System.Numerics.Vector3;

namespace MassiveSlicer.Tests;

/// <summary>
/// A new cut used to sit on the rotary table: horizontal spawn copied only the mesh
/// height, and a cylinder-projection mill stores the cutter at the station (the arm),
/// not on the line that is drawn.
/// </summary>
public sealed class CutSpawnCenterTest
{
    static ViewportViewModel RotaryVm()
    {
        var vm = new ViewportViewModel();
        vm.SetRotaryBedGroup(new SceneNode { Name = "RotaryBed_Top" }, "Rotary Bed");
        return vm;
    }

    static OutlinerItemViewModel Import(ViewportViewModel vm, string name)
    {
        var node = new SceneNode { Name = name, Selectable = true };
        vm.AddImportNode(node);
        return vm.OutlinerItems.First(i => i.Name == "Rotary Bed").Children.First(c => c.Node == node);
    }

    /// <summary>
    /// Station points sit 100–200 mm +X and 50 mm +Y of the rotary centre, E1 +90° with
    /// sign −1. Drawing spins them back onto the part at X = −50, Y = 100 and 200.
    /// A travel home to the table must not drag that box back to the centre.
    /// </summary>
    static (SceneNode Node, Toolpath Path) StationMill()
    {
        var path = new Toolpath();
        var layer = new ToolpathLayer(0, 50f);
        layer.Moves.Add(new ToolpathMove(NumVec3.Zero, NumVec3.Zero, MoveKind.Travel));
        layer.Moves.Add(new ToolpathMove(
            new NumVec3(100f, 50f, 40f), new NumVec3(200f, 50f, 60f), MoveKind.Mill) { E1Mm = 90f });
        path.Layers.Add(layer);

        var node = new SceneNode { Name = "part Mill", Selectable = true };
        // The renderer parks the node on the raw station centroid, which is NOT the drawn line.
        node.LocalTransform = Matrix4.CreateTranslation(150f, 50f, 50f);
        return (node, path);
    }

    static void Wire(ViewportViewModel vm, SceneNode millNode, Toolpath path, Matrix4? spin = null)
    {
        vm.GetToolpathSnapshot = n => ReferenceEquals(n, millNode)
            ? new ToolpathSnapshot(path, path, 6f, 3f, NumVec3.One, NumVec3.Zero, -1f, spin)
            : null;
    }

    static void Near(float actual, float expected, string field)
        => Assert.True(MathF.Abs(actual - expected) < 0.05f, $"{field}: expected {expected}, got {actual}");

    [Fact]
    public void A_new_cut_spawns_on_the_drawn_toolpath_not_the_table()
    {
        var vm = RotaryVm();
        var model = Import(vm, "part");
        var print = new SceneNode { Name = "part Print", Selectable = true };
        vm.RegisterToolpathInOutliner(print, model, OutlinerToolpathKind.Print);
        var (millNode, path) = StationMill();
        vm.RegisterToolpathInOutliner(millNode, model, OutlinerToolpathKind.Mill);
        Wire(vm, millNode, path);

        var cut = vm.AddCutModifier(model);
        var plane = vm.GetModifierGizmoNode(cut);

        // Drawn box centre (−50, 150, 50), bed centre at the origin.
        Near(cut.PositionX, -50f, "PositionX");
        Near(cut.PositionY, 150f, "PositionY");
        Near(cut.Offset, 50f, "Offset");
        Assert.NotNull(plane);
        Near(plane!.WorldTransform.Row3.X, -50f, "plane X");
        Near(plane.WorldTransform.Row3.Y, 150f, "plane Y");
        Near(plane.WorldTransform.Row3.Z, 50f, "plane Z");
    }

    [Fact]
    public void A_new_vertical_cut_spawns_on_the_same_drawn_center()
    {
        var vm = RotaryVm();
        var model = Import(vm, "part");
        var (millNode, path) = StationMill();
        vm.RegisterToolpathInOutliner(millNode, model, OutlinerToolpathKind.Mill);
        Wire(vm, millNode, path);

        var cut = new CutModifier { Name = "Cut 01", Orientation = CutOrientation.Vertical };
        vm.GetOrCreateModifierGizmoNode(cut, model);

        // Rotation 0: normal +X, tangent +Y, so the drawn centre splits across those fields.
        Near(cut.Offset, -50f, "Offset");
        Near(cut.PositionTangent, 150f, "PositionTangent");
        Near(cut.PositionZ, 50f, "PositionZ");
    }

    [Fact]
    public void A_turned_table_spawns_the_cut_where_the_line_is_now()
    {
        var vm = RotaryVm();
        var model = Import(vm, "part");
        var (millNode, path) = StationMill();
        vm.RegisterToolpathInOutliner(millNode, model, OutlinerToolpathKind.Mill);
        Wire(vm, millNode, path, Matrix4.CreateRotationZ(MathF.PI));

        var cut = vm.AddCutModifier(model);

        // 180° about the rotary centre sends (−50, 150, 50) to (50, −150, 50).
        Near(cut.PositionX, 50f, "PositionX");
        Near(cut.PositionY, -150f, "PositionY");
        Near(cut.Offset, 50f, "Offset");
    }

    [Fact]
    public void A_restored_cut_does_not_take_the_spawn_center()
    {
        var vm = RotaryVm();
        var model = Import(vm, "part");
        model.Node.PendingMesh = new MeshData(
            [new Vector3(10f, 20f, 30f), new Vector3(110f, 80f, 90f)],
            [Vector3.UnitZ, Vector3.UnitZ],
            null,
            "part");
        var (millNode, path) = StationMill();
        vm.RegisterToolpathInOutliner(millNode, model, OutlinerToolpathKind.Mill);
        Wire(vm, millNode, path);

        var cut = vm.AddCutModifier(model, "Cut 03");

        Near(cut.PositionX, 0f, "PositionX");
        Near(cut.PositionY, 0f, "PositionY");
        Near(cut.Offset, 0f, "Offset");
    }

    [Fact]
    public void A_cut_without_a_toolpath_spawns_on_the_mesh_not_the_table()
    {
        var vm = RotaryVm();
        var model = Import(vm, "part");
        model.Node.PendingMesh = new MeshData(
            [new Vector3(10f, 20f, 30f), new Vector3(110f, 80f, 90f)],
            [Vector3.UnitZ, Vector3.UnitZ],
            null,
            "part");

        var cut = vm.AddCutModifier(model);

        // Box (10,20,30)–(110,80,90), centre (60, 50, 60). Bed centre is the origin.
        Near(cut.PositionX, 60f, "PositionX");
        Near(cut.PositionY, 50f, "PositionY");
        Near(cut.Offset, 60f, "Offset");
    }
}
