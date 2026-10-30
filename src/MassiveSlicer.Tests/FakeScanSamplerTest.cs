using MassiveSlicer.Core.Scanning;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

public class FakeScanSamplerTest
{
    [Fact]
    public void A_face_in_front_of_the_camera_becomes_scan_points()
    {
        // Camera at the origin, looking +Z. Winding faces back toward the camera.
        float[] triangles =
        [
            -80, -80, 400,
            0, 80, 400,
            80, -80, 400,
        ];

        var cloud = FakeScanSampler.Rasterize(
            triangles,
            0, 0, 0,
            1, 0, 0,
            0, 1, 0,
            0, 0, 1);

        Assert.True(cloud.ValidPointCount > 50);
        int center = (cloud.Height / 2 * cloud.Width + cloud.Width / 2) * 3;
        Assert.InRange(cloud.PointsXYZ[center], -15f, 15f);
        Assert.InRange(cloud.PointsXYZ[center + 1], -15f, 15f);
        Assert.InRange(cloud.PointsXYZ[center + 2], 390f, 410f);
    }

    [Fact]
    public void A_high_camera_sees_the_whole_plate_and_a_close_one_sees_only_the_crown()
    {
        float[] plate =
        [
            -400, -400, 0,  400, -400, 0,  400, 400, 0,
            -400, -400, 0,  400, 400, 0,  -400, 400, 0,
        ];

        var high = FakeScanSampler.Rasterize(plate, 0, 0, 1200, -1, 0, 0, 0, 1, 0, 0, 0, -1);
        var close = FakeScanSampler.Rasterize(plate, 0, 0, 220, -1, 0, 0, 0, 1, 0, 0, 0, -1);

        Assert.True(Extent(high) > 320f);
        Assert.True(Extent(close) < 200f);
        Assert.True(high.ValidPointCount > 1000);
        Assert.True(close.ValidPointCount > 1000);
    }

    [Fact]
    public void A_camera_above_the_origin_looks_down()
    {
        Assert.True(FakeScanSampler.TryLookAt(0, 0, 1000, 0, 0, 0, out var pose));
        Assert.InRange(pose.ForwardZ, -1.01f, -0.99f);
        Assert.InRange(pose.UpY, 0.99f, 1.01f);
    }

    static float Extent(FakeScanCloud cloud)
    {
        float max = 0f;
        for (int i = 0; i < cloud.PointsXYZ.Length; i += 3)
        {
            if (float.IsNaN(cloud.PointsXYZ[i])) continue;
            max = MathF.Max(max, MathF.Abs(cloud.PointsXYZ[i]));
            max = MathF.Max(max, MathF.Abs(cloud.PointsXYZ[i + 1]));
        }
        return max;
    }

    [Fact]
    public void A_back_face_and_a_face_behind_the_camera_are_ignored()
    {
        float[] away =
        [
            -80, -80, 400,
            80, -80, 400,
            0, 80, 400,
        ];
        float[] behind =
        [
            -80, -80, -400,
            0, 80, -400,
            80, -80, -400,
        ];

        var turned = FakeScanSampler.Rasterize(away, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1);
        var rear = FakeScanSampler.Rasterize(behind, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 1);
        Assert.Equal(0, turned.ValidPointCount);
        Assert.Equal(0, rear.ValidPointCount);
    }

    [Fact]
    public void A_long_move_stops_along_the_way_and_ends_on_the_pose()
    {
        var start = new float[] { 0, 0, 0, 0, 0, 0, 0 };
        var target = new float[] { 90, -20, 0, 0, 0, 0, 10 };
        var stops = FakeScanMotion.CaptureStops(start, target, stepDegrees: 30f, maxStops: 8);

        Assert.Equal(3, stops.Count);
        Assert.InRange(stops[0][0], 25f, 35f);
        Assert.Equal(90f, stops[^1][0]);
        Assert.Equal(-20f, stops[^1][1]);
        Assert.Equal(10f, stops[^1][6]);
    }

    [Fact]
    public void A_pose_the_arm_is_already_on_is_one_stop()
    {
        var pose = new float[] { 10, -40, 80, 0, 20, 5, 0 };
        var stops = FakeScanMotion.CaptureStops(pose, (float[])pose.Clone());
        var only = Assert.Single(stops);
        Assert.Equal(pose, only);
    }

    [Fact]
    public void Fake_scan_data_starts_off()
    {
        var scan = new ScanSettingsViewModel();
        Assert.False(scan.UseFakeScanData);
        Assert.Contains("uses the Zivid camera", scan.DescribeFakeScan(), StringComparison.OrdinalIgnoreCase);

        scan.UseFakeScanData = true;
        Assert.Contains("not used", scan.DescribeFakeScan(), StringComparison.OrdinalIgnoreCase);
        scan.UseFakeScanData = false;
        Assert.Contains("uses the Zivid camera", scan.DescribeFakeScan(), StringComparison.OrdinalIgnoreCase);
    }
}
