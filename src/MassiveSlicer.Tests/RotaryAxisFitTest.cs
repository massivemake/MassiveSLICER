using System.Numerics;
using MassiveSlicer.Core.Scanning;

namespace MassiveSlicer.Tests;

public class RotaryAxisFitTest
{
    // Build samples on a circle of radius R about a known centre, in a plane whose normal is
    // tilted from +Z, then confirm the fit recovers the centre, the tilt, and a base orientation
    // whose Z axis matches the true axis.
    private static List<(double, Vector3)> MakeCircle(Vector3 center, Vector3 axis, float r, int count)
    {
        axis = Vector3.Normalize(axis);
        var seed = MathF.Abs(axis.X) < 0.9f ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0);
        var u = Vector3.Normalize(seed - Vector3.Dot(seed, axis) * axis);
        var v = Vector3.Cross(axis, u);
        var pts = new List<(double, Vector3)>();
        for (int i = 0; i < count; i++)
        {
            double deg = i * (300.0 / (count - 1));   // 0..300° spread
            double t = deg * Math.PI / 180.0;
            var p = center + r * (MathF.Cos((float)t) * u + MathF.Sin((float)t) * v);
            pts.Add((deg, p));
        }
        return pts;
    }

    [Fact]
    public void Vertical_axis_recovers_centre_and_zero_tilt()
    {
        var center = new Vector3(2007.07f, 10.98f, 900.08f);
        var pts = MakeCircle(center, Vector3.UnitZ, 250f, 12);

        var res = RotaryBedCalibration.Fit(pts);
        Assert.True(res.Success, res.Error);
        Assert.Equal(center.X, res.CenterX, 1);
        Assert.Equal(center.Y, res.CenterY, 1);
        Assert.Equal(center.Z, res.CenterZ, 1);
        Assert.Equal(250f, res.Radius, 1);
        Assert.True(res.AxisTiltDeg < 0.05f, $"tilt {res.AxisTiltDeg}");
        Assert.True(MathF.Abs(res.BaseA) < 0.1f && MathF.Abs(res.BaseB) < 0.1f && MathF.Abs(res.BaseC) < 0.1f,
            $"ABC ({res.BaseA},{res.BaseB},{res.BaseC}) should be ~0 for a vertical axis");
    }

    [Fact]
    public void Tilted_axis_recovers_tilt_and_axis_direction()
    {
        // Axis tilted ~3° toward +X (small B), like a real positioner mounting.
        float tiltDeg = 3f;
        float t = tiltDeg * MathF.PI / 180f;
        var axis = Vector3.Normalize(new Vector3(MathF.Sin(t), 0f, MathF.Cos(t)));
        var center = new Vector3(100f, -50f, 300f);
        var pts = MakeCircle(center, axis, 200f, 16);

        var res = RotaryBedCalibration.Fit(pts);
        Assert.True(res.Success, res.Error);
        Assert.Equal(center.X, res.CenterX, 1);
        Assert.Equal(center.Y, res.CenterY, 1);
        Assert.Equal(center.Z, res.CenterZ, 1);

        // Recovered axis matches the true axis (within ~0.2°), and tilt magnitude matches.
        var fitAxis = Vector3.Normalize(new Vector3(res.AxisX, res.AxisY, res.AxisZ));
        float dot = Math.Clamp(Vector3.Dot(fitAxis, axis), -1f, 1f);
        float angErr = MathF.Acos(dot) * 180f / MathF.PI;
        Assert.True(angErr < 0.2f, $"axis error {angErr}°");
        Assert.Equal(tiltDeg, res.AxisTiltDeg, 1);

        // A tilt purely toward +X maps (analytically) to a pure KUKA B = tilt, with A = C = 0.
        Assert.Equal(tiltDeg, res.BaseB, 1);
        Assert.True(MathF.Abs(res.BaseA) < 0.2f, $"A {res.BaseA}");
        Assert.True(MathF.Abs(res.BaseC) < 0.2f, $"C {res.BaseC}");
    }
}
