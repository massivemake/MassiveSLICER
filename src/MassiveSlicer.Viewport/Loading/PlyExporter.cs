using System.Globalization;
using System.Text;
using MassiveSlicer.Viewport.Scene;
namespace MassiveSlicer.Viewport.Loading;

/// <summary>Writes point clouds as ASCII PLY (Z-up millimetres).</summary>
public static class PlyExporter
{
    public static void Write(string path, MeshData mesh)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var points = mesh.Positions;
        var normals = mesh.Normals;
        bool hasNormals = normals.Length == points.Length;

        var sb = new StringBuilder();
        sb.AppendLine("ply");
        sb.AppendLine("format ascii 1.0");
        sb.AppendLine($"element vertex {points.Length}");
        sb.AppendLine("property float x");
        sb.AppendLine("property float y");
        sb.AppendLine("property float z");
        if (hasNormals)
        {
            sb.AppendLine("property float nx");
            sb.AppendLine("property float ny");
            sb.AppendLine("property float nz");
        }
        sb.AppendLine("end_header");

        var culture = CultureInfo.InvariantCulture;
        for (int i = 0; i < points.Length; i++)
        {
            var p = points[i];
            if (hasNormals)
            {
                var n = normals[i];
                sb.Append(p.X.ToString(culture)).Append(' ')
                  .Append(p.Y.ToString(culture)).Append(' ')
                  .Append(p.Z.ToString(culture)).Append(' ')
                  .Append(n.X.ToString(culture)).Append(' ')
                  .Append(n.Y.ToString(culture)).Append(' ')
                  .AppendLine(n.Z.ToString(culture));
            }
            else
            {
                sb.Append(p.X.ToString(culture)).Append(' ')
                  .Append(p.Y.ToString(culture)).Append(' ')
                  .AppendLine(p.Z.ToString(culture));
            }
        }

        File.WriteAllText(path, sb.ToString());
    }
}