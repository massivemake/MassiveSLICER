namespace MassiveSlicer.Core.Models;

/// <summary>
/// Turns a clicked face into one Multi-Planar guide: height along the part,
/// and the X/Y tilts that reproduce that face normal.
/// </summary>
public static class GuidePlaneAlign
{
    public readonly record struct Result(double HeightPct, double AngleX, double AngleY);

    public static Result FromFace(
        float hitX, float hitY, float hitZ,
        float nx, float ny, float nz,
        float minX, float minY, float minZ,
        float maxX, float maxY, float maxZ)
    {
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len > 1e-6f) { nx /= len; ny /= len; nz /= len; }
        if (nz < 0f) { nx = -nx; ny = -ny; nz = -nz; }

        float ty = MathF.Asin(Math.Clamp(nx, -1f, 1f));
        float tx = MathF.Abs(MathF.Cos(ty)) < 1e-4f ? 0f : MathF.Atan2(-ny, nz);
        double angleY = Math.Clamp(ty * 180.0 / Math.PI, -80, 80);
        double angleX = Math.Clamp(tx * 180.0 / Math.PI, -80, 80);

        float cx = (minX + maxX) * 0.5f;
        float cy = (minY + maxY) * 0.5f;
        float h = MathF.Abs(nz) < 0.05f
            ? hitZ
            : hitZ + (nx * (hitX - cx) + ny * (hitY - cy)) / nz;
        float span = MathF.Max(maxZ - minZ, 1e-3f);
        double pct = Math.Clamp((h - minZ) / span * 100.0, 0, 100);
        return new Result(pct, angleX, angleY);
    }
}
