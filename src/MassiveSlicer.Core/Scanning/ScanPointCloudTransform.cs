using System.Numerics;

namespace MassiveSlicer.Core.Scanning;

/// <summary>
/// CPU-side point cloud transforms — extract at Zivid capture time, not from GPU meshes.
/// </summary>
public static class ScanPointCloudTransform
{
    /// <summary>
    /// Maps camera-frame <see cref="ScanCaptureResult.PointsXYZ"/> to world mm via
    /// <paramref name="cameraToWorld"/> (row-vector convention, same as viewport FK).
    /// </summary>
    public static (float[] World, int ValidCount) ToWorld(float[] cameraXyz, Matrix4x4 cameraToWorld)
    {
        var world = new float[cameraXyz.Length];
        int valid = 0;
        for (int i = 0; i + 2 < cameraXyz.Length; i += 3)
        {
            if (float.IsNaN(cameraXyz[i]))
            {
                world[i] = float.NaN;
                world[i + 1] = float.NaN;
                world[i + 2] = float.NaN;
                continue;
            }

            var wv = Vector3.Transform(new Vector3(cameraXyz[i], cameraXyz[i + 1], cameraXyz[i + 2]), cameraToWorld);
            world[i] = wv.X;
            world[i + 1] = wv.Y;
            world[i + 2] = wv.Z;
            valid++;
        }

        return (world, valid);
    }

    /// <summary>Decimates a flat XYZ array to at most <paramref name="maxPoints"/> valid samples.</summary>
    public static float[] Decimate(float[] xyz, int maxPoints = 8000)
    {
        int total = 0;
        for (int i = 0; i + 2 < xyz.Length; i += 3)
            if (!float.IsNaN(xyz[i])) total++;

        if (total <= maxPoints) return xyz;

        int step = Math.Max(1, total / maxPoints);
        var outPts = new List<float>(maxPoints * 3);
        int seen = 0;
        for (int i = 0; i + 2 < xyz.Length; i += 3)
        {
            if (float.IsNaN(xyz[i])) continue;
            if (seen++ % step != 0) continue;
            outPts.Add(xyz[i]);
            outPts.Add(xyz[i + 1]);
            outPts.Add(xyz[i + 2]);
        }

        return outPts.ToArray();
    }

    /// <summary>
    /// Writes world XYZ (one point per line) for offline analysis / Python scripts.
    /// </summary>
    public static void WriteXyzFile(string path, float[] worldXyz)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder(worldXyz.Length * 8);
        for (int i = 0; i + 2 < worldXyz.Length; i += 3)
        {
            if (float.IsNaN(worldXyz[i])) continue;
            sb.Append(worldXyz[i].ToString("F2", inv)).Append(' ')
              .Append(worldXyz[i + 1].ToString("F2", inv)).Append(' ')
              .Append(worldXyz[i + 2].ToString("F2", inv)).Append('\n');
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, sb.ToString());
    }
}