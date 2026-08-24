using MassiveSlicer.Viewport.Loading;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public sealed class PointCloudMesherColorTest
{
    [Fact]
    public void Build_without_color_keeps_lime_and_no_texture()
    {
        var xyz = GridXyz(2, 2, spacing: 5f);
        var node = PointCloudMesher.Build(xyz, 2, 2, "Scan gray");
        Assert.NotNull(node);
        var mesh = node!.PendingMesh!;
        Assert.Null(mesh.Uvs);
        Assert.Null(mesh.Material?.BaseColor);
        Assert.True(mesh.BaseColor.Y > mesh.BaseColor.X);
    }

    [Fact]
    public void Build_with_rgba_attaches_srgb_color_map_and_grid_uvs()
    {
        const int w = 3, h = 2;
        var xyz = GridXyz(w, h, spacing: 4f);
        xyz[0] = float.NaN; // punch one invalid pixel
        xyz[1] = float.NaN;
        xyz[2] = float.NaN;

        var rgba = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba[i * 4]     = (byte)(40 + i * 20);
            rgba[i * 4 + 1] = (byte)(10 + i * 15);
            rgba[i * 4 + 2] = (byte)(200 - i * 10);
            rgba[i * 4 + 3] = 255;
        }

        var node = PointCloudMesher.Build(xyz, w, h, "Scan color", colorsRgba: rgba);
        Assert.NotNull(node);
        var mesh = node!.PendingMesh!;
        Assert.NotNull(mesh.Uvs);
        Assert.Equal(mesh.Positions.Length, mesh.Uvs!.Length);
        Assert.NotNull(mesh.Material?.BaseColor);
        var tex = mesh.Material!.BaseColor!;
        Assert.Equal(w, tex.Width);
        Assert.Equal(h, tex.Height);
        Assert.True(tex.IsSrgb);
        Assert.Equal(rgba.Length, tex.Pixels.Length);
        Assert.Equal(rgba[4 * 4], tex.Pixels[4 * 4]); // first valid pixel is grid index 1
        Assert.Equal(Vector4.One, mesh.BaseColor);

        // UV of first kept vertex (grid 1 = col 1, row 0) sits in that texel.
        Assert.InRange(mesh.Uvs[0].X, 1.4f / w, 1.6f / w);
        Assert.InRange(mesh.Uvs[0].Y, 0.4f / h, 0.6f / h);
    }

    static float[] GridXyz(int width, int height, float spacing)
    {
        var xyz = new float[width * height * 3];
        for (int r = 0; r < height; r++)
        for (int c = 0; c < width; c++)
        {
            int i = (r * width + c) * 3;
            xyz[i]     = c * spacing;
            xyz[i + 1] = r * spacing;
            xyz[i + 2] = 0f;
        }
        return xyz;
    }
}
