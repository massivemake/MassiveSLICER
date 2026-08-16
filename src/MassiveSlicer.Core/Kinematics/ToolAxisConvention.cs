using System.Numerics;

namespace MassiveSlicer.Core.Kinematics;

/// <summary>
/// Which tool axis points forward (away from the flange). Extra rotation after
/// taught TCP ABC — display / triad only. Does not change TOOL_DATA.
/// </summary>
public enum ToolAxisConvention
{
    Undefined = 0,
    ZMinus = 1,
    ZPlus = 2,
    XMinus = 3,
    XPlus = 4,
}

public static class ToolAxisConventionMath
{
    /// <summary>
    /// Right-handed remap of the taught tool frame. Undefined and Z+ are identity.
    /// Z- flips Z (and Y). X+ / X- swing taught +Z onto ±X.
    /// </summary>
    public static Matrix4x4 ExtraRotation(ToolAxisConvention kind)
    {
        const float d90 = MathF.PI * 0.5f;
        const float d180 = MathF.PI;
        return kind switch
        {
            ToolAxisConvention.ZMinus => Matrix4x4.CreateRotationX(d180),
            ToolAxisConvention.XPlus  => Matrix4x4.CreateRotationY(-d90),
            ToolAxisConvention.XMinus => Matrix4x4.CreateRotationY(d90),
            _ => Matrix4x4.Identity,
        };
    }

    /// <summary>
    /// Display-only flange triad: X_shown = Y_kuka, Y_shown = -X_kuka, Z_shown = Z_kuka
    /// (+90 deg about flange Z). Does not move the mesh or TOOL_DATA / TCP.
    /// </summary>
    public static Matrix4x4 FlangeDisplayRotation =>
        Matrix4x4.CreateRotationZ(MathF.PI * 0.5f);
}
