using System.Text.Json.Serialization;

namespace MassiveSlicer.Core.Kinematics;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RotationAxis { X, Y, Z }

/// <summary>
/// Per-joint angle conversion constants.
/// KRL → bone:  <c>bone_rad = KrlSign × krl_deg × π/180</c>
/// KrlOffset is a KRL↔math convention artifact used only for IK, not FK.
/// </summary>
public sealed record JointConfig
{
    public float        KrlOffset { get; init; } = 0f;
    public float        KrlSign   { get; init; } = 1f;
    public float        MinDeg    { get; init; } = -360f;
    public float        MaxDeg    { get; init; } = 360f;
    public RotationAxis Axis      { get; init; } = RotationAxis.Y;

    public float KrlToMathRad(float krlDeg) =>
        KrlSign * (krlDeg + KrlOffset) * MathF.PI / 180f;

    public float MathRadToKrl(float mathRad) =>
        KrlSign * mathRad * 180f / MathF.PI - KrlOffset;

    public float Clamp(float krlDeg) => Math.Clamp(krlDeg, MinDeg, MaxDeg);
}
