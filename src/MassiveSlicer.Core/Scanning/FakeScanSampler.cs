namespace MassiveSlicer.Core.Scanning;

/// <summary>Organized camera-frame cloud. Invalid pixels are NaN.</summary>
public readonly record struct FakeScanCloud(float[] PointsXYZ, int Width, int Height, int ValidPointCount);

/// <summary>World camera. +Forward is the optical axis. Right, up, and forward are unit axes.</summary>
public readonly record struct FakeScanPose(
    float OriginX, float OriginY, float OriginZ,
    float RightX, float RightY, float RightZ,
    float UpX, float UpY, float UpZ,
    float ForwardX, float ForwardY, float ForwardZ);

/// <summary>
/// Builds a stand-in scan by rasterizing world triangles into the simulated camera.
/// The camera looks along +forward. A face is kept when its normal points back
/// toward the camera. Nothing here talks to a camera or a robot.
/// </summary>
public static class FakeScanSampler
{
    public const int Width = 160;
    public const int Height = 120;
    public const float HorizontalFovDegrees = 70f;
    public const float NearMm = 30f;
    public const float FarMm = 4000f;

    /// <summary>
    /// Camera at the origin looking at the target.
    /// World up is +Z, unless that is the look direction, in which case +Y is up.
    /// </summary>
    public static bool TryLookAt(
        float originX, float originY, float originZ,
        float targetX, float targetY, float targetZ,
        out FakeScanPose pose)
    {
        float fx = targetX - originX, fy = targetY - originY, fz = targetZ - originZ;
        float fl = MathF.Sqrt(fx * fx + fy * fy + fz * fz);
        if (fl < 1f)
        {
            pose = default;
            return false;
        }

        fx /= fl; fy /= fl; fz /= fl;
        float ux = 0f, uy = 0f, uz = 1f;
        if (MathF.Abs(fx * ux + fy * uy + fz * uz) > 0.98f)
        {
            ux = 0f; uy = 1f; uz = 0f;
        }

        float rx = uy * fz - uz * fy;
        float ry = uz * fx - ux * fz;
        float rz = ux * fy - uy * fx;
        float rl = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
        if (rl < 1e-4f)
        {
            pose = default;
            return false;
        }

        rx /= rl; ry /= rl; rz /= rl;
        ux = fy * rz - fz * ry;
        uy = fz * rx - fx * rz;
        uz = fx * ry - fy * rx;
        pose = new FakeScanPose(originX, originY, originZ, rx, ry, rz, ux, uy, uz, fx, fy, fz);
        return true;
    }

    /// <summary>
    /// <paramref name="triangles"/> is world xyz, 9 floats per triangle.
    /// Camera axes are world-space unit vectors. +forward is the optical axis.
    /// </summary>
    public static FakeScanCloud Rasterize(
        float[]? triangles,
        float originX, float originY, float originZ,
        float rightX, float rightY, float rightZ,
        float upX, float upY, float upZ,
        float forwardX, float forwardY, float forwardZ,
        int width = Width,
        int height = Height,
        float horizontalFovDegrees = HorizontalFovDegrees,
        float nearMm = NearMm,
        float farMm = FarMm)
    {
        if (width < 2) width = 2;
        if (height < 2) height = 2;
        int pixels = width * height;
        var points = new float[pixels * 3];
        for (int i = 0; i < points.Length; i++) points[i] = float.NaN;
        if (triangles is null || triangles.Length < 9 || farMm <= nearMm || horizontalFovDegrees <= 1f)
            return new FakeScanCloud(points, width, height, 0);

        float tanH = MathF.Tan(horizontalFovDegrees * 0.5f * (MathF.PI / 180f));
        float tanV = tanH * height / MathF.Max(1f, width);
        var depth = new float[pixels];
        Array.Fill(depth, float.PositiveInfinity);

        int triCount = triangles.Length / 9;
        for (int t = 0; t < triCount; t++)
        {
            int b = t * 9;
            ToCamera(triangles[b], triangles[b + 1], triangles[b + 2], out float ax, out float ay, out float az);
            ToCamera(triangles[b + 3], triangles[b + 4], triangles[b + 5], out float bx, out float by, out float bz);
            ToCamera(triangles[b + 6], triangles[b + 7], triangles[b + 8], out float cx, out float cy, out float cz);
            if (az <= nearMm || bz <= nearMm || cz <= nearMm) continue;
            if (az > farMm && bz > farMm && cz > farMm) continue;

            float e1x = bx - ax, e1y = by - ay;
            float e2x = cx - ax, e2y = cy - ay;
            float nz = e1x * e2y - e1y * e2x;
            if (nz >= 0f) continue;

            if (!Project(ax, ay, az, out float ac, out float ar)) continue;
            if (!Project(bx, by, bz, out float bc, out float br)) continue;
            if (!Project(cx, cy, cz, out float cc, out float cr)) continue;

            int c0 = (int)MathF.Floor(MathF.Min(ac, MathF.Min(bc, cc)));
            int c1 = (int)MathF.Ceiling(MathF.Max(ac, MathF.Max(bc, cc)));
            int r0 = (int)MathF.Floor(MathF.Min(ar, MathF.Min(br, cr)));
            int r1 = (int)MathF.Ceiling(MathF.Max(ar, MathF.Max(br, cr)));
            if (c1 < 0 || r1 < 0 || c0 >= width || r0 >= height) continue;
            if (c0 < 0) c0 = 0;
            if (r0 < 0) r0 = 0;
            if (c1 >= width) c1 = width - 1;
            if (r1 >= height) r1 = height - 1;

            float den = (br - cr) * (ac - cc) + (cc - bc) * (ar - cr);
            if (MathF.Abs(den) < 1e-6f) continue;

            for (int row = r0; row <= r1; row++)
            {
                for (int col = c0; col <= c1; col++)
                {
                    float px = col + 0.5f;
                    float py = row + 0.5f;
                    float weightA = ((br - cr) * (px - cc) + (cc - bc) * (py - cr)) / den;
                    float weightB = ((cr - ar) * (px - cc) + (ac - cc) * (py - cr)) / den;
                    float weightC = 1f - weightA - weightB;
                    if (weightA < -1e-3f || weightB < -1e-3f || weightC < -1e-3f) continue;

                    float invZ = weightA / az + weightB / bz + weightC / cz;
                    if (invZ <= 1e-8f) continue;
                    float z = 1f / invZ;
                    if (z < nearMm || z > farMm || z >= depth[row * width + col]) continue;

                    float x = z * (weightA * ax / az + weightB * bx / bz + weightC * cx / cz);
                    float y = z * (weightA * ay / az + weightB * by / bz + weightC * cy / cz);
                    int at = (row * width + col) * 3;
                    depth[row * width + col] = z;
                    points[at] = x;
                    points[at + 1] = y;
                    points[at + 2] = z;
                }
            }
        }

        int valid = 0;
        for (int i = 0; i < pixels; i++)
            if (!float.IsNaN(points[i * 3])) valid++;
        return new FakeScanCloud(points, width, height, valid);

        void ToCamera(float x, float y, float z, out float cx, out float cy, out float cz)
        {
            float dx = x - originX, dy = y - originY, dz = z - originZ;
            cx = dx * rightX + dy * rightY + dz * rightZ;
            cy = dx * upX + dy * upY + dz * upZ;
            cz = dx * forwardX + dy * forwardY + dz * forwardZ;
        }

        bool Project(float x, float y, float z, out float col, out float row)
        {
            float nx = (x / z) / tanH;
            float ny = (y / z) / tanV;
            col = (nx * 0.5f + 0.5f) * width;
            row = (0.5f - ny * 0.5f) * height;
            return true;
        }
    }
}
