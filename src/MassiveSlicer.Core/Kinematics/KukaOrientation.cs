using System.Numerics;

namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// KUKA TCP orientation (A/B/C ZYX Euler, degrees) from a surface/approach normal.
/// Shared by KRL export and MassiveDRIVE job packages so both match the viewport.
/// </summary>
public static class KukaOrientation
{
    const float D2R = MathF.PI / 180f;
    const float R2D = 180f / MathF.PI;

    /// <summary>
    /// Computes KUKA A/B/C for a tool approaching along <paramref name="surfaceNormal"/>
    /// (nozzle into the surface = −normal).
    ///
    /// Matches <c>KrlExporter</c> historical behaviour:
    ///   1. Base perpendicular frame from Rodrigues (0,0,−1) → −normal
    ///   2. Local toolhead ZYX offset in that frame
    ///   3. Optional spin about the approach axis (<paramref name="tcpYawDeg"/>)
    ///   4. Extract ZYX Euler (gimbal-lock safe near B = ±90°)
    /// </summary>
    public static (float A, float B, float C) AbcFromNormal(
        Vector3 surfaceNormal,
        float toolheadOffsetA = 0f,
        float toolheadOffsetB = 0f,
        float toolheadOffsetC = 0f,
        float tcpYawDeg = 0f)
    {
        var normal = Vector3.Normalize(surfaceNormal);

        // Step 1: base perpendicular frame via Rodrigues (0,0,−1) → xBase = −normal
        var xBase = -normal;
        var xDef = new Vector3(0f, 0f, -1f);
        float cosT = Math.Clamp(Vector3.Dot(xDef, xBase), -1f, 1f);
        Vector3 yBase, zBase;
        if (MathF.Abs(cosT - 1f) < 1e-6f)
        {
            yBase = new Vector3(0f, 1f, 0f);
            zBase = new Vector3(1f, 0f, 0f);
        }
        else if (MathF.Abs(cosT + 1f) < 1e-6f)
        {
            yBase = new Vector3(0f, 1f, 0f);
            zBase = new Vector3(-1f, 0f, 0f);
        }
        else
        {
            var axis = Vector3.Normalize(Vector3.Cross(xDef, xBase));
            float sinT = MathF.Sqrt(1f - cosT * cosT);
            yBase = Rodrigues(new Vector3(0f, 1f, 0f), axis, sinT, cosT);
            zBase = Rodrigues(new Vector3(1f, 0f, 0f), axis, sinT, cosT);
        }

        // Step 2: local KUKA ZYX offset in the base frame
        // R_final = R_base · Rz(A)·Ry(B)·Rx(C)
        float ca = MathF.Cos(toolheadOffsetA * D2R), sa = MathF.Sin(toolheadOffsetA * D2R);
        float cb = MathF.Cos(toolheadOffsetB * D2R), sb = MathF.Sin(toolheadOffsetB * D2R);
        float cc = MathF.Cos(toolheadOffsetC * D2R), sc = MathF.Sin(toolheadOffsetC * D2R);

        var xF = xBase * (ca * cb) + yBase * (sa * cb) + zBase * (-sb);
        var yF = xBase * (ca * sb * sc - sa * cc) + yBase * (sa * sb * sc + ca * cc) + zBase * (cb * sc);
        var zF = xBase * (ca * sb * cc + sa * sc) + yBase * (sa * sb * cc - ca * sc) + zBase * (cb * cc);

        // Step 2b: optional spin about the nozzle (approach) axis
        if (MathF.Abs(tcpYawDeg) > 1e-4f)
        {
            float cy = MathF.Cos(tcpYawDeg * D2R), sy = MathF.Sin(tcpYawDeg * D2R);
            var y2 = yF * cy + zF * sy;
            var z2 = zF * cy - yF * sy;
            yF = y2;
            zF = z2;
        }

        // Step 3: extract ZYX Euler
        float bRad = MathF.Atan2(-xF.Z, MathF.Sqrt(xF.X * xF.X + xF.Y * xF.Y));
        float aRad, cRad;
        // Near B = ±90° only (A − C) is physical — recover A from y-axis
        if (MathF.Abs(MathF.Abs(bRad) - MathF.PI / 2f) < 0.05f)
        {
            aRad = MathF.Atan2(-yF.X, yF.Y);
            cRad = 0f;
        }
        else
        {
            aRad = MathF.Atan2(xF.Y, xF.X);
            cRad = MathF.Atan2(yF.Z, zF.Z);
        }

        return (aRad * R2D, bRad * R2D, cRad * R2D);
    }

    /// <summary>
    /// Spindle / T12: KUKA tool <b>Z</b> (the cutter) points into the work = −surface normal.
    /// Print/extruder uses <see cref="AbcFromNormal"/> (approach on tool X). Using that
    /// for mill left T12's TCP pointing sideways along the path.
    /// </summary>
    public static (float A, float B, float C) AbcFromMillNormal(
        Vector3 surfaceNormal,
        float tcpYawDeg = 0f,
        float toolheadOffsetA = 0f,
        float toolheadOffsetB = 0f,
        float toolheadOffsetC = 0f)
    {
        var (x, y, z) = MillFrame(surfaceNormal, tcpYawDeg, toolheadOffsetA, toolheadOffsetB, toolheadOffsetC);
        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return KukaIkSolver.MatrixToAbc(m);
    }

    /// <summary>
    /// Mill tool axes after the toolhead offset. Z is the cutter, pointing into the work.
    /// The stick-out cylinder runs the other way, from the tip back toward the spindle.
    /// </summary>
    public static (Vector3 X, Vector3 Y, Vector3 Z) MillFrame(
        Vector3 surfaceNormal,
        float tcpYawDeg = 0f,
        float toolheadOffsetA = 0f,
        float toolheadOffsetB = 0f,
        float toolheadOffsetC = 0f)
    {
        var (x, y, z) = MillToolAxes(surfaceNormal, tcpYawDeg);
        if (MathF.Abs(toolheadOffsetA) + MathF.Abs(toolheadOffsetB) + MathF.Abs(toolheadOffsetC) > 1e-4f)
        {
            float ca = MathF.Cos(toolheadOffsetA * D2R), sa = MathF.Sin(toolheadOffsetA * D2R);
            float cb = MathF.Cos(toolheadOffsetB * D2R), sb = MathF.Sin(toolheadOffsetB * D2R);
            float cc = MathF.Cos(toolheadOffsetC * D2R), sc = MathF.Sin(toolheadOffsetC * D2R);
            var xF = x * (ca * cb) + y * (sa * cb) + z * (-sb);
            var yF = x * (ca * sb * sc - sa * cc) + y * (sa * sb * sc + ca * cc) + z * (cb * sc);
            var zF = x * (ca * sb * cc + sa * sc) + y * (sa * sb * cc - ca * sc) + z * (cb * cc);
            x = xF;
            y = yF;
            z = zF;
        }
        return (x, y, z);
    }

    /// <summary>
    /// Mill tool axes before the toolhead offset. Z is the cutter (−surface normal).
    /// Upright (cutter world −Z) is X = world +Y, Y = world +X. Any other cutter
    /// direction is that same frame swung onto Z, so a lean cannot reverse Y.
    /// Yaw then spins X and Y around Z: <c>X' = X cos + Y sin</c>, <c>Y' = Y cos − X sin</c>.
    /// </summary>
    public static (Vector3 X, Vector3 Y, Vector3 Z) MillToolAxes(Vector3 surfaceNormal, float tcpYawDeg = 0f)
    {
        var n = surfaceNormal.LengthSquared() > 1e-12f ? Vector3.Normalize(surfaceNormal) : Vector3.UnitZ;
        var z = -n;
        // Upright frame. Rebuilding X/Y from a world hint flipped Y once the cutter
        // left the vertical cone (|Z| = 0.9) and again on the far side of the part.
        var z0 = -Vector3.UnitZ;
        var x0 = Vector3.UnitY;
        var y0 = Vector3.UnitX;
        float cosT = Math.Clamp(Vector3.Dot(z0, z), -1f, 1f);
        Vector3 x, y;
        if (cosT > 1f - 1e-6f)
        {
            x = x0;
            y = y0;
        }
        else if (cosT < -1f + 1e-6f)
        {
            // Cutter straight up: 180° about X, the limit of a lean toward +Y.
            // Y stays on world +X.
            x = -x0;
            y = y0;
        }
        else
        {
            var axis = Vector3.Normalize(Vector3.Cross(z0, z));
            float sinT = MathF.Sqrt(1f - cosT * cosT);
            x = Rodrigues(x0, axis, sinT, cosT);
            y = Rodrigues(y0, axis, sinT, cosT);
        }

        x = Vector3.Normalize(x);
        y = Vector3.Cross(z, x);
        if (y.LengthSquared() < 1e-10f)
            y = y0;
        y = Vector3.Normalize(y);
        x = Vector3.Cross(y, z);

        if (MathF.Abs(tcpYawDeg) > 1e-4f)
        {
            float cy = MathF.Cos(tcpYawDeg * D2R), sy = MathF.Sin(tcpYawDeg * D2R);
            var x2 = x * cy + y * sy;
            var y2 = y * cy - x * sy;
            x = x2;
            y = y2;
        }

        return (x, y, z);
    }

    /// <summary>
    /// Print tool axes with no toolhead offset. X is the nozzle (−surface normal).
    /// Yaw spins Y and Z around X.
    /// </summary>
    public static (Vector3 X, Vector3 Y, Vector3 Z) PrintToolAxes(Vector3 surfaceNormal, float tcpYawDeg = 0f)
    {
        var normal = surfaceNormal.LengthSquared() > 1e-12f ? Vector3.Normalize(surfaceNormal) : Vector3.UnitZ;
        var x = -normal;
        var xDef = new Vector3(0f, 0f, -1f);
        float cosT = Math.Clamp(Vector3.Dot(xDef, x), -1f, 1f);
        Vector3 y, z;
        if (MathF.Abs(cosT - 1f) < 1e-6f)
        {
            y = new Vector3(0f, 1f, 0f);
            z = new Vector3(1f, 0f, 0f);
        }
        else if (MathF.Abs(cosT + 1f) < 1e-6f)
        {
            y = new Vector3(0f, 1f, 0f);
            z = new Vector3(-1f, 0f, 0f);
        }
        else
        {
            var axis = Vector3.Normalize(Vector3.Cross(xDef, x));
            float sinT = MathF.Sqrt(1f - cosT * cosT);
            y = Rodrigues(new Vector3(0f, 1f, 0f), axis, sinT, cosT);
            z = Rodrigues(new Vector3(1f, 0f, 0f), axis, sinT, cosT);
        }

        if (MathF.Abs(tcpYawDeg) > 1e-4f)
        {
            float cy = MathF.Cos(tcpYawDeg * D2R), sy = MathF.Sin(tcpYawDeg * D2R);
            var y2 = y * cy + z * sy;
            var z2 = z * cy - y * sy;
            y = y2;
            z = z2;
        }

        return (x, y, z);
    }

    /// <summary>
    /// KUKA ABC whose tool Z lies along <paramref name="toolZInFlange"/> (KUKA flange axes).
    /// The LFAM 3 spindle cutter is flange −X, so this is B=−90°, not T2's A=15° spin
    /// (that leaves tool Z on the flange, 45° off world-down at the milling home).
    /// </summary>
    public static (float A, float B, float C) AbcAimingToolZ(Vector3 toolZInFlange)
    {
        var z = toolZInFlange.LengthSquared() > 1e-12f
            ? Vector3.Normalize(toolZInFlange)
            : -Vector3.UnitZ;
        var hint = MathF.Abs(z.Z) > 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        var x = Vector3.Cross(hint, z);
        if (x.LengthSquared() < 1e-12f)
            x = Vector3.Cross(Vector3.UnitY, z);
        x = Vector3.Normalize(x);
        var y = Vector3.Cross(z, x);
        // Same storage as AbcToMatrix: row 2 is tool Z in the flange frame.
        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0,
            y.X, y.Y, y.Z, 0,
            z.X, z.Y, z.Z, 0,
            0, 0, 0, 1);
        return KukaIkSolver.MatrixToAbc(m);
    }

    static Vector3 Rodrigues(Vector3 v, Vector3 axis, float sinTheta, float cosTheta)
        => v * cosTheta + Vector3.Cross(axis, v) * sinTheta + axis * Vector3.Dot(axis, v) * (1f - cosTheta);
}
