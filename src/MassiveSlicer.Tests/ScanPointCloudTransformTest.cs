using System.Numerics;
using MassiveSlicer.Core.Scanning;

namespace MassiveSlicer.Tests;

public class ScanPointCloudTransformTest
{
    [Fact]
    public void ToWorld_Transforms_Valid_Points()
    {
        var cam = new float[] { 0, 0, 100, float.NaN, 1, 2 };
        var xform = Matrix4x4.CreateTranslation(1000, -50, 200);
        var (world, valid) = ScanPointCloudTransform.ToWorld(cam, xform);
        Assert.Equal(1, valid);
        Assert.Equal(1000f, world[0], 1);
        Assert.Equal(-50f, world[1], 1);
        Assert.Equal(300f, world[2], 1);
        Assert.True(float.IsNaN(world[3]));
    }

    [Fact]
    public void Decimate_Reduces_Count()
    {
        var pts = new float[30000];
        for (int i = 0; i < 10000; i++)
        {
            pts[i * 3] = i;
            pts[i * 3 + 1] = 0;
            pts[i * 3 + 2] = 0;
        }

        var dec = ScanPointCloudTransform.Decimate(pts, maxPoints: 100);
        Assert.True(dec.Length / 3 <= 100);
    }
}