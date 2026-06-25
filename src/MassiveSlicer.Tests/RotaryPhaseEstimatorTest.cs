using MassiveSlicer.Core.Scanning;

namespace MassiveSlicer.Tests;

public class RotaryPhaseEstimatorTest
{
    // Build a dense perforated-plate surface (grid of circular holes), rotate it by a known angle,
    // and confirm the estimator recovers that angle from the hole lattice.
    private static List<(double, double)> PerforatedPlate(double angleDeg, double radius = 780,
        double pitch = 68, double holeR = 16, double sampleStep = 2.0)
    {
        double t = angleDeg * Math.PI / 180.0, c = Math.Cos(t), s = Math.Sin(t);
        // hole centres on a square lattice within the disk
        var holes = new List<(double, double)>();
        for (double hx = -radius; hx <= radius; hx += pitch)
            for (double hy = -radius; hy <= radius; hy += pitch)
                if (hx * hx + hy * hy < (radius - holeR) * (radius - holeR))
                    holes.Add((hx, hy));

        var pts = new List<(double, double)>();
        for (double x = -radius; x <= radius; x += sampleStep)
            for (double y = -radius; y <= radius; y += sampleStep)
            {
                if (x * x + y * y > radius * radius) continue;
                bool inHole = false;
                foreach (var (hx, hy) in holes)
                    if ((x - hx) * (x - hx) + (y - hy) * (y - hy) < holeR * holeR) { inHole = true; break; }
                if (inHole) continue;
                pts.Add((c * x - s * y, s * x + c * y));   // rotate the whole plate by angleDeg
            }
        return pts;
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.92)]
    [InlineData(1.5)]
    [InlineData(-3.0)]
    public void Recovers_known_plate_rotation(double trueAngle)
    {
        var pts = PerforatedPlate(trueAngle);
        var got = RotaryPhaseEstimator.HoleLatticeAngleDeg(pts, out int holes);
        Assert.True(got is not null, "estimator returned null");
        Assert.True(holes > 50, $"too few holes ({holes})");
        Assert.Equal(trueAngle, got!.Value, 1);   // within ~0.3° (tolerance 1 decimal)
    }
}
