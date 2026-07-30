using OpenTK.Mathematics;
using MassiveSlicer.App.Views;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Tests;

/// <summary>
/// Drop to Plate must move the part straight down in WORLD space regardless of the frame its
/// parent sits in. On LFAM 3 user models hang off the rotary-bed pivot, whose frame carries
/// <c>baseAbc C = -90°</c> (lfam3.json) — local +Z maps onto world ±Y. The old code
/// post-multiplied a world-space translation onto the node's local transform, which silently
/// assumes the parent is identity, so Drop to Plate slid the part sideways a foot or two
/// instead of dropping it (reported by the shop 2026-07-29).
/// </summary>
public class DropToPlateFrameTest
{
    private const float BedZ = 893.67f;   // lfam3.json bed origin Z

    /// <summary>Unit cube spanning 0..100 in each axis, as pickable geometry.</summary>
    private static SceneNode CubeNode()
    {
        var pos = new List<Vector3>();
        void Tri(Vector3 a, Vector3 b, Vector3 c) { pos.Add(a); pos.Add(b); pos.Add(c); }
        var lo = Vector3.Zero;
        var hi = new Vector3(100f, 100f, 100f);
        // Only the extreme corners matter for min-Z; a couple of tris is enough.
        Tri(lo, new Vector3(hi.X, lo.Y, lo.Z), new Vector3(lo.X, hi.Y, lo.Z));
        Tri(hi, new Vector3(lo.X, hi.Y, hi.Z), new Vector3(hi.X, lo.Y, hi.Z));

        var mesh = new MeshData([.. pos], [.. pos.Select(_ => Vector3.UnitZ)], indices: null, "cube");
        // PendingMesh, not Mesh: MeshRenderer needs a live GL context. LayFlatMinZ falls back
        // to PendingMesh (the repo-wide idiom), so this exercises the real code path.
        return new SceneNode { Name = "cube", PendingMesh = mesh };
    }

    /// <summary>LFAM 3-style parent: rotary pivot inside a frame rolled -90° about X, offset.</summary>
    private static SceneNode RotaryParent()
    {
        var root = new SceneNode { Name = "RotaryBed" };
        root.LocalTransform = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(-90f))
                              * Matrix4.CreateTranslation(2134.44f, -52.88f, BedZ);
        var pivot = new SceneNode { Name = "RotaryBed_Top" };
        root.AddChild(pivot);
        return pivot;
    }

    /// <summary>Lowest world Z of the node's geometry — what "sits on the bed" means.</summary>
    private static float WorldMinZ(SceneNode node)
    {
        float min = float.MaxValue;
        foreach (var n in node.SelfAndDescendants())
        {
            if ((n.Mesh?.PickingData ?? n.PendingMesh) is not { } mesh) continue;
            var m = n.WorldTransform;
            foreach (var p in mesh.Positions)
                min = MathF.Min(min, p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);
        }
        return min;
    }

    private static Vector3 WorldOrigin(SceneNode n) => n.WorldTransform.Row3.Xyz;

    [Fact]
    public void Drop_lands_on_the_bed_under_a_flat_translated_parent()
    {
        // LFAM 1/2 shape: bed node is a pure translation.
        var bed = new SceneNode { Name = "bed" };
        bed.LocalTransform = Matrix4.CreateTranslation(1475.5f, -609.3f, 70f);
        var cube = CubeNode();
        bed.AddChild(cube);
        cube.LocalTransform = Matrix4.CreateTranslation(0f, 0f, 500f);

        ViewportView.DropNodeToBed(cube, BedZ);

        Assert.Equal(BedZ, WorldMinZ(cube), 2);
    }

    [Fact]
    public void Drop_lands_on_the_bed_under_the_LFAM3_rotary_frame()
    {
        var pivot = RotaryParent();
        var cube  = CubeNode();
        pivot.AddChild(cube);
        cube.LocalTransform = Matrix4.CreateTranslation(0f, 0f, 400f);

        ViewportView.DropNodeToBed(cube, BedZ);

        Assert.Equal(BedZ, WorldMinZ(cube), 2);
    }

    [Fact]
    public void Drop_moves_the_part_only_vertically_in_world_space()
    {
        // The actual reported symptom: it slid along Y instead of moving vertically.
        // Note "drop" legitimately moves a part UP when it starts below the bed — the
        // invariant is that the motion is purely world-vertical and ends on the bed.
        var pivot = RotaryParent();
        var cube  = CubeNode();
        pivot.AddChild(cube);
        cube.LocalTransform = Matrix4.CreateTranslation(0f, 0f, 400f);

        var before = WorldOrigin(cube);
        ViewportView.DropNodeToBed(cube, BedZ);
        var after = WorldOrigin(cube);

        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Y, after.Y, 2);
        Assert.Equal(BedZ, WorldMinZ(cube), 2);
    }

    [Fact]
    public void Drop_still_works_after_the_part_is_flipped_180()
    {
        // Wes's exact workflow: flip 180 degrees, then Drop to Plate.
        var pivot = RotaryParent();
        var cube  = CubeNode();
        pivot.AddChild(cube);
        cube.LocalTransform = Matrix4.CreateRotationX(MathF.PI)
                              * Matrix4.CreateTranslation(0f, 0f, 400f);

        var before = WorldOrigin(cube);
        ViewportView.DropNodeToBed(cube, BedZ);
        var after = WorldOrigin(cube);

        Assert.Equal(BedZ, WorldMinZ(cube), 2);
        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Y, after.Y, 2);
    }

    [Fact]
    public void Dropping_twice_is_idempotent()
    {
        var pivot = RotaryParent();
        var cube  = CubeNode();
        pivot.AddChild(cube);
        cube.LocalTransform = Matrix4.CreateTranslation(0f, 0f, 400f);

        ViewportView.DropNodeToBed(cube, BedZ);
        var once = WorldOrigin(cube);
        ViewportView.DropNodeToBed(cube, BedZ);
        var twice = WorldOrigin(cube);

        Assert.Equal(once.X, twice.X, 3);
        Assert.Equal(once.Y, twice.Y, 3);
        Assert.Equal(once.Z, twice.Z, 3);
    }
}
