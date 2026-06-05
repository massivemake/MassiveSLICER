using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Loads 3MF files (ZIP + XML) into a <see cref="SceneNode"/> hierarchy.
/// Handles unit conversion to millimetres and computes flat face normals
/// (3MF does not store normals in the mesh element).
/// </summary>
public static class ThreeMfLoader
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";

    public static SceneNode Load(string path, string? name = null)
    {
        var rootName = name ?? Path.GetFileNameWithoutExtension(path);

        using var zip = ZipFile.OpenRead(path);

        var modelPath  = FindModelEntry(zip);
        var modelEntry = zip.GetEntry(modelPath)
            ?? throw new InvalidDataException($"3MF model entry '{modelPath}' not found in archive.");

        XDocument doc;
        using (var stream = modelEntry.Open())
            doc = XDocument.Load(stream);

        var modelEl = doc.Root!;
        float scale = UnitToMm(modelEl.Attribute("unit")?.Value ?? "millimeter");

        var resources = modelEl.Element(Ns + "resources");
        if (resources is null)
            return new SceneNode { Name = rootName };

        var objectEls = resources.Elements(Ns + "object")
            .Where(o => (o.Attribute("type")?.Value ?? "model") == "model")
            .ToList();

        if (objectEls.Count == 0)
            return new SceneNode { Name = rootName };

        SceneNode BuildObject(XElement objEl)
        {
            var objName = objEl.Attribute("name")?.Value ?? rootName;
            var meshEl  = objEl.Element(Ns + "mesh")
                ?? throw new InvalidDataException($"3MF object '{objName}' has no <mesh> element.");

            var vertEls = meshEl.Element(Ns + "vertices")?.Elements(Ns + "vertex").ToArray() ?? [];
            var triEls  = meshEl.Element(Ns + "triangles")?.Elements(Ns + "triangle").ToArray() ?? [];

            var verts = new Vector3[vertEls.Length];
            for (int i = 0; i < vertEls.Length; i++)
                verts[i] = new Vector3(
                    F(vertEls[i].Attribute("x")!.Value) * scale,
                    F(vertEls[i].Attribute("y")!.Value) * scale,
                    F(vertEls[i].Attribute("z")!.Value) * scale);

            var outPos  = new Vector3[triEls.Length * 3];
            var outNorm = new Vector3[triEls.Length * 3];

            for (int t = 0; t < triEls.Length; t++)
            {
                var p0 = verts[int.Parse(triEls[t].Attribute("v1")!.Value)];
                var p1 = verts[int.Parse(triEls[t].Attribute("v2")!.Value)];
                var p2 = verts[int.Parse(triEls[t].Attribute("v3")!.Value)];

                var n = SafeNorm(Vector3.Cross(p1 - p0, p2 - p0));
                outPos [t * 3]     = p0; outNorm[t * 3]     = n;
                outPos [t * 3 + 1] = p1; outNorm[t * 3 + 1] = n;
                outPos [t * 3 + 2] = p2; outNorm[t * 3 + 2] = n;
            }

            return new SceneNode { Name = objName, PendingMesh = new MeshData(outPos, outNorm, null, objName) };
        }

        if (objectEls.Count == 1)
            return BuildObject(objectEls[0]);

        var root = new SceneNode { Name = rootName };
        foreach (var objEl in objectEls)
            root.AddChild(BuildObject(objEl));
        return root;
    }

    // -- Helpers ------------------------------------------------------------------

    private static string FindModelEntry(ZipArchive zip)
    {
        // Check _rels/.rels for the 3D model start part
        var relsEntry = zip.GetEntry("_rels/.rels");
        if (relsEntry is not null)
        {
            using var stream = relsEntry.Open();
            var doc    = XDocument.Load(stream);
            var target = doc.Root?
                .Elements()
                .FirstOrDefault(e => (e.Attribute("Type")?.Value ?? "").Contains("3dmodel"))
                ?.Attribute("Target")?.Value;

            if (target is not null)
                return target.TrimStart('/');
        }

        // Fallback: find any .model entry
        var found = zip.Entries.FirstOrDefault(
            e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
        return found?.FullName ?? "3D/3dmodel.model";
    }

    private static float UnitToMm(string unit) => unit.ToLowerInvariant() switch
    {
        "millimeter" or "mm" => 1f,
        "centimeter" or "cm" => 10f,
        "meter"      or "m"  => 1000f,
        "inch"               => 25.4f,
        "foot"               => 304.8f,
        "micron"             => 0.001f,
        _                    => 1f,
    };

    private static Vector3 SafeNorm(Vector3 v)
    {
        float len = v.Length;
        return len > 1e-6f ? v / len : Vector3.UnitZ;
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
}
