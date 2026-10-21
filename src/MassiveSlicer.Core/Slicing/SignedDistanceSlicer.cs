using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing;

/// <summary>
/// Signed-distance slicer, from the shell idea in Chermain et al., Atomizer
/// (Computer Graphics Forum 2025). Bead centers lie on iso-surfaces of the mesh
/// signed distance, inset by half a bead and then one bead at a time. Each shell
/// is covered by surface-distance contours spaced one layer height apart, the same
/// contour walk <see cref="GeodesicSlicer"/> uses. The nozzle axis is the outward
/// gradient, mirrored into the upper hemisphere so the tool never aims down.
/// </summary>
public static class SignedDistanceSlicer
{
    public const int MaxShells = 48;
    public const int MaxAxisCells = 64;

    public static Toolpath Slice(IReadOnlyList<Vector3[]> meshes, SliceSettings settings,
                                 Action<float>? progress = null)
    {
        var result = new Toolpath();
        var sdf = new MeshSignedDistance(meshes);
        if (!sdf.HasTriangles) return result;

        float h = MathF.Max(settings.LayerHeight, 0.2f);
        float tau = MathF.Max(settings.BeadWidth, 0.2f);
        var (origin, spacing, nx, ny, nz, coarsened) = PlanGrid(sdf.Bounds.Min, sdf.Bounds.Max, h);
        if (coarsened)
            result.Warnings.Add(
                "Signed Distance grid was coarsened to keep the field under 64 cells on the longest side.");

        var field = new float[nx * ny * nz];
        Parallel.For(0, nz, k =>
        {
            for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                var p = origin + new Vector3(i * spacing.X, j * spacing.Y, k * spacing.Z);
                field[i + nx * (j + ny * k)] = sdf.Sample(p).Distance;
            }
        });
        progress?.Invoke(0.3f);

        var shells = new List<Toolpath>();
        for (int k = 0; k < MaxShells; k++)
        {
            float iso = -(k + 0.5f) * tau;
            var soup = IsoSurface.Extract(field, nx, ny, nz, origin, spacing, iso);
            if (soup.Length < 9) break;

            float shellStart = 0.3f + 0.7f * k / MaxShells;
            float shellSpan = 0.7f / MaxShells;
            var shell = GeodesicSlicer.Slice(
                [soup],
                settings,
                progress is null ? null : f => progress(shellStart + f * shellSpan));
            if (shell.Layers.Count == 0) break;

            StampNormals(shell, sdf);
            shells.Add(shell);
            progress?.Invoke(0.3f + 0.7f * (k + 1) / MaxShells);
        }

        if (shells.Count == 0)
        {
            result.Warnings.Add(
                "Signed Distance found no interior shell. The mesh may be open, or thinner than one bead.");
            return result;
        }

        if (shells.Count == MaxShells)
        {
            var center = (sdf.Bounds.Min + sdf.Bounds.Max) * 0.5f;
            float deepest = -(MaxShells - 0.5f) * tau;
            if (sdf.Sample(center).Distance < deepest - 1f)
                result.Warnings.Add(
                    "Signed Distance stopped at 48 shells; the core was not filled.");
        }

        Interleave(shells, settings.LayerHeight, result);
        progress?.Invoke(1f);
        return result;
    }

    /// <summary>Grid that covers <paramref name="min"/>–<paramref name="max"/> with one cell of padding.
    /// Pitch targets half a layer height, and no axis exceeds <see cref="MaxAxisCells"/>.</summary>
    internal static (Vector3 Origin, Vector3 Spacing, int Nx, int Ny, int Nz, bool Coarsened) PlanGrid(
        Vector3 min, Vector3 max, float layerHeight)
    {
        float target = MathF.Max(MathF.Max(layerHeight, 0.2f) * 0.5f, 0.25f);
        var size = Vector3.Max(max - min, new Vector3(target));

        static int Cells(float extent, float pitch) =>
            Math.Clamp((int)MathF.Ceiling(extent / pitch), 1, MaxAxisCells);

        int cx = Cells(size.X, target);
        int cy = Cells(size.Y, target);
        int cz = Cells(size.Z, target);
        bool coarsened = size.X / target > MaxAxisCells + 0.01f
                      || size.Y / target > MaxAxisCells + 0.01f
                      || size.Z / target > MaxAxisCells + 0.01f;
        var spacing = new Vector3(size.X / cx, size.Y / cy, size.Z / cz);
        var origin = min - spacing;
        return (origin, spacing, cx + 2, cy + 2, cz + 2, coarsened);
    }

    /// <summary>Mirrors a downward gradient into the upper hemisphere. A straight-down
    /// normal becomes +Z. A sideways normal stays sideways.</summary>
    internal static Vector3 ToolNormal(Vector3 outward)
    {
        if (outward.LengthSquared() < 1e-8f) return Vector3.UnitZ;
        var n = Vector3.Normalize(outward);
        if (n.Z < 0f) n.Z = -n.Z;
        float len = n.Length();
        return len < 1e-6f ? Vector3.UnitZ : n / len;
    }

    static void StampNormals(Toolpath shell, MeshSignedDistance sdf)
    {
        foreach (var layer in shell.Layers)
        {
            for (int i = 0; i < layer.Moves.Count; i++)
            {
                var move = layer.Moves[i];
                var mid = (move.From + move.To) * 0.5f;
                var n = ToolNormal(sdf.Sample(mid).Outward);
                layer.Moves[i] = move with
                {
                    Normal = n,
                    IsWall = move.Kind == MoveKind.Extrude,
                };
            }
        }
    }

    /// <summary>Prints shell rung 0 (every shell) before rung 1, so a bead is down before
    /// the contour above it. Within a rung the outer shell runs first.</summary>
    static void Interleave(List<Toolpath> shells, float layerHeight, Toolpath result)
    {
        int rungs = 0;
        foreach (var shell in shells)
            if (shell.Layers.Count > rungs) rungs = shell.Layers.Count;

        int index = 0;
        for (int rung = 0; rung < rungs; rung++)
        {
            var batch = new List<ToolpathMove>();
            Vector3? last = null;
            for (int s = 0; s < shells.Count; s++)
            {
                if (rung >= shells[s].Layers.Count) continue;
                foreach (var move in shells[s].Layers[rung].Moves)
                {
                    if (last is { } prev && (move.From - prev).LengthSquared() > 0.25f)
                        batch.Add(new ToolpathMove(prev, move.From, MoveKind.Travel) { Normal = move.Normal });
                    batch.Add(move);
                    last = move.To;
                }
            }
            if (batch.Count == 0) continue;

            float z = 0f;
            foreach (var move in batch) z += (move.From.Z + move.To.Z) * 0.5f;
            var layer = new ToolpathLayer(index, z / batch.Count) { Height = layerHeight };
            layer.Moves.AddRange(batch);
            result.Layers.Add(layer);
            index++;
        }
    }
}
