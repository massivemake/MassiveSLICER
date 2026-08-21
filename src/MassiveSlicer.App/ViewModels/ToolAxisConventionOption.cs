using MassiveSlicer.Core.Kinematics;

namespace MassiveSlicer.ViewModels;

/// <summary>One row in the ROBOT "tool convention" dropdown.</summary>
public sealed record ToolAxisConventionOption(ToolAxisConvention Kind, string Label)
{
    public override string ToString() => Label;

    public static IReadOnlyList<ToolAxisConventionOption> All { get; } =
    [
        new(ToolAxisConvention.Undefined, "Undefined"),
        new(ToolAxisConvention.ZMinus,    "Z- (backward)"),
        new(ToolAxisConvention.ZPlus,     "Z+ (forward)"),
        new(ToolAxisConvention.XMinus,    "X- (backward)"),
        new(ToolAxisConvention.XPlus,     "X+ (forward)"),
    ];

    /// <summary>Startup + null fallback. Shop default is Z- (backward), not Undefined.</summary>
    public static ToolAxisConventionOption Default =>
        All.First(o => o.Kind == ToolAxisConvention.ZMinus);
}
