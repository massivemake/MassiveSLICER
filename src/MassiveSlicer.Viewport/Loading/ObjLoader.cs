using System.Globalization;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Loads Wavefront OBJ files into a <see cref="SceneNode"/> hierarchy.
/// Supports indexed positions and vertex normals, fan-triangulates polygons,
/// and computes flat face normals when the file omits them.
/// Assumes the OBJ is already in Z-up millimetres — no coordinate conversion applied.
/// </summary>
public static class ObjLoader
{
    public static SceneNode Load(string path, string? name = null)
    {
        var rootName = name ?? Path.GetFileNameWithoutExtension(path);

        var rawPos  = new List<Vector3>();
        var rawNorm = new List<Vector3>();

        // Accumulate per-object triangle soup: each entry is (pos[3], norm?[3])
        var objects  = new List<(string name, List<(Vector3[] pos, Vector3?[] norm)> tris)>();
        var curName  = rootName;
        var curTris  = new List<(Vector3[] pos, Vector3?[] norm)>();

        void Flush()
        {
            if (curTris.Count > 0) objects.Add((curName, curTris));
        }

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.AsSpan().Trim();
            if (line.IsEmpty || line[0] == '#') continue;

            var tok = line.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tok.Length == 0) continue;

            switch (tok[0])
            {
                case "v" when tok.Length >= 4:
                    rawPos.Add(new Vector3(F(tok[1]), F(tok[2]), F(tok[3])));
                    break;

                case "vn" when tok.Length >= 4:
                    rawNorm.Add(new Vector3(F(tok[1]), F(tok[2]), F(tok[3])));
                    break;

                case "o" or "g":
                    Flush();
                    curName = tok.Length > 1 ? tok[1] : rootName;
                    curTris = [];
                    break;

                case "f" when tok.Length >= 4:
                    int fanLen = tok.Length - 1;
                    var fp = new Vector3[fanLen];
                    var fn = new Vector3?[fanLen];

                    for (int i = 0; i < fanLen; i++)
                    {
                        var (pi, ni) = ParseFaceVert(tok[i + 1], rawPos.Count, rawNorm.Count);
                        fp[i] = rawPos[pi];
                        fn[i] = ni >= 0 ? rawNorm[ni] : null;
                    }

                    // Fan triangulation: v0-v1-v2, v0-v2-v3, …
                    for (int i = 1; i + 1 < fanLen; i++)
                    {
                        curTris.Add((
                            [fp[0], fp[i], fp[i + 1]],
                            [fn[0], fn[i], fn[i + 1]]
                        ));
                    }
                    break;
            }
        }

        Flush();

        if (objects.Count == 0)
            return new SceneNode { Name = rootName };

        static SceneNode BuildNode(string objName, List<(Vector3[] pos, Vector3?[] norm)> tris)
        {
            var outPos  = new Vector3[tris.Count * 3];
            var outNorm = new Vector3[tris.Count * 3];

            for (int t = 0; t < tris.Count; t++)
            {
                var (pos, norm) = tris[t];
                var faceN = SafeNorm(Vector3.Cross(pos[1] - pos[0], pos[2] - pos[0]));
                for (int v = 0; v < 3; v++)
                {
                    outPos [t * 3 + v] = pos[v];
                    outNorm[t * 3 + v] = norm[v] ?? faceN;
                }
            }

            return new SceneNode { Name = objName, PendingMesh = new MeshData(outPos, outNorm, null, objName) };
        }

        if (objects.Count == 1)
            return BuildNode(objects[0].name, objects[0].tris);

        var root = new SceneNode { Name = rootName };
        foreach (var (oName, oTris) in objects)
            root.AddChild(BuildNode(oName, oTris));
        return root;
    }

    // -- Helpers ------------------------------------------------------------------

    private static (int posIdx, int normIdx) ParseFaceVert(string token, int posCount, int normCount)
    {
        int slash = token.IndexOf('/');
        if (slash < 0)
            return (Resolve(int.Parse(token, CultureInfo.InvariantCulture), posCount), -1);

        var parts = token.Split('/');
        int pi = Resolve(int.Parse(parts[0], CultureInfo.InvariantCulture), posCount);
        int ni = parts.Length >= 3 && parts[2].Length > 0
            ? Resolve(int.Parse(parts[2], CultureInfo.InvariantCulture), normCount)
            : -1;
        return (pi, ni);
    }

    /// <summary>Converts a 1-based (positive) or end-relative (negative) OBJ index to 0-based.</summary>
    private static int Resolve(int raw, int count) => raw > 0 ? raw - 1 : count + raw;

    private static Vector3 SafeNorm(Vector3 v)
    {
        float len = v.Length;
        return len > 1e-6f ? v / len : Vector3.UnitZ;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
}
