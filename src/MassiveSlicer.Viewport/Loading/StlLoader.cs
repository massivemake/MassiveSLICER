using System.Globalization;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Loads binary and ASCII STL files into a <see cref="SceneNode"/> with a flat,
/// non-indexed triangle mesh. Assumes the STL is already in Z-up millimetres
/// (Rhino / CAD convention) — no coordinate conversion is applied.
/// </summary>
public static class StlLoader
{
    /// <summary>Loads an STL file and returns a scene node ready for GPU upload.</summary>
    /// <param name="path">Path to the .stl file.</param>
    /// <param name="name">Display name for the scene node (defaults to the filename stem).</param>
    public static SceneNode Load(string path, string? name = null)
    {
        var nodeName = name ?? Path.GetFileNameWithoutExtension(path);
        var mesh     = IsBinaryStl(path) ? ReadBinary(path, nodeName) : ReadAscii(path, nodeName);
        return new SceneNode { Name = nodeName, PendingMesh = mesh };
    }

    // ── Format detection ─────────────────────────────────────────────────────

    private static bool IsBinaryStl(string path)
    {
        var info = new FileInfo(path);
        if (info.Length < 84) return false;

        using var fs = File.OpenRead(path);
        fs.Seek(80, SeekOrigin.Begin);
        using var r  = new BinaryReader(fs);
        uint count   = r.ReadUInt32();
        return info.Length == 80 + 4 + (long)count * 50;
    }

    // ── Binary STL ───────────────────────────────────────────────────────────

    private static MeshData ReadBinary(string path, string name)
    {
        using var fs = File.OpenRead(path);
        using var r  = new BinaryReader(fs);

        r.ReadBytes(80);               // header
        uint triCount = r.ReadUInt32();

        var positions = new Vector3[triCount * 3];
        var normals   = new Vector3[triCount * 3];

        for (int t = 0; t < triCount; t++)
        {
            var n = ReadVec3(r);
            for (int v = 0; v < 3; v++)
            {
                int idx        = t * 3 + v;
                positions[idx] = ReadVec3(r);
                normals[idx]   = n;
            }
            r.ReadUInt16(); // attribute byte count
        }

        return new MeshData(positions, normals, null, name);
    }

    private static Vector3 ReadVec3(BinaryReader r) =>
        new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    // ── ASCII STL ────────────────────────────────────────────────────────────

    private static MeshData ReadAscii(string path, string name)
    {
        var positions = new List<Vector3>();
        var normals   = new List<Vector3>();
        var current   = Vector3.UnitZ;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();

            if (line.StartsWith("facet normal", StringComparison.OrdinalIgnoreCase))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                current = new Vector3(ParseF(p[2]), ParseF(p[3]), ParseF(p[4]));
            }
            else if (line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                positions.Add(new Vector3(ParseF(p[1]), ParseF(p[2]), ParseF(p[3])));
                normals.Add(current);
            }
        }

        return new MeshData(positions.ToArray(), normals.ToArray(), null, name);
    }

    private static float ParseF(string s) =>
        float.Parse(s, CultureInfo.InvariantCulture);
}
