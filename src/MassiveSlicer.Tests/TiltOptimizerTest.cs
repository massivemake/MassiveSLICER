using System.Numerics;
using MassiveSlicer.Core.Slicing;
using Xunit;

namespace MassiveSlicer.Tests;

public sealed class TiltOptimizerTest
{
    /// <summary>Triangle soup for an axis-aligned box leaned by rotating about the Y axis (toward +X).</summary>
    private static Vector3[] LeaningBox(float leanDeg, float sx = 200f, float sy = 200f, float sz = 800f)
    {
        float a = leanDeg * MathF.PI / 180f;
        var soup = new List<Vector3>(36);

        Vector3 Lean(Vector3 p) => new(
            p.X * MathF.Cos(a) + p.Z * MathF.Sin(a),
            p.Y,
            -p.X * MathF.Sin(a) + p.Z * MathF.Cos(a));

        void Quad(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
        {
            soup.Add(Lean(p0)); soup.Add(Lean(p1)); soup.Add(Lean(p2));
            soup.Add(Lean(p0)); soup.Add(Lean(p2)); soup.Add(Lean(p3));
        }

        float hx = sx / 2f, hy = sy / 2f;
        // CCW-outward faces of the upright box, then leaned.
        Quad(new(-hx, -hy, 0), new(-hx,  hy, 0), new( hx,  hy, 0), new( hx, -hy, 0));      // bottom (n -Z)
        Quad(new(-hx, -hy, sz), new( hx, -hy, sz), new( hx,  hy, sz), new(-hx,  hy, sz));  // top (n +Z)
        Quad(new(-hx, -hy, 0), new( hx, -hy, 0), new( hx, -hy, sz), new(-hx, -hy, sz));    // front (n -Y)
        Quad(new( hx,  hy, 0), new(-hx,  hy, 0), new(-hx,  hy, sz), new( hx,  hy, sz));    // back (n +Y)
        Quad(new( hx, -hy, 0), new( hx,  hy, 0), new( hx,  hy, sz), new( hx, -hy, sz));    // right (n +X)
        Quad(new(-hx,  hy, 0), new(-hx, -hy, 0), new(-hx, -hy, sz), new(-hx,  hy, sz));    // left (n -X)

        // Shift so the lowest vertex sits on Z=0 (bed).
        float minZ = float.MaxValue;
        foreach (var p in soup) minZ = MathF.Min(minZ, p.Z);
        var arr = soup.ToArray();
        for (int i = 0; i < arr.Length; i++) arr[i] = arr[i] with { Z = arr[i].Z - minZ };
        return arr;
    }

    [Fact]
    public void TiltToDirection_MatchesAngledSlicerConvention()
    {
        // Pure Y tilt leans the normal toward +X; pure X tilt toward -Y (slicer's formula).
        var dY = TiltOptimizer.TiltToDirection(0f, 30f);
        Assert.True(dY.X > 0.49f && MathF.Abs(dY.Y) < 1e-4f);

        var dX = TiltOptimizer.TiltToDirection(30f, 0f);
        Assert.True(dX.Y < -0.49f && MathF.Abs(dX.X) < 1e-4f);
    }

    [Fact]
    public void Optimize_UprightBox_KeepsTiltNearZero()
    {
        var soup = new List<Vector3[]> { LeaningBox(0f) };
        var r = TiltOptimizer.Optimize(soup, 0f, 0f, allowMeshYaw: false);

        // A vertical prism has no overhangs; the tilt penalty must keep the answer flat.
        Assert.True(MathF.Abs(r.TiltXDeg) < 1.0f, $"X tilt {r.TiltXDeg}");
        Assert.True(MathF.Abs(r.TiltYDeg) < 1.0f, $"Y tilt {r.TiltYDeg}");
    }

    [Fact]
    public void Optimize_ColumnLeaningTowardX_TiltsWithTheLean()
    {
        // Slender column: caps are negligible, so aligning with the lean is unambiguous.
        var soup = new List<Vector3[]> { LeaningBox(60f, sx: 80f, sy: 80f, sz: 1200f) };
        var r = TiltOptimizer.Optimize(soup, 0f, 0f, allowMeshYaw: false);

        Assert.True(r.RiskBefore > 0.05f, $"upright slicing should be risky, got {r.RiskBefore:0.###}");
        Assert.True(r.TiltYDeg is > 40f and < 65f, $"expected tilt along the lean, got Y={r.TiltYDeg} X={r.TiltXDeg}");
        Assert.True(MathF.Abs(r.TiltXDeg) < 5f, $"X tilt should stay near zero, got {r.TiltXDeg}");
        Assert.True(r.RiskAfter < r.RiskBefore * 0.25f,
            $"risk should collapse: {r.RiskBefore:0.###} -> {r.RiskAfter:0.###}");
    }

    [Fact]
    public void Optimize_BoxLeaningTowardX_PicksAxisAlignedCompromise()
    {
        // A wide box's bottom cap becomes an unprintable ceiling when slicing fully along the
        // lean, so the optimum is a moderate tilt — but it must stay single-axis, not diagonal.
        var soup = new List<Vector3[]> { LeaningBox(60f) };
        var r = TiltOptimizer.Optimize(soup, 0f, 0f, allowMeshYaw: false);

        Assert.True(r.TiltYDeg is > 8f and < 40f, $"expected moderate +Y tilt, got Y={r.TiltYDeg}");
        Assert.True(MathF.Abs(r.TiltXDeg) < 5f, $"X tilt should stay near zero, got {r.TiltXDeg}");
        Assert.True(r.RiskAfter < r.RiskBefore * 0.25f,
            $"risk should collapse: {r.RiskBefore:0.###} -> {r.RiskAfter:0.###}");
    }

    [Fact]
    public void Optimize_ColumnLeaningTowardY_TiltOnly_UsesNegativeXTilt()
    {
        // Slicer convention: positive X tilt leans toward -Y, so a +Y lean needs negative X tilt.
        var col = Rotate90AboutZ(LeaningBox(60f, sx: 80f, sy: 80f, sz: 1200f));
        var r = TiltOptimizer.Optimize(new List<Vector3[]> { col }, 0f, 0f, allowMeshYaw: false);

        Assert.True(r.TiltXDeg is < -40f and > -65f, $"expected strong negative X tilt, got X={r.TiltXDeg}");
        Assert.True(MathF.Abs(r.TiltYDeg) < 5f, $"Y tilt should stay near zero, got {r.TiltYDeg}");
    }

    [Fact]
    public void Optimize_FreeAzimuth_YawsLeanOntoPureYTilt()
    {
        // Column leaning toward +Y (azimuth 90°) — free mode must yaw it onto +X (pure Y tilt).
        var col = Rotate90AboutZ(LeaningBox(60f, sx: 80f, sy: 80f, sz: 1200f));
        var r = TiltOptimizer.Optimize(new List<Vector3[]> { col }, 0f, 0f, allowMeshYaw: true);

        Assert.Equal(0f, r.TiltXDeg);
        Assert.True(r.TiltYDeg is > 40f and < 65f, $"expected tilt along the lean, got {r.TiltYDeg}");
        Assert.True(MathF.Abs(r.MeshYawDeg - (-90f)) < 5f, $"expected yaw ≈ -90°, got {r.MeshYawDeg}");
        Assert.True(r.RiskAfter < r.RiskBefore * 0.25f,
            $"risk should collapse: {r.RiskBefore:0.###} -> {r.RiskAfter:0.###}");
    }

    private static Vector3[] Rotate90AboutZ(Vector3[] soup)
    {
        var rot = new Vector3[soup.Length];
        for (int i = 0; i < soup.Length; i++)
        {
            var p = soup[i];
            rot[i] = new Vector3(-p.Y, p.X, p.Z);   // +90° about Z (orientation-preserving)
        }
        return rot;
    }
}
