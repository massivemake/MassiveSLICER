using MassiveSlicer.App;
using MassiveSlicer.ViewModels;
using MassiveSlicer.Viewport.Rendering;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public sealed class ScanMergerTest
{
    [Fact]
    public void IsScan_detects_scan_names()
    {
        Assert.True(OutlinerModelOps.IsScan(new SceneNode { Name = "Scan 12-34-56" }));
        Assert.True(OutlinerModelOps.IsScan(new SceneNode { Name = "Merged Scan (2 clouds)" }));
        Assert.False(OutlinerModelOps.IsScan(new SceneNode { Name = "part.glb" }));
    }

    [Fact]
    public void VertexBufferBytes_keeps_a_merged_scan_past_int32()
    {
        // Saved merged scan: 20,084,133 triangles, non-indexed, 14 floats per vertex.
        int vertices = 20_084_133 * 3;
        int floats = vertices * 14;
        long bytes = MeshRenderer.VertexBufferBytes(floats);

        Assert.Equal(3_374_134_344L, bytes);
        Assert.True(bytes > int.MaxValue);
        Assert.True(unchecked(floats * sizeof(float)) < 0);
    }

    [Fact]
    public void ApplyScanAppearance_keeps_the_same_vertex_positions()
    {
        var positions = new[]
        {
            new Vector3(12.5f, -3.25f, 40f),
            new Vector3(1f, 2f, 3f),
            new Vector3(0f, 0f, 1f),
        };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        var node = new SceneNode
        {
            Name = "Scan 01-02-03",
            PendingMesh = new MeshData(positions, normals, null, "scan"),
        };

        ViewportViewModel.ApplyScanAppearance(node);

        Assert.Same(positions, node.PendingMesh!.Positions);
        Assert.Same(normals, node.PendingMesh.Normals);
        Assert.True(node.PendingMesh.CompactScanLayout);
        Assert.True(node.PendingMesh.ReduceScanDisplay);
        Assert.Equal(6, MeshRenderer.FloatsPerUploadedVertex(node.PendingMesh));
        Assert.Equal(12.5f, node.PendingMesh.Positions[0].X, 4);
        Assert.Equal(-3.25f, node.PendingMesh.Positions[0].Y, 4);
        Assert.Equal(40f, node.PendingMesh.Positions[0].Z, 4);
    }

    [Fact]
    public void Old_scan_method_keeps_the_same_positions_at_full_stride()
    {
        var positions = new[]
        {
            new Vector3(12.5f, -3.25f, 40f),
            new Vector3(1f, 2f, 3f),
            new Vector3(0f, 0f, 1f),
        };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        var node = new SceneNode
        {
            Name = "Scan 01-02-03",
            PendingMesh = new MeshData(positions, normals, null, "scan") { CompactScanLayout = true },
        };

        ViewportViewModel.ApplyScanAppearance(node, compactScanLayout: false);

        Assert.Same(positions, node.PendingMesh!.Positions);
        Assert.Same(normals, node.PendingMesh.Normals);
        Assert.False(node.PendingMesh.CompactScanLayout);
        Assert.Equal(14, MeshRenderer.FloatsPerUploadedVertex(node.PendingMesh));
        Assert.Equal(12.5f, node.PendingMesh.Positions[0].X, 4);
        Assert.Equal(-3.25f, node.PendingMesh.Positions[0].Y, 4);
        Assert.Equal(40f, node.PendingMesh.Positions[0].Z, 4);

        var restored = ViewportViewModel.ScanMeshForUpload(node.PendingMesh, compactScanLayout: true);
        Assert.Same(positions, restored.Positions);
        Assert.Same(normals, restored.Normals);
        Assert.True(restored.CompactScanLayout);
        Assert.Equal(6, MeshRenderer.FloatsPerUploadedVertex(restored, allowCompact: true));
        Assert.Equal(14, MeshRenderer.FloatsPerUploadedVertex(restored, allowCompact: false));

        var textured = new MeshData(positions, normals, null, "part");
        textured.SetUvs([Vector2.Zero, Vector2.UnitX, Vector2.UnitY]);
        Assert.Same(textured, ViewportViewModel.ScanMeshForUpload(textured, compactScanLayout: true));
    }

    [Fact]
    public void Compact_layout_is_not_used_for_a_part()
    {
        var part = new MeshData(
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
            null,
            "rock");

        Assert.False(part.CompactScanLayout);
        Assert.False(MeshRenderer.UseCompactScanLayout(part));
        Assert.Equal(14, MeshRenderer.FloatsPerUploadedVertex(part));
    }

    [Fact]
    public void Merge_detached_world_snapshot_matches_a_parented_scan()
    {
        var parent = new SceneNode { LocalTransform = Matrix4.CreateTranslation(100f, -20f, 5f) };
        var aLocal = Matrix4.CreateTranslation(1f, 2f, 3f);
        var bLocal = Matrix4.CreateTranslation(4f, 0f, 0f);
        var a = ScanNode("Scan A", aLocal, 0f);
        var b = ScanNode("Scan B", bLocal, 1f);
        parent.AddChild(a);
        parent.AddChild(b);

        var live = ScanMerger.Merge([a, b], ScanMergeOutput.Mesh);
        var snapshots = new List<SceneNode>
        {
            new()
            {
                Name = a.Name,
                PendingMesh = a.PendingMesh,
                LocalTransform = a.WorldTransform,
            },
            new()
            {
                Name = b.Name,
                PendingMesh = b.PendingMesh,
                LocalTransform = b.WorldTransform,
            },
        };
        var snap = ScanMerger.Merge(snapshots, ScanMergeOutput.Mesh);

        Assert.NotNull(live);
        Assert.NotNull(snap);
        Assert.Equal(live!.Mesh.Positions.Length, snap!.Mesh.Positions.Length);
        for (int i = 0; i < live.Mesh.Positions.Length; i++)
        {
            Assert.Equal(live.Mesh.Positions[i].X, snap.Mesh.Positions[i].X, 4);
            Assert.Equal(live.Mesh.Positions[i].Y, snap.Mesh.Positions[i].Y, 4);
            Assert.Equal(live.Mesh.Positions[i].Z, snap.Mesh.Positions[i].Z, 4);
        }

        Assert.Equal(aLocal.M41, a.LocalTransform.M41, 4);
        Assert.Equal(aLocal.M42, a.LocalTransform.M42, 4);
        Assert.Equal(aLocal.M43, a.LocalTransform.M43, 4);
        Assert.Equal(bLocal.M41, b.LocalTransform.M41, 4);
        Assert.Equal(bLocal.M42, b.LocalTransform.M42, 4);
        Assert.Equal(bLocal.M43, b.LocalTransform.M43, 4);
    }

    [Fact]
    public void ApplyScanAppearance_stamps_a_white_restored_stl_lime()
    {
        var node = new SceneNode
        {
            Name = "Merged Scan (9 meshes)",
            PendingMesh = new MeshData(
                [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
                [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                null,
                "scan"),
        };

        ViewportViewModel.ApplyScanAppearance(node);

        var color = node.PendingMesh!.BaseColor;
        Assert.Equal(0.55f, color.X, 2);
        Assert.Equal(1.00f, color.Y, 2);
        Assert.Equal(0.15f, color.Z, 2);
        Assert.True(ViewportViewModel.IsScanLime(color));
    }

    [Fact]
    public void Merge_mesh_output_combines_world_geometry()
    {
        var a = ScanNode("Scan A", Matrix4.CreateTranslation(0f, 0f, 0f), 0f);
        var b = ScanNode("Scan B", Matrix4.CreateTranslation(5f, 0f, 0f), 1f);

        var result = ScanMerger.Merge([a, b], ScanMergeOutput.Mesh);
        Assert.NotNull(result);
        Assert.True(result!.PointCount >= 6);
        Assert.True(result.TriangleCount >= 2);
        Assert.False(result.Mesh.RenderAsPoints);
    }

    [Fact]
    public void Merge_point_cloud_output_has_no_indices()
    {
        var a = ScanNode("Scan A", Matrix4.Identity, 0f);
        var b = ScanNode("Scan B", Matrix4.CreateTranslation(2f, 0f, 0f), 0.5f);

        var result = ScanMerger.Merge([a, b], ScanMergeOutput.PointCloud);
        Assert.NotNull(result);
        Assert.Null(result!.Mesh.Indices);
        Assert.True(result.Mesh.RenderAsPoints);
    }

    [Fact]
    public void AlignToReferenceRotationOnly_undoes_a_ten_degree_yaw_on_a_long_cloud()
    {
        var reference = new List<Vector3>();
        for (int i = 0; i <= 30; i += 5)
            for (int j = 0; j <= 4; j++)
                reference.Add(new Vector3(i, j, 0));

        var pivot = Centroid(reference);
        const float rad = 10f * MathF.PI / 180f;
        float c = MathF.Cos(rad), s = MathF.Sin(rad);
        var moving = reference.Select(p =>
        {
            float x = p.X - pivot.X;
            float y = p.Y - pivot.Y;
            return new Vector3(pivot.X + c * x - s * y, pivot.Y + s * x + c * y, p.Z);
        }).ToList();

        var align = ScanPointCloudAligner.AlignToReferenceRotationOnly(reference, moving);
        float worst = 0f;
        for (int i = 0; i < reference.Count; i++)
        {
            var q = ScanPointCloudAligner.TransformPoint(moving[i], align);
            worst = MathF.Max(worst, (q - reference[i]).Length);
        }

        Assert.True(worst < 1f, $"worst residual {worst:0.###} mm");
    }

    [Fact]
    public void AlignToReferenceRotationOnly_recovers_a_yaw_past_the_pair_gate()
    {
        var reference = new List<Vector3>();
        for (int i = -80; i <= 80; i += 4)
        {
            reference.Add(new Vector3(i, 0, 0));
            reference.Add(new Vector3(i, 8, 0));
        }

        var pivot = Centroid(reference);
        const float rad = 25f * MathF.PI / 180f;
        float c = MathF.Cos(rad), s = MathF.Sin(rad);
        var moving = reference.Select(p =>
        {
            float x = p.X - pivot.X;
            float y = p.Y - pivot.Y;
            return new Vector3(pivot.X + c * x - s * y, pivot.Y + s * x + c * y, p.Z);
        }).ToList();

        var align = ScanPointCloudAligner.AlignToReferenceRotationOnly(reference, moving);
        float worst = 0f;
        for (int i = 0; i < reference.Count; i++)
        {
            var q = ScanPointCloudAligner.TransformPoint(moving[i], align);
            worst = MathF.Max(worst, (q - reference[i]).Length);
        }

        Assert.True(worst < 1.5f, $"worst residual {worst:0.###} mm");
    }

    [Fact]
    public void AlignToReferenceRotationOnly_preserves_moving_centroid()
    {
        var reference = new List<Vector3>
        {
            new(0, 0, 0), new(10, 0, 0), new(0, 10, 0), new(10, 10, 0),
        };
        var offset = new Vector3(50f, -20f, 5f);
        var moving = reference.Select(p => p + offset).ToList();

        var align = ScanPointCloudAligner.AlignToReferenceRotationOnly(reference, moving);
        var aligned = moving.Select(p => ScanPointCloudAligner.TransformPoint(p, align)).ToList();
        var alignedCentroid = Centroid(aligned);
        var movingCentroid  = Centroid(moving);

        Assert.Equal(movingCentroid.X, alignedCentroid.X, 0.2f);
        Assert.Equal(movingCentroid.Y, alignedCentroid.Y, 0.2f);
        Assert.Equal(movingCentroid.Z, alignedCentroid.Z, 0.2f);
    }

    [Fact]
    public void AlignToReference_closes_small_translation_gap()
    {
        var reference = new List<Vector3>
        {
            new(0, 0, 0), new(10, 0, 0), new(0, 10, 0), new(10, 10, 0),
        };
        var moving = reference.Select(p => p + new Vector3(2f, -1f, 0.5f)).ToList();

        var align = ScanPointCloudAligner.AlignToReference(reference, moving);
        var aligned = moving.Select(p => ScanPointCloudAligner.TransformPoint(p, align)).ToList();

        for (int i = 0; i < reference.Count; i++)
        {
            Assert.Equal(reference[i].X, aligned[i].X, 0.5f);
            Assert.Equal(reference[i].Y, aligned[i].Y, 0.5f);
            Assert.Equal(reference[i].Z, aligned[i].Z, 0.5f);
        }
    }

    [Fact]
    public void Reduced_display_keeps_original_triangles_and_the_full_mesh()
    {
        var mesh = DisplaySheet(40, 20, reduce: true);
        var source = mesh.Positions;
        var before = source[0];

        Assert.True(ScanDisplaySubset.TryBuild(mesh, out var thin, out var thinNormals, 50));

        Assert.Same(source, mesh.Positions);
        Assert.Equal(before, mesh.Positions[0]);
        Assert.True(thin.Length < source.Length);
        Assert.Equal(0, thin.Length % 3);
        Assert.Equal(thin.Length, thinNormals.Length);
        for (int i = 0; i < thin.Length; i++)
        {
            Assert.True(IsSourceVertex(source, mesh.Normals, thin[i], thinNormals[i]));
            Assert.Equal(thin[i].X, thinNormals[i].X);
            Assert.Equal(1f, thinNormals[i].Z);
        }

        var (srcMin, srcMax) = BoundsOf(source);
        var (outMin, outMax) = BoundsOf(thin);
        Assert.True(srcMin == outMin);
        Assert.True(srcMax == outMax);

        int probes = 0, covered = 0;
        for (int y = 2; y <= 17; y += 3)
        for (int x = 2; x <= 37; x += 3)
        {
            probes++;
            if (NearDisplaySurface(x * 2.5f, y * 3.5f, thin, 4f)) covered++;
        }
        Assert.True(covered * 4 >= probes * 3, $"covered {covered} of {probes}");
    }

    [Fact]
    public void Reduced_display_keeps_original_points()
    {
        const int n = 20;
        var positions = new Vector3[n * n];
        var normals = new Vector3[positions.Length];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int i = y * n + x;
            positions[i] = new Vector3(x * 5f, y * 4f, ((x + y) % 3) * 0.2f);
            normals[i] = new Vector3(positions[i].X, positions[i].Y, 1f);
        }

        var mesh = new MeshData(positions, normals, null, "scan")
        {
            RenderAsPoints = true,
            ReduceScanDisplay = true,
        };

        Assert.True(ScanDisplaySubset.TryBuild(mesh, out var thin, out var thinNormals, 30));
        Assert.Same(positions, mesh.Positions);
        Assert.True(thin.Length < positions.Length);
        Assert.Equal(0, thin.Length % 3);
        var known = new HashSet<Vector3>(positions);
        for (int i = 0; i < thin.Length; i++)
        {
            Assert.Contains(thin[i], known);
            Assert.Equal(thin[i].X, thinNormals[i].X);
            Assert.Equal(thin[i].Y, thinNormals[i].Y);
        }

        var (srcMin, srcMax) = BoundsOf(positions);
        var (outMin, outMax) = BoundsOf(thin);
        Assert.True(srcMin == outMin);
        Assert.True(srcMax == outMax);
    }

    [Fact]
    public void Reduced_display_keeps_indexed_triangles_in_their_original_order()
    {
        const int n = 8;
        var positions = new Vector3[n * n];
        var normals = new Vector3[positions.Length];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int i = y * n + x;
            positions[i] = new Vector3(x * 3f, y * 2f, ((x * 3 + y) % 5) * 0.5f);
            normals[i] = new Vector3(positions[i].X, 0f, 1f);
        }

        var indices = new List<uint>();
        for (int y = 0; y < n - 1; y++)
        for (int x = 0; x < n - 1; x++)
        {
            uint i = (uint)(y * n + x);
            indices.Add(i);
            indices.Add(i + 1);
            indices.Add(i + (uint)n);
            indices.Add(i + 1);
            indices.Add(i + 1 + (uint)n);
            indices.Add(i + (uint)n);
        }

        var indexArray = indices.ToArray();
        var mesh = new MeshData(positions, normals, indexArray, "scan") { ReduceScanDisplay = true };
        Assert.True(ScanDisplaySubset.TryBuild(mesh, out var thin, out _, 12));
        Assert.Same(positions, mesh.Positions);
        Assert.True(thin.Length < indexArray.Length);
        Assert.Equal(0, thin.Length % 3);
        var known = new HashSet<Vector3>(positions);
        for (int i = 0; i < thin.Length; i++)
            Assert.Contains(thin[i], known);

        var (srcMin, srcMax) = BoundsOf(positions);
        var (outMin, outMax) = BoundsOf(thin);
        Assert.True(srcMin == outMin);
        Assert.True(srcMax == outMax);
    }

    [Fact]
    public void Mill_paint_on_a_reduced_scan_keeps_the_thinned_draw()
    {
        var mesh = DisplaySheet(40, 20, reduce: true);
        var cpu = mesh.Positions;
        var full = new Vector2[cpu.Length];
        for (int i = 0; i < full.Length; i++)
            full[i] = new Vector2((i % 17) * 0.05f, 0f);

        Assert.True(MeshRenderer.TryReducedPaintVertices(
            mesh, full, out var drawPos, out var drawNrm, out var drawPaint, target: 40));
        Assert.True(drawPos.Length < cpu.Length);
        Assert.Equal(drawPos.Length, drawNrm.Length);
        Assert.Equal(drawPos.Length, drawPaint.Length);
        Assert.Same(cpu, mesh.Positions);

        Assert.True(ScanDisplaySubset.TryBuild(mesh, out _, out _, out var src, 40));
        Assert.Equal(drawPos.Length, src.Length);
        for (int i = 0; i < drawPos.Length; i++)
        {
            Assert.Equal(cpu[src[i]], drawPos[i]);
            Assert.Equal(full[src[i]].X, drawPaint[i].X, 5);
        }

        var whole = DisplaySheet(40, 20, reduce: false);
        var wholePaint = new Vector2[whole.Positions.Length];
        Assert.False(MeshRenderer.TryReducedPaintVertices(
            whole, wholePaint, out var samePos, out _, out var samePaint, target: 40));
        Assert.Same(whole.Positions, samePos);
        Assert.Same(wholePaint, samePaint);
    }

    [Fact]
    public void Display_subset_leaves_a_small_scan_and_an_unmarked_mesh_whole()
    {
        var small = DisplaySheet(2, 2, reduce: true);
        Assert.False(ScanDisplaySubset.TryBuild(small, out var samePos, out _));
        Assert.Same(small.Positions, samePos);

        var unmarked = DisplaySheet(40, 20, reduce: false);
        Assert.False(ScanDisplaySubset.TryBuild(unmarked, out _, out _, 50));
    }

    [Fact]
    public void Display_toggle_off_keeps_the_same_positions()
    {
        var positions = new[]
        {
            new Vector3(12.5f, -3.25f, 40f),
            new Vector3(1f, 2f, 3f),
            new Vector3(0f, 0f, 1f),
        };
        var normals = new[] { Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ };
        var node = new SceneNode
        {
            Name = "Scan 01-02-03",
            PendingMesh = new MeshData(positions, normals, null, "scan") { ReduceScanDisplay = true },
        };

        ViewportViewModel.ApplyScanAppearance(node, compactScanLayout: true, reduceScanDisplay: false);

        Assert.Same(positions, node.PendingMesh!.Positions);
        Assert.Same(normals, node.PendingMesh.Normals);
        Assert.False(node.PendingMesh.ReduceScanDisplay);
        Assert.Equal(12.5f, node.PendingMesh.Positions[0].X, 4);

        var uploaded = ViewportViewModel.ScanMeshForUpload(node.PendingMesh, compactScanLayout: true, reduceScanDisplay: false);
        Assert.Same(positions, uploaded.Positions);
        Assert.False(uploaded.ReduceScanDisplay);
    }

    private static MeshData DisplaySheet(int quadsX, int quadsY, bool reduce)
    {
        var positions = new List<Vector3>(quadsX * quadsY * 6);
        var normals = new List<Vector3>(positions.Capacity);
        for (int y = 0; y < quadsY; y++)
        for (int x = 0; x < quadsX; x++)
        {
            AddDisplayTri(positions, normals, x, y, x + 1, y, x, y + 1);
            AddDisplayTri(positions, normals, x + 1, y, x + 1, y + 1, x, y + 1);
        }

        return new MeshData(positions.ToArray(), normals.ToArray(), null, "scan")
        {
            ReduceScanDisplay = reduce,
        };
    }

    private static void AddDisplayTri(List<Vector3> positions, List<Vector3> normals, int x0, int y0, int x1, int y1, int x2, int y2)
    {
        AddDisplayVert(positions, normals, x0, y0);
        AddDisplayVert(positions, normals, x1, y1);
        AddDisplayVert(positions, normals, x2, y2);
    }

    private static void AddDisplayVert(List<Vector3> positions, List<Vector3> normals, int x, int y)
    {
        var p = new Vector3(x * 2.5f, y * 3.5f, ((x * 3 + y * 5) % 7) * 0.37f);
        positions.Add(p);
        normals.Add(new Vector3(p.X, p.Y, 1f));
    }

    private static bool IsSourceVertex(Vector3[] source, Vector3[] normals, Vector3 p, Vector3 n)
    {
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == p && normals[i] == n)
                return true;
        }
        return false;
    }

    private static bool NearDisplaySurface(float x, float y, Vector3[] tris, float slack)
    {
        float slack2 = slack * slack;
        for (int i = 0; i + 2 < tris.Length; i += 3)
        {
            if (PointInTriangleXY(x, y, tris[i], tris[i + 1], tris[i + 2]))
                return true;
            if (Dist2XY(x, y, tris[i]) <= slack2) return true;
            if (Dist2XY(x, y, tris[i + 1]) <= slack2) return true;
            if (Dist2XY(x, y, tris[i + 2]) <= slack2) return true;
        }
        return false;
    }

    private static float Dist2XY(float x, float y, Vector3 p)
    {
        float dx = x - p.X;
        float dy = y - p.Y;
        return dx * dx + dy * dy;
    }

    private static bool PointInTriangleXY(float x, float y, Vector3 a, Vector3 b, Vector3 c)
    {
        float c0 = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
        float c1 = (c.X - b.X) * (y - b.Y) - (c.Y - b.Y) * (x - b.X);
        float c2 = (a.X - c.X) * (y - c.Y) - (a.Y - c.Y) * (x - c.X);
        return (c0 >= -1e-3f && c1 >= -1e-3f && c2 >= -1e-3f)
            || (c0 <= 1e-3f && c1 <= 1e-3f && c2 <= 1e-3f);
    }

    private static (Vector3 Min, Vector3 Max) BoundsOf(Vector3[] positions)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.ComponentMin(min, p);
            max = Vector3.ComponentMax(max, p);
        }
        return (min, max);
    }

    private static Vector3 Centroid(IReadOnlyList<Vector3> pts)
    {
        var sum = Vector3.Zero;
        foreach (var p in pts) sum += p;
        return sum / pts.Count;
    }

    private static SceneNode ScanNode(string name, Matrix4 local, float zOffset)
    {
        var mesh = BoxMesh(zOffset);
        return new SceneNode
        {
            Name         = name,
            LocalTransform = local,
            PendingMesh  = mesh,
            CullFaces    = false,
        };
    }

    private static MeshData BoxMesh(float zOffset)
    {
        var positions = new[]
        {
            new Vector3(0, 0, zOffset), new Vector3(10, 0, zOffset),
            new Vector3(10, 10, zOffset), new Vector3(0, 10, zOffset),
            new Vector3(0, 0, 10 + zOffset), new Vector3(10, 0, 10 + zOffset),
            new Vector3(10, 10, 10 + zOffset), new Vector3(0, 10, 10 + zOffset),
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
        var normals = positions.Select(_ => -Vector3.UnitZ).ToArray();
        return new MeshData(positions, normals, indices, "scan");
    }
}