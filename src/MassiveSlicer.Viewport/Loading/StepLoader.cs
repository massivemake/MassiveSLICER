using MassiveSlicer.Viewport.Scene;
using Occt;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Tessellates STEP (.stp / .step) CAD files into a flat triangle mesh via Open CASCADE.
/// Assumes Z-up millimetres (Rhino / CAD convention) — no coordinate conversion applied.
/// </summary>
public static class StepLoader
{
    /// <summary>Linear mesh deflection in millimetres.</summary>
    private const double LinearDeflectionMm = 0.5;

    /// <summary>Angular deflection in radians.</summary>
    private const double AngularDeflectionRad = 0.5;

    public static SceneNode Load(string path, string? name = null)
    {
        OcctBootstrap.EnsureInitialized();

        var nodeName = name ?? Path.GetFileNameWithoutExtension(path);
        var mesh     = Tessellate(path, nodeName);
        return new SceneNode { Name = nodeName, PendingMesh = mesh };
    }

    private static MeshData Tessellate(string path, string name)
    {
        var reader = new STEPControl_Reader();
        var status = reader.ReadFile(path);
        if (status != IFSelect_ReturnStatus.IFSelect_RetDone)
            throw new InvalidDataException($"STEP read failed ({status}): {path}");

        reader.TransferRoots();
        var shape = reader.OneShape();

        var mesher = new BRepMesh_IncrementalMesh(shape, LinearDeflectionMm, false, AngularDeflectionRad, true);
        mesher.Perform();

        var positions = new List<Vector3>();
        var normals   = new List<Vector3>();

        var exp = new TopExp_Explorer(shape, TopAbs_ShapeEnum.TopAbs_FACE);
        while (exp.More)
        {
            AppendFaceTriangles((TopoDS_Face)exp.Current, positions, normals);
            exp.Next();
        }

        if (positions.Count == 0)
            throw new InvalidDataException($"STEP file contains no tessellated geometry: {path}");

        return new MeshData(positions.ToArray(), normals.ToArray(), null, name);
    }

    private static void AppendFaceTriangles(TopoDS_Face face, List<Vector3> positions, List<Vector3> normals)
    {
        TopLoc_Location loc = new();
        var tri = BRep_Tool.Triangulation(face, out loc);
        if (tri is null) return;

        var trsf = loc.Transformation;
        var nodes = new Vector3[tri.NbNodes];
        for (int i = 1; i <= tri.NbNodes; i++)
        {
            var p = tri.Node(i);
            p.Transform(trsf);
            nodes[i - 1] = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
        }

        bool reversed = face.Orientation == TopAbs_Orientation.TopAbs_REVERSED;

        for (int t = 1; t <= tri.NbTriangles; t++)
        {
            var triObj = tri.Triangle(t);
            var a = nodes[triObj.Value(1) - 1];
            var b = nodes[triObj.Value(2) - 1];
            var c = nodes[triObj.Value(3) - 1];

            if (reversed)
                (b, c) = (c, b);

            var n = SafeNorm(Vector3.Cross(b - a, c - a));
            positions.Add(a); positions.Add(b); positions.Add(c);
            normals.Add(n);   normals.Add(n);   normals.Add(n);
        }
    }

    private static Vector3 SafeNorm(Vector3 v)
    {
        float len = v.Length;
        return len > 1e-6f ? v / len : Vector3.UnitZ;
    }
}