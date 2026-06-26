using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.App;

/// <summary>Persists dev-mode scene edits back into the active cell JSON.</summary>
internal static class CellDevTransformSaver
{
    private static readonly Matrix4 YupToZupFrame = Matrix4.CreateRotationX(MathF.PI / 2f);
    private static readonly Matrix4 DockToolFixup  = Matrix4.CreateRotationY(MathF.PI / 2f);

    public static bool TrySave(string cellPath, CellConfig cell, SceneNode node, string kind, string? id,
        out string? error)
    {
        error = null;
        return kind switch
        {
            "stand" when id is not null => SaveStand(cellPath, node, id, out error),
            "rotary"                    => SaveRotary(cellPath, cell, node, out error),
            "dock" when id is not null  => SaveDock(cellPath, cell, node, id, out error),
            "bed"                       => SaveBed(cellPath, node, out error),
            _                           => false,
        };
    }

    private static bool SaveStand(string cellPath, SceneNode node, string standId, out string? error)
    {
        var pos = node.LocalTransform.Row3.Xyz;
        var rot = ExtractRotation(node.LocalTransform);
        var seedPos = new[] { pos.X / 1000f, pos.Z / 1000f, -pos.Y / 1000f };
        var seedRot = StandSeedRotation(rot);
        return CellLoader.SaveStandTransform(cellPath, standId, seedPos, seedRot, out error);
    }

    private static bool SaveRotary(string cellPath, CellConfig cell, SceneNode node, out string? error)
    {
        var pos = node.LocalTransform.Row3.Xyz;
        var rot = ExtractRotation(node.LocalTransform);
        var rp  = cell.Robot.WorldPosition;
        var (a, b, c) = KukaIkSolver.MatrixToAbc(ToMatrix4x4(rot));
        return CellLoader.SaveRotaryBedTransform(
            cellPath,
            [pos.X - rp.X, pos.Y - rp.Y, pos.Z - rp.Z],
            [a, b, c],
            out error);
    }

    private static bool SaveDock(string cellPath, CellConfig cell, SceneNode node, string toolName,
        out string? error)
    {
        Matrix4.Invert(DockToolFixup, out var fixInv);
        var pose = node.LocalTransform * fixInv;
        var pos  = pose.Row3.Xyz;
        var rot  = ExtractRotation(pose);
        var rp   = cell.Robot.WorldPosition;
        var (a, b, c) = KukaIkSolver.MatrixToAbc(ToMatrix4x4(rot));
        return CellLoader.SaveToolDock(cellPath, toolName, new ToolDockCellConfig
        {
            X = pos.X - rp.X,
            Y = pos.Y - rp.Y,
            Z = pos.Z - rp.Z,
            A = a, B = b, C = c,
        }, out error);
    }

    private static bool SaveBed(string cellPath, SceneNode node, out string? error)
    {
        var pos = node.WorldTransform.Row3.Xyz;
        return CellLoader.SaveBedDevTransform(cellPath, pos.X, pos.Y, pos.Z, out error);
    }

    private static float[] StandSeedRotation(Matrix4 worldRot)
    {
        Matrix4.Invert(YupToZupFrame, out var frameInv);
        var rThree = YupToZupFrame * worldRot * frameInv;
        float sy = MathF.Sqrt(rThree.M11 * rThree.M11 + rThree.M12 * rThree.M12);
        if (sy < 1e-6f)
            return [MathF.Atan2(-rThree.M32, rThree.M22), MathF.Atan2(-rThree.M13, sy), 0f];
        return [
            MathF.Atan2(rThree.M23, rThree.M33),
            MathF.Atan2(-rThree.M13, sy),
            MathF.Atan2(rThree.M12, rThree.M11),
        ];
    }

    private static Matrix4 ExtractRotation(Matrix4 m)
    {
        var r0 = m.Row0.Xyz; var r1 = m.Row1.Xyz; var r2 = m.Row2.Xyz;
        if (r0.LengthSquared > 1e-12f) r0 = Vector3.Normalize(r0);
        if (r1.LengthSquared > 1e-12f) r1 = Vector3.Normalize(r1);
        if (r2.LengthSquared > 1e-12f) r2 = Vector3.Normalize(r2);
        return new Matrix4(
            new Vector4(r0, 0f), new Vector4(r1, 0f), new Vector4(r2, 0f), new Vector4(0f, 0f, 0f, 1f));
    }

    private static System.Numerics.Matrix4x4 ToMatrix4x4(Matrix4 m) => new(
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44);
}